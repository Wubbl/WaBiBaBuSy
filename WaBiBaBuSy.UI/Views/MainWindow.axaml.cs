using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using WaBiBaBuSy.UI.ViewModels;
using WaBiBaBuSy.WallpaperEngine.Services;
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.UI.Views;

public partial class MainWindow : Window
{
    private Canvas? _topologyCanvas;
    private Point _dragStartPoint;
    private bool _isDragging = false;
    private Rectangle? _selectionRectangle;

    public MainWindow()
    {
        InitializeComponent();

        // Pre-initialize LibVLC in background to eliminate ~9s delay on first wallpaper
        // This runs asynchronously and won't block the UI
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().AddDebug());
        var logger = loggerFactory.CreateLogger<MainWindow>();
        _ = LibVLCPreloader.PreloadAsync(logger);

        // Set storage provider on the view model when the window is opened
        Opened += OnWindowOpened;
        Closed += OnWindowClosed;

        // Subscribe to DataContext changes
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            // Subscribe to collection changes
            viewModel.Clients.CollectionChanged += OnClientsCollectionChanged;
            // Render initial clients
            RenderClientNodes();

            // Set up canvas for selection
            _topologyCanvas = this.FindControl<Canvas>("TopologyCanvas");
            if (_topologyCanvas != null)
            {
                _topologyCanvas.PointerPressed += OnCanvasPointerPressed;
                _topologyCanvas.PointerMoved += OnCanvasPointerMoved;
                _topologyCanvas.PointerReleased += OnCanvasPointerReleased;
            }
        }
    }

    private void OnClientsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Debug.WriteLine($"[MainWindow] Clients collection changed: {e.Action}");
        RenderClientNodes();
    }

    private void RenderClientNodes()
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        var canvas = this.FindControl<Canvas>("TopologyCanvas");
        if (canvas == null)
        {
            Debug.WriteLine("[MainWindow] TopologyCanvas not found!");
            return;
        }

        Debug.WriteLine($"[MainWindow] Rendering {viewModel.Clients.Count} client nodes");

        // Clear existing nodes (keep debug text at index 0)
        while (canvas.Children.Count > 1)
        {
            canvas.Children.RemoveAt(1);
        }

        // Add client nodes
        foreach (var client in viewModel.Clients)
        {
            var border = CreateClientNodeBorder(client, viewModel);
            Canvas.SetLeft(border, client.X);
            Canvas.SetTop(border, client.Y);
            canvas.Children.Add(border);

            Debug.WriteLine($"[MainWindow] Added node for {client.DisplayName} at ({client.X}, {client.Y})");
        }
    }

    private Border CreateClientNodeBorder(ClientNodeViewModel client, MainWindowViewModel viewModel)
    {
        var border = new Border
        {
            Width = 150,
            Height = 100,
            Background = new SolidColorBrush(Color.Parse("#3E3E42")),
            BorderBrush = new SolidColorBrush(Color.Parse("#666666")),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Cursor = new Cursor(StandardCursorType.Hand),
            DataContext = client
        };

        // Update styling based on selection state
        void UpdateSelectionState()
        {
            if (client.IsSelected)
            {
                // Selected state: bright blue border and lighter background
                border.BorderBrush = new SolidColorBrush(Color.Parse("#0078D4"));
                border.BorderThickness = new Thickness(3);
                border.Background = new SolidColorBrush(Color.Parse("#4E5A6E"));
            }
            else
            {
                // Normal state
                border.BorderBrush = new SolidColorBrush(Color.Parse("#666666"));
                border.BorderThickness = new Thickness(2);
                border.Background = new SolidColorBrush(Color.Parse("#3E3E42"));
            }
        }

        // Subscribe to property changes on the client
        client.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(client.IsSelected))
            {
                UpdateSelectionState();
            }
        };

        // Set initial state
        UpdateSelectionState();

        var stackPanel = new StackPanel { Spacing = 5 };

        // Display Name
        stackPanel.Children.Add(new TextBlock
        {
            Text = client.DisplayName,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // IP Address
        stackPanel.Children.Add(new TextBlock
        {
            Text = client.IpAddress,
            Foreground = new SolidColorBrush(Color.Parse("#AAAAAA")),
            FontSize = 11
        });

        // Status
        stackPanel.Children.Add(new TextBlock
        {
            Text = client.Status,
            Foreground = new SolidColorBrush(Color.Parse("#00FF00")),
            FontSize = 10
        });

        // Current Wallpaper
        if (!string.IsNullOrEmpty(client.CurrentWallpaper))
        {
            stackPanel.Children.Add(new TextBlock
            {
                Text = $"WP: {client.CurrentWallpaper}",
                Foreground = new SolidColorBrush(Color.Parse("#888888")),
                FontSize = 9,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        // Physical Distance
        stackPanel.Children.Add(new TextBlock
        {
            Text = $"Distance: {client.PhysicalDistanceCm} cm",
            Foreground = new SolidColorBrush(Color.Parse("#FFA500")),
            FontSize = 9
        });

        border.Child = stackPanel;

        // Add click handler with Ctrl+Click support for multi-select
        border.PointerPressed += (s, e) =>
        {
            Debug.WriteLine($"[MainWindow] Client node clicked: {client.DisplayName}");
            var properties = e.GetCurrentPoint(border).Properties;

            // Check if Ctrl key is pressed
            var ctrlPressed = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;

            if (ctrlPressed)
            {
                // Toggle selection without clearing other selections
                Debug.WriteLine($"[MainWindow] Ctrl+Click: toggling selection for {client.DisplayName}");
                client.IsSelected = !client.IsSelected;
            }
            else
            {
                // Normal click: select only this client (deselect others)
                viewModel.SelectClientCommand.Execute(client);
            }
        };

        // Add hover effect (but respect selection state)
        border.PointerEntered += (s, e) =>
        {
            if (!client.IsSelected)
            {
                border.Background = new SolidColorBrush(Color.Parse("#4E4E52"));
            }
        };

        border.PointerExited += (s, e) =>
        {
            if (!client.IsSelected)
            {
                border.Background = new SolidColorBrush(Color.Parse("#3E3E42"));
            }
        };

        return border;
    }

    /// <summary>
    /// Handle canvas mouse down - start rectangle selection drag
    /// </summary>
    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_topologyCanvas == null)
            return;

        var point = e.GetCurrentPoint(_topologyCanvas);

        // Only start drag selection on empty canvas area (not on client nodes)
        if (point.Properties.IsLeftButtonPressed && e.Source == _topologyCanvas)
        {
            _dragStartPoint = point.Position;
            _isDragging = true;

            // Check if Ctrl is pressed
            var ctrlPressed = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;
            if (!ctrlPressed)
            {
                // Deselect all clients when starting new selection without Ctrl
                if (DataContext is MainWindowViewModel viewModel)
                {
                    foreach (var client in viewModel.Clients)
                    {
                        client.IsSelected = false;
                    }
                }
            }

            // Create selection rectangle
            _selectionRectangle = new Rectangle
            {
                Fill = new SolidColorBrush(Color.Parse("#0078D433")),  // Semi-transparent blue
                Stroke = new SolidColorBrush(Color.Parse("#0078D4")),
                StrokeThickness = 2
            };

            Canvas.SetLeft(_selectionRectangle, _dragStartPoint.X);
            Canvas.SetTop(_selectionRectangle, _dragStartPoint.Y);
            _topologyCanvas.Children.Add(_selectionRectangle);

            Debug.WriteLine($"[MainWindow] Started rectangle selection at ({_dragStartPoint.X}, {_dragStartPoint.Y})");
        }
    }

    /// <summary>
    /// Handle canvas mouse move - update rectangle selection
    /// </summary>
    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || _topologyCanvas == null || _selectionRectangle == null)
            return;

        var point = e.GetCurrentPoint(_topologyCanvas);
        var currentPoint = point.Position;

        // Calculate rectangle bounds
        var left = Math.Min(_dragStartPoint.X, currentPoint.X);
        var top = Math.Min(_dragStartPoint.Y, currentPoint.Y);
        var width = Math.Abs(currentPoint.X - _dragStartPoint.X);
        var height = Math.Abs(currentPoint.Y - _dragStartPoint.Y);

        Canvas.SetLeft(_selectionRectangle, left);
        Canvas.SetTop(_selectionRectangle, top);
        _selectionRectangle.Width = width;
        _selectionRectangle.Height = height;

        // Update client selections based on rectangle
        if (DataContext is MainWindowViewModel viewModel)
        {
            var selectionBounds = new Rect(left, top, width, height);

            foreach (var client in viewModel.Clients)
            {
                // Find the border for this client
                var clientBorder = _topologyCanvas.Children
                    .OfType<Border>()
                    .FirstOrDefault(b => b.DataContext == client);

                if (clientBorder != null)
                {
                    var clientX = Canvas.GetLeft(clientBorder);
                    var clientY = Canvas.GetTop(clientBorder);
                    var clientRect = new Rect(clientX, clientY, clientBorder.Width, clientBorder.Height);

                    // Check if client node intersects with selection rectangle
                    if (selectionBounds.Intersects(clientRect))
                    {
                        client.IsSelected = true;
                    }
                    else
                    {
                        // Only deselect if Ctrl is not pressed (to preserve existing selections)
                        var ctrlPressed = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;
                        if (!ctrlPressed)
                        {
                            client.IsSelected = false;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Handle canvas mouse up - finish rectangle selection
    /// </summary>
    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging || _topologyCanvas == null || _selectionRectangle == null)
            return;

        _isDragging = false;

        // Remove selection rectangle
        _topologyCanvas.Children.Remove(_selectionRectangle);
        _selectionRectangle = null;

        Debug.WriteLine("[MainWindow] Finished rectangle selection");
    }

    private void OnWindowOpened(object? sender, System.EventArgs e)
    {
        // Provide the storage provider and window reference to the view model for dialogs
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetStorageProvider(StorageProvider);
            viewModel.SetMainWindow(this);
            // Update server status to reflect current state when window is reopened
            viewModel.UpdateServerStatus();
            // Start the refresh timer when window is visible
            viewModel.StartRefreshTimer();
        }
    }

    private void OnWindowClosed(object? sender, System.EventArgs e)
    {
        // Stop the refresh timer when window is closed to save resources
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.StopRefreshTimer();
        }
    }
}
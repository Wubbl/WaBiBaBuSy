using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WaBiBaBuSy.UI.ViewModels;
using WaBiBaBuSy.WallpaperEngine.Services;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
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
            Width = 180,
            Height = 150,
            Background = new SolidColorBrush(Color.Parse("#3E3E42")),
            BorderBrush = new SolidColorBrush(Color.Parse("#666666")),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Cursor = new Cursor(StandardCursorType.Hand),
            DataContext = client
        };

        // Update styling based on selection and animation state
        void UpdateNodeAppearance()
        {
            if (client.IsCurrentAnimationTarget)
            {
                border.BorderBrush = new SolidColorBrush(Color.Parse("#FFD700"));
                border.BorderThickness = new Thickness(3);
                border.Background = new SolidColorBrush(Color.Parse("#4E4A2E"));
            }
            else if (client.IsAnimating)
            {
                border.BorderBrush = new SolidColorBrush(Color.Parse("#00AA44"));
                border.BorderThickness = new Thickness(3);
                border.Background = new SolidColorBrush(Color.Parse("#2E4A3E"));
            }
            else if (client.IsSelected)
            {
                border.BorderBrush = new SolidColorBrush(Color.Parse("#0078D4"));
                border.BorderThickness = new Thickness(3);
                border.Background = new SolidColorBrush(Color.Parse("#4E5A6E"));
            }
            else
            {
                border.BorderBrush = new SolidColorBrush(Color.Parse("#666666"));
                border.BorderThickness = new Thickness(2);
                border.Background = new SolidColorBrush(Color.Parse("#3E3E42"));
            }
        }

        client.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(client.IsSelected) or nameof(client.IsAnimating) or nameof(client.IsCurrentAnimationTarget))
            {
                UpdateNodeAppearance();
            }
        };

        UpdateNodeAppearance();

        var stackPanel = new StackPanel { Spacing = 4 };

        // Thumbnail area: 160x90 image or "No Preview" placeholder
        var thumbnailImage = new Image
        {
            Width = 160,
            Height = 90,
            Stretch = Stretch.UniformToFill,
            Source = client.ThumbnailImage,
            IsVisible = client.ThumbnailImage != null
        };

        var noPreviewText = new TextBlock
        {
            Text = "No Preview",
            Foreground = new SolidColorBrush(Color.Parse("#666666")),
            FontSize = 11,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsVisible = client.ThumbnailImage == null
        };

        var thumbnailContainer = new Border
        {
            Width = 160,
            Height = 90,
            Background = new SolidColorBrush(Color.Parse("#2D2D30")),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        // Use a Grid to overlay the image and placeholder
        var thumbnailGrid = new Grid
        {
            Width = 160,
            Height = 90
        };
        thumbnailGrid.Children.Add(noPreviewText);
        thumbnailGrid.Children.Add(thumbnailImage);
        thumbnailContainer.Child = thumbnailGrid;

        stackPanel.Children.Add(thumbnailContainer);

        // Subscribe to ThumbnailImage changes to swap visibility
        client.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(client.ThumbnailImage))
            {
                thumbnailImage.Source = client.ThumbnailImage;
                thumbnailImage.IsVisible = client.ThumbnailImage != null;
                noPreviewText.IsVisible = client.ThumbnailImage == null;
            }
        };

        // Display Name (hostname)
        stackPanel.Children.Add(new TextBlock
        {
            Text = client.DisplayName,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        });

        // Status with colored dot
        var statusColor = client.IsConnected
            ? Color.Parse("#00FF00")
            : Color.Parse("#FF4444");
        var statusPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        var statusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(statusColor),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var statusText = new TextBlock
        {
            Text = client.IsConnected ? "Connected" : "Disconnected",
            Foreground = new SolidColorBrush(Color.Parse("#AAAAAA")),
            FontSize = 10,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        statusPanel.Children.Add(statusDot);
        statusPanel.Children.Add(statusText);
        stackPanel.Children.Add(statusPanel);

        // Animation name indicator (shown when animating)
        var animNameText = new TextBlock
        {
            Text = client.ActiveAnimationName ?? string.Empty,
            Foreground = new SolidColorBrush(Color.Parse("#00CC66")),
            FontSize = 8,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            IsVisible = client.IsAnimating
        };
        stackPanel.Children.Add(animNameText);

        // Update status indicator and animation info on property changes
        client.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(client.IsConnected))
            {
                var color = client.IsConnected ? Color.Parse("#00FF00") : Color.Parse("#FF4444");
                statusDot.Fill = new SolidColorBrush(color);
                statusText.Text = client.IsConnected ? "Connected" : "Disconnected";
            }
            else if (e.PropertyName == nameof(client.IsAnimating))
            {
                animNameText.IsVisible = client.IsAnimating;
            }
            else if (e.PropertyName == nameof(client.ActiveAnimationName))
            {
                animNameText.Text = client.ActiveAnimationName ?? string.Empty;
            }
        };

        border.Child = stackPanel;

        // Click handler with Ctrl+Click support for multi-select
        border.PointerPressed += (s, e) =>
        {
            Debug.WriteLine($"[MainWindow] Client node clicked: {client.DisplayName}");

            var ctrlPressed = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;

            if (ctrlPressed)
            {
                Debug.WriteLine($"[MainWindow] Ctrl+Click: toggling selection for {client.DisplayName}");
                client.IsSelected = !client.IsSelected;
            }
            else
            {
                viewModel.SelectClientCommand.Execute(client);
            }
        };

        // Hover effect
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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using WaBiBaBuSy.UI.ViewModels;
using WaBiBaBuSy.WallpaperEngine.Services;
using System.Collections.Specialized;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.UI.Views;

public partial class MainWindow : Window
{
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

        // Add click handler
        border.PointerPressed += (s, e) =>
        {
            Debug.WriteLine($"[MainWindow] Client node clicked: {client.DisplayName}");
            viewModel.SelectClientCommand.Execute(client);
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
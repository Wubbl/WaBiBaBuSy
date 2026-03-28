using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WaBiBaBuSy.UI.ViewModels;
using WaBiBaBuSy.WallpaperEngine.Services;
using System;
using System.Collections.Generic;
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

    // Drag-and-drop reordering state
    private Border? _draggedNodeBorder;
    private ClientNodeViewModel? _draggedClient;
    private Point _nodeDragStartPoint;
    private bool _isNodeDragging;
    private Rectangle? _dropIndicator;
    private int _dropTargetIndex = -1;
    private DateTime _nodeDragStartTime;

    // Layout constants
    private const double NodeWidth = 180;
    private const double NodeMinHeight = 150;
    private const double HSpacing = 20;
    private const double VSpacing = 30;
    private const double LayoutPadding = 20;

    // Arrow animation state
    private readonly List<ArrowAnimationData> _arrowAnimations = new();
    private DispatcherTimer? _arrowAnimTimer;
    private double _arrowAnimProgress; // 0.0 to 1.0, loops

    private record ArrowAnimationData(
        Point Start, Point End, Avalonia.Controls.Shapes.Path ArrowHead,
        double DirectionX, double DirectionY, Color ArrowColor);

    public MainWindow()
    {
        InitializeComponent();

        // Pre-initialize LibVLC in background to eliminate ~9s delay on first wallpaper
        // This runs asynchronously and won't block the UI
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().AddDebug());
        var logger = loggerFactory.CreateLogger<MainWindow>();
        _ = LibVLCPreloader.PreloadAsync(logger);

        // Drag-and-drop handlers for gallery
        AddHandler(DragDrop.DropEvent, OnFileDrop);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);

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

            // Make canvas fill its parent border (at minimum), allowing scroll when nodes overflow
            var topologyBorder = this.FindControl<Border>("TopologyBorder");
            if (topologyBorder != null && _topologyCanvas != null)
            {
                topologyBorder.SizeChanged += (s, e) => UpdateCanvasSize(e.NewSize);
            }

            // Click on empty gallery area deselects wallpaper
            var galleryScrollViewer = this.FindControl<ScrollViewer>("GalleryScrollViewer");
            if (galleryScrollViewer != null)
            {
                galleryScrollViewer.PointerPressed += OnGalleryPointerPressed;
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

        // Stop arrow animation before clearing
        StopArrowAnimation();
        _arrowAnimations.Clear();

        // Clear existing nodes (keep debug text at index 0)
        while (canvas.Children.Count > 1)
        {
            canvas.Children.RemoveAt(1);
        }

        // Auto-wrap layout: calculate positions based on canvas width
        CalculateAutoWrapPositions(canvas, viewModel);

        // Assign group colors to nodes that share a physical machine (multi-monitor)
        AssignGroupColors(viewModel);

        // Add arrow connections between nodes
        DrawArrowConnections(canvas, viewModel);

        // Add client nodes
        foreach (var client in viewModel.Clients.OrderBy(c => c.Order))
        {
            var border = CreateClientNodeBorder(client, viewModel);
            Canvas.SetLeft(border, client.X);
            Canvas.SetTop(border, client.Y);
            canvas.Children.Add(border);

            Debug.WriteLine($"[MainWindow] Added node for {client.DisplayName} at ({client.X}, {client.Y})");
        }

        // Update canvas size to accommodate all nodes
        var topologyBorder = this.FindControl<Border>("TopologyBorder");
        if (topologyBorder != null)
        {
            UpdateCanvasSize(topologyBorder.Bounds.Size);
        }
    }

    private Border CreateClientNodeBorder(ClientNodeViewModel client, MainWindowViewModel viewModel)
    {
        var border = new Border
        {
            Width = 180,
            MinHeight = 150,
            Background = new SolidColorBrush(Color.Parse("#3E3E42")),
            BorderBrush = new SolidColorBrush(Color.Parse("#666666")),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            ClipToBounds = true,
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
            else if (client.GroupColor is Color gc)
            {
                // Multi-monitor node: accent border + very subtle background tint
                border.BorderBrush = new SolidColorBrush(gc);
                border.BorderThickness = new Thickness(2);
                border.Background = new SolidColorBrush(Color.FromArgb(30, gc.R, gc.G, gc.B));
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
            if (e.PropertyName is nameof(client.IsSelected) or nameof(client.IsAnimating)
                or nameof(client.IsCurrentAnimationTarget) or nameof(client.GroupColor))
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

        // Order badge (top-right corner of thumbnail)
        var orderBadge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0078D4")),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(0, -94, 0, 0) // Overlap onto thumbnail
        };
        var orderText = new TextBlock
        {
            Text = $"#{client.Order}",
            Foreground = Brushes.White,
            FontSize = 9,
            FontWeight = FontWeight.Bold
        };
        orderBadge.Child = orderText;
        stackPanel.Children.Add(orderBadge);

        // Update order badge on Order changes
        client.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(client.Order))
            {
                orderText.Text = $"#{client.Order}";
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

        // Resolution + connection status on one line
        var statusColor = client.IsConnected
            ? Color.Parse("#00FF00")
            : Color.Parse("#FF4444");
        var statusPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        if (client.MonitorWidth > 0 && client.MonitorHeight > 0)
        {
            statusPanel.Children.Add(new TextBlock
            {
                Text = $"{client.MonitorWidth}x{client.MonitorHeight}",
                Foreground = new SolidColorBrush(Color.Parse("#888888")),
                FontSize = 10,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            statusPanel.Children.Add(new TextBlock
            {
                Text = "|",
                Foreground = new SolidColorBrush(Color.Parse("#555555")),
                FontSize = 10,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
        }
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
            FontSize = 9,
            MaxWidth = 160,
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

        // Click and drag handler: click = select, drag = reorder
        border.PointerPressed += (s, e) =>
        {
            if (_topologyCanvas == null) return;
            var point = e.GetCurrentPoint(_topologyCanvas);
            if (!point.Properties.IsLeftButtonPressed) return;

            _nodeDragStartPoint = point.Position;
            _draggedNodeBorder = border;
            _draggedClient = client;
            _isNodeDragging = false;
            _nodeDragStartTime = DateTime.UtcNow;
            e.Pointer.Capture((Avalonia.Input.IInputElement)border);
            e.Handled = true; // prevent canvas rectangle-selection
        };

        border.PointerMoved += (s, e) =>
        {
            if (_draggedNodeBorder != border || _draggedClient != client) return;
            if (_topologyCanvas == null) return;

            var pos = e.GetPosition(_topologyCanvas);
            var delta = pos - _nodeDragStartPoint;

            // Start dragging after 5px movement threshold
            if (!_isNodeDragging && (Math.Abs(delta.X) > 5 || Math.Abs(delta.Y) > 5))
            {
                _isNodeDragging = true;
                viewModel.StopRefreshTimer();
                border.Opacity = 0.5;
                border.ZIndex = 100; // bring to front during drag
            }

            if (_isNodeDragging)
            {
                // Move node to follow cursor
                Canvas.SetLeft(border, pos.X - NodeWidth / 2);
                Canvas.SetTop(border, pos.Y - NodeMinHeight / 2);
                UpdateDropIndicator(pos, viewModel.Clients.Count);
            }

            // Safety timeout: cancel drag after 10 seconds
            if (_isNodeDragging && (DateTime.UtcNow - _nodeDragStartTime).TotalSeconds > 10)
            {
                CancelNodeDrag(viewModel);
            }
        };

        border.PointerReleased += (s, e) =>
        {
            e.Pointer.Capture(null);
            if (_draggedNodeBorder != border) return;

            if (!_isNodeDragging)
            {
                // Was a click, not a drag
                HandleNodeClick(client, e.KeyModifiers, viewModel);
            }
            else
            {
                // Complete the drag reorder
                CompleteNodeDrag(viewModel);
                return; // CompleteNodeDrag handles cleanup
            }

            _draggedNodeBorder = null;
            _draggedClient = null;
            _isNodeDragging = false;
        };

        // Hover effect
        border.PointerEntered += (s, e) =>
        {
            if (!client.IsSelected && _draggedNodeBorder == null)
            {
                border.Background = new SolidColorBrush(Color.Parse("#4E4E52"));
            }
        };

        border.PointerExited += (s, e) =>
        {
            if (!client.IsSelected && _draggedNodeBorder == null)
            {
                border.Background = new SolidColorBrush(Color.Parse("#3E3E42"));
            }
        };

        return border;
    }

    /// <summary>
    /// Extract the base machine ID from a client ID (strips _MONITOR_N suffix).
    /// Two nodes with the same base ID are on the same physical machine.
    /// </summary>
    private static string GetBaseMachineId(string clientId)
    {
        var idx = clientId.LastIndexOf("_MONITOR_");
        return idx >= 0 ? clientId[..idx] : clientId;
    }

    /// <summary>
    /// Draw straight-line arrows between consecutive nodes (ordered by Order).
    /// Blue (#0078D4) for same-machine nodes, gray (#888888) for cross-machine.
    /// Arrowheads animate from start to end of each line.
    /// </summary>
    private void DrawArrowConnections(Canvas canvas, MainWindowViewModel viewModel)
    {
        // Clear previous animation data
        _arrowAnimations.Clear();

        var sorted = viewModel.Clients.OrderBy(c => c.Order).ToList();
        if (sorted.Count < 2)
        {
            StopArrowAnimation();
            return;
        }

        const double arrowHeadSize = 10;

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var from = sorted[i];
            var to = sorted[i + 1];

            var fromBaseId = GetBaseMachineId(from.ClientId);
            var toBaseId = GetBaseMachineId(to.ClientId);
            bool sameMachine = fromBaseId == toBaseId;
            var arrowColor = sameMachine
                ? Color.Parse("#0078D4")  // blue
                : Color.Parse("#888888"); // gray

            double startX = from.X + NodeWidth;
            double startY = from.Y + NodeMinHeight / 2;
            double endX = to.X;
            double endY = to.Y + NodeMinHeight / 2;

            // Draw straight line
            var line = new Line
            {
                StartPoint = new Point(startX, startY),
                EndPoint = new Point(endX, endY),
                Stroke = new SolidColorBrush(arrowColor),
                StrokeThickness = 2,
                IsHitTestVisible = false,
                Opacity = 0.6
            };
            canvas.Children.Add(line);

            // Create arrowhead (triangle) that will be animated along the line
            double dx = endX - startX;
            double dy = endY - startY;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) continue;
            double ndx = dx / len;
            double ndy = dy / len;

            var arrowHead = CreateArrowHeadPath(arrowColor, arrowHeadSize, ndx, ndy);
            canvas.Children.Add(arrowHead);

            _arrowAnimations.Add(new ArrowAnimationData(
                new Point(startX, startY), new Point(endX, endY),
                arrowHead, ndx, ndy, arrowColor));
        }

        // Start animation timer if we have arrows
        if (_arrowAnimations.Count > 0)
            StartArrowAnimation();
    }

    /// <summary>
    /// Create an arrowhead Path (filled triangle) pointing in direction (dx, dy).
    /// </summary>
    private static Avalonia.Controls.Shapes.Path CreateArrowHeadPath(
        Color color, double size, double dx, double dy)
    {
        // Perpendicular direction
        double perpX = -dy;
        double perpY = dx;

        // Triangle points relative to (0,0) — will be positioned via Canvas.Left/Top
        var tip = new Point(dx * size * 0.5, dy * size * 0.5);
        var left = new Point(-dx * size * 0.5 + perpX * size * 0.4, -dy * size * 0.5 + perpY * size * 0.4);
        var right = new Point(-dx * size * 0.5 - perpX * size * 0.4, -dy * size * 0.5 - perpY * size * 0.4);

        var figure = new PathFigure
        {
            StartPoint = tip,
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments!.Add(new LineSegment { Point = left });
        figure.Segments!.Add(new LineSegment { Point = right });

        var geometry = new PathGeometry();
        geometry.Figures!.Add(figure);

        return new Avalonia.Controls.Shapes.Path
        {
            Data = geometry,
            Fill = new SolidColorBrush(color),
            IsHitTestVisible = false
        };
    }

    /// <summary>
    /// Start the arrow animation timer (animates arrowheads along their lines).
    /// </summary>
    private void StartArrowAnimation()
    {
        if (_arrowAnimTimer != null) return;

        _arrowAnimTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _arrowAnimTimer.Tick += OnArrowAnimationTick;
        _arrowAnimTimer.Start();
    }

    /// <summary>
    /// Stop the arrow animation timer.
    /// </summary>
    private void StopArrowAnimation()
    {
        _arrowAnimTimer?.Stop();
        _arrowAnimTimer = null;
    }

    /// <summary>
    /// Animation tick: move all arrowheads along their lines.
    /// </summary>
    private void OnArrowAnimationTick(object? sender, EventArgs e)
    {
        // Advance progress: full cycle in ~2.5 seconds
        _arrowAnimProgress += 0.012;
        if (_arrowAnimProgress > 1.0)
            _arrowAnimProgress = 0.0;

        foreach (var arrow in _arrowAnimations)
        {
            // Lerp position along the line
            double x = arrow.Start.X + (arrow.End.X - arrow.Start.X) * _arrowAnimProgress;
            double y = arrow.Start.Y + (arrow.End.Y - arrow.Start.Y) * _arrowAnimProgress;

            Canvas.SetLeft(arrow.ArrowHead, x);
            Canvas.SetTop(arrow.ArrowHead, y);
        }
    }

    // Palette of accent colors for multi-monitor machine groups.
    private static readonly Color[] GroupColorPalette =
    [
        Color.Parse("#E8A020"), // amber
        Color.Parse("#3BAA6B"), // green
        Color.Parse("#A04DC8"), // purple
        Color.Parse("#20A8C8"), // teal
        Color.Parse("#E05050"), // red
        Color.Parse("#D07030"), // orange
    ];

    /// <summary>
    /// Assign a distinct accent color to every node that shares a physical machine with
    /// at least one other node. Single-monitor nodes get GroupColor = null.
    /// </summary>
    private static void AssignGroupColors(MainWindowViewModel viewModel)
    {
        // Clear all group colors first
        foreach (var c in viewModel.Clients)
            c.GroupColor = null;

        var groups = viewModel.Clients
            .GroupBy(c => GetBaseMachineId(c.ClientId))
            .Where(g => g.Count() > 1)
            .ToList();

        for (int i = 0; i < groups.Count; i++)
        {
            var color = GroupColorPalette[i % GroupColorPalette.Length];
            foreach (var node in groups[i])
                node.GroupColor = color;
        }
    }

    /// <summary>
    /// Calculate auto-wrap positions for nodes based on available canvas width.
    /// Nodes flow left-to-right, wrapping to a new row when width is exceeded.
    /// </summary>
    private void CalculateAutoWrapPositions(Canvas canvas, MainWindowViewModel viewModel)
    {
        var topologyBorder = this.FindControl<Border>("TopologyBorder");
        double canvasWidth = topologyBorder?.Bounds.Width ?? canvas.Bounds.Width;
        if (canvasWidth < NodeWidth + LayoutPadding * 2)
            canvasWidth = 800; // sensible fallback

        int nodesPerRow = Math.Max(1, (int)((canvasWidth - LayoutPadding) / (NodeWidth + HSpacing)));

        int index = 0;
        foreach (var client in viewModel.Clients.OrderBy(c => c.Order))
        {
            int col = index % nodesPerRow;
            int row = index / nodesPerRow;
            client.X = LayoutPadding + col * (NodeWidth + HSpacing);
            client.Y = LayoutPadding + row * (NodeMinHeight + VSpacing);
            index++;
        }
    }

    /// <summary>
    /// Get the grid position (node index) for a canvas coordinate, used for drag-and-drop targeting.
    /// </summary>
    private int GetDropIndexAtPosition(Point pos, int nodeCount)
    {
        var topologyBorder = this.FindControl<Border>("TopologyBorder");
        double canvasWidth = topologyBorder?.Bounds.Width ?? _topologyCanvas?.Bounds.Width ?? 800;
        int nodesPerRow = Math.Max(1, (int)((canvasWidth - LayoutPadding) / (NodeWidth + HSpacing)));

        int col = Math.Max(0, (int)((pos.X - LayoutPadding + HSpacing / 2) / (NodeWidth + HSpacing)));
        int row = Math.Max(0, (int)((pos.Y - LayoutPadding + VSpacing / 2) / (NodeMinHeight + VSpacing)));

        col = Math.Min(col, nodesPerRow - 1);
        int index = row * nodesPerRow + col;
        return Math.Clamp(index, 0, nodeCount - 1);
    }

    /// <summary>
    /// Handle node click (extracted for reuse from drag-and-drop vs. click detection).
    /// </summary>
    private void HandleNodeClick(ClientNodeViewModel client, KeyModifiers modifiers, MainWindowViewModel viewModel)
    {
        Debug.WriteLine($"[MainWindow] Client node clicked: {client.DisplayName}");
        var ctrlPressed = (modifiers & KeyModifiers.Control) == KeyModifiers.Control;
        if (ctrlPressed)
        {
            Debug.WriteLine($"[MainWindow] Ctrl+Click: toggling selection for {client.DisplayName}");
            client.IsSelected = !client.IsSelected;
        }
        else
        {
            viewModel.SelectClientCommand.Execute(client);
        }
    }

    /// <summary>
    /// Complete a node drag operation: reorder nodes and persist.
    /// </summary>
    private async void CompleteNodeDrag(MainWindowViewModel viewModel)
    {
        if (_draggedClient == null) return;

        // Remove drop indicator
        if (_dropIndicator != null && _topologyCanvas != null)
        {
            _topologyCanvas.Children.Remove(_dropIndicator);
            _dropIndicator = null;
        }

        // Restore visual state
        if (_draggedNodeBorder != null)
            _draggedNodeBorder.Opacity = 1.0;

        // Calculate new order based on drop position
        var sorted = viewModel.Clients.OrderBy(c => c.Order).ToList();
        var draggedIdx = sorted.IndexOf(_draggedClient);
        if (draggedIdx >= 0 && _dropTargetIndex >= 0 && _dropTargetIndex != draggedIdx)
        {
            // Remove from current position and insert at target
            sorted.RemoveAt(draggedIdx);
            int insertIdx = Math.Min(_dropTargetIndex, sorted.Count);
            sorted.Insert(insertIdx, _draggedClient);

            // Reassign Order values sequentially
            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].Order = i;
            }

            // Persist the new order
            await viewModel.PersistClientOrderAsync();
        }

        // Re-render to snap nodes back to grid
        RenderClientNodes();

        // Restart the refresh timer
        viewModel.StartRefreshTimer();

        _draggedNodeBorder = null;
        _draggedClient = null;
        _isNodeDragging = false;
        _dropTargetIndex = -1;
    }

    /// <summary>
    /// Update the drop indicator position during a node drag.
    /// </summary>
    private void UpdateDropIndicator(Point cursorPos, int nodeCount)
    {
        if (_topologyCanvas == null) return;

        int targetIdx = GetDropIndexAtPosition(cursorPos, nodeCount);
        _dropTargetIndex = targetIdx;

        // Calculate where the indicator line should be
        var topologyBorder = this.FindControl<Border>("TopologyBorder");
        double canvasWidth = topologyBorder?.Bounds.Width ?? _topologyCanvas.Bounds.Width;
        int nodesPerRow = Math.Max(1, (int)((canvasWidth - LayoutPadding) / (NodeWidth + HSpacing)));

        int col = targetIdx % nodesPerRow;
        int row = targetIdx / nodesPerRow;
        double indicatorX = LayoutPadding + col * (NodeWidth + HSpacing) - HSpacing / 2;
        double indicatorY = LayoutPadding + row * (NodeMinHeight + VSpacing) - 5;

        if (_dropIndicator == null)
        {
            _dropIndicator = new Rectangle
            {
                Width = 3,
                Height = NodeMinHeight + 10,
                Fill = new SolidColorBrush(Color.Parse("#0078D4")),
                IsHitTestVisible = false
            };
            _topologyCanvas.Children.Add(_dropIndicator);
        }

        Canvas.SetLeft(_dropIndicator, indicatorX);
        Canvas.SetTop(_dropIndicator, indicatorY);
        _dropIndicator.Height = NodeMinHeight + 10;
    }

    /// <summary>
    /// Cancel a node drag operation without reordering.
    /// </summary>
    private void CancelNodeDrag(MainWindowViewModel viewModel)
    {
        if (_draggedNodeBorder != null)
        {
            _draggedNodeBorder.Opacity = 1.0;
            _draggedNodeBorder.ZIndex = 0;
        }

        if (_dropIndicator != null && _topologyCanvas != null)
        {
            _topologyCanvas.Children.Remove(_dropIndicator);
            _dropIndicator = null;
        }

        _draggedNodeBorder = null;
        _draggedClient = null;
        _isNodeDragging = false;
        _dropTargetIndex = -1;

        // Re-render to reset positions and restart timer
        RenderClientNodes();
        viewModel.StartRefreshTimer();
    }

    /// <summary>
    /// Handle canvas mouse down - start rectangle selection drag
    /// </summary>
    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_topologyCanvas == null || _isNodeDragging)
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
                // Deselect all clients and clear selected client when clicking empty canvas
                if (DataContext is MainWindowViewModel viewModel)
                {
                    foreach (var client in viewModel.Clients)
                    {
                        client.IsSelected = false;
                    }
                    viewModel.SelectedClient = null;
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
        if (!_isDragging || _isNodeDragging || _topologyCanvas == null || _selectionRectangle == null)
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
                    var clientRect = new Rect(clientX, clientY, clientBorder.Bounds.Width, clientBorder.Bounds.Height);

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

    /// <summary>
    /// Handle click on gallery background to deselect wallpaper
    /// </summary>
    private void OnGalleryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only deselect when clicking directly on the ScrollViewer or ItemsControl background,
        // not when clicking on a wallpaper button
        if (e.Source is ScrollViewer or ScrollContentPresenter or ItemsControl or WrapPanel or Border { Name: "GalleryScrollViewer" })
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                foreach (var w in viewModel.Wallpapers)
                    w.IsSelected = false;
                viewModel.SelectedWallpaper = null;
            }
        }
    }

    /// <summary>
    /// Updates the canvas size to fill its container at minimum, but grows when nodes overflow.
    /// </summary>
    private void UpdateCanvasSize(Size containerSize)
    {
        if (_topologyCanvas == null) return;

        // Calculate the required extent from node positions
        double maxRight = 0, maxBottom = 0;
        if (DataContext is MainWindowViewModel viewModel)
        {
            foreach (var client in viewModel.Clients)
            {
                maxRight = Math.Max(maxRight, client.X + 200); // node width ~180 + margin
                maxBottom = Math.Max(maxBottom, client.Y + 170); // node height ~150 + margin
            }
        }

        _topologyCanvas.Width = Math.Max(containerSize.Width, maxRight);
        _topologyCanvas.Height = Math.Max(containerSize.Height, maxBottom);
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

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // Avalonia 11.x: Data is deprecated but DataTransfer requires IAsyncDataTransfer
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
#pragma warning restore CS0618
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
#pragma warning disable CS0618
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        if (files == null) return;
        foreach (var item in files)
        {
            var path = item.Path?.LocalPath;
            if (!string.IsNullOrEmpty(path))
                vm.AddWallpaperFromPath(path);
        }
    }
}
// WaBiBaBuSy.UI/Controls/RoomView.cs
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Models.Networking;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>
/// The room: one lane per seat-map row, one tile per node, the scene painted inside every tile with
/// the players' own math (<see cref="ScenePainter"/>). Geometry and hit testing come from
/// <see cref="RoomGrid"/>; every change goes through <see cref="IRoomHost"/>.
/// Click selects, Ctrl+click toggles, Shift+click adds, dragging empty space draws a selection
/// rectangle, dragging a tile moves it within or between rows (or onto "+ new row").
/// </summary>
public partial class RoomView : Control
{
    public static readonly StyledProperty<IRoomHost?> HostProperty =
        AvaloniaProperty.Register<RoomView, IRoomHost?>(nameof(Host));
    public static readonly StyledProperty<CrossScreenConfig?> SceneProperty =
        AvaloniaProperty.Register<RoomView, CrossScreenConfig?>(nameof(Scene));
    public static readonly StyledProperty<long> SharedStartUtcMsProperty =
        AvaloniaProperty.Register<RoomView, long>(nameof(SharedStartUtcMs));
    public static readonly StyledProperty<string?> SpriteImagePathProperty =
        AvaloniaProperty.Register<RoomView, string?>(nameof(SpriteImagePath));

    public IRoomHost? Host { get => GetValue(HostProperty); set => SetValue(HostProperty, value); }
    /// <summary>Scene painted in the tiles (the running scene for now; the editor draft in Plan 2).</summary>
    public CrossScreenConfig? Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    /// <summary>&gt; 0 = live clock (players' shared start).</summary>
    public long SharedStartUtcMs { get => GetValue(SharedStartUtcMsProperty); set => SetValue(SharedStartUtcMsProperty, value); }
    public string? SpriteImagePath { get => GetValue(SpriteImagePathProperty); set => SetValue(SpriteImagePathProperty, value); }

    private const double DragThreshold = 5;

    private static readonly IBrush ViewBackground = new SolidColorBrush(Color.Parse("#1A1A1A"));
    private static readonly IBrush LaneFill = new SolidColorBrush(Color.Parse("#202023"));
    private static readonly IPen LanePen = new Pen(new SolidColorBrush(Color.Parse("#333338")), 1);
    private static readonly IBrush SceneBg = new SolidColorBrush(Color.Parse("#141414"));
    private static readonly IBrush Caption = new SolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush Faint = new SolidColorBrush(Color.Parse("#666666"));
    private static readonly IBrush ChipFill = new SolidColorBrush(Color.Parse("#2D2D30"));
    private static readonly IPen ChipPen = new Pen(new SolidColorBrush(Color.Parse("#4A4A50")), 1);
    private static readonly IBrush BadgeBrush = new SolidColorBrush(Color.Parse("#0078D4"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FFD080"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#0078D4"));
    private static readonly IBrush AccentFaint = new SolidColorBrush(Color.Parse("#0078D4"), 0.2);
    private static readonly IPen DashedPen = new Pen(new SolidColorBrush(Color.Parse("#4A4A50")), 1, new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IPen DashedHotPen = new Pen(new SolidColorBrush(Color.Parse("#0078D4")), 2, new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#00CC66"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF4444"));
    private static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#FFC800"));

    private readonly ScenePainter _painter = new();
    private readonly SceneClock _clock = new();
    private DispatcherTimer? _timer;
    private SeatMapLayoutResult _layout = new();
    private RoomGrid _grid = RoomGrid.Build(SeatMap.SingleRow(), new SeatMapLayoutResult());
    private IRoomHost? _host;
    private System.Collections.ObjectModel.ObservableCollection<ClientNodeViewModel>? _clients;
    private readonly List<ClientNodeViewModel> _watched = new();

    private enum Gesture { None, PendingTile, DraggingTile, RubberBand }
    private Gesture _gesture;
    private Point _pressPoint;
    private Point _pointer;
    private string? _pressedId;
    private KeyModifiers _pressModifiers;
    private DropTarget? _drop;
    private string? _hoverId;

    static RoomView()
    {
        AffectsRender<RoomView>(SceneProperty, SharedStartUtcMsProperty, SpriteImagePathProperty);
        FocusableProperty.OverrideDefaultValue<RoomView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<RoomView>(true);
    }

    // ── host wiring ──────────────────────────────────────────────────────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HostProperty) AttachHost(Host);
        else if (change.Property == SpriteImagePathProperty) _painter.SpriteImagePath = SpriteImagePath;
        else if (change.Property == SharedStartUtcMsProperty) _clock.SharedStartUtcMs = SharedStartUtcMs;
        else if (change.Property == SceneProperty && !_clock.IsLive) _clock.Restart();
    }

    private void AttachHost(IRoomHost? host)
    {
        if (_host != null) _host.PropertyChanged -= OnHostPropertyChanged;
        if (_clients != null) _clients.CollectionChanged -= OnClientsChanged;
        UnwatchClients();

        _host = host;
        _clients = host?.Clients;
        if (_host != null) _host.PropertyChanged += OnHostPropertyChanged;
        if (_clients != null) _clients.CollectionChanged += OnClientsChanged;
        WatchClients();
        Relayout();
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IRoomHost.SeatMapVersion)) OnUi(Relayout);
        else if (e.PropertyName == nameof(IRoomHost.Clients)) OnUi(() => AttachHost(_host));
    }

    private void OnClientsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnUi(() =>
    {
        CancelGesture();   // a node joined or left mid-drag: never apply a stale move
        UnwatchClients();
        WatchClients();
        Relayout();
    });

    private void WatchClients()
    {
        if (_clients == null) return;
        foreach (var c in _clients)
        {
            c.PropertyChanged += OnClientPropertyChanged;
            _watched.Add(c);
        }
    }

    private void UnwatchClients()
    {
        foreach (var c in _watched) c.PropertyChanged -= OnClientPropertyChanged;
        _watched.Clear();
    }

    private void OnClientPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnUi(() =>
    {
        if (e.PropertyName is nameof(ClientNodeViewModel.Order) or nameof(ClientNodeViewModel.MonitorWidth)
            or nameof(ClientNodeViewModel.MonitorHeight) or nameof(ClientNodeViewModel.PhysicalDistanceCm)
            or nameof(ClientNodeViewModel.PixelsPerCm))
        {
            if (_gesture == Gesture.None) Relayout();
        }
        else InvalidateVisual();
    });

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void Relayout()
    {
        var host = _host;
        _layout = host == null || host.Clients.Count == 0 ? new SeatMapLayoutResult() : host.BuildSeatLayout(host.Clients);
        _grid = RoomGrid.Build(host?.SeatMap ?? SeatMap.SingleRow(), _layout);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(_grid.Width, _grid.Height);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            if (IsEffectivelyVisible && (ScenePainter.HasSprite(Scene) || _gesture != Gesture.None)) InvalidateVisual();
        });
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        CancelGesture();
        base.OnDetachedFromVisualTree(e);
    }

    // ── rendering ────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        ctx.FillRectangle(ViewBackground, new Rect(Bounds.Size));
        var host = _host;
        if (host == null) return;

        if (_grid.Tiles.Count == 0)
            DrawText(ctx, "No nodes yet — start the server or connect clients.", new Point(RoomGrid.Margin, RoomGrid.Margin + 4), Dim, 12);

        var scene = ScenePainter.Resolve(Scene, _layout);
        long elapsed = _clock.ElapsedMs();
        var byId = host.Clients.GroupBy(c => c.ClientId).ToDictionary(g => g.Key, g => g.First());

        foreach (var lane in _grid.Lanes) DrawLane(ctx, lane, host.SeatMap);
        foreach (var tile in _grid.Tiles) DrawTile(ctx, tile, byId.GetValueOrDefault(tile.Id), scene, elapsed);
        foreach (var gap in _grid.Gaps) DrawGap(ctx, gap, byId.GetValueOrDefault(gap.HolderId));
        DrawNewRowZone(ctx);

        if (_gesture == Gesture.DraggingTile)
        {
            if (_drop is { } d && d.Row < _grid.Lanes.Count)
                ctx.FillRectangle(Accent, new Rect(d.MarkerX - 1.5, d.MarkerTop, 3, d.MarkerHeight));
            var ghost = new Rect(_pointer.X - RoomGrid.TileW / 2, _pointer.Y - 30, RoomGrid.TileW, RoomGrid.TileH);
            ctx.DrawRectangle(AccentFaint, new Pen(Accent, 2), ghost, 8, 8);
        }
        else if (_gesture == Gesture.RubberBand)
        {
            ctx.DrawRectangle(AccentFaint, new Pen(Accent, 1.5), BandRect());
        }
    }

    private void DrawLane(DrawingContext ctx, LaneBox lane, SeatMap map)
    {
        ctx.DrawRectangle(LaneFill, LanePen, ToRect(lane.Body), 6, 6);
        var def = lane.Row < map.Rows.Count ? map.Rows[lane.Row] : null;
        string name = def?.Name ?? $"Row {lane.Row + 1}";
        string arrow = map.Traversal == TraversalMode.Parallel ? "" : lane.Reversed ? "   ←" : "   →";
        string ring = map.Traversal == TraversalMode.Ring && lane.Row == 0 && _grid.Lanes.Count > 1 ? "   · ring" : "";
        DrawText(ctx, name + arrow + ring, new Point(lane.Header.X + 4, lane.Header.Y + 5), Caption, 12, bold: true);
        if (lane.Row > 0)
            DrawChip(ctx, lane.FacingChip, def?.Orientation == RowOrientation.Facing ? "↕ facing" : "⇉ same side");
        DrawChip(ctx, lane.MenuChip, "⋯");
    }

    private static void DrawChip(DrawingContext ctx, Box box, string text)
    {
        ctx.DrawRectangle(ChipFill, ChipPen, ToRect(box), 4, 4);
        var ft = Format(text, Caption, 10, bold: false);
        ctx.DrawText(ft, new Point(box.X + (box.W - ft.Width) / 2, box.Y + (box.H - ft.Height) / 2));
    }

    private void DrawTile(DrawingContext ctx, TileBox t, ClientNodeViewModel? c, CrossScreenConfig? scene, long elapsed)
    {
        bool dragged = _gesture == Gesture.DraggingTile && t.Id == _pressedId;
        using var opacity = ctx.PushOpacity(dragged ? 0.35 : 1.0);

        var (fill, pen) = TileStyle(c, t.Id == _hoverId);
        ctx.DrawRectangle(fill, pen, ToRect(t.Tile), 8, 8);

        var sceneBox = ToRect(t.Scene);
        ctx.FillRectangle(SceneBg, sceneBox, 4);
        var node = _layout.Get(t.Id);
        if (node != null && ScenePainter.HasSprite(scene))
        {
            double s = Math.Min(sceneBox.Width / node.Width, sceneBox.Height / node.Height);
            var screen = new Rect(sceneBox.X + (sceneBox.Width - node.Width * s) / 2,
                                  sceneBox.Y + (sceneBox.Height - node.Height * s) / 2,
                                  node.Width * s, node.Height * s);
            _painter.PaintBackground(ctx, screen, s, scene);
            _painter.PaintSprite(ctx, screen, s, node, scene!, elapsed);
        }
        else if (c?.ThumbnailImage is { } thumb)
        {
            using var clip = ctx.PushClip(sceneBox);
            double k = Math.Max(sceneBox.Width / thumb.Size.Width, sceneBox.Height / thumb.Size.Height);
            var dest = new Rect(sceneBox.Center.X - thumb.Size.Width * k / 2, sceneBox.Center.Y - thumb.Size.Height * k / 2,
                                thumb.Size.Width * k, thumb.Size.Height * k);
            ctx.DrawImage(thumb, new Rect(thumb.Size), dest);
        }
        else
        {
            var ft = Format("idle", Faint, 10, bold: false);
            ctx.DrawText(ft, new Point(sceneBox.Center.X - ft.Width / 2, sceneBox.Center.Y - ft.Height / 2));
        }

        var badge = new Rect(sceneBox.X + 4, sceneBox.Y + 4, 28, 15);
        ctx.FillRectangle(BadgeBrush, badge, 3);
        DrawText(ctx, $"#{c?.Order ?? t.ChainIndex}", new Point(badge.X + 4, badge.Y + 1), Brushes.White, 9, bold: true);

        if (_host?.NodeWarning(t.Id) != null)
            DrawText(ctx, "⚠", new Point(sceneBox.Right - 16, sceneBox.Y + 1), WarnBrush, 12);

        double x = t.Tile.X + 8, w = t.Tile.W - 16, y = sceneBox.Bottom + 5;
        DrawText(ctx, c?.DisplayName ?? t.Id, new Point(x, y), Brushes.White, 12, bold: true, maxWidth: w);
        y += 16;
        string res = c == null || c.MonitorWidth <= 0 ? c?.IpAddress ?? "" :
            c.MonitorRefreshHz > 0 ? $"{c.MonitorWidth}×{c.MonitorHeight} @ {c.MonitorRefreshHz} Hz" : $"{c.MonitorWidth}×{c.MonitorHeight}";
        DrawText(ctx, res, new Point(x, y), Dim, 10, maxWidth: w - 14);
        ctx.DrawEllipse(c?.IsConnected == true ? Green : Red, null, new Point(t.Tile.Right - 12, y + 7), 4, 4);
        y += 14;
        var (status, statusBrush) = StatusLine(c);
        if (status.Length > 0) DrawText(ctx, status, new Point(x, y), statusBrush, 9, maxWidth: w);
    }

    /// <summary>Drift label first, then prefetch progress, then the running animation name (same wording as the old topology).</summary>
    private static (string Text, IBrush Brush) StatusLine(ClientNodeViewModel? c)
    {
        if (c == null) return ("", Dim);
        switch (c.DriftState)
        {
            case DriftState.Stale: return ("sync: —", Dim);
            case DriftState.Ok: return ($"±{Math.Abs(c.DriftMs):F0}ms", Green);
            case DriftState.Warn: return ($"±{Math.Abs(c.DriftMs):F0}ms", Yellow);
            case DriftState.None: break;
            default: return ($"±{Math.Abs(c.DriftMs):F0}ms", Red);
        }
        if (c.PrefetchTotal > 0)
            return c.IsPrefetchComplete ? ("✓ cached", Green) : ($"⬇ {c.PrefetchReady}/{c.PrefetchTotal}", Yellow);
        if (c.IsAnimating && !string.IsNullOrEmpty(c.ActiveAnimationName)) return (c.ActiveAnimationName!, Green);
        return ("", Dim);
    }

    private static (IBrush Fill, IPen Pen) TileStyle(ClientNodeViewModel? c, bool hover)
    {
        if (c?.IsCurrentAnimationTarget == true) return (Solid("#4E4A2E"), new Pen(Solid("#FFD700"), 3));
        if (c?.IsAnimating == true) return (Solid("#2E4A3E"), new Pen(Solid("#00AA44"), 3));
        if (c?.IsSelected == true) return (Solid("#3E4A5E"), new Pen(Solid("#0078D4"), 3));
        if (c?.GroupColor is Color gc) return (new SolidColorBrush(Color.FromArgb(30, gc.R, gc.G, gc.B)), new Pen(new SolidColorBrush(gc), 2));
        return (Solid(hover ? "#35353A" : "#2A2A2E"), new Pen(Solid("#555555"), 1.5));
    }

    private void DrawGap(DrawingContext ctx, GapBox gap, ClientNodeViewModel? holder)
    {
        int cm = holder?.PhysicalDistanceCm ?? 0;
        var ft = Format(cm > 0 ? $"{cm}" : "·", cm > 0 ? Caption : Faint, 9, bold: false);
        ctx.DrawText(ft, new Point(gap.Hit.X + (gap.Hit.W - ft.Width) / 2, gap.Hit.Y + (gap.Hit.H - ft.Height) / 2));
    }

    private void DrawNewRowZone(DrawingContext ctx)
    {
        bool hot = _gesture == Gesture.DraggingTile && _drop?.Row == _grid.Lanes.Count;
        var zone = ToRect(_grid.NewRowZone);
        ctx.DrawRectangle(hot ? AccentFaint : null, hot ? DashedHotPen : DashedPen, zone, 6, 6);
        var ft = Format("+ new row — drop a node here", hot ? Caption : Faint, 11, bold: false);
        ctx.DrawText(ft, new Point(zone.X + 12, zone.Y + (zone.Height - ft.Height) / 2));
    }

    // ── pointer input ────────────────────────────────────────────────────────

    /// <summary>Right-click on a tile (Task 7: node context menu). The tile is already selected.</summary>
    partial void HandleSecondaryPress(TileBox tile);

    /// <summary>Left press on lane chips or gap handles (Task 7). Set <paramref name="handled"/> to stop tile/rubber-band handling.</summary>
    partial void HandleChromePress(Point p, ref bool handled);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var host = _host;
        if (host == null) return;
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        Focus();
        var tile = _grid.HitTile(p.X, p.Y);

        if (props.IsRightButtonPressed)
        {
            if (tile != null)
            {
                // Windows convention: right-clicking outside the selection selects just that node first.
                if (!IsSelected(tile.Id)) host.SelectOnly(tile.Id);
                HandleSecondaryPress(tile);
            }
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        bool handled = false;
        HandleChromePress(p, ref handled);
        if (handled) { e.Handled = true; return; }

        _pressPoint = _pointer = p;
        _pressModifiers = e.KeyModifiers;
        if (tile != null)
        {
            _gesture = Gesture.PendingTile;
            _pressedId = tile.Id;
        }
        else
        {
            _gesture = Gesture.RubberBand;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) host.ClearSelection();
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _pointer = p;
        switch (_gesture)
        {
            case Gesture.PendingTile:
                if (Math.Abs(p.X - _pressPoint.X) > DragThreshold || Math.Abs(p.Y - _pressPoint.Y) > DragThreshold)
                {
                    _gesture = Gesture.DraggingTile;
                    _host?.BeginNodeDrag();
                    goto case Gesture.DraggingTile;
                }
                break;
            case Gesture.DraggingTile:
                _drop = _grid.ResolveDrop(p.X, p.Y);
                InvalidateVisual();
                break;
            case Gesture.RubberBand:
            {
                var band = BandRect();
                var ids = _grid.Tiles.Where(t => band.Intersects(ToRect(t.Tile))).Select(t => t.Id).ToList();
                _host?.SetRubberBandSelection(ids, additive: _pressModifiers.HasFlag(KeyModifiers.Control));
                InvalidateVisual();
                break;
            }
            default:
                UpdateHover(p);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var host = _host;
        var gesture = _gesture;
        var id = _pressedId;
        var drop = _drop;
        var modifiers = _pressModifiers;
        ResetGesture();
        e.Pointer.Capture(null);
        if (host == null) return;

        if (gesture == Gesture.PendingTile && id != null)
        {
            if (modifiers.HasFlag(KeyModifiers.Control)) host.ToggleSelection(id);
            else if (modifiers.HasFlag(KeyModifiers.Shift)) host.AddToSelection(id);
            else host.SelectOnly(id);
        }
        else if (gesture == Gesture.DraggingTile)
        {
            _ = RunSafe(FinishDragAsync(host, id, drop));
        }
    }

    /// <summary>
    /// Persist the move first, then resume topology refreshes — resuming first would let a refresh
    /// write the server's old order back onto the clients before the new one is stored.
    /// </summary>
    private static async Task FinishDragAsync(IRoomHost host, string? id, DropTarget? drop)
    {
        try
        {
            if (id != null && drop is { } d)
                await host.MoveNodeAsync(id, d.Row, d.ChainIndex);
        }
        finally
        {
            host.EndNodeDrag();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_gesture != Gesture.None) CancelGesture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _gesture != Gesture.None)
        {
            CancelGesture();
            e.Handled = true;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverId != null) { _hoverId = null; ToolTip.SetTip(this, null); InvalidateVisual(); }
    }

    private void CancelGesture()
    {
        if (_gesture == Gesture.DraggingTile) _host?.EndNodeDrag();
        ResetGesture();
    }

    private void ResetGesture()
    {
        _gesture = Gesture.None;
        _pressedId = null;
        _drop = null;
        InvalidateVisual();
    }

    private void UpdateHover(Point p)
    {
        var id = _grid.HitTile(p.X, p.Y)?.Id;
        if (id == _hoverId) return;
        _hoverId = id;
        ToolTip.SetTip(this, id == null ? null : TooltipFor(id));
        InvalidateVisual();
    }

    private string TooltipFor(string id)
    {
        var c = _host?.Clients.FirstOrDefault(x => x.ClientId == id);
        if (c == null) return id;
        var lines = new List<string> { c.DisplayName, c.IpAddress, c.MonitorDisplayName };
        var warning = _host?.NodeWarning(id);
        if (warning != null) lines.Add("⚠ " + warning);
        return string.Join(Environment.NewLine, lines.Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    private bool IsSelected(string id) => _host?.Clients.Any(c => c.ClientId == id && c.IsSelected) == true;

    private Rect BandRect() => new(
        Math.Min(_pressPoint.X, _pointer.X), Math.Min(_pressPoint.Y, _pointer.Y),
        Math.Abs(_pointer.X - _pressPoint.X), Math.Abs(_pointer.Y - _pressPoint.Y));

    private static async Task RunSafe(Task task)
    {
        try { await task; }
        catch (Exception ex) { AppLogger.CreateLogger<RoomView>().LogError(ex, "Room edit failed"); }
    }

    // ── drawing helpers ──────────────────────────────────────────────────────

    private static Rect ToRect(Box b) => new(b.X, b.Y, b.W, b.H);

    private static SolidColorBrush Solid(string hex) => new(Color.Parse(hex));

    private static FormattedText Format(string text, IBrush brush, double size, bool bold) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), size, brush);

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush, double size, bool bold = false, double maxWidth = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = Format(text, brush, size, bold);
        if (maxWidth > 0)
        {
            ft.MaxTextWidth = maxWidth;
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        ctx.DrawText(ft, at);
    }
}

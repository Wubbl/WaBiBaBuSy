using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>
/// Live preview of a cross-screen scene over the room's seat map.
///
/// Draws every node as a rectangle in its lane (row) and animates the sprite / pattern with the
/// very same pure functions the D2D players run (<see cref="MovementCalculator"/>,
/// <see cref="PatternLayout"/>, <see cref="ColorGrader"/>, <see cref="NodeMapping"/>,
/// <see cref="SyncTiming"/>). Two clocks:
/// <list type="bullet">
/// <item><b>Design clock</b> (default): a local stopwatch with <see cref="ClockSpeed"/> and pause,
/// restarted by <see cref="RestartClock"/> whenever the config changes.</item>
/// <item><b>Live clock</b>: when <see cref="SharedStartUtcMs"/> &gt; 0, elapsed = now − that
/// timestamp — the exact epoch the players use, so the preview shows where the sprite <i>is</i>.</item>
/// </list>
/// Fidelity: geometry + color only. GIF frame timing, IconZone path-following and LibVLC are not
/// simulated (IconZone falls back to the plain movement).
/// </summary>
public class ScenePreviewControl : Control
{
    public static readonly StyledProperty<SeatMapLayoutResult?> LayoutProperty =
        AvaloniaProperty.Register<ScenePreviewControl, SeatMapLayoutResult?>(nameof(Layout));

    public static readonly StyledProperty<CrossScreenConfig?> SceneProperty =
        AvaloniaProperty.Register<ScenePreviewControl, CrossScreenConfig?>(nameof(Scene));

    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> LabelsProperty =
        AvaloniaProperty.Register<ScenePreviewControl, IReadOnlyDictionary<string, string>?>(nameof(Labels));

    public static readonly StyledProperty<string?> SpriteImagePathProperty =
        AvaloniaProperty.Register<ScenePreviewControl, string?>(nameof(SpriteImagePath));

    /// <summary>Shared UTC start (ms). &gt; 0 switches to the live clock.</summary>
    public static readonly StyledProperty<long> SharedStartUtcMsProperty =
        AvaloniaProperty.Register<ScenePreviewControl, long>(nameof(SharedStartUtcMs));

    public static readonly StyledProperty<double> ClockSpeedProperty =
        AvaloniaProperty.Register<ScenePreviewControl, double>(nameof(ClockSpeed), 1.0);

    public static readonly StyledProperty<bool> IsPausedProperty =
        AvaloniaProperty.Register<ScenePreviewControl, bool>(nameof(IsPaused));

    public SeatMapLayoutResult? Layout { get => GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public CrossScreenConfig? Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public IReadOnlyDictionary<string, string>? Labels { get => GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public string? SpriteImagePath { get => GetValue(SpriteImagePathProperty); set => SetValue(SpriteImagePathProperty, value); }
    public long SharedStartUtcMs { get => GetValue(SharedStartUtcMsProperty); set => SetValue(SharedStartUtcMsProperty, value); }
    public double ClockSpeed { get => GetValue(ClockSpeedProperty); set => SetValue(ClockSpeedProperty, value); }
    public bool IsPaused { get => GetValue(IsPausedProperty); set => SetValue(IsPausedProperty, value); }

    // Design clock: accumulated ms at the current speed + running stopwatch.
    private readonly Stopwatch _stopwatch = new();
    private double _accumulatedMs;
    private double _lastSpeed = 1.0;

    private DispatcherTimer? _timer;

    // Sprite bitmap cache (one entry — the current path).
    private string? _bitmapPath;
    private Bitmap? _bitmap;
    private bool _bitmapFailed;

    private const double Pad = 10;
    private const double RowLabelH = 14;
    private const double LegendH = 16;
    private const int MaxPreviewCellsPerNode = 400;

    private static readonly IBrush NodeFill = new SolidColorBrush(Color.Parse("#202020"));
    private static readonly IPen NodePen = new Pen(new SolidColorBrush(Color.Parse("#3A3A3A")), 1);
    private static readonly IPen NodePenHot = new Pen(new SolidColorBrush(Color.Parse("#00CC66")), 1.5);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#E0B060"));
    private static readonly IBrush SeamBrush = new SolidColorBrush(Color.Parse("#555555"));
    private static readonly IBrush MonoCellBrush = new SolidColorBrush(Color.Parse("#BBBBBB"));
    private static readonly Typeface Font = Typeface.Default;

    static ScenePreviewControl()
    {
        AffectsRender<ScenePreviewControl>(LayoutProperty, SceneProperty, LabelsProperty, SpriteImagePathProperty, SharedStartUtcMsProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ScenePreviewControl>(true);
    }

    public ScenePreviewControl()
    {
        _stopwatch.Start();
    }

    /// <summary>Restart the design clock at t = 0 (call after every config change).</summary>
    public void RestartClock()
    {
        _accumulatedMs = 0;
        _stopwatch.Restart();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClockSpeedProperty)
        {
            // Fold the time run so far at the old speed, then continue at the new one.
            _accumulatedMs += _stopwatch.Elapsed.TotalMilliseconds * _lastSpeed;
            _stopwatch.Restart();
            _lastSpeed = Math.Max(0.01, ClockSpeed);
        }
        else if (change.Property == IsPausedProperty)
        {
            if (IsPaused)
            {
                _accumulatedMs += _stopwatch.Elapsed.TotalMilliseconds * _lastSpeed;
                _stopwatch.Reset();
            }
            else
            {
                _stopwatch.Start();
            }
        }
        else if (change.Property == SpriteImagePathProperty)
        {
            _bitmapPath = null; _bitmapFailed = false; _bitmap = null;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            if (IsEffectivelyVisible && Bounds.Width > 0) InvalidateVisual();
        });
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private long CurrentElapsedMs()
    {
        if (SharedStartUtcMs > 0)
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - SharedStartUtcMs;
        double running = IsPaused ? 0 : _stopwatch.Elapsed.TotalMilliseconds * _lastSpeed;
        return (long)(_accumulatedMs + running);
    }

    private Bitmap? GetSpriteBitmap()
    {
        var path = SpriteImagePath;
        if (string.IsNullOrEmpty(path)) return null;
        if (path == _bitmapPath) return _bitmapFailed ? null : _bitmap;
        _bitmapPath = path;
        _bitmap = null; _bitmapFailed = false;
        try
        {
            if (File.Exists(path)) _bitmap = new Bitmap(path);
            else _bitmapFailed = true;
        }
        catch { _bitmapFailed = true; }
        return _bitmap;
    }

    // ───────────────────────────── rendering ─────────────────────────────

    private sealed record LaneNode(NodeLayout Node, Rect Rect);

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var bounds = Bounds;
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#141414")), new Rect(bounds.Size));

        var layout = Layout;
        if (layout == null || layout.Nodes.Count == 0)
        {
            DrawText(ctx, "No nodes selected — the preview shows the sprite travelling across the selected monitors.", new Point(Pad, Pad), LabelBrush, 11);
            return;
        }

        // ── lanes ──
        var rows = layout.Nodes.GroupBy(n => n.RowIndex).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        var rowMinX = rows.Select(r => r.Min(n => n.OffsetX)).ToList();
        var rowW = rows.Select((r, i) => r.Max(n => n.OffsetX + n.Width) - rowMinX[i]).ToList();
        var rowMinY = rows.Select(r => r.Min(n => n.OffsetY)).ToList();
        var rowH = rows.Select((r, i) => r.Max(n => n.OffsetY + n.Height) - rowMinY[i]).ToList();

        double totalW = rowW.Max();
        double rowGapVirtual = rows.Count > 1 ? rowH.Max() * 0.35 : 0;
        double totalH = rowH.Sum() + rowGapVirtual * (rows.Count - 1);
        double availW = Math.Max(1, bounds.Width - 2 * Pad);
        double availH = Math.Max(1, bounds.Height - 2 * Pad - LegendH - rows.Count * RowLabelH);
        double s = Math.Min(availW / totalW, availH / totalH);
        if (s <= 0 || double.IsNaN(s) || double.IsInfinity(s)) return;

        var lanes = new List<LaneNode>();
        double y = Pad;
        for (int r = 0; r < rows.Count; r++)
        {
            y += RowLabelH;
            foreach (var n in rows[r])
            {
                var rect = new Rect(
                    Pad + (n.OffsetX - rowMinX[r]) * s,
                    y + (n.OffsetY - rowMinY[r]) * s,
                    Math.Max(1, n.Width * s),
                    Math.Max(1, n.Height * s));
                lanes.Add(new LaneNode(n, rect));
            }
            DrawText(ctx, RowCaption(r, rows.Count, layout), new Point(Pad, y - RowLabelH + 1), LabelBrush, 10);
            y += rowH[r] * s + rowGapVirtual * s;
        }

        // Resolve cm → canvas px exactly like the apply path does, so the preview and the wall agree.
        var scene = Scene;
        if (scene != null)
        {
            var (mv, an) = PhysicalUnits.ResolveForCanvas(scene, layout.RefPixelsPerCm);
            scene = new CrossScreenConfig
            {
                Background = scene.Background, Movement = mv, Animation = an,
                DistributionMode = scene.DistributionMode, SelectedMonitorIds = scene.SelectedMonitorIds,
                AnimationSpeedPxPerSecond = scene.AnimationSpeedPxPerSecond
            };
        }
        long elapsed = CurrentElapsedMs();
        var bg = scene?.Background;
        var bgBrush = new SolidColorBrush(ParseHex(bg == null ? "#000000" :
            bg.Mode == BackgroundMode.IconZone ? bg.IconCorridorColorHex :
            bg.Mode == BackgroundMode.ThreeZone ? bg.CorridorColorHex : bg.ColorHex));

        string? hotNodeId = null;
        var (animW, animH) = SpriteSizeFor(scene, lanes[0].Node);

        foreach (var lane in lanes)
        {
            ctx.FillRectangle(bgBrush, lane.Rect);
            if (bg?.Mode == BackgroundMode.ThreeZone)
                DrawThreeZone(ctx, lane, bg, s);
            ctx.DrawRectangle(null, NodePen, lane.Rect);
        }

        if (scene != null && !string.IsNullOrEmpty(scene.Animation.AnimationPath))
        {
            foreach (var lane in lanes)
            {
                using (ctx.PushClip(lane.Rect))
                {
                    var hit = DrawSceneOnNode(ctx, lane, scene, layout, elapsed, s);
                    if (hit) hotNodeId = lane.Node.Id;
                }
            }
        }

        // Ring seam marker on the first and last node
        if (layout.Wraps && lanes.Count > 1)
        {
            var first = lanes.First(l => l.Node.Order == lanes.Min(x => x.Node.Order));
            var last = lanes.First(l => l.Node.Order == lanes.Max(x => x.Node.Order));
            var seamPen = new Pen(SeamBrush, 1, new DashStyle(new double[] { 2, 2 }, 0));
            double fx = first.Node.Mirrored ? first.Rect.Right : first.Rect.Left;
            double lx = last.Node.Mirrored ? last.Rect.Left : last.Rect.Right;
            ctx.DrawLine(seamPen, new Point(fx, first.Rect.Top), new Point(fx, first.Rect.Bottom));
            ctx.DrawLine(seamPen, new Point(lx, last.Rect.Top), new Point(lx, last.Rect.Bottom));
        }

        // Labels + highlight
        var labels = Labels;
        foreach (var lane in lanes)
        {
            bool hot = lane.Node.Id == hotNodeId;
            if (hot) ctx.DrawRectangle(null, NodePenHot, lane.Rect);
            string name = labels != null && labels.TryGetValue(lane.Node.Id, out var l) ? l : lane.Node.Id;
            string caption = $"#{lane.Node.Order + 1} {Shorten(name, Math.Max(3, (int)(lane.Rect.Width / 6.5)))}";
            if (lane.Rect.Width >= 28)
                DrawText(ctx, caption, new Point(lane.Rect.Left + 2, lane.Rect.Bottom - 12), hot ? InfoBrush : LabelBrush, 9);
        }

        // Legend
        DrawText(ctx, BuildInfoLine(scene, layout, elapsed, animW, hotNodeId, labels), new Point(Pad, bounds.Height - LegendH - 1), InfoBrush, 10);
    }

    private static string RowCaption(int r, int rows, SeatMapLayoutResult layout)
    {
        if (rows == 1) return "";
        string dir = layout.Traversal == TraversalMode.Parallel ? "" : (r % 2 == 0 ? "  →" : "  ←");
        return $"Row {r + 1}{dir}";
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..Math.Max(1, max - 1)] + "…";

    private static void DrawThreeZone(DrawingContext ctx, LaneNode lane, BackgroundLayerConfig bg, double s)
    {
        double top = lane.Rect.Top + bg.CorridorTopPx * s;
        double h = bg.CorridorHeightPx * s;
        ctx.FillRectangle(new SolidColorBrush(ParseHex(bg.TopZoneColorHex)), new Rect(lane.Rect.Left, lane.Rect.Top, lane.Rect.Width, Math.Max(0, top - lane.Rect.Top)));
        ctx.FillRectangle(new SolidColorBrush(ParseHex(bg.BottomZoneColorHex)), new Rect(lane.Rect.Left, Math.Min(lane.Rect.Bottom, top + h), lane.Rect.Width, Math.Max(0, lane.Rect.Bottom - (top + h))));
    }

    /// <summary>Sprite size after FitMode — mirrors the player's CalculateAnimationLayout for the given node.</summary>
    private (int W, int H) SpriteSizeFor(CrossScreenConfig? scene, NodeLayout node)
    {
        if (scene == null) return (0, 0);
        int nativeW = 400, nativeH = 300;
        var bmp = GetSpriteBitmap();
        if (bmp != null && bmp.PixelSize.Width > 0 && bmp.PixelSize.Height > 0)
        {
            nativeW = bmp.PixelSize.Width; nativeH = bmp.PixelSize.Height;
        }
        else if (scene.Animation.TargetHeight > 0)
        {
            nativeH = scene.Animation.TargetHeight; nativeW = (int)(nativeH * 16 / 9.0);
        }
        switch (scene.Animation.FitMode)
        {
            case ContentFitMode.Fit:
            {
                double k = Math.Min((double)node.Width / nativeW, (double)node.Height / nativeH);
                return ((int)(nativeW * k), (int)(nativeH * k));
            }
            case ContentFitMode.Fill:
            {
                double k = Math.Max((double)node.Width / nativeW, (double)node.Height / nativeH);
                return ((int)(nativeW * k), (int)(nativeH * k));
            }
            case ContentFitMode.Stretch:
                return (node.Width, node.Height);
            case ContentFitMode.TargetHeight:
            {
                int th = Math.Max(1, scene.Animation.TargetHeight);
                return (Math.Max(1, (int)Math.Round(nativeW * (double)th / nativeH)), th);
            }
            default:
                return (nativeW, nativeH);
        }
    }

    /// <summary>Draws the scene on one node; returns true when the sprite's center is on this node.</summary>
    private bool DrawSceneOnNode(DrawingContext ctx, LaneNode lane, CrossScreenConfig scene, SeatMapLayoutResult layout, long elapsed, double s)
    {
        var node = lane.Node;
        bool perMonitor = scene.DistributionMode == AnimationDistributionMode.Simultaneous;
        long e = SyncTiming.ApplyNodePhase(elapsed, node.Order, scene.Movement.NodePhaseDelayMs, perMonitor);
        if (!SyncTiming.ShouldDrawAnimation(e)) return false;

        var eff = perMonitor
            ? new NodeLayout { Id = node.Id, OffsetX = 0, OffsetY = 0, Width = node.Width, Height = node.Height, CanvasWidth = node.Width, CanvasHeight = node.Height, Order = node.Order }
            : node;
        var (animW, animH) = SpriteSizeFor(scene, node);
        if (animW <= 0 || animH <= 0) return false;

        var (vx, vy) = MovementCalculator.Calculate(scene.Movement, e, animW, animH, eff.CanvasWidth, eff.CanvasHeight,
            tileAlignStepX: 0f, canvasWraps: eff.Wraps);
        float ly = NodeMapping.ToLocalY(vy, eff);

        // ThreeZone corridor clamp (as the player does)
        var bg = scene.Background;
        if (bg.Mode == BackgroundMode.ThreeZone && bg.CorridorHeightPx > 0)
        {
            float minY = bg.CorridorTopPx, maxY = bg.CorridorTopPx + bg.CorridorHeightPx - animH;
            if (maxY < minY) maxY = minY;
            ly = Math.Clamp(ly, minY, maxY);
        }

        var grading = scene.Animation.ColorGrading;
        bool traveling = grading != null && grading.Mode is ColorGradingMode.TravelingRainbow or ColorGradingMode.TravelingList or ColorGradingMode.TravelingRandom;
        Color? tint = null;
        if (grading != null && grading.Mode != ColorGradingMode.None && !traveling)
        {
            var (r, g, b) = ColorGrader.ComputeCurrentColor(grading, e);
            tint = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        bool hit = false;
        if (scene.Animation.Pattern != null)
        {
            var cells = PatternLayout.Compute(scene.Animation.Pattern, vx, ly, animW, animH,
                eff.CanvasWidth, eff.CanvasHeight, eff.OffsetX, eff.Width, eff.Height, 1);
            int stride = Math.Max(1, cells.Count / MaxPreviewCellsPerNode);
            double cw = Math.Max(2, animW * s), ch = Math.Max(2, animH * s);
            for (int i = 0; i < cells.Count; i += stride)
            {
                var c = cells[i];
                float cx = NodeMapping.MirrorLocalX(c.ScreenX, animW, eff);
                if (!NodeMapping.IsVisible(cx, animW, eff) || c.ScreenY + animH < 0 || c.ScreenY > eff.Height) continue;
                IBrush brush;
                if (traveling && grading != null)
                {
                    var rgb = ColorGrader.IsCellColored(grading, c.LogicalI, c.LogicalJ) ? ColorGrader.ComputeCellColor(grading, c.LogicalI, c.LogicalJ) : null;
                    brush = rgb.HasValue ? new SolidColorBrush(Color.FromRgb((byte)(rgb.Value.R * 255), (byte)(rgb.Value.G * 255), (byte)(rgb.Value.B * 255))) : MonoCellBrush;
                }
                else brush = tint.HasValue ? new SolidColorBrush(tint.Value) : MonoCellBrush;
                ctx.FillRectangle(brush, new Rect(lane.Rect.Left + cx * s, lane.Rect.Top + c.ScreenY * s, Math.Max(1, cw - 1), Math.Max(1, ch - 1)), 1);
            }
            return false;
        }

        Span<float> copies = stackalloc float[2];
        int n = NodeMapping.WrapCopies(vx, animW, eff, copies);
        var bmp = GetSpriteBitmap();
        for (int i = 0; i < n; i++)
        {
            float lx = NodeMapping.ToLocalX(copies[i], animW, eff);
            if (!NodeMapping.IsVisible(lx, animW, eff)) continue;
            var dest = new Rect(lane.Rect.Left + lx * s, lane.Rect.Top + ly * s, Math.Max(1, animW * s), Math.Max(1, animH * s));
            if (bmp != null)
            {
                ctx.DrawImage(bmp, new Rect(bmp.Size), dest);
                if (tint.HasValue)
                    ctx.FillRectangle(new SolidColorBrush(tint.Value, 0.45), dest);
            }
            else
            {
                ctx.FillRectangle(new SolidColorBrush(tint ?? Color.Parse("#E0E0E0")), dest, 2);
            }
            float center = lx + animW / 2f;
            if (center >= 0 && center < eff.Width) hit = true;
        }
        return hit;
    }

    private static string BuildInfoLine(CrossScreenConfig? scene, SeatMapLayoutResult layout, long elapsed, int animW, string? hotId, IReadOnlyDictionary<string, string>? labels)
    {
        var parts = new List<string> { $"t = {Math.Max(0, elapsed) / 1000.0:0.0} s" };
        if (scene != null)
        {
            int lap = PlaylistScheduler.ComputeLapMs(scene.Movement, layout.CanvasWidth, animW);
            if (lap > 0) parts.Add($"lap ≈ {lap / 1000.0:0} s");
            parts.Add(layout.IsPhysical
                ? $"{PhysicalUnits.PxToCm(layout.CanvasWidth, layout.RefPixelsPerCm) / 100f:0.0} m canvas (physical)" + (layout.Wraps ? " · ring" : "")
                : $"{layout.CanvasWidth:N0} px canvas" + (layout.Wraps ? " · ring" : ""));
            if (layout.IsPhysical && animW > 0)
                parts.Add($"sprite {PhysicalUnits.PxToCm(animW, layout.RefPixelsPerCm):0.0} cm wide");
            if (scene.Background.Mode == BackgroundMode.IconZone) parts.Add("IconZone path not previewed");
            if (hotId != null)
            {
                string name = labels != null && labels.TryGetValue(hotId, out var l) ? l : hotId;
                var node = layout.Get(hotId);
                parts.Add($"sprite on: {name}" + (node != null ? $" (#{node.Order + 1})" : ""));
            }
        }
        return string.Join("   ·   ", parts);
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush, double size)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush);
        ctx.DrawText(ft, at);
    }

    private static Color ParseHex(string? hex)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? Colors.Black : Color.Parse(hex); }
        catch { return Colors.Black; }
    }
}

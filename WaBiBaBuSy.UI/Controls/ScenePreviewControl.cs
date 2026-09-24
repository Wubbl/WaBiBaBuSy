// WaBiBaBuSy.UI/Controls/ScenePreviewControl.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>
/// Live preview of a cross-screen scene over the room's seat map: every node as a rectangle in its
/// lane, the scene painted by <see cref="ScenePainter"/> on a <see cref="SceneClock"/> (design clock by
/// default, live clock when <see cref="SharedStartUtcMs"/> &gt; 0).
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

    private readonly SceneClock _clock = new();
    private readonly ScenePainter _painter = new();
    private DispatcherTimer? _timer;

    private const double Pad = 10;
    private const double RowLabelH = 14;
    private const double LegendH = 16;

    private static readonly IPen NodePen = new Pen(new SolidColorBrush(Color.Parse("#3A3A3A")), 1);
    private static readonly IPen NodePenHot = new Pen(new SolidColorBrush(Color.Parse("#00CC66")), 1.5);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush SeamBrush = new SolidColorBrush(Color.Parse("#555555"));
    private static readonly Typeface Font = Typeface.Default;

    static ScenePreviewControl()
    {
        AffectsRender<ScenePreviewControl>(LayoutProperty, SceneProperty, LabelsProperty, SpriteImagePathProperty, SharedStartUtcMsProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ScenePreviewControl>(true);
    }

    /// <summary>Restart the design clock at t = 0 (call after every config change).</summary>
    public void RestartClock()
    {
        _clock.Restart();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClockSpeedProperty) _clock.Speed = ClockSpeed;
        else if (change.Property == IsPausedProperty) _clock.IsPaused = IsPaused;
        else if (change.Property == SpriteImagePathProperty) _painter.SpriteImagePath = SpriteImagePath;
        else if (change.Property == SharedStartUtcMsProperty) _clock.SharedStartUtcMs = SharedStartUtcMs;
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

        var scene = ScenePainter.Resolve(Scene, layout);
        long elapsed = _clock.ElapsedMs();
        string? hotNodeId = null;
        var (animW, _) = _painter.SpriteSizeFor(scene, lanes[0].Node);

        foreach (var lane in lanes)
        {
            _painter.PaintBackground(ctx, lane.Rect, s, scene);
            ctx.DrawRectangle(null, NodePen, lane.Rect);
        }

        if (ScenePainter.HasSprite(scene))
        {
            foreach (var lane in lanes)
                if (_painter.PaintSprite(ctx, lane.Rect, s, lane.Node, scene!, elapsed))
                    hotNodeId = lane.Node.Id;
        }

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

        DrawText(ctx, ScenePainter.BuildInfoLine(scene, layout, elapsed, animW, hotNodeId, labels), new Point(Pad, bounds.Height - LegendH - 1), InfoBrush, 10);
    }

    private static string RowCaption(int r, int rows, SeatMapLayoutResult layout)
    {
        if (rows == 1) return "";
        string dir = layout.Traversal == TraversalMode.Parallel ? "" : (r % 2 == 0 ? "  →" : "  ←");
        return $"Row {r + 1}{dir}";
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..Math.Max(1, max - 1)] + "…";

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush, double size)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush);
        ctx.DrawText(ft, at);
    }
}

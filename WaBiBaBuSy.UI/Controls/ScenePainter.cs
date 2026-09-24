// WaBiBaBuSy.UI/Controls/ScenePainter.cs
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>
/// Paints a cross-screen scene onto one node's rectangle with the very same pure functions the D2D
/// players run (<see cref="MovementCalculator"/>, <see cref="PatternLayout"/>, <see cref="ColorGrader"/>,
/// <see cref="NodeMapping"/>, <see cref="SyncTiming"/>). Geometry + color only: GIF frame timing,
/// IconZone path-following and video are not simulated. Shared by <see cref="ScenePreviewControl"/>
/// and RoomView; holds a one-entry sprite bitmap cache.
/// </summary>
public sealed class ScenePainter
{
    private const int MaxPreviewCellsPerNode = 400;
    private static readonly IBrush MonoCellBrush = new SolidColorBrush(Color.Parse("#BBBBBB"));

    private string? _spritePath;
    private Bitmap? _bitmap;
    private bool _loaded;

    /// <summary>Image drawn for the sprite (the file for images/GIFs, a thumbnail for videos).</summary>
    public string? SpriteImagePath
    {
        get => _spritePath;
        set
        {
            if (value == _spritePath) return;
            _spritePath = value;
            _bitmap = null;
            _loaded = false;
        }
    }

    private Bitmap? SpriteBitmap
    {
        get
        {
            if (_loaded) return _bitmap;
            _loaded = true;
            try
            {
                if (!string.IsNullOrEmpty(_spritePath) && File.Exists(_spritePath))
                    _bitmap = new Bitmap(_spritePath);
            }
            catch
            {
                _bitmap = null;
            }
            return _bitmap;
        }
    }

    /// <summary>True when the scene has an animation file to draw.</summary>
    public static bool HasSprite(CrossScreenConfig? scene) => scene != null && !string.IsNullOrEmpty(scene.Animation.AnimationPath);

    /// <summary>Resolve cm → canvas px exactly like the apply path does, so preview and wall agree.</summary>
    public static CrossScreenConfig? Resolve(CrossScreenConfig? scene, SeatMapLayoutResult layout)
    {
        if (scene == null) return null;
        var (mv, an) = PhysicalUnits.ResolveForCanvas(scene, layout.RefPixelsPerCm);
        return new CrossScreenConfig
        {
            Background = scene.Background, Movement = mv, Animation = an,
            DistributionMode = scene.DistributionMode, SelectedMonitorIds = scene.SelectedMonitorIds,
            AnimationSpeedPxPerSecond = scene.AnimationSpeedPxPerSecond
        };
    }

    /// <summary>Background color (and ThreeZone bands) of the scene over the node rect.</summary>
    public void PaintBackground(DrawingContext ctx, Rect rect, double s, CrossScreenConfig? scene)
    {
        var bg = scene?.Background;
        var color = ParseHex(bg == null ? "#000000" :
            bg.Mode == BackgroundMode.IconZone ? bg.IconCorridorColorHex :
            bg.Mode == BackgroundMode.ThreeZone ? bg.CorridorColorHex : bg.ColorHex);
        ctx.FillRectangle(new SolidColorBrush(color), rect);
        if (bg?.Mode != BackgroundMode.ThreeZone) return;

        double top = rect.Top + bg.CorridorTopPx * s;
        double h = bg.CorridorHeightPx * s;
        ctx.FillRectangle(new SolidColorBrush(ParseHex(bg.TopZoneColorHex)),
            new Rect(rect.Left, rect.Top, rect.Width, Math.Max(0, top - rect.Top)));
        ctx.FillRectangle(new SolidColorBrush(ParseHex(bg.BottomZoneColorHex)),
            new Rect(rect.Left, Math.Min(rect.Bottom, top + h), rect.Width, Math.Max(0, rect.Bottom - (top + h))));
    }

    /// <summary>Sprite size after FitMode — mirrors the player's CalculateAnimationLayout for the given node.</summary>
    public (int W, int H) SpriteSizeFor(CrossScreenConfig? scene, NodeLayout node)
    {
        if (scene == null) return (0, 0);
        int nativeW = 400, nativeH = 300;
        var bmp = SpriteBitmap;
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

    /// <summary>Draws the scene's sprite/pattern on one node (clipped to <paramref name="rect"/>); true when the sprite's center is on this node.</summary>
    public bool PaintSprite(DrawingContext ctx, Rect rect, double s, NodeLayout node, CrossScreenConfig scene, long elapsedMs)
    {
        using var clip = ctx.PushClip(rect);
        bool perMonitor = scene.DistributionMode == AnimationDistributionMode.Simultaneous;
        long e = SyncTiming.ApplyNodePhase(elapsedMs, node.Order, scene.Movement.NodePhaseDelayMs, perMonitor);
        if (!SyncTiming.ShouldDrawAnimation(e)) return false;

        var eff = perMonitor
            ? new NodeLayout { Id = node.Id, OffsetX = 0, OffsetY = 0, Width = node.Width, Height = node.Height, CanvasWidth = node.Width, CanvasHeight = node.Height, Order = node.Order }
            : node;
        var (animW, animH) = SpriteSizeFor(scene, node);
        if (animW <= 0 || animH <= 0) return false;

        var (vx, vy) = MovementCalculator.Calculate(scene.Movement, e, animW, animH, eff.CanvasWidth, eff.CanvasHeight,
            tileAlignStepX: 0f, canvasWraps: eff.Wraps);
        float ly = NodeMapping.ToLocalY(vy, eff);

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
                ctx.FillRectangle(brush, new Rect(rect.Left + cx * s, rect.Top + c.ScreenY * s, Math.Max(1, cw - 1), Math.Max(1, ch - 1)), 1);
            }
            return false;
        }

        bool hit = false;
        Span<float> copies = stackalloc float[2];
        int n = NodeMapping.WrapCopies(vx, animW, eff, copies);
        var bmp = SpriteBitmap;
        for (int i = 0; i < n; i++)
        {
            float lx = NodeMapping.ToLocalX(copies[i], animW, eff);
            if (!NodeMapping.IsVisible(lx, animW, eff)) continue;
            var dest = new Rect(rect.Left + lx * s, rect.Top + ly * s, Math.Max(1, animW * s), Math.Max(1, animH * s));
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

    /// <summary>One-line legend: time, lap, canvas size, sprite owner.</summary>
    public static string BuildInfoLine(CrossScreenConfig? scene, SeatMapLayoutResult layout, long elapsed, int animW, string? hotId, IReadOnlyDictionary<string, string>? labels)
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

    /// <summary>Parse #RRGGBB / #AARRGGBB, black on null or invalid input.</summary>
    public static Color ParseHex(string? hex)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? Colors.Black : Color.Parse(hex); }
        catch { return Colors.Black; }
    }
}

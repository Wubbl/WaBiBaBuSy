// WaBiBaBuSy.UI/Controls/ScenePainter.cs
using System;
using System.Collections.Generic;
using System.Linq;
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
/// IconZone path-following and video are not simulated. Used by <see cref="RoomView"/> for every tile;
/// holds a one-entry sprite bitmap cache.
/// </summary>
public sealed partial class ScenePainter
{
    private const int MaxPreviewCellsPerNode = 400;
    private static readonly IBrush MonoCellBrush = new SolidColorBrush(Color.Parse("#BBBBBB"));

    // ponytail: the cache is dropped whole when full; per-scene eviction if big playlists make the preview stutter.
    // Sprites are kept downscaled (tiles are a few hundred px), so a 60-image layer fits without thrashing.
    private const int MaxCachedBitmaps = 256;
    private const int SpritePreviewMaxPx = 256;
    private readonly Dictionary<string, Sprite> _bitmaps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A decoded preview image and the source's native pixel size (what the players size it by).</summary>
    private readonly record struct Sprite(Bitmap? Bmp, PixelSize Native);

    /// <summary>
    /// Maps a layer's animation file to the image the preview draws (the file for images/GIFs, a thumbnail for
    /// videos, null = placeholder). Null resolver = draw the file itself.
    /// </summary>
    public Func<string?, string?>? SpriteImageResolver { get; set; }

    /// <summary>Forget every decoded image (e.g. after the gallery produced new video thumbnails).</summary>
    public void ClearBitmaps() => _bitmaps.Clear();

    private Bitmap? BitmapFor(string? animationPath) => SpriteFor(animationPath).Bmp;

    private Sprite SpriteFor(string? animationPath)
    {
        var image = SpriteImageResolver != null ? SpriteImageResolver(animationPath) : animationPath;
        if (string.IsNullOrEmpty(image)) return default;
        if (_bitmaps.TryGetValue(image, out var cached)) return cached;
        Sprite sprite = default;
        try
        {
            if (File.Exists(image))
            {
                var full = new Bitmap(image);
                var native = full.PixelSize;
                double k = (double)SpritePreviewMaxPx / Math.Max(native.Width, native.Height);
                if (k < 1)
                {
                    var small = full.CreateScaledBitmap(new PixelSize(Math.Max(1, (int)(native.Width * k)), Math.Max(1, (int)(native.Height * k))));
                    full.Dispose();   // never drawn, safe to free now
                    full = small;
                }
                sprite = new Sprite(full, native);
            }
        }
        catch
        {
            sprite = default;
        }
        if (_bitmaps.Count >= MaxCachedBitmaps) _bitmaps.Clear();
        _bitmaps[image] = sprite;
        return sprite;
    }

    // The preview only needs a few hundred px per tile: decode panoramas at 4096 px wide instead of 20000+.
    private const int PanoramaPreviewWidth = 4096;
    private const string PanoramaKeyPrefix = "panorama|";

    private Bitmap? PanoramaBitmap(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string key = PanoramaKeyPrefix + path;
        if (_bitmaps.TryGetValue(key, out var cached)) return cached.Bmp;
        Bitmap? bmp = null;
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                bmp = Bitmap.DecodeToWidth(stream, PanoramaPreviewWidth);
            }
        }
        catch
        {
            bmp = null;
        }
        if (_bitmaps.Count >= MaxCachedBitmaps) _bitmaps.Clear();
        _bitmaps[key] = new Sprite(bmp, bmp?.PixelSize ?? default);
        return bmp;
    }

    /// <summary>True when the scene has an enabled layer with an animation file to draw.</summary>
    public static bool HasSprite(CrossScreenConfig? scene) =>
        scene != null && scene.Layers.Any(l => l.Enabled && !string.IsNullOrEmpty(l.Animation.AnimationPath));

    /// <summary>Resolve cm → canvas px exactly like the apply path does, so preview and wall agree.</summary>
    public static CrossScreenConfig? Resolve(CrossScreenConfig? scene, SeatMapLayoutResult layout) =>
        scene == null ? null : PhysicalUnits.ResolveScene(scene, layout.RefPixelsPerCm);

    /// <summary>True when the scene has something to paint: a sprite, or a panorama background.</summary>
    public static bool HasContent(CrossScreenConfig? scene) =>
        HasSprite(scene) || (scene?.Background is { Mode: BackgroundMode.Panorama } bg && !string.IsNullOrEmpty(bg.ImagePath));

    /// <summary>Background color (ThreeZone bands, this node's panorama slice) of the scene over the node rect.</summary>
    public void PaintBackground(DrawingContext ctx, Rect rect, double s, CrossScreenConfig? scene, NodeLayout? node = null)
    {
        var bg = scene?.Background;
        var color = ParseHex(bg == null ? "#000000" :
            bg.Mode == BackgroundMode.IconZone ? bg.IconCorridorColorHex :
            bg.Mode == BackgroundMode.ThreeZone ? bg.CorridorColorHex : bg.ColorHex);
        ctx.FillRectangle(new SolidColorBrush(color), rect);
        if (bg?.Mode == BackgroundMode.Panorama && PanoramaBitmap(bg.ImagePath) is { } pano)
        {
            var ((px, py, pw, ph), dst) = NodeMapping.PanoramaSource(node, pano.PixelSize.Width, pano.PixelSize.Height, bg.PanoramaFit);
            if (pw <= 0 || ph <= 0) return;   // the image misses this screen
            using var flip = node?.Mirrored == true
                ? ctx.PushTransform(Matrix.CreateTranslation(-rect.Center.X, 0) * Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(rect.Center.X, 0))
                : default(DrawingContext.PushedState?);
            var to = dst is { } d ? new Rect(rect.X + d.X * s, rect.Y + d.Y * s, d.Width * s, d.Height * s) : rect;
            ctx.DrawImage(pano, new Rect(px, py, pw, ph), to);
            return;
        }
        if (bg?.Mode != BackgroundMode.ThreeZone) return;

        double top = rect.Top + bg.CorridorTopPx * s;
        double h = bg.CorridorHeightPx * s;
        ctx.FillRectangle(new SolidColorBrush(ParseHex(bg.TopZoneColorHex)),
            new Rect(rect.Left, rect.Top, rect.Width, Math.Max(0, top - rect.Top)));
        ctx.FillRectangle(new SolidColorBrush(ParseHex(bg.BottomZoneColorHex)),
            new Rect(rect.Left, Math.Min(rect.Bottom, top + h), rect.Width, Math.Max(0, rect.Bottom - (top + h))));
    }

    /// <summary>Sprite size after FitMode — mirrors the player's CalculateAnimationLayout for the given node.</summary>
    public (int W, int H) SpriteSizeFor(SceneLayer? layer, NodeLayout node)
    {
        if (layer == null) return (0, 0);
        var anim = layer.Animation;
        int nativeW = 400, nativeH = 300;
        var native = SpriteFor(anim.AnimationPath).Native;
        if (native.Width > 0 && native.Height > 0)
        {
            nativeW = native.Width; nativeH = native.Height;
        }
        else if (anim.TargetHeight > 0)
        {
            nativeH = anim.TargetHeight; nativeW = (int)(nativeH * 16 / 9.0);
        }
        switch (anim.FitMode)
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
                int th = Math.Max(1, anim.TargetHeight);
                return (Math.Max(1, (int)Math.Round(nativeW * (double)th / nativeH)), th);
            }
            default:
                return (nativeW, nativeH);
        }
    }

    /// <summary>
    /// The layout a node's player uses for a layer: the seat-map node for a Sequential layer; for a Simultaneous one
    /// a per-monitor layout (offset 0, its own canvas, not mirrored, no wrap) — what the player builds itself.
    /// Show events follow the bottom layer (Sequential when the scene has none), as on the players.
    /// </summary>
    public static NodeLayout EffectiveNode(SceneLayer? layer, NodeLayout node) =>
        layer?.Distribution == AnimationDistributionMode.Simultaneous
            ? new NodeLayout { Id = node.Id, OffsetX = 0, OffsetY = 0, Width = node.Width, Height = node.Height, CanvasWidth = node.Width, CanvasHeight = node.Height, Order = node.Order }
            : node;

    /// <summary>
    /// Draws every enabled layer bottom to top on one node (clipped to <paramref name="rect"/>); true when the
    /// lowest drawn layer's sprite center is on this node.
    /// </summary>
    public bool PaintLayers(DrawingContext ctx, Rect rect, double s, NodeLayout node, CrossScreenConfig scene, long elapsedMs)
    {
        using var clip = ctx.PushClip(rect);
        bool? hit = null;
        for (int i = 0; i < scene.Layers.Count; i++)
        {
            var layer = scene.Layers[i].ForSeat(node.Order);   // SourcePerSeat: this seat's own source, as its player
            if (!layer.Enabled || string.IsNullOrEmpty(layer.Animation.AnimationPath)) continue;
            float shown = SyncTiming.LayerElapsed(elapsedMs, node.Order, layer) is long le
                ? LayerSchedule.Visibility(layer.ShowEveryMs, layer.ShowForMs, le) : 1f;   // spawn window, as the players
            if (shown <= 0f) continue;
            float opacity = Math.Clamp(layer.Opacity, 0f, 1f) * shown;
            using var alpha = opacity < 0.999f ? ctx.PushOpacity(opacity) : default(DrawingContext.PushedState?);
            bool h = PaintLayer(ctx, rect, s, node, layer, scene.Background, followsBackground: i == 0, elapsedMs);
            hit ??= h;
        }
        return hit == true;
    }

    /// <summary>One layer's sprite/pattern; true when its sprite's center is on this node.</summary>
    private bool PaintLayer(DrawingContext ctx, Rect rect, double s, NodeLayout node, SceneLayer layer,
        BackgroundLayerConfig background, bool followsBackground, long elapsedMs)
    {
        if (SyncTiming.LayerElapsed(elapsedMs, node.Order, layer) is not long e) return false;
        var anim = layer.Animation;
        var movement = layer.Movement;

        var eff = EffectiveNode(layer, node);
        var (animW, animH) = SpriteSizeFor(layer, node);
        if (animW <= 0 || animH <= 0) return false;

        var grading = anim.ColorGrading;
        bool traveling = grading != null && grading.Mode is ColorGradingMode.TravelingRainbow or ColorGradingMode.TravelingList or ColorGradingMode.TravelingRandom;

        // Same position math as the players for Traveling + pattern: whole-cell loop snap, and in Endless mode the
        // phase + cell offset split (review 2026-10-13 E10). The preview has one source, so the step is animW + spacing.
        float tileStep = traveling && anim.Pattern != null ? animW + anim.Pattern.SpacingX : 0f;
        int cellOffsetI = 0;
        float vx, vy;
        if (MovementCalculator.EndlessTravelingScroll(movement, e, tileStep) is { } endless)
        {
            vx = endless.X;
            vy = movement.StartY ?? (eff.CanvasHeight - animH) / 2f;
            cellOffsetI = endless.CellOffsetI;
        }
        else
        {
            (vx, vy) = MovementCalculator.Calculate(movement, e, animW, animH, eff.CanvasWidth, eff.CanvasHeight,
                tileAlignStepX: tileStep, canvasWraps: eff.Wraps);
        }
        float ly = NodeMapping.ToLocalY(vy, eff);

        // Only the bottom layer follows the background's corridor / IconZone path (as on the players).
        var bg = followsBackground ? background : new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor };
        ly = ClampCorridor(ly, bg, animH);
        Color? tint = null;
        if (grading != null && grading.Mode != ColorGradingMode.None && !traveling)
        {
            var (r, g, b) = ColorGrader.ComputeCurrentColor(grading, e);
            tint = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        // Every source image of the layer, decoded once (player order: primary first, then the additional ones).
        var paths = anim.GetAllAnimationPaths();
        var sources = new Sprite[Math.Max(1, paths.Count)];
        for (int i = 0; i < paths.Count; i++) sources[i] = SpriteFor(paths[i]);

        if (anim.Pattern != null)
        {
            // Cell slot as on the players: the widest source scaled to the sprite height of the tallest one.
            int maxNativeH = sources.Max(src => src.Native.Height);
            int cellW = animW;
            if (maxNativeH > 0)
            {
                int widest = sources.Max(src => (int)(src.Native.Width * ((float)animH / maxNativeH)));
                if (widest > 0) cellW = widest;
            }
            var cells = PatternLayout.Compute(anim.Pattern, vx, ly, cellW, animH,
                eff.CanvasWidth, eff.CanvasHeight, eff.OffsetX, eff.Width, eff.Height, sources.Length, cellLogicalOffsetI: cellOffsetI);
            int stride = Math.Max(1, cells.Count / MaxPreviewCellsPerNode);
            for (int i = 0; i < cells.Count; i += stride)
            {
                var c = cells[i];
                var src = sources[c.SourceIndex % sources.Length];
                int drawW = src.Native.Height > 0 ? (int)(src.Native.Width * ((float)animH / src.Native.Height)) : animW;
                float cx = NodeMapping.MirrorLocalX(c.ScreenX, drawW, eff);
                if (!NodeMapping.IsVisible(cx, drawW, eff) || c.ScreenY + animH < 0 || c.ScreenY > eff.Height) continue;
                Color? cellTint = tint;
                if (traveling && grading != null)
                {
                    var rgb = ColorGrader.IsCellColored(grading, c.LogicalI, c.LogicalJ) ? ColorGrader.ComputeCellColor(grading, c.LogicalI, c.LogicalJ) : null;
                    cellTint = rgb.HasValue ? Color.FromRgb((byte)(rgb.Value.R * 255), (byte)(rgb.Value.G * 255), (byte)(rgb.Value.B * 255)) : null;
                }
                if (src.Bmp != null)
                    DrawSprite(ctx, rect, s, src.Bmp, cx, c.ScreenY, drawW, animH, 1f, c.RotationDeg, 1.0, cellTint);
                else
                    ctx.FillRectangle(cellTint.HasValue ? new SolidColorBrush(cellTint.Value) : MonoCellBrush,
                        new Rect(rect.Left + cx * s, rect.Top + c.ScreenY * s, Math.Max(1, drawW * s - 1), Math.Max(1, animH * s - 1)), 1);
            }
            return false;
        }

        // Several images without a pattern: each at its seeded offset around the anchor, sized to TargetHeight
        // (the players' multi-image branch: no wrap copies, no effects).
        if (sources.Length > 1)
        {
            float ax = NodeMapping.ToLocalX(vx, animW, eff);
            int seed = anim.Pattern?.Seed ?? 0;
            for (int i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                var (dx, dy, _) = PatternLayout.MultiImageOffset(seed, i, anim.MultiImageSpread, anim.MultiImagePhaseJitterMs);
                int h = anim.TargetHeight;
                int w = src.Native.Height > 0 ? (int)(src.Native.Width * ((float)h / src.Native.Height)) : animW;
                if (h <= 0) (w, h) = (animW, animH);
                DrawSprite(ctx, rect, s, src.Bmp, ax + dx, ly + dy, w, h, 1f, 0f, 1.0, tint);
            }
            float center = ax + animW / 2f;
            return center >= 0 && center < eff.Width;
        }

        bool hit = false;
        Span<float> copies = stackalloc float[2];
        var bmp = sources[0].Bmp;

        // Roadmap 2.4 effects, with the players' pure math (SpriteEffects).
        var fx = SpriteEffects.Supported(anim, movement, bg.Mode) ? anim.Effects : null;
        var input = new SpritePoseInput(movement, animW, animH, eff.CanvasWidth, eff.CanvasHeight, 0f, eff);

        // Trail first (farthest ghost first), then the sprite on top.
        if (fx != null)
        {
            int count = SpriteEffects.TrailCount(fx.Trail);
            for (int k = count; k >= 1; k--)
            {
                var ghost = SpriteEffects.Ghost(fx.Trail!, k);
                long t = e - ghost.OffsetMs;
                if (t < 0 || ghost.Opacity <= 0.005f) continue;
                var (gx, gy) = SpriteEffects.Pose(input, t);
                float gly = ClampCorridor(NodeMapping.ToLocalY(gy, eff), bg, animH);
                float gScale = SpriteEffects.PulseScale(fx.Pulse, t) * ghost.Scale;
                // Like the players: with a time-based color mode each ghost keeps the color it had at its moment.
                var gTint = tint;
                if (tint.HasValue && fx.Trail!.UseGradingColor && grading != null)
                {
                    var (gr, gg, gb) = ColorGrader.ComputeCurrentColor(grading, t);
                    gTint = Color.FromRgb((byte)(gr * 255), (byte)(gg * 255), (byte)(gb * 255));
                }
                float gRot = SpriteEffects.SpinDegrees(fx.SpinDegPerSecond, t);
                int gn = NodeMapping.WrapCopies(gx, animW, eff, copies);
                for (int i = 0; i < gn; i++)
                {
                    float glx = NodeMapping.ToLocalX(copies[i], animW, eff);
                    if (!NodeMapping.IsVisible(glx, animW, eff)) continue;
                    DrawSprite(ctx, rect, s, bmp, glx, gly, animW, animH, gScale, gRot, ghost.Opacity, gTint);
                }
            }
        }

        float spriteScale = fx != null ? SpriteEffects.PulseScale(fx.Pulse, e) : 1f;
        float spriteRot = fx != null ? SpriteEffects.SpinDegrees(fx.SpinDegPerSecond, e) : 0f;
        int n = NodeMapping.WrapCopies(vx, animW, eff, copies);
        for (int i = 0; i < n; i++)
        {
            float lx = NodeMapping.ToLocalX(copies[i], animW, eff);
            if (!NodeMapping.IsVisible(lx, animW, eff)) continue;
            DrawSprite(ctx, rect, s, bmp, lx, ly, animW, animH, spriteScale, spriteRot, 1.0, tint);
            float center = lx + animW / 2f;
            if (center >= 0 && center < eff.Width) hit = true;
        }

        if (fx != null && SpriteEffects.FindPortal(input, fx, e) is { } portal && portal.RadiusY >= 0.5f)
        {
            var (pr, pg, pb) = SpriteEffects.ParseBezelColor(fx.BezelColorHex);
            var pc = Color.FromRgb(pr, pg, pb);
            var center = new Point(rect.Left + portal.X * s, rect.Top + portal.Y * s);
            ctx.DrawEllipse(new SolidColorBrush(pc, 0.28 * portal.Openness),
                new Pen(new SolidColorBrush(pc, portal.Openness), Math.Max(1, (3 + 5 * portal.Openness) * s)),
                center, portal.RadiusX * s, portal.RadiusY * s);
        }
        if (fx != null && SpriteEffects.FindBezelEvent(input, fx, e) is { } ev)
        {
            var (cr, cg, cb) = SpriteEffects.ParseBezelColor(fx.BezelColorHex);   // same fallback as the players
            PaintBezel(ctx, rect, s, SpriteEffects.Visual(ev, eff), Color.FromRgb(cr, cg, cb));
        }
        return hit;
    }

    /// <summary>The sprite (or its placeholder) at local canvas coordinates, scaled and rotated about its center.</summary>
    private static void DrawSprite(DrawingContext ctx, Rect rect, double s, Bitmap? bmp, float lx, float ly, int animW, int animH,
        float scale, float rotationDeg, double opacity, Color? tint)
    {
        var dest = new Rect(rect.Left + lx * s, rect.Top + ly * s, Math.Max(1, animW * s), Math.Max(1, animH * s));
        bool transformed = Math.Abs(scale - 1f) > 0.0005f || Math.Abs(rotationDeg) > 0.01f;
        using var transform = transformed
            ? ctx.PushTransform(Matrix.CreateTranslation(-dest.Center.X, -dest.Center.Y) * Matrix.CreateScale(scale, scale)
                * Matrix.CreateRotation(rotationDeg * Math.PI / 180.0) * Matrix.CreateTranslation(dest.Center.X, dest.Center.Y))
            : default(DrawingContext.PushedState?);
        using var alpha = opacity < 0.999 ? ctx.PushOpacity(opacity) : default(DrawingContext.PushedState?);
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
    }

    /// <summary>ThreeZone corridor clamp, as the players do it.</summary>
    private static float ClampCorridor(float ly, BackgroundLayerConfig bg, int animH)
    {
        if (bg.Mode != BackgroundMode.ThreeZone || bg.CorridorHeightPx <= 0) return ly;
        float minY = bg.CorridorTopPx, maxY = bg.CorridorTopPx + bg.CorridorHeightPx - animH;
        if (maxY < minY) maxY = minY;
        return Math.Clamp(ly, minY, maxY);
    }

    /// <summary>Glow strip, flash or ripple ring of a bezel event over the node rect.</summary>
    private static void PaintBezel(DrawingContext ctx, Rect rect, double s, BezelVisual v, Color color)
    {
        if (v.Opacity <= 0.005f) return;
        var area = new Rect(rect.Left + v.X * s, rect.Top + v.Y * s, v.Width * s, v.Height * s);
        switch (v.Effect)
        {
            case BezelEffect.Glow:
                var bright = Color.FromArgb((byte)(255 * v.Opacity), color.R, color.G, color.B);
                var clear = Color.FromArgb(0, color.R, color.G, color.B);
                var brush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(v.EdgeOnRight ? 1 : 0, 0.5, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(v.EdgeOnRight ? 0 : 1, 0.5, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(bright, 0), new GradientStop(clear, 1) },
                };
                ctx.FillRectangle(brush, area);
                break;
            case BezelEffect.Flash:
                ctx.FillRectangle(new SolidColorBrush(color, v.Opacity), area);
                break;
            case BezelEffect.Ripple:
                var pen = new Pen(new SolidColorBrush(color, v.Opacity), Math.Max(1, v.StrokeWidth * s));
                ctx.DrawEllipse(null, pen, new Point(rect.Left + v.X * s, rect.Top + v.Y * s), v.Radius * s, v.Radius * s);
                break;
        }
    }

    /// <summary>One-line legend: time, lap, canvas size, sprite owner.</summary>
    public static string BuildInfoLine(CrossScreenConfig? scene, SeatMapLayoutResult layout, long elapsed, int animW, string? hotId, IReadOnlyDictionary<string, string>? labels)
    {
        var parts = new List<string> { $"t = {Math.Max(0, elapsed) / 1000.0:0.0} s" };
        if (scene?.FirstLayer is { } bottom)
        {
            int lap = PlaylistScheduler.ComputeLapMs(bottom.Movement, layout.CanvasWidth, animW, speedPxOverride: 0f, canvasWraps: layout.Wraps);
            if (lap > 0) parts.Add($"lap ≈ {lap / 1000.0:0} s");
            parts.Add(layout.IsPhysical
                ? $"{PhysicalUnits.PxToCm(layout.CanvasWidth, layout.RefPixelsPerCm) / 100f:0.0} m canvas (physical)" + (layout.Wraps ? " · ring" : "")
                : $"{layout.CanvasWidth:N0} px canvas" + (layout.Wraps ? " · ring" : ""));
            if (layout.IsPhysical && animW > 0)
                parts.Add($"sprite {PhysicalUnits.PxToCm(animW, layout.RefPixelsPerCm):0.0} cm wide");
            if (MovementCalculator.FollowsIconPath(scene.Background.Mode, bottom.Movement)) parts.Add("IconZone path not previewed");
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

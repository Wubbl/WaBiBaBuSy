using ImageMagick;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using WaBiBaBuSy.Client.Linux.Platform;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;
using WaBiBaBuSy.WallpaperEngine.Desktop;

namespace WaBiBaBuSy.Client.Linux.Rendering;

/// <summary>
/// One loaded scene on one monitor: the background and the sprite layers, bottom first. Port of the Windows player's
/// HandleLoadAnimationCommand / InitializeBackground / DrawBackground / DrawNativeGifScene. Immutable after
/// <see cref="Load"/> apart from the layers' movement state; the owning <see cref="WallpaperScene"/> serializes access.
/// </summary>
internal sealed class SceneContent : IDisposable
{
    private readonly ILogger _logger;
    private readonly int _width, _height;          // monitor, device pixels
    private readonly List<SpriteLayer> _layers = new();

    public NodeLayout? SequentialLayout { get; }

    // ── Background ──
    public BackgroundMode BackgroundMode { get; private set; } = BackgroundMode.SolidColor;
    private SKColor _backgroundColor = SKColors.Black;
    private SKImage? _backgroundImage;
    private SKRect? _panoramaDest;
    public int CorridorTopPx { get; private set; }
    public int CorridorHeightPx { get; private set; }
    private SKColor _topZoneColor, _bottomZoneColor, _corridorColor;
    private SKColor _iconCorridorBgColor = new(0x1E, 0x1E, 0x1E);
    private readonly List<(float Y, float Height, float X, float Width, bool IsFree, SKColor Color)> _iconZoneBands = new();
    public List<(float X, float Y)> AnimPath { get; } = new();
    public float AnimPathLength { get; private set; }
    public bool MaskZones { get; }
    private readonly bool _useZonePalette;

    private readonly SKPaint _paint = new() { IsAntialias = true };
    private readonly SKSamplingOptions _sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private SceneContent(int width, int height, PlayerCommandLoadAnimation cmd, ILogger logger)
    {
        _width = width;
        _height = height;
        _logger = logger;
        SequentialLayout = cmd.Layout;
        MaskZones = cmd.MaskZones;
        _useZonePalette = cmd.UseZonePalette;
    }

    /// <summary>Layer 0 — the layer whose layout show events, the agenda and BiBaBo hit-testing use.</summary>
    public SpriteLayer BottomLayer => _layers[0];

    /// <summary>The layout show events are drawn on (layer 0's canvas, like the Windows player's resting <c>_layout</c>).</summary>
    public NodeLayout EventLayout => BottomLayer.Layout;

    public IReadOnlyList<SpriteLayer> Layers => _layers;

    /// <summary>Decode everything (slow, any thread) and build the scene.</summary>
    public static SceneContent Load(int width, int height, PlayerCommandLoadAnimation cmd, ILogger logger)
    {
        var scene = new SceneContent(width, height, cmd, logger);
        logger.LogInformation("[LOAD] {Count} layer(s), bottom {Path}, bg={Bg}", cmd.Layers.Count,
            cmd.Layers.Count > 0 ? cmd.Layers[0].Animation.AnimationPath : "(none)", cmd.BackgroundConfig.Mode);

        // Background-only scene: an empty, disabled layer 0 keeps every "layer 0" path valid (as in the D2D player)
        var layers = cmd.Layers.Count > 0 ? cmd.Layers : new List<SceneLayer> { new() { Enabled = false } };
        for (int i = 0; i < layers.Count; i++)
        {
            var config = layers[i].ForSeat(cmd.Layout?.Order ?? 0);   // SourcePerSeat: this seat's own source only
            var layout = scene.LayoutFor(config.Distribution);
            bool perMonitor = config.Distribution == AnimationDistributionMode.Simultaneous;
            scene._layers.Add(SpriteLayer.Load(scene, config, isBottom: i == 0, layout, perMonitor, logger));
            if (i == 0) scene.InitializeBackground(cmd.BackgroundConfig);   // IconZone pads by layer 0's height
        }
        var l0 = scene.EventLayout;
        logger.LogInformation("[LOAD] Layout: offset=({OX},{OY}) canvas={CW}x{CH} slice={SW}x{SH} scale={S:F3} mirrored={M} wraps={W} order={O}",
            l0.OffsetX, l0.OffsetY, l0.CanvasWidth, l0.CanvasHeight, l0.Width, l0.Height, l0.Scale, l0.Mirrored, l0.Wraps, l0.Order);
        return scene;
    }

    /// <summary>
    /// The canvas a layer plays on (port of LayoutFor). Sequential: the seat-map layout. Simultaneous (or no layout):
    /// this monitor alone — offset 0, its own size, not mirrored, no wrap, scale 1.
    /// </summary>
    private NodeLayout LayoutFor(AnimationDistributionMode distribution)
    {
        NodeLayout layout;
        if (distribution == AnimationDistributionMode.Sequential && SequentialLayout != null)
        {
            layout = SequentialLayout;
        }
        else
        {
            layout = new NodeLayout
            {
                Id = SequentialLayout?.Id ?? string.Empty,   // targeted show events match on it
                OffsetX = 0, OffsetY = 0, Width = _width, Height = _height,
                CanvasWidth = _width, CanvasHeight = _height,
                Mirrored = false, Wraps = false, Order = SequentialLayout?.Order ?? 0, Scale = 1f
            };
        }
        // Normalize missing sizes (ApplyLayout)
        if (layout.Width <= 0) layout.Width = _width;
        if (layout.Height <= 0) layout.Height = _height;
        if (layout.Scale <= 0f) layout.Scale = 1f;
        return layout;
    }

    // ════════════════════════════════════════════════════════════════════
    // Background (port of InitializeBackground)
    // ════════════════════════════════════════════════════════════════════

    private void InitializeBackground(BackgroundLayerConfig config)
    {
        BackgroundMode = config.Mode;
        switch (config.Mode)
        {
            case BackgroundMode.SolidColor:
                _backgroundColor = ParseHexColor(config.ColorHex);
                break;

            case BackgroundMode.StretchedImage:
            case BackgroundMode.TiledImage:
            case BackgroundMode.Panorama:
            {
                bool local = config.Mode == BackgroundMode.StretchedImage && config.UseLocalDesktopWallpaper;
                var desktop = local ? LocalWallpaper.Find(_logger) : null;
                if (local && desktop?.ImagePath == null && desktop?.ColorHex is { } desktopColor)
                {
                    // No wallpaper picture: the desktop's plain colour
                    BackgroundMode = BackgroundMode.SolidColor;
                    _backgroundColor = ParseHexColor(desktopColor);
                    break;
                }
                var imagePath = desktop?.ImagePath ?? config.ImagePath;
                _backgroundColor = ParseHexColor(config.ColorHex);   // panorama: around a centred image
                if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                {
                    _logger.LogWarning("[BG] Image not found, falling back to solid color: {Path}", imagePath);
                    BackgroundMode = BackgroundMode.SolidColor;
                    break;
                }
                try
                {
                    using var image = new MagickImage(imagePath);
                    // Panorama: cut this node's slice on the CPU, so only a monitor-sized image is kept
                    bool onScreen = config.Mode != BackgroundMode.Panorama || CropPanorama(image, config.PanoramaFit);
                    if (local) CropToFill(image);
                    if (onScreen) _backgroundImage = AnimationSource.ToSkImage(image);
                    _logger.LogInformation("[BG] Image loaded: {Path} ({W}x{H})", imagePath, image.Width, image.Height);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[BG] Failed to load background image {Path}", imagePath);
                    BackgroundMode = BackgroundMode.SolidColor;
                }
                break;
            }

            case BackgroundMode.ThreeZone:
                _topZoneColor = ParseHexColor(config.TopZoneColorHex);
                _bottomZoneColor = ParseHexColor(config.BottomZoneColorHex);
                _corridorColor = ParseHexColor(config.CorridorColorHex);
                CorridorTopPx = config.CorridorTopPx;
                CorridorHeightPx = config.CorridorHeightPx;
                break;

            case BackgroundMode.IconZone:
            {
                _iconCorridorBgColor = ParseHexColor(config.IconCorridorColorHex);
                var bottom = BottomLayer;
                var layout = bottom.Layout;
                // Linux: desktop icon positions are not readable (GNOME has none by default, KDE's are private to
                // plasmashell) → plan with an empty icon set.
                var zones = ZonePlanner.Compute(
                    Array.Empty<(int X, int Y)>(), 0, 0, layout.Width, layout.Height,
                    config.IconZonePaletteHexes, config.IconCorridorColorHex,
                    paddingPx: bottom.PathPaddingPx(MaskZones),
                    visualPaddingPx: 25);
                float s = layout.Scale > 0 ? layout.Scale : 1f;
                foreach (var band in zones.Bands)   // drawn in device px
                    _iconZoneBands.Add((band.Y * s, band.Height * s, band.X * s, band.Width < 0 ? band.Width : band.Width * s, band.IsFree, ParseHexColor(band.ColorHex)));

                // Sequential mode: the server sends one global path for the whole wall
                if (bottom.PrecomputedPath is { Count: > 0 } precomputed)
                    foreach (var wp in precomputed) AnimPath.Add((wp.X, wp.Y));
                else
                    foreach (var wp in zones.Path) AnimPath.Add((wp.X + layout.OffsetX, wp.Y));
                AnimPathLength = PathLength(AnimPath);
                _logger.LogInformation("[BG] IconZone (no icons on Linux): {Pts} path points, len={Len:F0}", AnimPath.Count, AnimPathLength);
                break;
            }
        }
    }

    /// <summary>The centred part with this monitor's aspect (Windows' "Fill").</summary>
    private void CropToFill(MagickImage image)
    {
        double target = (double)Math.Max(1, _width) / Math.Max(1, _height);
        int w = (int)image.Width, h = (int)image.Height;
        if ((double)w / h > target) w = Math.Max(1, (int)(h * target)); else h = Math.Max(1, (int)(w / target));
        image.Crop(new MagickGeometry(((int)image.Width - w) / 2, ((int)image.Height - h) / 2, (uint)w, (uint)h));
        image.ResetPage();
    }

    /// <summary>
    /// Crop to this node's panorama slice (<see cref="NodeMapping.PanoramaSource"/>), flipped on a mirrored node.
    /// False = the image does not reach this screen (background colour only).
    /// </summary>
    private bool CropPanorama(MagickImage image, PanoramaFit fit)
    {
        var layout = SequentialLayout;
        var ((x, y, w, h), dst) = NodeMapping.PanoramaSource(layout, (int)image.Width, (int)image.Height, fit);
        if (w <= 0 || h <= 0) return false;
        bool mirrored = layout?.Mirrored == true;
        if (dst is { } d && layout != null)
        {
            float kx = _width / (float)layout.Width, ky = _height / (float)layout.Height;   // layout px → device px
            _panoramaDest = SKRect.Create((mirrored ? layout.Width - d.X - d.Width : d.X) * kx, d.Y * ky, d.Width * kx, d.Height * ky);
        }
        image.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
        image.ResetPage();
        if (mirrored) image.Flop();
        return true;
    }

    // ════════════════════════════════════════════════════════════════════
    // Rendering
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Background, then every enabled layer at its own clock (<see cref="SyncTiming.LayerElapsed"/>, which also covers
    /// layer 0's Wave phase and a future shared start). <paramref name="rawElapsedMs"/> = now − shared start.
    /// </summary>
    public void Render(SKCanvas canvas, long rawElapsedMs)
    {
        DrawBackground(canvas);
        foreach (var layer in _layers)
        {
            if (!layer.Config.Enabled || !layer.HasContent) continue;
            if (SyncTiming.LayerElapsed(rawElapsedMs, layer.NodeOrder, layer.Config) is not long t) continue;
            layer.Update(t);
            layer.Draw(canvas, t);
        }
    }

    private void DrawBackground(SKCanvas canvas)
    {
        switch (BackgroundMode)
        {
            case BackgroundMode.SolidColor:
                canvas.Clear(_backgroundColor);
                break;

            case BackgroundMode.StretchedImage:
            case BackgroundMode.Panorama:   // the image already is this node's slice
            {
                bool pano = BackgroundMode == BackgroundMode.Panorama;
                canvas.Clear(pano ? _backgroundColor : SKColors.Black);
                if (_backgroundImage != null)
                {
                    var dest = pano && _panoramaDest is { } d ? d : SKRect.Create(0, 0, _width, _height);
                    canvas.DrawImage(_backgroundImage, dest, _sampling, _paint);
                }
                break;
            }

            case BackgroundMode.TiledImage:
                canvas.Clear(SKColors.Black);
                if (_backgroundImage is { Width: > 0, Height: > 0 } tile)
                    for (int ty = 0; ty < _height; ty += tile.Height)
                        for (int tx = 0; tx < _width; tx += tile.Width)
                            canvas.DrawImage(tile, SKRect.Create(tx, ty, tile.Width, tile.Height), _sampling, _paint);
                break;

            case BackgroundMode.ThreeZone:
            {
                canvas.Clear(SKColors.Black);
                using var paint = new SKPaint();
                if (CorridorTopPx > 0)
                {
                    paint.Color = _topZoneColor;
                    canvas.DrawRect(SKRect.Create(0, 0, _width, CorridorTopPx), paint);
                }
                paint.Color = _corridorColor;
                canvas.DrawRect(SKRect.Create(0, CorridorTopPx, _width, CorridorHeightPx), paint);
                int bottomY = CorridorTopPx + CorridorHeightPx;
                if (bottomY < _height)
                {
                    paint.Color = _bottomZoneColor;
                    canvas.DrawRect(SKRect.Create(0, bottomY, _width, _height - bottomY), paint);
                }
                break;
            }

            case BackgroundMode.IconZone:
                canvas.Clear(_iconCorridorBgColor);
                if (!MaskZones && _useZonePalette)
                {
                    using var paint = new SKPaint { IsAntialias = true };
                    foreach (var (zY, zH, zX, zW, isFree, color) in _iconZoneBands)
                    {
                        if (isFree) continue;
                        paint.Color = color;
                        canvas.DrawRoundRect(SKRect.Create(zX, zY, zW < 0 ? _width : zW, zH), 8f, 8f, paint);
                    }
                }
                break;
        }
    }

    internal static float PathLength(List<(float X, float Y)> path)
    {
        float total = 0f;
        for (int i = 0; i < path.Count - 1; i++)
        {
            float dx = path[i + 1].X - path[i].X, dy = path[i + 1].Y - path[i].Y;
            total += MathF.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    internal static (float X, float Y) SamplePath(List<(float X, float Y)> path, float dist)
    {
        if (path.Count == 0) return (0f, 0f);
        if (path.Count == 1) return path[0];
        float remaining = dist;
        for (int i = 0; i < path.Count - 1; i++)
        {
            float dx = path[i + 1].X - path[i].X, dy = path[i + 1].Y - path[i].Y;
            float segLen = MathF.Sqrt(dx * dx + dy * dy);
            if (remaining <= segLen || i == path.Count - 2)
            {
                float t = segLen > 0f ? Math.Clamp(remaining / segLen, 0f, 1f) : 0f;
                return (path[i].X + dx * t, path[i].Y + dy * t);
            }
            remaining -= segLen;
        }
        return path[^1];
    }

    internal static SKColor ParseHexColor(string? hex)
    {
        hex = (hex ?? string.Empty).TrimStart('#');
        if (hex.Length == 6
            && byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return new SKColor(r, g, b);
        return SKColors.Black;
    }

    public void Dispose()
    {
        foreach (var l in _layers) l.Dispose();
        _layers.Clear();
        _backgroundImage?.Dispose();
        _backgroundImage = null;
        _paint.Dispose();
    }
}

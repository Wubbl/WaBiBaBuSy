using System.Diagnostics;
using ImageMagick;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;
using WaBiBaBuSy.WallpaperEngine.Desktop;

namespace WaBiBaBuSy.Client.Linux.Rendering;

/// <summary>
/// Skia port of the Windows D2D player (WaBiBaBuSy.Player.D2D/Program.cs) for one monitor.
///
/// Receives the same <see cref="PlayerCommandLoadAnimation"/> / <see cref="PlayerCommandStartAnimation"/>
/// metadata and computes every frame from <c>elapsedMs</c> alone using the shared deterministic
/// helpers in WaBiBaBuSy.Models (MovementCalculator, PatternLayout, ColorGrader) — so a Linux
/// monitor shows exactly what a Windows monitor at the same canvas offset would show.
///
/// Not yet ported (weekend MVP): video, desktop-icon detection (IconZone runs with no icons),
/// zone-fade masks, the F11 debug overlay.
///
/// Thread model: Load/Start/Stop may be called from any thread; Render runs on the render
/// thread. All state is guarded by <see cref="_sync"/>; decoding happens outside the lock.
/// </summary>
public sealed class WallpaperScene : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _sync = new();

    // Monitor (local) size in pixels
    private readonly int _width;
    private readonly int _height;

    // ── Loaded configuration ──
    private AnimationLayerConfig? _animationConfig;
    private BackgroundLayerConfig? _backgroundConfig;
    private MovementConfig? _movementConfig;
    private int _virtualCanvasWidth;
    private int _monitorOffsetX;
    private bool _maskZones;
    private bool _useZonePalette;

    // ── Content ──
    private readonly List<AnimationSource> _sources = new();
    private double _gifSpeedMultiplier = 1.0;
    private int _contentNativeWidth;
    private int _contentNativeHeight;
    private bool _hasContent;

    // ── Layout / position ──
    private int _animWidth;
    private int _animHeight;
    private float _animX;
    private float _animY;
    private bool _centerInitialPosition;
    private bool _rotateWithPath;
    private float _animRotationRad;
    private bool _isTravelingMode;
    private float _tileAlignStepX;
    private int _endlessCellOffsetI;

    // ── Background ──
    private BackgroundMode _backgroundMode = BackgroundMode.SolidColor;
    private SKColor _backgroundColor = SKColors.Black;
    private SKImage? _backgroundImage;
    private bool _hasCorridorConstraint;
    private int _corridorTopPx;
    private int _corridorHeightPx;
    private SKColor _topZoneColor, _bottomZoneColor, _corridorColor;
    private SKColor _iconCorridorBgColor = new(0x1E, 0x1E, 0x1E);
    private readonly List<(float Y, float Height, float X, float Width, bool IsFree, SKColor Color)> _iconZoneBands = new();
    private readonly List<(float X, float Y)> _animPath = new();
    private float _animPathTotalLength;
    private int _traverseCount;

    // ── Timing ──
    private bool _isPlaying;
    private int _pixelsPerSecond;
    private double _startUnixMs;            // elapsedMs = nowUnixMs - _startUnixMs

    // Precise "now": wall clock sampled once, advanced by Stopwatch (monotonic, sub-ms)
    private static readonly double s_anchorUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly long s_anchorTicks = Stopwatch.GetTimestamp();

    private readonly SKPaint _bitmapPaint = new() { IsAntialias = true };
    private readonly SKSamplingOptions _sampling = new(SKFilterMode.Linear, SKMipmapMode.None);
    private bool _disposed;

    public WallpaperScene(int width, int height, ILogger logger)
    {
        _width = width;
        _height = height;
        _logger = logger;
    }

    public int Width => _width;
    public int Height => _height;
    public bool IsPlaying { get { lock (_sync) return _isPlaying; } }

    internal static double NowUnixMs()
        => s_anchorUnixMs + (Stopwatch.GetTimestamp() - s_anchorTicks) * 1000.0 / Stopwatch.Frequency;

    // ════════════════════════════════════════════════════════════════════
    // Commands (mirror HandleLoadAnimationCommand / HandleStartAnimationCommand)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Load animation content + configuration. Decoding happens before taking the lock so the
    /// previous animation keeps rendering until the new one is ready.
    /// </summary>
    public void Load(PlayerCommandLoadAnimation cmd)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WallpaperScene));

        var animConfig = cmd.AnimationConfig;
        var filePath = animConfig.AnimationPath;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        _logger.LogInformation("[LOAD] {Path} (canvas={VCW}px offset={Off}px monitor={W}x{H})",
            filePath, cmd.VirtualCanvasWidth, cmd.MonitorOffsetX, _width, _height);

        // ── Decode outside the lock ──
        var newSources = new List<AnimationSource>();
        if (AnimationSource.IsVideo(ext))
        {
            _logger.LogWarning("[LOAD] Video ({Ext}) is not supported by the Linux client yet — rendering background only", ext);
        }
        else
        {
            try
            {
                newSources.Add(AnimationSource.Load(filePath, _logger));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LOAD] Failed to decode {Path}", filePath);
            }
        }

        if (newSources.Count > 0)
        {
            foreach (var path in animConfig.AdditionalAnimationPaths ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var addExt = Path.GetExtension(path).ToLowerInvariant();
                if (!AnimationSource.IsImage(addExt))
                {
                    // Same limitation as the Windows player: only static-image additional sources
                    _logger.LogWarning("[LOAD] Skipping non-image additional source: {Path}", path);
                    continue;
                }
                try { newSources.Add(AnimationSource.Load(path, _logger)); }
                catch (Exception ex) { _logger.LogError(ex, "[LOAD] Failed to load additional source {Path}", path); }
            }
        }

        SKImage? newBackgroundImage = null;
        var bg = cmd.BackgroundConfig;
        if ((bg.Mode == BackgroundMode.StretchedImage || bg.Mode == BackgroundMode.TiledImage)
            && !string.IsNullOrEmpty(bg.ImagePath) && File.Exists(bg.ImagePath))
        {
            try
            {
                using var img = new MagickImage(bg.ImagePath);
                newBackgroundImage = AnimationSource.ToSkImage(img);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BG] Failed to load background image {Path}", bg.ImagePath);
            }
        }

        // ── Swap in under the lock ──
        lock (_sync)
        {
            DisposeContent();

            _animationConfig = animConfig;
            _backgroundConfig = bg;
            _movementConfig = cmd.MovementConfig;
            _virtualCanvasWidth = cmd.VirtualCanvasWidth;
            _monitorOffsetX = cmd.MonitorOffsetX;
            _maskZones = cmd.MaskZones;
            _useZonePalette = cmd.UseZonePalette;
            _gifSpeedMultiplier = animConfig.SpeedMultiplier;

            _sources.AddRange(newSources);
            _hasContent = _sources.Count > 0;
            _contentNativeWidth = _hasContent ? _sources[0].Width : 0;
            _contentNativeHeight = _hasContent ? _sources[0].Height : 0;
            _backgroundImage = newBackgroundImage;

            // Layout first so _animHeight is set (IconZone path padding uses it)
            CalculateAnimationLayout(animConfig);
            InitializeBackground(bg);

            // Tile-align step for seamless Traveling-color loop snapping (mirrors the D2D player)
            var cgMode = animConfig.ColorGrading?.Mode ?? ColorGradingMode.None;
            _isTravelingMode = cgMode is ColorGradingMode.TravelingRainbow
                                      or ColorGradingMode.TravelingList
                                      or ColorGradingMode.TravelingRandom;
            _tileAlignStepX = _isTravelingMode && animConfig.Pattern != null
                ? ComputePatternCellWidth() + animConfig.Pattern.SpacingX
                : 0f;
        }

        _logger.LogInformation("[LOAD] Ready: {Sources} source(s), anim {W}x{H}, bg={Bg}",
            newSources.Count, _animWidth, _animHeight, bg.Mode);
    }

    public void Start(PlayerCommandStartAnimation cmd)
    {
        lock (_sync)
        {
            _pixelsPerSecond = cmd.PixelsPerSecond;

            // Shared UTC timestamp (> 0, already converted to this machine's clock by
            // WallpaperSyncClient) → all machines compute the same elapsedMs. 0 → start now.
            _startUnixMs = cmd.StartTimestampMs > 0 ? cmd.StartTimestampMs : NowUnixMs();
            _isPlaying = true;

            if (_animationConfig != null)
            {
                _animX = _centerInitialPosition || _pixelsPerSecond == 0
                    ? (_width - _animWidth) / 2f
                    : -_animWidth;
            }

            _logger.LogInformation("[START] ts={Ts}ms speed={Speed}px/s elapsedNow={Elapsed:F0}ms",
                cmd.StartTimestampMs, cmd.PixelsPerSecond, NowUnixMs() - _startUnixMs);
        }
    }

    public void Stop()
    {
        lock (_sync) _isPlaying = false;
    }

    // ════════════════════════════════════════════════════════════════════
    // Rendering
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Render the current frame (pixel coordinates, origin = monitor top-left).</summary>
    public void Render(SKCanvas canvas) => Render(canvas, NowUnixMs());

    /// <summary>Render the frame for an explicit wall-clock time (tests / offscreen).</summary>
    public void Render(SKCanvas canvas, double nowUnixMs)
    {
        lock (_sync)
        {
            if (_disposed) return;

            if (!_isPlaying || _animationConfig == null)
            {
                canvas.Clear(SKColors.Black);
                return;
            }

            long elapsedMs = (long)(nowUnixMs - _startUnixMs);

            DrawBackground(canvas);
            if (_hasContent)
            {
                UpdateAnimationPosition(elapsedMs);
                DrawAnimationLayer(canvas, elapsedMs);
            }
        }
    }

    /// <summary>Current animation top-left in local pixels (for tests/diagnostics).</summary>
    public (float X, float Y, int W, int H) CurrentLayout
    {
        get { lock (_sync) return (_animX, _animY, _animWidth, _animHeight); }
    }

    // ════════════════════════════════════════════════════════════════════
    // Layout (port of CalculateAnimationLayout)
    // ════════════════════════════════════════════════════════════════════

    private void CalculateAnimationLayout(AnimationLayerConfig config)
    {
        int nativeWidth = _contentNativeWidth;
        int nativeHeight = _contentNativeHeight;

        if (nativeWidth <= 0 || nativeHeight <= 0)
        {
            _animWidth = _width;
            _animHeight = _height;
        }
        else
        {
            switch (config.FitMode)
            {
                case ContentFitMode.Center:
                    _animWidth = nativeWidth;
                    _animHeight = nativeHeight;
                    break;
                case ContentFitMode.Fit:
                    double fitScale = Math.Min((double)_width / nativeWidth, (double)_height / nativeHeight);
                    _animWidth = (int)(nativeWidth * fitScale);
                    _animHeight = (int)(nativeHeight * fitScale);
                    break;
                case ContentFitMode.Fill:
                    double fillScale = Math.Max((double)_width / nativeWidth, (double)_height / nativeHeight);
                    _animWidth = (int)(nativeWidth * fillScale);
                    _animHeight = (int)(nativeHeight * fillScale);
                    break;
                case ContentFitMode.Stretch:
                default:
                    _animWidth = _width;
                    _animHeight = _height;
                    break;
            }
        }

        _centerInitialPosition = config.CenterInitialPosition;
        _rotateWithPath = config.RotateWithPath;

        _animX = _centerInitialPosition || _pixelsPerSecond == 0
            ? (_width - _animWidth) / 2f
            : -_animWidth;

        if (_hasCorridorConstraint && _corridorHeightPx > 0)
        {
            _animY = MathF.Max(_corridorTopPx, _corridorTopPx + (_corridorHeightPx - _animHeight) / 2f);
        }
        else
        {
            _animY = config.VerticalAlign switch
            {
                VerticalAlignment.Top => 0,
                VerticalAlignment.Bottom => _height - _animHeight,
                _ => (_height - _animHeight) / 2f
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Background (port of InitializeBackground / DrawBackground)
    // ════════════════════════════════════════════════════════════════════

    private void InitializeBackground(BackgroundLayerConfig config)
    {
        _backgroundMode = config.Mode;
        _hasCorridorConstraint = false;
        _corridorTopPx = 0;
        _corridorHeightPx = 0;
        _iconZoneBands.Clear();
        _animPath.Clear();
        _animPathTotalLength = 0f;
        _traverseCount = 0;

        switch (config.Mode)
        {
            case BackgroundMode.SolidColor:
                _backgroundColor = ParseHexColor(config.ColorHex);
                break;

            case BackgroundMode.StretchedImage:
            case BackgroundMode.TiledImage:
                if (_backgroundImage == null)
                {
                    _logger.LogWarning("[BG] Image not available, falling back to solid color: {Path}", config.ImagePath);
                    _backgroundMode = BackgroundMode.SolidColor;
                    _backgroundColor = ParseHexColor(config.ColorHex);
                }
                break;

            case BackgroundMode.ThreeZone:
                _topZoneColor = ParseHexColor(config.TopZoneColorHex);
                _bottomZoneColor = ParseHexColor(config.BottomZoneColorHex);
                _corridorColor = ParseHexColor(config.CorridorColorHex);
                _corridorTopPx = config.CorridorTopPx;
                _corridorHeightPx = config.CorridorHeightPx;
                _hasCorridorConstraint = true;
                // Re-run vertical placement now that the corridor is known
                if (_animationConfig != null) CalculateAnimationLayout(_animationConfig);
                break;

            case BackgroundMode.IconZone:
            {
                _iconCorridorBgColor = ParseHexColor(config.IconCorridorColorHex);

                // Linux: desktop icon positions are not readable (GNOME has none by default,
                // KDE's are private to plasmashell) → plan with an empty icon set.
                var icons = Array.Empty<(int X, int Y)>();
                int pathPaddingPx = _maskZones ? _animHeight * 3 / 2 : _animHeight / 2;
                if (_movementConfig?.Type == MovementType.SineWave)
                    pathPaddingPx += (int)Math.Ceiling(_movementConfig.WaveAmplitudePixels);
                if (_movementConfig?.Type == MovementType.RandomWalk && _movementConfig.WaveAmplitudePixels > 0)
                    pathPaddingPx += (int)Math.Ceiling(_movementConfig.WaveAmplitudePixels);

                var layout = ZonePlanner.Compute(
                    icons, 0, 0, _width, _height,
                    config.IconZonePaletteHexes, config.IconCorridorColorHex,
                    paddingPx: pathPaddingPx,
                    visualPaddingPx: 25);

                foreach (var band in layout.Bands)
                    _iconZoneBands.Add((band.Y, band.Height, band.X, band.Width, band.IsFree, ParseHexColor(band.ColorHex)));

                // Sequential mode: the server sends one global path for the whole wall
                var precomputed = _animationConfig?.PrecomputedPath;
                if (precomputed?.Count > 0)
                {
                    foreach (var wp in precomputed) _animPath.Add((wp.X, wp.Y));
                }
                else
                {
                    foreach (var wp in layout.Path) _animPath.Add((wp.X + _monitorOffsetX, wp.Y));
                }
                _animPathTotalLength = ComputePathLength(_animPath);
                _logger.LogInformation("[BG] IconZone (no icons on Linux): {Pts} path points, len={Len:F0}px",
                    _animPath.Count, _animPathTotalLength);
                break;
            }
        }
    }

    private void DrawBackground(SKCanvas canvas)
    {
        switch (_backgroundMode)
        {
            case BackgroundMode.SolidColor:
                canvas.Clear(_backgroundColor);
                break;

            case BackgroundMode.StretchedImage:
                canvas.Clear(SKColors.Black);
                if (_backgroundImage != null)
                    canvas.DrawImage(_backgroundImage, SKRect.Create(0, 0, _width, _height), _sampling, _bitmapPaint);
                break;

            case BackgroundMode.TiledImage:
                canvas.Clear(SKColors.Black);
                if (_backgroundImage != null && _backgroundImage.Width > 0 && _backgroundImage.Height > 0)
                {
                    int tileW = _backgroundImage.Width, tileH = _backgroundImage.Height;
                    for (int ty = 0; ty < _height; ty += tileH)
                        for (int tx = 0; tx < _width; tx += tileW)
                            canvas.DrawImage(_backgroundImage, SKRect.Create(tx, ty, tileW, tileH), _sampling, _bitmapPaint);
                }
                break;

            case BackgroundMode.ThreeZone:
            {
                canvas.Clear(SKColors.Black);
                using var paint = new SKPaint();
                if (_corridorTopPx > 0)
                {
                    paint.Color = _topZoneColor;
                    canvas.DrawRect(SKRect.Create(0, 0, _width, _corridorTopPx), paint);
                }
                paint.Color = _corridorColor;
                canvas.DrawRect(SKRect.Create(0, _corridorTopPx, _width, _corridorHeightPx), paint);
                int bottomY = _corridorTopPx + _corridorHeightPx;
                if (bottomY < _height)
                {
                    paint.Color = _bottomZoneColor;
                    canvas.DrawRect(SKRect.Create(0, bottomY, _width, _height - bottomY), paint);
                }
                break;
            }

            case BackgroundMode.IconZone:
                canvas.Clear(_iconCorridorBgColor);
                if (!_maskZones && _useZonePalette)
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

    // ════════════════════════════════════════════════════════════════════
    // Movement (port of UpdateAnimationPosition)
    // ════════════════════════════════════════════════════════════════════

    private void UpdateAnimationPosition(long elapsedMs)
    {
        if (!_maskZones && _backgroundMode == BackgroundMode.IconZone && _animPath.Count >= 2)
        {
            UpdatePathFollowingPosition(elapsedMs);
            return;
        }

        if (_movementConfig != null)
        {
            // Endless traveling-color mode: small float phase + growing integer cell offset
            if (_isTravelingMode && _tileAlignStepX > 0f
                && _movementConfig.Endless && _movementConfig.Type == MovementType.Linear)
            {
                double scrolledD = (double)elapsedMs * _movementConfig.SpeedPixelsPerSecond / 1000.0;
                long scrolledCells = (long)(scrolledD / _tileAlignStepX);
                float phaseX = (float)(scrolledD - (double)scrolledCells * _tileAlignStepX);
                _endlessCellOffsetI = _movementConfig.Reversed ? -(int)scrolledCells : (int)scrolledCells;
                float vx = _movementConfig.Reversed ? -phaseX : phaseX;
                _animX = vx - _monitorOffsetX;
                _animY = _movementConfig.StartY ?? (_height - _animHeight) / 2f;
            }
            else
            {
                _endlessCellOffsetI = 0;
                var (vx, vy) = MovementCalculator.Calculate(
                    _movementConfig, elapsedMs,
                    _animWidth, _animHeight,
                    _virtualCanvasWidth, _height,
                    tileAlignStepX: _isTravelingMode ? _tileAlignStepX : 0f);
                _animX = vx - _monitorOffsetX;
                _animY = vy;

                if (_hasCorridorConstraint && _corridorHeightPx > 0)
                {
                    float minY = _corridorTopPx;
                    float maxY = MathF.Max(minY, _corridorTopPx + _corridorHeightPx - _animHeight);
                    _animY = Math.Clamp(_animY, minY, maxY);
                }
            }
        }
        else if (_pixelsPerSecond > 0)
        {
            // Legacy: simple left-to-right scroll for configs without MovementConfig
            _animX = (float)(-_animWidth + elapsedMs / 1000.0 * _pixelsPerSecond);
        }
    }

    private void UpdatePathFollowingPosition(long elapsedMs)
    {
        float speed = _movementConfig?.SpeedPixelsPerSecond ?? 300f;
        bool bounce = _movementConfig?.Type == MovementType.Bounce;

        if (_animPathTotalLength > 0f)
        {
            float fullDist = elapsedMs * speed / 1000f;
            float cycleDist = bounce ? _animPathTotalLength * 2f : _animPathTotalLength;
            int newTraverseCount = (int)(fullDist / cycleDist);
            // No local re-planning without icons; the counter only seeds RandomWalk per lap
            if (newTraverseCount > _traverseCount) _traverseCount = newTraverseCount;
        }

        float dist;
        if (bounce)
        {
            float cycle = _animPathTotalLength * 2f;
            float t = (elapsedMs * speed / 1000f) % cycle;
            dist = t < _animPathTotalLength ? t : cycle - t;
        }
        else
        {
            dist = _animPathTotalLength > 0f ? (elapsedMs * speed / 1000f) % _animPathTotalLength : 0f;
        }

        var (px, py) = SamplePath(_animPath, dist);

        bool needsTangent = _rotateWithPath
            || _movementConfig?.Type == MovementType.SineWave
            || (_movementConfig?.Type == MovementType.RandomWalk && _movementConfig.WaveAmplitudePixels > 0);
        float tangentAngle = 0f;
        if (needsTangent && _animPathTotalLength > 0f)
        {
            float lookAhead = MathF.Min(10f, _animPathTotalLength * 0.01f);
            bool nearEnd = dist > _animPathTotalLength - lookAhead;
            var (px2, py2) = SamplePath(_animPath, nearEnd ? dist - lookAhead : (dist + lookAhead) % _animPathTotalLength);
            float dx = nearEnd ? px - px2 : px2 - px;
            float dy = nearEnd ? py - py2 : py2 - py;
            tangentAngle = MathF.Atan2(dy, dx);
        }
        _animRotationRad = _rotateWithPath ? tangentAngle : 0f;

        if (_movementConfig?.Type == MovementType.SineWave)
        {
            float sinOffset = MathF.Sin(elapsedMs / 1000f * _movementConfig.WaveFrequencyHz * MathF.Tau)
                              * _movementConfig.WaveAmplitudePixels;
            px += -MathF.Sin(tangentAngle) * sinOffset;
            py += MathF.Cos(tangentAngle) * sinOffset;
            py = Math.Clamp(py, _animHeight / 2f, Math.Max(_animHeight / 2f, _height - _animHeight / 2f));
        }

        if (_movementConfig?.Type == MovementType.RandomWalk && _movementConfig.WaveAmplitudePixels > 0)
        {
            float stepIntervalMs = MathF.Max(100f, _movementConfig.RandomStepIntervalMs);
            int stepIndex = (int)(elapsedMs / stepIntervalMs);
            float withinStep = (elapsedMs % stepIntervalMs) / stepIntervalMs;
            int lapSeed = _movementConfig.RandomSeed ^ (int)((uint)_traverseCount * 2246822519u);
            float t0 = GeneratePerpOffset(lapSeed, stepIndex);
            float t1 = GeneratePerpOffset(lapSeed, stepIndex + 1);
            float perpOffset = (t0 + (t1 - t0) * withinStep) * _movementConfig.WaveAmplitudePixels;
            px += -MathF.Sin(tangentAngle) * perpOffset;
            py += MathF.Cos(tangentAngle) * perpOffset;
            py = Math.Clamp(py, _animHeight / 2f, Math.Max(_animHeight / 2f, _height - _animHeight / 2f));
        }

        // (px, py) is the animation CENTER in virtual-canvas space
        _animX = px - _animWidth / 2f - _monitorOffsetX;
        _animY = py - _animHeight / 2f;
    }

    private static float ComputePathLength(List<(float X, float Y)> path)
    {
        float total = 0f;
        for (int i = 0; i < path.Count - 1; i++)
        {
            float dx = path[i + 1].X - path[i].X;
            float dy = path[i + 1].Y - path[i].Y;
            total += MathF.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    private static float GeneratePerpOffset(int seed, int stepIndex)
    {
        var rng = new Random(seed ^ (int)((uint)stepIndex * 2654435761u));
        return (float)(rng.NextDouble() * 2.0 - 1.0);
    }

    private static (float X, float Y) SamplePath(List<(float X, float Y)> path, float dist)
    {
        if (path.Count == 0) return (0f, 0f);
        if (path.Count == 1) return path[0];

        float remaining = dist;
        for (int i = 0; i < path.Count - 1; i++)
        {
            float dx = path[i + 1].X - path[i].X;
            float dy = path[i + 1].Y - path[i].Y;
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

    // ════════════════════════════════════════════════════════════════════
    // Animation layer (port of DrawAnimationLayer)
    // ════════════════════════════════════════════════════════════════════

    private void DrawAnimationLayer(SKCanvas canvas, long elapsedMs)
    {
        if (_animationConfig == null || _sources.Count == 0) return;
        int sourceCount = _sources.Count;

        var colorGrading = _animationConfig.ColorGrading;
        bool gradingActive = colorGrading != null && colorGrading.Mode != ColorGradingMode.None;
        bool isTraveling = gradingActive && _isTravelingMode;

        // Time-based grading: one matrix for the whole frame
        using var frameFilter = gradingActive && !isTraveling
            ? ToColorFilter(ColorGrader.Compute(colorGrading!, elapsedMs))
            : null;

        var pattern = _animationConfig.Pattern;
        if (pattern != null)
        {
            int cellW = ComputePatternCellWidth();
            int cellH = _animHeight;

            // VIRTUAL anchor so each monitor shows a different slice of the infinite grid
            var cells = PatternLayout.Compute(
                pattern,
                anchorX: _animX + _monitorOffsetX, anchorY: _animY,
                cellW: cellW, cellH: cellH,
                virtualCanvasWidth: _virtualCanvasWidth, virtualCanvasHeight: _height,
                monitorOffsetX: _monitorOffsetX,
                monitorWidth: _width, monitorHeight: _height,
                sourceImageCount: sourceCount,
                cellLogicalOffsetI: _endlessCellOffsetI);

            foreach (var cell in cells)
            {
                int srcIdx = cell.SourceIndex % sourceCount;
                var src = _sources[srcIdx];
                var img = src.Frames[src.FrameIndexAt((long)(elapsedMs * _gifSpeedMultiplier))];
                var (drawW, drawH) = ComputePatternDrawSize(srcIdx);

                if (isTraveling)
                {
                    // Deterministic per-cell decision: same cells colored regardless of scroll position
                    bool shouldColor = colorGrading!.ColoredCellPercentage >= 1.0f
                        || (uint)PatternLayout.Hash3(colorGrading.Seed ^ 0x55AA55AA, cell.LogicalI, cell.LogicalJ) % 100
                            < (uint)(colorGrading.ColoredCellPercentage * 100f);
                    using var cellFilter = shouldColor
                        ? ToColorFilter(ColorGrader.ComputeForCell(colorGrading, cell.LogicalI, cell.LogicalJ))
                        : null;
                    DrawCell(canvas, img, cell.ScreenX, cell.ScreenY, drawW, drawH, cell.RotationDeg, cellFilter);
                }
                else
                {
                    DrawCell(canvas, img, cell.ScreenX, cell.ScreenY, drawW, drawH, cell.RotationDeg, frameFilter);
                }
            }
        }
        else if (sourceCount > 1)
        {
            // Multi-image without pattern: each source at a seeded offset around the anchor
            float spread = Math.Max(0f, _animationConfig.MultiImageSpread);
            float jitterMs = Math.Max(0f, _animationConfig.MultiImagePhaseJitterMs);

            for (int srcIdx = 0; srcIdx < sourceCount; srcIdx++)
            {
                int hash = Hash2(0, srcIdx); // Windows player: Pattern?.Seed ?? 0, and Pattern is null here
                float dx = ((((hash & 0xFFFF) / 65535f) * 2f) - 1f) * spread;
                float dy = (((((hash >> 16) & 0xFFFF) / 65535f) * 2f) - 1f) * spread;
                long phase = (long)(((srcIdx * 0.6180339887) % 1.0) * jitterMs);

                var src = _sources[srcIdx];
                var img = src.Frames[src.FrameIndexAt((long)((elapsedMs + phase) * _gifSpeedMultiplier))];
                var (drawW, drawH) = ScaleToTargetHeight(src.Width, src.Height);
                DrawCell(canvas, img, _animX + dx, _animY + dy, drawW, drawH, 0f, isTraveling ? null : frameFilter);
            }
        }
        else
        {
            var src = _sources[0];
            var img = src.Frames[src.FrameIndexAt((long)(elapsedMs * _gifSpeedMultiplier))];
            float rotDeg = _rotateWithPath && _animRotationRad != 0f && _animPath.Count >= 2
                ? _animRotationRad * 180f / MathF.PI
                : 0f;
            DrawCell(canvas, img, _animX, _animY, _animWidth, _animHeight, rotDeg, isTraveling ? null : frameFilter);
        }
    }

    /// <summary>Pattern cell slot width: widest source scaled to _animHeight (mirrors the D2D player).</summary>
    private int ComputePatternCellWidth()
    {
        int cellW = _animWidth;
        if (_sources.Count > 0 && _animHeight > 0)
        {
            int maxNativeH = _sources.Max(s => s.Height);
            if (maxNativeH > 0)
            {
                float scale = (float)_animHeight / maxNativeH;
                int maxScaledW = _sources.Max(s => (int)(s.Width * scale));
                if (maxScaledW > 0) cellW = maxScaledW;
            }
        }
        return cellW;
    }

    private (int W, int H) ComputePatternDrawSize(int srcIdx)
    {
        var src = _sources[srcIdx];
        if (_animHeight <= 0 || src.Height <= 0) return (_animWidth, _animHeight);
        float scale = (float)_animHeight / src.Height;
        return ((int)(src.Width * scale), _animHeight);
    }

    private (int W, int H) ScaleToTargetHeight(int srcW, int srcH)
    {
        int targetH = _animationConfig?.TargetHeight ?? 720;
        if (srcH <= 0) return (_animWidth, _animHeight);
        float scale = (float)targetH / srcH;
        return ((int)(srcW * scale), targetH);
    }

    private void DrawCell(SKCanvas canvas, SKImage img, float x, float y, int w, int h, float rotationDeg, SKColorFilter? filter)
    {
        bool rotate = MathF.Abs(rotationDeg) > 0.01f;
        if (rotate)
        {
            canvas.Save();
            canvas.RotateDegrees(rotationDeg, x + w / 2f, y + h / 2f);
        }

        _bitmapPaint.ColorFilter = filter;
        canvas.DrawImage(img, SKRect.Create(x, y, w, h), _sampling, _bitmapPaint);
        _bitmapPaint.ColorFilter = null;

        if (rotate) canvas.Restore();
    }

    /// <summary>
    /// D2D ColorMatrix (row-vector, 5x4: out = [r g b a 1] · M) → Skia (column-vector, 4x5 row-major:
    /// out_i = Σ m[i*5+j]·in_j + m[i*5+4]). Both operate on unpremultiplied colours with offsets in 0..1.
    /// </summary>
    internal static SKColorFilter ToColorFilter(ColorMatrix5x4 m) => SKColorFilter.CreateColorMatrix(new[]
    {
        m.M11, m.M21, m.M31, m.M41, m.M51,
        m.M12, m.M22, m.M32, m.M42, m.M52,
        m.M13, m.M23, m.M33, m.M43, m.M53,
        m.M14, m.M24, m.M34, m.M44, m.M54,
    });

    private static int Hash2(int seed, int k)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)k * 2654435761u;
            h ^= h >> 13;
            h *= 2246822519u;
            h ^= h >> 16;
            return (int)h;
        }
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

    private void DisposeContent()
    {
        foreach (var s in _sources) s.Dispose();
        _sources.Clear();
        _hasContent = false;
        _backgroundImage?.Dispose();
        _backgroundImage = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _isPlaying = false;
            DisposeContent();
            _bitmapPaint.Dispose();
        }
    }
}

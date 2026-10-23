using Microsoft.Extensions.Logging;
using SkiaSharp;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Client.Linux.Rendering;

/// <summary>
/// One scene layer on one monitor: its sources, canvas, layout and movement state. Port of the Windows player's
/// per-sprite code (Program.cs CalculateAnimationLayout / UpdateAnimationPosition / DrawAnimationLayerCore,
/// Program.Effects.cs) with the LayerRuntime statics as instance fields. Positions and sizes are canvas units;
/// <see cref="Draw"/> scales them to device pixels by the layout's <see cref="NodeLayout.Scale"/>.
/// Not thread-safe: the owning <see cref="SceneContent"/> serializes access.
/// </summary>
internal sealed class SpriteLayer : IDisposable
{
    private readonly SceneContent _scene;
    private readonly ILogger _logger;

    public SceneLayer Config { get; }
    public bool FollowsBackground { get; }
    public NodeLayout Layout { get; }
    public bool PerMonitor { get; }

    private readonly AnimationLayerConfig _anim;
    private readonly MovementConfig _movement;
    private readonly List<AnimationSource> _sources;

    // Canvas (canvas units)
    private readonly int _cw, _ch, _virtualCanvasWidth, _virtualCanvasHeight, _monitorOffsetX, _canvasOffsetY;

    // Layout / position
    private int _animWidth, _animHeight;
    private float _animX, _animY, _animVirtualX, _animRotationRad, _tileAlignStepX;
    private bool _isTravelingMode;
    private int _endlessCellOffsetI;
    private bool _facingLeft, _hasPrevFacingX, _lastFlipX;
    private float _prevFacingX;

    private readonly SKPaint _paint = new() { IsAntialias = true };
    private readonly SKSamplingOptions _sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private SpriteLayer(SceneContent scene, SceneLayer config, bool isBottom, NodeLayout layout, bool perMonitor,
        List<AnimationSource> sources, ILogger logger)
    {
        _scene = scene;
        _logger = logger;
        Config = config;
        FollowsBackground = isBottom;
        Layout = layout;
        PerMonitor = perMonitor;
        _anim = config.Animation;
        _movement = config.Movement;
        _sources = sources;

        _cw = layout.Width;
        _ch = layout.Height;
        _virtualCanvasWidth = layout.CanvasWidth > 0 ? layout.CanvasWidth : layout.Width;
        _virtualCanvasHeight = layout.CanvasHeight > 0 ? layout.CanvasHeight : layout.Height;
        _monitorOffsetX = layout.OffsetX;
        _canvasOffsetY = layout.OffsetY;
    }

    public bool HasContent => _sources.Count > 0;
    public int NodeOrder => Layout.Order;
    private float Scale => Layout.Scale > 0f ? Layout.Scale : 1f;
    private BackgroundMode LayerBackgroundMode => FollowsBackground ? _scene.BackgroundMode : BackgroundMode.SolidColor;

    /// <summary>Current animation top-left in local canvas units (tests / diagnostics).</summary>
    public (float X, float Y, int W, int H) CurrentLayout => (_animX, _animY, _animWidth, _animHeight);

    // ════════════════════════════════════════════════════════════════════
    // Load (port of LoadActiveLayerSprite / LoadAdditionalAnimationSources)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Decode the layer's files (any thread) and lay it out. Unsupported or missing files leave it empty.</summary>
    public static SpriteLayer Load(SceneContent scene, SceneLayer config, bool isBottom, NodeLayout layout, bool perMonitor, ILogger logger)
    {
        var sources = new List<AnimationSource>();
        var anim = config.Animation;
        if (config.Enabled && !string.IsNullOrWhiteSpace(anim.AnimationPath))
        {
            var ext = Path.GetExtension(anim.AnimationPath).ToLowerInvariant();
            if (AnimationSource.IsVideo(ext))
            {
                logger.LogWarning("[LOAD] Video ({Ext}) is not supported by the Linux client yet — layer not drawn", ext);
            }
            else
            {
                try { sources.Add(AnimationSource.Load(anim.AnimationPath, logger)); }
                catch (Exception ex) { logger.LogError(ex, "[LOAD] Failed to decode {Path}", anim.AnimationPath); }
            }

            if (sources.Count > 0)
            {
                foreach (var path in anim.AdditionalAnimationPaths ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (!AnimationSource.IsImage(Path.GetExtension(path).ToLowerInvariant()))
                    {
                        // Same limitation as the Windows player: only static-image additional sources
                        logger.LogWarning("[LOAD] Skipping non-image additional source: {Path}", path);
                        continue;
                    }
                    try { sources.Add(AnimationSource.Load(path, logger)); }
                    catch (Exception ex) { logger.LogError(ex, "[LOAD] Failed to load additional source {Path}", path); }
                }
            }
        }

        var layer = new SpriteLayer(scene, config, isBottom, layout, perMonitor, sources, logger);
        layer.CalculateLayout();
        return layer;
    }

    /// <summary>Port of CalculateAnimationLayout plus the Traveling tile-align step.</summary>
    private void CalculateLayout()
    {
        int nativeW = _sources.Count > 0 ? _sources[0].Width : 0;
        int nativeH = _sources.Count > 0 ? _sources[0].Height : 0;
        if (nativeW <= 0 || nativeH <= 0)
        {
            _animWidth = _cw;
            _animHeight = _ch;
        }
        else
        {
            switch (_anim.FitMode)
            {
                case ContentFitMode.Center:
                    _animWidth = nativeW;
                    _animHeight = nativeH;
                    break;
                case ContentFitMode.Fit:
                    double fit = Math.Min((double)_cw / nativeW, (double)_ch / nativeH);
                    _animWidth = (int)(nativeW * fit);
                    _animHeight = (int)(nativeH * fit);
                    break;
                case ContentFitMode.Fill:
                    double fill = Math.Max((double)_cw / nativeW, (double)_ch / nativeH);
                    _animWidth = (int)(nativeW * fill);
                    _animHeight = (int)(nativeH * fill);
                    break;
                case ContentFitMode.TargetHeight:
                    int targetH = Math.Max(1, _anim.TargetHeight);
                    _animHeight = targetH;
                    _animWidth = Math.Max(1, (int)Math.Round(nativeW * (double)targetH / nativeH));
                    break;
                case ContentFitMode.Stretch:
                default:
                    _animWidth = _cw;
                    _animHeight = _ch;
                    break;
            }
        }

        _animX = (_cw - _animWidth) / 2f;
        if (FollowsBackground && _scene.CorridorHeightPx > 0)
        {
            _animY = MathF.Max(_scene.CorridorTopPx, _scene.CorridorTopPx + (_scene.CorridorHeightPx - _animHeight) / 2f);
        }
        else
        {
            _animY = _anim.VerticalAlign switch
            {
                VerticalAlignment.Top => 0,
                VerticalAlignment.Bottom => _ch - _animHeight,
                _ => (_ch - _animHeight) / 2f
            };
        }

        var cgMode = _anim.ColorGrading?.Mode ?? ColorGradingMode.None;
        _isTravelingMode = cgMode is ColorGradingMode.TravelingRainbow or ColorGradingMode.TravelingList or ColorGradingMode.TravelingRandom;
        _tileAlignStepX = _isTravelingMode && _anim.Pattern != null ? PatternCellWidth() + _anim.Pattern.SpacingX : 0f;
    }

    /// <summary>The IconZone planner's path padding for this (bottom) layer.</summary>
    public int PathPaddingPx(bool maskZones)
    {
        int pad = maskZones ? _animHeight * 3 / 2 : _animHeight / 2;
        if (_movement.Type == MovementType.SineWave)
            pad += (int)Math.Ceiling(_movement.WaveAmplitudePixels);
        if (_movement.Type == MovementType.RandomWalk && _movement.WaveAmplitudePixels > 0)
            pad += (int)Math.Ceiling(_movement.WaveAmplitudePixels);
        return pad;
    }

    public IReadOnlyList<WaypointF>? PrecomputedPath => _anim.PrecomputedPath;

    // ════════════════════════════════════════════════════════════════════
    // Movement (port of UpdateAnimationPosition)
    // ════════════════════════════════════════════════════════════════════

    public void Update(long elapsedMs)
    {
        var path = _scene.AnimPath;
        if (!_scene.MaskZones && MovementCalculator.FollowsIconPath(LayerBackgroundMode, _movement) && path.Count >= 2)
        {
            UpdatePathFollowing(elapsedMs, path, _scene.AnimPathLength);
            return;
        }

        if (_isTravelingMode && MovementCalculator.EndlessTravelingScroll(_movement, elapsedMs, _tileAlignStepX) is { } endless)
        {
            _endlessCellOffsetI = endless.CellOffsetI;
            _animVirtualX = endless.X;
            _animX = NodeMapping.ToLocalX(endless.X, _animWidth, Layout);
            _animY = (_movement.StartY ?? (_virtualCanvasHeight - _animHeight) / 2f) - _canvasOffsetY;
            return;
        }

        _endlessCellOffsetI = 0;
        var (vx, vy) = MovementCalculator.Calculate(
            _movement, elapsedMs,
            _animWidth, _animHeight,
            _virtualCanvasWidth, _virtualCanvasHeight,
            tileAlignStepX: _isTravelingMode ? _tileAlignStepX : 0f,
            canvasWraps: Layout.Wraps);
        _animVirtualX = vx;
        _animX = NodeMapping.ToLocalX(vx, _animWidth, Layout);   // mirrored nodes flip inside their slice
        _animY = ClampToCorridor(vy - _canvasOffsetY);
    }

    private void UpdatePathFollowing(long elapsedMs, List<(float X, float Y)> path, float pathLength)
    {
        float speed = _movement.SpeedPixelsPerSecond;
        bool bounce = _movement.Type == MovementType.Bounce;
        // Linux has no desktop icons to re-plan around, so every lap keeps the same route (lap count only seeds RandomWalk)
        int traverseCount = 0;
        if (pathLength > 0f)
        {
            float cycleDist = bounce ? pathLength * 2f : pathLength;
            traverseCount = (int)(elapsedMs * speed / 1000f / cycleDist);
        }

        float dist;
        if (bounce)
        {
            float cycle = pathLength * 2f;
            float t = (elapsedMs * speed / 1000f) % cycle;
            dist = t < pathLength ? t : cycle - t;
        }
        else
        {
            dist = pathLength > 0f ? (elapsedMs * speed / 1000f) % pathLength : 0f;
        }

        var (px, py) = SceneContent.SamplePath(path, dist);

        bool needsTangent = _anim.RotateWithPath
            || _movement.Type == MovementType.SineWave
            || (_movement.Type == MovementType.RandomWalk && _movement.WaveAmplitudePixels > 0);
        float tangentAngle = 0f;
        if (needsTangent && pathLength > 0f)
        {
            float lookAhead = MathF.Min(10f, pathLength * 0.01f);
            bool nearEnd = dist > pathLength - lookAhead;
            var (px2, py2) = SceneContent.SamplePath(path, nearEnd ? dist - lookAhead : (dist + lookAhead) % pathLength);
            float dx = nearEnd ? px - px2 : px2 - px;
            float dy = nearEnd ? py - py2 : py2 - py;
            tangentAngle = MathF.Atan2(dy, dx);
        }
        _animRotationRad = _anim.RotateWithPath ? tangentAngle : 0f;

        if (_movement.Type == MovementType.SineWave)
        {
            float sinOffset = MathF.Sin(elapsedMs / 1000f * _movement.WaveFrequencyHz * MathF.Tau) * _movement.WaveAmplitudePixels;
            px += -MathF.Sin(tangentAngle) * sinOffset;
            py += MathF.Cos(tangentAngle) * sinOffset;
            py = Math.Clamp(py, _animHeight / 2f, Math.Max(_animHeight / 2f, _ch - _animHeight / 2f));
        }

        if (_movement.Type == MovementType.RandomWalk && _movement.WaveAmplitudePixels > 0)
        {
            float stepIntervalMs = MathF.Max(100f, _movement.RandomStepIntervalMs);
            int stepIndex = (int)(elapsedMs / stepIntervalMs);
            float withinStep = (elapsedMs % stepIntervalMs) / stepIntervalMs;
            int lapSeed = _movement.RandomSeed ^ (int)((uint)traverseCount * 2246822519u);
            float t0 = PerpOffset(lapSeed, stepIndex);
            float t1 = PerpOffset(lapSeed, stepIndex + 1);
            float perpOffset = (t0 + (t1 - t0) * withinStep) * _movement.WaveAmplitudePixels;
            px += -MathF.Sin(tangentAngle) * perpOffset;
            py += MathF.Cos(tangentAngle) * perpOffset;
            py = Math.Clamp(py, _animHeight / 2f, Math.Max(_animHeight / 2f, _ch - _animHeight / 2f));
        }

        // (px, py) is the animation CENTER in virtual-canvas space
        _animVirtualX = px - _animWidth / 2f;
        _animX = NodeMapping.ToLocalX(_animVirtualX, _animWidth, Layout);
        _animY = py - _animHeight / 2f;
    }

    private static float PerpOffset(int seed, int stepIndex)
    {
        var rng = new Random(seed ^ (int)((uint)stepIndex * 2654435761u));
        return (float)(rng.NextDouble() * 2.0 - 1.0);
    }

    /// <summary>ThreeZone background: keep a sprite top (canvas units) inside the corridor (device px → canvas units).</summary>
    private float ClampToCorridor(float y)
    {
        if (!FollowsBackground || _scene.CorridorHeightPx <= 0) return y;
        float minY = _scene.CorridorTopPx / Scale;
        float maxY = (_scene.CorridorTopPx + _scene.CorridorHeightPx) / Scale - _animHeight;
        if (maxY < minY) maxY = minY;
        return Math.Clamp(y, minY, maxY);
    }

    private SpritePoseInput PoseInput() => new(
        _movement, _animWidth, _animHeight, _virtualCanvasWidth, _virtualCanvasHeight,
        _isTravelingMode ? _tileAlignStepX : 0f, Layout);

    // ════════════════════════════════════════════════════════════════════
    // Drawing (port of DrawAnimationLayer / DrawAnimationLayerCore)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Draw at the layer's own elapsed, faded by its spawn window. Canvas: device pixels, origin = monitor top-left.</summary>
    public void Draw(SKCanvas canvas, long elapsedMs)
    {
        if (_sources.Count == 0) return;
        float windowAlpha = LayerSchedule.Visibility(Config.ShowEveryMs, Config.ShowForMs, elapsedMs);
        if (windowAlpha <= 0f) return;
        float alpha = Math.Clamp(Config.Opacity, 0f, 1f) * windowAlpha;

        canvas.Save();
        canvas.Scale(Scale);   // canvas units → device px (physical canvas)
        try { DrawCore(canvas, elapsedMs, alpha); }
        finally { canvas.Restore(); }
    }

    private void DrawCore(SKCanvas canvas, long elapsedMs, float alpha)
    {
        int sourceCount = _sources.Count;
        var grading = _anim.ColorGrading;
        bool gradingActive = grading != null && grading.Mode != ColorGradingMode.None;
        bool isTraveling = gradingActive && _isTravelingMode;
        using var frameFilter = gradingActive && !isTraveling ? WallpaperScene.ToColorFilter(ColorGrader.Compute(grading!, elapsedMs)) : null;

        if (_anim.Pattern is { } pattern)
        {
            int cellW = PatternCellWidth(), cellH = _animHeight;
            var cells = PatternLayout.Compute(
                pattern,
                anchorX: _animVirtualX, anchorY: _animY,
                cellW: cellW, cellH: cellH,
                virtualCanvasWidth: _virtualCanvasWidth, virtualCanvasHeight: _virtualCanvasHeight,
                monitorOffsetX: _monitorOffsetX,
                monitorWidth: _cw, monitorHeight: _ch,
                sourceImageCount: sourceCount,
                cellLogicalOffsetI: _endlessCellOffsetI);

            foreach (var cell in cells)
            {
                int srcIdx = cell.SourceIndex % sourceCount;
                var img = Frame(srcIdx, elapsedMs);
                var (drawW, drawH) = PatternDrawSize(srcIdx);
                float cellX = Layout.Mirrored ? NodeMapping.MirrorLocalX(cell.ScreenX, drawW, Layout) : cell.ScreenX;
                if (isTraveling)
                {
                    bool shouldColor = grading!.ColoredCellPercentage >= 1.0f
                        || (uint)PatternLayout.Hash3(grading.Seed ^ 0x55AA55AA, cell.LogicalI, cell.LogicalJ) % 100
                            < (uint)(grading.ColoredCellPercentage * 100f);
                    using var cellFilter = shouldColor ? WallpaperScene.ToColorFilter(ColorGrader.ComputeForCell(grading, cell.LogicalI, cell.LogicalJ)) : null;
                    DrawCell(canvas, img, cellX, cell.ScreenY, drawW, drawH, cell.RotationDeg, cellFilter, alpha);
                }
                else
                {
                    DrawCell(canvas, img, cellX, cell.ScreenY, drawW, drawH, cell.RotationDeg, frameFilter, alpha);
                }
            }
            return;
        }

        if (sourceCount > 1)
        {
            // Multi-image without pattern: each source at a seeded offset around the anchor
            for (int srcIdx = 0; srcIdx < sourceCount; srcIdx++)
            {
                var (dx, dy, phase) = PatternLayout.MultiImageOffset(0, srcIdx, _anim.MultiImageSpread, _anim.MultiImagePhaseJitterMs);
                var img = Frame(srcIdx, elapsedMs + phase);
                var (drawW, drawH) = ScaleToTargetHeight(_sources[srcIdx].Width, _sources[srcIdx].Height);
                DrawCell(canvas, img, _animX + dx, _animY + dy, drawW, drawH, 0f, isTraveling ? null : frameFilter, alpha);
            }
            return;
        }

        // Single source
        var bmp = Frame(0, elapsedMs);
        float rotDeg = _anim.RotateWithPath && _animRotationRad != 0f && _scene.AnimPath.Count >= 2 ? _animRotationRad * 180f / MathF.PI : 0f;

        // Face travel direction: screen-space dx since the previous frame with hysteresis; a loop wrap is no turn
        bool flipX = false;
        if (_anim.FaceTravelDirection)
        {
            float dx = _hasPrevFacingX ? _animX - _prevFacingX : 0f;
            if (MathF.Abs(dx) > _cw) dx = 0f;
            _facingLeft = SyncTiming.ResolveFacingLeft(dx, _facingLeft);
            flipX = _facingLeft;
        }
        _lastFlipX = flipX;
        _prevFacingX = _animX;
        _hasPrevFacingX = true;

        var fx = SpriteEffects.Supported(_anim, _movement, LayerBackgroundMode) ? _anim.Effects : null;
        if (fx != null && _anim.FaceTravelDirection)
            flipX = _facingLeft = _lastFlipX = SpriteEffects.FacingLeftAt(PoseInput(), elapsedMs, _facingLeft);
        if (fx != null) DrawTrail(canvas, fx, bmp, elapsedMs, isTraveling ? null : frameFilter, gradingActive && !isTraveling, alpha);
        float spriteScale = fx != null ? SpriteEffects.PulseScale(fx.Pulse, elapsedMs) : 1f;
        if (fx != null) rotDeg += SpriteEffects.SpinDegrees(fx.SpinDegPerSecond, elapsedMs);

        // Ring seam: a sprite straddling the end of the perimeter is drawn at x and x − P
        Span<float> copies = stackalloc float[2];
        int copyCount = NodeMapping.WrapCopies(_animVirtualX, _animWidth, Layout, copies);
        for (int c = 0; c < copyCount; c++)
        {
            float lx = NodeMapping.ToLocalX(copies[c], _animWidth, Layout);
            if (!NodeMapping.IsVisible(lx, _animWidth, Layout)) continue;
            DrawCell(canvas, bmp, lx, _animY, _animWidth, _animHeight, rotDeg, isTraveling ? null : frameFilter, alpha, flipX, spriteScale);
        }
        if (fx != null) DrawBezelEffect(canvas, fx, elapsedMs);
    }

    private SKImage Frame(int srcIdx, long elapsedMs)
    {
        var src = _sources[srcIdx];
        return src.Frames[src.FrameIndexAt((long)(elapsedMs * _anim.SpeedMultiplier))];
    }

    /// <summary>Pattern cell slot width: widest source scaled to the animation height (mirrors the D2D player).</summary>
    private int PatternCellWidth()
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

    private (int W, int H) PatternDrawSize(int srcIdx)
    {
        var src = _sources[srcIdx];
        if (_animHeight <= 0 || src.Height <= 0) return (_animWidth, _animHeight);
        float scale = (float)_animHeight / src.Height;
        return ((int)(src.Width * scale), _animHeight);
    }

    private (int W, int H) ScaleToTargetHeight(int srcW, int srcH)
    {
        int targetH = _anim.TargetHeight;
        if (srcH <= 0 || targetH <= 0) return (_animWidth, _animHeight);
        float scale = (float)targetH / srcH;
        return ((int)(srcW * scale), targetH);
    }

    /// <summary>One sprite in canvas units, flipped / pulsed / rotated about its centre, graded, at <paramref name="alpha"/>.</summary>
    private void DrawCell(SKCanvas canvas, SKImage img, float x, float y, int w, int h, float rotationDeg, SKColorFilter? filter,
        float alpha, bool flipX = false, float spriteScale = 1f)
    {
        if (alpha <= 0.002f) return;
        bool rotate = MathF.Abs(rotationDeg) > 0.01f;
        bool scale = MathF.Abs(spriteScale - 1f) > 0.0005f;
        bool transform = rotate || flipX || scale;
        if (transform)
        {
            float cx = x + w / 2f, cy = y + h / 2f;
            canvas.Save();
            // Same order as the D2D matrix product (flip, then scale, then rotate — about the centre)
            if (rotate) canvas.RotateDegrees(rotationDeg, cx, cy);
            if (scale) canvas.Scale(spriteScale, spriteScale, cx, cy);
            if (flipX) canvas.Scale(-1f, 1f, cx, cy);
        }

        _paint.ColorFilter = filter;
        _paint.Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255));
        canvas.DrawImage(img, SKRect.Create(x, y, w, h), _sampling, _paint);
        _paint.ColorFilter = null;

        if (transform) canvas.Restore();
    }

    /// <summary>Ghosts behind the sprite (port of Program.Effects DrawTrail).</summary>
    private void DrawTrail(SKCanvas canvas, SpriteEffectsConfig fx, SKImage bmp, long elapsedMs, SKColorFilter? spriteFilter, bool gradingActive, float alpha)
    {
        int count = SpriteEffects.TrailCount(fx.Trail);
        if (count == 0) return;
        var input = PoseInput();
        var grading = _anim.ColorGrading;
        bool tintGhosts = gradingActive && fx.Trail!.UseGradingColor && grading != null;
        Span<float> copies = stackalloc float[2];
        for (int k = count; k >= 1; k--)
        {
            var ghost = SpriteEffects.Ghost(fx.Trail!, k);
            long t = elapsedMs - ghost.OffsetMs;
            if (t < 0 || ghost.Opacity <= 0.005f) continue;
            var (vx, vy) = SpriteEffects.Pose(input, t);
            float ly = ClampToCorridor(NodeMapping.ToLocalY(vy, Layout));
            bool flip = _anim.FaceTravelDirection && SpriteEffects.FacingLeftAt(input, t, _lastFlipX);
            float rot = SpriteEffects.SpinDegrees(fx.SpinDegPerSecond, t);
            float scale = SpriteEffects.PulseScale(fx.Pulse, t) * ghost.Scale;
            using var ghostFilter = tintGhosts ? WallpaperScene.ToColorFilter(ColorGrader.Compute(grading!, t)) : null;
            int n = NodeMapping.WrapCopies(vx, _animWidth, Layout, copies);
            for (int c = 0; c < n; c++)
            {
                float lx = NodeMapping.ToLocalX(copies[c], _animWidth, Layout);
                if (!NodeMapping.IsVisible(lx, _animWidth, Layout)) continue;
                DrawCell(canvas, bmp, lx, ly, _animWidth, _animHeight, rot, ghostFilter ?? spriteFilter, alpha * ghost.Opacity, flip, scale);
            }
        }
    }

    /// <summary>Portal / Glow / Flash / Ripple where the sprite crosses this node's edge (port of DrawBezelEffect).</summary>
    private void DrawBezelEffect(SKCanvas canvas, SpriteEffectsConfig fx, long elapsedMs)
    {
        var (r, g, b) = SpriteEffects.ParseBezelColor(fx.BezelColorHex);
        var color = new SKColor(r, g, b);
        using var paint = new SKPaint { IsAntialias = true, Color = color };

        if (SpriteEffects.FindPortal(PoseInput(), fx, elapsedMs) is { } portal && portal.RadiusY >= 0.5f)
        {
            var oval = SKRect.Create(portal.X - portal.RadiusX, portal.Y - portal.RadiusY, 2 * portal.RadiusX, 2 * portal.RadiusY);
            paint.Style = SKPaintStyle.Fill;
            paint.Color = color.WithAlpha(ToByte(0.28f * portal.Openness));
            canvas.DrawOval(oval, paint);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 3f + 5f * portal.Openness;
            paint.Color = color.WithAlpha(ToByte(portal.Openness));
            canvas.DrawOval(oval, paint);
        }

        var ev = SpriteEffects.FindBezelEvent(PoseInput(), fx, elapsedMs);
        if (ev == null) return;
        var v = SpriteEffects.Visual(ev.Value, Layout);
        if (v.Opacity <= 0.005f || v.Effect == BezelEffect.None) return;
        switch (v.Effect)
        {
            case BezelEffect.Glow:
            {
                // Bright at the crossing edge, fading into the screen
                var start = new SKPoint(v.EdgeOnRight ? v.X + v.Width : v.X, 0f);
                var end = new SKPoint(v.EdgeOnRight ? v.X : v.X + v.Width, 0f);
                using var shader = SKShader.CreateLinearGradient(start, end,
                    new[] { color.WithAlpha(ToByte(v.Opacity)), color.WithAlpha(0) }, SKShaderTileMode.Clamp);
                paint.Style = SKPaintStyle.Fill;
                paint.Shader = shader;
                canvas.DrawRect(SKRect.Create(v.X, v.Y, v.Width, v.Height), paint);
                paint.Shader = null;
                break;
            }
            case BezelEffect.Flash:
                paint.Style = SKPaintStyle.Fill;
                paint.Color = color.WithAlpha(ToByte(v.Opacity));
                canvas.DrawRect(SKRect.Create(v.X, v.Y, v.Width, v.Height), paint);
                break;
            case BezelEffect.Ripple:
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = v.StrokeWidth;
                paint.Color = color.WithAlpha(ToByte(v.Opacity));
                canvas.DrawCircle(v.X, v.Y, v.Radius, paint);
                break;
        }
    }

    internal static byte ToByte(float opacity) => (byte)Math.Round(Math.Clamp(opacity, 0f, 1f) * 255);

    public void Dispose()
    {
        foreach (var s in _sources) s.Dispose();
        _sources.Clear();
        _paint.Dispose();
    }
}

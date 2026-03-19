using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Models;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Renderers;

namespace WaBiBaBuSy.WallpaperEngine.Composition;

/// <summary>
/// Renders the animation layer for cross-screen wallpaper composition.
/// Loads video/GIF content and renders the visible portion for each screen based on animation position.
/// </summary>
public class AnimationLayerRenderer : IDisposable
{
    private readonly ILogger<AnimationLayerRenderer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private IWallpaperRenderer? _sourceRenderer;
    private AnimationLayerConfig? _config;

    private int _animationWidth;
    private int _animationHeight;
    private int _screenWidth; // Screen width for horizontal centering
    private int _currentVirtualX; // Current X position in virtual canvas
    private int _currentVirtualY; // Current Y position in virtual canvas
    private long _currentElapsedMs; // Current elapsed time in milliseconds (for frame selection)

    private long _animationStartTime;
    private int _virtualCanvasHeight;
    private int _virtualCanvasWidth;
    private MovementConfig? _movementConfig;
    private bool _disposed;

    public AnimationLayerRenderer(
        ILogger<AnimationLayerRenderer> logger,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Initialize the animation renderer with configuration
    /// </summary>
    /// <param name="config">Animation layer configuration</param>
    /// <param name="virtualCanvasHeight">Height of the virtual canvas for alignment calculations</param>
    /// <param name="monitorIndex">Monitor index for renderer initialization (default: 0)</param>
    public async Task InitializeAsync(AnimationLayerConfig config, int virtualCanvasHeight, int monitorIndex = 0)
    {
        _logger.LogInformation("Initializing animation layer renderer: Path={Path}, TargetHeight={Height}",
            config.AnimationPath, config.TargetHeight);

        _config = config;
        _virtualCanvasHeight = virtualCanvasHeight;

        if (string.IsNullOrEmpty(config.AnimationPath) || !File.Exists(config.AnimationPath))
        {
            _logger.LogError("Animation file not found: {Path}", config.AnimationPath);
            throw new FileNotFoundException("Animation file not found", config.AnimationPath);
        }

        // Determine file type and create appropriate renderer
        var extension = Path.GetExtension(config.AnimationPath).ToLowerInvariant();

        IWallpaperRenderer renderer = extension switch
        {
            // GIFs are handled by LibVLC (VideoWallpaperRenderer) for instant loading
            // LibVLC can decode GIF files natively as video media with hardware acceleration
            // This avoids the 10-minute GDI+ SelectActiveFrame() performance disaster
            ".gif" or ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" =>
                new VideoWallpaperRenderer(
                    _loggerFactory.CreateLogger<VideoWallpaperRenderer>(),
                    new Native.DesktopWindowManager(_loggerFactory.CreateLogger<Native.DesktopWindowManager>())),

            ".jpg" or ".jpeg" or ".png" or ".bmp" =>
                new ImageWallpaperRendererLibVLC(
                    _loggerFactory.CreateLogger<ImageWallpaperRendererLibVLC>(),
                    new Native.DesktopWindowManager(_loggerFactory.CreateLogger<Native.DesktopWindowManager>())),

            _ => throw new NotSupportedException($"Animation file format not supported: {extension}")
        };

        _sourceRenderer = renderer;

        // Initialize the renderer with the animation config
        // Create a WallpaperConfig from the AnimationLayerConfig
        // CRITICAL: Use HeadlessMode=true for composition pipeline
        // This prevents the renderer from creating its own window
        var wallpaperConfig = new WallpaperConfig
        {
            FilePath = config.AnimationPath,
            Type = extension switch
            {
                // GIFs now handled as video by LibVLC
                ".gif" or ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" => WallpaperType.Video,
                ".jpg" or ".jpeg" or ".png" or ".bmp" => WallpaperType.Image,
                _ => WallpaperType.Video
            },
            Loop = true,
            HardwareAcceleration = true,
            MaxFPS = 60,
            MonitorIndex = monitorIndex,
            HeadlessMode = true,  // Required for composition - no window creation
            SpeedMultiplier = config.SpeedMultiplier  // Apply speed multiplier from animation config
        };

        await _sourceRenderer.InitializeAsync(wallpaperConfig);
        _logger.LogInformation("Source renderer initialized for animation: {Path} with speed multiplier {Multiplier}x",
            config.AnimationPath, config.SpeedMultiplier);

        // Start the renderer to load media (required for GetFrameAtPosition to work)
        // In headless mode, this loads the media without displaying a window
        await _sourceRenderer.StartAsync();
        _logger.LogInformation("Source renderer started (media loaded for frame extraction)");

        // Calculate animation dimensions
        await CalculateAnimationDimensionsAsync(config);

        // Initialize the start position
        // For static images or centered playback mode, center them on screen
        // For cross-screen animations, start off-screen to the left
        bool isStaticImage = extension is ".jpg" or ".jpeg" or ".png" or ".bmp";
        bool shouldCenter = isStaticImage || config.CenterInitialPosition;

        if (shouldCenter)
        {
            // Center content horizontally on the screen
            _currentVirtualX = (_screenWidth - _animationWidth) / 2;
            _logger.LogInformation("[AnimLayer] Content centered at X={X} (screen={ScreenW}, anim={AnimW}, StaticImage={IsStatic}, CenterFlag={CenterFlag})",
                _currentVirtualX, _screenWidth, _animationWidth, isStaticImage, config.CenterInitialPosition);
        }
        else
        {
            // Animations start off-screen to the left for cross-screen movement
            _currentVirtualX = -_animationWidth;
            _logger.LogInformation("[AnimLayer] Animation positioned off-screen at X={X} (cross-screen mode)", _currentVirtualX);
        }
        CalculateVerticalPosition();

        // DO NOT set _animationStartTime here - let it be set by the render loop start
        // The render loop passes elapsed time, so _animationStartTime tracks frame position timing
        _animationStartTime = 0;

        _logger.LogInformation("[AnimLayer] Animation layer initialized: Size={Width}x{Height}, StartPosition=({X},{Y}), RendererType={RendererType}, AnimationPath={Path}",
            _animationWidth, _animationHeight, _currentVirtualX, _currentVirtualY, _sourceRenderer.GetType().Name, _config.AnimationPath);
    }

    /// <summary>
    /// Set the movement configuration and virtual canvas width for this renderer.
    /// Call after InitializeAsync and before UpdatePosition.
    /// </summary>
    public void SetMovementConfig(MovementConfig? movementConfig, int virtualCanvasWidth)
    {
        _movementConfig = movementConfig;
        _virtualCanvasWidth = virtualCanvasWidth;
    }

    /// <summary>
    /// Update the animation position based on elapsed time and speed.
    /// Uses MovementCalculator when available, falls back to legacy linear scroll.
    /// </summary>
    public void UpdatePosition(long timestampMs, int pixelsPerSecond)
    {
        if (_config == null)
            throw new InvalidOperationException("Renderer not initialized");

        // timestampMs is the elapsed time from render loop start, NOT Unix time
        var elapsedMs = timestampMs;

        // Store elapsed time for use by GetAnimationFrame()
        _currentElapsedMs = elapsedMs;

        if (_movementConfig != null && _movementConfig.Type != MovementType.Static)
        {
            int canvasW = _virtualCanvasWidth > 0 ? _virtualCanvasWidth : _screenWidth;
            var (vx, vy) = MovementCalculator.Calculate(
                _movementConfig, elapsedMs,
                _animationWidth, _animationHeight,
                canvasW, _virtualCanvasHeight);

            var prevX = _currentVirtualX;
            _currentVirtualX = (int)vx;
            _currentVirtualY = (int)vy;

            _logger.LogInformation("[AnimLayer-Detail] Movement({Type}): X={X} Y={Y} (was X={PrevX}), elapsed={Elapsed}ms",
                _movementConfig.Type, _currentVirtualX, _currentVirtualY, prevX, elapsedMs);
        }
        else if (pixelsPerSecond > 0)
        {
            // Legacy backward compat: simple left-to-right scroll
            var elapsedSeconds = elapsedMs / 1000.0;
            var prevX = _currentVirtualX;
            _currentVirtualX = (int)(-_animationWidth + (elapsedSeconds * pixelsPerSecond));

            _logger.LogInformation("[AnimLayer-Detail] Position updated: X={X} (was {PrevX}), elapsed={Elapsed}s, pixelsPerSecond={PPS}, AnimWidth={AnimW}",
                _currentVirtualX, prevX, elapsedSeconds, pixelsPerSecond, _animationWidth);
        }
        // For static (pixelsPerSecond=0 and no movement config), position remains unchanged
    }

    /// <summary>
    /// Reset animation to starting position (useful for looping)
    /// </summary>
    public void Reset()
    {
        _currentVirtualX = -_animationWidth;
        _animationStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _logger.LogDebug("Animation position reset");
    }

    /// <summary>
    /// Check if animation is visible on a specific screen
    /// </summary>
    public bool IsVisibleOnScreen(ScreenMapping screen)
    {
        var screenVirtualLeft = screen.VirtualBounds.X;
        var screenVirtualRight = screenVirtualLeft + screen.VirtualBounds.Width;
        var animationLeft = _currentVirtualX;
        var animationRight = animationLeft + _animationWidth;

        return animationRight >= screenVirtualLeft && animationLeft <= screenVirtualRight;
    }

    /// <summary>
    /// Render the animation layer for a specific screen.
    /// Returns null if the animation is not visible on this screen.
    /// </summary>
    public Bitmap? RenderForScreen(ScreenMapping screen)
    {
        try
        {
            if (_config == null || _sourceRenderer == null)
                throw new InvalidOperationException("Renderer not initialized");

            // Check if animation overlaps with this screen
            if (!IsVisibleOnScreen(screen))
            {
                _logger.LogInformation("[AnimLayer-Detail] Animation not visible on screen {Order}: AnimX={AnimX}, AnimWidth={AnimWidth}, ScreenX={ScreenX}, ScreenWidth={ScreenWidth}",
                    screen.Order, _currentVirtualX, _animationWidth, screen.VirtualBounds.X, screen.VirtualBounds.Width);
                return null;
            }

        _logger.LogDebug("[AnimLayer] Rendering animation for screen {Order}: ScreenPos=({ScreenX},{ScreenY}), AnimPos=({AnimX},{AnimY}), AnimSize={AnimW}x{AnimH}",
            screen.Order, screen.VirtualBounds.X, screen.VirtualBounds.Y, _currentVirtualX, _currentVirtualY, _animationWidth, _animationHeight);

        // Calculate the animation's bounding box in virtual coordinates
        var animationVirtualBounds = new Rectangle(
            _currentVirtualX,
            _currentVirtualY,
            _animationWidth,
            _animationHeight);

        _logger.LogTrace("[AnimLayer] Animation bounds: {AnimBounds}, Screen bounds: {ScreenBounds}",
            animationVirtualBounds, screen.VirtualBounds);

        // Calculate the intersection (visible region)
        var visibleRegion = Rectangle.Intersect(screen.VirtualBounds, animationVirtualBounds);

        _logger.LogTrace("[AnimLayer] Visible region: {VisibleRegion}", visibleRegion);

        if (visibleRegion.IsEmpty)
        {
            _logger.LogWarning("[AnimLayer] Visible region is empty (animation not overlapping screen) on screen {Order}", screen.Order);
            return null;
        }

        // Create bitmap for the visible portion on this screen
        var bitmap = new Bitmap(screen.ScreenBounds.Width, screen.ScreenBounds.Height);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            // Set high-quality rendering
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;

            // Clear to transparent
            graphics.Clear(Color.Transparent);

            // Calculate where to draw on the screen bitmap
            var drawX = visibleRegion.X - screen.VirtualBounds.X;
            var drawY = visibleRegion.Y - screen.VirtualBounds.Y;

            // Calculate which portion of the animation to draw
            var animSourceX = visibleRegion.X - _currentVirtualX;
            var animSourceY = visibleRegion.Y - _currentVirtualY;

            // Get the current animation frame
            _logger.LogTrace("[AnimLayer] Getting animation frame for drawX={DrawX}, drawY={DrawY}, visibleWidth={VW}, visibleHeight={VH}",
                drawX, drawY, visibleRegion.Width, visibleRegion.Height);

            using var animationFrame = GetAnimationFrame(_currentVirtualX);

            if (animationFrame != null)
            {
                _logger.LogTrace("[AnimLayer] Got animation frame: {Width}x{Height}, drawing to bitmap", animationFrame.Width, animationFrame.Height);

                // Map from the visible region in virtual coords to source rect within the frame.
                // The animation occupies _animationWidth x _animationHeight in virtual space,
                // but the actual frame bitmap may be a different size (native resolution).
                // We need to scale the source rect proportionally.
                float scaleX = (float)animationFrame.Width / _animationWidth;
                float scaleY = (float)animationFrame.Height / _animationHeight;

                var srcRect = new RectangleF(
                    animSourceX * scaleX,
                    animSourceY * scaleY,
                    visibleRegion.Width * scaleX,
                    visibleRegion.Height * scaleY);

                var destRect = new RectangleF(drawX, drawY, visibleRegion.Width, visibleRegion.Height);

                graphics.DrawImage(animationFrame, destRect, srcRect, GraphicsUnit.Pixel);

                _logger.LogTrace("[AnimLayer] Animation frame drawn to bitmap");
            }
            else
            {
                _logger.LogWarning("[AnimLayer] GetAnimationFrame returned null - animation will not be visible!");
            }
        }

            _logger.LogTrace("[AnimLayer] Returning composed animation bitmap for screen {Order}: {Width}x{Height}",
                screen.Order, bitmap.Width, bitmap.Height);
            return bitmap;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AnimLayer] EXCEPTION in RenderForScreen: {Message}", ex.Message);
            return null;
        }
    }

    private async Task CalculateAnimationDimensionsAsync(AnimationLayerConfig config)
    {
        // Get actual content dimensions from the renderer (available after StartAsync)
        int nativeWidth = _sourceRenderer?.ContentWidth ?? 0;
        int nativeHeight = _sourceRenderer?.ContentHeight ?? 0;

        if (nativeWidth <= 0 || nativeHeight <= 0)
        {
            // Fallback: use TargetHeight with 16:9 aspect ratio
            _logger.LogWarning("Could not get native content dimensions ({W}x{H}), falling back to TargetHeight with 16:9",
                nativeWidth, nativeHeight);
            _animationHeight = config.TargetHeight;
            _animationWidth = (int)(_animationHeight * 16.0 / 9.0);
            _screenWidth = _animationWidth;
        }
        else
        {
            int screenWidth = config.TargetHeight > 0 ? (int)(config.TargetHeight * 16.0 / 9.0) : 1920;
            int screenHeight = config.TargetHeight > 0 ? config.TargetHeight : 1080;
            _screenWidth = screenWidth;

            switch (config.FitMode)
            {
                case ContentFitMode.Center:
                    // Native resolution, centered
                    _animationWidth = nativeWidth;
                    _animationHeight = nativeHeight;
                    break;

                case ContentFitMode.Fit:
                    // Scale to fit within screen, preserve aspect ratio (letterboxed)
                    double fitScale = Math.Min(
                        (double)screenWidth / nativeWidth,
                        (double)screenHeight / nativeHeight);
                    _animationWidth = (int)(nativeWidth * fitScale);
                    _animationHeight = (int)(nativeHeight * fitScale);
                    break;

                case ContentFitMode.Fill:
                    // Scale to fill screen, preserve aspect ratio (may crop)
                    double fillScale = Math.Max(
                        (double)screenWidth / nativeWidth,
                        (double)screenHeight / nativeHeight);
                    _animationWidth = (int)(nativeWidth * fillScale);
                    _animationHeight = (int)(nativeHeight * fillScale);
                    break;

                case ContentFitMode.Stretch:
                default:
                    // Stretch to fill screen (current behavior)
                    _animationWidth = screenWidth;
                    _animationHeight = screenHeight;
                    break;
            }

            _logger.LogInformation("Animation dimensions: {Width}x{Height} (native: {NW}x{NH}, FitMode: {FitMode}, screen: {SW}x{SH})",
                _animationWidth, _animationHeight, nativeWidth, nativeHeight, config.FitMode, screenWidth, screenHeight);
        }

        await Task.CompletedTask;
    }

    private void CalculateVerticalPosition()
    {
        if (_config == null)
            return;

        // Calculate Y position based on vertical alignment
        _currentVirtualY = _config.VerticalAlign switch
        {
            VerticalAlignment.Top => 0,
            VerticalAlignment.Center => (_virtualCanvasHeight - _animationHeight) / 2,
            VerticalAlignment.Bottom => _virtualCanvasHeight - _animationHeight,
            _ => 0
        };

        _logger.LogDebug("Vertical position calculated: Y={Y} (align={Align})",
            _currentVirtualY, _config.VerticalAlign);
    }

    /// <summary>
    /// Get the current animation frame as a Bitmap.
    /// Uses the renderer's GetFrameAtPosition() method which handles all animation types.
    /// </summary>
    private Bitmap? GetAnimationFrame(int currentX)
    {
        if (_config == null || _sourceRenderer == null || string.IsNullOrEmpty(_config.AnimationPath))
        {
            _logger.LogTrace("[AnimFrame] GetAnimationFrame aborted: config={Config}, renderer={Renderer}, path={Path}",
                _config != null, _sourceRenderer != null, !string.IsNullOrEmpty(_config?.AnimationPath));
            return null;
        }

        try
        {
            // Use the stored elapsed time from UpdatePosition()
            // This ensures frame selection is synchronized with position updates
            var elapsedMs = _currentElapsedMs;

            _logger.LogTrace("[AnimFrame] Requesting frame from {RendererType} at elapsed {ElapsedMs}ms",
                _sourceRenderer.GetType().Name, elapsedMs);

            // Use the renderer's GetFrameAtPosition() method
            // This works for all animation types: GIFs, videos, and images
            var frame = _sourceRenderer.GetFrameAtPosition(elapsedMs);

            if (frame == null)
            {
                _logger.LogWarning("[AnimFrame] GetFrameAtPosition returned null at elapsedMs={ElapsedMs}", elapsedMs);
                return null;
            }

            _logger.LogTrace("[AnimFrame] Received frame: {Width}x{Height}", frame.Width, frame.Height);
            return frame;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AnimFrame] Error getting animation frame");
            return null;
        }
    }


    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("[Dispose] Starting animation layer renderer disposal");
        var startTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        _logger.LogInformation("[Dispose] Disposing source renderer: {RendererType}", _sourceRenderer?.GetType().Name ?? "null");
        _sourceRenderer?.Dispose();
        var elapsedMs = (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond) - startTime;
        _logger.LogInformation("[Dispose] ANIMATION RENDERER DISPOSAL TIME: {ElapsedMs}ms (this is where GIF file handles close)", elapsedMs);

        _sourceRenderer = null;

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

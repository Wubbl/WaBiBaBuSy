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
    private int _currentVirtualX; // Current X position in virtual canvas
    private int _currentVirtualY; // Current Y position in virtual canvas
    private long _currentElapsedMs; // Current elapsed time in milliseconds (for frame selection)

    private long _animationStartTime;
    private int _virtualCanvasHeight;
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
            ".gif" => new GifWallpaperRenderer(
                _loggerFactory.CreateLogger<GifWallpaperRenderer>(),
                new Native.DesktopWindowManager(_loggerFactory.CreateLogger<Native.DesktopWindowManager>())),

            ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" =>
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
                ".gif" => WallpaperType.Gif,
                ".jpg" or ".jpeg" or ".png" or ".bmp" => WallpaperType.Image,
                _ => WallpaperType.Video
            },
            Loop = true,
            HardwareAcceleration = true,
            MaxFPS = 60,
            MonitorIndex = monitorIndex,
            HeadlessMode = true  // Required for composition - no window creation
        };

        await _sourceRenderer.InitializeAsync(wallpaperConfig);
        _logger.LogInformation("Source renderer initialized for animation: {Path}", config.AnimationPath);

        // Calculate animation dimensions
        await CalculateAnimationDimensionsAsync(config);

        // Initialize the start position
        // For static images or centered playback mode, center them on screen
        // For cross-screen animations, start off-screen to the left
        bool isStaticImage = extension is ".jpg" or ".jpeg" or ".png" or ".bmp";
        bool shouldCenter = isStaticImage || config.CenterInitialPosition;

        if (shouldCenter)
        {
            // Center content on the screen (X=0)
            _currentVirtualX = 0;
            _logger.LogInformation("[AnimLayer] Content positioned at X=0 (centered - StaticImage={IsStatic}, CenterFlag={CenterFlag})",
                isStaticImage, config.CenterInitialPosition);
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
    /// Update the animation position based on elapsed time and speed
    /// </summary>
    public void UpdatePosition(long timestampMs, int pixelsPerSecond)
    {
        if (_config == null)
            throw new InvalidOperationException("Renderer not initialized");

        // timestampMs is the elapsed time from render loop start, NOT Unix time
        // So we use it directly
        var elapsedMs = timestampMs;
        var elapsedSeconds = elapsedMs / 1000.0;

        // Store elapsed time for use by GetAnimationFrame()
        _currentElapsedMs = elapsedMs;

        // Only update position for moving animations
        // Static images should remain at their initial position (X=0)
        if (pixelsPerSecond > 0)
        {
            // Calculate new X position for moving animations
            var prevX = _currentVirtualX;
            _currentVirtualX = (int)(-_animationWidth + (elapsedSeconds * pixelsPerSecond));

            _logger.LogInformation("[AnimLayer-Detail] Position updated: X={X} (was {PrevX}), elapsed={Elapsed}s, pixelsPerSecond={PPS}, AnimWidth={AnimW}",
                _currentVirtualX, prevX, elapsedSeconds, pixelsPerSecond, _animationWidth);
        }
        // For static images (pixelsPerSecond=0), X position remains unchanged at 0
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

            var animationFrame = GetAnimationFrame(_currentVirtualX);

            if (animationFrame != null)
            {
                _logger.LogTrace("[AnimLayer] Got animation frame: {Width}x{Height}, drawing to bitmap", animationFrame.Width, animationFrame.Height);

                // Draw the animation frame on the screen
                graphics.DrawImage(
                    animationFrame,
                    drawX,
                    drawY,
                    visibleRegion.Width,
                    visibleRegion.Height);

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
        // For now, we'll use the target height directly
        // TODO: Load actual animation and get its native dimensions

        // Assume 16:9 aspect ratio for now
        _animationHeight = config.TargetHeight;
        _animationWidth = (int)(_animationHeight * 16.0 / 9.0);

        _logger.LogInformation("Animation dimensions calculated: {Width}x{Height} (assumed 16:9)",
            _animationWidth, _animationHeight);

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

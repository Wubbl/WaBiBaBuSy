using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Interfaces;
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
    public async Task InitializeAsync(AnimationLayerConfig config, int virtualCanvasHeight)
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

            _ => throw new NotSupportedException($"Animation file format not supported: {extension}")
        };

        _sourceRenderer = renderer;

        // Calculate animation dimensions
        await CalculateAnimationDimensionsAsync(config);

        // Initialize the start position (off-screen to the left)
        _currentVirtualX = -_animationWidth;
        CalculateVerticalPosition();

        _animationStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _logger.LogInformation("Animation layer initialized: Size={Width}x{Height}, Position=({X},{Y})",
            _animationWidth, _animationHeight, _currentVirtualX, _currentVirtualY);
    }

    /// <summary>
    /// Update the animation position based on elapsed time and speed
    /// </summary>
    public void UpdatePosition(long timestampMs, int pixelsPerSecond)
    {
        if (_config == null)
            throw new InvalidOperationException("Renderer not initialized");

        var elapsedMs = timestampMs - _animationStartTime;
        var elapsedSeconds = elapsedMs / 1000.0;

        // Calculate new X position
        _currentVirtualX = (int)(-_animationWidth + (elapsedSeconds * pixelsPerSecond));

        _logger.LogTrace("Animation position updated: X={X} (elapsed={Elapsed}s)",
            _currentVirtualX, elapsedSeconds);
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
        if (_config == null || _sourceRenderer == null)
            throw new InvalidOperationException("Renderer not initialized");

        // Check if animation overlaps with this screen
        if (!IsVisibleOnScreen(screen))
        {
            _logger.LogTrace("Animation not visible on screen {Order}", screen.Order);
            return null;
        }

        _logger.LogDebug("Rendering animation for screen {Order}: ScreenPos=({X},{Y}), AnimPos=({AnimX},{AnimY})",
            screen.Order, screen.VirtualBounds.X, screen.VirtualBounds.Y, _currentVirtualX, _currentVirtualY);

        // Calculate the animation's bounding box in virtual coordinates
        var animationVirtualBounds = new Rectangle(
            _currentVirtualX,
            _currentVirtualY,
            _animationWidth,
            _animationHeight);

        // Calculate the intersection (visible region)
        var visibleRegion = Rectangle.Intersect(screen.VirtualBounds, animationVirtualBounds);

        if (visibleRegion.IsEmpty)
            return null;

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

            // For now, we'll create a placeholder rectangle
            // TODO: Integrate with actual video/GIF frame capture
            using var brush = new SolidBrush(Color.FromArgb(200, 255, 0, 0)); // Semi-transparent red for testing
            graphics.FillRectangle(brush, drawX, drawY, visibleRegion.Width, visibleRegion.Height);

            // Draw border for debugging
            using var pen = new Pen(Color.Yellow, 2);
            graphics.DrawRectangle(pen, drawX, drawY, visibleRegion.Width - 1, visibleRegion.Height - 1);
        }

        return bitmap;
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

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("Disposing animation layer renderer");

        _sourceRenderer?.Dispose();
        _sourceRenderer = null;

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

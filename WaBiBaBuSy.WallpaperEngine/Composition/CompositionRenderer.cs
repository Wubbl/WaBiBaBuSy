using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.WallpaperEngine.Composition;

/// <summary>
/// Composites background and animation layers into final frame buffers for each screen.
/// </summary>
public class CompositionRenderer : IDisposable
{
    private readonly ILogger<CompositionRenderer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private BackgroundLayerRenderer? _backgroundRenderer;
    private AnimationLayerRenderer? _animationRenderer;
    private VirtualCanvasManager? _canvasManager;
    private bool _disposed;

    // Frame buffer cache to reduce allocations
    private readonly Dictionary<string, Bitmap> _frameBufferCache = new();

    // Diagnostic tracking
    private long _composeCallCount = 0;

    /// <summary>
    /// Provides access to the animation layer renderer for movement configuration.
    /// </summary>
    public AnimationLayerRenderer? AnimationRenderer => _animationRenderer;

    public CompositionRenderer(
        ILogger<CompositionRenderer> logger,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Initialize the compositor with canvas layout and layer configurations
    /// </summary>
    public async Task InitializeAsync(
        VirtualCanvasManager canvasManager,
        BackgroundLayerConfig backgroundConfig,
        AnimationLayerConfig animationConfig,
        int monitorIndex = 0)
    {
        _logger.LogInformation("Initializing composition renderer");

        _canvasManager = canvasManager;

        // Initialize background layer
        _backgroundRenderer = new BackgroundLayerRenderer(
            _loggerFactory.CreateLogger<BackgroundLayerRenderer>());
        await _backgroundRenderer.InitializeAsync(backgroundConfig);

        // Initialize animation layer
        _animationRenderer = new AnimationLayerRenderer(
            _loggerFactory.CreateLogger<AnimationLayerRenderer>(),
            _loggerFactory);
        await _animationRenderer.InitializeAsync(animationConfig, canvasManager.VirtualBounds.Height, monitorIndex);

        _logger.LogInformation("Composition renderer initialized successfully");
    }

    /// <summary>
    /// Update the animation position (called each frame)
    /// </summary>
    public void UpdateAnimationPosition(long timestampMs, int pixelsPerSecond)
    {
        if (_animationRenderer == null)
            throw new InvalidOperationException("Renderer not initialized");

        // TASK-012: Disabled noisy log (was LogInformation, now Trace)
        _logger.LogTrace("[Composition-Detail] UpdateAnimationPosition called: timestampMs={TimestampMs}, pixelsPerSecond={PPS}",
            timestampMs, pixelsPerSecond);

        _animationRenderer.UpdatePosition(timestampMs, pixelsPerSecond);
    }

    /// <summary>
    /// Compose a complete frame for a specific screen
    /// </summary>
    public Bitmap ComposeForScreen(ScreenMapping screen)
    {
        if (_backgroundRenderer == null || _animationRenderer == null)
            throw new InvalidOperationException("Renderer not initialized");

        _logger.LogTrace("[Composition] ComposeForScreen called for screen {Order}", screen.Order);

        // Render background layer
        _logger.LogTrace("[Composition] Rendering background layer");
        var backgroundBitmap = _backgroundRenderer.RenderForScreen(screen);
        _logger.LogTrace("[Composition] Background layer rendered: {Width}x{Height}", backgroundBitmap.Width, backgroundBitmap.Height);

        // Render animation layer (may be null if not visible)
        _logger.LogTrace("[Composition] Rendering animation layer");
        var animationBitmap = _animationRenderer.RenderForScreen(screen);

        if (animationBitmap == null)
        {
            // No animation visible, just return background
            _logger.LogWarning("[Composition] Animation layer returned null, returning background only");
            return backgroundBitmap;
        }

        _logger.LogTrace("[Composition] Animation layer rendered: {Width}x{Height}", animationBitmap.Width, animationBitmap.Height);

        // Composite animation on top of background
        _logger.LogTrace("[Composition] Compositing animation layer on top of background");
        using (var graphics = Graphics.FromImage(backgroundBitmap))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

            // Draw animation layer on top
            graphics.DrawImage(animationBitmap, 0, 0);
            _logger.LogTrace("[Composition] Animation composited successfully");
        }

        animationBitmap.Dispose();

        // TASK-012: Disabled noisy log (was LogInformation, now Trace)
        _logger.LogTrace("[Composition] Frame composition complete for screen {Order}: {Width}x{Height}",
            screen.Order, backgroundBitmap.Width, backgroundBitmap.Height);

        // DIAGNOSTIC: Sample center pixel of composed frame every 60 frames
        if (_composeCallCount % 60 == 0)
        {
            var centerX = backgroundBitmap.Width / 2;
            var centerY = backgroundBitmap.Height / 2;
            var centerPixel = backgroundBitmap.GetPixel(centerX, centerY);
            _logger.LogInformation("[COMPOSITION-PIXEL] Frame #{Count} | Center pixel: R={R} G={G} B={B} A={A}",
                _composeCallCount, centerPixel.R, centerPixel.G, centerPixel.B, centerPixel.A);
        }
        _composeCallCount++;

        return backgroundBitmap;
    }

    /// <summary>
    /// Compose frames for all screens and return as a dictionary keyed by client ID
    /// </summary>
    public Dictionary<string, Bitmap> ComposeForAllScreens(long timestampMs, int pixelsPerSecond)
    {
        if (_canvasManager == null)
            throw new InvalidOperationException("Renderer not initialized");

        // DEFENSIVE CHECK: Verify renderers are initialized
        if (_backgroundRenderer == null)
        {
            _logger.LogError("CRITICAL: BackgroundRenderer is NULL - composition cannot proceed");
            throw new InvalidOperationException("BackgroundRenderer not initialized. Call InitializeAsync first.");
        }

        if (_animationRenderer == null)
        {
            _logger.LogError("CRITICAL: AnimationRenderer is NULL - composition cannot proceed");
            throw new InvalidOperationException("AnimationRenderer not initialized. Call InitializeAsync first.");
        }

        // Update animation position
        UpdateAnimationPosition(timestampMs, pixelsPerSecond);

        var frames = new Dictionary<string, Bitmap>();

        foreach (var screen in _canvasManager.ScreenMappings)
        {
            var frame = ComposeForScreen(screen);
            frames[screen.ClientId] = frame;
        }

        _logger.LogDebug("Composed frames for {Count} screens", frames.Count);

        return frames;
    }

    /// <summary>
    /// Encode a bitmap to JPEG bytes for network transmission
    /// </summary>
    public byte[] EncodeBitmapToJpeg(Bitmap bitmap, int quality = 90)
    {
        using var stream = new MemoryStream();

        // Create JPEG encoder with specified quality
        var encoder = GetJpegEncoder();
        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);

        bitmap.Save(stream, encoder, encoderParams);

        return stream.ToArray();
    }

    /// <summary>
    /// Reset animation to starting position
    /// </summary>
    public void ResetAnimation()
    {
        _animationRenderer?.Reset();
        _logger.LogInformation("Animation reset to starting position");
    }

    /// <summary>
    /// Clear cached frame buffers
    /// </summary>
    public void ClearCache()
    {
        foreach (var buffer in _frameBufferCache.Values)
        {
            buffer.Dispose();
        }
        _frameBufferCache.Clear();
        _logger.LogDebug("Frame buffer cache cleared");
    }

    private ImageCodecInfo GetJpegEncoder()
    {
        var codecs = ImageCodecInfo.GetImageEncoders();
        return codecs.First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("[Dispose] Starting composition renderer disposal");
        var startTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        _logger.LogInformation("[Dispose] Clearing frame cache");
        ClearCache();
        var afterCacheTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] Cache cleared in {ElapsedMs}ms", afterCacheTime - startTime);

        _logger.LogInformation("[Dispose] Disposing background renderer");
        _backgroundRenderer?.Dispose();
        _backgroundRenderer = null;
        var afterBgTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] Background renderer disposed in {ElapsedMs}ms", afterBgTime - afterCacheTime);

        _logger.LogInformation("[Dispose] Disposing animation renderer");
        _animationRenderer?.Dispose();
        _animationRenderer = null;
        var afterAnimTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] Animation renderer disposed in {ElapsedMs}ms", afterAnimTime - afterBgTime);

        _disposed = true;
        var totalTime = afterAnimTime - startTime;
        _logger.LogInformation("[Dispose] COMPOSITION RENDERER TOTAL DISPOSAL TIME: {TotalMs}ms", totalTime);

        GC.SuppressFinalize(this);
    }
}

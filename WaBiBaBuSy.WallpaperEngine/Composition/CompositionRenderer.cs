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
        AnimationLayerConfig animationConfig)
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
        await _animationRenderer.InitializeAsync(animationConfig, canvasManager.VirtualBounds.Height);

        _logger.LogInformation("Composition renderer initialized successfully");
    }

    /// <summary>
    /// Update the animation position (called each frame)
    /// </summary>
    public void UpdateAnimationPosition(long timestampMs, int pixelsPerSecond)
    {
        if (_animationRenderer == null)
            throw new InvalidOperationException("Renderer not initialized");

        _animationRenderer.UpdatePosition(timestampMs, pixelsPerSecond);
    }

    /// <summary>
    /// Compose a complete frame for a specific screen
    /// </summary>
    public Bitmap ComposeForScreen(ScreenMapping screen)
    {
        if (_backgroundRenderer == null || _animationRenderer == null)
            throw new InvalidOperationException("Renderer not initialized");

        _logger.LogTrace("Composing frame for screen {Order}", screen.Order);

        // Render background layer
        var backgroundBitmap = _backgroundRenderer.RenderForScreen(screen);

        // Render animation layer (may be null if not visible)
        var animationBitmap = _animationRenderer.RenderForScreen(screen);

        if (animationBitmap == null)
        {
            // No animation visible, just return background
            return backgroundBitmap;
        }

        // Composite animation on top of background
        using (var graphics = Graphics.FromImage(backgroundBitmap))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

            // Draw animation layer on top
            graphics.DrawImage(animationBitmap, 0, 0);
        }

        animationBitmap.Dispose();

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

        _logger.LogInformation("Disposing composition renderer");

        ClearCache();

        _backgroundRenderer?.Dispose();
        _backgroundRenderer = null;

        _animationRenderer?.Dispose();
        _animationRenderer = null;

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

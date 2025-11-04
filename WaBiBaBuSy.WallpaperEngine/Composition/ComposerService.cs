using System.Drawing;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.WallpaperEngine.Composition;

/// <summary>
/// Service wrapper around CompositionRenderer that handles composition orchestration.
/// Separates composition logic from rendering logic for easier testing.
/// Clients (local or remote) use this to compose background + animation into final frames.
/// </summary>
public class ComposerService : IDisposable
{
    private readonly ILogger<ComposerService> _logger;
    private readonly CompositionRenderer _compositionRenderer;
    private bool _disposed;

    // Canvas and configuration
    private VirtualCanvasManager? _canvasManager;
    private BackgroundLayerConfig? _backgroundConfig;
    private AnimationLayerConfig? _animationConfig;

    public ComposerService(
        ILogger<ComposerService> logger,
        CompositionRenderer compositionRenderer)
    {
        _logger = logger;
        _compositionRenderer = compositionRenderer ?? throw new ArgumentNullException(nameof(compositionRenderer));
    }

    /// <summary>
    /// Initialize the composer with canvas layout and layer configurations.
    /// Must be called before ComposeSingle or ComposeAll.
    /// </summary>
    public async Task InitializeAsync(
        VirtualCanvasManager canvasManager,
        BackgroundLayerConfig backgroundConfig,
        AnimationLayerConfig animationConfig)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ComposerService));

        _logger.LogInformation("Initializing composer service");

        _canvasManager = canvasManager ?? throw new ArgumentNullException(nameof(canvasManager));
        _backgroundConfig = backgroundConfig ?? throw new ArgumentNullException(nameof(backgroundConfig));
        _animationConfig = animationConfig ?? throw new ArgumentNullException(nameof(animationConfig));

        // Initialize the underlying composition renderer
        await _compositionRenderer.InitializeAsync(canvasManager, backgroundConfig, animationConfig);

        _logger.LogInformation("Composer service initialized successfully");
    }

    /// <summary>
    /// Compose a single frame for a specific screen.
    /// Returns the composed bitmap (caller is responsible for disposal).
    /// </summary>
    public Bitmap ComposeSingle(ScreenMapping screen, long timestampMs, int pixelsPerSecond)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ComposerService));

        if (_canvasManager == null)
            throw new InvalidOperationException("Composer not initialized. Call InitializeAsync first.");

        _logger.LogDebug("Composing frame for screen {Order}", screen.Order);

        // Update animation position based on timestamp
        _compositionRenderer.UpdateAnimationPosition(timestampMs, pixelsPerSecond);

        // Compose and return the frame
        var frame = _compositionRenderer.ComposeForScreen(screen);

        _logger.LogTrace("Frame composed for screen {Order}: {Width}x{Height}", screen.Order, frame.Width, frame.Height);

        return frame;
    }

    /// <summary>
    /// Compose frames for all screens in the canvas.
    /// Returns a dictionary keyed by client ID with pre-composed bitmaps.
    /// Caller is responsible for disposing frames after rendering.
    /// </summary>
    public Dictionary<string, Bitmap> ComposeAll(long timestampMs, int pixelsPerSecond)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ComposerService));

        if (_canvasManager == null)
            throw new InvalidOperationException("Composer not initialized. Call InitializeAsync first.");

        _logger.LogDebug("Composing frames for all screens");

        var frames = _compositionRenderer.ComposeForAllScreens(timestampMs, pixelsPerSecond);

        _logger.LogDebug("Composed frames for {Count} screens", frames.Count);

        return frames;
    }

    /// <summary>
    /// Reset animation to starting position.
    /// </summary>
    public void ResetAnimation()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ComposerService));

        _compositionRenderer.ResetAnimation();
        _logger.LogInformation("Animation reset via composer service");
    }

    /// <summary>
    /// Update animation position (called each frame).
    /// </summary>
    public void UpdateAnimationPosition(long timestampMs, int pixelsPerSecond)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ComposerService));

        _compositionRenderer.UpdateAnimationPosition(timestampMs, pixelsPerSecond);
    }

    /// <summary>
    /// Get screen mappings currently configured in canvas.
    /// </summary>
    public IReadOnlyList<ScreenMapping> GetScreenMappings()
    {
        if (_canvasManager == null)
            throw new InvalidOperationException("Composer not initialized");

        return _canvasManager.ScreenMappings;
    }

    /// <summary>
    /// Clear cached frame buffers in underlying renderer.
    /// </summary>
    public void ClearCache()
    {
        _compositionRenderer.ClearCache();
        _logger.LogDebug("Frame cache cleared via composer service");
    }

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("Disposing composer service");

        _compositionRenderer?.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

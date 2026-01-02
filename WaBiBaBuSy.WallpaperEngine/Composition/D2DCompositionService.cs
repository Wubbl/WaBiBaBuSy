using System.Drawing;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Direct2D;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Composition;

/// <summary>
/// Orchestrates the Direct2D composition and display pipeline.
/// Manages CompositionRenderer → JPEG encoding → D2DPlayerHost → Display
/// </summary>
public class D2DCompositionService : IDisposable
{
    private readonly ILogger<D2DCompositionService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DesktopWindowManager _desktopWindowManager;
    private readonly CompositionRenderer _compositionRenderer;

    private VirtualCanvasManager? _canvasManager;
    private readonly Dictionary<int, D2DPlayerHost> _playerHosts = new();
    private CancellationTokenSource? _renderLoopCts;
    private Task? _renderLoopTask;
    private bool _disposed;
    private bool _isRunning;

    // Configuration
    private int _targetFps = 30;
    private int _jpegQuality = 90;

    public D2DCompositionService(
        ILogger<D2DCompositionService> logger,
        ILoggerFactory loggerFactory,
        DesktopWindowManager desktopWindowManager,
        CompositionRenderer compositionRenderer)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _desktopWindowManager = desktopWindowManager ?? throw new ArgumentNullException(nameof(desktopWindowManager));
        _compositionRenderer = compositionRenderer ?? throw new ArgumentNullException(nameof(compositionRenderer));
    }

    /// <summary>
    /// Gets whether the service is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets or sets the target frame rate (FPS).
    /// </summary>
    public int TargetFps
    {
        get => _targetFps;
        set
        {
            if (value <= 0 || value > 120)
                throw new ArgumentOutOfRangeException(nameof(value), "FPS must be between 1 and 120");
            _targetFps = value;
            _logger.LogInformation("Target FPS set to {Fps}", _targetFps);
        }
    }

    /// <summary>
    /// Gets or sets the JPEG encoding quality (1-100).
    /// </summary>
    public int JpegQuality
    {
        get => _jpegQuality;
        set
        {
            if (value < 1 || value > 100)
                throw new ArgumentOutOfRangeException(nameof(value), "Quality must be between 1 and 100");
            _jpegQuality = value;
            _logger.LogInformation("JPEG quality set to {Quality}", _jpegQuality);
        }
    }

    /// <summary>
    /// Initialize the composition service with canvas layout and layer configurations.
    /// </summary>
    public async Task InitializeAsync(
        VirtualCanvasManager canvasManager,
        BackgroundLayerConfig backgroundConfig,
        AnimationLayerConfig animationConfig,
        Rectangle actualMonitorBounds,
        int monitorIndex = 0,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(D2DCompositionService));

        _logger.LogInformation("Initializing D2D composition service for {ScreenCount} screens",
            canvasManager.ScreenMappings.Count);

        _canvasManager = canvasManager ?? throw new ArgumentNullException(nameof(canvasManager));

        // Initialize composition renderer
        await _compositionRenderer.InitializeAsync(canvasManager, backgroundConfig, animationConfig, monitorIndex);

        // Create D2D player hosts for each screen
        foreach (var screen in canvasManager.ScreenMappings)
        {
            _logger.LogInformation("Creating D2D player host for screen {Order}", screen.Order);

            var playerHost = new D2DPlayerHost(
                screen,
                _loggerFactory.CreateLogger<D2DPlayerHost>(),
                _desktopWindowManager,
                actualMonitorBounds);

            await playerHost.InitializeAsync(cancellationToken);

            _playerHosts[screen.Order] = playerHost;
        }

        _logger.LogInformation("D2D composition service initialized successfully");
    }

    /// <summary>
    /// Start the render loop at the configured FPS.
    /// </summary>
    public void Start(long startTimestampMs, int pixelsPerSecond)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(D2DCompositionService));

        if (_isRunning)
        {
            _logger.LogWarning("D2D composition service already running");
            return;
        }

        if (_canvasManager == null || _playerHosts.Count == 0)
        {
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");
        }

        _logger.LogInformation("Starting D2D composition render loop at {Fps} FPS", _targetFps);

        _renderLoopCts = new CancellationTokenSource();
        _renderLoopTask = Task.Run(() => RenderLoopAsync(startTimestampMs, pixelsPerSecond, _renderLoopCts.Token));
        _isRunning = true;
    }

    /// <summary>
    /// Stop the render loop.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
        {
            _logger.LogWarning("D2D composition service not running");
            return;
        }

        _logger.LogInformation("Stopping D2D composition render loop");

        _renderLoopCts?.Cancel();

        if (_renderLoopTask != null)
        {
            try
            {
                await _renderLoopTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping render loop");
            }
        }

        _renderLoopCts?.Dispose();
        _renderLoopCts = null;
        _renderLoopTask = null;
        _isRunning = false;

        _logger.LogInformation("D2D composition render loop stopped");
    }

    /// <summary>
    /// Main render loop that generates and sends frames at the target FPS.
    /// </summary>
    private async Task RenderLoopAsync(long startTimestampMs, int pixelsPerSecond, CancellationToken cancellationToken)
    {
        var frameIntervalMs = 1000.0 / _targetFps;
        var frameCount = 0;
        var startTime = DateTime.UtcNow;

        _logger.LogInformation("Render loop started: startTimestamp={StartMs}, pps={PPS}, interval={IntervalMs}ms",
            startTimestampMs, pixelsPerSecond, frameIntervalMs);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var loopStartTime = DateTime.UtcNow;

                // Calculate current timestamp
                var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
                var currentTimestampMs = startTimestampMs + (long)elapsedMs;

                // Update animation position
                _compositionRenderer.UpdateAnimationPosition(currentTimestampMs, pixelsPerSecond);

                // Compose and send frames for each screen
                foreach (var screen in _canvasManager!.ScreenMappings)
                {
                    if (!_playerHosts.TryGetValue(screen.Order, out var playerHost))
                    {
                        _logger.LogWarning("No player host for screen {Order}", screen.Order);
                        continue;
                    }

                    if (!playerHost.IsRunning)
                    {
                        _logger.LogWarning("Player host for screen {Order} not running", screen.Order);
                        continue;
                    }

                    try
                    {
                        // Compose frame
                        using var frame = _compositionRenderer.ComposeForScreen(screen);

                        // Encode to JPEG
                        var jpegData = _compositionRenderer.EncodeBitmapToJpeg(frame, _jpegQuality);

                        // Send to player
                        await playerHost.SetFrameAsync(jpegData);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error rendering frame for screen {Order}", screen.Order);
                    }
                }

                frameCount++;

                // Maintain target frame rate
                var frameTime = (DateTime.UtcNow - loopStartTime).TotalMilliseconds;
                var sleepTime = frameIntervalMs - frameTime;

                if (sleepTime > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(sleepTime), cancellationToken);
                }
                else if (frameTime > frameIntervalMs * 1.5)
                {
                    _logger.LogWarning("Frame rendering took {FrameTime}ms (target: {Target}ms)", frameTime, frameIntervalMs);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Render loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in render loop");
        }

        _logger.LogInformation("Render loop finished. Rendered {FrameCount} frames", frameCount);
    }

    /// <summary>
    /// Update animation position manually (if not using the render loop).
    /// </summary>
    public void UpdateAnimationPosition(long timestampMs, int pixelsPerSecond)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(D2DCompositionService));

        _compositionRenderer.UpdateAnimationPosition(timestampMs, pixelsPerSecond);
    }

    /// <summary>
    /// Compose and display a single frame manually (if not using the render loop).
    /// </summary>
    public async Task RenderSingleFrameAsync(long timestampMs, int pixelsPerSecond)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(D2DCompositionService));

        if (_canvasManager == null || _playerHosts.Count == 0)
        {
            throw new InvalidOperationException("Service not initialized");
        }

        // Update animation position
        _compositionRenderer.UpdateAnimationPosition(timestampMs, pixelsPerSecond);

        // Render for all screens
        foreach (var screen in _canvasManager.ScreenMappings)
        {
            if (!_playerHosts.TryGetValue(screen.Order, out var playerHost))
                continue;

            using var frame = _compositionRenderer.ComposeForScreen(screen);
            var jpegData = _compositionRenderer.EncodeBitmapToJpeg(frame, _jpegQuality);
            await playerHost.SetFrameAsync(jpegData);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _logger.LogInformation("Disposing D2D composition service");

        // Stop render loop
        if (_isRunning)
        {
            StopAsync().GetAwaiter().GetResult();
        }

        // Dispose all player hosts
        foreach (var playerHost in _playerHosts.Values)
        {
            playerHost.Dispose();
        }
        _playerHosts.Clear();

        // Dispose composition renderer
        _compositionRenderer?.Dispose();

        _disposed = true;

        _logger.LogInformation("D2D composition service disposed");
    }
}

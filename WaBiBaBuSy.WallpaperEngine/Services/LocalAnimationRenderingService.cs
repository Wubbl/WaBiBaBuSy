using System.Drawing;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Direct2D;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Services;

/// <summary>
/// Service for rendering animations locally using composition + Direct2D.
/// Separates the animation composition and rendering concerns for easier testing and switching.
///
/// This service:
/// 1. Takes background and animation configurations
/// 2. Uses ComposerService to compose background + animation into frames
/// 3. Uses Direct2DRenderer to display composed frames to screen
/// 4. Manages the lifecycle of both components
/// </summary>
public class LocalAnimationRenderingService : IDisposable
{
    private readonly ILogger<LocalAnimationRenderingService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DesktopWindowManager _desktopWindowManager;
    private readonly ComposerService? _composer;
    private readonly Direct2DRenderer? _renderer;
    private bool _disposed;

    // Render state
    private int _monitorIndex = 0;
    private long _startTimestampMs = 0;
    private int _pixelsPerSecond = 100;
    private System.Threading.Timer? _renderTimer;
    private const int RenderIntervalMs = 16; // ~60 FPS
    private readonly object _syncLock = new();

    // Diagnostics
    private int _frameCount = 0;
    private long _lastLogTimestampMs = 0;
    private const int LogIntervalMs = 1000; // Log stats every second

    // Screen configuration
    private VirtualCanvasManager? _canvasManager;
    private ScreenMapping? _screenMapping;

    public LocalAnimationRenderingService(
        ILogger<LocalAnimationRenderingService> logger,
        DesktopWindowManager desktopWindowManager,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _desktopWindowManager = desktopWindowManager ?? throw new ArgumentNullException(nameof(desktopWindowManager));

        // Initialize composer and renderer
        var compositionRenderer = new CompositionRenderer(
            _loggerFactory.CreateLogger<CompositionRenderer>(),
            _loggerFactory);

        _composer = new ComposerService(
            _loggerFactory.CreateLogger<ComposerService>(),
            compositionRenderer);

        _renderer = new Direct2DRenderer(
            _loggerFactory.CreateLogger<Direct2DRenderer>(),
            desktopWindowManager);
    }

    /// <summary>
    /// Initialize the rendering service with animation and background configurations.
    /// </summary>
    public async Task InitializeAsync(
        BackgroundLayerConfig backgroundConfig,
        AnimationLayerConfig animationConfig,
        int monitorIndex = 0)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LocalAnimationRenderingService));

        _logger.LogInformation("Initializing local animation rendering service for monitor {MonitorIndex}", monitorIndex);

        try
        {
            _monitorIndex = monitorIndex;

            // Get screen information
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (monitorIndex < 0 || monitorIndex >= screens.Length)
            {
                throw new InvalidOperationException($"Invalid monitor index {monitorIndex}. Available monitors: {screens.Length}");
            }

            var screen = screens[monitorIndex];
            _logger.LogInformation("Screen {MonitorIndex}: {Width}x{Height} at ({X}, {Y})",
                monitorIndex, screen.Bounds.Width, screen.Bounds.Height, screen.Bounds.X, screen.Bounds.Y);

            // Create virtual canvas manager for single screen
            _canvasManager = new VirtualCanvasManager(
                _loggerFactory.CreateLogger<VirtualCanvasManager>());

            // Configure layout for single screen
            var screenConfig = new ScreenConfiguration
            {
                ClientId = "LOCAL",
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height,
                Order = 0
            };

            _canvasManager.CalculateLayout(new[] { screenConfig });

            // Create screen mapping for the local monitor
            _screenMapping = new ScreenMapping
            {
                ClientId = "LOCAL",
                Order = 0,
                ScreenBounds = screen.Bounds,
                VirtualBounds = new System.Drawing.Rectangle(0, 0, screen.Bounds.Width, screen.Bounds.Height),
                Hostname = System.Net.Dns.GetHostName(),
                MonitorIndex = monitorIndex
            };

            // Initialize composer with configurations
            if (_composer == null)
                throw new InvalidOperationException("Composer not initialized");

            await _composer.InitializeAsync(_canvasManager, backgroundConfig, animationConfig);

            _startTimestampMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            _logger.LogInformation("Local animation rendering service initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing local animation rendering service");
            throw;
        }
    }

    /// <summary>
    /// Start rendering animation frames to screen.
    /// </summary>
    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LocalAnimationRenderingService));

        lock (_syncLock)
        {
            if (_renderTimer != null)
            {
                _logger.LogWarning("Rendering already in progress");
                return;
            }

            _logger.LogInformation("Starting local animation rendering");

            // Find and initialize WorkerW if not already done
            if (_desktopWindowManager.WorkerWHandle == IntPtr.Zero)
            {
                _desktopWindowManager.FindDesktopWorkerWindow();
            }

            _startTimestampMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

            // Start rendering timer
            _renderTimer = new System.Threading.Timer(RenderFrame, null, 0, RenderIntervalMs);
        }
    }

    /// <summary>
    /// Stop rendering and cleanup.
    /// </summary>
    public void Stop()
    {
        lock (_syncLock)
        {
            if (_renderTimer != null)
            {
                _renderTimer.Dispose();
                _renderTimer = null;
                _logger.LogInformation("Stopped local animation rendering");
            }
        }
    }

    /// <summary>
    /// Render a single frame (called by timer).
    /// </summary>
    private void RenderFrame(object? state)
    {
        if (_disposed || _composer == null || _renderer == null || _screenMapping == null)
        {
            _logger.LogTrace("RenderFrame aborted: disposed={Disposed}, composer={Composer}, renderer={Renderer}, mapping={Mapping}",
                _disposed, _composer != null, _renderer != null, _screenMapping != null);
            return;
        }

        try
        {
            lock (_syncLock)
            {
                // Calculate elapsed time since start
                var currentTimestampMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                var elapsedMs = currentTimestampMs - _startTimestampMs;

                _frameCount++;

                // Log diagnostic info every second
                if (currentTimestampMs - _lastLogTimestampMs >= LogIntervalMs)
                {
                    _logger.LogInformation("[RenderLoop] Rendered {FrameCount} frames in last {IntervalMs}ms, elapsed={ElapsedMs}ms, pixelsPerSecond={PixelsPerSecond}",
                        _frameCount, LogIntervalMs, elapsedMs, _pixelsPerSecond);
                    _frameCount = 0;
                    _lastLogTimestampMs = currentTimestampMs;
                }

                // Compose frame
                _logger.LogTrace("[Compose] Requesting frame at elapsedMs={ElapsedMs}, screenMapping={ScreenId}", elapsedMs, _screenMapping.ClientId);
                var frame = _composer.ComposeSingle(_screenMapping, elapsedMs, _pixelsPerSecond);

                if (frame == null)
                {
                    _logger.LogWarning("[Compose] ComposeSingle returned null frame at elapsedMs={ElapsedMs}", elapsedMs);
                    return;
                }

                _logger.LogTrace("[Compose] Received frame: {Width}x{Height}, disposing after display", frame.Width, frame.Height);

                // Render to screen
                _logger.LogTrace("[Display] Calling DisplayFrame for clientId=LOCAL");
                _renderer.DisplayFrame("LOCAL", frame, _screenMapping);
                _logger.LogTrace("[Display] DisplayFrame completed");

                // Frame is disposed by DisplayFrame
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RenderFrame] Error rendering frame");
        }
    }

    /// <summary>
    /// Set animation playback speed.
    /// </summary>
    public void SetPlaybackSpeed(int pixelsPerSecond)
    {
        _pixelsPerSecond = pixelsPerSecond;
        _logger.LogDebug("Playback speed set to {PixelsPerSecond} px/s", pixelsPerSecond);
    }

    /// <summary>
    /// Reset animation to starting position.
    /// </summary>
    public void Reset()
    {
        _composer?.ResetAnimation();
        _startTimestampMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("Animation reset");
    }

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("[Dispose] Starting disposal of local animation rendering service");
        var startTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        _logger.LogInformation("[Dispose] Stopping render timer");
        Stop();
        var afterStopTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] Timer stopped in {ElapsedMs}ms", afterStopTime - startTime);

        _logger.LogInformation("[Dispose] Disposing renderer");
        _renderer?.Dispose();
        var afterRendererTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] Renderer disposed in {ElapsedMs}ms", afterRendererTime - afterStopTime);

        _logger.LogInformation("[Dispose] Disposing composer");
        _composer?.Dispose();
        var afterComposerTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] Composer disposed in {ElapsedMs}ms", afterComposerTime - afterRendererTime);

        _disposed = true;
        var totalTime = afterComposerTime - startTime;
        _logger.LogInformation("[Dispose] TOTAL DISPOSAL TIME: {TotalMs}ms", totalTime);

        GC.SuppressFinalize(this);
    }
}

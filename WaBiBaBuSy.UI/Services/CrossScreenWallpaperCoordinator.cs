using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Composition;
using Timer = System.Threading.Timer;

namespace WaBiBaBuSy.UI.Services;

/// <summary>
/// Orchestrates cross-screen wallpaper synchronization with composition pipeline.
/// Coordinates background + animation layers and distributes frames to all connected clients.
/// </summary>
public class CrossScreenWallpaperCoordinator : IDisposable
{
    private readonly ILogger<CrossScreenWallpaperCoordinator> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly WallpaperSyncCoordinator _syncCoordinator;

    private VirtualCanvasManager? _canvasManager;
    private CompositionRenderer? _compositor;
    private CrossScreenConfig? _config;
    private Timer? _renderTimer;

    private bool _isRunning;
    private long _startTimestamp;
    private int _frameCount;
    private bool _disposed;

    // Performance metrics
    private readonly System.Diagnostics.Stopwatch _performanceTimer = new();
    private double _averageRenderTimeMs;

    public event EventHandler<CrossScreenStatusEventArgs>? StatusChanged;

    public CrossScreenWallpaperCoordinator(
        ILogger<CrossScreenWallpaperCoordinator> logger,
        ILoggerFactory loggerFactory,
        WallpaperSyncCoordinator syncCoordinator)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _syncCoordinator = syncCoordinator;
    }

    /// <summary>
    /// Initialize the cross-screen system with client configurations
    /// </summary>
    public async Task InitializeAsync(
        IEnumerable<ScreenConfiguration> screens,
        CrossScreenConfig config)
    {
        _logger.LogInformation("Initializing cross-screen wallpaper coordinator");

        _config = config;

        // Initialize virtual canvas
        _canvasManager = new VirtualCanvasManager(
            _loggerFactory.CreateLogger<VirtualCanvasManager>());
        _canvasManager.CalculateLayout(screens);

        if (_canvasManager.ScreenCount == 0)
        {
            throw new InvalidOperationException("No screens configured for cross-screen mode");
        }

        // Initialize compositor
        _compositor = new CompositionRenderer(
            _loggerFactory.CreateLogger<CompositionRenderer>(),
            _loggerFactory);

        await _compositor.InitializeAsync(
            _canvasManager,
            config.Background,
            config.Animation);

        _logger.LogInformation("Cross-screen coordinator initialized for {ScreenCount} screens",
            _canvasManager.ScreenCount);

        StatusChanged?.Invoke(this, new CrossScreenStatusEventArgs
        {
            Status = CrossScreenStatus.Initialized,
            Message = $"Initialized with {_canvasManager.ScreenCount} screens"
        });
    }

    /// <summary>
    /// Start the cross-screen wallpaper animation
    /// </summary>
    public Task StartAsync()
    {
        if (_isRunning)
        {
            _logger.LogWarning("Cross-screen coordinator already running");
            return Task.CompletedTask;
        }

        if (_config == null || _compositor == null || _canvasManager == null)
        {
            throw new InvalidOperationException("Coordinator not initialized. Call InitializeAsync first.");
        }

        _logger.LogInformation("Starting cross-screen wallpaper animation");

        _isRunning = true;
        _startTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _frameCount = 0;

        // Start render loop at 30 FPS (33ms per frame)
        var frameIntervalMs = 1000 / 30;
        _renderTimer = new Timer(OnRenderFrame, null, 0, frameIntervalMs);

        StatusChanged?.Invoke(this, new CrossScreenStatusEventArgs
        {
            Status = CrossScreenStatus.Running,
            Message = "Animation started"
        });

        _logger.LogInformation("Cross-screen animation started at 30 FPS");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the cross-screen wallpaper animation
    /// </summary>
    public Task StopAsync()
    {
        if (!_isRunning)
        {
            _logger.LogWarning("Cross-screen coordinator not running");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Stopping cross-screen wallpaper animation");

        _isRunning = false;
        _renderTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        StatusChanged?.Invoke(this, new CrossScreenStatusEventArgs
        {
            Status = CrossScreenStatus.Stopped,
            Message = "Animation stopped"
        });

        _logger.LogInformation("Cross-screen animation stopped. Total frames: {FrameCount}, Avg render time: {AvgMs:F2}ms",
            _frameCount, _averageRenderTimeMs);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Pause the animation (keeps current position)
    /// </summary>
    public Task PauseAsync()
    {
        if (!_isRunning)
            return Task.CompletedTask;

        _renderTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _isRunning = false;

        StatusChanged?.Invoke(this, new CrossScreenStatusEventArgs
        {
            Status = CrossScreenStatus.Paused,
            Message = "Animation paused"
        });

        _logger.LogInformation("Cross-screen animation paused");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reset animation to starting position
    /// </summary>
    public void Reset()
    {
        _compositor?.ResetAnimation();
        _startTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _frameCount = 0;

        _logger.LogInformation("Animation reset to starting position");
    }

    private void OnRenderFrame(object? state)
    {
        if (!_isRunning || _compositor == null || _canvasManager == null || _config == null)
            return;

        try
        {
            _performanceTimer.Restart();

            var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Compose frames for all screens
            var frames = _compositor.ComposeForAllScreens(
                currentTimestamp,
                _config.AnimationSpeedPxPerSecond);

            // Distribute frames to clients
            foreach (var (clientId, frameBitmap) in frames)
            {
                try
                {
                    // Encode frame to JPEG for network transmission
                    var frameData = _compositor.EncodeBitmapToJpeg(frameBitmap, quality: 90);

                    // Send frame via sync coordinator
                    _ = _syncCoordinator.SendCrossScreenFrameAsync(
                        clientId,
                        _frameCount,
                        currentTimestamp,
                        frameData,
                        frameBitmap.Width,
                        frameBitmap.Height);

                    _logger.LogTrace("Frame {FrameNum} sent to client {ClientId}: {Width}x{Height}, {Size} KB",
                        _frameCount, clientId, frameBitmap.Width, frameBitmap.Height, frameData.Length / 1024);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending frame to client {ClientId}", clientId);
                }
                finally
                {
                    // Always dispose frame bitmap
                    frameBitmap.Dispose();
                }
            }

            _frameCount++;
            _performanceTimer.Stop();

            // Update performance metrics
            var renderTimeMs = _performanceTimer.Elapsed.TotalMilliseconds;
            _averageRenderTimeMs = (_averageRenderTimeMs * (_frameCount - 1) + renderTimeMs) / _frameCount;

            // Log performance every 100 frames
            if (_frameCount % 100 == 0)
            {
                _logger.LogInformation("Performance: Frame {Count}, Render time: {Time:F2}ms, Avg: {Avg:F2}ms",
                    _frameCount, renderTimeMs, _averageRenderTimeMs);
            }

            // Check if animation needs to loop
            CheckAnimationLoop(currentTimestamp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rendering cross-screen frame");
        }
    }

    private void CheckAnimationLoop(long currentTimestamp)
    {
        if (_config == null || _canvasManager == null)
            return;

        // Calculate if animation has moved past the end of the virtual canvas
        var elapsedMs = currentTimestamp - _startTimestamp;
        var elapsedSeconds = elapsedMs / 1000.0;
        var currentPosition = (int)(elapsedSeconds * _config.AnimationSpeedPxPerSecond);

        // If animation has completely passed through, reset if looping is enabled
        if (currentPosition > _canvasManager.VirtualBounds.Width + 1000 && _config.Animation.Loop)
        {
            _logger.LogInformation("Animation loop detected, resetting position");
            Reset();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("Disposing cross-screen wallpaper coordinator");

        StopAsync().Wait();

        _renderTimer?.Dispose();
        _renderTimer = null;

        _compositor?.Dispose();
        _compositor = null;

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Status of the cross-screen system
/// </summary>
public enum CrossScreenStatus
{
    Uninitialized,
    Initialized,
    Running,
    Paused,
    Stopped,
    Error
}

/// <summary>
/// Event args for cross-screen status changes
/// </summary>
public class CrossScreenStatusEventArgs : EventArgs
{
    public required CrossScreenStatus Status { get; init; }
    public string? Message { get; init; }
    public Exception? Error { get; init; }
}

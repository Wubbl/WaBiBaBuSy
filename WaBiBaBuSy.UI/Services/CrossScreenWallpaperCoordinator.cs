using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Composition;

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
    private DispatcherTimer? _renderTimer;

    private bool _isRunning;
    private long _startTimestamp;
    private int _frameCount;
    private bool _disposed;

    // For local-only mode rendering
    public delegate Task LocalFrameHandler(Dictionary<string, Bitmap> frames, long timestamp);
    public event LocalFrameHandler? LocalFrameRendered;

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

        // Start render loop at 30 FPS (33ms per frame) using DispatcherTimer (UI thread)
        var frameIntervalMs = 1000 / 30;
        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(frameIntervalMs)
        };
        _renderTimer.Tick += (s, e) => OnRenderFrame(null);
        _renderTimer.Start();

        _logger.LogInformation("DispatcherTimer started with interval {IntervalMs}ms", frameIntervalMs);

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
        if (_renderTimer != null)
        {
            _renderTimer.Stop();
            _logger.LogInformation("DispatcherTimer stopped");
        }

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

        _renderTimer?.Stop();
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
        try
        {
            _logger.LogDebug("OnRenderFrame called - isRunning={IsRunning}", _isRunning);

            if (!_isRunning || _compositor == null || _canvasManager == null || _config == null)
            {
                _logger.LogWarning("OnRenderFrame: Early exit - isRunning={IsRunning}, compositor={Compositor}, canvasManager={Canvas}, config={Config}",
                    _isRunning, _compositor != null, _canvasManager != null, _config != null);
                return;
            }

            // For local-only mode, we just render frames but don't send them over network
            // A full local rendering implementation would apply frames directly to wallpaper
            // For now, we just log that we're rendering
            var isLocalOnlyMode = _syncCoordinator == null;

            _performanceTimer.Restart();

            var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _logger.LogDebug("Composing frames - timestamp={Timestamp}, speed={Speed}",
                currentTimestamp, _config.AnimationSpeedPxPerSecond);

            // Compose frames for all screens
            var frames = _compositor.ComposeForAllScreens(
                currentTimestamp,
                _config.AnimationSpeedPxPerSecond);

            _logger.LogDebug("Composition complete - frames generated: {Count}", frames?.Count ?? 0);

            if (frames == null || frames.Count == 0)
            {
                _logger.LogWarning("No frames generated from composition");
                return;
            }

            // If in local-only mode, raise event for UI layer to handle
            if (isLocalOnlyMode)
            {
                try
                {
                    _logger.LogTrace("Frame {FrameNum} composed for {Count} local screens", _frameCount, frames.Count);

                    // Raise event so UI layer can apply frames to wallpaper
                    LocalFrameRendered?.Invoke(frames, currentTimestamp);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error raising LocalFrameRendered event");
                }
                finally
                {
                    // Dispose all frames
                    foreach (var (_, frameBitmap) in frames)
                    {
                        frameBitmap?.Dispose();
                    }
                }
                _frameCount++;
                return;
            }

            // Distribute frames to clients over network
            foreach (var (clientId, frameBitmap) in frames)
            {
                try
                {
                    if (frameBitmap == null)
                    {
                        _logger.LogWarning("Null frame bitmap for client {ClientId}", clientId);
                        continue;
                    }

                    if (_syncCoordinator != null)
                    {
                        // Encode frame to JPEG for network transmission
                        var frameData = _compositor.EncodeBitmapToJpeg(frameBitmap, quality: 90);

                        if (frameData == null || frameData.Length == 0)
                        {
                            _logger.LogWarning("Failed to encode frame for client {ClientId}", clientId);
                            continue;
                        }

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
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending frame to client {ClientId}", clientId);
                }
                finally
                {
                    // Always dispose frame bitmap
                    frameBitmap?.Dispose();
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

        _renderTimer?.Stop();
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

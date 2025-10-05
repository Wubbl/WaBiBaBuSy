using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models;

namespace WaBiBaBuSy.Core.Services;

/// <summary>
/// Manages wallpaper playback in response to sync commands from server
/// </summary>
public class WallpaperPlaybackService : IDisposable
{
    private const int MAX_DRIFT_MS = 50; // Maximum allowed drift before correction
    private const int DRIFT_CHECK_INTERVAL_MS = 1000; // Check drift every second

    private readonly ILogger<WallpaperPlaybackService> _logger;
    private readonly WallpaperSyncClient _syncClient;
    private readonly Dictionary<string, IWallpaperRenderer> _renderers;
    private readonly Dictionary<string, string> _contentCache; // contentId -> local file path
    private readonly Func<string, IWallpaperRenderer?>? _rendererFactory;

    // Drift detection state
    private CancellationTokenSource? _driftMonitorCts;
    private Task? _driftMonitorTask;
    private string? _activeContentId;
    private long _playbackStartTimestamp; // UTC timestamp when playback started
    private long _initialPositionMs; // Initial position when playback started

    public WallpaperPlaybackService(
        ILogger<WallpaperPlaybackService> logger,
        WallpaperSyncClient syncClient,
        Func<string, IWallpaperRenderer?>? rendererFactory = null)
    {
        _logger = logger;
        _syncClient = syncClient;
        _rendererFactory = rendererFactory;
        _renderers = new Dictionary<string, IWallpaperRenderer>();
        _contentCache = new Dictionary<string, string>();

        // Subscribe to sync commands
        _syncClient.SyncCommandReceived += OnSyncCommandReceived;
    }

    /// <summary>
    /// Register a content file in the local cache
    /// </summary>
    public void RegisterContent(string contentId, string localFilePath)
    {
        _contentCache[contentId] = localFilePath;
        _logger.LogInformation("Registered content {ContentId} at {FilePath}", contentId, localFilePath);
    }

    /// <summary>
    /// Handle incoming sync command from server
    /// </summary>
    private void OnSyncCommandReceived(object? sender, SyncCommandReceivedEventArgs e)
    {
        var command = e.Command;
        _logger.LogInformation("Received {CommandType} command for content {ContentId}, sequence {SequenceNumber}",
            command.Type, command.ContentId, command.SequenceNumber);

        // Schedule command execution based on timestamp
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteCommandWithTimingAsync(command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing sync command");
            }
        });
    }

    /// <summary>
    /// Execute command at the specified timestamp
    /// </summary>
    private async Task ExecuteCommandWithTimingAsync(SyncCommand command)
    {
        // Calculate delay until execution time
        var targetTimestamp = command.TimestampUtc;
        var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var delayMs = targetTimestamp - currentTimestamp;

        if (delayMs > 0)
        {
            _logger.LogDebug("Waiting {DelayMs}ms before executing {CommandType} command",
                delayMs, command.Type);
            await Task.Delay((int)delayMs);
        }
        else if (delayMs < -1000) // More than 1 second late
        {
            _logger.LogWarning("Command {CommandType} is {DelayMs}ms late, executing immediately",
                command.Type, Math.Abs(delayMs));
        }

        // Execute the command
        await ExecuteCommandAsync(command);
    }

    /// <summary>
    /// Execute the sync command
    /// </summary>
    private async Task ExecuteCommandAsync(SyncCommand command)
    {
        switch (command.Type)
        {
            case CommandType.Load:
                await HandleLoadCommandAsync(command);
                break;

            case CommandType.Play:
                await HandlePlayCommandAsync(command);
                break;

            case CommandType.Pause:
                await HandlePauseCommandAsync(command);
                break;

            case CommandType.Stop:
                await HandleStopCommandAsync(command);
                break;

            case CommandType.Seek:
                await HandleSeekCommandAsync(command);
                break;

            default:
                _logger.LogWarning("Unknown command type: {CommandType}", command.Type);
                break;
        }
    }

    /// <summary>
    /// Handle LOAD command - initialize wallpaper renderer
    /// </summary>
    private async Task HandleLoadCommandAsync(SyncCommand command)
    {
        try
        {
            // Check if content is in cache
            if (!_contentCache.TryGetValue(command.ContentId, out var filePath))
            {
                _logger.LogError("Content {ContentId} not found in cache", command.ContentId);
                return;
            }

            _logger.LogInformation("Loading wallpaper: {FilePath}", filePath);

            // Create renderer using factory if provided
            IWallpaperRenderer? renderer = null;
            if (_rendererFactory != null)
            {
                renderer = _rendererFactory(filePath);
            }

            if (renderer == null)
            {
                _logger.LogWarning("No renderer factory configured or factory returned null for {FilePath}", filePath);
                return;
            }

            // Store renderer for this content
            _renderers[command.ContentId] = renderer;

            // Initialize renderer
            var config = new WallpaperConfig
            {
                FilePath = filePath,
                Loop = true
            };

            await renderer.InitializeAsync(config);
            _logger.LogInformation("Wallpaper loaded successfully: {ContentId}", command.ContentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading wallpaper");
        }
    }

    /// <summary>
    /// Handle PLAY command - start wallpaper playback
    /// </summary>
    private async Task HandlePlayCommandAsync(SyncCommand command)
    {
        try
        {
            if (!_renderers.TryGetValue(command.ContentId, out var renderer))
            {
                _logger.LogWarning("Renderer not found for content {ContentId}", command.ContentId);
                return;
            }

            _logger.LogInformation("Playing wallpaper: {ContentId}", command.ContentId);

            // Seek to target position if specified
            long initialPosition = 0;
            if (command.Params != null && command.Params.TargetPositionMs > 0)
            {
                initialPosition = command.Params.TargetPositionMs;
                await renderer.SeekAsync(TimeSpan.FromMilliseconds(initialPosition));
            }

            await renderer.StartAsync();

            // Start drift monitoring
            _activeContentId = command.ContentId;
            _playbackStartTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _initialPositionMs = initialPosition;
            StartDriftMonitoring();

            _logger.LogInformation("Wallpaper playback started: {ContentId}", command.ContentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing wallpaper");
        }
    }

    /// <summary>
    /// Handle PAUSE command - pause wallpaper playback
    /// </summary>
    private async Task HandlePauseCommandAsync(SyncCommand command)
    {
        try
        {
            if (!_renderers.TryGetValue(command.ContentId, out var renderer))
            {
                _logger.LogWarning("Renderer not found for content {ContentId}", command.ContentId);
                return;
            }

            _logger.LogInformation("Pausing wallpaper: {ContentId}", command.ContentId);

            // Stop drift monitoring when paused
            StopDriftMonitoring();

            await renderer.PauseAsync();
            _logger.LogInformation("Wallpaper paused: {ContentId}", command.ContentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing wallpaper");
        }
    }

    /// <summary>
    /// Handle STOP command - stop wallpaper playback
    /// </summary>
    private async Task HandleStopCommandAsync(SyncCommand command)
    {
        try
        {
            if (!_renderers.TryGetValue(command.ContentId, out var renderer))
            {
                _logger.LogWarning("Renderer not found for content {ContentId}", command.ContentId);
                return;
            }

            _logger.LogInformation("Stopping wallpaper: {ContentId}", command.ContentId);

            // Stop drift monitoring when stopped
            StopDriftMonitoring();

            await renderer.StopAsync();
            _logger.LogInformation("Wallpaper stopped: {ContentId}", command.ContentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping wallpaper");
        }
    }

    /// <summary>
    /// Handle SEEK command - seek to specific position
    /// </summary>
    private async Task HandleSeekCommandAsync(SyncCommand command)
    {
        try
        {
            if (!_renderers.TryGetValue(command.ContentId, out var renderer))
            {
                _logger.LogWarning("Renderer not found for content {ContentId}", command.ContentId);
                return;
            }

            if (command.Params == null || command.Params.TargetPositionMs <= 0)
            {
                _logger.LogWarning("Invalid seek position for content {ContentId}", command.ContentId);
                return;
            }

            _logger.LogInformation("Seeking wallpaper {ContentId} to {PositionMs}ms",
                command.ContentId, command.Params.TargetPositionMs);

            await renderer.SeekAsync(TimeSpan.FromMilliseconds(command.Params.TargetPositionMs));
            _logger.LogInformation("Wallpaper seeked: {ContentId}", command.ContentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeking wallpaper");
        }
    }

    /// <summary>
    /// Start background task to monitor playback drift
    /// </summary>
    private void StartDriftMonitoring()
    {
        // Stop any existing monitoring task
        StopDriftMonitoring();

        _driftMonitorCts = new CancellationTokenSource();
        _driftMonitorTask = Task.Run(async () => await MonitorDriftAsync(_driftMonitorCts.Token));

        _logger.LogDebug("Started drift monitoring for content {ContentId}", _activeContentId);
    }

    /// <summary>
    /// Stop drift monitoring task
    /// </summary>
    private void StopDriftMonitoring()
    {
        if (_driftMonitorCts != null)
        {
            _driftMonitorCts.Cancel();
            _driftMonitorCts.Dispose();
            _driftMonitorCts = null;
        }

        _driftMonitorTask = null;
        _activeContentId = null;

        _logger.LogDebug("Stopped drift monitoring");
    }

    /// <summary>
    /// Background task that monitors playback position and corrects drift
    /// </summary>
    private async Task MonitorDriftAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(DRIFT_CHECK_INTERVAL_MS, cancellationToken);

                if (_activeContentId == null || !_renderers.TryGetValue(_activeContentId, out var renderer))
                {
                    continue;
                }

                // Calculate expected position based on elapsed time
                var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var elapsedMs = currentTimestamp - _playbackStartTimestamp;
                var expectedPositionMs = _initialPositionMs + elapsedMs;

                // Get actual position from renderer
                var actualPositionMs = renderer.PositionMs;

                // Calculate drift
                var driftMs = Math.Abs(expectedPositionMs - actualPositionMs);

                if (driftMs > MAX_DRIFT_MS)
                {
                    _logger.LogWarning(
                        "Playback drift detected: {DriftMs}ms (expected: {ExpectedPos}ms, actual: {ActualPos}ms). Correcting...",
                        driftMs, expectedPositionMs, actualPositionMs);

                    // Perform micro-seek to correct drift
                    await renderer.SeekAsync(TimeSpan.FromMilliseconds(expectedPositionMs));

                    _logger.LogInformation("Drift corrected by seeking to {Position}ms", expectedPositionMs);
                }
                else
                {
                    _logger.LogDebug(
                        "Playback drift: {DriftMs}ms (within tolerance of {MaxDrift}ms)",
                        driftMs, MAX_DRIFT_MS);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
            _logger.LogDebug("Drift monitoring cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in drift monitoring task");
        }
    }

    public void Dispose()
    {
        _logger.LogInformation("Disposing WallpaperPlaybackService");

        // Stop drift monitoring
        StopDriftMonitoring();

        // Unsubscribe from events
        _syncClient.SyncCommandReceived -= OnSyncCommandReceived;

        // Dispose all renderers
        foreach (var renderer in _renderers.Values)
        {
            renderer.Dispose();
        }

        _renderers.Clear();
        _contentCache.Clear();
    }
}

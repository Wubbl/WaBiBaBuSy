using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models;
using WaBiBaBuSy.Models.Wallpaper;

using WaBiBaBuSy.Models.Content;

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
    // Multi-monitor support: ConcurrentDictionary<contentId, ConcurrentDictionary<monitorIndex, renderer>>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, IWallpaperRenderer>> _renderers;
    private readonly ConcurrentDictionary<string, string> _contentCache; // contentId -> local file path (thread-safe)
    private readonly Func<string, int, IWallpaperRenderer?>? _rendererFactory; // Updated to take monitorIndex
    private readonly string _cacheDirectory;
    private readonly ContentCacheManager? _cacheManager;

    // Tier 1.3 prefetch progress for the last PREFETCH list (reported via heartbeat)
    private volatile int _prefetchReady;
    private volatile int _prefetchTotal;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);   // one download at a time per client

    /// <summary>
    /// Delegate for D2D rendering: (filePath, monitorIndex, backgroundColor, fitMode) → Task
    /// Set by the UI layer to enable D2D composition rendering on this client.
    /// </summary>
    public Func<string, int, string, int, Task>? D2DApplyDelegate { get; set; }

    /// <summary>
    /// Delegate for cross-screen D2D rendering. Receives the full request
    /// (canvas geometry, shared timestamp, movement/animation/background JSON).
    /// Set by the UI layer to enable synchronized cross-screen D2D animation on this client.
    /// </summary>
    public Func<CrossScreenApplyRequest, Task>? D2DCrossScreenApplyDelegate { get; set; }

    /// <summary>
    /// Delegate invoked when the server sends a Stop command that targets cross-screen D2D content.
    /// The UI layer sets this to tear down _remoteD2DServices on the client side.
    /// </summary>
    public Func<Task>? D2DCrossScreenStopDelegate { get; set; }

    // Drift detection state
    private CancellationTokenSource? _driftMonitorCts;
    private Task? _driftMonitorTask;
    private string? _activeContentId;
    private long _playbackStartTimestamp; // UTC timestamp when playback started
    private long _initialPositionMs; // Initial position when playback started

    public WallpaperPlaybackService(
        ILogger<WallpaperPlaybackService> logger,
        WallpaperSyncClient syncClient,
        Func<string, int, IWallpaperRenderer?>? rendererFactory = null,
        string? cacheDirectory = null,
        ContentCacheManager? cacheManager = null)
    {
        _logger = logger;
        _syncClient = syncClient;
        _rendererFactory = rendererFactory;
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WaBiBaBuSy", "Cache");
        _cacheManager = cacheManager;
        _renderers = new ConcurrentDictionary<string, ConcurrentDictionary<int, IWallpaperRenderer>>();
        // Persistent index: a client restart must not re-download files that are still on disk.
        _contentCache = new ConcurrentDictionary<string, string>(ContentCacheIndex.Load(_cacheDirectory));
        if (_contentCache.Count > 0)
            _logger.LogInformation("Content cache index restored: {Count} file(s)", _contentCache.Count);

        _syncClient.PrefetchStatusProvider = () => (_prefetchReady, _prefetchTotal);

        // Subscribe to sync commands
        _syncClient.SyncCommandReceived += OnSyncCommandReceived;
    }

    /// <summary>
    /// Register a content file in the local cache
    /// </summary>
    public void RegisterContent(string contentId, string localFilePath)
    {
        _contentCache[contentId] = localFilePath;
        SaveCacheIndex();
        _logger.LogInformation("Registered content {ContentId} at {FilePath}", contentId, localFilePath);
    }

    private void SaveCacheIndex()
    {
        try { ContentCacheIndex.Save(_cacheDirectory, new Dictionary<string, string>(_contentCache)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not persist the content cache index"); }
    }

    /// <summary>
    /// Local path of a content id, downloading it first when it is not cached. Null when the
    /// download failed. Downloads are serialized per client so a PREFETCH cannot starve a LOAD.
    /// </summary>
    private async Task<string?> EnsureCachedAsync(string contentId)
    {
        if (_contentCache.TryGetValue(contentId, out var cached) && File.Exists(cached))
        {
            _cacheManager?.TouchFile(cached);
            return cached;
        }

        await _downloadGate.WaitAsync();
        try
        {
            if (_contentCache.TryGetValue(contentId, out cached) && File.Exists(cached)) return cached;
            _cacheManager?.EnsureSpace(0);
            var path = await _syncClient.DownloadContentAsync(contentId, _cacheDirectory);
            if (path == null) return null;
            _contentCache[contentId] = path;
            SaveCacheIndex();
            return path;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <summary>
    /// PREFETCH: cache every listed asset in the background and expose progress for the heartbeat.
    /// The server waits (bounded) for all nodes to report ready before starting a show.
    /// </summary>
    private async Task HandlePrefetchAsync(SyncCommand command)
    {
        var assets = command.Params?.Assets;
        if (assets == null || assets.Count == 0) return;

        _prefetchTotal = assets.Count;
        _prefetchReady = assets.Count(a => _contentCache.TryGetValue(a.ContentId, out var p) && File.Exists(p));
        _logger.LogInformation("[Playback:PREFETCH] {Ready}/{Total} already cached", _prefetchReady, _prefetchTotal);

        foreach (var asset in assets)
        {
            if (_contentCache.TryGetValue(asset.ContentId, out var p) && File.Exists(p)) continue;
            var path = await EnsureCachedAsync(asset.ContentId);
            if (path != null) _prefetchReady++;
            else _logger.LogWarning("[Playback:PREFETCH] Could not fetch {ContentId}", asset.ContentId);
        }
        _logger.LogInformation("[Playback:PREFETCH] done: {Ready}/{Total}", _prefetchReady, _prefetchTotal);
    }

    /// <summary>
    /// Handle incoming sync command from server
    /// </summary>
    private void OnSyncCommandReceived(object? sender, SyncCommandReceivedEventArgs e)
    {
        var command = e.Command;
        _logger.LogInformation("[Playback] === COMMAND RECEIVED === Type={CommandType}, ContentId={ContentId}, Seq={SequenceNumber}",
            command.Type, command.ContentId, command.SequenceNumber);
        _logger.LogInformation("[Playback] D2DApplyDelegate is {Status}", D2DApplyDelegate != null ? "SET" : "NULL (no D2D rendering possible)");
        _logger.LogInformation("[Playback] RendererFactory is {Status}", _rendererFactory != null ? "SET" : "NULL");

        // Schedule command execution based on timestamp
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteCommandWithTimingAsync(command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Playback] Error executing sync command: {Message}", ex.Message);
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

            case CommandType.Prefetch:
                await HandlePrefetchAsync(command);
                break;

            default:
                _logger.LogWarning("Unknown command type: {CommandType}", command.Type);
                break;
        }
    }

    /// <summary>
    /// Handle LOAD command - download content if needed, then render via D2D or LibVLC
    /// </summary>
    private async Task HandleLoadCommandAsync(SyncCommand command)
    {
        try
        {
            _logger.LogInformation("[Playback:LOAD] === HANDLING LOAD === ContentId={ContentId}", command.ContentId);

            // Check if content is in cache, auto-download if not (index is persistent; a restart
            // does not re-download). Downloads are serialized with any running prefetch.
            bool wasCached = _contentCache.TryGetValue(command.ContentId, out var cachedPath) && File.Exists(cachedPath);
            var filePath = await EnsureCachedAsync(command.ContentId);
            if (filePath == null)
            {
                _logger.LogError("[Playback:LOAD] FAILED to download content {ContentId} from server", command.ContentId);
                return;
            }
            if (!wasCached)
            {
                _logger.LogInformation("[Playback:LOAD] Content {ContentId} downloaded and cached at {FilePath}", command.ContentId, filePath);
            }
            else
            {
                _logger.LogInformation("[Playback:LOAD] Content {ContentId} found in cache at {FilePath}", command.ContentId, filePath);
                _cacheManager?.TouchFile(filePath);
            }

            // Verify file exists on disk
            if (!File.Exists(filePath))
            {
                _logger.LogError("[Playback:LOAD] ERROR: Cached file does not exist on disk: {FilePath}", filePath);
                _contentCache.TryRemove(command.ContentId, out _);
                return;
            }
            _logger.LogInformation("[Playback:LOAD] File verified on disk: {FilePath} ({Size} bytes)", filePath, new FileInfo(filePath).Length);

            var rendererType = command.Params?.RendererType ?? string.Empty;
            var bgColor = command.Params?.BackgroundColor ?? "#000000";
            var fitMode = command.Params?.FitMode ?? 0;
            int monitorIndex = 0; // Default to primary monitor

            _logger.LogInformation("[Playback:LOAD] Params: renderer={Renderer}, bg={BgColor}, fit={FitMode}, monitor={Monitor}",
                rendererType, bgColor, fitMode, monitorIndex);
            _logger.LogInformation("[Playback:LOAD] D2DApplyDelegate={D2DStatus}, RendererFactory={FactoryStatus}",
                D2DApplyDelegate != null ? "SET" : "NULL",
                _rendererFactory != null ? "SET" : "NULL");

            // Use cross-screen D2D renderer if requested
            if (rendererType == "d2d_crossscreen")
            {
                if (D2DCrossScreenApplyDelegate != null)
                {
                    // Tier 1.3: fetch every referenced asset (additional images, background image)
                    // before applying, and hand the UI a map from server paths to local files.
                    var assetRefs = new List<ContentAssetRef>();
                    var localAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (command.Params?.Assets != null)
                    {
                        foreach (var a in command.Params.Assets)
                        {
                            assetRefs.Add(new ContentAssetRef { ContentId = a.ContentId, Role = a.Role, OriginalPath = a.OriginalPath });
                            var local = a.ContentId == command.ContentId ? filePath : await EnsureCachedAsync(a.ContentId);
                            if (local != null && !string.IsNullOrEmpty(a.OriginalPath)) localAssets[a.OriginalPath] = local;
                            else if (local == null) _logger.LogWarning("[Playback:LOAD] Asset {ContentId} ({Role}) unavailable — scene degrades", a.ContentId, a.Role);
                        }
                    }

                    var request = new CrossScreenApplyRequest
                    {
                        FilePath = filePath,
                        Assets = assetRefs,
                        LocalAssetPaths = localAssets,
                        MonitorIndex = command.Params?.TargetMonitorIndex ?? 0,
                        BackgroundColor = bgColor,
                        FitMode = fitMode,
                        VirtualCanvasWidth = command.Params?.VirtualCanvasWidth ?? 1920,
                        MonitorOffsetX = command.Params?.MonitorOffsetX ?? 0,
                        VirtualCanvasHeight = command.Params?.VirtualCanvasHeight ?? 0,
                        MonitorOffsetY = command.Params?.MonitorOffsetY ?? 0,
                        NodeOrder = command.Params?.NodeOrder ?? 0,
                        SharedStartTimestampMs = command.Params?.SharedStartTimestampMs ?? 0L,
                        PixelsPerSecond = command.Params?.PixelsPerSecond ?? 0,
                        PerMonitorMode = command.Params?.PerMonitorMode ?? false,
                        MovementType = command.Params?.MovementType ?? 0,
                        PatternJson = command.Params?.PatternJson ?? string.Empty,
                        ColorGradingJson = command.Params?.ColorGradingJson ?? string.Empty,
                        MovementJson = command.Params?.MovementJson ?? string.Empty,
                        AnimationJson = command.Params?.AnimationJson ?? string.Empty,
                        BackgroundJson = command.Params?.BackgroundJson ?? string.Empty,
                        LayoutJson = command.Params?.LayoutJson ?? string.Empty
                    };
                    _logger.LogInformation(
                        "[Playback:LOAD] Cross-screen D2D: canvas={VCW}px, offset={Offset}px, ts={Ts}ms, speed={Speed}px/s, perMonitor={PerMonitor}, movType={MovType}, monitor={Monitor}, hasMovementJson={HasMov}, hasAnimJson={HasAnim}, hasBgJson={HasBg}",
                        request.VirtualCanvasWidth, request.MonitorOffsetX, request.SharedStartTimestampMs,
                        request.PixelsPerSecond, request.PerMonitorMode, request.MovementType, request.MonitorIndex,
                        request.MovementJson.Length > 0, request.AnimationJson.Length > 0, request.BackgroundJson.Length > 0);
                    await D2DCrossScreenApplyDelegate(request);
                    _logger.LogInformation("[Playback:LOAD] Cross-screen D2D applied: {ContentId}", command.ContentId);
                }
                else
                {
                    _logger.LogWarning("[Playback:LOAD] d2d_crossscreen requested but D2DCrossScreenApplyDelegate is NULL - falling back to plain D2D");
                    if (D2DApplyDelegate != null)
                        await D2DApplyDelegate(filePath, monitorIndex, bgColor, fitMode);
                }
                return;
            }

            // Use D2D renderer if requested and delegate is available
            if (rendererType == "d2d" && D2DApplyDelegate != null)
            {
                _logger.LogInformation("[Playback:LOAD] Using D2D path: applying on monitor {Monitor} (bg={BgColor}, fit={FitMode})",
                    monitorIndex, bgColor, fitMode);
                await D2DApplyDelegate(filePath, monitorIndex, bgColor, fitMode);
                _logger.LogInformation("[Playback:LOAD] D2D wallpaper applied successfully: {ContentId}", command.ContentId);
                return;
            }
            else if (rendererType == "d2d" && D2DApplyDelegate == null)
            {
                _logger.LogWarning("[Playback:LOAD] D2D renderer requested but D2DApplyDelegate is NULL - delegate was never wired! Falling back to LibVLC");
            }

            // Fallback: LibVLC renderer
            _logger.LogInformation("[Playback:LOAD] Using LibVLC path on monitor {Monitor}", monitorIndex);

            _renderers.GetOrAdd(command.ContentId, new ConcurrentDictionary<int, IWallpaperRenderer>());

            IWallpaperRenderer? renderer = null;
            if (_rendererFactory != null)
            {
                renderer = _rendererFactory(filePath, monitorIndex);
            }

            if (renderer == null)
            {
                _logger.LogWarning("No renderer factory configured or factory returned null for {FilePath} on monitor {Monitor}",
                    filePath, monitorIndex);
                return;
            }

            _renderers[command.ContentId][monitorIndex] = renderer;

            var config = new WallpaperConfig
            {
                FilePath = filePath,
                Loop = true,
                MonitorIndex = monitorIndex
            };

            await renderer.InitializeAsync(config);
            await renderer.StartAsync();
            _logger.LogInformation("LibVLC wallpaper loaded and started: {ContentId} on monitor {Monitor}",
                command.ContentId, monitorIndex);

            _activeContentId = command.ContentId;
            _playbackStartTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _initialPositionMs = 0;
            StartDriftMonitoring();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading wallpaper");
        }
    }

    /// <summary>
    /// Handle PLAY command - start wallpaper playback on all loaded monitors
    /// </summary>
    private async Task HandlePlayCommandAsync(SyncCommand command)
    {
        try
        {
            if (!_renderers.TryGetValue(command.ContentId, out var monitorRenderers))
            {
                _logger.LogWarning("No renderers found for content {ContentId}", command.ContentId);
                return;
            }

            _logger.LogInformation("Playing wallpaper: {ContentId} on {Count} monitor(s)",
                command.ContentId, monitorRenderers.Count);

            // Seek to target position if specified
            long initialPosition = 0;
            if (command.Params != null && command.Params.TargetPositionMs > 0)
            {
                initialPosition = command.Params.TargetPositionMs;
            }

            // Start playback on all monitors synchronously
            foreach (var (monitorIndex, renderer) in monitorRenderers)
            {
                if (initialPosition > 0)
                {
                    await renderer.SeekAsync(TimeSpan.FromMilliseconds(initialPosition));
                }
                await renderer.StartAsync();
                _logger.LogDebug("Started playback on monitor {Monitor}", monitorIndex);
            }

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
    /// Handle PAUSE command - pause wallpaper playback on all monitors
    /// </summary>
    private async Task HandlePauseCommandAsync(SyncCommand command)
    {
        try
        {
            if (!_renderers.TryGetValue(command.ContentId, out var monitorRenderers))
            {
                _logger.LogWarning("No renderers found for content {ContentId}", command.ContentId);
                return;
            }

            _logger.LogInformation("Pausing wallpaper: {ContentId}", command.ContentId);

            // Stop drift monitoring when paused
            StopDriftMonitoring();

            // Pause all monitors
            foreach (var (monitorIndex, renderer) in monitorRenderers)
            {
                await renderer.PauseAsync();
                _logger.LogDebug("Paused playback on monitor {Monitor}", monitorIndex);
            }

            _logger.LogInformation("Wallpaper paused: {ContentId}", command.ContentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing wallpaper");
        }
    }

    /// <summary>
    /// Handle STOP command - stop wallpaper playback on all monitors
    /// </summary>
    private async Task HandleStopCommandAsync(SyncCommand command)
    {
        try
        {
            // Stop cross-screen D2D services on the client (tracked in ViewModel, not in _renderers)
            if (D2DCrossScreenStopDelegate != null)
            {
                await D2DCrossScreenStopDelegate();
                _logger.LogInformation("Cross-screen D2D stopped via delegate: {ContentId}", command.ContentId);
            }

            if (!_renderers.TryGetValue(command.ContentId, out var monitorRenderers))
            {
                _logger.LogInformation("No renderer-based content found for {ContentId} (may have been cross-screen D2D only)", command.ContentId);
                return;
            }

            _logger.LogInformation("Stopping wallpaper: {ContentId}", command.ContentId);

            StopDriftMonitoring();

            foreach (var (monitorIndex, renderer) in monitorRenderers)
            {
                await renderer.StopAsync();
                _logger.LogDebug("Stopped playback on monitor {Monitor}", monitorIndex);
            }

            _logger.LogInformation("Wallpaper stopped: {ContentId}", command.ContentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping wallpaper");
        }
    }

    /// <summary>
    /// Handle SEEK command - seek to specific position on all monitors
    /// </summary>
    private async Task HandleSeekCommandAsync(SyncCommand command)
    {
        try
        {
            if (!_renderers.TryGetValue(command.ContentId, out var monitorRenderers))
            {
                _logger.LogWarning("No renderers found for content {ContentId}", command.ContentId);
                return;
            }

            if (command.Params == null || command.Params.TargetPositionMs <= 0)
            {
                _logger.LogWarning("Invalid seek position for content {ContentId}", command.ContentId);
                return;
            }

            _logger.LogInformation("Seeking wallpaper {ContentId} to {PositionMs}ms",
                command.ContentId, command.Params.TargetPositionMs);

            // Seek all monitors
            foreach (var (monitorIndex, renderer) in monitorRenderers)
            {
                await renderer.SeekAsync(TimeSpan.FromMilliseconds(command.Params.TargetPositionMs));
                _logger.LogDebug("Seeked monitor {Monitor} to {Position}ms", monitorIndex, command.Params.TargetPositionMs);
            }

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
    /// Background task that monitors playback position and corrects drift across all monitors
    /// </summary>
    private async Task MonitorDriftAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(DRIFT_CHECK_INTERVAL_MS, cancellationToken);

                if (_activeContentId == null || !_renderers.TryGetValue(_activeContentId, out var monitorRenderers))
                {
                    continue;
                }

                // Calculate expected position based on elapsed time
                var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var elapsedMs = currentTimestamp - _playbackStartTimestamp;
                var expectedPositionMs = _initialPositionMs + elapsedMs;

                // Check drift on all monitors and correct if needed
                foreach (var (monitorIndex, renderer) in monitorRenderers)
                {
                    // Get actual position from renderer
                    var actualPositionMs = renderer.PositionMs;

                    // Calculate drift
                    var driftMs = Math.Abs(expectedPositionMs - actualPositionMs);

                    if (driftMs > MAX_DRIFT_MS)
                    {
                        _logger.LogWarning(
                            "Monitor {Monitor} drift detected: {DriftMs}ms (expected: {ExpectedPos}ms, actual: {ActualPos}ms). Correcting...",
                            monitorIndex, driftMs, expectedPositionMs, actualPositionMs);

                        // Perform micro-seek to correct drift
                        await renderer.SeekAsync(TimeSpan.FromMilliseconds(expectedPositionMs));

                        _logger.LogInformation("Monitor {Monitor} drift corrected by seeking to {Position}ms",
                            monitorIndex, expectedPositionMs);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Monitor {Monitor} playback drift: {DriftMs}ms (within tolerance of {MaxDrift}ms)",
                            monitorIndex, driftMs, MAX_DRIFT_MS);
                    }
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

        // Dispose all renderers across all monitors
        foreach (var monitorRenderers in _renderers.Values)
        {
            foreach (var renderer in monitorRenderers.Values)
            {
                renderer.Dispose();
            }
        }

        _renderers.Clear();
        _contentCache.Clear();
    }
}

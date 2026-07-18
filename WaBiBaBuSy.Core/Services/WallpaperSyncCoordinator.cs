using System.Text.Json;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Grpc.Services;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Core.Services;

/// <summary>
/// Coordinates wallpaper synchronization across multiple clients with physical distance-based delays
/// </summary>
public class WallpaperSyncCoordinator
{
    private readonly ILogger<WallpaperSyncCoordinator> _logger;
    private readonly WallpaperSyncService? _syncService;
    private int _sequenceNumber = 0;

    /// <summary>
    /// Last cross-screen D2D command sent per client. Re-sent when the client
    /// re-registers after a connection loss so it rejoins the running animation
    /// (deterministic epoch back-dating lands it at the correct position).
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SyncCommand> _activeCrossScreenCommands = new();

    /// <summary>
    /// Default animation speed in cm/s (can be adjusted based on visual effect desired)
    /// For example: 100 cm/s = 1 meter per second
    /// </summary>
    public int DefaultAnimationSpeedCmPerSec { get; set; } = 100;

    public WallpaperSyncCoordinator(
        ILogger<WallpaperSyncCoordinator> logger,
        WallpaperSyncService? syncService = null)
    {
        _logger = logger;
        _syncService = syncService;

        if (_syncService != null)
            _syncService.ClientRegistered += OnClientRegistered;
    }

    /// <summary>
    /// Resume an active cross-screen animation on a client that just (re-)registered.
    /// The SyncStream opens shortly after registration, so retry until it exists.
    /// </summary>
    private async void OnClientRegistered(object? sender, string clientId)
    {
        try
        {
            if (!_activeCrossScreenCommands.TryGetValue(clientId, out var command))
                return;

            for (int attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(500);
                if (_syncService != null && await _syncService.SendCommandToClientAsync(clientId, command))
                {
                    _logger.LogInformation("Resumed active cross-screen animation on reconnected client {ClientId}", clientId);
                    return;
                }
            }
            _logger.LogWarning("Could not resume animation on reconnected client {ClientId} — sync stream never came up", clientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming animation on reconnected client {ClientId}", clientId);
        }
    }

    /// <summary>
    /// Broadcast a wallpaper load command to all clients with calculated delays
    /// </summary>
    public async Task BroadcastLoadWallpaperAsync(
        string contentId,
        string filePath,
        int? customAnimationSpeed = null)
    {
        if (_syncService == null)
        {
            _logger.LogWarning("Cannot broadcast - sync service not available");
            return;
        }

        var animationSpeed = customAnimationSpeed ?? DefaultAnimationSpeedCmPerSec;
        var clients = _syncService.GetConnectedClients()
            .OrderBy(c => c.OrderPosition)
            .ToList();

        if (!clients.Any())
        {
            _logger.LogWarning("No connected clients to broadcast to");
            return;
        }

        _logger.LogInformation("Broadcasting LOAD command for content {ContentId} to {Count} clients with animation speed {Speed} cm/s",
            contentId, clients.Count, animationSpeed);

        // Register content so clients can download it
        _syncService.RegisterContent(contentId, filePath);

        var baseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sequenceNum = System.Threading.Interlocked.Increment(ref _sequenceNumber);

        // Calculate cumulative delay for each client based on physical distances
        long cumulativeDelayMs = 0;

        foreach (var client in clients)
        {
            // Calculate delay for this client
            var delayMs = CalculateDelayMs(client.PhysicalDistanceCm, animationSpeed);
            cumulativeDelayMs += delayMs;

            var command = new SyncCommand
            {
                Type = CommandType.Load,
                TimestampUtc = baseTimestamp + cumulativeDelayMs,
                SequenceNumber = sequenceNum,
                ContentId = contentId,
                Params = new SyncParameters
                {
                    AnimationSpeedCmPerSec = animationSpeed,
                    CalculatedDelayMs = cumulativeDelayMs,
                    Loop = true
                }
            };

            _logger.LogDebug("Client {ClientId} (position {Position}, distance {Distance}cm): delay = {Delay}ms, execute at {ExecuteTime}",
                client.ClientId, client.OrderPosition, client.PhysicalDistanceCm, cumulativeDelayMs,
                DateTimeOffset.FromUnixTimeMilliseconds(command.TimestampUtc).ToString("HH:mm:ss.fff"));

            // Send command to specific client via streaming
            await _syncService.SendCommandToClientAsync(client.ClientId, command);
        }

        _logger.LogInformation("Broadcast complete. Total animation duration: {Duration}ms", cumulativeDelayMs);
    }

    /// <summary>
    /// Broadcast a PLAY command to all clients
    /// </summary>
    public async Task BroadcastPlayAsync(string contentId, int? customAnimationSpeed = null)
    {
        if (_syncService == null) return;

        var animationSpeed = customAnimationSpeed ?? DefaultAnimationSpeedCmPerSec;
        var clients = _syncService.GetConnectedClients()
            .OrderBy(c => c.OrderPosition)
            .ToList();

        if (!clients.Any()) return;

        _logger.LogInformation("Broadcasting PLAY command for content {ContentId}", contentId);

        var baseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sequenceNum = System.Threading.Interlocked.Increment(ref _sequenceNumber);
        long cumulativeDelayMs = 0;

        foreach (var client in clients)
        {
            var delayMs = CalculateDelayMs(client.PhysicalDistanceCm, animationSpeed);
            cumulativeDelayMs += delayMs;

            var command = new SyncCommand
            {
                Type = CommandType.Play,
                TimestampUtc = baseTimestamp + cumulativeDelayMs,
                SequenceNumber = sequenceNum,
                ContentId = contentId,
                Params = new SyncParameters
                {
                    AnimationSpeedCmPerSec = animationSpeed,
                    CalculatedDelayMs = cumulativeDelayMs,
                    TargetPositionMs = 0 // Start from beginning
                }
            };

            // Send command to specific client via streaming
            await _syncService.SendCommandToClientAsync(client.ClientId, command);
        }

        _logger.LogInformation("PLAY broadcast complete");
    }

    /// <summary>
    /// Broadcast a PAUSE command to all clients simultaneously
    /// </summary>
    public async Task BroadcastPauseAsync(string contentId)
    {
        if (_syncService == null) return;

        var clients = _syncService.GetConnectedClients().ToList();
        if (!clients.Any()) return;

        _logger.LogInformation("Broadcasting PAUSE command for content {ContentId}", contentId);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sequenceNum = System.Threading.Interlocked.Increment(ref _sequenceNumber);

        foreach (var client in clients)
        {
            var command = new SyncCommand
            {
                Type = CommandType.Pause,
                TimestampUtc = timestamp,
                SequenceNumber = sequenceNum,
                ContentId = contentId,
                Params = new SyncParameters()
            };

            // Send command to specific client via streaming
            await _syncService.SendCommandToClientAsync(client.ClientId, command);
        }

        _logger.LogInformation("PAUSE broadcast complete");
    }

    /// <summary>
    /// Broadcast a STOP command to all clients
    /// </summary>
    public async Task BroadcastStopAsync(string contentId)
    {
        if (_syncService == null) return;

        var clients = _syncService.GetConnectedClients().ToList();
        if (!clients.Any()) return;

        _logger.LogInformation("Broadcasting STOP command for content {ContentId}", contentId);

        _activeCrossScreenCommands.Clear();

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sequenceNum = System.Threading.Interlocked.Increment(ref _sequenceNumber);

        foreach (var client in clients)
        {
            var command = new SyncCommand
            {
                Type = CommandType.Stop,
                TimestampUtc = timestamp,
                SequenceNumber = sequenceNum,
                ContentId = contentId,
                Params = new SyncParameters()
            };

            // Send command to specific client via streaming
            await _syncService.SendCommandToClientAsync(client.ClientId, command);
        }

        _logger.LogInformation("STOP broadcast complete");
    }

    /// <summary>
    /// Calculate delay in milliseconds based on physical distance and animation speed
    /// </summary>
    private long CalculateDelayMs(int distanceCm, int animationSpeedCmPerSec)
    {
        if (animationSpeedCmPerSec <= 0 || distanceCm <= 0)
            return 0;

        // delay (ms) = (distance (cm) / speed (cm/s)) * 1000
        return (long)((distanceCm / (double)animationSpeedCmPerSec) * 1000);
    }

    /// <summary>
    /// Update physical distance for a client
    /// </summary>
    public void UpdateClientDistance(string clientId, int distanceCm)
    {
        if (_syncService == null) return;

        var client = _syncService.GetConnectedClients()
            .FirstOrDefault(c => c.ClientId == clientId);

        if (client != null)
        {
            client.PhysicalDistanceCm = distanceCm;
            _logger.LogInformation("Updated client {ClientId} physical distance to {Distance}cm",
                clientId, distanceCm);
        }
    }

    /// <summary>
    /// Load wallpaper on a specific client (not broadcast)
    /// </summary>
    public async Task LoadWallpaperOnClientAsync(
        string clientId,
        string contentId,
        string filePath,
        int? customAnimationSpeed = null)
    {
        if (_syncService == null)
        {
            _logger.LogWarning("Cannot load wallpaper: sync service not initialized");
            return;
        }

        var animationSpeed = customAnimationSpeed ?? DefaultAnimationSpeedCmPerSec;

        var command = new SyncCommand
        {
            Type = CommandType.Load,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SequenceNumber = System.Threading.Interlocked.Increment(ref _sequenceNumber),
            ContentId = contentId,
            Params = new SyncParameters
            {
                AnimationSpeedCmPerSec = animationSpeed,
                CalculatedDelayMs = 0,  // No delay for targeted single client
                Loop = true
            }
        };

        // Register content so client can download it
        _syncService.RegisterContent(contentId, filePath);

        _logger.LogInformation("Loading wallpaper {ContentId} on client {ClientId}", contentId, clientId);

        try
        {
            await _syncService.SendCommandToClientAsync(clientId, command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading wallpaper on client {ClientId}", clientId);
        }
    }

    /// <summary>
    /// Play wallpaper on a specific client (not broadcast)
    /// </summary>
    public async Task PlayOnClientAsync(string clientId, string contentId)
    {
        if (_syncService == null)
        {
            _logger.LogWarning("Cannot play wallpaper: sync service not initialized");
            return;
        }

        var command = new SyncCommand
        {
            Type = CommandType.Play,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SequenceNumber = System.Threading.Interlocked.Increment(ref _sequenceNumber),
            ContentId = contentId,
            Params = new SyncParameters
            {
                AnimationSpeedCmPerSec = DefaultAnimationSpeedCmPerSec,
                CalculatedDelayMs = 0,
                Loop = true
            }
        };

        _logger.LogInformation("Playing wallpaper {ContentId} on client {ClientId}", contentId, clientId);

        try
        {
            await _syncService.SendCommandToClientAsync(clientId, command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing wallpaper on client {ClientId}", clientId);
        }
    }

    /// <summary>
    /// Load wallpaper on a specific client using D2D renderer
    /// </summary>
    public async Task LoadWallpaperD2DOnClientAsync(
        string clientId,
        string contentId,
        string filePath,
        string backgroundColor,
        int fitMode)
    {
        if (_syncService == null)
        {
            _logger.LogWarning("Cannot load D2D wallpaper: sync service not initialized");
            return;
        }

        // Register content so client can download it
        _syncService.RegisterContent(contentId, filePath);

        var command = new SyncCommand
        {
            Type = CommandType.Load,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SequenceNumber = System.Threading.Interlocked.Increment(ref _sequenceNumber),
            ContentId = contentId,
            Params = new SyncParameters
            {
                Loop = true,
                RendererType = "d2d",
                BackgroundColor = backgroundColor,
                FitMode = fitMode
            }
        };

        _logger.LogInformation("Loading D2D wallpaper {ContentId} on client {ClientId} (bg={BgColor}, fit={FitMode})",
            contentId, clientId, backgroundColor, fitMode);

        try
        {
            await _syncService.SendCommandToClientAsync(clientId, command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading D2D wallpaper on client {ClientId}", clientId);
        }
    }

    /// <summary>
    /// Send a cross-screen D2D command to a remote client.
    /// Tells the client to start a D2D player with the given virtual canvas parameters,
    /// synchronized to the shared start timestamp.
    /// </summary>
    public async Task StartCrossScreenD2DOnClientAsync(
        string clientId,
        string contentId,
        string filePath,
        string backgroundColor,
        int fitMode,
        int virtualCanvasWidth,
        int monitorOffsetX,
        long sharedStartTimestampMs,
        int pixelsPerSecond,
        bool perMonitorMode,
        int movementType,
        PatternConfig? pattern = null,
        ColorGradingConfig? colorGrading = null,
        MovementConfig? movement = null,
        AnimationLayerConfig? animation = null,
        Models.Wallpaper.BackgroundLayerConfig? background = null,
        int targetMonitorIndex = 0)
    {
        if (_syncService == null)
        {
            _logger.LogWarning("Cannot start cross-screen D2D: sync service not initialized");
            return;
        }

        _syncService.RegisterContent(contentId, filePath);

        var command = new SyncCommand
        {
            Type = CommandType.Load,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SequenceNumber = System.Threading.Interlocked.Increment(ref _sequenceNumber),
            ContentId = contentId,
            Params = new SyncParameters
            {
                RendererType = "d2d_crossscreen",
                BackgroundColor = backgroundColor,
                FitMode = fitMode,
                Loop = true,
                VirtualCanvasWidth = virtualCanvasWidth,
                MonitorOffsetX = monitorOffsetX,
                SharedStartTimestampMs = sharedStartTimestampMs,
                PixelsPerSecond = pixelsPerSecond,
                PerMonitorMode = perMonitorMode,
                MovementType = movementType,
                PatternJson = pattern != null ? JsonSerializer.Serialize(pattern) : string.Empty,
                ColorGradingJson = colorGrading != null ? JsonSerializer.Serialize(colorGrading) : string.Empty,
                // Full configs as JSON — remote clients must compute the exact same
                // deterministic math as local players (Reversed/Endless/wave/orbit/
                // seed/TargetHeight/backgrounds all matter for sync).
                MovementJson = movement != null ? JsonSerializer.Serialize(movement) : string.Empty,
                AnimationJson = animation != null ? JsonSerializer.Serialize(animation) : string.Empty,
                BackgroundJson = background != null ? JsonSerializer.Serialize(background) : string.Empty,
                TargetMonitorIndex = targetMonitorIndex
            }
        };

        // Remember the command so a reconnecting client resumes the animation.
        _activeCrossScreenCommands[clientId] = command;

        _logger.LogInformation(
            "Starting cross-screen D2D on client {ClientId}: canvas={VCW}px, offset={Offset}px, ts={Ts}ms, speed={Speed}px/s, perMonitor={PerMonitor}",
            clientId, virtualCanvasWidth, monitorOffsetX, sharedStartTimestampMs, pixelsPerSecond, perMonitorMode);

        try
        {
            await _syncService.SendCommandToClientAsync(clientId, command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending cross-screen D2D command to client {ClientId}", clientId);
        }
    }

    /// <summary>
    /// Send a Stop command to a specific client to terminate its cross-screen D2D player.
    /// </summary>
    public async Task StopCrossScreenOnClientAsync(string clientId, string contentId)
    {
        if (_syncService == null)
        {
            _logger.LogWarning("Cannot stop cross-screen on client {ClientId}: sync service not initialized", clientId);
            return;
        }

        _activeCrossScreenCommands.TryRemove(clientId, out _);

        var command = new SyncCommand
        {
            Type = CommandType.Stop,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SequenceNumber = System.Threading.Interlocked.Increment(ref _sequenceNumber),
            ContentId = contentId,
            Params = new SyncParameters()
        };

        _logger.LogInformation("Sending cross-screen Stop to client {ClientId}, contentId={ContentId}", clientId, contentId);

        try
        {
            await _syncService.SendCommandToClientAsync(clientId, command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Stop command to client {ClientId}", clientId);
        }
    }

}

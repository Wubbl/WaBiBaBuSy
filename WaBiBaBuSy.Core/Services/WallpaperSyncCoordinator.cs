using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Grpc.Services;

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
}

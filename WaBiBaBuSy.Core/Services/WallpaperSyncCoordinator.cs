using System.Text.Json;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Services.Show;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Grpc.Services;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Wallpaper;

using WaBiBaBuSy.Models.Topology;

using WaBiBaBuSy.Models.Content;

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

    /// <summary>
    /// The server's show events that have not ended, each with its Traversal image (or null). Set by the server's
    /// playback host; read on whatever thread the coordinator runs (session resume, Resync), so it must be
    /// thread-safe. Those events are re-sent to a client that resumes or is resynced, so a node that reconnects,
    /// restarts or missed the broadcast draws a running event at its current position (dedupe by event id on the
    /// client makes repeats harmless).
    /// </summary>
    public Func<IReadOnlyList<(ShowEvent Event, ContentAssetRef? Asset)>>? ActiveShowEventsProvider { get; set; }

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
                    await ReplayShowEventsAsync(clientId);
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
        // Persist for server-local monitors and expanded nodes too — they are not in the
        // connected-client list but their distance must survive a restart as well.
        _syncService.PersistClientDistance(clientId, distanceCm);
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
    /// Send a cross-screen D2D scene to a remote client: the resolved scene (background + layers), this node's
    /// seat-map layout and every asset, synchronized to the shared start timestamp. <paramref name="contentId"/> /
    /// <paramref name="filePath"/> are the bottom layer's file (the command's primary download).
    /// </summary>
    public async Task StartCrossScreenD2DOnClientAsync(
        string clientId,
        string contentId,
        string filePath,
        CrossScreenConfig scene,
        NodeLayout? layout,
        long sharedStartTimestampMs,
        int targetMonitorIndex,
        IReadOnlyList<ContentAssetRef>? assets = null,
        bool parkOnly = false,
        string transition = "")
    {
        if (_syncService == null)
        {
            _logger.LogWarning("Cannot start cross-screen D2D: sync service not initialized");
            return;
        }

        // Scene layers changed the LOAD format: a client older than the floor would show an empty scene. It gets no
        // scene until auto-update brings it level (the room flags the seat "needs update").
        // parkOnly (an offline seat): nothing to check or send now — the command waits for its session resume.
        var version = _syncService.GetConnectedClients().FirstOrDefault(c => c.ClientId == clientId)?.AppVersion;
        if (!parkOnly && !WaBiBaBuSy.Common.Version.VersionInfo.SupportsSceneLayers(version))
        {
            _logger.LogWarning("Client {ClientId} runs {Version}, scenes need {Min}+ — not sent until it updates",
                clientId, version ?? "(unknown)", WaBiBaBuSy.Common.Version.VersionInfo.MinSceneLayersVersion);
            return;
        }

        _syncService.RegisterContent(contentId, filePath);
        // Tier 1.3: every referenced file (additional images, background image) is downloadable too.
        if (assets != null)
            foreach (var a in assets)
                _syncService.RegisterContent(a.ContentId, a.OriginalPath);

        var command = new SyncCommand
        {
            Type = CommandType.Load,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SequenceNumber = System.Threading.Interlocked.Increment(ref _sequenceNumber),
            ContentId = contentId,
            Params = new SyncParameters
            {
                RendererType = "d2d_crossscreen",
                BackgroundColor = scene.Background.ColorHex ?? "#000000",
                Loop = true,
                SharedStartTimestampMs = sharedStartTimestampMs,
                TargetMonitorIndex = targetMonitorIndex,
                // The whole resolved scene: remote players must compute the exact same deterministic math.
                SceneJson = JsonSerializer.Serialize(scene),
                LayoutJson = layout != null ? JsonSerializer.Serialize(layout) : string.Empty,
                // Item transition ("Fade:1500", "" = Cut); a resume replays it after T0, where it is over already.
                TransitionEffect = transition ?? string.Empty
            }
        };
        if (assets != null)
            foreach (var a in assets)
                command.Params.Assets.Add(new ContentRef { ContentId = a.ContentId, Role = a.Role, OriginalPath = a.OriginalPath });

        // Remember the command so a reconnecting client resumes the animation.
        _activeCrossScreenCommands[clientId] = command;
        if (parkOnly) return;

        _logger.LogInformation(
            "Starting cross-screen D2D on client {ClientId}: {Layers} layer(s), canvas={VCW}px, offset={Offset}px, ts={Ts}ms",
            clientId, scene.Layers.Count, layout?.CanvasWidth ?? 0, layout?.OffsetX ?? 0, sharedStartTimestampMs);

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
    /// Tier 1.3: ask every connected client to cache the given assets in the background (playlist
    /// prefetch), so item switches never wait for downloads. Progress comes back via heartbeat.
    /// </summary>
    public async Task BroadcastPrefetchAsync(IReadOnlyList<ContentAssetRef> assets)
    {
        if (_syncService == null || assets.Count == 0) return;
        foreach (var a in assets)
            _syncService.RegisterContent(a.ContentId, a.OriginalPath);

        var command = new SyncCommand
        {
            Type = CommandType.Prefetch,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SequenceNumber = System.Threading.Interlocked.Increment(ref _sequenceNumber),
            ContentId = assets[0].ContentId,
            Params = new SyncParameters()
        };
        foreach (var a in assets)
            command.Params.Assets.Add(new ContentRef { ContentId = a.ContentId, Role = a.Role, OriginalPath = a.OriginalPath });

        var clients = _syncService.GetConnectedClients().ToList();
        _logger.LogInformation("Prefetch: {Assets} asset(s) → {Clients} client(s)", assets.Count, clients.Count);
        foreach (var client in clients)
        {
            try { await _syncService.SendCommandToClientAsync(client.ClientId, command); }
            catch (Exception ex) { _logger.LogError(ex, "Prefetch send failed for {ClientId}", client.ClientId); }
        }
    }

    /// <summary>
    /// One-shot show event to the connected clients in <paramref name="clientIds"/> (null = every connected client;
    /// the host passes the running scene's remote seats, the same ones Stop reaches) — roadmap 2.4. The image of a
    /// Traversal is registered for download like scene assets. Returns how many clients the command reached.
    /// </summary>
    public async Task<int> BroadcastShowEventAsync(ShowEvent evt, ContentAssetRef? asset, IReadOnlyCollection<string>? clientIds = null)
    {
        if (_syncService == null) return 0;
        if (asset != null) _syncService.RegisterContent(asset.ContentId, asset.OriginalPath);

        var command = ShowEventCodec.ToCommand(evt, System.Threading.Interlocked.Increment(ref _sequenceNumber), asset);
        var targets = clientIds == null ? null : new HashSet<string>(clientIds);
        int sent = 0;
        foreach (var client in _syncService.GetConnectedClients().ToList())
        {
            if (targets != null && !targets.Contains(client.ClientId)) continue;
            try { if (await _syncService.SendCommandToClientAsync(client.ClientId, command)) sent++; }
            catch (Exception ex) { _logger.LogError(ex, "Show event send failed for {ClientId}", client.ClientId); }
        }
        _logger.LogInformation("Show event {Type} {Id} (T0 {T0}) → {Sent} client(s)", evt.Type, evt.EventId, evt.T0Utc, sent);
        return sent;
    }

    /// <summary>
    /// Re-send the active cross-screen command (original shared start) to every client that has
    /// one. Cheap; fixes any node that missed a command or is showing something stale.
    /// </summary>
    public async Task<int> ResyncAllAsync()
    {
        if (_syncService == null) return 0;
        int sent = 0;
        foreach (var kvp in _activeCrossScreenCommands.ToArray())
        {
            try
            {
                if (await _syncService.SendCommandToClientAsync(kvp.Key, kvp.Value))
                {
                    sent++;
                    await ReplayShowEventsAsync(kvp.Key);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Resync failed for {ClientId}", kvp.Key); }
        }
        _logger.LogInformation("Resync: re-sent the active animation to {Count} client(s)", sent);
        return sent;
    }

    /// <summary>
    /// Re-send the active cross-screen command to one client. False when that client has no active
    /// command or the send failed.
    /// </summary>
    public async Task<bool> ResyncClientAsync(string clientId)
    {
        if (_syncService == null || !_activeCrossScreenCommands.TryGetValue(clientId, out var command))
            return false;
        try
        {
            if (!await _syncService.SendCommandToClientAsync(clientId, command)) return false;
            await ReplayShowEventsAsync(clientId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resync failed for {ClientId}", clientId);
            return false;
        }
    }

    /// <summary>
    /// Send the server's running show events (<see cref="ActiveShowEventsProvider"/>) to one client that just got
    /// the active cross-screen command again (session resume, Resync) — only clients of the running scene have
    /// one. Per-event failures are logged and skipped.
    /// </summary>
    private async Task ReplayShowEventsAsync(string clientId)
    {
        if (_syncService == null || ActiveShowEventsProvider == null) return;
        IReadOnlyList<(ShowEvent Event, ContentAssetRef? Asset)> events;
        try { events = ActiveShowEventsProvider(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the active show events failed");
            return;
        }
        if (events.Count == 0) return;

        foreach (var (_, asset) in events)
            if (asset != null) _syncService.RegisterContent(asset.ContentId, asset.OriginalPath);

        var commands = BuildReplayCommands(events, () => System.Threading.Interlocked.Increment(ref _sequenceNumber));
        for (int i = 0; i < commands.Count; i++)
        {
            try
            {
                await _syncService.SendCommandToClientAsync(clientId, commands[i]);
                _logger.LogInformation("Replayed show event {Type} {Id} to {ClientId}", events[i].Event.Type, events[i].Event.EventId, clientId);
            }
            catch (Exception ex) { _logger.LogError(ex, "Show event replay failed for {ClientId}", clientId); }
        }
    }

    /// <summary>One EVENT command per running event (T0 in the timestamp, Traversal image as an asset ref), in order.</summary>
    internal static IReadOnlyList<SyncCommand> BuildReplayCommands(IEnumerable<(ShowEvent Event, ContentAssetRef? Asset)> events, Func<int> nextSequenceNumber) =>
        events.Select(e => ShowEventCodec.ToCommand(e.Event, nextSequenceNumber(), e.Asset)).ToList();

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

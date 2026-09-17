using Grpc.Core;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Common.Version;
using WaBiBaBuSy.Models.Networking;

namespace WaBiBaBuSy.Grpc.Services;

/// <summary>
/// Server-side implementation of the WallpaperSync gRPC service
/// </summary>
public class WallpaperSyncService : WallpaperSync.WallpaperSyncBase
{
    private readonly ILogger<WallpaperSyncService> _logger;
    private readonly ServerConfiguration _serverConfig;
    private readonly ConcurrentDictionary<string, ConnectedClient> _connectedClients;
    private readonly ConcurrentDictionary<string, IServerStreamWriter<SyncCommand>> _clientCommandStreams;
    private readonly ConcurrentDictionary<string, ThumbnailData> _clientThumbnails;
    private readonly ConcurrentDictionary<string, string> _contentRegistry; // contentId -> server file path
    private readonly ConcurrentDictionary<string, ClientLogData> _clientLogs; // clientId -> latest logs
    private readonly ConcurrentDictionary<string, int> _serverLocalMonitorOrders = new(); // SERVER_LOCALHOST_MONITOR_* order overrides
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new(); // per-client gRPC stream write serialization
    private readonly DriftMonitor _driftMonitor = new(); // per-client drift telemetry from heartbeats
    private readonly Timer _heartbeatSweepTimer;
    private int _nextClientOrder = 1;
    // Tier 1.3: 20 clients fetching the same 20 MB GIF at once would thrash one server — queue them.
    private readonly SemaphoreSlim _downloadSlots = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private const int MaxConcurrentDownloads = 6;
    // Tier 0.3: order + bezel distance per node survive server restarts and client reconnects.
    private readonly TopologyStore _topology;
    private readonly object _topologyLock = new();

    /// <summary>Clients whose last heartbeat is older than this are considered dead and swept.</summary>
    private const int HeartbeatTimeoutSeconds = 30;

    /// <summary>
    /// Raised after a client registers (or re-registers) successfully.
    /// Used e.g. to resume an active cross-screen animation after a reconnect.
    /// </summary>
    public event EventHandler<string>? ClientRegistered;

    public WallpaperSyncService(
        ILogger<WallpaperSyncService> logger,
        ServerConfiguration serverConfig)
    {
        _logger = logger;
        _serverConfig = serverConfig;
        _connectedClients = new ConcurrentDictionary<string, ConnectedClient>();
        _clientCommandStreams = new ConcurrentDictionary<string, IServerStreamWriter<SyncCommand>>();
        _clientThumbnails = new ConcurrentDictionary<string, ThumbnailData>();
        _contentRegistry = new ConcurrentDictionary<string, string>();
        _clientLogs = new ConcurrentDictionary<string, ClientLogData>();

        // Ensure content directory exists
        Directory.CreateDirectory(_serverConfig.ContentDirectory);

        // Restore the persisted topology. Real clients are re-attached in RegisterClient;
        // server-local / expanded monitor nodes live in _serverLocalMonitorOrders.
        _topology = TopologyStore.Load();
        foreach (var entry in _topology.Entries)
        {
            if (entry.ClientId.StartsWith("SERVER_LOCALHOST_MONITOR_") || entry.ClientId.Contains("_MONITOR_"))
                _serverLocalMonitorOrders[entry.ClientId] = entry.OrderPosition;
            _nextClientOrder = Math.Max(_nextClientOrder, entry.OrderPosition + 1);
        }
        if (_topology.Entries.Count > 0)
            _logger.LogInformation("Restored topology for {Count} node(s) from {Path}", _topology.Entries.Count, TopologyStore.DefaultPath);

        // Sweep clients that died without a TCP reset (power loss, sleep) —
        // stream teardown never fires for those, so LastHeartbeat is the only signal.
        _heartbeatSweepTimer = new Timer(SweepDeadClients, null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Remove clients whose heartbeat stopped. Registration seeds LastHeartbeat,
    /// so freshly registered clients are never swept prematurely.
    /// </summary>
    private void SweepDeadClients(object? state)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - HeartbeatTimeoutSeconds;
            foreach (var kvp in _connectedClients)
            {
                if (kvp.Value.LastHeartbeat < cutoff)
                {
                    _logger.LogWarning("Client {ClientId} ({Hostname}) heartbeat timed out (> {Timeout}s) — removing from topology",
                        kvp.Key, kvp.Value.Hostname, HeartbeatTimeoutSeconds);
                    RemoveClient(kvp.Key);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during dead-client sweep");
        }
    }

    /// <summary>
    /// Register a new client connection
    /// </summary>
    public override async Task<RegistrationResponse> RegisterClient(
        ClientInfo request,
        ServerCallContext context)
    {
        _logger.LogInformation("Client registration request from {Hostname} ({IpAddress}) - Version: {Version} Build: {Build}",
            request.Hostname, request.IpAddress, request.AppVersion, request.BuildNumber);

        try
        {
            // Generate client ID if not provided
            var clientId = string.IsNullOrEmpty(request.ClientId)
                ? Guid.NewGuid().ToString()
                : request.ClientId;

            // Check for version compatibility and updates
            bool updateAvailable = false;
            string updateDescription = string.Empty;
            long updatePackageSize = 0;
            var serverVersion = WaBiBaBuSy.Common.Version.VersionInfo.AppVersion;
            var serverBuild = WaBiBaBuSy.Common.Version.VersionInfo.BuildNumber;

            if (_serverConfig.UpdateManagement.EnableUpdates)
            {

                // Check if client version is older than server version
                updateAvailable = WaBiBaBuSy.Common.Version.VersionInfo.IsNewerVersion(
                    request.AppVersion, request.BuildNumber,
                    serverVersion, serverBuild);

                if (updateAvailable)
                {
                    _logger.LogInformation("Update available for client {ClientId}. Client: {ClientVer} build {ClientBuild}, Server: {ServerVer} build {ServerBuild}",
                        clientId, request.AppVersion, request.BuildNumber,
                        serverVersion, serverBuild);

                    updateDescription = $"Update to {serverVersion}";

                    // Package size will be determined when the client actually requests the download.
                    // We no longer pre-build the package on registration to avoid unnecessary work.
                }

                // Check if client version is below minimum compatible version
                bool isClientTooOld = WaBiBaBuSy.Common.Version.VersionInfo.IsUpdateRequired(
                    request.AppVersion,
                    _serverConfig.UpdateManagement.MinimumCompatibleVersion);

                if (isClientTooOld && _serverConfig.UpdateManagement.EnforceMandatoryUpdates)
                {
                    _logger.LogWarning("Client {ClientId} version {Version} is below minimum {MinVersion}. Rejecting connection.",
                        clientId, request.AppVersion, _serverConfig.UpdateManagement.MinimumCompatibleVersion);

                    return new RegistrationResponse
                    {
                        Success = false,
                        Message = $"Client version {request.AppVersion} is too old. Minimum version required: {_serverConfig.UpdateManagement.MinimumCompatibleVersion}. Please update.",
                        UpdateAvailable = true,
                        RequiredVersion = serverVersion,
                        RequiredBuildNumber = serverBuild,
                        UpdatePackageSize = updatePackageSize,
                        UpdateDescription = "Mandatory update required"
                    };
                }
            }

            // Re-attach the machine to its persisted seat (by id, then by hostname); a brand-new
            // machine is appended to the end of the chain and remembered.
            int orderPosition;
            int distanceCm = 0;
            lock (_topologyLock)
            {
                var seat = _topology.Resolve(clientId, request.Hostname);
                if (seat != null)
                {
                    orderPosition = seat.OrderPosition;
                    distanceCm = seat.PhysicalDistanceCm;
                    if (!string.IsNullOrWhiteSpace(request.Hostname)) seat.Hostname = request.Hostname;
                }
                else
                {
                    orderPosition = Math.Max(_topology.NextFreeOrder(), _nextClientOrder);
                    _topology.Upsert(new TopologyEntry
                    {
                        ClientId = clientId,
                        Hostname = request.Hostname,
                        OrderPosition = orderPosition
                    });
                }
                _nextClientOrder = Math.Max(_nextClientOrder, orderPosition + 1);
                _topology.Save();
            }

            // Create connected client record
            var connectedClient = new ConnectedClient
            {
                ClientId = clientId,
                Hostname = request.Hostname,
                IpAddress = request.IpAddress,
                OrderPosition = orderPosition,
                PhysicalDistanceCm = distanceCm,
                Status = ClientStatusEnum.ClientConnected,
                LastHeartbeat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ScreenConfig = request.ScreenConfig
            };

            // Add or update client
            _connectedClients.AddOrUpdate(clientId, connectedClient,
                (key, existing) => connectedClient);

            _logger.LogInformation("Client {ClientId} registered successfully. Order position: {OrderPosition}",
                clientId, connectedClient.OrderPosition);

            ClientRegistered?.Invoke(this, clientId);

            return new RegistrationResponse
            {
                Success = true,
                Message = "Registration successful",
                AssignedClientId = clientId,
                OrderPosition = connectedClient.OrderPosition,
                UpdateAvailable = updateAvailable,
                RequiredVersion = serverVersion,
                RequiredBuildNumber = serverBuild,
                UpdatePackageSize = updatePackageSize,
                UpdateDescription = updateDescription
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering client");
            return new RegistrationResponse
            {
                Success = false,
                Message = $"Registration failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Handle client heartbeat to maintain connection
    /// </summary>
    public override Task<HeartbeatResponse> Heartbeat(
        HeartbeatRequest request,
        ServerCallContext context)
    {
        if (_connectedClients.TryGetValue(request.ClientId, out var client))
        {
            client.LastHeartbeat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            client.Status = request.Status;
            client.PrefetchReady = request.PrefetchReady;
            client.PrefetchTotal = request.PrefetchTotal;

            if (request.HasDriftReport)
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                client.ClockOffsetMs = request.ClockOffsetMs;
                client.RttMs = request.RttMs;
                client.LastDriftReportUtc = nowMs;

                // Record returns true only on a NEW breach — log once, not every heartbeat.
                if (_driftMonitor.Record(request.ClientId, request.ClockOffsetMs, request.RttMs, nowMs))
                {
                    _logger.LogWarning(
                        "Client {ClientId} clock offset {OffsetMs:F1}ms exceeds the ±{ToleranceMs:F0}ms sync tolerance (RTT {RttMs:F1}ms)",
                        request.ClientId, request.ClockOffsetMs, DriftMonitor.BreachThresholdMs, request.RttMs);
                }
            }

            _logger.LogDebug("Heartbeat received from client {ClientId}", request.ClientId);

            return Task.FromResult(new HeartbeatResponse
            {
                Acknowledged = true,
                ServerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        _logger.LogWarning("Heartbeat from unknown client {ClientId}", request.ClientId);
        return Task.FromResult(new HeartbeatResponse
        {
            Acknowledged = false,
            ServerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    /// <summary>
    /// Bidirectional streaming for sync commands
    /// Client sends responses/status, server sends commands
    /// </summary>
    public override async Task SyncStream(
        IAsyncStreamReader<SyncResponse> requestStream,
        IServerStreamWriter<SyncCommand> responseStream,
        ServerCallContext context)
    {
        // Extract client ID from metadata
        var clientIdHeader = context.RequestHeaders.FirstOrDefault(h => h.Key == "client-id");
        var clientId = clientIdHeader?.Value;

        if (string.IsNullOrEmpty(clientId))
        {
            _logger.LogWarning("SyncStream started without client-id header");
            return;
        }

        _logger.LogInformation("SyncStream started for client {ClientId}", clientId);

        try
        {
            // Register this client's command stream (server->client direction)
            _clientCommandStreams.AddOrUpdate(clientId, responseStream, (key, existing) => responseStream);

            // Listen for client responses/status updates
            await foreach (var response in requestStream.ReadAllAsync(context.CancellationToken))
            {
                _logger.LogDebug("Received sync response from client {ClientId}: Sequence {SequenceNumber}, State {State}",
                    response.ClientId, response.SequenceNumber, response.State);

                // Update client status based on wallpaper state
                if (_connectedClients.TryGetValue(clientId, out var client))
                {
                    client.Status = MapWallpaperStateToClientStatus(response.State);
                    client.LastHeartbeat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
            }
        }
        catch (IOException)
        {
            // Client disconnected abruptly (reset stream) — this is normal
            _logger.LogWarning("Client {ClientId} disconnected (stream reset)", clientId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SyncStream cancelled for client {ClientId}", clientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SyncStream for client {ClientId}", clientId);
        }
        finally
        {
            // Remove client completely on disconnect
            RemoveClient(clientId);
            _logger.LogInformation("Client {ClientId} removed from topology after disconnect", clientId);
        }
    }

    /// <summary>
    /// Map wallpaper state to client status
    /// </summary>
    private ClientStatusEnum MapWallpaperStateToClientStatus(WallpaperStateEnum state)
    {
        return state switch
        {
            WallpaperStateEnum.WallpaperUninitialized => ClientStatusEnum.ClientConnected,
            WallpaperStateEnum.WallpaperStopped => ClientStatusEnum.ClientConnected,
            WallpaperStateEnum.WallpaperPlaying => ClientStatusEnum.ClientPlaying,
            WallpaperStateEnum.WallpaperPaused => ClientStatusEnum.ClientConnected,
            WallpaperStateEnum.WallpaperBuffering => ClientStatusEnum.ClientSyncing,
            WallpaperStateEnum.WallpaperError => ClientStatusEnum.ClientError,
            _ => ClientStatusEnum.ClientConnected
        };
    }

    /// <summary>
    /// Handle content file transfer from client to server
    /// </summary>
    public override async Task<TransferStatus> TransferContent(
        IAsyncStreamReader<ContentChunk> requestStream,
        ServerCallContext context)
    {
        _logger.LogInformation("Content transfer started");

        var chunksReceived = 0;
        var totalChunks = 0;
        string? contentId = null;
        string? filename = null;

        try
        {
            var chunks = new List<ContentChunk>();

            await foreach (var chunk in requestStream.ReadAllAsync(context.CancellationToken))
            {
                contentId = chunk.ContentId;
                filename = chunk.Filename;
                totalChunks = chunk.TotalChunks;
                chunksReceived++;

                chunks.Add(chunk);

                _logger.LogDebug("Received chunk {ChunkIndex}/{TotalChunks} for {Filename}",
                    chunk.ChunkIndex, chunk.TotalChunks, chunk.Filename);
            }

            // Validate all chunks received
            if (chunksReceived != totalChunks)
            {
                _logger.LogError("Incomplete transfer: received {Received}/{Total} chunks", chunksReceived, totalChunks);
                return new TransferStatus
                {
                    Success = false,
                    Message = $"Incomplete transfer: received {chunksReceived}/{totalChunks} chunks",
                    ChunksReceived = chunksReceived,
                    TotalChunks = totalChunks
                };
            }

            // Sort chunks by index
            var sortedChunks = chunks.OrderBy(c => c.ChunkIndex).ToList();

            // Reassemble file
            var filePath = Path.Combine(_serverConfig.ContentDirectory, filename ?? "unknown");
            await using (var fileStream = File.Create(filePath))
            {
                foreach (var chunk in sortedChunks)
                {
                    await fileStream.WriteAsync(chunk.Data.ToByteArray(), context.CancellationToken);
                }
            }

            // Verify file hash if provided
            if (!string.IsNullOrEmpty(sortedChunks[0].Hash))
            {
                var actualHash = await ComputeFileHashAsync(filePath);
                if (!actualHash.Equals(sortedChunks[0].Hash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Hash mismatch for {Filename}. Expected: {Expected}, Actual: {Actual}",
                        filename, sortedChunks[0].Hash, actualHash);

                    // Delete corrupted file
                    File.Delete(filePath);

                    return new TransferStatus
                    {
                        Success = false,
                        Message = "Hash verification failed - file corrupted",
                        ChunksReceived = chunksReceived,
                        TotalChunks = totalChunks
                    };
                }
            }

            _logger.LogInformation("Content transfer completed successfully for {Filename}. Saved to {FilePath}",
                filename, filePath);

            return new TransferStatus
            {
                Success = true,
                Message = $"Transfer completed successfully. File saved to {filePath}",
                ChunksReceived = chunksReceived,
                TotalChunks = totalChunks
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during content transfer");
            return new TransferStatus
            {
                Success = false,
                Message = $"Transfer failed: {ex.Message}",
                ChunksReceived = chunksReceived,
                TotalChunks = totalChunks
            };
        }
    }

    /// <summary>
    /// Get current network topology (all connected clients)
    /// </summary>
    public override Task<TopologyResponse> GetTopology(
        TopologyRequest request,
        ServerCallContext context)
    {
        var response = new TopologyResponse();
        response.Clients.AddRange(_connectedClients.Values.OrderBy(c => c.OrderPosition));

        _logger.LogDebug("Topology requested. Returning {ClientCount} clients",
            response.Clients.Count);

        return Task.FromResult(response);
    }

    /// <summary>
    /// Update the order of clients in the topology
    /// </summary>
    public override Task<OrderUpdateResponse> UpdateClientOrder(
        ClientOrderUpdate request,
        ServerCallContext context)
    {
        _logger.LogInformation("Updating client order for {Count} clients",
            request.ClientOrders.Count);

        try
        {
            foreach (var orderItem in request.ClientOrders)
            {
                if (_connectedClients.TryGetValue(orderItem.ClientId, out var client))
                {
                    client.OrderPosition = orderItem.NewPosition;
                    PersistClientOrder(orderItem.ClientId, client.Hostname, orderItem.NewPosition, save: false);
                    _logger.LogDebug("Updated client {ClientId} to position {Position}",
                        orderItem.ClientId, orderItem.NewPosition);
                }
            }
            SaveTopology();

            return Task.FromResult(new OrderUpdateResponse
            {
                Success = true,
                Message = "Client order updated successfully"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating client order");
            return Task.FromResult(new OrderUpdateResponse
            {
                Success = false,
                Message = $"Update failed: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Update the physical distance of a client
    /// </summary>
    public override Task<DistanceUpdateResponse> UpdateClientDistance(
        ClientDistanceUpdate request,
        ServerCallContext context)
    {
        _logger.LogInformation("Updating physical distance for client {ClientId} to {Distance} cm",
            request.ClientId, request.PhysicalDistanceCm);

        try
        {
            if (_connectedClients.TryGetValue(request.ClientId, out var client))
            {
                client.PhysicalDistanceCm = request.PhysicalDistanceCm;
                PersistClientDistance(request.ClientId, request.PhysicalDistanceCm);
                _logger.LogInformation("Updated client {ClientId} physical distance to {Distance} cm",
                    request.ClientId, request.PhysicalDistanceCm);

                return Task.FromResult(new DistanceUpdateResponse
                {
                    Success = true,
                    Message = "Physical distance updated successfully"
                });
            }
            else
            {
                _logger.LogWarning("Client {ClientId} not found", request.ClientId);
                return Task.FromResult(new DistanceUpdateResponse
                {
                    Success = false,
                    Message = $"Client {request.ClientId} not found"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating client distance");
            return Task.FromResult(new DistanceUpdateResponse
            {
                Success = false,
                Message = $"Update failed: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Get all connected clients (for external access)
    /// </summary>
    public IEnumerable<ConnectedClient> GetConnectedClients()
    {
        return _connectedClients.Values.OrderBy(c => c.OrderPosition);
    }

    /// <summary>
    /// Update client order directly (for in-process server-mode calls).
    /// Handles both real connected clients and SERVER_LOCALHOST_MONITOR_* nodes.
    /// </summary>
    public bool UpdateClientOrderDirect(Dictionary<string, int> clientOrders)
    {
        try
        {
            foreach (var (clientId, newPosition) in clientOrders)
            {
                if (_connectedClients.TryGetValue(clientId, out var client))
                {
                    client.OrderPosition = newPosition;
                    PersistClientOrder(clientId, client.Hostname, newPosition, save: false);
                }
                else if (clientId.StartsWith("SERVER_LOCALHOST_MONITOR_"))
                {
                    _serverLocalMonitorOrders[clientId] = newPosition;
                    PersistClientOrder(clientId, null, newPosition, save: false);
                }
                else
                {
                    // Could be a per-monitor expanded node (e.g., "guid_MONITOR_0")
                    // Try to find the base client
                    var baseIdx = clientId.LastIndexOf("_MONITOR_");
                    if (baseIdx >= 0)
                    {
                        var baseClientId = clientId[..baseIdx];
                        // Store as server-local override since expanded nodes aren't in _connectedClients
                        _serverLocalMonitorOrders[clientId] = newPosition;
                        PersistClientOrder(clientId, null, newPosition, save: false);
                    }
                }
            }
            SaveTopology();

            _logger.LogInformation("Updated order for {Count} clients (direct)", clientOrders.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating client order (direct)");
            return false;
        }
    }

    /// <summary>
    /// Remember a node's bezel distance in the persisted topology (server-mode UI edits bypass
    /// the gRPC UpdateClientDistance RPC and call this directly).
    /// </summary>
    public void PersistClientDistance(string clientId, int distanceCm)
    {
        lock (_topologyLock)
        {
            var hostname = _connectedClients.TryGetValue(clientId, out var c) ? c.Hostname : null;
            _topology.SetDistance(clientId, hostname, distanceCm);
            _topology.Save();
        }
    }

    private void PersistClientOrder(string clientId, string? hostname, int orderPosition, bool save)
    {
        lock (_topologyLock)
        {
            _topology.SetOrder(clientId, hostname, orderPosition);
            _nextClientOrder = Math.Max(_nextClientOrder, orderPosition + 1);
            if (save) _topology.Save();
        }
    }

    private void SaveTopology()
    {
        lock (_topologyLock) _topology.Save();
    }

    /// <summary>
    /// Get the persisted order for a server-local or expanded monitor node.
    /// Returns null if no override has been set.
    /// </summary>
    public int? GetServerLocalMonitorOrder(string clientId)
    {
        return _serverLocalMonitorOrders.TryGetValue(clientId, out var order) ? order : null;
    }

    /// <summary>
    /// Handle client thumbnail upload
    /// </summary>
    public override Task<ThumbnailResponse> SendThumbnail(
        ThumbnailData request,
        ServerCallContext context)
    {
        _clientThumbnails.AddOrUpdate(request.ClientId, request, (_, _) => request);
        _logger.LogDebug("Received thumbnail from client {ClientId}: {Width}x{Height}, {Size} bytes",
            request.ClientId, request.Width, request.Height, request.ThumbnailJpeg.Length);

        return Task.FromResult(new ThumbnailResponse { Acknowledged = true });
    }

    /// <summary>
    /// Get the latest thumbnail for a client
    /// </summary>
    public ThumbnailData? GetClientThumbnail(string clientId)
    {
        _clientThumbnails.TryGetValue(clientId, out var thumbnail);
        return thumbnail;
    }

    /// <summary>
    /// Remove a client from connected clients
    /// </summary>
    public bool RemoveClient(string clientId)
    {
        var removed = _connectedClients.TryRemove(clientId, out _);
        if (removed)
        {
            _logger.LogInformation("Client {ClientId} removed", clientId);
        }
        _clientCommandStreams.TryRemove(clientId, out _);
        _clientThumbnails.TryRemove(clientId, out _);
        // Drop (don't Dispose) the write lock: a concurrent writer may still hold it,
        // and releasing a disposed SemaphoreSlim throws.
        _writeLocks.TryRemove(clientId, out _);
        _driftMonitor.Remove(clientId);
        return removed;
    }

    /// <summary>
    /// Write a command to a client's response stream. IServerStreamWriter forbids
    /// overlapping writes, so all writes to one client are serialized through a
    /// per-client semaphore — otherwise concurrent senders (broadcast racing a
    /// direct send) throw and the command is silently lost.
    /// </summary>
    private async Task WriteToClientStreamAsync(string clientId, IServerStreamWriter<SyncCommand> stream, SyncCommand command)
    {
        var gate = _writeLocks.GetOrAdd(clientId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await stream.WriteAsync(command);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Send a command to a specific client
    /// </summary>
    public async Task<bool> SendCommandToClientAsync(string clientId, SyncCommand command)
    {
        if (_clientCommandStreams.TryGetValue(clientId, out var stream))
        {
            try
            {
                await WriteToClientStreamAsync(clientId, stream, command);
                _logger.LogDebug("Sent {CommandType} command to client {ClientId}", command.Type, clientId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send command to client {ClientId}", clientId);
                return false;
            }
        }

        _logger.LogWarning("Cannot send command to client {ClientId} - no active stream", clientId);
        return false;
    }

    /// <summary>
    /// Compute SHA-256 hash of a file
    /// </summary>
    private async Task<string> ComputeFileHashAsync(string filePath)
    {
        using var sha256 = SHA256.Create();
        await using var fileStream = File.OpenRead(filePath);
        var hashBytes = await sha256.ComputeHashAsync(fileStream);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    #region Update Distribution

    // Cached path to auto-generated update package (built from server's own binaries)
    private string? _cachedUpdatePackagePath;
    private readonly SemaphoreSlim _packageBuildSemaphore = new(1, 1);

    /// <summary>
    /// Handle client checking for available updates
    /// </summary>
    public override async Task<UpdateCheckResponse> CheckForUpdates(
        UpdateCheckRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("Update check from client {ClientId}: version {Version} build {Build}",
            request.ClientId, request.CurrentVersion, request.CurrentBuild);

        try
        {
            if (!_serverConfig.UpdateManagement.EnableUpdates)
            {
                return new UpdateCheckResponse { UpdateAvailable = false };
            }

            // Compare using the ACTUAL running server version, not config
            var serverVersion = WaBiBaBuSy.Common.Version.VersionInfo.AppVersion;
            var serverBuild = WaBiBaBuSy.Common.Version.VersionInfo.BuildNumber;

            bool updateAvailable = WaBiBaBuSy.Common.Version.VersionInfo.IsNewerVersion(
                request.CurrentVersion, request.CurrentBuild,
                serverVersion, serverBuild);

            if (!updateAvailable)
            {
                _logger.LogInformation("Client {ClientId} is up to date (both at {Version} build {Build})",
                    request.ClientId, request.CurrentVersion, request.CurrentBuild);
                return new UpdateCheckResponse { UpdateAvailable = false };
            }

            // Package will be built on-demand when the client requests the download.
            // Check if a cached package already exists for the size info.
            long packageSize = 0;
            if (_cachedUpdatePackagePath != null && File.Exists(_cachedUpdatePackagePath))
            {
                packageSize = new FileInfo(_cachedUpdatePackagePath).Length;
            }

            bool isMandatory = _serverConfig.UpdateManagement.EnforceMandatoryUpdates &&
                WaBiBaBuSy.Common.Version.VersionInfo.IsUpdateRequired(
                    request.CurrentVersion,
                    _serverConfig.UpdateManagement.MinimumCompatibleVersion);

            var response = new UpdateCheckResponse
            {
                UpdateAvailable = true,
                NewVersion = serverVersion,
                NewBuild = serverBuild,
                PackageSize = packageSize,
                PackageHash = string.Empty, // Computed during download
                ReleaseNotes = $"Update from {request.CurrentVersion} to {serverVersion}",
                IsMandatory = isMandatory
            };

            _logger.LogInformation("Update available for client {ClientId}: {ClientVer} → {ServerVer} ({Size:N0} bytes)",
                request.ClientId, request.CurrentVersion, serverVersion, packageSize);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for updates");
            return new UpdateCheckResponse { UpdateAvailable = false };
        }
    }

    /// <summary>
    /// Stream update package to client
    /// </summary>
    public override async Task DownloadUpdate(
        UpdateDownloadRequest request,
        IServerStreamWriter<UpdateChunk> responseStream,
        ServerCallContext context)
    {
        _logger.LogInformation("Update download requested by client {ClientId}: version {Version}",
            request.ClientId, request.RequestedVersion);

        // Auto-build package from server's own binaries if needed
        var packagePath = await EnsureUpdatePackageExistsAsync();

        if (!File.Exists(packagePath))
        {
            _logger.LogError("Failed to create update package");
            throw new RpcException(new Status(StatusCode.Internal, "Failed to create update package"));
        }

        try
        {
            var fileInfo = new FileInfo(packagePath);
            var filename = fileInfo.Name;
            var fileSize = fileInfo.Length;
            const int chunkSize = 1024 * 1024; // 1 MB chunks
            var totalChunks = (int)Math.Ceiling((double)fileSize / chunkSize);

            var packageHash = await ComputeFileHashAsync(packagePath);

            _logger.LogInformation("Streaming update package: {Filename} ({FileSize:N0} bytes, {TotalChunks} chunks)",
                filename, fileSize, totalChunks);

            await using var fileStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[chunkSize];
            int chunkIndex = 0;

            while (true)
            {
                var bytesRead = await fileStream.ReadAsync(buffer, context.CancellationToken);
                if (bytesRead == 0) break;

                var chunk = new UpdateChunk
                {
                    UpdateId = request.RequestedVersion,
                    Filename = filename,
                    TotalSize = fileSize,
                    ChunkIndex = chunkIndex,
                    TotalChunks = totalChunks,
                    Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead),
                    FileHash = string.Empty,
                    PackageHash = packageHash
                };

                await responseStream.WriteAsync(chunk);
                chunkIndex++;
            }

            _logger.LogInformation("Update download complete for client {ClientId}: {TotalChunks} chunks sent",
                request.ClientId, totalChunks);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Update download cancelled for client {ClientId}", request.ClientId);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming update to client {ClientId}", request.ClientId);
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    /// <summary>
    /// Receive update status report from client
    /// </summary>
    public override Task<UpdateStatusResponse> ReportUpdateStatus(
        UpdateStatusReport request,
        ServerCallContext context)
    {
        _logger.LogInformation("Update status from client {ClientId}: {Status} ({Progress}%) - {Error}",
            request.ClientId, request.Status, request.ProgressPercent, request.ErrorMessage);

        return Task.FromResult(new UpdateStatusResponse { Acknowledged = true });
    }

    /// <summary>
    /// Auto-generate update package from the server's own running binaries.
    /// The server IS the newest version — it packages itself for outdated clients.
    /// </summary>
    private async Task<string> EnsureUpdatePackageExistsAsync()
    {
        var serverVersion = WaBiBaBuSy.Common.Version.VersionInfo.AppVersion;
        var updatesDir = _serverConfig.UpdateManagement.UpdatesDirectory;
        Directory.CreateDirectory(updatesDir);

        var packagePath = Path.Combine(updatesDir, $"UpdatePackage_{serverVersion}.zip");

        // Return cached path if package already exists and matches current version
        if (_cachedUpdatePackagePath != null && File.Exists(_cachedUpdatePackagePath))
            return _cachedUpdatePackagePath;

        if (File.Exists(packagePath))
        {
            _cachedUpdatePackagePath = packagePath;
            return packagePath;
        }

        // Build package from server's own installation directory
        await _packageBuildSemaphore.WaitAsync();
        try
        {
            // Double-check after acquiring semaphore
            if (File.Exists(packagePath))
            {
                _cachedUpdatePackagePath = packagePath;
                return packagePath;
            }

            _logger.LogInformation("Auto-generating update package from server binaries for version {Version}", serverVersion);

            var serverInstallDir = AppDomain.CurrentDomain.BaseDirectory;
            var tempDir = Path.Combine(updatesDir, $"_build_{serverVersion}");

            try
            {
                // Clean temp dir
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);

                var binariesDir = Path.Combine(tempDir, "binaries");
                var updaterDir = Path.Combine(tempDir, "updater");
                Directory.CreateDirectory(binariesDir);
                Directory.CreateDirectory(updaterDir);

                // Copy all application files to binaries/
                var extensions = new[] { "*.exe", "*.dll", "*.json", "*.pdb", "*.deps.json", "*.runtimeconfig.json" };
                var manifestFiles = new List<(string relativePath, string fullPath)>();

                foreach (var ext in extensions)
                {
                    foreach (var file in Directory.GetFiles(serverInstallDir, ext, SearchOption.TopDirectoryOnly))
                    {
                        var fileName = Path.GetFileName(file);
                        // Skip config files that are machine-specific
                        if (fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
                            fileName.Equals("appsettings.Development.json", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var destPath = Path.Combine(binariesDir, fileName);
                        File.Copy(file, destPath, overwrite: true);
                        manifestFiles.Add(($"binaries/{fileName}", destPath));
                    }
                }

                // Copy native libraries (libvlc, etc.) if they exist in subdirectories
                foreach (var subDir in new[] { "libvlc", "runtimes" })
                {
                    var sourceSubDir = Path.Combine(serverInstallDir, subDir);
                    if (Directory.Exists(sourceSubDir))
                    {
                        CopyDirectoryRecursive(sourceSubDir, Path.Combine(binariesDir, subDir));
                    }
                }

                // Copy updater exe — required for clients to apply the update
                var updaterExe = Path.Combine(serverInstallDir, "WaBiBaBuSy.Updater.exe");
                if (File.Exists(updaterExe))
                {
                    File.Copy(updaterExe, Path.Combine(updaterDir, "WaBiBaBuSy.Updater.exe"), overwrite: true);

                    // Also copy updater dependencies
                    foreach (var file in Directory.GetFiles(serverInstallDir, "WaBiBaBuSy.Updater.*"))
                    {
                        var fileName = Path.GetFileName(file);
                        File.Copy(file, Path.Combine(updaterDir, fileName), overwrite: true);
                    }
                }
                else
                {
                    _logger.LogError("WaBiBaBuSy.Updater.exe not found in server install dir {Dir} — update package will be built without it. Clients must have the updater pre-installed to apply this update.", serverInstallDir);
                }

                // Generate manifest.json with SHA-256 hashes
                var manifest = new
                {
                    Version = serverVersion,
                    BuildNumber = WaBiBaBuSy.Common.Version.VersionInfo.BuildNumber,
                    ReleaseDate = DateTime.UtcNow.ToString("o"),
                    MinimumCompatibleVersion = _serverConfig.UpdateManagement.MinimumCompatibleVersion,
                    ReleaseNotes = $"Auto-generated update package from server v{serverVersion}",
                    Files = manifestFiles.Select(f =>
                    {
                        using var sha = System.Security.Cryptography.SHA256.Create();
                        using var stream = File.OpenRead(f.fullPath);
                        var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                        return new { Path = f.relativePath, Size = new FileInfo(f.fullPath).Length, Sha256 = hash, Action = "Replace" };
                    }).ToList()
                };

                var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(tempDir, "manifest.json"), manifestJson);

                // Create ZIP - use explicit ZipArchive to ensure file handle is released
                if (File.Exists(packagePath))
                    File.Delete(packagePath);

                using (var zipStream = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var filePath in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
                    {
                        var entryName = Path.GetRelativePath(tempDir, filePath).Replace('\\', '/');
                        archive.CreateEntryFromFile(filePath, entryName, System.IO.Compression.CompressionLevel.Optimal);
                    }
                }

                _logger.LogInformation("Update package created: {PackagePath} ({Size:N0} bytes, {FileCount} files)",
                    packagePath, new FileInfo(packagePath).Length, manifestFiles.Count);

                _cachedUpdatePackagePath = packagePath;
            }
            finally
            {
                // Clean up temp build directory
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { /* best effort */ }
            }
        }
        finally
        {
            _packageBuildSemaphore.Release();
        }

        return packagePath;
    }

    /// <summary>
    /// Recursively copy a directory
    /// </summary>
    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    #endregion

    #region Content Download (Server → Client)

    /// <summary>
    /// Register a content file so clients can download it by content ID
    /// </summary>
    public void RegisterContent(string contentId, string filePath)
    {
        _contentRegistry[contentId] = filePath;
        _logger.LogInformation("Registered content {ContentId} at {FilePath}", contentId, filePath);
    }

    /// <summary>
    /// Stream content file to client (server-streaming RPC)
    /// </summary>
    public override async Task DownloadContent(
        ContentDownloadRequest request,
        IServerStreamWriter<ContentChunk> responseStream,
        ServerCallContext context)
    {
        _logger.LogInformation("Content download requested: {ContentId}", request.ContentId);

        if (!_contentRegistry.TryGetValue(request.ContentId, out var filePath))
        {
            _logger.LogError("Content {ContentId} not found in registry", request.ContentId);
            throw new RpcException(new Status(StatusCode.NotFound, $"Content '{request.ContentId}' not found"));
        }

        if (!File.Exists(filePath))
        {
            _logger.LogError("Content file not found on disk: {FilePath}", filePath);
            throw new RpcException(new Status(StatusCode.NotFound, $"File not found: {filePath}"));
        }

        await _downloadSlots.WaitAsync(context.CancellationToken);
        try
        {
            var fileInfo = new FileInfo(filePath);
            var filename = fileInfo.Name;
            var fileSize = fileInfo.Length;
            const int chunkSize = 1024 * 1024; // 1 MB chunks
            var totalChunks = (int)Math.Ceiling((double)fileSize / chunkSize);

            // Compute file hash
            var fileHash = await ComputeFileHashAsync(filePath);

            _logger.LogInformation("Streaming content {ContentId}: {Filename} ({FileSize} bytes, {TotalChunks} chunks)",
                request.ContentId, filename, fileSize, totalChunks);

            await using var fileStream = File.OpenRead(filePath);
            var buffer = new byte[chunkSize];
            int chunkIndex = 0;

            while (true)
            {
                var bytesRead = await fileStream.ReadAsync(buffer, context.CancellationToken);
                if (bytesRead == 0) break;

                var chunk = new ContentChunk
                {
                    ContentId = request.ContentId,
                    Filename = filename,
                    TotalSize = fileSize,
                    ChunkIndex = chunkIndex,
                    TotalChunks = totalChunks,
                    Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead),
                    Hash = fileHash
                };

                await responseStream.WriteAsync(chunk);
                chunkIndex++;
            }

            _logger.LogInformation("Content download complete: {ContentId} ({TotalChunks} chunks sent)", request.ContentId, totalChunks);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Content download cancelled: {ContentId}", request.ContentId);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming content {ContentId}", request.ContentId);
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
        finally
        {
            _downloadSlots.Release();
        }
    }

    #endregion

    #region Remote Log Fetch

    /// <summary>
    /// Receive log data from a client
    /// </summary>
    public override Task<LogReceiveResponse> SendClientLogs(
        ClientLogData request,
        ServerCallContext context)
    {
        _logger.LogInformation("Received logs from client {ClientId}: {Length} chars",
            request.ClientId, request.LogContent.Length);

        _clientLogs[request.ClientId] = request;

        // Raise event for UI
        ClientLogsReceived?.Invoke(this, new ClientLogsReceivedEventArgs(request.ClientId, request.LogContent));

        return Task.FromResult(new LogReceiveResponse
        {
            Success = true,
            Message = "Logs received"
        });
    }

    /// <summary>
    /// Request a client to send its logs by sending FETCH_LOGS command
    /// </summary>
    public async Task RequestClientLogsAsync(string clientId)
    {
        var command = new SyncCommand
        {
            Type = CommandType.FetchLogs,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ContentId = clientId
        };

        await SendCommandToClientAsync(clientId, command);
        _logger.LogInformation("Requested logs from client {ClientId}", clientId);
    }

    /// <summary>
    /// Get the latest logs received from a client
    /// </summary>
    public string? GetClientLogs(string clientId)
    {
        return _clientLogs.TryGetValue(clientId, out var logData) ? logData.LogContent : null;
    }

    /// <summary>
    /// Event raised when client logs are received
    /// </summary>
    public event EventHandler<ClientLogsReceivedEventArgs>? ClientLogsReceived;

    #endregion

    #region Distributed Animation Composition (Phase 3)

    /// <summary>
    /// Delegate to handle animation preparation requests
    /// </summary>
    public delegate Task<AnimationAck> OnPrepareAnimationDelegate(string clientId, AnimationPrepare prepare);
    public event OnPrepareAnimationDelegate? OnPrepareAnimation;

    /// <summary>
    /// Delegate to handle animation ready confirmations
    /// </summary>
    public delegate Task<AnimationAck> OnAnimationReadyDelegate(AnimationReady ready);
    public event OnAnimationReadyDelegate? OnAnimationReady;

    /// <summary>
    /// Delegate to handle animation completion reports
    /// </summary>
    public delegate Task<AnimationAck> OnAnimationCompleteDelegate(AnimationCompleteReport report);
    public event OnAnimationCompleteDelegate? OnAnimationComplete;

    /// <summary>
    /// Server sends animation preparation to client (warm-up phase)
    /// </summary>
    public override async Task<AnimationAck> PrepareAnimation(
        AnimationPrepare request,
        ServerCallContext context)
    {
        var clientId = context.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? "unknown";

        _logger.LogInformation(
            "Received AnimationPrepare request: AnimationId={AnimationId}, ClientId={ClientId}",
            request.AnimationId, clientId);

        try
        {
            // Fire event for orchestrator/coordinator to handle
            if (OnPrepareAnimation != null)
            {
                return await OnPrepareAnimation.Invoke(clientId, request);
            }

            return new AnimationAck
            {
                Success = true,
                Message = "Animation prepared successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error preparing animation: {AnimationId}", request.AnimationId);
            return new AnimationAck
            {
                Success = false,
                Message = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Client confirms animation renderer is ready
    /// </summary>
    public override async Task<AnimationAck> ReportAnimationReady(
        AnimationReady request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            "Received AnimationReady confirmation: AnimationId={AnimationId}, ClientId={ClientId}, ReadyAt={ReadyAt}",
            request.AnimationId, request.ClientId, request.ReadyTimestampUtc);

        try
        {
            // Fire event for orchestrator to handle
            if (OnAnimationReady != null)
            {
                return await OnAnimationReady.Invoke(request);
            }

            return new AnimationAck
            {
                Success = true,
                Message = "Animation ready confirmation received"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling animation ready: {AnimationId}", request.AnimationId);
            return new AnimationAck
            {
                Success = false,
                Message = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Server sends animation start signal to client
    /// </summary>
    public override Task<AnimationAck> SendAnimationStart(
        AnimationMetadata request,
        ServerCallContext context)
    {
        var clientId = context.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? "unknown";

        _logger.LogInformation(
            "Sending animation start: AnimationId={AnimationId}, ClientId={ClientId}, StartTime={StartTime}ms",
            request.AnimationId, clientId, request.StartTimestampUtc);

        // Note: This is the basic server handler
        // The actual RPC dispatch will be handled by WallpaperSyncClient on client side
        return Task.FromResult(new AnimationAck
        {
            Success = true,
            Message = "Animation start received"
        });
    }

    /// <summary>
    /// Client reports animation completion
    /// </summary>
    public override async Task<AnimationAck> ReportAnimationComplete(
        AnimationCompleteReport request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            "Received animation completion: AnimationId={AnimationId}, ClientId={ClientId}, " +
            "Started={StartedAt}, Completed={CompletedAt}, Duration={Duration}ms",
            request.AnimationId, request.ClientId, request.StartedTimestampUtc,
            request.CompletedTimestampUtc, request.ActualDurationMs);

        try
        {
            // Fire event for orchestrator to handle handoff to next client
            if (OnAnimationComplete != null)
            {
                return await OnAnimationComplete.Invoke(request);
            }

            return new AnimationAck
            {
                Success = true,
                Message = "Animation completion received"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling animation completion: {AnimationId}", request.AnimationId);
            return new AnimationAck
            {
                Success = false,
                Message = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Server stops animation on a client
    /// </summary>
    public override Task<AnimationAck> StopAnimation(
        AnimationStopRequest request,
        ServerCallContext context)
    {
        var clientId = context.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? "unknown";

        _logger.LogInformation(
            "Stopping animation: AnimationId={AnimationId}, ClientId={ClientId}",
            request.AnimationId, clientId);

        return Task.FromResult(new AnimationAck
        {
            Success = true,
            Message = "Animation stop command received"
        });
    }

    /// <summary>
    /// Get current status of animation on client
    /// </summary>
    public override Task<AnimationStatusResponse> GetAnimationStatus(
        AnimationStatusRequest request,
        ServerCallContext context)
    {
        _logger.LogDebug("Getting animation status: AnimationId={AnimationId}", request.AnimationId);

        // Placeholder implementation
        return Task.FromResult(new AnimationStatusResponse
        {
            AnimationId = request.AnimationId,
            Status = AnimationStatusResponse.Types.AnimationStatus.Idle,
            CurrentPositionMs = 0
        });
    }

    #endregion

}

public class ClientLogsReceivedEventArgs : EventArgs
{
    public string ClientId { get; }
    public string LogContent { get; }

    public ClientLogsReceivedEventArgs(string clientId, string logContent)
    {
        ClientId = clientId;
        LogContent = logContent;
    }
}

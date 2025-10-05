using Grpc.Core;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace WaBiBaBuSy.Grpc.Services;

/// <summary>
/// Server-side implementation of the WallpaperSync gRPC service
/// </summary>
public class WallpaperSyncService : WallpaperSync.WallpaperSyncBase
{
    private readonly ILogger<WallpaperSyncService> _logger;
    private readonly ConcurrentDictionary<string, ConnectedClient> _connectedClients;
    private int _nextClientOrder = 1;

    public WallpaperSyncService(ILogger<WallpaperSyncService> logger)
    {
        _logger = logger;
        _connectedClients = new ConcurrentDictionary<string, ConnectedClient>();
    }

    /// <summary>
    /// Register a new client connection
    /// </summary>
    public override Task<RegistrationResponse> RegisterClient(
        ClientInfo request,
        ServerCallContext context)
    {
        _logger.LogInformation("Client registration request from {Hostname} ({IpAddress})",
            request.Hostname, request.IpAddress);

        try
        {
            // Generate client ID if not provided
            var clientId = string.IsNullOrEmpty(request.ClientId)
                ? Guid.NewGuid().ToString()
                : request.ClientId;

            // Create connected client record
            var connectedClient = new ConnectedClient
            {
                ClientId = clientId,
                Hostname = request.Hostname,
                IpAddress = request.IpAddress,
                OrderPosition = _nextClientOrder++,
                Status = ClientStatusEnum.ClientConnected,
                LastHeartbeat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ScreenConfig = request.ScreenConfig
            };

            // Add or update client
            _connectedClients.AddOrUpdate(clientId, connectedClient,
                (key, existing) => connectedClient);

            _logger.LogInformation("Client {ClientId} registered successfully. Order position: {OrderPosition}",
                clientId, connectedClient.OrderPosition);

            return Task.FromResult(new RegistrationResponse
            {
                Success = true,
                Message = "Registration successful",
                AssignedClientId = clientId,
                OrderPosition = connectedClient.OrderPosition
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering client");
            return Task.FromResult(new RegistrationResponse
            {
                Success = false,
                Message = $"Registration failed: {ex.Message}"
            });
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
    /// Client sends commands/responses, server processes them
    /// </summary>
    public override async Task SyncStream(
        IAsyncStreamReader<SyncCommand> requestStream,
        IServerStreamWriter<SyncResponse> responseStream,
        ServerCallContext context)
    {
        _logger.LogInformation("SyncStream started");

        try
        {
            // Listen for client commands/status
            await foreach (var command in requestStream.ReadAllAsync(context.CancellationToken))
            {
                _logger.LogInformation("Received sync command: {CommandType} for content {ContentId}",
                    command.Type, command.ContentId);

                // TODO: Process commands and broadcast to other clients
                // This will be implemented when integrating with the wallpaper engine

                // Send acknowledgment back
                await responseStream.WriteAsync(new SyncResponse
                {
                    ClientId = "server",
                    SequenceNumber = command.SequenceNumber,
                    Acknowledged = true,
                    State = WallpaperStateEnum.WallpaperPlaying
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SyncStream");
        }
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

            // TODO: Reassemble chunks and save file
            // This will be implemented when adding content management

            _logger.LogInformation("Content transfer completed for {Filename}. Received {ChunksReceived}/{TotalChunks} chunks",
                filename, chunksReceived, totalChunks);

            return new TransferStatus
            {
                Success = true,
                Message = "Transfer completed successfully",
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
                    _logger.LogDebug("Updated client {ClientId} to position {Position}",
                        orderItem.ClientId, orderItem.NewPosition);
                }
            }

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
    /// Get all connected clients (for external access)
    /// </summary>
    public IEnumerable<ConnectedClient> GetConnectedClients()
    {
        return _connectedClients.Values.OrderBy(c => c.OrderPosition);
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
        return removed;
    }
}

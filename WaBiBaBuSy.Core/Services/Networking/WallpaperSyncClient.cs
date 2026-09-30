using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Common.IO;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Networking;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Google.Protobuf;
using System.Text.Json;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using AppVersionInfo = WaBiBaBuSy.Common.Version.VersionInfo;

namespace WaBiBaBuSy.Core.Services.Networking;

/// <summary>
/// Client wrapper for WallpaperSync gRPC service
/// </summary>
public class WallpaperSyncClient : IDisposable
{
    private readonly ILogger<WallpaperSyncClient> _logger;
    private readonly ClientConfiguration _configuration;
    private GrpcChannel? _channel;
    private WallpaperSync.WallpaperSyncClient? _client;
    private string? _clientId;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private CancellationTokenSource? _syncStreamCts;
    private Task? _syncStreamTask;
    private AsyncDuplexStreamingCall<SyncResponse, SyncCommand>? _syncStreamCall;
    private CancellationTokenSource? _thumbnailCts;
    private Task? _thumbnailTask;
    private ThumbnailCaptureService? _thumbnailCaptureService;
    // Thumbnail upload: server-driven pause (heartbeat response), 5 s cadence, unchanged frames skipped.
    private const int ThumbnailIntervalMs = 5000;
    private volatile bool _thumbnailsPaused;
    private byte[]? _lastSentThumbnail;
    private long _thumbnailDueTick;
    private readonly ClockOffsetEstimator _clockOffset = new();

    // Automated test mode (2026-09-30): simulated skew of this machine's clock and the players to probe.
    private volatile int _clockSkewMs;
    private readonly PerfSampler _perfSampler = new();

    /// <summary>Set by the UI: this client's running D2D players (one per monitor that plays).</summary>
    public Func<IReadOnlyList<ITestProbeTarget>>? TestProbeTargets { get; set; }

    /// <summary>Last TEST_MODE from the server; applied to players created later (before their start).</summary>
    public (bool Timecode, int ClockSkewMs) TestModeState { get; private set; }

    /// <summary>This machine's clock as the sync sees it: real UTC plus the simulated test skew (0 outside tests).</summary>
    private long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _clockSkewMs;

    /// <summary>This machine's clock as the sync sees it (real UTC + simulated test skew).</summary>
    public long NowUtcMs => NowMs();
    private readonly ReconnectBackoff _backoff = new();
    private string? _serverAddress;
    private int _serverPort;
    private volatile bool _userDisconnected;
    private int _reconnecting;
    private int _heartbeatFailures;

    public bool IsConnected { get; private set; }
    public string? ClientId => _clientId;

    /// <summary>Set by the playback service: (cached, total) of the last PREFETCH list, reported via heartbeat.</summary>
    public Func<(int Ready, int Total)>? PrefetchStatusProvider { get; set; }

    /// <summary>Estimated (server - client) clock offset in ms, from heartbeat round-trips.</summary>
    public long ClockOffsetMs => _clockOffset.OffsetMs;

    /// <summary>
    /// Get the raw gRPC client stub for direct RPC calls (used by UpdateManager/UpdateDownloader)
    /// </summary>
    public WallpaperSync.WallpaperSyncClient? GrpcClient => _client;

    /// <summary>
    /// Set the thumbnail capture service for sending live wallpaper previews to the server.
    /// </summary>
    public ThumbnailCaptureService? ThumbnailCaptureService
    {
        get => _thumbnailCaptureService;
        set => _thumbnailCaptureService = value;
    }

    public event EventHandler<ConnectionStatusChangedEventArgs>? ConnectionStatusChanged;
    public event EventHandler<SyncCommandReceivedEventArgs>? SyncCommandReceived;
    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;

    // Animation composition events (Phase 3)
    public event EventHandler<AnimationPrepareReceivedEventArgs>? AnimationPrepareReceived;
    public event EventHandler<AnimationStartReceivedEventArgs>? AnimationStartReceived;

    public WallpaperSyncClient(
        ILogger<WallpaperSyncClient> logger,
        ClientConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        // Persistent identity: reuse the id the server assigned last time so this machine keeps
        // its topology seat across client restarts and session-resume can match it.
        if (!string.IsNullOrWhiteSpace(configuration.ClientId))
            _clientId = configuration.ClientId;
    }

    /// <summary>
    /// Connect to the WaBiBaBuSy server
    /// </summary>
    public async Task<bool> ConnectAsync(string serverAddress, int serverPort)
    {
        if (IsConnected)
        {
            _logger.LogWarning("Already connected to server");
            return true;
        }

        try
        {
            _logger.LogInformation("Connecting to server at {ServerAddress}:{ServerPort}",
                serverAddress, serverPort);

            _serverAddress = serverAddress;
            _serverPort = serverPort;
            _userDisconnected = false;

            // Create gRPC channel
            var serverUrl = $"http://{serverAddress}:{serverPort}";
            _channel = GrpcChannel.ForAddress(serverUrl);
            _client = new WallpaperSync.WallpaperSyncClient(_channel);

            // Register with server
            var registration = await RegisterAsync();
            if (!registration)
            {
                _logger.LogError("Failed to register with server");
                return false;
            }

            IsConnected = true;
            _heartbeatFailures = 0;
            _backoff.Reset();
            ConnectionStatusChanged?.Invoke(this,
                new ConnectionStatusChangedEventArgs(true, serverAddress, serverPort));

            // Fresh session: the server lost our last thumbnail, and until the first heartbeat
            // answers we don't know whether anyone is watching.
            _thumbnailsPaused = true;
            _lastSentThumbnail = null;
            _thumbnailDueTick = 0;

            // Start heartbeat
            StartHeartbeat();

            // Start sync stream to receive commands
            StartSyncStream();

            // Start thumbnail sending
            StartThumbnailSending();

            _logger.LogInformation("Successfully connected to server. Client ID: {ClientId}", _clientId);
            return true;
        }
        catch (global::Grpc.Core.RpcException rpcEx)
        {
            _logger.LogError("[Connect] gRPC error connecting to {Address}:{Port} — Status={Status}, Detail={Detail}",
                serverAddress, serverPort, rpcEx.StatusCode, rpcEx.Status.Detail);
            IsConnected = false;
            ConnectionStatusChanged?.Invoke(this,
                new ConnectionStatusChangedEventArgs(false, serverAddress, serverPort));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Connect] Error connecting to {Address}:{Port} — {Message}",
                serverAddress, serverPort, ex.InnerException?.Message ?? ex.Message);
            IsConnected = false;
            ConnectionStatusChanged?.Invoke(this,
                new ConnectionStatusChangedEventArgs(false, serverAddress, serverPort));
            return false;
        }
    }

    /// <summary>
    /// Disconnect from the server
    /// </summary>
    public async Task DisconnectAsync()
    {
        // Mark as user-initiated FIRST so any in-flight reconnect loop stops.
        _userDisconnected = true;

        if (!IsConnected)
        {
            return;
        }

        _logger.LogInformation("Disconnecting from server");

        StopHeartbeat();
        StopSyncStream();
        StopThumbnailSending();

        IsConnected = false;
        ConnectionStatusChanged?.Invoke(this,
            new ConnectionStatusChangedEventArgs(false, string.Empty, 0));

        if (_channel != null)
        {
            await _channel.ShutdownAsync();
            _channel.Dispose();
            _channel = null;
        }

        _client = null;
        // _clientId is intentionally kept: it is this machine's persistent identity
        // (ClientConfiguration.ClientId), not a per-session token.

        _logger.LogInformation("Disconnected from server");
    }

    /// <summary>
    /// Begin auto-reconnection after an unexpected connection loss.
    /// No-op if the user disconnected intentionally or a reconnect is already in flight.
    /// </summary>
    private void TriggerReconnect(string reason)
    {
        if (_userDisconnected) return;
        if (Interlocked.Exchange(ref _reconnecting, 1) == 1) return;

        _logger.LogWarning("Connection lost ({Reason}) — starting auto-reconnect with exponential backoff", reason);
        IsConnected = false;
        ConnectionStatusChanged?.Invoke(this,
            new ConnectionStatusChangedEventArgs(false, _serverAddress ?? string.Empty, _serverPort));

        _ = Task.Run(ReconnectLoopAsync);
    }

    /// <summary>
    /// Retry loop: tear down the dead channel and reconnect with 1s..30s backoff
    /// until connected or the user disconnects. _clientId is preserved across
    /// attempts so the server can resume our session.
    /// </summary>
    private async Task ReconnectLoopAsync()
    {
        try
        {
            while (!_userDisconnected && !string.IsNullOrEmpty(_serverAddress))
            {
                var delay = _backoff.NextDelay();
                _logger.LogInformation("Reconnecting to {Address}:{Port} in {Delay}s...",
                    _serverAddress, _serverPort, delay.TotalSeconds);
                await Task.Delay(delay);
                if (_userDisconnected) return;

                await TeardownChannelAsync();

                try
                {
                    if (await ConnectAsync(_serverAddress!, _serverPort))
                    {
                        _logger.LogInformation("Reconnected to server after connection loss");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Reconnect attempt failed: {Message}", ex.Message);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    /// <summary>
    /// Tear down streams and channel WITHOUT clearing _clientId or firing the
    /// user-facing disconnect path — used between reconnect attempts.
    /// </summary>
    private async Task TeardownChannelAsync()
    {
        StopHeartbeat();
        StopSyncStream();
        StopThumbnailSending();

        if (_channel != null)
        {
            try { await _channel.ShutdownAsync(); } catch { /* channel already dead */ }
            _channel.Dispose();
            _channel = null;
        }
        _client = null;
    }

    /// <summary>
    /// Register this client with the server
    /// </summary>
    private async Task<bool> RegisterAsync()
    {
        if (_client == null)
        {
            return false;
        }

        try
        {
            var clientInfo = new ClientInfo
            {
                ClientId = _clientId ?? string.Empty,
                Hostname = Environment.MachineName,
                IpAddress = GetLocalIpAddress(),
                RegistrationTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ScreenConfig = GetScreenConfiguration(),
                AppVersion = AppVersionInfo.AppVersion,
                BuildNumber = AppVersionInfo.BuildNumber,
                FrameworkVersion = AppVersionInfo.FrameworkVersion
            };

            _logger.LogInformation("Registering with server. Version: {Version}, Build: {Build}",
                AppVersionInfo.AppVersion, AppVersionInfo.BuildNumber);

            var response = await _client.RegisterClientAsync(clientInfo);

            if (response.Success)
            {
                _clientId = response.AssignedClientId;
                _logger.LogInformation("Registered with server. Client ID: {ClientId}, Order: {Order}",
                    _clientId, response.OrderPosition);

                if (!string.IsNullOrWhiteSpace(_clientId) && _configuration.ClientId != _clientId)
                {
                    _configuration.ClientId = _clientId;
                    ConfigurationManager.UpdateClientId(_clientId);
                }

                // Check if update is available
                if (response.UpdateAvailable)
                {
                    _logger.LogWarning("Update available! Server version: {Version} build {Build}",
                        response.RequiredVersion, response.RequiredBuildNumber);
                    _logger.LogInformation("Update size: {Size:N0} bytes - {Description}",
                        response.UpdatePackageSize, response.UpdateDescription);

                    // Raise event to notify UI about available update
                    UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs
                    {
                        ServerVersion = response.RequiredVersion,
                        ServerBuildNumber = response.RequiredBuildNumber,
                        PackageSize = response.UpdatePackageSize,
                        Description = response.UpdateDescription
                    });
                }

                return true;
            }
            else
            {
                _logger.LogError("Registration failed: {Message}", response.Message);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during registration");
            return false;
        }
    }

    /// <summary>
    /// Start sending periodic heartbeats to the server
    /// </summary>
    private void StartHeartbeat()
    {
        _heartbeatCts = new CancellationTokenSource();
        _heartbeatTask = Task.Run(async () =>
        {
            while (!_heartbeatCts.Token.IsCancellationRequested && IsConnected)
            {
                try
                {
                    await SendHeartbeatAsync();
                    _heartbeatFailures = 0;
                    await Task.Delay(
                        TimeSpan.FromSeconds(_configuration.HeartbeatIntervalSeconds),
                        _heartbeatCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _heartbeatFailures++;
                    _logger.LogError(ex, "Error sending heartbeat (consecutive failures: {Count})", _heartbeatFailures);
                    if (_heartbeatFailures >= 3)
                    {
                        TriggerReconnect("3 consecutive heartbeat failures");
                        break;
                    }
                    try
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(_configuration.HeartbeatIntervalSeconds),
                            _heartbeatCts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, _heartbeatCts.Token);
    }

    /// <summary>
    /// Stop heartbeat task
    /// </summary>
    private void StopHeartbeat()
    {
        _heartbeatCts?.Cancel();
        _heartbeatTask?.Wait(TimeSpan.FromSeconds(2));
        _heartbeatCts?.Dispose();
        _heartbeatCts = null;
        _heartbeatTask = null;
    }

    /// <summary>
    /// Send heartbeat to server
    /// </summary>
    private async Task SendHeartbeatAsync()
    {
        if (_client == null || string.IsNullOrEmpty(_clientId))
        {
            return;
        }

        var sendMs = NowMs();
        var request = new HeartbeatRequest
        {
            ClientId = _clientId,
            Timestamp = sendMs,
            Status = ClientStatusEnum.ClientConnected
        };

        // Drift telemetry: report the current estimate (from prior round-trips).
        // First heartbeat has no sample yet — HasDriftReport stays false so the
        // server doesn't mistake default-0 for a perfect sync.
        if (_clockOffset.HasSamples)
        {
            request.ClockOffsetMs = _clockOffset.OffsetMs;
            request.RttMs = _clockOffset.RttMs;
            request.HasDriftReport = true;
        }

        var prefetch = PrefetchStatusProvider?.Invoke();
        if (prefetch.HasValue)
        {
            request.PrefetchReady = prefetch.Value.Ready;
            request.PrefetchTotal = prefetch.Value.Total;
        }

        var response = await _client.HeartbeatAsync(request);
        var receiveMs = NowMs();

        // NTP-style clock sync: every heartbeat is a free offset/RTT sample.
        if (response.Acknowledged && response.ServerTimestamp != 0)
            _clockOffset.AddSample(sendMs, response.ServerTimestamp, receiveMs);
        if (response.Acknowledged)
            _thumbnailsPaused = response.ThumbnailsPaused;

        _logger.LogDebug("Heartbeat acknowledged: {Acknowledged}, clock offset: {Offset}ms",
            response.Acknowledged, _clockOffset.OffsetMs);
    }

    /// <summary>
    /// Get network topology from server
    /// </summary>
    public async Task<TopologyResponse?> GetTopologyAsync()
    {
        if (_client == null || !IsConnected)
        {
            _logger.LogWarning("Cannot get topology - not connected");
            return null;
        }

        try
        {
            var response = await _client.GetTopologyAsync(new TopologyRequest());
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting topology");
            return null;
        }
    }

    /// <summary>
    /// Update physical distance for a client on the server
    /// </summary>
    public async Task<bool> UpdateClientDistanceAsync(string clientId, int distanceCm)
    {
        if (_client == null || !IsConnected)
        {
            _logger.LogWarning("Cannot update client distance - not connected");
            return false;
        }

        try
        {
            var request = new ClientDistanceUpdate
            {
                ClientId = clientId,
                PhysicalDistanceCm = distanceCm
            };

            var response = await _client.UpdateClientDistanceAsync(request);

            if (response.Success)
            {
                _logger.LogInformation("Successfully updated distance for client {ClientId} to {Distance} cm",
                    clientId, distanceCm);
            }
            else
            {
                _logger.LogWarning("Failed to update distance for client {ClientId}: {Message}",
                    clientId, response.Message);
            }

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating client distance");
            return false;
        }
    }

    /// <summary>
    /// Update the order of clients in the topology on the server
    /// </summary>
    public async Task<bool> UpdateClientOrderAsync(Dictionary<string, int> clientOrders)
    {
        if (_client == null || !IsConnected)
        {
            _logger.LogWarning("Cannot update client order - not connected");
            return false;
        }

        try
        {
            var request = new ClientOrderUpdate();
            foreach (var (clientId, newPosition) in clientOrders)
            {
                request.ClientOrders.Add(new ClientOrderItem
                {
                    ClientId = clientId,
                    NewPosition = newPosition
                });
            }

            var response = await _client.UpdateClientOrderAsync(request);

            if (response.Success)
            {
                _logger.LogInformation("Successfully updated order for {Count} clients", clientOrders.Count);
            }
            else
            {
                _logger.LogWarning("Failed to update client order: {Message}", response.Message);
            }

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating client order");
            return false;
        }
    }

    /// <summary>
    /// Transfer a content file to the server
    /// </summary>
    public async Task<bool> TransferContentAsync(string filePath, string contentId, int chunkSizeBytes = 1024 * 1024)
    {
        if (_client == null || !IsConnected)
        {
            _logger.LogWarning("Cannot transfer content - not connected");
            return false;
        }

        if (!File.Exists(filePath))
        {
            _logger.LogError("File not found: {FilePath}", filePath);
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            var filename = fileInfo.Name;
            var fileSize = fileInfo.Length;
            var totalChunks = (int)Math.Ceiling((double)fileSize / chunkSizeBytes);

            _logger.LogInformation("Starting content transfer: {Filename} ({FileSize} bytes, {TotalChunks} chunks)",
                filename, fileSize, totalChunks);

            // Compute file hash
            var fileHash = await ComputeFileHashAsync(filePath);

            // Start streaming call
            using var call = _client.TransferContent();

            // Read and send chunks
            await using var fileStream = File.OpenRead(filePath);
            var buffer = new byte[chunkSizeBytes];
            int chunkIndex = 0;

            while (true)
            {
                var bytesRead = await fileStream.ReadAsync(buffer);
                if (bytesRead == 0)
                    break;

                var chunk = new ContentChunk
                {
                    ContentId = contentId,
                    Filename = filename,
                    TotalSize = fileSize,
                    ChunkIndex = chunkIndex,
                    TotalChunks = totalChunks,
                    Data = ByteString.CopyFrom(buffer, 0, bytesRead),
                    Hash = fileHash
                };

                await call.RequestStream.WriteAsync(chunk);

                _logger.LogDebug("Sent chunk {ChunkIndex}/{TotalChunks} ({BytesRead} bytes)",
                    chunkIndex, totalChunks, bytesRead);

                chunkIndex++;
            }

            // Complete the request
            await call.RequestStream.CompleteAsync();

            // Get response
            var response = await call.ResponseAsync;

            if (response.Success)
            {
                _logger.LogInformation("Content transfer completed successfully: {Filename}", filename);
                return true;
            }
            else
            {
                _logger.LogError("Content transfer failed: {Message}", response.Message);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transferring content");
            return false;
        }
    }

    /// <summary>
    /// Download content from server by content ID.
    /// Saves to the specified cache directory and returns the local file path.
    /// </summary>
    public async Task<string?> DownloadContentAsync(string contentId, string cacheDirectory)
    {
        if (_client == null || !IsConnected)
        {
            _logger.LogWarning("Cannot download content - not connected");
            return null;
        }

        try
        {
            _logger.LogInformation("Downloading content {ContentId} from server", contentId);

            Directory.CreateDirectory(cacheDirectory);

            var request = new ContentDownloadRequest { ContentId = contentId };
            using var call = _client.DownloadContent(request);

            string? filename = null;
            string? fileHash = null;
            var chunks = new List<ContentChunk>();

            await foreach (var chunk in call.ResponseStream.ReadAllAsync())
            {
                filename ??= chunk.Filename;
                fileHash ??= chunk.Hash;
                chunks.Add(chunk);

                _logger.LogDebug("Received chunk {ChunkIndex}/{TotalChunks} for {ContentId}",
                    chunk.ChunkIndex, chunk.TotalChunks, contentId);
            }

            if (chunks.Count == 0 || filename == null)
            {
                _logger.LogError("No data received for content {ContentId}", contentId);
                return null;
            }

            // Sort chunks and write to file
            var sortedChunks = chunks.OrderBy(c => c.ChunkIndex).ToList();
            var filePath = Path.Combine(cacheDirectory, filename);

            await using (var fileStream = File.Create(filePath))
            {
                foreach (var chunk in sortedChunks)
                {
                    await fileStream.WriteAsync(chunk.Data.ToByteArray());
                }
            }

            // Verify hash
            if (!string.IsNullOrEmpty(fileHash))
            {
                var actualHash = await ComputeFileHashAsync(filePath);
                if (!actualHash.Equals(fileHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Hash mismatch for content {ContentId}. Expected: {Expected}, Actual: {Actual}",
                        contentId, fileHash, actualHash);
                    File.Delete(filePath);
                    return null;
                }
            }

            _logger.LogInformation("Content {ContentId} downloaded successfully: {FilePath} ({Chunks} chunks)",
                contentId, filePath, chunks.Count);

            // Add to local wallpaper gallery so it appears in the UI
            try
            {
                if (ConfigurationManager.AddToGalleryIfMissing(filePath))
                {
                    _logger.LogInformation("Added downloaded content to wallpaper gallery: {FilePath}", filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add downloaded content to gallery (non-critical)");
            }

            return filePath;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            _logger.LogError("Content {ContentId} not found on server", contentId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading content {ContentId}", contentId);
            return null;
        }
    }

    /// <summary>
    /// Upload the local log file to the server (FETCH_LOGS). Always answers: a read failure is
    /// sent back as text so the server never waits forever. A nonzero window returns only the
    /// lines between the two UTC instants (test runs).
    /// </summary>
    public async Task SendLogsToServerAsync(string logDirectory, long fromUtcMs = 0, long toUtcMs = 0)
    {
        if (_client == null || string.IsNullOrEmpty(_clientId))
        {
            _logger.LogWarning("Cannot send logs - not connected");
            return;
        }

        var logDate = DateTime.Today.ToString("yyyy-MM-dd");
        var logPath = Path.Combine(logDirectory, $"wabibabusy-{logDate}.log");
        string logContent;
        try
        {
            if (File.Exists(logPath))
            {
                bool windowed = fromUtcMs > 0 && toUtcMs >= fromUtcMs;
                var lines = await LogTail.ReadLastLinesAsync(logPath, windowed ? 20000 : 500);
                if (windowed)
                    lines = LogTail.FilterWindow(lines, ToLocalTimeOfDay(fromUtcMs), ToLocalTimeOfDay(toUtcMs));
                logContent = string.Join(Environment.NewLine, lines);
            }
            else
            {
                logContent = $"[No log file at {logPath} — enable Settings → Logging → Log to file on this machine]";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read log file {Path}", logPath);
            logContent = $"[Could not read {logPath}: {ex.Message}]";
        }

        try
        {
            var response = await _client.SendClientLogsAsync(new ClientLogData
            {
                ClientId = _clientId,
                LogContent = logContent,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LogDate = logDate
            });
            _logger.LogInformation("Logs sent to server: {Success}", response.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending logs to server");
        }
    }

    /// <summary>
    /// TEST_MODE: remember the state (for players created later), switch the simulated skew and
    /// re-measure the clock offset right away, then forward to the running players.
    /// </summary>
    private void ApplyTestMode(SyncParameters p)
    {
        if (!_configuration.AllowTestRuns)
        {
            _logger.LogWarning("Ignoring TEST_MODE: test runs are disabled on this client");
            return;
        }

        TestModeState = (p.TestTimecode, p.TestClockSkewMs);
        if (_clockSkewMs != p.TestClockSkewMs)
        {
            _clockSkewMs = p.TestClockSkewMs;
            _clockOffset.Reset();   // old samples were taken with the old clock
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    try { await SendHeartbeatAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Re-sync heartbeat failed"); }
                    await Task.Delay(300);
                }
            });
        }
        _logger.LogInformation("[Test] Timecode={Timecode} SimulatedSkew={Skew}ms", p.TestTimecode, p.TestClockSkewMs);

        var targets = TestProbeTargets?.Invoke() ?? Array.Empty<ITestProbeTarget>();
        _ = ProbeFanOut.SetTestModeAllAsync(targets, p.TestTimecode, p.TestClockSkewMs);
    }

    /// <summary>TEST_PROBE: probe every local player at the (already local) instant and upload the result.</summary>
    private async Task HandleTestProbeAsync(SyncCommand command)
    {
        var p = command.Params ?? new SyncParameters();
        var result = new RemoteProbeResult
        {
            ClientId = _clientId ?? string.Empty,
            ProbeId = p.TestProbeId,
            ClockOffsetMs = _clockOffset.OffsetMs,
            RttMs = _clockOffset.RttMs,
        };
        try
        {
            var targets = TestProbeTargets?.Invoke() ?? Array.Empty<ITestProbeTarget>();
            if (!_configuration.AllowTestRuns) result.Error = TestModeErrors.Disabled;
            else if (targets.Count == 0) result.Error = TestModeErrors.NoPlayer;
            else
            {
                var request = new PlayerProbeRequest
                {
                    ProbeId = p.TestProbeId,
                    AtLocalUtcMs = command.TimestampUtc,   // converted to this machine's clock on receipt
                    Capture = p.TestCapture,
                    ExactElapsedMs = p.TestHasExactElapsed ? p.TestExactElapsedMs : null,
                    CaptureDirectory = Path.Combine(Path.GetTempPath(), "WaBiBaBuSy", "probes"),
                };
                var wait = TimeSpan.FromMilliseconds(Math.Max(0, command.TimestampUtc - NowMs()) + 2000);
                result.Replies = (await ProbeFanOut.ProbeAllAsync(targets, request, wait, CancellationToken.None)).ToList();
                foreach (var reply in result.Replies)
                {
                    if (reply.CapturePath == null || !File.Exists(reply.CapturePath)) continue;
                    result.Captures[reply.MonitorIndex] = await File.ReadAllBytesAsync(reply.CapturePath);
                    try { File.Delete(reply.CapturePath); } catch { /* temp file */ }
                }
            }
            result.Perf = _perfSampler.Sample();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test probe {ProbeId} failed", p.TestProbeId);
            result.Error ??= ex.Message;
        }
        await SubmitProbeResultAsync(result);
    }

    private async Task SubmitProbeResultAsync(RemoteProbeResult result)
    {
        if (_client == null) return;
        const int chunkSize = 256 * 1024;
        try
        {
            using var call = _client.SubmitProbeResult();
            await call.RequestStream.WriteAsync(new ProbeResultChunk
            {
                Header = new ProbeResultHeader { ClientId = result.ClientId, ProbeId = result.ProbeId, ResultJson = JsonSerializer.Serialize(result) }
            });
            foreach (var (monitor, png) in result.Captures)
                for (int offset = 0; offset < png.Length; offset += chunkSize)
                    await call.RequestStream.WriteAsync(new ProbeResultChunk
                    {
                        Capture = new ProbeCaptureChunk { MonitorIndex = monitor, Data = ByteString.CopyFrom(png, offset, Math.Min(chunkSize, png.Length - offset)) }
                    });
            await call.RequestStream.CompleteAsync();
            await call.ResponseAsync;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not upload probe result {ProbeId}", result.ProbeId);
        }
    }

    private static TimeSpan ToLocalTimeOfDay(long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToLocalTime().TimeOfDay;

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

    /// <summary>
    /// Get local IP address
    /// </summary>
    private string GetLocalIpAddress()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            var ipAddress = host.AddressList
                .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return ipAddress?.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Start sync stream to receive commands from server
    /// </summary>
    private void StartSyncStream()
    {
        if (_client == null || string.IsNullOrEmpty(_clientId))
        {
            _logger.LogWarning("Cannot start sync stream - not connected");
            return;
        }

        _syncStreamCts = new CancellationTokenSource();

        // Create metadata with client ID
        var metadata = new Metadata
        {
            { "client-id", _clientId }
        };

        // Start the bidirectional stream
        _syncStreamCall = _client.SyncStream(metadata, cancellationToken: _syncStreamCts.Token);

        // Start task to receive commands
        _syncStreamTask = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("[SyncStream] Stream started, listening for commands from server");

                await foreach (var command in _syncStreamCall.ResponseStream.ReadAllAsync(_syncStreamCts.Token))
                {
                    _logger.LogInformation("[SyncStream] === RECEIVED COMMAND === Type={CommandType}, ContentId={ContentId}, Seq={SequenceNumber}, Timestamp={Timestamp}",
                        command.Type, command.ContentId, command.SequenceNumber, command.TimestampUtc);

                    // Convert server-clock timestamps into this machine's clock terms so
                    // scheduled waits and the shared animation epoch are unaffected by
                    // wall-clock skew between machines.
                    var clockOffset = _clockOffset.OffsetMs;
                    if (clockOffset != 0)
                    {
                        if (command.TimestampUtc != 0)
                            command.TimestampUtc -= clockOffset;
                        if (command.Params != null && command.Params.SharedStartTimestampMs != 0)
                            command.Params.SharedStartTimestampMs -= clockOffset;
                    }

                    // Log all command parameters if present
                    if (command.Params != null)
                    {
                        _logger.LogInformation("[SyncStream] Command params: RendererType={RendererType}, BackgroundColor={BgColor}, FitMode={FitMode}, Speed={Speed}, Loop={Loop}",
                            command.Params.RendererType ?? "(null)",
                            command.Params.BackgroundColor ?? "(null)",
                            command.Params.FitMode,
                            command.Params.AnimationSpeedCmPerSec,
                            command.Params.Loop);
                    }
                    else
                    {
                        _logger.LogInformation("[SyncStream] Command has no params");
                    }

                    // Handle FETCH_LOGS command directly (don't pass to playback service)
                    if (command.Type == CommandType.FetchLogs)
                    {
                        _logger.LogInformation("Server requested logs - sending log file");
                        var logDir = ConfigurationManager.LoadLoggingConfiguration().LogDirectory;
                        long from = command.Params?.LogFromUtcMs ?? 0, to = command.Params?.LogToUtcMs ?? 0;
                        _ = Task.Run(() => SendLogsToServerAsync(logDir, from, to));
                        continue;
                    }

                    // Automated test mode: handled here, never passed to the playback service (no scheduling wait).
                    if (command.Type == CommandType.TestMode)
                    {
                        ApplyTestMode(command.Params ?? new SyncParameters());
                        continue;
                    }
                    if (command.Type == CommandType.TestProbe)
                    {
                        var probeCommand = command;
                        _ = Task.Run(() => HandleTestProbeAsync(probeCommand));
                        continue;
                    }

                    // Raise event for command processing
                    var hasSubscribers = SyncCommandReceived != null;
                    _logger.LogInformation("[SyncStream] Raising SyncCommandReceived event (hasSubscribers={HasSubs})", hasSubscribers);
                    SyncCommandReceived?.Invoke(this, new SyncCommandReceivedEventArgs(command));

                    // Send acknowledgment back to server
                    _logger.LogInformation("[SyncStream] Sending acknowledgment for sequence {Seq}", command.SequenceNumber);
                    await SendSyncResponseAsync(command.SequenceNumber, WallpaperStateEnum.WallpaperBuffering);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Sync stream cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in sync stream");
                TriggerReconnect("sync stream lost");
            }
        }, _syncStreamCts.Token);

        _logger.LogInformation("Sync stream initialized");
    }

    /// <summary>
    /// Stop sync stream
    /// </summary>
    private void StopSyncStream()
    {
        if (_syncStreamCts != null)
        {
            _syncStreamCts.Cancel();
            _syncStreamTask?.Wait(TimeSpan.FromSeconds(2));
            _syncStreamCts.Dispose();
            _syncStreamCts = null;
            _syncStreamTask = null;
        }

        _syncStreamCall?.Dispose();
        _syncStreamCall = null;

        _logger.LogInformation("Sync stream stopped");
    }

    /// <summary>
    /// Send a sync response to the server
    /// </summary>
    private async Task SendSyncResponseAsync(int sequenceNumber, WallpaperStateEnum state)
    {
        if (_syncStreamCall == null || string.IsNullOrEmpty(_clientId))
        {
            return;
        }

        try
        {
            var response = new SyncResponse
            {
                ClientId = _clientId,
                SequenceNumber = sequenceNumber,
                Acknowledged = true,
                State = state
            };

            await _syncStreamCall.RequestStream.WriteAsync(response);
            _logger.LogDebug("Sent sync response for sequence {SequenceNumber}", sequenceNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending sync response");
        }
    }

    /// <summary>
    /// Get screen configuration for this machine
    /// </summary>
    private ScreenConfiguration GetScreenConfiguration()
    {
        var config = new ScreenConfiguration();

        // Get all screens
        var screens = System.Windows.Forms.Screen.AllScreens;
        config.MonitorCount = screens.Length;

        // Sort screens by X position (left to right)
        var sortedScreens = screens.OrderBy(s => s.Bounds.X).ToArray();

        // Calculate total width and height
        if (sortedScreens.Length > 0)
        {
            // Find the leftmost and rightmost X coordinates
            var minX = sortedScreens.Min(s => s.Bounds.X);
            var maxX = sortedScreens.Max(s => s.Bounds.Right);
            config.TotalWidth = maxX - minX;

            // Find the topmost and bottommost Y coordinates
            var minY = sortedScreens.Min(s => s.Bounds.Y);
            var maxY = sortedScreens.Max(s => s.Bounds.Bottom);
            config.TotalHeight = maxY - minY;
        }

        // Physical DPI per display device — lets the server use real bezel/gap
        // math for this client instead of assuming its own monitor model.
        var pixelsPerCmByDevice = MonitorDpiHelper.GetPixelsPerCmByDevice();

        // Add monitor information in left-to-right order
        for (int i = 0; i < sortedScreens.Length; i++)
        {
            var screen = sortedScreens[i];
            var monitorInfo = new MonitorInfo
            {
                Index = i,
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height,
                X = screen.Bounds.X,
                Y = screen.Bounds.Y,
                IsPrimary = screen.Primary,
                DeviceName = screen.DeviceName,
                RefreshRate = QueryDisplayRefreshRate(screen.DeviceName),
                PixelsPerCm = pixelsPerCmByDevice.TryGetValue(screen.DeviceName, out var ppcm) ? ppcm : 0f
            };

            config.Monitors.Add(monitorInfo);
        }

        _logger.LogInformation("Detected {MonitorCount} monitor(s), total resolution: {Width}x{Height}",
            config.MonitorCount, config.TotalWidth, config.TotalHeight);

        return config;
    }

    // EnumDisplaySettings P/Invoke for querying refresh rate (Hz) per monitor.
    // Inlined here because WaBiBaBuSy.Core can't reference WaBiBaBuSy.WallpaperEngine.
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int   dmFields;
        public int   dmPositionX;
        public int   dmPositionY;
        public int   dmDisplayOrientation;
        public int   dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int   dmBitsPerPel;
        public int   dmPelsWidth;
        public int   dmPelsHeight;
        public int   dmDisplayFlags;
        public int   dmDisplayFrequency;
        public int   dmICMMethod;
        public int   dmICMIntent;
        public int   dmMediaType;
        public int   dmDitherType;
        public int   dmReserved1;
        public int   dmReserved2;
        public int   dmPanningWidth;
        public int   dmPanningHeight;
    }

    private static int QueryDisplayRefreshRate(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return 0;
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : 0;
    }

    #region Thumbnail Sending

    /// <summary>
    /// Start periodic thumbnail sending: at most every 5 s, only while the server shows
    /// thumbnails, and only when the image changed. The 1 s tick just checks those
    /// conditions, so a resumed server gets a fresh thumbnail within a second.
    /// </summary>
    private void StartThumbnailSending()
    {
        _thumbnailCts = new CancellationTokenSource();
        _thumbnailTask = Task.Run(async () =>
        {
            while (!_thumbnailCts.Token.IsCancellationRequested && IsConnected)
            {
                try
                {
                    await SendThumbnailIfDueAsync();
                    await Task.Delay(1000, _thumbnailCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error sending thumbnail (non-critical)");
                }
            }
        }, _thumbnailCts.Token);

        _logger.LogInformation("Thumbnail sending started");
    }

    /// <summary>
    /// Stop thumbnail sending
    /// </summary>
    private void StopThumbnailSending()
    {
        _thumbnailCts?.Cancel();
        _thumbnailTask?.Wait(TimeSpan.FromSeconds(2));
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
        _thumbnailTask = null;
    }

    /// <summary>
    /// Capture and send a thumbnail to the server when one is due, wanted and changed.
    /// </summary>
    private async Task SendThumbnailIfDueAsync()
    {
        if (_client == null || string.IsNullOrEmpty(_clientId) || _thumbnailCaptureService == null)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (_thumbnailsPaused)
        {
            _thumbnailDueTick = 0; // due as soon as the server resumes
            return;
        }
        if (now < _thumbnailDueTick)
        {
            return;
        }
        _thumbnailDueTick = now + ThumbnailIntervalMs;

        var jpegBytes = _thumbnailCaptureService.CaptureCurrentThumbnail();
        if (jpegBytes == null)
        {
            return;
        }

        // A static wallpaper encodes to the same bytes every time; the server still holds it.
        if (_lastSentThumbnail != null && jpegBytes.AsSpan().SequenceEqual(_lastSentThumbnail))
        {
            return;
        }

        var thumbnailData = new ThumbnailData
        {
            ClientId = _clientId,
            ThumbnailJpeg = Google.Protobuf.ByteString.CopyFrom(jpegBytes),
            Width = 160,
            Height = 90,
            WallpaperName = _thumbnailCaptureService.CurrentWallpaperName ?? string.Empty
        };

        await _client.SendThumbnailAsync(thumbnailData);
        _lastSentThumbnail = jpegBytes;
        _logger.LogDebug("Sent thumbnail: {Size} bytes", jpegBytes.Length);
    }

    #endregion

    #region Animation Composition Methods (Phase 3)

    /// <summary>
    /// Send animation ready confirmation to server
    /// </summary>
    public async Task<bool> ReportAnimationReadyAsync(string animationId)
    {
        if (_client == null || string.IsNullOrEmpty(_clientId))
        {
            _logger.LogWarning("Not connected to server, cannot report animation ready");
            return false;
        }

        try
        {
            var ready = new AnimationReady
            {
                AnimationId = animationId,
                ClientId = _clientId,
                ReadyTimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var response = await _client.ReportAnimationReadyAsync(ready);
            _logger.LogInformation("Animation ready reported: {AnimationId}, Success={Success}",
                animationId, response.Success);

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting animation ready: {AnimationId}", animationId);
            return false;
        }
    }

    /// <summary>
    /// Send animation completion report to server
    /// </summary>
    public async Task<bool> ReportAnimationCompleteAsync(
        string animationId,
        long startedTimestampUtc,
        long completedTimestampUtc,
        long actualDurationMs)
    {
        if (_client == null || string.IsNullOrEmpty(_clientId))
        {
            _logger.LogWarning("Not connected to server, cannot report animation completion");
            return false;
        }

        try
        {
            var report = new AnimationCompleteReport
            {
                ClientId = _clientId,
                AnimationId = animationId,
                StartedTimestampUtc = startedTimestampUtc,
                CompletedTimestampUtc = completedTimestampUtc,
                ActualDurationMs = actualDurationMs,
                Successful = true
            };

            var response = await _client.ReportAnimationCompleteAsync(report);
            _logger.LogInformation(
                "Animation completion reported: {AnimationId}, Duration={Duration}ms, Success={Success}",
                animationId, actualDurationMs, response.Success);

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting animation completion: {AnimationId}", animationId);
            return false;
        }
    }

    /// <summary>
    /// Fire animation prepare received event
    /// </summary>
    internal void OnAnimationPrepareReceived(AnimationPrepare prepare)
    {
        AnimationPrepareReceived?.Invoke(this, new AnimationPrepareReceivedEventArgs(prepare));
    }

    /// <summary>
    /// Fire animation start received event
    /// </summary>
    internal void OnAnimationStartReceived(AnimationMetadata metadata)
    {
        AnimationStartReceived?.Invoke(this, new AnimationStartReceivedEventArgs(metadata));
    }

    #endregion

    public void Dispose()
    {
        _userDisconnected = true;
        // Run on the thread pool so blocking here cannot deadlock a UI
        // SynchronizationContext waiting on its own continuations.
        Task.Run(() => DisconnectAsync()).Wait(TimeSpan.FromSeconds(5));
    }
}

public class ConnectionStatusChangedEventArgs : EventArgs
{
    public bool IsConnected { get; }
    public string ServerAddress { get; }
    public int ServerPort { get; }

    public ConnectionStatusChangedEventArgs(bool isConnected, string serverAddress, int serverPort)
    {
        IsConnected = isConnected;
        ServerAddress = serverAddress;
        ServerPort = serverPort;
    }
}

public class SyncCommandReceivedEventArgs : EventArgs
{
    public SyncCommand Command { get; }

    public SyncCommandReceivedEventArgs(SyncCommand command)
    {
        Command = command;
    }
}

public class UpdateAvailableEventArgs : EventArgs
{
    public string ServerVersion { get; set; } = string.Empty;
    public int ServerBuildNumber { get; set; }
    public long PackageSize { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class AnimationPrepareReceivedEventArgs : EventArgs
{
    public AnimationPrepare Prepare { get; }

    public AnimationPrepareReceivedEventArgs(AnimationPrepare prepare)
    {
        Prepare = prepare;
    }
}

public class AnimationStartReceivedEventArgs : EventArgs
{
    public AnimationMetadata Metadata { get; }

    public AnimationStartReceivedEventArgs(AnimationMetadata metadata)
    {
        Metadata = metadata;
    }
}

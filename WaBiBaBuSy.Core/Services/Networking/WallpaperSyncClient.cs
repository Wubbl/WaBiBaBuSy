using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Configuration;
using System.Security.Cryptography;
using Google.Protobuf;
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
    private CancellationTokenSource? _frameStreamCts;
    private Task? _frameStreamTask;
    private AsyncDuplexStreamingCall<FrameAcknowledgment, CrossScreenFrame>? _frameStreamCall;
    private CancellationTokenSource? _thumbnailCts;
    private Task? _thumbnailTask;
    private ThumbnailCaptureService? _thumbnailCaptureService;

    public bool IsConnected { get; private set; }
    public bool IsCrossScreenActive { get; private set; }
    public string? ClientId => _clientId;

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
    public event EventHandler<CrossScreenFrameReceivedEventArgs>? CrossScreenFrameReceived;
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
            ConnectionStatusChanged?.Invoke(this,
                new ConnectionStatusChangedEventArgs(true, serverAddress, serverPort));

            // Start heartbeat
            StartHeartbeat();

            // Start sync stream to receive commands
            StartSyncStream();

            // Start thumbnail sending
            StartThumbnailSending();

            _logger.LogInformation("Successfully connected to server. Client ID: {ClientId}", _clientId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to server");
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
        if (!IsConnected)
        {
            return;
        }

        _logger.LogInformation("Disconnecting from server");

        StopHeartbeat();
        StopSyncStream();
        StopCrossScreenFrameStream();
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
        _clientId = null;

        _logger.LogInformation("Disconnected from server");
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
                    _logger.LogError(ex, "Error sending heartbeat");
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

        var request = new HeartbeatRequest
        {
            ClientId = _clientId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Status = ClientStatusEnum.ClientConnected
        };

        var response = await _client.HeartbeatAsync(request);
        _logger.LogDebug("Heartbeat acknowledged: {Acknowledged}", response.Acknowledged);
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
    /// Upload local log file to the server (triggered by FETCH_LOGS command)
    /// </summary>
    public async Task SendLogsToServerAsync(string logDirectory)
    {
        if (_client == null || string.IsNullOrEmpty(_clientId))
        {
            _logger.LogWarning("Cannot send logs - not connected");
            return;
        }

        try
        {
            // Find today's log file
            var logDate = DateTime.Today.ToString("yyyy-MM-dd");
            var logPath = Path.Combine(logDirectory, $"wabibabusy-{logDate}.log");

            string logContent;
            if (File.Exists(logPath))
            {
                // Read last 500 lines (avoid sending huge files)
                var lines = await File.ReadAllLinesAsync(logPath);
                var lastLines = lines.Length > 500 ? lines[^500..] : lines;
                logContent = string.Join(Environment.NewLine, lastLines);
            }
            else
            {
                logContent = $"[No log file found at {logPath}]";
            }

            var logData = new ClientLogData
            {
                ClientId = _clientId,
                LogContent = logContent,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LogDate = logDate
            };

            var response = await _client.SendClientLogsAsync(logData);
            _logger.LogInformation("Logs sent to server: {Success}", response.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending logs to server");
        }
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

                    // Handle cross-screen start/stop commands
                    if (command.Type == CommandType.CrossscreenStart)
                    {
                        _logger.LogInformation("Cross-screen mode started - initiating frame stream");
                        StartCrossScreenFrameStream();
                    }
                    else if (command.Type == CommandType.CrossscreenStop)
                    {
                        _logger.LogInformation("Cross-screen mode stopped - terminating frame stream");
                        StopCrossScreenFrameStream();
                    }

                    // Handle FETCH_LOGS command directly (don't pass to playback service)
                    if (command.Type == CommandType.FetchLogs)
                    {
                        _logger.LogInformation("Server requested logs - sending log file");
                        var logDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "WaBiBaBuSy", "Logs");
                        _ = Task.Run(() => SendLogsToServerAsync(logDir));
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
                DeviceName = screen.DeviceName
            };

            config.Monitors.Add(monitorInfo);
        }

        _logger.LogInformation("Detected {MonitorCount} monitor(s), total resolution: {Width}x{Height}",
            config.MonitorCount, config.TotalWidth, config.TotalHeight);

        return config;
    }

    /// <summary>
    /// Start cross-screen frame stream to receive frames from server
    /// </summary>
    private void StartCrossScreenFrameStream()
    {
        if (_client == null || string.IsNullOrEmpty(_clientId))
        {
            _logger.LogWarning("Cannot start frame stream - not connected");
            return;
        }

        if (IsCrossScreenActive)
        {
            _logger.LogWarning("Cross-screen frame stream already active");
            return;
        }

        _frameStreamCts = new CancellationTokenSource();

        // Create metadata with client ID
        var metadata = new Metadata
        {
            { "client-id", _clientId }
        };

        // Start the bidirectional stream
        _frameStreamCall = _client.StreamCrossScreenFrames(metadata, cancellationToken: _frameStreamCts.Token);

        IsCrossScreenActive = true;

        // Start task to receive frames
        _frameStreamTask = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Cross-screen frame stream started, listening for frames");

                await foreach (var frame in _frameStreamCall.ResponseStream.ReadAllAsync(_frameStreamCts.Token))
                {
                    _logger.LogDebug("Received frame {FrameNumber} from server: {Width}x{Height}, {Size} KB",
                        frame.FrameNumber, frame.Width, frame.Height, frame.FrameData.Length / 1024);

                    // Raise event for frame processing
                    CrossScreenFrameReceived?.Invoke(this, new CrossScreenFrameReceivedEventArgs(frame));

                    // Send acknowledgment back to server
                    await SendFrameAcknowledgmentAsync(frame.FrameNumber, true);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Cross-screen frame stream cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cross-screen frame stream");
                IsCrossScreenActive = false;
            }
        }, _frameStreamCts.Token);

        _logger.LogInformation("Cross-screen frame stream initialized");
    }

    /// <summary>
    /// Stop cross-screen frame stream
    /// </summary>
    private void StopCrossScreenFrameStream()
    {
        if (_frameStreamCts != null)
        {
            _frameStreamCts.Cancel();
            _frameStreamTask?.Wait(TimeSpan.FromSeconds(2));
            _frameStreamCts.Dispose();
            _frameStreamCts = null;
            _frameStreamTask = null;
        }

        _frameStreamCall?.Dispose();
        _frameStreamCall = null;

        IsCrossScreenActive = false;

        _logger.LogInformation("Cross-screen frame stream stopped");
    }

    /// <summary>
    /// Send a frame acknowledgment to the server
    /// </summary>
    private async Task SendFrameAcknowledgmentAsync(int frameNumber, bool success, string? errorMessage = null)
    {
        if (_frameStreamCall == null || string.IsNullOrEmpty(_clientId))
        {
            return;
        }

        try
        {
            var ack = new FrameAcknowledgment
            {
                ClientId = _clientId,
                FrameNumber = frameNumber,
                Success = success,
                ErrorMessage = errorMessage ?? string.Empty,
                ReceiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RenderTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            await _frameStreamCall.RequestStream.WriteAsync(ack);
            _logger.LogTrace("Sent frame acknowledgment for frame {FrameNumber}", frameNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending frame acknowledgment");
        }
    }

    #region Thumbnail Sending

    /// <summary>
    /// Start periodic thumbnail sending (every ~1 second)
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
                    await SendThumbnailAsync();
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
    /// Capture and send a thumbnail to the server
    /// </summary>
    private async Task SendThumbnailAsync()
    {
        if (_client == null || string.IsNullOrEmpty(_clientId) || _thumbnailCaptureService == null)
        {
            return;
        }

        var jpegBytes = _thumbnailCaptureService.CaptureCurrentThumbnail();
        if (jpegBytes == null)
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
        DisconnectAsync().Wait();
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

public class CrossScreenFrameReceivedEventArgs : EventArgs
{
    public CrossScreenFrame Frame { get; }

    public CrossScreenFrameReceivedEventArgs(CrossScreenFrame frame)
    {
        Frame = frame;
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

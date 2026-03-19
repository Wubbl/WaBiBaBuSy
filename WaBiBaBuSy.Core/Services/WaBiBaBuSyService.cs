using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Core.Services.Update;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Animation;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Update;
using UpdateAvailableEventArgs = WaBiBaBuSy.Core.Services.Networking.UpdateAvailableEventArgs;

namespace WaBiBaBuSy.Core.Services;

/// <summary>
/// Main service coordinating server and client functionality for WaBiBaBuSy
/// </summary>
public class WaBiBaBuSyService : IDisposable
{
    private readonly ILogger<WaBiBaBuSyService> _logger;
    private readonly ServerConfiguration _serverConfig;
    private readonly ClientConfiguration _clientConfig;
    private readonly Func<string, int, IWallpaperRenderer?>? _rendererFactory; // Updated to include monitorIndex

    private WallpaperSyncServerHost? _serverHost;
    private MdnsServerService? _mdnsServerService;
    private WallpaperSyncClient? _client;
    private MdnsClientDiscoveryService? _mdnsClientDiscovery;
    private WallpaperSyncCoordinator? _syncCoordinator;
    private WallpaperPlaybackService? _playbackService;
    private AnimationDistributor? _animationDistributor;
    private AnimationOrchestrator? _animationOrchestrator;
    private UpdateManager? _updateManager;
    private UpdateDownloader? _updateDownloader;
    private UpdateVerifier? _updateVerifier;
    private UpdateApplicator? _updateApplicator;

    public bool IsServerMode { get; private set; }
    public bool IsClientMode { get; private set; }
    public bool IsServerRunning => _serverHost?.IsRunning ?? false;
    public bool IsClientConnected => _client?.IsConnected ?? false;

    /// <summary>
    /// Get the sync coordinator (only available in server mode)
    /// </summary>
    public WallpaperSyncCoordinator? SyncCoordinator => _syncCoordinator;

    /// <summary>
    /// Get the playback service (only available in client mode)
    /// </summary>
    public WallpaperPlaybackService? PlaybackService => _playbackService;

    /// <summary>
    /// Set the D2D apply delegate on the playback service.
    /// Call this after ConnectToServerAsync to enable D2D rendering on this client.
    /// Signature: (filePath, monitorIndex, backgroundColor, fitMode) → Task
    /// </summary>
    public void SetD2DApplyDelegate(Func<string, int, string, int, Task>? d2dApply)
    {
        if (_playbackService != null)
            _playbackService.D2DApplyDelegate = d2dApply;
    }

    /// <summary>
    /// Get the wallpaper sync client (only available in client mode)
    /// </summary>
    public WallpaperSyncClient? Client => _client;

    /// <summary>
    /// Get the animation orchestrator (only available in server mode)
    /// </summary>
    public AnimationOrchestrator? AnimationOrchestrator => _animationOrchestrator;

    // Events for UI updates
    public event EventHandler<ServerStatusChangedEventArgs>? ServerStatusChanged;
    public event EventHandler<ConnectionStatusChangedEventArgs>? ClientConnectionStatusChanged;
    public event EventHandler<ServerDiscoveredEventArgs>? ServerDiscovered;
    public event EventHandler<ClientListChangedEventArgs>? ClientListChanged;
    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;

    public WaBiBaBuSyService(
        ILogger<WaBiBaBuSyService> logger,
        ServerConfiguration serverConfig,
        ClientConfiguration clientConfig,
        Func<string, int, IWallpaperRenderer?>? rendererFactory = null)
    {
        _logger = logger;
        _serverConfig = serverConfig;
        _clientConfig = clientConfig;
        _rendererFactory = rendererFactory;
    }

    /// <summary>
    /// Start in server mode
    /// </summary>
    public async Task StartServerAsync()
    {
        if (IsServerRunning)
        {
            _logger.LogWarning("Server is already running");
            return;
        }

        try
        {
            _logger.LogInformation("Starting WaBiBaBuSy in server mode");

            // Create mDNS service if auto-discovery is enabled
            if (_serverConfig.EnableAutoDiscovery)
            {
                var mdnsLogger = LoggerFactory.Create(builder => builder.AddConsole())
                    .CreateLogger<MdnsServerService>();
                _mdnsServerService = new MdnsServerService(mdnsLogger, _serverConfig);
            }

            // Create and start server host
            var serverLogger = LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<WallpaperSyncServerHost>();
            _serverHost = new WallpaperSyncServerHost(serverLogger, _serverConfig, _mdnsServerService);
            _serverHost.ServerStatusChanged += OnServerStatusChanged;

            await _serverHost.StartAsync();

            // Wire log events
            if (_serverHost.SyncService != null)
            {
                _serverHost.SyncService.ClientLogsReceived += (s, e) =>
                    ClientLogsReceived?.Invoke(this, e);
            }

            // Create sync coordinator
            var coordinatorLogger = LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<WallpaperSyncCoordinator>();
            _syncCoordinator = new WallpaperSyncCoordinator(coordinatorLogger, _serverHost.SyncService);

            // Create animation distribution and orchestration services (Phase 3)
            var distributorLogger = LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<AnimationDistributor>();
            _animationDistributor = new AnimationDistributor(distributorLogger);

            var orchestratorLogger = LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<AnimationOrchestrator>();
            _animationOrchestrator = new AnimationOrchestrator(orchestratorLogger, _animationDistributor);

            // Wire orchestrator event handlers
            // Note: Events are handled by gRPC service, orchestrator is self-contained
            _logger.LogInformation("Animation orchestrator events registered");

            IsServerMode = true;
            _logger.LogInformation("Server mode started successfully with animation orchestration");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start server mode");
            throw;
        }
    }

    /// <summary>
    /// Stop server mode
    /// </summary>
    public async Task StopServerAsync()
    {
        if (!IsServerRunning)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Stopping server mode");

            if (_serverHost != null)
            {
                await _serverHost.StopAsync();
                _serverHost.ServerStatusChanged -= OnServerStatusChanged;
                _serverHost.Dispose();
                _serverHost = null;
            }

            _mdnsServerService?.Dispose();
            _mdnsServerService = null;

            _syncCoordinator = null;

            IsServerMode = false;
            _logger.LogInformation("Server mode stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping server mode");
        }
    }

    /// <summary>
    /// Start client mode and connect to server
    /// </summary>
    public async Task<bool> ConnectToServerAsync(string serverAddress, int serverPort)
    {
        if (IsClientConnected)
        {
            _logger.LogWarning("Client is already connected");
            return true;
        }

        try
        {
            _logger.LogInformation("Connecting to server at {Address}:{Port}", serverAddress, serverPort);

            // Create client
            var clientLogger = LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<WallpaperSyncClient>();
            _client = new WallpaperSyncClient(clientLogger, _clientConfig);
            _client.ConnectionStatusChanged += OnClientConnectionStatusChanged;
            _client.UpdateAvailable += OnUpdateAvailable;

            // Connect
            var connected = await _client.ConnectAsync(serverAddress, serverPort);

            if (connected)
            {
                // Create and initialize wallpaper playback service
                var playbackLogger = LoggerFactory.Create(builder => builder.AddConsole())
                    .CreateLogger<WallpaperPlaybackService>();
                _playbackService = new WallpaperPlaybackService(playbackLogger, _client, _rendererFactory, _clientConfig.CacheDirectory);

                // Initialize update services
                var verifierLogger = LoggerFactory.Create(builder => builder.AddConsole())
                    .CreateLogger<UpdateVerifier>();
                _updateVerifier = new UpdateVerifier(verifierLogger);

                var downloaderLogger = LoggerFactory.Create(builder => builder.AddConsole())
                    .CreateLogger<UpdateDownloader>();
                _updateDownloader = new UpdateDownloader(downloaderLogger, _updateVerifier);

                var managerLogger = LoggerFactory.Create(builder => builder.AddConsole())
                    .CreateLogger<UpdateManager>();
                _updateManager = new UpdateManager(
                    managerLogger, _updateDownloader, _updateVerifier,
                    _clientConfig.UpdateSettings.DownloadDirectory,
                    _clientConfig.UpdateSettings.BackupDirectory);

                var applicatorLogger = LoggerFactory.Create(builder => builder.AddConsole())
                    .CreateLogger<UpdateApplicator>();
                _updateApplicator = new UpdateApplicator(applicatorLogger);

                // Forward update status events
                _updateManager.StatusChanged += (s, e) => UpdateStatusChanged?.Invoke(this, e);

                IsClientMode = true;
                _logger.LogInformation("Client mode started successfully");
            }

            return connected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to server");
            return false;
        }
    }

    /// <summary>
    /// Disconnect from server
    /// </summary>
    public async Task DisconnectFromServerAsync()
    {
        if (!IsClientConnected)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Disconnecting from server");

            // Dispose playback service first
            _playbackService?.Dispose();
            _playbackService = null;

            if (_client != null)
            {
                await _client.DisconnectAsync();
                _client.ConnectionStatusChanged -= OnClientConnectionStatusChanged;
                _client.Dispose();
                _client = null;
            }

            IsClientMode = false;
            _logger.LogInformation("Client mode stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting from server");
        }
    }

    /// <summary>
    /// Start discovering servers via mDNS
    /// </summary>
    public void StartServerDiscovery()
    {
        if (_mdnsClientDiscovery != null)
        {
            _logger.LogWarning("Server discovery is already running");
            return;
        }

        try
        {
            _logger.LogInformation("Starting server discovery");

            var logger = LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<MdnsClientDiscoveryService>();
            _mdnsClientDiscovery = new MdnsClientDiscoveryService(logger);
            _mdnsClientDiscovery.ServerDiscovered += OnServerDiscovered;

            _mdnsClientDiscovery.StartDiscovery(_serverConfig.ServiceType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start server discovery");
        }
    }

    /// <summary>
    /// Stop discovering servers
    /// </summary>
    public void StopServerDiscovery()
    {
        if (_mdnsClientDiscovery != null)
        {
            _mdnsClientDiscovery.ServerDiscovered -= OnServerDiscovered;
            _mdnsClientDiscovery.StopDiscovery();
            _mdnsClientDiscovery.Dispose();
            _mdnsClientDiscovery = null;
        }
    }

    /// <summary>
    /// Get list of discovered servers
    /// </summary>
    public IReadOnlyList<DiscoveredServer> GetDiscoveredServers()
    {
        return _mdnsClientDiscovery?.GetDiscoveredServers() ?? Array.Empty<DiscoveredServer>();
    }

    /// <summary>
    /// Get topology from server (when in client mode)
    /// </summary>
    public async Task<TopologyResponse?> GetTopologyAsync()
    {
        if (_client == null || !IsClientConnected)
        {
            _logger.LogWarning("Cannot get topology - client not connected");
            return null;
        }

        return await _client.GetTopologyAsync();
    }

    /// <summary>
    /// Get the latest thumbnail for a client (when in server mode)
    /// </summary>
    public byte[]? GetClientThumbnail(string clientId)
    {
        var thumbnail = _serverHost?.SyncService?.GetClientThumbnail(clientId);
        return thumbnail?.ThumbnailJpeg?.ToByteArray();
    }

    /// <summary>
    /// Get connected clients (when in server mode)
    /// </summary>
    public IEnumerable<ConnectedClient> GetConnectedClients()
    {
        if (_serverHost?.SyncService == null)
        {
            return Enumerable.Empty<ConnectedClient>();
        }

        return _serverHost.SyncService.GetConnectedClients();
    }

    /// <summary>
    /// Update client physical distance (when in client mode)
    /// </summary>
    public async Task<bool> UpdateClientDistanceAsync(string clientId, int distanceCm)
    {
        if (_client == null || !IsClientConnected)
        {
            _logger.LogWarning("Cannot update client distance - not connected to server");
            return false;
        }

        return await _client.UpdateClientDistanceAsync(clientId, distanceCm);
    }

    /// <summary>
    /// Transfer content file to server (when in client mode)
    /// </summary>
    public async Task<bool> TransferContentToServerAsync(string filePath, string contentId)
    {
        if (_client == null || !IsClientConnected)
        {
            _logger.LogWarning("Cannot transfer content - not connected to server");
            return false;
        }

        return await _client.TransferContentAsync(filePath, contentId);
    }

    /// <summary>
    /// Register content for wallpaper playback (when in client mode)
    /// </summary>
    public void RegisterWallpaperContent(string contentId, string localFilePath)
    {
        if (_playbackService == null)
        {
            _logger.LogWarning("Cannot register wallpaper content - playback service not initialized");
            return;
        }

        _playbackService.RegisterContent(contentId, localFilePath);
    }

    #region Update System (Client Mode)

    /// <summary>
    /// Event raised when update status changes
    /// </summary>
    public event EventHandler<UpdateStatusInfo>? UpdateStatusChanged;

    /// <summary>
    /// Check for available updates from the server (client mode only)
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_client?.GrpcClient == null || _updateManager == null)
        {
            _logger.LogWarning("Cannot check for updates - client or update manager not initialized");
            return null;
        }

        return await _updateManager.CheckForUpdatesAsync(
            _client.GrpcClient, _client.ClientId ?? "unknown", cancellationToken);
    }

    /// <summary>
    /// Download and prepare an update (client mode only)
    /// </summary>
    public async Task<string?> DownloadUpdateAsync(UpdateInfo updateInfo, CancellationToken cancellationToken = default)
    {
        if (_client?.GrpcClient == null || _updateManager == null)
        {
            _logger.LogWarning("Cannot download update - client or update manager not initialized");
            return null;
        }

        return await _updateManager.DownloadAndPrepareUpdateAsync(
            _client.GrpcClient, updateInfo, cancellationToken);
    }

    /// <summary>
    /// Apply a downloaded update (launches external updater and exits app)
    /// </summary>
    public bool ApplyUpdate(string updateDirectory)
    {
        if (_updateApplicator == null)
        {
            _logger.LogWarning("Cannot apply update - applicator not initialized");
            return false;
        }

        var installDir = AppDomain.CurrentDomain.BaseDirectory;
        var backupDir = _clientConfig.UpdateSettings.BackupDirectory;

        _logger.LogInformation("Applying update from {UpdateDir} to {InstallDir}", updateDirectory, installDir);

        // Validate package before applying
        if (!_updateApplicator.ValidateUpdatePackage(updateDirectory))
        {
            _logger.LogError("Update package validation failed");
            return false;
        }

        // Create backup first
        _ = _updateManager?.CreateBackupAsync(installDir);

        // Launch updater (this will exit the application)
        return _updateApplicator.LaunchUpdaterAndExit(updateDirectory, installDir, backupDir);
    }

    #endregion

    /// <summary>
    /// Request logs from a remote client (server mode only)
    /// </summary>
    public async Task RequestClientLogsAsync(string clientId)
    {
        if (_serverHost?.SyncService == null)
        {
            _logger.LogWarning("Cannot request logs - server not running");
            return;
        }

        await _serverHost.SyncService.RequestClientLogsAsync(clientId);
    }

    /// <summary>
    /// Get stored logs for a client (server mode only)
    /// </summary>
    public string? GetClientLogs(string clientId)
    {
        return _serverHost?.SyncService?.GetClientLogs(clientId);
    }

    /// <summary>
    /// Event raised when client logs are received on the server
    /// </summary>
    public event EventHandler<WaBiBaBuSy.Grpc.Services.ClientLogsReceivedEventArgs>? ClientLogsReceived;

    private void OnServerStatusChanged(object? sender, ServerStatusChangedEventArgs e)
    {
        ServerStatusChanged?.Invoke(this, e);
    }

    private void OnClientConnectionStatusChanged(object? sender, ConnectionStatusChangedEventArgs e)
    {
        ClientConnectionStatusChanged?.Invoke(this, e);
    }

    private void OnServerDiscovered(object? sender, ServerDiscoveredEventArgs e)
    {
        ServerDiscovered?.Invoke(this, e);
    }

    private void OnUpdateAvailable(object? sender, UpdateAvailableEventArgs e)
    {
        UpdateAvailable?.Invoke(this, e);
    }

    /// <summary>
    /// Start sequential animation across selected clients
    /// Animation flows from one client to the next in the specified order
    /// </summary>
    public async Task<string> StartSequentialAnimationAsync(
        Models.Animation.AnimationMetadata baseMetadata,
        List<string> selectedClientIds,
        bool loop = false)
    {
        if (_animationOrchestrator == null)
        {
            _logger.LogError("Animation orchestrator not available. Ensure server mode is running.");
            throw new InvalidOperationException("Animation orchestrator not available");
        }

        return await _animationOrchestrator.StartSequentialAnimationAsync(baseMetadata, selectedClientIds, loop);
    }

    /// <summary>
    /// Start simultaneous animation across all selected clients
    /// All clients start rendering at the same time
    /// </summary>
    public async Task<string> StartSimultaneousAnimationAsync(
        Models.Animation.AnimationMetadata baseMetadata,
        List<string> selectedClientIds)
    {
        if (_animationOrchestrator == null)
        {
            _logger.LogError("Animation orchestrator not available. Ensure server mode is running.");
            throw new InvalidOperationException("Animation orchestrator not available");
        }

        return await _animationOrchestrator.StartSimultaneousAnimationAsync(baseMetadata, selectedClientIds);
    }

    /// <summary>
    /// Stop an active animation schedule
    /// </summary>
    public void StopAnimation(string scheduleId)
    {
        if (_animationOrchestrator == null)
        {
            _logger.LogWarning("Animation orchestrator not available");
            return;
        }

        try
        {
            _logger.LogInformation("Stopping animation: {ScheduleId}", scheduleId);
            _animationOrchestrator.StopSchedule(scheduleId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping animation: {ScheduleId}", scheduleId);
        }
    }

    /// <summary>
    /// Get animation schedule status
    /// </summary>
    public AnimationSchedule? GetAnimationStatus(string scheduleId)
    {
        return _animationOrchestrator?.GetSchedule(scheduleId);
    }

    public void Dispose()
    {
        StopServerAsync().Wait();
        DisconnectFromServerAsync().Wait();
        StopServerDiscovery();
    }
}

public class ClientListChangedEventArgs : EventArgs
{
    public IEnumerable<ConnectedClient> Clients { get; }
    public ClientListChangedEventArgs(IEnumerable<ConnectedClient> clients) => Clients = clients;
}

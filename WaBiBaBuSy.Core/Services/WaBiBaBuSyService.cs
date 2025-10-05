using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Configuration;

namespace WaBiBaBuSy.Core.Services;

/// <summary>
/// Main service coordinating server and client functionality for WaBiBaBuSy
/// </summary>
public class WaBiBaBuSyService : IDisposable
{
    private readonly ILogger<WaBiBaBuSyService> _logger;
    private readonly ServerConfiguration _serverConfig;
    private readonly ClientConfiguration _clientConfig;

    private WallpaperSyncServerHost? _serverHost;
    private MdnsServerService? _mdnsServerService;
    private WallpaperSyncClient? _client;
    private MdnsClientDiscoveryService? _mdnsClientDiscovery;
    private WallpaperSyncCoordinator? _syncCoordinator;

    public bool IsServerMode { get; private set; }
    public bool IsClientMode { get; private set; }
    public bool IsServerRunning => _serverHost?.IsRunning ?? false;
    public bool IsClientConnected => _client?.IsConnected ?? false;

    /// <summary>
    /// Get the sync coordinator (only available in server mode)
    /// </summary>
    public WallpaperSyncCoordinator? SyncCoordinator => _syncCoordinator;

    // Events for UI updates
    public event EventHandler<ServerStatusChangedEventArgs>? ServerStatusChanged;
    public event EventHandler<ConnectionStatusChangedEventArgs>? ClientConnectionStatusChanged;
    public event EventHandler<ServerDiscoveredEventArgs>? ServerDiscovered;
    public event EventHandler<ClientListChangedEventArgs>? ClientListChanged;

    public WaBiBaBuSyService(
        ILogger<WaBiBaBuSyService> logger,
        ServerConfiguration serverConfig,
        ClientConfiguration clientConfig)
    {
        _logger = logger;
        _serverConfig = serverConfig;
        _clientConfig = clientConfig;
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

            // Create sync coordinator
            var coordinatorLogger = LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<WallpaperSyncCoordinator>();
            _syncCoordinator = new WallpaperSyncCoordinator(coordinatorLogger, _serverHost.SyncService);

            IsServerMode = true;
            _logger.LogInformation("Server mode started successfully");
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

            // Connect
            var connected = await _client.ConnectAsync(serverAddress, serverPort);

            if (connected)
            {
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

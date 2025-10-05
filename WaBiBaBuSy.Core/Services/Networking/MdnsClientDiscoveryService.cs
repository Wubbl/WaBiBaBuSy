using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using System.Net;

namespace WaBiBaBuSy.Core.Services.Networking;

/// <summary>
/// mDNS service for discovering WaBiBaBuSy servers on the local network
/// </summary>
public class MdnsClientDiscoveryService : IDisposable
{
    private readonly ILogger<MdnsClientDiscoveryService> _logger;
    private readonly Makaretu.Dns.ServiceDiscovery _serviceDiscovery;
    private readonly List<DiscoveredServer> _discoveredServers;
    private bool _isDiscovering;

    public event EventHandler<ServerDiscoveredEventArgs>? ServerDiscovered;
    public event EventHandler<ServerLostEventArgs>? ServerLost;

    public MdnsClientDiscoveryService(ILogger<MdnsClientDiscoveryService> logger)
    {
        _logger = logger;
        _serviceDiscovery = new Makaretu.Dns.ServiceDiscovery();
        _discoveredServers = new List<DiscoveredServer>();

        // Setup event handlers
        _serviceDiscovery.ServiceDiscovered += OnServiceDiscovered;
        _serviceDiscovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
        _serviceDiscovery.ServiceInstanceShutdown += OnServiceInstanceShutdown;
    }

    /// <summary>
    /// Start discovering WaBiBaBuSy servers
    /// </summary>
    public void StartDiscovery(string serviceType = "_wabibabusy._tcp")
    {
        if (_isDiscovering)
        {
            _logger.LogWarning("Discovery is already running");
            return;
        }

        try
        {
            _logger.LogInformation("Starting mDNS discovery for service type: {ServiceType}", serviceType);

            // Query for the service
            _serviceDiscovery.QueryServiceInstances(serviceType);

            _isDiscovering = true;
            _logger.LogInformation("mDNS discovery started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start mDNS discovery");
            throw;
        }
    }

    /// <summary>
    /// Stop discovering servers
    /// </summary>
    public void StopDiscovery()
    {
        if (!_isDiscovering)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Stopping mDNS discovery");
            _serviceDiscovery.Mdns.Stop();
            _isDiscovering = false;
            _discoveredServers.Clear();
            _logger.LogInformation("mDNS discovery stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping mDNS discovery");
        }
    }

    /// <summary>
    /// Get list of currently discovered servers
    /// </summary>
    public IReadOnlyList<DiscoveredServer> GetDiscoveredServers()
    {
        return _discoveredServers.AsReadOnly();
    }

    private void OnServiceDiscovered(object? sender, DomainName serviceName)
    {
        _logger.LogDebug("Service discovered: {ServiceName}", serviceName);
    }

    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        _logger.LogInformation("Server instance discovered: {InstanceName}", e.ServiceInstanceName);

        try
        {
            var server = new DiscoveredServer
            {
                InstanceName = e.ServiceInstanceName.ToString(),
                DiscoveredAt = DateTime.UtcNow
            };

            // Extract properties from TXT records
            if (e.Message.AdditionalRecords != null)
            {
                foreach (var record in e.Message.AdditionalRecords)
                {
                    if (record is SRVRecord srvRecord)
                    {
                        server.Port = srvRecord.Port;
                        server.Hostname = srvRecord.Target.ToString();
                    }
                    else if (record is ARecord aRecord)
                    {
                        server.IpAddress = aRecord.Address.ToString();
                    }
                    else if (record is TXTRecord txtRecord)
                    {
                        foreach (var text in txtRecord.Strings)
                        {
                            var parts = text.Split('=', 2);
                            if (parts.Length == 2)
                            {
                                server.Properties[parts[0]] = parts[1];
                            }
                        }
                    }
                }
            }

            // Add to discovered servers if not already present
            lock (_discoveredServers)
            {
                if (!_discoveredServers.Any(s => s.InstanceName == server.InstanceName))
                {
                    _discoveredServers.Add(server);
                    _logger.LogInformation("Server added: {InstanceName} at {IpAddress}:{Port}",
                        server.InstanceName, server.IpAddress, server.Port);

                    ServerDiscovered?.Invoke(this, new ServerDiscoveredEventArgs(server));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing discovered server instance");
        }
    }

    private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        _logger.LogInformation("Server instance shutdown: {InstanceName}", e.ServiceInstanceName);

        lock (_discoveredServers)
        {
            var server = _discoveredServers.FirstOrDefault(s =>
                s.InstanceName == e.ServiceInstanceName.ToString());

            if (server != null)
            {
                _discoveredServers.Remove(server);
                _logger.LogInformation("Server removed: {InstanceName}", server.InstanceName);
                ServerLost?.Invoke(this, new ServerLostEventArgs(server));
            }
        }
    }

    public void Dispose()
    {
        StopDiscovery();
        _serviceDiscovery?.Dispose();
    }
}

/// <summary>
/// Information about a discovered server
/// </summary>
public class DiscoveredServer
{
    public string InstanceName { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public DateTime DiscoveredAt { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new();
}

public class ServerDiscoveredEventArgs : EventArgs
{
    public DiscoveredServer Server { get; }
    public ServerDiscoveredEventArgs(DiscoveredServer server) => Server = server;
}

public class ServerLostEventArgs : EventArgs
{
    public DiscoveredServer Server { get; }
    public ServerLostEventArgs(DiscoveredServer server) => Server = server;
}

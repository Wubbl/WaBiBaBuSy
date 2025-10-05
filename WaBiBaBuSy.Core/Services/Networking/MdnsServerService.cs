using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using WaBiBaBuSy.Models.Configuration;

namespace WaBiBaBuSy.Core.Services.Networking;

/// <summary>
/// mDNS service for advertising the WaBiBaBuSy server on the local network
/// </summary>
public class MdnsServerService : IDisposable
{
    private readonly ILogger<MdnsServerService> _logger;
    private readonly ServerConfiguration _configuration;
    private readonly ServiceDiscovery _serviceDiscovery;
    private ServiceProfile? _serviceProfile;
    private bool _isAdvertising;

    public MdnsServerService(
        ILogger<MdnsServerService> logger,
        ServerConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _serviceDiscovery = new ServiceDiscovery();
    }

    /// <summary>
    /// Start advertising the server via mDNS
    /// </summary>
    public void StartAdvertising()
    {
        if (_isAdvertising)
        {
            _logger.LogWarning("mDNS advertising is already running");
            return;
        }

        try
        {
            _logger.LogInformation("Starting mDNS advertising for service: {ServiceName} on port {Port}",
                _configuration.ServiceName, _configuration.Port);

            // Get local IP addresses
            var localIPs = GetLocalIPAddresses();

            // Create service profile with local IPs
            _serviceProfile = new ServiceProfile(
                instanceName: _configuration.ServiceName,
                serviceName: _configuration.ServiceType,
                port: (ushort)_configuration.Port,
                addresses: localIPs);

            // Add TXT records with additional info
            _serviceProfile.AddProperty("version", "2.0");
            _serviceProfile.AddProperty("maxclients", _configuration.MaxClients.ToString());

            // Start advertising
            _serviceDiscovery.Advertise(_serviceProfile);

            _isAdvertising = true;
            _logger.LogInformation("mDNS advertising started successfully for IPs: {IPs}",
                string.Join(", ", localIPs.Select(ip => ip.ToString())));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start mDNS advertising");
            throw;
        }
    }

    /// <summary>
    /// Stop advertising the server
    /// </summary>
    public void StopAdvertising()
    {
        if (!_isAdvertising)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Stopping mDNS advertising");

            if (_serviceProfile != null)
            {
                _serviceDiscovery.Unadvertise(_serviceProfile);
            }

            _isAdvertising = false;
            _logger.LogInformation("mDNS advertising stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping mDNS advertising");
        }
    }

    /// <summary>
    /// Get local IPv4 addresses for this machine
    /// </summary>
    private IEnumerable<IPAddress> GetLocalIPAddresses()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        return host.AddressList
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();
    }

    public void Dispose()
    {
        StopAdvertising();
        _serviceDiscovery?.Dispose();
    }
}

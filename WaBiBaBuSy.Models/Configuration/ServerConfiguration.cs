namespace WaBiBaBuSy.Models.Configuration;

/// <summary>
/// Configuration for the WaBiBaBuSy server
/// </summary>
public class ServerConfiguration
{
    /// <summary>
    /// Port for gRPC server to listen on
    /// </summary>
    public int Port { get; set; } = 50051;

    /// <summary>
    /// Maximum number of clients that can connect
    /// </summary>
    public int MaxClients { get; set; } = 10;

    /// <summary>
    /// Directory containing wallpaper content files
    /// </summary>
    public string ContentDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "WaBiBaBuSy", "Content");

    /// <summary>
    /// Enable mDNS auto-discovery for clients
    /// </summary>
    public bool EnableAutoDiscovery { get; set; } = true;

    /// <summary>
    /// Service name for mDNS broadcasting
    /// </summary>
    public string ServiceName { get; set; } = "WaBiBaBuSy Server";

    /// <summary>
    /// Service type for mDNS (_wabibabusy._tcp.local)
    /// </summary>
    public string ServiceType { get; set; } = "_wabibabusy._tcp";
}

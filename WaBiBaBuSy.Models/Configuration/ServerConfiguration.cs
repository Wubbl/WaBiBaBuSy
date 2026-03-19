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

    /// <summary>
    /// Update management configuration
    /// </summary>
    public UpdateManagementConfiguration UpdateManagement { get; set; } = new();
}

/// <summary>
/// Configuration for update management on the server
/// </summary>
public class UpdateManagementConfiguration
{
    /// <summary>
    /// Enable automatic update distribution
    /// </summary>
    public bool EnableUpdates { get; set; } = true;

    /// <summary>
    /// Directory containing update packages
    /// </summary>
    public string UpdatesDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "WaBiBaBuSy", "Updates");

    /// <summary>
    /// Minimum compatible client version that can connect
    /// </summary>
    public string MinimumCompatibleVersion { get; set; } = "2.0.0";

    /// <summary>
    /// Enforce mandatory updates (disconnect old clients)
    /// </summary>
    public bool EnforceMandatoryUpdates { get; set; } = false;

    /// <summary>
    /// Allow clients to defer updates
    /// </summary>
    public bool AllowDeferredUpdates { get; set; } = true;

    /// <summary>
    /// Maximum days an update can be deferred (for mandatory updates)
    /// </summary>
    public int MaxDeferralDays { get; set; } = 7;
}

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
    /// Total cap for server → client transfers (content downloads and update packages),
    /// shared by all concurrent downloads, in MB/s. 0 = unlimited. Applied when the server starts.
    /// </summary>
    public int UploadLimitMBps { get; set; } = 20;

    /// <summary>
    /// Developer: enables automated test runs (Developer tools → Run test suite, --test-run and the
    /// local control API on <see cref="TestControlPort"/>, bound to 127.0.0.1 only).
    /// </summary>
    public bool EnableTestMode { get; set; } = false;

    /// <summary>Port of the local test control API (HTTP/1.1, 127.0.0.1 only).</summary>
    public int TestControlPort { get; set; } = 50052;

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

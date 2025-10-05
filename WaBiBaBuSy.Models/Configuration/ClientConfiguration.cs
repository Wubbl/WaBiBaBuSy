namespace WaBiBaBuSy.Models.Configuration;

/// <summary>
/// Configuration for the WaBiBaBuSy client
/// </summary>
public class ClientConfiguration
{
    /// <summary>
    /// Server address to connect to (IP or hostname)
    /// </summary>
    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>
    /// Server port to connect to
    /// </summary>
    public int ServerPort { get; set; } = 50051;

    /// <summary>
    /// Automatically connect to server on startup
    /// </summary>
    public bool AutoConnect { get; set; } = false;

    /// <summary>
    /// Try to discover server via mDNS first before using manual address
    /// </summary>
    public bool PreferAutoDiscovery { get; set; } = true;

    /// <summary>
    /// Local cache directory for downloaded wallpapers
    /// </summary>
    public string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WaBiBaBuSy", "Cache");

    /// <summary>
    /// Maximum cache size in MB (default 5GB)
    /// </summary>
    public int MaxCacheSizeMB { get; set; } = 5120;

    /// <summary>
    /// Heartbeat interval in seconds
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 5;
}

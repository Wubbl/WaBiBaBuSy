namespace WaBiBaBuSy.Models.Configuration;

/// <summary>
/// Configuration for the WaBiBaBuSy client
/// </summary>
public class ClientConfiguration
{
    /// <summary>
    /// Persistent identity of this machine towards the server. Assigned by the server on first
    /// registration and reused on every later connect, so the node keeps its place in the topology
    /// across client restarts and session-resume can match it. Empty = not yet assigned.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

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

    /// <summary>
    /// Pause wallpaper rendering when a fullscreen application is detected
    /// </summary>
    public bool PauseOnFullscreen { get; set; } = true;

    /// <summary>
    /// Update settings configuration
    /// </summary>
    public UpdateSettingsConfiguration UpdateSettings { get; set; } = new();
}

/// <summary>
/// Configuration for client-side update behavior
/// </summary>
public class UpdateSettingsConfiguration
{
    /// <summary>
    /// Enable automatic updates from server
    /// </summary>
    public bool EnableAutoUpdates { get; set; } = true;

    /// <summary>
    /// Prompt user before downloading updates
    /// </summary>
    public bool PromptBeforeUpdate { get; set; } = true;

    /// <summary>
    /// Check for updates on application startup
    /// </summary>
    public bool UpdateCheckOnStartup { get; set; } = true;

    /// <summary>
    /// Check for updates periodically (minutes, 0 = disabled)
    /// </summary>
    public int UpdateCheckIntervalMinutes { get; set; } = 0;

    /// <summary>
    /// Directory for downloading updates
    /// </summary>
    public string DownloadDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WaBiBaBuSy", "Updates", "Pending");

    /// <summary>
    /// Directory for backup files before update
    /// </summary>
    public string BackupDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WaBiBaBuSy", "Updates", "Backup");

    /// <summary>
    /// Maximum number of backups to keep
    /// </summary>
    public int MaxBackupsToKeep { get; set; } = 2;

    /// <summary>
    /// Automatically apply updates without user confirmation
    /// </summary>
    public bool AutoApplyUpdates { get; set; } = false;
}

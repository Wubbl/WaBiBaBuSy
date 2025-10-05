namespace WaBiBaBuSy.Models;

/// <summary>
/// Application configuration settings.
/// </summary>
public class AppConfiguration
{
    /// <summary>
    /// Operation mode (Client or Server).
    /// </summary>
    public OperationMode Mode { get; set; } = OperationMode.Client;

    /// <summary>
    /// Server-specific configuration.
    /// </summary>
    public ServerConfig Server { get; set; } = new();

    /// <summary>
    /// Client-specific configuration.
    /// </summary>
    public ClientConfig Client { get; set; } = new();

    /// <summary>
    /// Wallpaper rendering configuration.
    /// </summary>
    public WallpaperRenderConfig Wallpaper { get; set; } = new();

    /// <summary>
    /// Logging configuration.
    /// </summary>
    public LoggingConfig Logging { get; set; } = new();
}

/// <summary>
/// Server configuration.
/// </summary>
public class ServerConfig
{
    /// <summary>
    /// Server listening port.
    /// </summary>
    public int Port { get; set; } = 50051;

    /// <summary>
    /// Maximum number of concurrent clients.
    /// </summary>
    public int MaxClients { get; set; } = 10;

    /// <summary>
    /// Directory where wallpaper content is stored.
    /// </summary>
    public string ContentDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Enable mDNS auto-discovery.
    /// </summary>
    public bool EnableAutoDiscovery { get; set; } = true;
}

/// <summary>
/// Client configuration.
/// </summary>
public class ClientConfig
{
    /// <summary>
    /// Server address to connect to.
    /// </summary>
    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>
    /// Server port to connect to.
    /// </summary>
    public int ServerPort { get; set; } = 50051;

    /// <summary>
    /// Automatically connect to server on startup.
    /// </summary>
    public bool AutoConnect { get; set; } = false;

    /// <summary>
    /// Local cache directory for downloaded content.
    /// </summary>
    public string CacheDirectory { get; set; } = string.Empty;
}

/// <summary>
/// Wallpaper rendering configuration.
/// </summary>
public class WallpaperRenderConfig
{
    /// <summary>
    /// Enable hardware acceleration.
    /// </summary>
    public bool HardwareAcceleration { get; set; } = true;

    /// <summary>
    /// Maximum frames per second.
    /// </summary>
    public int MaxFPS { get; set; } = 60;

    /// <summary>
    /// Pause wallpaper when a fullscreen app is active.
    /// </summary>
    public bool PauseOnFullscreen { get; set; } = true;

    /// <summary>
    /// Pause wallpaper when running on battery.
    /// </summary>
    public bool PauseOnBattery { get; set; } = true;

    /// <summary>
    /// Preload buffer time in milliseconds.
    /// </summary>
    public int PreloadBuffer { get; set; } = 200;
}

/// <summary>
/// Logging configuration.
/// </summary>
public class LoggingConfig
{
    /// <summary>
    /// Logging level (Debug, Information, Warning, Error).
    /// </summary>
    public string Level { get; set; } = "Information";

    /// <summary>
    /// Log file directory.
    /// </summary>
    public string Directory { get; set; } = string.Empty;
}

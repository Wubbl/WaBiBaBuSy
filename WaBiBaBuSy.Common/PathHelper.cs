namespace WaBiBaBuSy.Common;

/// <summary>
/// Helper class for managing application paths.
/// </summary>
public static class PathHelper
{
    private static readonly string AppDataFolder = "WaBiBaBuSy";

    /// <summary>
    /// Gets the application data directory path.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    public static string GetAppDataPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = Path.Combine(appData, AppDataFolder);

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    /// <summary>
    /// Gets the local application data directory path.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    public static string GetLocalAppDataPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localAppData, AppDataFolder);

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    /// <summary>
    /// Gets the configuration file path.
    /// </summary>
    public static string GetConfigPath()
    {
        return Path.Combine(GetAppDataPath(), "config.json");
    }

    /// <summary>
    /// Gets the cache directory path.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    public static string GetCachePath()
    {
        var path = Path.Combine(GetLocalAppDataPath(), "Cache");

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    /// <summary>
    /// Gets the logs directory path.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    public static string GetLogsPath()
    {
        var path = Path.Combine(GetLocalAppDataPath(), "Logs");

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    /// <summary>
    /// Gets the content directory path (for server).
    /// Creates the directory if it doesn't exist.
    /// </summary>
    public static string GetContentPath()
    {
        var path = Path.Combine(GetLocalAppDataPath(), "Content");

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }
}

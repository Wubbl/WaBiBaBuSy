using System.Text.Json;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Configuration;

/// <summary>
/// Manages loading and saving configuration files
/// </summary>
public class ConfigurationManager
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WaBiBaBuSy");

    private static readonly string ServerConfigPath = Path.Combine(ConfigDirectory, "server-config.json");
    private static readonly string ClientConfigPath = Path.Combine(ConfigDirectory, "client-config.json");
    private static readonly string WallpaperGalleryPath = Path.Combine(ConfigDirectory, "wallpaper-gallery.json");
    private static readonly string LoggingConfigPath = Path.Combine(ConfigDirectory, "logging-config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Load server configuration from file, or create default if not exists
    /// </summary>
    public static ServerConfiguration LoadServerConfiguration()
    {
        EnsureConfigDirectoryExists();

        if (File.Exists(ServerConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ServerConfigPath);
                var config = JsonSerializer.Deserialize<ServerConfiguration>(json, JsonOptions);
                return config ?? new ServerConfiguration();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading server config: {ex.Message}. Using defaults.");
                return new ServerConfiguration();
            }
        }

        // Create default config
        var defaultConfig = new ServerConfiguration();
        SaveServerConfiguration(defaultConfig);
        return defaultConfig;
    }

    /// <summary>
    /// Load client configuration from file, or create default if not exists
    /// </summary>
    public static ClientConfiguration LoadClientConfiguration()
    {
        EnsureConfigDirectoryExists();

        if (File.Exists(ClientConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ClientConfigPath);
                var config = JsonSerializer.Deserialize<ClientConfiguration>(json, JsonOptions);
                return config ?? new ClientConfiguration();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading client config: {ex.Message}. Using defaults.");
                return new ClientConfiguration();
            }
        }

        // Create default config
        var defaultConfig = new ClientConfiguration();
        SaveClientConfiguration(defaultConfig);
        return defaultConfig;
    }

    /// <summary>
    /// Save server configuration to file
    /// </summary>
    public static void SaveServerConfiguration(ServerConfiguration config)
    {
        EnsureConfigDirectoryExists();

        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ServerConfigPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving server config: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Save client configuration to file
    /// </summary>
    public static void SaveClientConfiguration(ClientConfiguration config)
    {
        EnsureConfigDirectoryExists();

        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ClientConfigPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving client config: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Persist the server-assigned client identity. Re-reads the file first so a concurrent
    /// Settings save is not clobbered - only <see cref="ClientConfiguration.ClientId"/> changes.
    /// </summary>
    public static void UpdateClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;
        try
        {
            var config = LoadClientConfiguration();
            if (config.ClientId == clientId) return;
            config.ClientId = clientId;
            SaveClientConfiguration(config);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error persisting client id: {ex.Message}");
        }
    }

    /// <summary>
    /// Get the path to the server configuration file
    /// </summary>
    public static string GetServerConfigPath() => ServerConfigPath;

    /// <summary>
    /// Get the path to the client configuration file
    /// </summary>
    public static string GetClientConfigPath() => ClientConfigPath;

    /// <summary>
    /// Get the configuration directory path
    /// </summary>
    public static string GetConfigDirectory() => ConfigDirectory;

    /// <summary>
    /// Load wallpaper gallery from file, or create empty if not exists
    /// </summary>
    public static WallpaperGallery LoadWallpaperGallery()
    {
        EnsureConfigDirectoryExists();

        if (File.Exists(WallpaperGalleryPath))
        {
            try
            {
                var json = File.ReadAllText(WallpaperGalleryPath);
                var gallery = JsonSerializer.Deserialize<WallpaperGallery>(json, JsonOptions);
                return gallery ?? new WallpaperGallery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading wallpaper gallery: {ex.Message}. Using empty gallery.");
                return new WallpaperGallery();
            }
        }

        // Create empty gallery
        var defaultGallery = new WallpaperGallery();
        SaveWallpaperGallery(defaultGallery);
        return defaultGallery;
    }

    /// <summary>
    /// Save wallpaper gallery to file
    /// </summary>
    public static void SaveWallpaperGallery(WallpaperGallery gallery)
    {
        EnsureConfigDirectoryExists();

        try
        {
            var json = JsonSerializer.Serialize(gallery, JsonOptions);
            File.WriteAllText(WallpaperGalleryPath, json);
            Console.WriteLine($"Wallpaper gallery saved to: {WallpaperGalleryPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving wallpaper gallery: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Get the path to the wallpaper gallery file
    /// </summary>
    public static string GetWallpaperGalleryPath() => WallpaperGalleryPath;

    /// <summary>
    /// Add a wallpaper file to the gallery if it's not already present (by file path).
    /// Determines the wallpaper type from the file extension.
    /// Returns true if the item was added, false if it already existed or the type is unsupported.
    /// </summary>
    public static bool AddToGalleryIfMissing(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        string type;
        if (new[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm", ".flv" }.Contains(extension))
            type = "Video";
        else if (extension == ".gif")
            type = "Gif";
        else if (new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(extension))
            type = "Image";
        else
            return false; // Unsupported format

        var gallery = LoadWallpaperGallery();

        // Check for duplicate by file path (case-insensitive on Windows)
        if (gallery.Wallpapers.Any(w =>
            string.Equals(w.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var fileInfo = new FileInfo(filePath);
        var item = new WallpaperGalleryItem
        {
            WallpaperId = Guid.NewGuid().ToString(),
            Name = Path.GetFileNameWithoutExtension(filePath),
            FilePath = filePath,
            Type = type,
            FileSizeBytes = fileInfo.Length,
            AddedDate = DateTime.UtcNow
        };

        gallery.Wallpapers.Add(item);
        SaveWallpaperGallery(gallery);
        return true;
    }

    /// <summary>
    /// Load logging configuration from file, or create default if not exists
    /// </summary>
    public static LoggingConfiguration LoadLoggingConfiguration()
    {
        EnsureConfigDirectoryExists();

        if (File.Exists(LoggingConfigPath))
        {
            try
            {
                var json = File.ReadAllText(LoggingConfigPath);
                var config = JsonSerializer.Deserialize<LoggingConfiguration>(json, JsonOptions);
                return config ?? new LoggingConfiguration();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading logging config: {ex.Message}. Using defaults.");
                return new LoggingConfiguration();
            }
        }

        var defaultConfig = new LoggingConfiguration();
        SaveLoggingConfiguration(defaultConfig);
        return defaultConfig;
    }

    /// <summary>
    /// Save logging configuration to file
    /// </summary>
    public static void SaveLoggingConfiguration(LoggingConfiguration config)
    {
        EnsureConfigDirectoryExists();

        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(LoggingConfigPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving logging config: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Get the path to the logging configuration file
    /// </summary>
    public static string GetLoggingConfigPath() => LoggingConfigPath;

    /// <summary>
    /// Ensure the configuration directory exists
    /// </summary>
    private static void EnsureConfigDirectoryExists()
    {
        if (!Directory.Exists(ConfigDirectory))
        {
            Directory.CreateDirectory(ConfigDirectory);
        }
    }
}

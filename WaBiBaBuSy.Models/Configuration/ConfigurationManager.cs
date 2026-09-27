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
    /// Serializes all <see cref="UpdateJsonFile{T}"/> read-modify-write calls within this process,
    /// so a concurrent Settings save and <see cref="UpdateClientId"/> continuation cannot interleave
    /// their read and write and lose an edit, or read mid-truncation from another thread's write.
    /// </summary>
    private static readonly object ConfigFileLock = new();

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
            if (LoadClientConfiguration().ClientId == clientId) return;
            UpdateClientConfiguration(c => c.ClientId = clientId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error persisting client id: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-read the server config, apply <paramref name="edit"/> and save. Fields the edit does
    /// not touch keep their on-disk values.
    /// </summary>
    public static void UpdateServerConfiguration(Action<ServerConfiguration> edit) =>
        UpdateJsonFile(ServerConfigPath, edit);

    /// <summary>
    /// Re-read the client config, apply <paramref name="edit"/> and save. Keeps fields the
    /// dialog never shows, such as <see cref="ClientConfiguration.ClientId"/>.
    /// </summary>
    public static void UpdateClientConfiguration(Action<ClientConfiguration> edit) =>
        UpdateJsonFile(ClientConfigPath, edit);

    /// <summary>
    /// Re-read the logging config, apply <paramref name="edit"/>, save, and return the saved config.
    /// </summary>
    public static LoggingConfiguration UpdateLoggingConfiguration(Action<LoggingConfiguration> edit) =>
        UpdateJsonFile(LoggingConfigPath, edit);

    /// <summary>
    /// Load <paramref name="path"/> (missing or corrupt JSON → defaults), apply <paramref name="edit"/>,
    /// write it back. The whole read-modify-write is serialized within this process via
    /// <see cref="ConfigFileLock"/>, so concurrent callers (e.g. a Settings save and
    /// <see cref="UpdateClientId"/> running on a thread-pool continuation) cannot interleave and lose
    /// each other's edits or observe a partially-written file.
    /// Only a corrupt/unparsable file (<see cref="JsonException"/>) falls back to <c>new T()</c>;
    /// read I/O errors (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>) propagate
    /// to the caller instead of silently discarding the file's contents. Also throws if the write fails.
    /// </summary>
    internal static T UpdateJsonFile<T>(string path, Action<T> edit) where T : class, new()
    {
        lock (ConfigFileLock)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            T config;
            try
            {
                config = File.Exists(path)
                    ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? new T()
                    : new T();
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Error parsing {Path.GetFileName(path)}: {ex.Message}. Using defaults.");
                config = new T();
            }

            edit(config);
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
            return config;
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

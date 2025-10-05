using System.Text.Json;

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

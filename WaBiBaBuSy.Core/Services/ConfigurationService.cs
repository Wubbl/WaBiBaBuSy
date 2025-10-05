using System.Text.Json;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models;

namespace WaBiBaBuSy.Core.Services;

/// <summary>
/// Service for managing application configuration.
/// </summary>
public class ConfigurationService
{
    private readonly ILogger<ConfigurationService> _logger;
    private readonly string _configPath;
    private AppConfiguration? _configuration;

    public ConfigurationService(ILogger<ConfigurationService> logger, string configPath)
    {
        _logger = logger;
        _configPath = configPath;
    }

    /// <summary>
    /// Gets the current configuration.
    /// </summary>
    public AppConfiguration Configuration => _configuration ?? throw new InvalidOperationException("Configuration not loaded");

    /// <summary>
    /// Loads configuration from file or creates default if not exists.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                _logger.LogInformation("Loading configuration from {ConfigPath}", _configPath);
                var json = await File.ReadAllTextAsync(_configPath);
                _configuration = JsonSerializer.Deserialize<AppConfiguration>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (_configuration == null)
                {
                    _logger.LogWarning("Failed to deserialize configuration, using defaults");
                    _configuration = CreateDefaultConfiguration();
                }
            }
            else
            {
                _logger.LogInformation("Configuration file not found, creating default configuration");
                _configuration = CreateDefaultConfiguration();
                await SaveAsync();
            }

            // Ensure paths use environment variables
            ExpandEnvironmentVariables();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading configuration, using defaults");
            _configuration = CreateDefaultConfiguration();
        }
    }

    /// <summary>
    /// Saves current configuration to file.
    /// </summary>
    public async Task SaveAsync()
    {
        try
        {
            _logger.LogInformation("Saving configuration to {ConfigPath}", _configPath);
            var json = JsonSerializer.Serialize(_configuration,
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving configuration");
            throw;
        }
    }

    /// <summary>
    /// Updates configuration and saves to file.
    /// </summary>
    public async Task UpdateAsync(Action<AppConfiguration> updateAction)
    {
        if (_configuration == null)
            throw new InvalidOperationException("Configuration not loaded");

        updateAction(_configuration);
        await SaveAsync();
    }

    private AppConfiguration CreateDefaultConfiguration()
    {
        var config = new AppConfiguration
        {
            Mode = OperationMode.Client,
            Server = new ServerConfig
            {
                Port = 50051,
                MaxClients = 10,
                ContentDirectory = "%LOCALAPPDATA%\\WaBiBaBuSy\\Content",
                EnableAutoDiscovery = true
            },
            Client = new ClientConfig
            {
                ServerAddress = "",
                ServerPort = 50051,
                AutoConnect = false,
                CacheDirectory = "%LOCALAPPDATA%\\WaBiBaBuSy\\Cache"
            },
            Wallpaper = new WallpaperRenderConfig
            {
                HardwareAcceleration = true,
                MaxFPS = 60,
                PauseOnFullscreen = true,
                PauseOnBattery = true,
                PreloadBuffer = 200
            },
            Logging = new LoggingConfig
            {
                Level = "Information",
                Directory = "%LOCALAPPDATA%\\WaBiBaBuSy\\Logs"
            }
        };

        return config;
    }

    private void ExpandEnvironmentVariables()
    {
        if (_configuration == null) return;

        _configuration.Server.ContentDirectory = Environment.ExpandEnvironmentVariables(
            _configuration.Server.ContentDirectory);

        _configuration.Client.CacheDirectory = Environment.ExpandEnvironmentVariables(
            _configuration.Client.CacheDirectory);

        _configuration.Logging.Directory = Environment.ExpandEnvironmentVariables(
            _configuration.Logging.Directory);
    }
}

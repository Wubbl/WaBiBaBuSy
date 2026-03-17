using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Core.Services.Logging;
using System;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// ViewModel for the settings window
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private int _serverPort;

    [ObservableProperty]
    private int _maxClients;

    [ObservableProperty]
    private string _contentDirectory = string.Empty;

    [ObservableProperty]
    private bool _enableAutoDiscovery;

    [ObservableProperty]
    private string _serviceName = string.Empty;

    [ObservableProperty]
    private string _serverAddress = string.Empty;

    [ObservableProperty]
    private int _clientServerPort;

    [ObservableProperty]
    private bool _autoConnect;

    [ObservableProperty]
    private bool _preferAutoDiscovery;

    [ObservableProperty]
    private string _cacheDirectory = string.Empty;

    [ObservableProperty]
    private int _maxCacheSizeMB;

    [ObservableProperty]
    private int _heartbeatIntervalSeconds;

    // ── Logging Settings ─────────────────────────────────────────────────────

    [ObservableProperty] private bool _logLevelInfo = true;
    [ObservableProperty] private bool _logLevelDebug;
    [ObservableProperty] private bool _logLevelWarning;
    [ObservableProperty] private bool _logLevelError;

    [ObservableProperty] private bool _logUI = true;
    [ObservableProperty] private bool _logD2DPlayer = true;
    [ObservableProperty] private bool _logComposition = true;
    [ObservableProperty] private bool _logRenderers = true;
    [ObservableProperty] private bool _logNetworking = true;
    [ObservableProperty] private bool _logAnimation;
    [ObservableProperty] private bool _logFileTransfer;

    [ObservableProperty] private bool _logToFile;
    [ObservableProperty] private bool _logPerformanceMetrics;
    [ObservableProperty] private bool _logFrameByFrame;
    [ObservableProperty] private string _logDirectory = string.Empty;

    public event EventHandler? SettingsSaved;
    public event EventHandler? SettingsCancelled;

    public SettingsViewModel()
    {
        LoadSettings();
    }

    private void LoadSettings()
    {
        var serverConfig = ConfigurationManager.LoadServerConfiguration();
        var clientConfig = ConfigurationManager.LoadClientConfiguration();
        var loggingConfig = ConfigurationManager.LoadLoggingConfiguration();

        // Server settings
        ServerPort = serverConfig.Port;
        MaxClients = serverConfig.MaxClients;
        ContentDirectory = serverConfig.ContentDirectory;
        EnableAutoDiscovery = serverConfig.EnableAutoDiscovery;
        ServiceName = serverConfig.ServiceName;

        // Client settings
        ServerAddress = clientConfig.ServerAddress;
        ClientServerPort = clientConfig.ServerPort;
        AutoConnect = clientConfig.AutoConnect;
        PreferAutoDiscovery = clientConfig.PreferAutoDiscovery;
        CacheDirectory = clientConfig.CacheDirectory;
        MaxCacheSizeMB = clientConfig.MaxCacheSizeMB;
        HeartbeatIntervalSeconds = clientConfig.HeartbeatIntervalSeconds;

        // Logging settings
        LogLevelInfo    = loggingConfig.Level == "Information";
        LogLevelDebug   = loggingConfig.Level == "Debug";
        LogLevelWarning = loggingConfig.Level == "Warning";
        LogLevelError   = loggingConfig.Level == "Error";
        if (!LogLevelInfo && !LogLevelDebug && !LogLevelWarning && !LogLevelError)
            LogLevelInfo = true; // fallback

        LogUI               = loggingConfig.LogUI;
        LogD2DPlayer        = loggingConfig.LogD2DPlayer;
        LogComposition      = loggingConfig.LogComposition;
        LogRenderers        = loggingConfig.LogRenderers;
        LogNetworking       = loggingConfig.LogNetworking;
        LogAnimation        = loggingConfig.LogAnimation;
        LogFileTransfer     = loggingConfig.LogFileTransfer;

        LogToFile           = loggingConfig.LogToFile;
        LogPerformanceMetrics = loggingConfig.LogPerformanceMetrics;
        LogFrameByFrame     = loggingConfig.LogFrameByFrame;
        LogDirectory        = loggingConfig.LogDirectory;
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            // Create configuration objects
            var serverConfig = new ServerConfiguration
            {
                Port = ServerPort,
                MaxClients = MaxClients,
                ContentDirectory = ContentDirectory,
                EnableAutoDiscovery = EnableAutoDiscovery,
                ServiceName = ServiceName
            };

            var clientConfig = new ClientConfiguration
            {
                ServerAddress = ServerAddress,
                ServerPort = ClientServerPort,
                AutoConnect = AutoConnect,
                PreferAutoDiscovery = PreferAutoDiscovery,
                CacheDirectory = CacheDirectory,
                MaxCacheSizeMB = MaxCacheSizeMB,
                HeartbeatIntervalSeconds = HeartbeatIntervalSeconds
            };

            var loggingConfig = new LoggingConfiguration
            {
                Level           = LogLevelDebug ? "Debug" : LogLevelWarning ? "Warning" : LogLevelError ? "Error" : "Information",
                LogUI           = LogUI,
                LogD2DPlayer    = LogD2DPlayer,
                LogComposition  = LogComposition,
                LogRenderers    = LogRenderers,
                LogNetworking   = LogNetworking,
                LogAnimation    = LogAnimation,
                LogFileTransfer = LogFileTransfer,
                LogToFile       = LogToFile,
                LogPerformanceMetrics = LogPerformanceMetrics,
                LogFrameByFrame = LogFrameByFrame,
                LogDirectory    = LogDirectory
            };

            // Save to file
            ConfigurationManager.SaveServerConfiguration(serverConfig);
            ConfigurationManager.SaveClientConfiguration(clientConfig);
            ConfigurationManager.SaveLoggingConfiguration(loggingConfig);

            // Apply immediately – no restart needed
            AppLogger.ApplyConfig(loggingConfig);

            Console.WriteLine("Settings saved successfully");
            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving settings: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        SettingsCancelled?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void BrowseContentDirectory()
    {
        // TODO: Open folder picker dialog
        Console.WriteLine("Browse content directory clicked");
    }

    [RelayCommand]
    private void BrowseCacheDirectory()
    {
        // TODO: Open folder picker dialog
        Console.WriteLine("Browse cache directory clicked");
    }
}

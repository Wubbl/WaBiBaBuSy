using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Models.Configuration;
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

            // Save to file
            ConfigurationManager.SaveServerConfiguration(serverConfig);
            ConfigurationManager.SaveClientConfiguration(clientConfig);

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

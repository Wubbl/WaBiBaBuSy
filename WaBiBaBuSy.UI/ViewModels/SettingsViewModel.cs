using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Core.Services.Logging;
using System;
using System.IO;
using System.Threading.Tasks;

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

    /// <summary>Total server → client transfer cap in MB/s; 0 = unlimited.</summary>
    [ObservableProperty]
    private int _uploadLimitMBps;

    [ObservableProperty]
    private bool _enableTestMode;

    [ObservableProperty]
    private int _testControlPort;

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

    [ObservableProperty]
    private bool _pauseOnFullscreen;

    [ObservableProperty]
    private bool _allowTestRuns;

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

    // ── Sidebar ──────────────────────────────────────────────────────────────

    /// <summary>Sidebar section: 0 Server, 1 Client, 2 Wallpaper, 3 Logging.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerSection), nameof(IsClientSection),
                              nameof(IsWallpaperSection), nameof(IsLoggingSection))]
    private int _selectedSectionIndex;

    public bool IsServerSection => SelectedSectionIndex == 0;
    public bool IsClientSection => SelectedSectionIndex == 1;
    public bool IsWallpaperSection => SelectedSectionIndex == 2;
    public bool IsLoggingSection => SelectedSectionIndex == 3;

    /// <summary>One-line error shown in the footer when saving failed; null when fine.</summary>
    [ObservableProperty]
    private string? _saveError;

    private IStorageProvider? _storageProvider;

    /// <summary>Called by the window so the Browse buttons can open a folder picker.</summary>
    public void SetStorageProvider(IStorageProvider storageProvider) => _storageProvider = storageProvider;

    public event EventHandler? SettingsSaved;
    public event EventHandler? SettingsCancelled;

    /// <summary>Raised after any Settings window saved (UI thread), e.g. to refresh the test-mode menu.</summary>
    public static event EventHandler? AnySettingsSaved;

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
        UploadLimitMBps = Math.Max(0, serverConfig.UploadLimitMBps);
        EnableTestMode = serverConfig.EnableTestMode;
        TestControlPort = Math.Clamp(serverConfig.TestControlPort, 1024, 65535);   // a hand-edited file may hold anything

        // Client settings
        ServerAddress = clientConfig.ServerAddress;
        ClientServerPort = clientConfig.ServerPort;
        AutoConnect = clientConfig.AutoConnect;
        PreferAutoDiscovery = clientConfig.PreferAutoDiscovery;
        CacheDirectory = clientConfig.CacheDirectory;
        MaxCacheSizeMB = clientConfig.MaxCacheSizeMB;
        HeartbeatIntervalSeconds = clientConfig.HeartbeatIntervalSeconds;
        PauseOnFullscreen = clientConfig.PauseOnFullscreen;
        AllowTestRuns = clientConfig.AllowTestRuns;

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
        SaveError = null;
        try
        {
            ConfigurationManager.UpdateServerConfiguration(s =>
            {
                s.Port = ServerPort;
                s.MaxClients = MaxClients;
                s.ContentDirectory = ContentDirectory;
                s.EnableAutoDiscovery = EnableAutoDiscovery;
                s.ServiceName = ServiceName;
                s.UploadLimitMBps = Math.Max(0, UploadLimitMBps);
                s.EnableTestMode = EnableTestMode;
                s.TestControlPort = Math.Clamp(TestControlPort, 1024, 65535);
            });

            ConfigurationManager.UpdateClientConfiguration(c =>
            {
                c.ServerAddress = ServerAddress;
                c.ServerPort = ClientServerPort;
                c.AutoConnect = AutoConnect;
                c.PreferAutoDiscovery = PreferAutoDiscovery;
                c.CacheDirectory = CacheDirectory;
                c.MaxCacheSizeMB = MaxCacheSizeMB;
                c.HeartbeatIntervalSeconds = HeartbeatIntervalSeconds;
                c.PauseOnFullscreen = PauseOnFullscreen;
                c.AllowTestRuns = AllowTestRuns;
            });

            var loggingConfig = ConfigurationManager.UpdateLoggingConfiguration(l =>
            {
                l.Level           = LogLevelDebug ? "Debug" : LogLevelWarning ? "Warning" : LogLevelError ? "Error" : "Information";
                l.LogUI           = LogUI;
                l.LogD2DPlayer    = LogD2DPlayer;
                l.LogComposition  = LogComposition;
                l.LogRenderers    = LogRenderers;
                l.LogNetworking   = LogNetworking;
                l.LogAnimation    = LogAnimation;
                l.LogFileTransfer = LogFileTransfer;
                l.LogToFile       = LogToFile;
                l.LogPerformanceMetrics = LogPerformanceMetrics;
                l.LogFrameByFrame = LogFrameByFrame;
                l.LogDirectory    = LogDirectory;
            });

            // Apply immediately – no restart needed
            AppLogger.ApplyConfig(loggingConfig);

            Console.WriteLine("Settings saved successfully");
            SettingsSaved?.Invoke(this, EventArgs.Empty);
            AnySettingsSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving settings: {ex.Message}");
            SaveError = $"Could not save settings: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        SettingsCancelled?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task BrowseContentDirectory()
    {
        var path = await PickFolderAsync("Content directory", ContentDirectory);
        if (path != null) ContentDirectory = path;
    }

    [RelayCommand]
    private async Task BrowseCacheDirectory()
    {
        var path = await PickFolderAsync("Cache directory", CacheDirectory);
        if (path != null) CacheDirectory = path;
    }

    private async Task<string?> PickFolderAsync(string title, string current)
    {
        if (_storageProvider == null) return null;
        var start = Directory.Exists(current)
            ? await _storageProvider.TryGetFolderFromPathAsync(current)
            : null;
        var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}

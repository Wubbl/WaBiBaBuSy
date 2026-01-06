using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.UI.Views;
using WaBiBaBuSy.WallpaperEngine.Native;
using WaBiBaBuSy.WallpaperEngine.Renderers;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// ViewModel for the system tray icon
/// </summary>
public partial class TrayViewModel : ObservableObject
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly WaBiBaBuSyService _service;
    private readonly DesktopWindowManager _desktopManager;
    private MainWindow? _mainWindow;

    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private bool _isClientConnected;

    public TrayViewModel(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;

        // Load configuration from file
        var serverConfig = ConfigurationManager.LoadServerConfiguration();
        var clientConfig = ConfigurationManager.LoadClientConfiguration();

        Console.WriteLine($"Loaded server config from: {ConfigurationManager.GetServerConfigPath()}");
        Console.WriteLine($"Loaded client config from: {ConfigurationManager.GetClientConfigPath()}");

        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<WaBiBaBuSyService>();

        // Create desktop window manager (will be used for wallpaper restoration on exit)
        var desktopManagerLogger = loggerFactory.CreateLogger<DesktopWindowManager>();
        _desktopManager = new DesktopWindowManager(desktopManagerLogger);

        // Create renderer factory for wallpaper playback
        var rendererFactory = CreateRendererFactory(loggerFactory, _desktopManager);

        _service = new WaBiBaBuSyService(logger, serverConfig, clientConfig, rendererFactory);

        // Subscribe to service events
        _service.ServerStatusChanged += OnServerStatusChanged;
        _service.ClientConnectionStatusChanged += OnClientConnectionStatusChanged;

        // Subscribe to application exit event to restore desktop
        _desktop.Exit += OnApplicationExit;
    }

    /// <summary>
    /// Creates a renderer factory that instantiates appropriate renderers based on file type and monitor index
    /// </summary>
    private Func<string, int, IWallpaperRenderer?> CreateRendererFactory(ILoggerFactory loggerFactory, DesktopWindowManager desktopManager)
    {
        return (filePath, monitorIndex) =>
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            // Note: monitorIndex will be used when WallpaperConfig is passed to InitializeAsync
            return extension switch
            {
                // GIFs now use LibVLC (VideoWallpaperRenderer) for instant loading
                ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".flv" or ".gif" =>
                    new VideoWallpaperRenderer(
                        loggerFactory.CreateLogger<VideoWallpaperRenderer>(),
                        desktopManager),

                ".jpg" or ".jpeg" or ".png" or ".bmp" =>
                    new ImageWallpaperRendererLibVLC(
                        loggerFactory.CreateLogger<ImageWallpaperRendererLibVLC>(),
                        desktopManager),

                _ => null
            };
        };
    }

    [RelayCommand]
    private void ShowWindow()
    {
        // Always create a new window if the old one doesn't exist or check if it's visible
        if (_mainWindow == null || !_mainWindow.IsVisible)
        {
            _mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(_service)
            };
            _mainWindow.Show();
        }
        else
        {
            // Window exists and is visible, just activate it
            _mainWindow.Activate();
        }
    }

    [RelayCommand]
    private async Task StartServer()
    {
        try
        {
            await _service.StartServerAsync();
        }
        catch (Exception ex)
        {
            // TODO: Show error dialog to user
            Console.WriteLine($"Error starting server: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StopServer()
    {
        try
        {
            await _service.StopServerAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error stopping server: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Connect()
    {
        // TODO: Show client connection dialog
        // For now, try to discover servers
        _service.StartServerDiscovery();

        // Wait a moment for discovery
        Task.Delay(2000).ContinueWith(async _ =>
        {
            var servers = _service.GetDiscoveredServers();
            if (servers.Any())
            {
                var firstServer = servers.First();
                await _service.ConnectToServerAsync(firstServer.IpAddress, firstServer.Port);
            }
            else
            {
                // No servers found, try default
                await _service.ConnectToServerAsync("localhost", 50051);
            }

            _service.StopServerDiscovery();
        });
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        try
        {
            await _service.DisconnectFromServerAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error disconnecting: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Settings()
    {
        var settingsWindow = new SettingsWindow();
        settingsWindow.Show();
    }

    [RelayCommand]
    private async Task Exit()
    {
        // Cleanup wallpaper renderers (kill player processes)
        if (_mainWindow?.DataContext is MainWindowViewModel mainViewModel)
        {
            mainViewModel.Cleanup();
        }

        // Cleanup: Stop server/client if running
        await _service.StopServerAsync();
        await _service.DisconnectFromServerAsync();

        // Close main window if open
        _mainWindow?.Close();

        // Shutdown the application
        _desktop.Shutdown();
    }

    private void OnServerStatusChanged(object? sender, Core.Services.Networking.ServerStatusChangedEventArgs e)
    {
        IsServerRunning = e.IsRunning;
    }

    private void OnClientConnectionStatusChanged(object? sender, Core.Services.Networking.ConnectionStatusChangedEventArgs e)
    {
        IsClientConnected = e.IsConnected;
    }

    /// <summary>
    /// Called when the application is exiting. Restores the Windows desktop wallpaper.
    /// </summary>
    private void OnApplicationExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Console.WriteLine("Application exiting, restoring desktop wallpaper...");

        try
        {
            // Restore the desktop to its original state
            _desktopManager.RestoreDesktop();

            // Dispose the service to clean up resources
            _service.Dispose();

            Console.WriteLine("Desktop wallpaper restored successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error restoring desktop on exit: {ex.Message}");
        }
    }
}

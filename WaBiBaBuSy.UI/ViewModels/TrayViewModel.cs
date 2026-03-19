using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Common.Version;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.UI.Views;
using WaBiBaBuSy.WallpaperEngine.Composition;
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
    private readonly ILoggerFactory _loggerFactory;
    private MainWindow? _mainWindow;
    // D2D services for remote-triggered rendering on this client
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, D2DCompositionService> _clientD2DServices = new();

    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private bool _isClientConnected;

    public string VersionText => $"v{VersionInfo.AppVersion} (build {VersionInfo.BuildNumber})";

    public TrayViewModel(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;

        // Load configuration from file
        var serverConfig = ConfigurationManager.LoadServerConfiguration();
        var clientConfig = ConfigurationManager.LoadClientConfiguration();

        Console.WriteLine($"Loaded server config from: {ConfigurationManager.GetServerConfigPath()}");
        Console.WriteLine($"Loaded client config from: {ConfigurationManager.GetClientConfigPath()}");

        _loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = _loggerFactory.CreateLogger<WaBiBaBuSyService>();

        // Create desktop window manager (will be used for wallpaper restoration on exit)
        var desktopManagerLogger = _loggerFactory.CreateLogger<DesktopWindowManager>();
        _desktopManager = new DesktopWindowManager(desktopManagerLogger);

        // Create renderer factory for wallpaper playback
        var rendererFactory = CreateRendererFactory(_loggerFactory, _desktopManager);

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
            bool connected;
            if (servers.Any())
            {
                var firstServer = servers.First();
                connected = await _service.ConnectToServerAsync(firstServer.IpAddress, firstServer.Port);
            }
            else
            {
                // No servers found, try default
                connected = await _service.ConnectToServerAsync("localhost", 50051);
            }

            if (connected)
            {
                // Wire D2D rendering delegate so remote server can trigger D2D on this client
                _service.SetD2DApplyDelegate(ApplyD2DFromRemoteAsync);
            }

            _service.StopServerDiscovery();
        });
    }

    /// <summary>
    /// D2D apply delegate called when the server sends a D2D LOAD command to this client.
    /// Creates a local D2DCompositionService and renders on this machine's desktop.
    /// </summary>
    private async Task ApplyD2DFromRemoteAsync(string filePath, int monitorIndex, string backgroundColor, int fitMode)
    {
        // Dispose previous service for this monitor
        if (_clientD2DServices.TryRemove(monitorIndex, out var existing))
        {
            existing.Dispose();
            await Task.Delay(300);
        }

        var monitors = WaBiBaBuSy.WallpaperEngine.Native.NativeMonitorInfo.GetAllMonitors();
        if (monitorIndex < 0 || monitorIndex >= monitors.Length)
        {
            Console.WriteLine($"[D2D-Remote] Invalid monitor index {monitorIndex}");
            return;
        }

        var screen = monitors[monitorIndex];

        var screenConfig = new ScreenConfiguration
        {
            ClientId = $"REMOTE_CLIENT_MONITOR_{monitorIndex}",
            Order = monitorIndex,
            Width = screen.Bounds.Width,
            Height = screen.Bounds.Height,
            PhysicalDistanceCm = 0
        };

        var canvasManager = new VirtualCanvasManager(
            _loggerFactory.CreateLogger<VirtualCanvasManager>());
        canvasManager.CalculateLayout(new[] { screenConfig });

        var backgroundConfig = new BackgroundLayerConfig
        {
            Mode = BackgroundMode.SolidColor,
            ColorHex = backgroundColor
        };

        var animationConfig = new AnimationLayerConfig
        {
            AnimationPath = filePath,
            TargetHeight = screen.Bounds.Height,
            Loop = true,
            VerticalAlign = VerticalAlignment.Center,
            CenterInitialPosition = true,
            FitMode = (ContentFitMode)fitMode
        };

        var d2dService = new D2DCompositionService(
            _loggerFactory.CreateLogger<D2DCompositionService>(),
            _loggerFactory,
            _desktopManager);

        var actualBounds = new System.Drawing.Rectangle(
            screen.Bounds.X, screen.Bounds.Y,
            screen.Bounds.Width, screen.Bounds.Height);

        await d2dService.InitializeAsync(canvasManager, backgroundConfig, animationConfig, actualBounds, monitorIndex);
        await Task.Delay(100);
        await d2dService.StartAsync(startTimestampMs: 0, pixelsPerSecond: 0);

        _clientD2DServices[monitorIndex] = d2dService;
        Console.WriteLine($"[D2D-Remote] Applied D2D wallpaper '{Path.GetFileName(filePath)}' on monitor {monitorIndex}");
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

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.UI.Views;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// ViewModel for the system tray icon
/// </summary>
public partial class TrayViewModel : ObservableObject
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly WaBiBaBuSyService _service;
    private MainWindow? _mainWindow;

    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private bool _isClientConnected;

    public TrayViewModel(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;

        // Create service with default configuration
        // TODO: Load configuration from file
        var serverConfig = new ServerConfiguration();
        var clientConfig = new ClientConfiguration();

        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<WaBiBaBuSyService>();

        _service = new WaBiBaBuSyService(logger, serverConfig, clientConfig);

        // Subscribe to service events
        _service.ServerStatusChanged += OnServerStatusChanged;
        _service.ClientConnectionStatusChanged += OnClientConnectionStatusChanged;
    }

    [RelayCommand]
    private void ShowWindow()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(_service)
            };
        }

        _mainWindow.Show();
        _mainWindow.Activate();
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
        // TODO: Show settings window
    }

    [RelayCommand]
    private async Task Exit()
    {
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
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Core.Services;

namespace WaBiBaBuSy.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly WaBiBaBuSyService _service;
    private readonly System.Timers.Timer _refreshTimer;

    [ObservableProperty]
    private ObservableCollection<ClientNodeViewModel> _clients = new();

    [ObservableProperty]
    private ObservableCollection<WallpaperItemViewModel> _wallpapers = new();

    [ObservableProperty]
    private ClientNodeViewModel? _selectedClient;

    [ObservableProperty]
    private WallpaperItemViewModel? _selectedWallpaper;

    [ObservableProperty]
    private bool _isServerMode;

    [ObservableProperty]
    private string _serverStatus = "Stopped";

    public MainWindowViewModel(WaBiBaBuSyService service)
    {
        _service = service;

        // Subscribe to service events
        _service.ServerStatusChanged += OnServerStatusChanged;
        _service.ClientConnectionStatusChanged += OnClientConnectionStatusChanged;

        // Setup refresh timer for topology updates
        _refreshTimer = new System.Timers.Timer(2000); // Refresh every 2 seconds
        _refreshTimer.Elapsed += OnRefreshTimerElapsed;
        _refreshTimer.Start();

        // Add sample wallpapers for design-time preview
        InitializeSampleWallpapers();

        // Initial refresh
        RefreshTopology();
    }

    private void InitializeSampleWallpapers()
    {
        // Sample wallpapers
        Wallpapers.Add(new WallpaperItemViewModel
        {
            WallpaperId = "wp-1",
            Name = "Mountain Sunset",
            FilePath = "C:\\Wallpapers\\sunset.mp4",
            Type = WallpaperType.Video,
            Resolution = "1920x1080",
            FileSizeBytes = 52428800, // 50MB
            IsActive = true
        });

        Wallpapers.Add(new WallpaperItemViewModel
        {
            WallpaperId = "wp-2",
            Name = "Ocean Waves",
            FilePath = "C:\\Wallpapers\\ocean.gif",
            Type = WallpaperType.Gif,
            Resolution = "1920x1080",
            FileSizeBytes = 10485760 // 10MB
        });

        Wallpapers.Add(new WallpaperItemViewModel
        {
            WallpaperId = "wp-3",
            Name = "Forest Path",
            FilePath = "C:\\Wallpapers\\forest.jpg",
            Type = WallpaperType.Image,
            Resolution = "3840x2160",
            FileSizeBytes = 5242880 // 5MB
        });
    }

    [RelayCommand]
    private void SelectClient(ClientNodeViewModel client)
    {
        // Deselect all clients
        foreach (var c in Clients)
            c.IsSelected = false;

        // Select the clicked client
        client.IsSelected = true;
        SelectedClient = client;
    }

    [RelayCommand]
    private void SelectWallpaper(WallpaperItemViewModel wallpaper)
    {
        SelectedWallpaper = wallpaper;
    }

    [RelayCommand]
    private async Task ApplyWallpaperToSelected()
    {
        if (SelectedClient == null || SelectedWallpaper == null)
            return;

        // TODO: Implement single client wallpaper application
        SelectedClient.CurrentWallpaper = SelectedWallpaper.Name;
    }

    [RelayCommand]
    private async Task ApplyWallpaperToAll()
    {
        if (SelectedWallpaper == null)
            return;

        try
        {
            // Use the sync coordinator to broadcast with delays
            // TODO: Get sync coordinator from service
            // For now, just update UI
            foreach (var client in Clients.Where(c => c.IsConnected))
            {
                client.CurrentWallpaper = SelectedWallpaper.Name;
            }

            Console.WriteLine($"Broadcasting wallpaper {SelectedWallpaper.Name} to all clients");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error applying wallpaper: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task UpdateClientDistance(ClientNodeViewModel client)
    {
        if (client == null)
            return;

        try
        {
            // Update via sync coordinator if in server mode
            if (_service.IsServerRunning && _service.SyncCoordinator != null)
            {
                _service.SyncCoordinator.UpdateClientDistance(client.ClientId, client.PhysicalDistanceCm);
                Console.WriteLine($"Updated physical distance for client {client.ClientId} to {client.PhysicalDistanceCm} cm");
            }
            else if (_service.IsClientConnected)
            {
                // If we're a client, we need to send this update to the server via gRPC
                await UpdateClientDistanceOnServerAsync(client.ClientId, client.PhysicalDistanceCm);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating client distance: {ex.Message}");
        }
    }

    /// <summary>
    /// Update client distance on server via gRPC (when in client mode)
    /// </summary>
    private async Task UpdateClientDistanceOnServerAsync(string clientId, int distanceCm)
    {
        var success = await _service.UpdateClientDistanceAsync(clientId, distanceCm);
        if (success)
        {
            Console.WriteLine($"Successfully updated physical distance for client {clientId} to {distanceCm} cm on server");
        }
        else
        {
            Console.WriteLine($"Failed to update physical distance for client {clientId}");
        }
    }

    [RelayCommand]
    private void AddWallpaper()
    {
        // TODO: Open file picker and add wallpaper
    }

    [RelayCommand]
    private void RemoveWallpaper(WallpaperItemViewModel wallpaper)
    {
        Wallpapers.Remove(wallpaper);
    }

    [RelayCommand]
    private async Task ToggleServerMode()
    {
        if (_service.IsServerRunning)
        {
            await _service.StopServerAsync();
        }
        else
        {
            await _service.StartServerAsync();
        }
    }

    /// <summary>
    /// Refresh client topology from server
    /// </summary>
    private async void RefreshTopology()
    {
        try
        {
            if (_service.IsServerRunning)
            {
                // Server mode - get connected clients directly
                var connectedClients = _service.GetConnectedClients().ToList();
                UpdateClientList(connectedClients);
            }
            else if (_service.IsClientConnected)
            {
                // Client mode - get topology from server
                var topology = await _service.GetTopologyAsync();
                if (topology != null)
                {
                    UpdateClientList(topology.Clients);
                }
            }
            else
            {
                // Not connected - clear clients
                Clients.Clear();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error refreshing topology: {ex.Message}");
        }
    }

    /// <summary>
    /// Update client list from gRPC topology
    /// </summary>
    private void UpdateClientList(IEnumerable<WaBiBaBuSy.Grpc.ConnectedClient> connectedClients)
    {
        // Remove clients that are no longer connected
        var clientIds = connectedClients.Select(c => c.ClientId).ToHashSet();
        var toRemove = Clients.Where(c => !clientIds.Contains(c.ClientId)).ToList();
        foreach (var client in toRemove)
        {
            Clients.Remove(client);
        }

        // Add or update clients
        int index = 0;
        foreach (var grpcClient in connectedClients.OrderBy(c => c.OrderPosition))
        {
            var existing = Clients.FirstOrDefault(c => c.ClientId == grpcClient.ClientId);
            if (existing != null)
            {
                // Update existing
                existing.Hostname = grpcClient.Hostname;
                existing.IpAddress = grpcClient.IpAddress;
                existing.IsConnected = grpcClient.Status == WaBiBaBuSy.Grpc.ClientStatusEnum.ClientConnected ||
                                      grpcClient.Status == WaBiBaBuSy.Grpc.ClientStatusEnum.ClientPlaying;
                existing.Status = grpcClient.Status.ToString();
                existing.Order = grpcClient.OrderPosition;
                existing.PhysicalDistanceCm = grpcClient.PhysicalDistanceCm;
            }
            else
            {
                // Add new client
                var newClient = new ClientNodeViewModel
                {
                    ClientId = grpcClient.ClientId,
                    Hostname = grpcClient.Hostname,
                    IpAddress = grpcClient.IpAddress,
                    IsConnected = grpcClient.Status == WaBiBaBuSy.Grpc.ClientStatusEnum.ClientConnected ||
                                 grpcClient.Status == WaBiBaBuSy.Grpc.ClientStatusEnum.ClientPlaying,
                    Status = grpcClient.Status.ToString(),
                    Order = grpcClient.OrderPosition,
                    PhysicalDistanceCm = grpcClient.PhysicalDistanceCm,
                    X = 100 + (index * 200), // Space clients horizontally
                    Y = 100
                };
                Clients.Add(newClient);
            }
            index++;
        }
    }

    private void OnRefreshTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        RefreshTopology();
    }

    private void OnServerStatusChanged(object? sender, Core.Services.Networking.ServerStatusChangedEventArgs e)
    {
        IsServerMode = e.IsRunning;
        ServerStatus = e.IsRunning ? $"Running on port {e.Port}" : "Stopped";
    }

    private void OnClientConnectionStatusChanged(object? sender, Core.Services.Networking.ConnectionStatusChangedEventArgs e)
    {
        if (!e.IsConnected)
        {
            IsServerMode = false;
        }
    }
}

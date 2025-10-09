using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Wallpaper;

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

    // Storage provider for file picker dialogs
    private IStorageProvider? _storageProvider;

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

        // Load wallpaper gallery from disk
        LoadWallpaperGallery();

        // Initial refresh
        RefreshTopology();
    }

    /// <summary>
    /// Load wallpaper gallery from persistent storage
    /// </summary>
    private void LoadWallpaperGallery()
    {
        try
        {
            var gallery = ConfigurationManager.LoadWallpaperGallery();

            Console.WriteLine($"Loading {gallery.Wallpapers.Count} wallpapers from gallery");

            foreach (var item in gallery.Wallpapers)
            {
                // Verify file still exists
                if (!File.Exists(item.FilePath))
                {
                    Console.WriteLine($"Wallpaper file not found, skipping: {item.FilePath}");
                    continue;
                }

                // Convert to view model
                var viewModel = new WallpaperItemViewModel
                {
                    WallpaperId = item.WallpaperId,
                    Name = item.Name,
                    FilePath = item.FilePath,
                    Type = Enum.Parse<WallpaperType>(item.Type),
                    Resolution = item.Resolution,
                    FileSizeBytes = item.FileSizeBytes,
                    IsActive = item.IsActive
                };

                // Set thumbnail path for images and GIFs
                if (viewModel.Type == WallpaperType.Image || viewModel.Type == WallpaperType.Gif)
                {
                    viewModel.ThumbnailPath = item.FilePath;
                }

                // Load thumbnail
                viewModel.LoadThumbnail();

                Wallpapers.Add(viewModel);
            }

            Console.WriteLine($"Loaded {Wallpapers.Count} wallpapers successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading wallpaper gallery: {ex.Message}");
        }
    }

    /// <summary>
    /// Save wallpaper gallery to persistent storage
    /// </summary>
    private void SaveWallpaperGallery()
    {
        try
        {
            var gallery = new WallpaperGallery
            {
                Wallpapers = Wallpapers.Select(vm => new WallpaperGalleryItem
                {
                    WallpaperId = vm.WallpaperId,
                    Name = vm.Name,
                    FilePath = vm.FilePath,
                    Type = vm.Type.ToString(),
                    Resolution = vm.Resolution,
                    FileSizeBytes = vm.FileSizeBytes,
                    IsActive = vm.IsActive
                }).ToList()
            };

            ConfigurationManager.SaveWallpaperGallery(gallery);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving wallpaper gallery: {ex.Message}");
        }
    }

    /// <summary>
    /// Set the storage provider for file picker dialogs (called from View)
    /// </summary>
    public void SetStorageProvider(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
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
    private async Task AddWallpaper()
    {
        if (_storageProvider == null)
        {
            Console.WriteLine("Storage provider not available");
            return;
        }

        try
        {
            // Define file type filters
            var fileTypeFilters = new List<FilePickerFileType>
            {
                new("All Supported")
                {
                    Patterns = new[] { "*.mp4", "*.avi", "*.mkv", "*.mov", "*.wmv", "*.webm", "*.flv",
                                       "*.gif", "*.jpg", "*.jpeg", "*.png", "*.bmp" }
                },
                new("Videos")
                {
                    Patterns = new[] { "*.mp4", "*.avi", "*.mkv", "*.mov", "*.wmv", "*.webm", "*.flv" }
                },
                new("GIFs")
                {
                    Patterns = new[] { "*.gif" }
                },
                new("Images")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp" }
                }
            };

            // Open file picker
            var result = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Wallpaper",
                AllowMultiple = true,
                FileTypeFilter = fileTypeFilters
            });

            if (result == null || result.Count == 0)
                return;

            // Add each selected file to the wallpapers collection
            foreach (var file in result)
            {
                await AddWallpaperFromFile(file);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error adding wallpaper: {ex.Message}");
        }
    }

    /// <summary>
    /// Add a wallpaper from a storage file
    /// </summary>
    private async Task AddWallpaperFromFile(IStorageFile file)
    {
        try
        {
            var filePath = file.Path.LocalPath;
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            // Get file size
            var fileInfo = new FileInfo(filePath);
            var fileSizeBytes = fileInfo.Length;

            // Determine wallpaper type
            WallpaperType type;
            if (new[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm", ".flv" }.Contains(extension))
                type = WallpaperType.Video;
            else if (extension == ".gif")
                type = WallpaperType.Gif;
            else if (new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(extension))
                type = WallpaperType.Image;
            else
                return; // Unsupported format

            // Get image/video dimensions (simplified - you could use proper video/image libraries for this)
            var resolution = "Unknown";
            string? thumbnailPath = null;

            // For images and GIFs, we can use them directly as thumbnails
            if (type == WallpaperType.Image || type == WallpaperType.Gif)
            {
                thumbnailPath = filePath;

                // Try to get actual resolution
                try
                {
                    using var image = System.Drawing.Image.FromFile(filePath);
                    resolution = $"{image.Width}x{image.Height}";
                }
                catch
                {
                    // Ignore resolution detection errors
                }
            }

            // Create wallpaper view model
            var wallpaper = new WallpaperItemViewModel
            {
                WallpaperId = Guid.NewGuid().ToString(),
                Name = fileName,
                FilePath = filePath,
                Type = type,
                Resolution = resolution,
                FileSizeBytes = fileSizeBytes,
                ThumbnailPath = thumbnailPath ?? string.Empty,
                IsActive = false
            };

            // Load thumbnail bitmap
            wallpaper.LoadThumbnail();

            // Add to collection
            Wallpapers.Add(wallpaper);

            Console.WriteLine($"Added wallpaper: {fileName} ({type}, {wallpaper.FileSize})");

            // Save gallery to disk
            SaveWallpaperGallery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error adding wallpaper from file: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RemoveWallpaper(WallpaperItemViewModel wallpaper)
    {
        Wallpapers.Remove(wallpaper);

        // Save gallery to disk
        SaveWallpaperGallery();

        Console.WriteLine($"Removed wallpaper: {wallpaper.Name}");
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

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Models;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly WaBiBaBuSyService _service;
    private readonly System.Timers.Timer _refreshTimer;
    // private IWallpaperRenderer? _localWallpaperRenderer; // For local-only mode - TODO: implement with proper DI

    [ObservableProperty]
    private ObservableCollection<ClientNodeViewModel> _clients = new();

    [ObservableProperty]
    private ObservableCollection<WallpaperItemViewModel> _wallpapers = new();

    [ObservableProperty]
    private int _clientCount = 0;

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

        // Update server status on initialization
        UpdateServerStatus();
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
    private void ApplyWallpaperToSelected()
    {
        if (SelectedClient == null || SelectedWallpaper == null)
        {
            Console.WriteLine("[ApplyWallpaperToSelected] No client or wallpaper selected");
            return;
        }

        if (!_service.IsServerRunning || _service.SyncCoordinator == null)
        {
            Console.WriteLine("[ApplyWallpaperToSelected] Server not running or sync coordinator not available");
            return;
        }

        try
        {
            Console.WriteLine($"[ApplyWallpaperToSelected] Applying wallpaper '{SelectedWallpaper.Name}' to client '{SelectedClient.Hostname}'");

            // Use wallpaper ID as content ID
            var contentId = SelectedWallpaper.WallpaperId;

            // For single client, we need to implement a targeted send
            // For now, we'll just update the UI and log a warning
            Console.WriteLine($"WARNING: Single client wallpaper application not yet implemented in coordinator. Use 'Apply to All Clients' instead.");

            // Update UI optimistically
            SelectedClient.CurrentWallpaper = SelectedWallpaper.Name;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApplyWallpaperToSelected] Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply selected wallpaper to all connected clients OR to local machine in local-only mode
    ///
    /// IMPORTANT NOTE: For networked mode, clients must have the wallpaper file in their cache directory.
    /// Current limitation: Server-to-client content transfer not yet implemented.
    ///
    /// Workaround for testing:
    /// 1. Ensure wallpaper files are in server's ContentDirectory
    /// 2. Manually copy the same files to each client's CacheDirectory
    /// 3. The WallpaperId is used as the contentId for synchronization
    /// </summary>
    [RelayCommand]
    private async Task ApplyWallpaperToAll()
    {
        if (SelectedWallpaper == null)
        {
            Console.WriteLine("[ApplyWallpaperToAll] No wallpaper selected");
            return;
        }

        // Check if we're in local-only mode
        var isLocalOnlyMode = !_service.IsServerRunning && !_service.IsClientConnected;

        if (isLocalOnlyMode)
        {
            Console.WriteLine("[ApplyWallpaperToAll] Local-only mode detected");
            Console.WriteLine("[ApplyWallpaperToAll] NOTE: Local wallpaper application requires proper renderer setup with DI");
            Console.WriteLine("[ApplyWallpaperToAll] TODO: Implement local wallpaper application with proper dependency injection");
            // await ApplyWallpaperLocally(SelectedWallpaper);
            return;
        }

        if (!_service.IsServerRunning || _service.SyncCoordinator == null)
        {
            Console.WriteLine("[ApplyWallpaperToAll] Server not running or sync coordinator not available");
            return;
        }

        try
        {
            Console.WriteLine($"[ApplyWallpaperToAll] Broadcasting wallpaper '{SelectedWallpaper.Name}' to all clients");

            // Use wallpaper ID as content ID
            var contentId = SelectedWallpaper.WallpaperId;
            var filePath = SelectedWallpaper.FilePath;

            Console.WriteLine($"[ApplyWallpaperToAll] Content ID: {contentId}");
            Console.WriteLine($"[ApplyWallpaperToAll] File Path: {filePath}");
            Console.WriteLine($"[ApplyWallpaperToAll] NOTE: Clients must have this file in their cache directory!");

            // Update UI optimistically
            foreach (var client in Clients.Where(c => c.IsConnected && c.ClientId != "SERVER_LOCALHOST"))
            {
                client.CurrentWallpaper = SelectedWallpaper.Name;
            }

            // Send LOAD command to all clients
            Console.WriteLine($"[ApplyWallpaperToAll] Sending LOAD command...");
            await _service.SyncCoordinator.BroadcastLoadWallpaperAsync(contentId, filePath);

            // Wait a moment for LOAD to complete
            await Task.Delay(500);

            // Send PLAY command to all clients
            Console.WriteLine($"[ApplyWallpaperToAll] Sending PLAY command...");
            await _service.SyncCoordinator.BroadcastPlayAsync(contentId);

            Console.WriteLine($"[ApplyWallpaperToAll] Successfully broadcast wallpaper to all clients");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApplyWallpaperToAll] Error applying wallpaper: {ex.Message}");
            Console.WriteLine($"[ApplyWallpaperToAll] Stack trace: {ex.StackTrace}");
        }
    }

    // TODO: Implement local wallpaper application
    // This requires proper dependency injection for ILogger and DesktopWindowManager
    // For now, this is disabled - local wallpaper application will be implemented later
    /*
    /// <summary>
    /// Apply wallpaper locally without network (local-only mode)
    /// Uses the same wallpaper rendering engine as networked mode
    /// </summary>
    private async Task ApplyWallpaperLocally(WallpaperItemViewModel wallpaper)
    {
        try
        {
            Console.WriteLine($"[ApplyWallpaperLocally] Applying '{wallpaper.Name}' to local machine");

            // Dispose previous renderer if exists
            _localWallpaperRenderer?.Dispose();

            // Create appropriate renderer based on file extension
            var extension = Path.GetExtension(wallpaper.FilePath).ToLowerInvariant();
            _localWallpaperRenderer = extension switch
            {
                ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".flv"
                    => new WaBiBaBuSy.WallpaperEngine.Renderers.VideoWallpaperRenderer(),
                ".gif"
                    => new WaBiBaBuSy.WallpaperEngine.Renderers.GifWallpaperRenderer(),
                ".jpg" or ".jpeg" or ".png" or ".bmp"
                    => new WaBiBaBuSy.WallpaperEngine.Renderers.ImageWallpaperRenderer(),
                _ => null
            };

            if (_localWallpaperRenderer == null)
            {
                Console.WriteLine($"[ApplyWallpaperLocally] Unsupported file type: {extension}");
                return;
            }

            // Initialize and play
            var config = new WallpaperConfig
            {
                FilePath = wallpaper.FilePath,
                Loop = true
            };

            await _localWallpaperRenderer.InitializeAsync(config);
            await _localWallpaperRenderer.StartAsync();

            // Update UI
            var localClient = Clients.FirstOrDefault(c => c.ClientId == "LOCAL_MACHINE");
            if (localClient != null)
            {
                localClient.CurrentWallpaper = wallpaper.Name;
            }

            Console.WriteLine($"[ApplyWallpaperLocally] Successfully applied '{wallpaper.Name}' locally");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApplyWallpaperLocally] Error: {ex.Message}");
            Console.WriteLine($"[ApplyWallpaperLocally] Stack trace: {ex.StackTrace}");
        }
    }
    */

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
    private Task AddWallpaperFromFile(IStorageFile file)
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
                return Task.CompletedTask; // Unsupported format

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

        return Task.CompletedTask;
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
    /// Refresh client topology from server OR show local-only mode
    /// </summary>
    public async void RefreshTopology()
    {
        try
        {
            Console.WriteLine($"[RefreshTopology] Called - IsServerRunning: {_service.IsServerRunning}, IsClientConnected: {_service.IsClientConnected}");

            // Always show at least the local machine
            if (_service.IsServerRunning)
            {
                // Server mode - get connected clients and add localhost as server node
                var connectedClients = _service.GetConnectedClients().ToList();
                Console.WriteLine($"Server mode: Got {connectedClients.Count} connected clients");

                // Create a list that includes the server (localhost) as the first node
                var allNodes = new List<WaBiBaBuSy.Grpc.ConnectedClient>();

                // Add localhost server node
                var serverNode = new WaBiBaBuSy.Grpc.ConnectedClient
                {
                    ClientId = "SERVER_LOCALHOST",
                    Hostname = Environment.MachineName,
                    IpAddress = "127.0.0.1 (Server)",
                    Status = WaBiBaBuSy.Grpc.ClientStatusEnum.ClientConnected,
                    OrderPosition = 0,
                    PhysicalDistanceCm = 0,
                    ScreenConfig = new WaBiBaBuSy.Grpc.ScreenConfiguration()
                };

                allNodes.Add(serverNode);
                Console.WriteLine($"Added server node: {serverNode.Hostname} at position {serverNode.OrderPosition}");

                // Add all connected clients with adjusted order positions
                foreach (var client in connectedClients)
                {
                    allNodes.Add(new WaBiBaBuSy.Grpc.ConnectedClient
                    {
                        ClientId = client.ClientId,
                        Hostname = client.Hostname,
                        IpAddress = client.IpAddress,
                        Status = client.Status,
                        OrderPosition = client.OrderPosition + 1, // Offset by 1 since server is position 0
                        PhysicalDistanceCm = client.PhysicalDistanceCm,
                        ScreenConfig = client.ScreenConfig
                    });
                }

                Console.WriteLine($"Total nodes to display: {allNodes.Count}");
                UpdateClientList(allNodes);
            }
            else if (_service.IsClientConnected)
            {
                // Client mode - get topology from server
                var topology = await _service.GetTopologyAsync();
                if (topology != null)
                {
                    Console.WriteLine($"Client mode: Got topology with {topology.Clients.Count} clients");
                    UpdateClientList(topology.Clients);
                }
            }
            else
            {
                // Local-only mode - show local machine node
                Console.WriteLine("[RefreshTopology] Local-only mode - showing local machine");
                ShowLocalMachineNode();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error refreshing topology: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Show local machine node in local-only mode (when not connected to server/client)
    /// </summary>
    private void ShowLocalMachineNode()
    {
        var localNode = new WaBiBaBuSy.Grpc.ConnectedClient
        {
            ClientId = "LOCAL_MACHINE",
            Hostname = Environment.MachineName,
            IpAddress = "Local (No Network)",
            Status = WaBiBaBuSy.Grpc.ClientStatusEnum.ClientConnected,
            OrderPosition = 0,
            PhysicalDistanceCm = 0,
            ScreenConfig = new WaBiBaBuSy.Grpc.ScreenConfiguration()
        };

        UpdateClientList(new[] { localNode });
    }

    /// <summary>
    /// Update client list from gRPC topology
    /// </summary>
    private void UpdateClientList(IEnumerable<WaBiBaBuSy.Grpc.ConnectedClient> connectedClients)
    {
        Console.WriteLine($"UpdateClientList called with {connectedClients.Count()} clients");

        // Ensure UI updates happen on the UI thread
        Dispatcher.UIThread.Post(() =>
        {
            // Remove clients that are no longer connected
            var clientIds = connectedClients.Select(c => c.ClientId).ToHashSet();
            var toRemove = Clients.Where(c => !clientIds.Contains(c.ClientId)).ToList();
            foreach (var client in toRemove)
            {
                Console.WriteLine($"Removing client: {client.ClientId}");
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
                    Console.WriteLine($"Updating existing client: {grpcClient.ClientId} at position {grpcClient.OrderPosition}");
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
                    var x = 100 + (index * 200);
                    var y = 100;
                    Console.WriteLine($"Adding new client: {grpcClient.ClientId} ({grpcClient.Hostname}) at X={x}, Y={y}");

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
                        X = x,
                        Y = y
                    };
                    Clients.Add(newClient);
                    Console.WriteLine($"Client added. Total clients now: {Clients.Count}");
                }
                index++;
            }

            ClientCount = Clients.Count;
            Console.WriteLine($"[UpdateClientList] Complete. Final client count: {Clients.Count}");
            Console.WriteLine($"[UpdateClientList] Clients in collection: {string.Join(", ", Clients.Select(c => c.Hostname))}");
        });
    }

    private void OnRefreshTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        RefreshTopology();
    }

    private void OnServerStatusChanged(object? sender, Core.Services.Networking.ServerStatusChangedEventArgs e)
    {
        IsServerMode = e.IsRunning;
        ServerStatus = e.IsRunning ? $"Running on port {e.Port}" : "Stopped";

        // Refresh topology when server status changes
        Console.WriteLine($"[OnServerStatusChanged] Server status changed to: {(e.IsRunning ? "Running" : "Stopped")}");
        RefreshTopology();
    }

    private void OnClientConnectionStatusChanged(object? sender, Core.Services.Networking.ConnectionStatusChangedEventArgs e)
    {
        if (!e.IsConnected)
        {
            IsServerMode = false;
        }
    }

    /// <summary>
    /// Update server status display based on current state
    /// Called when window is opened to sync status with actual server state
    /// </summary>
    public void UpdateServerStatus()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_service.IsServerRunning)
            {
                IsServerMode = true;
                // Get port from configuration
                var config = ConfigurationManager.LoadServerConfiguration();
                ServerStatus = $"Running on port {config.Port}";
            }
            else
            {
                IsServerMode = false;
                ServerStatus = "Stopped";
            }

            // Also refresh topology when status is updated
            RefreshTopology();
        });
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Models;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Native;
using WaBiBaBuSy.WallpaperEngine.Renderers;
using WaBiBaBuSy.UI.Services;

namespace WaBiBaBuSy.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly WaBiBaBuSyService _service;
    private readonly System.Timers.Timer _refreshTimer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DesktopWindowManager _desktopManager;
    private readonly VideoThumbnailGenerator _thumbnailGenerator;
    // Multi-monitor support: ConcurrentDictionary<monitorIndex, renderer> (thread-safe for gRPC callbacks)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IWallpaperRenderer> _localWallpaperRenderers = new();

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

    [ObservableProperty]
    private bool _isCrossScreenMode;

    [ObservableProperty]
    private bool _isCrossScreenRunning;

    private UI.Services.CrossScreenWallpaperCoordinator? _crossScreenCoordinator;
    private CrossScreenConfig? _crossScreenConfig;
    private string? _currentAnimationScheduleId;  // Track active animation schedule (Phase 3)

    // Storage provider for file picker dialogs
    private IStorageProvider? _storageProvider;

    // Window reference for dialogs
    private Window? _mainWindow;

    public MainWindowViewModel(WaBiBaBuSyService service)
    {
        _service = service;

        // Initialize logger factory and desktop manager for local wallpaper rendering
        _loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().AddDebug());
        _desktopManager = new DesktopWindowManager(_loggerFactory.CreateLogger<DesktopWindowManager>());
        _thumbnailGenerator = new VideoThumbnailGenerator(_loggerFactory.CreateLogger<VideoThumbnailGenerator>());

        // Subscribe to service events
        _service.ServerStatusChanged += OnServerStatusChanged;
        _service.ClientConnectionStatusChanged += OnClientConnectionStatusChanged;

        // Setup refresh timer for topology updates (but don't start it yet - window will start it)
        _refreshTimer = new System.Timers.Timer(2000); // Refresh every 2 seconds
        _refreshTimer.Elapsed += OnRefreshTimerElapsed;
        // Note: Timer is started by the window's Opened event to avoid background updates

        // Load wallpaper gallery from disk
        LoadWallpaperGallery();
    }

    /// <summary>
    /// Load wallpaper gallery from persistent storage
    /// </summary>
    private void LoadWallpaperGallery()
    {
        try
        {
            var gallery = ConfigurationManager.LoadWallpaperGallery();

            Debug.WriteLine($"Loading {gallery.Wallpapers.Count} wallpapers from gallery");

            foreach (var item in gallery.Wallpapers)
            {
                // Verify file still exists
                if (!File.Exists(item.FilePath))
                {
                    Debug.WriteLine($"Wallpaper file not found, skipping: {item.FilePath}");
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
                // For videos, check if cached thumbnail exists
                else if (viewModel.Type == WallpaperType.Video)
                {
                    // Generate the same cache key that would be used for this video
                    var fileInfo = new FileInfo(item.FilePath);
                    var cacheKey = $"{item.FilePath}|{fileInfo.LastWriteTimeUtc.Ticks}|320";
                    var hash = ComputeThumbnailHash(cacheKey);
                    var thumbnailCacheDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "WaBiBaBuSy",
                        "Thumbnails");
                    var cachedThumbnailPath = Path.Combine(thumbnailCacheDir, $"{hash}.jpg");

                    if (File.Exists(cachedThumbnailPath))
                    {
                        viewModel.ThumbnailPath = cachedThumbnailPath;
                        Debug.WriteLine($"Found cached thumbnail for video: {item.Name}");
                    }
                    else
                    {
                        // Generate thumbnail asynchronously
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var thumb = await _thumbnailGenerator.GenerateThumbnail(item.FilePath);
                                if (!string.IsNullOrEmpty(thumb))
                                {
                                    await Dispatcher.UIThread.InvokeAsync(() =>
                                    {
                                        viewModel.ThumbnailPath = thumb;
                                        viewModel.LoadThumbnail();
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error generating thumbnail on load: {ex.Message}");
                            }
                        });
                    }
                }

                // Load thumbnail
                viewModel.LoadThumbnail();

                Wallpapers.Add(viewModel);
            }

            Debug.WriteLine($"Loaded {Wallpapers.Count} wallpapers successfully");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading wallpaper gallery: {ex.Message}");
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
            Debug.WriteLine($"Error saving wallpaper gallery: {ex.Message}");
        }
    }

    /// <summary>
    /// Set the storage provider for file picker dialogs (called from View)
    /// </summary>
    public void SetStorageProvider(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
    }

    /// <summary>
    /// Set the main window reference for dialog parenting (called from View)
    /// </summary>
    public void SetMainWindow(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    /// <summary>
    /// Start the refresh timer (called when window becomes visible)
    /// </summary>
    public void StartRefreshTimer()
    {
        Debug.WriteLine("[MainWindowViewModel] Starting refresh timer");
        _refreshTimer.Start();
        // Do an immediate refresh
        RefreshTopology();
    }

    /// <summary>
    /// Stop the refresh timer (called when window is closed/hidden)
    /// </summary>
    public void StopRefreshTimer()
    {
        Debug.WriteLine("[MainWindowViewModel] Stopping refresh timer");
        _refreshTimer.Stop();
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
        if (SelectedWallpaper == null)
        {
            Debug.WriteLine("[ApplyWallpaperToSelected] No wallpaper selected");
            return;
        }

        // Get all selected clients (multi-select support)
        var selectedClients = Clients.Where(c => c.IsSelected).ToList();

        if (selectedClients.Count == 0)
        {
            Debug.WriteLine("[ApplyWallpaperToSelected] No clients selected");
            return;
        }

        Debug.WriteLine($"[ApplyWallpaperToSelected] Applying wallpaper '{SelectedWallpaper.Name}' to {selectedClients.Count} selected client(s)");

        // Apply wallpaper to each selected client
        foreach (var client in selectedClients)
        {
            try
            {
                Debug.WriteLine($"[ApplyWallpaperToSelected] Sending wallpaper to '{client.Hostname}'");

                // Check if this is a local client (LOCAL_MACHINE)
                if (client.ClientId.StartsWith("LOCAL_MACHINE"))
                {
                    // Apply using unified method
                    await ApplyWallpaperAsync(SelectedWallpaper, client.ClientId);
                }
                else if (_service.IsServerRunning && _service.SyncCoordinator != null)
                {
                    // Apply via network to remote client
                    // Load wallpaper on client (targeted, not broadcast)
                    await _service.SyncCoordinator.LoadWallpaperOnClientAsync(
                        client.ClientId,
                        SelectedWallpaper.WallpaperId,
                        SelectedWallpaper.FilePath
                    );

                    // Play wallpaper on client (targeted, not broadcast)
                    await _service.SyncCoordinator.PlayOnClientAsync(
                        client.ClientId,
                        SelectedWallpaper.WallpaperId
                    );
                }
                else
                {
                    Debug.WriteLine($"[ApplyWallpaperToSelected] Skipping remote client '{client.Hostname}' - server not running");
                    continue;
                }

                // Update UI
                client.CurrentWallpaper = SelectedWallpaper.Name;

                Debug.WriteLine($"[ApplyWallpaperToSelected] Successfully applied wallpaper to '{client.Hostname}'");
            }
            catch (Exception clientEx)
            {
                Debug.WriteLine($"[ApplyWallpaperToSelected] Error applying to '{client.Hostname}': {clientEx.Message}");
            }
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
            Debug.WriteLine("[ApplyWallpaperToAll] No wallpaper selected");
            return;
        }

        try
        {
            Debug.WriteLine($"[ApplyWallpaperToAll] Applying '{SelectedWallpaper.Name}' to ALL targets");

            // Apply to all connected targets using UNIFIED method
            var allTargets = Clients.Where(c => c.IsConnected).ToList();
            foreach (var target in allTargets)
            {
                await ApplyWallpaperAsync(SelectedWallpaper, target.ClientId);
            }

            Debug.WriteLine($"[ApplyWallpaperToAll] Successfully applied wallpaper to all targets");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApplyWallpaperToAll] Error: {ex.Message}");
        }
    }

    /// <summary>
    /// UNIFIED: Apply wallpaper to ANY target (local monitor or remote client).
    /// Single code path for all wallpaper application - handles local and remote equally.
    /// </summary>
    private async Task ApplyWallpaperAsync(WallpaperItemViewModel wallpaper, string targetClientId)
    {
        if (wallpaper == null || string.IsNullOrEmpty(targetClientId))
            return;

        try
        {
            var isLocal = targetClientId.StartsWith("LOCAL_MACHINE_MONITOR_");
            var monitorIndex = isLocal ? int.Parse(targetClientId.Replace("LOCAL_MACHINE_MONITOR_", "")) : 0;

            Debug.WriteLine($"[ApplyWallpaperAsync] Applying '{wallpaper.Name}' to {targetClientId}");

            if (isLocal)
            {
                // LOCAL: Create renderer on this machine
                await ApplyWallpaperLocallyInternal(wallpaper, monitorIndex);
            }
            else
            {
                // REMOTE: Send command to remote client via gRPC
                await ApplyWallpaperRemotelyInternal(wallpaper, targetClientId);
            }

            // Update UI
            var client = Clients.FirstOrDefault(c => c.ClientId == targetClientId);
            if (client != null)
                client.CurrentWallpaper = wallpaper.Name;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApplyWallpaperAsync] Error on {targetClientId}: {ex.Message}");
        }
    }

    /// <summary>
    /// INTERNAL: Apply wallpaper locally (implementation detail)
    /// </summary>
    private async Task ApplyWallpaperLocallyInternal(WallpaperItemViewModel wallpaper, int monitorIndex = 0)
    {
        try
        {
            Debug.WriteLine($"[ApplyWallpaperLocally] Applying '{wallpaper.Name}' to local machine monitor {monitorIndex}");

            // Dispose previous renderer for this monitor if exists (atomic operation)
            if (_localWallpaperRenderers.TryRemove(monitorIndex, out var existingRenderer))
            {
                existingRenderer.Dispose();
            }

            // Validate monitor index
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (monitorIndex < 0 || monitorIndex >= screens.Length)
            {
                Debug.WriteLine($"[ApplyWallpaperLocally] Invalid monitor index {monitorIndex}. Available monitors: {screens.Length}");
                return;
            }

            // Create appropriate renderer based on file extension
            var extension = Path.GetExtension(wallpaper.FilePath).ToLowerInvariant();
            IWallpaperRenderer? renderer = extension switch
            {
                ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".flv"
                    => new VideoWallpaperRenderer(
                        _loggerFactory.CreateLogger<VideoWallpaperRenderer>(),
                        _desktopManager),
                ".gif"
                    => new GifWallpaperRenderer(
                        _loggerFactory.CreateLogger<GifWallpaperRenderer>(),
                        _desktopManager),
                ".jpg" or ".jpeg" or ".png" or ".bmp"
                    => new ImageWallpaperRendererLibVLC(
                        _loggerFactory.CreateLogger<ImageWallpaperRendererLibVLC>(),
                        _desktopManager),
                _ => null
            };

            if (renderer == null)
            {
                Debug.WriteLine($"[ApplyWallpaperLocally] Unsupported file type: {extension}");
                return;
            }

            // Initialize and play with specific monitor index
            var config = new WallpaperConfig
            {
                FilePath = wallpaper.FilePath,
                Loop = true,
                MonitorIndex = monitorIndex
            };

            await renderer.InitializeAsync(config);
            await renderer.StartAsync();

            // Store renderer for this monitor
            _localWallpaperRenderers[monitorIndex] = renderer;

            // Update UI for the specific monitor node
            var localClient = Clients.FirstOrDefault(c => c.ClientId == $"LOCAL_MACHINE_MONITOR_{monitorIndex}");
            if (localClient != null)
            {
                localClient.CurrentWallpaper = wallpaper.Name;
            }

            Debug.WriteLine($"[ApplyWallpaperLocally] Successfully applied '{wallpaper.Name}' to monitor {monitorIndex}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApplyWallpaperLocally] Error: {ex.Message}");
            Debug.WriteLine($"[ApplyWallpaperLocally] Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// INTERNAL: Apply wallpaper to remote client via gRPC (implementation detail)
    /// </summary>
    private async Task ApplyWallpaperRemotelyInternal(WallpaperItemViewModel wallpaper, string clientId)
    {
        if (!_service.IsServerRunning || _service.SyncCoordinator == null)
        {
            Debug.WriteLine($"[ApplyWallpaperRemotelyInternal] Server not running or sync coordinator unavailable");
            return;
        }

        try
        {
            Debug.WriteLine($"[ApplyWallpaperRemotelyInternal] Sending wallpaper '{wallpaper.Name}' to remote client {clientId}");

            var contentId = wallpaper.WallpaperId;
            var filePath = wallpaper.FilePath;

            // Send LOAD command
            await _service.SyncCoordinator.BroadcastLoadWallpaperAsync(contentId, filePath);
            await Task.Delay(200);  // Wait for LOAD to complete

            // Send PLAY command
            await _service.SyncCoordinator.BroadcastPlayAsync(contentId);

            Debug.WriteLine($"[ApplyWallpaperRemotelyInternal] Successfully sent wallpaper to {clientId}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApplyWallpaperRemotelyInternal] Error: {ex.Message}");
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
                Debug.WriteLine($"Updated physical distance for client {client.ClientId} to {client.PhysicalDistanceCm} cm");
            }
            else if (_service.IsClientConnected)
            {
                // If we're a client, we need to send this update to the server via gRPC
                await UpdateClientDistanceOnServerAsync(client.ClientId, client.PhysicalDistanceCm);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error updating client distance: {ex.Message}");
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
            Debug.WriteLine($"Successfully updated physical distance for client {clientId} to {distanceCm} cm on server");
        }
        else
        {
            Debug.WriteLine($"Failed to update physical distance for client {clientId}");
        }
    }

    [RelayCommand]
    private async Task AddWallpaper()
    {
        if (_storageProvider == null)
        {
            Debug.WriteLine("Storage provider not available");
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
            Debug.WriteLine($"Error adding wallpaper: {ex.Message}");
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
            // For videos, generate thumbnail using FFmpeg in background
            else if (type == WallpaperType.Video)
            {
                // Generate thumbnail asynchronously to avoid blocking UI
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Debug.WriteLine($"Starting FFmpeg thumbnail generation for: {filePath}");
                        var thumb = await _thumbnailGenerator.GenerateThumbnail(filePath);

                        if (!string.IsNullOrEmpty(thumb))
                        {
                            Debug.WriteLine($"Thumbnail generated: {thumb}");

                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                var wallpaperItem = Wallpapers.FirstOrDefault(w => w.FilePath == filePath);
                                if (wallpaperItem != null)
                                {
                                    wallpaperItem.ThumbnailPath = thumb;
                                    wallpaperItem.LoadThumbnail();
                                    Debug.WriteLine($"Thumbnail loaded for: {fileName}");
                                }
                            });
                        }
                        else
                        {
                            Debug.WriteLine($"Thumbnail generation failed for: {filePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ERROR in thumbnail generation: {ex.Message}");
                    }
                });

                resolution = "Video";
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

            Debug.WriteLine($"Added wallpaper: {fileName} ({type}, {wallpaper.FileSize})");

            // Save gallery to disk
            SaveWallpaperGallery();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error adding wallpaper from file: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void RemoveWallpaper(WallpaperItemViewModel wallpaper)
    {
        Wallpapers.Remove(wallpaper);

        // Save gallery to disk
        SaveWallpaperGallery();

        Debug.WriteLine($"Removed wallpaper: {wallpaper.Name}");
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
            // Debug.WriteLine($"[RefreshTopology] Called - IsServerRunning: {_service.IsServerRunning}, IsClientConnected: {_service.IsClientConnected}");

            // Always show at least the local machine
            if (_service.IsServerRunning)
            {
                // Server mode - get connected clients and add localhost as server node
                var connectedClients = _service.GetConnectedClients().ToList();
                Debug.WriteLine($"Server mode: Got {connectedClients.Count} connected clients");

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
                Debug.WriteLine($"Added server node: {serverNode.Hostname} at position {serverNode.OrderPosition}");

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

                Debug.WriteLine($"Total nodes to display: {allNodes.Count}");
                UpdateClientList(allNodes);
            }
            else if (_service.IsClientConnected)
            {
                // Client mode - get topology from server
                var topology = await _service.GetTopologyAsync();
                if (topology != null)
                {
                    Debug.WriteLine($"Client mode: Got topology with {topology.Clients.Count} clients");
                    UpdateClientList(topology.Clients);
                }
            }
            else
            {
                // Local-only mode - show local machine node
                Debug.WriteLine("[RefreshTopology] Local-only mode - showing local machine");
                ShowLocalMachineNode();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error refreshing topology: {ex.Message}");
            Debug.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Show local machine node(s) in local-only mode (when not connected to server/client)
    /// Creates one node per monitor for multi-monitor support
    /// </summary>
    private void ShowLocalMachineNode()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        Debug.WriteLine($"[ShowLocalMachineNode] Detected {screens.Length} monitor(s)");

        // Create screen configuration with all monitors
        var screenConfig = new WaBiBaBuSy.Grpc.ScreenConfiguration
        {
            MonitorCount = screens.Length,
            TotalWidth = System.Windows.Forms.SystemInformation.VirtualScreen.Width,
            TotalHeight = System.Windows.Forms.SystemInformation.VirtualScreen.Height
        };

        // Add monitor info for each screen
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            screenConfig.Monitors.Add(new WaBiBaBuSy.Grpc.MonitorInfo
            {
                Index = i,
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height,
                X = screen.Bounds.X,
                Y = screen.Bounds.Y,
                IsPrimary = screen.Primary,
                DeviceName = screen.DeviceName
            });
        }

        // Create one node per monitor
        var nodes = new List<WaBiBaBuSy.Grpc.ConnectedClient>();
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var node = new WaBiBaBuSy.Grpc.ConnectedClient
            {
                ClientId = $"LOCAL_MACHINE_MONITOR_{i}",
                Hostname = $"{Environment.MachineName} - Monitor {i + 1}",
                IpAddress = screen.Primary ? "Primary Monitor" : $"Monitor {i + 1}",
                Status = WaBiBaBuSy.Grpc.ClientStatusEnum.ClientConnected,
                OrderPosition = i,
                PhysicalDistanceCm = 0,
                ScreenConfig = screenConfig // Share the same screen config across all nodes
            };
            nodes.Add(node);
            Debug.WriteLine($"[ShowLocalMachineNode] Created node for monitor {i}: {screen.Bounds.Width}x{screen.Bounds.Height} at ({screen.Bounds.X}, {screen.Bounds.Y})");
        }

        UpdateClientList(nodes);
    }

    /// <summary>
    /// Update client list from gRPC topology
    /// </summary>
    private void UpdateClientList(IEnumerable<WaBiBaBuSy.Grpc.ConnectedClient> connectedClients)
    {
        // Debug.WriteLine($"UpdateClientList called with {connectedClients.Count()} clients");

        // Ensure UI updates happen on the UI thread
        Dispatcher.UIThread.Post(() =>
        {
            // Remove clients that are no longer connected
            var clientIds = connectedClients.Select(c => c.ClientId).ToHashSet();
            var toRemove = Clients.Where(c => !clientIds.Contains(c.ClientId)).ToList();
            foreach (var client in toRemove)
            {
                Debug.WriteLine($"Removing client: {client.ClientId}");
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
                    Debug.WriteLine($"Updating existing client: {grpcClient.ClientId} at position {grpcClient.OrderPosition}");
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
                    Debug.WriteLine($"[UpdateClientList] Adding new client: {grpcClient.ClientId} ({grpcClient.Hostname}) at X={x}, Y={y}");

                    // Extract monitor info for this specific node
                    int monitorIndex = -1;
                    string? monitorName = null;
                    int monitorWidth = 0;
                    int monitorHeight = 0;
                    bool isPrimary = false;

                    // Check if this is a monitor-specific node (LOCAL_MACHINE_MONITOR_X)
                    if (grpcClient.ClientId.StartsWith("LOCAL_MACHINE_MONITOR_"))
                    {
                        var monitorIndexStr = grpcClient.ClientId.Replace("LOCAL_MACHINE_MONITOR_", "");
                        if (int.TryParse(monitorIndexStr, out monitorIndex))
                        {
                            // Find the corresponding monitor info in screen config
                            if (grpcClient.ScreenConfig != null && grpcClient.ScreenConfig.Monitors.Count > monitorIndex)
                            {
                                var monitorInfo = grpcClient.ScreenConfig.Monitors[monitorIndex];
                                monitorName = monitorInfo.DeviceName;
                                monitorWidth = monitorInfo.Width;
                                monitorHeight = monitorInfo.Height;
                                isPrimary = monitorInfo.IsPrimary;
                            }
                        }
                    }

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
                        Y = y,
                        // Monitor-specific properties
                        MonitorIndex = monitorIndex,
                        MonitorName = monitorName,
                        MonitorWidth = monitorWidth,
                        MonitorHeight = monitorHeight,
                        IsPrimaryMonitor = isPrimary
                    };

                    Debug.WriteLine($"[UpdateClientList] About to add client to Clients collection");
                    Debug.WriteLine($"[UpdateClientList] Client details - ID: {newClient.ClientId}, Hostname: {newClient.Hostname}, DisplayName: {newClient.DisplayName}");
                    Debug.WriteLine($"[UpdateClientList] Monitor info - Index: {monitorIndex}, Size: {monitorWidth}x{monitorHeight}, Primary: {isPrimary}");
                    Debug.WriteLine($"[UpdateClientList] Position - X: {newClient.X}, Y: {newClient.Y}");
                    Debug.WriteLine($"[UpdateClientList] Status: {newClient.Status}, IsConnected: {newClient.IsConnected}");

                    Clients.Add(newClient);
                    Debug.WriteLine($"[UpdateClientList] Client added. Total clients now: {Clients.Count}");
                }
                index++;
            }

            ClientCount = Clients.Count;
            Debug.WriteLine($"[UpdateClientList] Complete. Final client count: {Clients.Count}");
            Debug.WriteLine($"[UpdateClientList] Clients in collection: {string.Join(", ", Clients.Select(c => c.Hostname))}");
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
        Debug.WriteLine($"[OnServerStatusChanged] Server status changed to: {(e.IsRunning ? "Running" : "Stopped")}");
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

    /// <summary>
    /// Cleanup method called when application is closing.
    /// Disposes all wallpaper renderers and their associated player processes.
    /// </summary>
    public void Cleanup()
    {
        Debug.WriteLine("[Cleanup] Disposing all wallpaper renderers");

        // Dispose all local wallpaper renderers (which will kill player processes)
        foreach (var kvp in _localWallpaperRenderers.ToList())
        {
            try
            {
                Debug.WriteLine($"[Cleanup] Disposing renderer for monitor {kvp.Key}");
                kvp.Value.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cleanup] Error disposing renderer for monitor {kvp.Key}: {ex.Message}");
            }
        }

        _localWallpaperRenderers.Clear();
        Debug.WriteLine("[Cleanup] All renderers disposed");
    }

    #region Cross-Screen Commands

    [RelayCommand]
    private async Task ConfigureCrossScreen()
    {
        Debug.WriteLine("[ConfigureCrossScreen] Button clicked - opening dialog");
        try
        {
            Debug.WriteLine("[ConfigureCrossScreen] Creating dialog and viewmodel");
            var dialog = new Views.CrossScreenConfigDialog();
            var viewModel = new CrossScreenConfigViewModel();

            // Set storage provider
            if (_storageProvider != null)
            {
                viewModel.SetStorageProvider(_storageProvider);
            }

            // Set available monitors/clients for selection
            viewModel.SetAvailableMonitors(Clients);

            // Load existing configuration or create default
            if (_crossScreenConfig != null)
            {
                viewModel.LoadFromConfig(_crossScreenConfig);
            }
            else
            {
                // Create default configuration
                _crossScreenConfig = new CrossScreenConfig
                {
                    Background = new BackgroundLayerConfig
                    {
                        Mode = BackgroundMode.SolidColor,
                        ColorHex = "#000000"
                    },
                    Animation = new AnimationLayerConfig
                    {
                        AnimationPath = string.Empty,
                        TargetHeight = 720,
                        Loop = true,
                        VerticalAlign = VerticalAlignment.Center
                    },
                    AnimationSpeedPxPerSecond = 500
                };
                viewModel.LoadFromConfig(_crossScreenConfig);
            }

            dialog.DataContext = viewModel;

            // Set close action so ViewModel can close the dialog
            viewModel.SetCloseAction(() => dialog.Close());

            // Show dialog using stored window reference
            if (_mainWindow != null)
            {
                Debug.WriteLine("[ConfigureCrossScreen] Showing dialog");
                await dialog.ShowDialog(_mainWindow);
                Debug.WriteLine("[ConfigureCrossScreen] Dialog closed");

                if (viewModel.DialogResult)
                {
                    _crossScreenConfig = viewModel.BuildConfig();
                    Debug.WriteLine("[CrossScreen] Configuration saved");
                }
                else
                {
                    Debug.WriteLine("[CrossScreen] Configuration cancelled");
                }
            }
            else
            {
                Debug.WriteLine("[ConfigureCrossScreen] ERROR: Could not get main window reference. _mainWindow is null");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ConfigureCrossScreen] ERROR: {ex.Message}");
            Debug.WriteLine($"[ConfigureCrossScreen] Stack trace: {ex.StackTrace}");
        }
    }

    [RelayCommand]
    private async Task StartCrossScreen()
    {
        if (IsCrossScreenRunning)
        {
            Debug.WriteLine("[CrossScreen] Already running");
            return;
        }

        if (_crossScreenConfig == null || string.IsNullOrEmpty(_crossScreenConfig.Animation.AnimationPath))
        {
            Debug.WriteLine("[CrossScreen] No configuration. Opening config dialog...");
            await ConfigureCrossScreen();

            if (_crossScreenConfig == null || string.IsNullOrEmpty(_crossScreenConfig.Animation.AnimationPath))
            {
                Debug.WriteLine("[CrossScreen] Configuration cancelled or incomplete");
                return;
            }
        }

        try
        {
            Debug.WriteLine("[CrossScreen] Starting cross-screen animation...");

            // Create coordinator if needed
            if (_crossScreenCoordinator == null)
            {
                _crossScreenCoordinator = new UI.Services.CrossScreenWallpaperCoordinator(
                    _loggerFactory.CreateLogger<UI.Services.CrossScreenWallpaperCoordinator>(),
                    _loggerFactory,
                    _service.SyncCoordinator);

                _crossScreenCoordinator.StatusChanged += OnCrossScreenStatusChanged;
            }

            // Always ensure frame rendering handler is subscribed (in case it was unsubscribed on previous stop)
            _crossScreenCoordinator.LocalFrameRendered -= OnLocalFrameRendered;  // Remove first to avoid duplicate subscriptions
            _crossScreenCoordinator.LocalFrameRendered += OnLocalFrameRendered;  // Then re-subscribe
            Debug.WriteLine("[CrossScreen] LocalFrameRendered event subscribed successfully");

            // Convert clients to screen configurations, filtering by selected monitors if configured
            var selectedMonitorIds = new HashSet<string>(_crossScreenConfig.SelectedMonitorIds);
            var clientsToUse = selectedMonitorIds.Count > 0
                ? Clients.Where(c => selectedMonitorIds.Contains(c.ClientId))
                : Clients;

            var screenConfigs = clientsToUse.Select(c => new WallpaperEngine.Composition.ScreenConfiguration
            {
                ClientId = c.ClientId,
                Width = c.MonitorWidth > 0 ? c.MonitorWidth : 1920,  // Default if not set
                Height = c.MonitorHeight > 0 ? c.MonitorHeight : 1080,
                Order = c.Order,
                PhysicalDistanceCm = c.PhysicalDistanceCm,
                Hostname = c.Hostname,
                MonitorIndex = c.MonitorIndex
            }).ToList();

            if (screenConfigs.Count == 0)
            {
                Debug.WriteLine("[CrossScreen] No clients connected or selected");
                return;
            }

            Debug.WriteLine($"[CrossScreen] Starting animation on {screenConfigs.Count} selected monitor(s)");

            // Check if we should use Phase 3 orchestrator (when server mode is active)
            if (_service.IsServerMode && _crossScreenConfig?.DistributionMode == WaBiBaBuSy.Models.Wallpaper.AnimationDistributionMode.Sequential)
            {
                Debug.WriteLine("[CrossScreen] Using Phase 3 orchestrator for sequential animation");
                await StartOrchestrationAnimation(screenConfigs);
            }
            else if (_service.IsServerMode && _crossScreenConfig?.DistributionMode == WaBiBaBuSy.Models.Wallpaper.AnimationDistributionMode.Simultaneous)
            {
                Debug.WriteLine("[CrossScreen] Using Phase 3 orchestrator for simultaneous animation");
                await StartOrchestrationAnimation(screenConfigs);
            }
            else
            {
                Debug.WriteLine("[CrossScreen] Using traditional cross-screen coordinator");
                // Initialize and start using traditional method
                await _crossScreenCoordinator.InitializeAsync(screenConfigs, _crossScreenConfig);
                await _crossScreenCoordinator.StartAsync();
            }

            IsCrossScreenRunning = true;
            Debug.WriteLine("[CrossScreen] Animation started successfully");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CrossScreen] Error starting: {ex.Message}");
            IsCrossScreenRunning = false;
        }
    }

    /// <summary>
    /// Convert Wallpaper.BackgroundLayerConfig to Animation.BackgroundLayerConfig
    /// </summary>
    private Models.Animation.BackgroundLayerConfig ConvertToAnimationBackground(Models.Wallpaper.BackgroundLayerConfig wallpaperConfig)
    {
        // Simple direct mapping since both models have the same properties
        var animationConfig = new Models.Animation.BackgroundLayerConfig
        {
            ColorHex = wallpaperConfig.ColorHex ?? "#000000",
            ImagePath = wallpaperConfig.ImagePath ?? string.Empty
        };

        return animationConfig;
    }

    /// <summary>
    /// Start sequential animation using Phase 3 orchestrator
    /// </summary>
    private async Task StartOrchestrationAnimation(List<WallpaperEngine.Composition.ScreenConfiguration> screenConfigs)
    {
        try
        {
            if (_crossScreenConfig?.Animation.AnimationPath == null)
            {
                Debug.WriteLine("[Orchestration] No animation path configured");
                return;
            }

            // Create animation metadata
            var metadata = new Models.Animation.AnimationMetadata
            {
                AnimationId = Guid.NewGuid().ToString(),
                ContentPath = _crossScreenConfig.Animation.AnimationPath,
                TargetHeightPx = _crossScreenConfig.Animation.TargetHeight,  // Use TargetHeight not TargetHeightPx
                AnimationSpeedPxSec = _crossScreenConfig.AnimationSpeedPxPerSecond,  // Use CrossScreenConfig's speed
                DurationMs = 5000,  // Default animation duration (5 seconds)
                StartTimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Background = ConvertToAnimationBackground(_crossScreenConfig.Background),  // Convert background config
                Loop = _crossScreenConfig.Animation.Loop,  // Use animation loop setting
                TargetMonitorIndex = 0
            };

            // Extract selected client IDs
            var selectedClientIds = screenConfigs.Select(s => s.ClientId).ToList();

            // Determine distribution mode from config
            var isSequential = _crossScreenConfig.DistributionMode == WaBiBaBuSy.Models.Wallpaper.AnimationDistributionMode.Sequential;
            Debug.WriteLine($"[Orchestration] Starting {(isSequential ? "sequential" : "simultaneous")} animation with {selectedClientIds.Count} clients");

            // Start animation via orchestrator based on configured distribution mode
            if (isSequential)
            {
                _currentAnimationScheduleId = await _service.StartSequentialAnimationAsync(metadata, selectedClientIds, loop: false);
            }
            else
            {
                _currentAnimationScheduleId = await _service.StartSimultaneousAnimationAsync(metadata, selectedClientIds);
            }

            Debug.WriteLine($"[Orchestration] Animation started with schedule ID: {_currentAnimationScheduleId}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Orchestration] Error starting animation: {ex.Message}");
            throw;
        }
    }

    [RelayCommand]
    private async Task StopCrossScreen()
    {
        if (!IsCrossScreenRunning)
        {
            Debug.WriteLine("[CrossScreen] Not running");
            return;
        }

        try
        {
            Debug.WriteLine("[CrossScreen] Stopping cross-screen animation...");

            // Stop orchestrator animation if active
            if (!string.IsNullOrEmpty(_currentAnimationScheduleId) && _service.IsServerMode)
            {
                Debug.WriteLine($"[Orchestration] Stopping animation schedule: {_currentAnimationScheduleId}");
                _service.StopAnimation(_currentAnimationScheduleId);
                _currentAnimationScheduleId = null;
            }

            // Stop coordinator animation if active
            if (_crossScreenCoordinator != null)
            {
                await _crossScreenCoordinator.StopAsync();
            }

            IsCrossScreenRunning = false;
            Debug.WriteLine("[CrossScreen] Animation stopped");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CrossScreen] Error stopping: {ex.Message}");
        }
    }

    private void OnCrossScreenStatusChanged(object? sender, UI.Services.CrossScreenStatusEventArgs e)
    {
        Debug.WriteLine($"[CrossScreen] Status changed: {e.Status} - {e.Message}");

        Dispatcher.UIThread.Post(() =>
        {
            IsCrossScreenRunning = e.Status == UI.Services.CrossScreenStatus.Running;
        });
    }

    /// <summary>
    /// Handle frame rendering for local wallpaper display (cross-screen mode).
    /// Confirms that composed frames are being received and processed correctly.
    /// </summary>
    private async Task OnLocalFrameRendered(Dictionary<string, System.Drawing.Bitmap> frames, long timestamp)
    {
        try
        {
            if (frames == null || frames.Count == 0)
            {
                return;
            }

            var logger = _loggerFactory.CreateLogger<MainWindowViewModel>();

            // Log frame rendering for debugging - confirms event subscription is working!
            if (timestamp % 3000 == 0)  // Every ~3 seconds (less spam)
            {
                logger.LogInformation("[LocalFrame] ✓ Event subscription working! Received {FrameCount} frames at {Timestamp}ms",
                    frames.Count, timestamp);
            }

            // Dispose frames properly
            foreach (var (clientId, frameBitmap) in frames)
            {
                frameBitmap?.Dispose();
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<MainWindowViewModel>()
                .LogError(ex, "[LocalFrame] Error in OnLocalFrameRendered");
        }
    }

    /// <summary>
    /// Handle animation rendering for distributed animation system (Phase 1-3).
    /// This is called when a client animation needs to be rendered.
    /// In distributed mode, this would compose and display frames locally.
    /// </summary>
    private Task OnDistributedAnimationRender(Models.Animation.AnimationMetadata metadata)
    {
        try
        {
            var logger = _loggerFactory.CreateLogger<MainWindowViewModel>();
            logger.LogInformation(
                "[DistributedAnimation] Starting frame composition: Animation={AnimationId}, Duration={DurationMs}ms, Monitor={MonitorIndex}",
                metadata.AnimationId, metadata.DurationMs, metadata.TargetMonitorIndex);

            // In a full implementation, this would:
            // 1. Instantiate CompositionRenderer (currently server-side only)
            // 2. Compose frames at 30 FPS based on metadata
            // 3. Display frames using existing wallpaper renderer
            // 4. Detect drift and correct with timing sync messages
            //
            // For now, this logs that the animation would render.
            // The actual frame composition is handled by CrossScreenWallpaperCoordinator for centralized mode.
            // Phase 4 would move CompositionRenderer to client-side.

            logger.LogInformation(
                "[DistributedAnimation] Animation render handler invoked. Actual rendering would start here.");

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<MainWindowViewModel>()
                .LogError(ex, "[DistributedAnimation] Error in OnDistributedAnimationRender");
            throw;
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Compute SHA256 hash for thumbnail cache key (matches VideoThumbnailGenerator)
    /// </summary>
    private static string ComputeThumbnailHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    #endregion
}

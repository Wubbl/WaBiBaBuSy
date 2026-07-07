using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Models;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Native;
using WaBiBaBuSy.WallpaperEngine.Renderers;
using WaBiBaBuSy.WallpaperEngine.Services;
using WaBiBaBuSy.Core.Services.Desktop;
using WaBiBaBuSy.UI.Services;

namespace WaBiBaBuSy.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly WaBiBaBuSyService _service;
    private readonly System.Timers.Timer _refreshTimer;
    private readonly DesktopWindowManager _desktopManager;
    private readonly VideoThumbnailGenerator _thumbnailGenerator;
    // Multi-monitor support: ConcurrentDictionary<monitorIndex, renderer> (thread-safe for gRPC callbacks)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IWallpaperRenderer> _localWallpaperRenderers = new();
    // D2D composition services: ConcurrentDictionary<monitorIndex, service> (for Direct2D rendering with separate player process)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, D2DCompositionService> _d2dCompositionServices = new();
    // Thumbnail capture services per monitor for live wallpaper preview in topology
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ThumbnailCaptureService> _thumbnailCaptureServices = new();
    // D2D services for remote-triggered rendering when in client mode
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, D2DCompositionService> _remoteD2DServices = new();
    // Fullscreen detection service for pausing wallpaper when a fullscreen app is active
    private FullscreenDetectionService? _fullscreenDetection;
    private bool _wallpaperPausedByFullscreen;
    // Content cache manager for LRU eviction
    private ContentCacheManager? _cacheManager;
    // Debug flag: Enable/disable network topology debug output
    private static bool _enableNetworkTopologyDebugOutput = false;
    // Guard flag: prevents RefreshTopology from overwriting order during reorder operations
    private volatile bool _isReorderingInProgress;

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
    private int _serverPort;

    [ObservableProperty]
    private int _connectedClientCount;

    [ObservableProperty]
    private bool _isCrossScreenMode;

    [ObservableProperty]
    private bool _isCrossScreenRunning;

    [ObservableProperty]
    private bool _hasAnimationConfig;

    [ObservableProperty]
    private string _d2dBackgroundColor = "#000000";

    [ObservableProperty]
    private bool _isAutoDetectBackground = true;

    [ObservableProperty]
    private int _selectedFitModeIndex = 0; // 0=Stretch, 1=Center, 2=Fit, 3=Fill

    [ObservableProperty]
    private string _remoteClientLogs = string.Empty;

    [ObservableProperty]
    private bool _isClientLogsVisible;

    [ObservableProperty]
    private bool _isClientConnected;

    [ObservableProperty]
    private string _clientConnectionStatus = "Disconnected";

    [ObservableProperty]
    private string _connectServerAddress = "";

    [ObservableProperty]
    private int _connectServerPort = 50051;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    [ObservableProperty]
    private int _updateProgressPercent;

    [ObservableProperty]
    private bool _isUpdateInProgress;

    // Debug overlay per-flag controls (defaults match player defaults)
    [ObservableProperty]
    private bool _debugOverlayEnabled;

    [ObservableProperty]
    private bool _debugShowPath = true;

    [ObservableProperty]
    private bool _debugShowIconRects = true;

    [ObservableProperty]
    private bool _debugShowZoneBands;

    [ObservableProperty]
    private bool _debugShowInfoPanel = true;

    private WaBiBaBuSy.Models.Update.UpdateInfo? _pendingUpdateInfo;
    private string? _downloadedUpdatePath;

    private CrossScreenConfig? _crossScreenConfig;
    private string? _currentAnimationScheduleId;  // Track active animation schedule (Phase 3)
    private string? _crossScreenContentId;         // ContentId sent to remote clients at start (used for stop)

    // Sequential IconZone lap-recompute state
    private int _lastGlobalLap = -1;
    private readonly object _seqLapLock = new();
    private int _seqCellW, _seqCellH, _seqVirtualCanvasWidth, _seqVirtualH;
    private int _seqPathPaddingPx, _seqVisualPaddingPx;
    private List<string> _seqPaletteHexes = new();
    private string _seqCorridorColorHex = "#1E1E1E";

    // Storage provider for file picker dialogs
    private IStorageProvider? _storageProvider;

    // Window reference for dialogs
    private Window? _mainWindow;

    public MainWindowViewModel(WaBiBaBuSyService service)
    {
        _service = service;

        // Kill any player processes left behind by a prior crashed/killed UI session so they
        // can't keep rendering stale content over a fresh start.
        int killed = KillOrphanPlayerProcesses();
        if (killed > 0)
            Debug.WriteLine($"[Startup] Killed {killed} orphan player process(es) from a prior session");

        // Initialize desktop manager for local wallpaper rendering
        _desktopManager = new DesktopWindowManager(AppLogger.CreateLogger<DesktopWindowManager>());
        _thumbnailGenerator = new VideoThumbnailGenerator(AppLogger.CreateLogger<VideoThumbnailGenerator>());

        // Load client config to pre-fill connect fields
        var clientConfig = ConfigurationManager.LoadClientConfiguration();
        ConnectServerAddress = clientConfig.ServerAddress;
        ConnectServerPort = clientConfig.ServerPort;

        // Subscribe to service events
        _service.ServerStatusChanged += OnServerStatusChanged;
        _service.ClientConnectionStatusChanged += OnClientConnectionStatusChanged;
        _service.ClientLogsReceived += OnClientLogsReceived;
        _service.UpdateAvailable += OnUpdateAvailableFromServer;
        _service.UpdateStatusChanged += OnUpdateStatusChanged;

        // Setup refresh timer for topology updates (but don't start it yet - window will start it)
        _refreshTimer = new System.Timers.Timer(2000); // Refresh every 2 seconds
        _refreshTimer.Elapsed += OnRefreshTimerElapsed;
        // Note: Timer is started by the window's Opened event to avoid background updates

        // Initialize content cache manager
        var clientCfg = ConfigurationManager.LoadClientConfiguration();
        _cacheManager = new ContentCacheManager(
            AppLogger.CreateLogger<ContentCacheManager>(),
            clientCfg.CacheDirectory,
            clientCfg.MaxCacheSizeMB);

        // Create fullscreen detection service (started/stopped with wallpaper lifecycle)
        if (clientCfg.PauseOnFullscreen)
        {
            _fullscreenDetection = new FullscreenDetectionService(
                AppLogger.CreateLogger<FullscreenDetectionService>());
            _fullscreenDetection.FullscreenStateChanged += OnFullscreenStateChanged;
            Debug.WriteLine("[Fullscreen] Detection ready (will start when wallpaper is active)");
        }

        // Load wallpaper gallery from disk
        LoadWallpaperGallery();
    }

    /// <summary>
    /// Start fullscreen detection when a wallpaper becomes active.
    /// </summary>
    private void StartFullscreenDetectionIfNeeded()
    {
        if (_fullscreenDetection != null && !_fullscreenDetection.IsRunning)
        {
            _fullscreenDetection.Start();
        }
    }

    /// <summary>
    /// Stop fullscreen detection when no wallpapers are active.
    /// </summary>
    private void StopFullscreenDetectionIfIdle()
    {
        if (_fullscreenDetection == null) return;

        bool hasActiveWallpaper = _d2dCompositionServices.Any()
            || _remoteD2DServices.Any()
            || _localWallpaperRenderers.Any()
            || IsCrossScreenRunning;

        if (!hasActiveWallpaper)
        {
            _fullscreenDetection.Stop();
            _wallpaperPausedByFullscreen = false;
            Debug.WriteLine("[Fullscreen] Detection stopped (no active wallpapers)");
        }
    }

    private void OnFullscreenStateChanged(object? sender, FullscreenStateChangedEventArgs e)
    {
        if (e.IsFullscreen)
        {
            // Hide all D2D player windows
            foreach (var kvp in _d2dCompositionServices)
                kvp.Value.SetPlayersVisible(false);
            foreach (var kvp in _remoteD2DServices)
                kvp.Value.SetPlayersVisible(false);

            // Pause LibVLC renderers
            foreach (var kvp in _localWallpaperRenderers)
            {
                try { kvp.Value.PauseAsync().GetAwaiter().GetResult(); }
                catch { /* best effort */ }
            }

            _wallpaperPausedByFullscreen = true;
            Debug.WriteLine($"[Fullscreen] Wallpaper hidden — fullscreen app: {e.ProcessName}");
        }
        else if (_wallpaperPausedByFullscreen)
        {
            // Show all D2D player windows
            foreach (var kvp in _d2dCompositionServices)
                kvp.Value.SetPlayersVisible(true);
            foreach (var kvp in _remoteD2DServices)
                kvp.Value.SetPlayersVisible(true);

            // Resume LibVLC renderers
            foreach (var kvp in _localWallpaperRenderers)
            {
                try { kvp.Value.StartAsync().GetAwaiter().GetResult(); }
                catch { /* best effort */ }
            }

            _wallpaperPausedByFullscreen = false;
            Debug.WriteLine("[Fullscreen] Wallpaper restored — fullscreen app closed");
        }
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

                // Re-detect resolution if missing or unknown
                if (string.IsNullOrEmpty(viewModel.Resolution) || viewModel.Resolution == "Unknown")
                {
                    if (viewModel.Type == WallpaperType.Image || viewModel.Type == WallpaperType.Gif)
                    {
                        try
                        {
                            using var image = System.Drawing.Image.FromFile(item.FilePath);
                            viewModel.Resolution = $"{image.Width}x{image.Height}";
                        }
                        catch { /* ignore */ }
                    }
                    else if (viewModel.Type == WallpaperType.Video)
                    {
                        // Schedule async resolution detection for videos
                        var vm = viewModel;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var videoRes = await _thumbnailGenerator.GetVideoResolution(item.FilePath);
                                if (!string.IsNullOrEmpty(videoRes))
                                {
                                    await Dispatcher.UIThread.InvokeAsync(() => vm.Resolution = videoRes);
                                }
                            }
                            catch { /* ignore */ }
                        });
                    }
                }

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
    private void SelectAllClients()
    {
        // Select all clients
        foreach (var c in Clients)
            c.IsSelected = true;
    }

    [RelayCommand]
    private async Task MoveClientUp()
    {
        if (SelectedClient == null) return;
        var sorted = Clients.OrderBy(c => c.Order).ToList();
        var idx = sorted.IndexOf(SelectedClient);
        if (idx > 0)
        {
            // Swap order values with the previous client
            var prev = sorted[idx - 1];
            (SelectedClient.Order, prev.Order) = (prev.Order, SelectedClient.Order);
            Debug.WriteLine($"[Topology] Moved {SelectedClient.DisplayName} up: Order={SelectedClient.Order}");
            await PersistClientOrderAsync();
        }
    }

    [RelayCommand]
    private async Task MoveClientDown()
    {
        if (SelectedClient == null) return;
        var sorted = Clients.OrderBy(c => c.Order).ToList();
        var idx = sorted.IndexOf(SelectedClient);
        if (idx >= 0 && idx < sorted.Count - 1)
        {
            // Swap order values with the next client
            var next = sorted[idx + 1];
            (SelectedClient.Order, next.Order) = (next.Order, SelectedClient.Order);
            Debug.WriteLine($"[Topology] Moved {SelectedClient.DisplayName} down: Order={SelectedClient.Order}");
            await PersistClientOrderAsync();
        }
    }

    /// <summary>
    /// Persist the current client order to the server (or locally in server mode).
    /// Sets a guard flag to prevent RefreshTopology from overwriting during the operation.
    /// </summary>
    public async Task PersistClientOrderAsync()
    {
        _isReorderingInProgress = true;
        try
        {
            var orders = Clients.ToDictionary(c => c.ClientId, c => c.Order);
            await _service.UpdateClientOrderAsync(orders);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Topology] Error persisting client order: {ex.Message}");
        }
        finally
        {
            _isReorderingInProgress = false;
        }
    }

    /// <summary>
    /// True when any renderer (D2D, LibVLC, or cross-screen) is active locally.
    /// Used to control "Clear Wallpaper" button visibility.
    /// </summary>
    public bool HasActiveRenderer =>
        _d2dCompositionServices.Any() || _localWallpaperRenderers.Any() || IsCrossScreenRunning;

    /// <summary>
    /// True when no wallpaper is selected but a cross-screen animation is running
    /// </summary>
    public bool ShowAnimationInfo => SelectedWallpaper == null && IsCrossScreenRunning && _crossScreenConfig != null;

    public string ActiveAnimationFileName => _crossScreenConfig?.Animation.AnimationPath != null
        ? Path.GetFileName(_crossScreenConfig.Animation.AnimationPath) : string.Empty;

    /// <summary>
    /// Warning text when this machine has 2+ monitors at different refresh rates.
    /// On Win11 24H2+, layered+WorkerW-parented wallpaper windows are forced through DWM
    /// composition, which paces to the primary monitor's refresh rate — secondary panels
    /// at lower refresh rates show tearing on horizontal-scrolling animations. Null when
    /// only one monitor exists or all monitors share the same rate. See OpenIssues.md.
    /// </summary>
    public string? LocalRefreshRateMismatchWarning
    {
        get
        {
            try
            {
                var monitors = NativeMonitorInfo.GetAllMonitors();
                var rates = monitors
                    .Select(m => m.RefreshRateHz)
                    .Where(hz => hz > 0)
                    .Distinct()
                    .OrderByDescending(hz => hz)
                    .ToArray();
                if (rates.Length < 2) return null;
                return $"Mixed refresh rates on local monitors ({string.Join(" + ", rates.Select(r => $"{r} Hz"))}). The slower panel may show tearing — match rates in Windows Display settings to fix.";
            }
            catch
            {
                return null;
            }
        }
    }

    public bool HasLocalRefreshRateMismatch => LocalRefreshRateMismatchWarning != null;

    public string ActiveDistributionMode => _crossScreenConfig?.DistributionMode switch
    {
        WaBiBaBuSy.Models.Wallpaper.AnimationDistributionMode.Sequential => "Sequential",
        WaBiBaBuSy.Models.Wallpaper.AnimationDistributionMode.Simultaneous => "Simultaneous",
        _ => "Unknown"
    };

    public int ActiveAnimationSpeed => _crossScreenConfig?.AnimationSpeedPxPerSecond ?? 0;

    public string ActiveBackgroundColor => _crossScreenConfig?.Background.ColorHex ?? "#000000";

    partial void OnSelectedWallpaperChanged(WallpaperItemViewModel? value)
    {
        OnPropertyChanged(nameof(ShowAnimationInfo));
        ApplyWallpaperToSelectedCommand.NotifyCanExecuteChanged();
        ApplyWallpaperViaDirect2DCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCrossScreenRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAnimationInfo));
        OnPropertyChanged(nameof(ActiveAnimationFileName));
        OnPropertyChanged(nameof(ActiveDistributionMode));
        OnPropertyChanged(nameof(ActiveAnimationSpeed));
        OnPropertyChanged(nameof(ActiveBackgroundColor));
        OnPropertyChanged(nameof(HasActiveRenderer));
        StartCrossScreenCommand.NotifyCanExecuteChanged();
        ClearAllWallpapersCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasAnimationConfigChanged(bool value)
    {
        StartCrossScreenCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Notify CanExecute for commands that depend on client selection.
    /// Called when a client's IsSelected property changes.
    /// </summary>
    private void NotifyClientSelectionCommands()
    {
        ApplyWallpaperToSelectedCommand.NotifyCanExecuteChanged();
        ApplyWallpaperViaDirect2DCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Check if a client ID represents a local monitor (either local-only or server mode)
    /// </summary>
    private static bool IsLocalMonitor(string clientId) =>
        clientId.StartsWith("LOCAL_MACHINE_MONITOR_") || clientId.StartsWith("SERVER_LOCALHOST_MONITOR_");

    /// <summary>
    /// Extract monitor index from a local monitor client ID
    /// </summary>
    private static int GetMonitorIndex(string clientId) =>
        int.Parse(clientId.Replace("LOCAL_MACHINE_MONITOR_", "").Replace("SERVER_LOCALHOST_MONITOR_", ""));

    /// <summary>
    /// Computes the effective rendered animation height in pixels for a given FitMode,
    /// matching the sizing logic in Player.D2D's CalculateAnimationLayout. Used to size
    /// the server-side A* path padding to the actual animation bitmap.
    /// </summary>
    private static int ComputeEffectiveAnimationHeight(AnimationLayerConfig anim, int screenWidth, int screenHeight)
    {
        try
        {
            int nativeW = 0, nativeH = 0;
            if (!string.IsNullOrEmpty(anim.AnimationPath) && File.Exists(anim.AnimationPath))
            {
                using var img = System.Drawing.Image.FromFile(anim.AnimationPath);
                nativeW = img.Width;
                nativeH = img.Height;
            }

            if (nativeW <= 0 || nativeH <= 0)
                return anim.TargetHeight;

            return anim.FitMode switch
            {
                ContentFitMode.Center  => nativeH,
                ContentFitMode.Fit     => (int)(nativeH * Math.Min((double)screenWidth / nativeW, (double)screenHeight / nativeH)),
                ContentFitMode.Fill    => (int)(nativeH * Math.Max((double)screenWidth / nativeW, (double)screenHeight / nativeH)),
                ContentFitMode.Stretch => screenHeight,
                _                      => anim.TargetHeight
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CrossScreen] Native-size probe failed for '{anim.AnimationPath}': {ex.Message}. Falling back to TargetHeight.");
            return anim.TargetHeight;
        }
    }

    private bool CanApplyWallpaperToSelected() =>
        SelectedWallpaper != null && Clients.Any(c => c.IsSelected);

    private bool CanApplyWallpaperViaDirect2D() =>
        SelectedWallpaper != null && Clients.Any(c => c.IsSelected);

    private bool CanStartCrossScreen() =>
        HasAnimationConfig && !IsCrossScreenRunning;

    /// <summary>
    /// Force-kills any WaBiBaBuSy.Player.D2D.exe processes that are still running but no longer
    /// tracked by this UI. Orphan players can arise after crashes, debug sessions, or when the
    /// UI state is reset while player processes keep rendering stale content.
    /// </summary>
    private static int KillOrphanPlayerProcesses()
    {
        int killed = 0;
        try
        {
            var current = Process.GetCurrentProcess().Id;
            foreach (var proc in Process.GetProcessesByName("WaBiBaBuSy.Player.D2D"))
            {
                try
                {
                    if (proc.Id == current) { proc.Dispose(); continue; }
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                    killed++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[KillOrphans] Failed to kill PID {proc.Id}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KillOrphans] Enumeration failed: {ex.Message}");
        }
        return killed;
    }

    /// <summary>
    /// Toggles the debug overlay on/off via the ViewModel so the UI checkbox stays in sync.
    /// Used by the F11 keyboard binding (main window) as a reliable alternative to the player's
    /// own F11 hotkey, which may be captured by another process first.
    /// </summary>
    [RelayCommand]
    private void ToggleDebugOverlay() => DebugOverlayEnabled = !DebugOverlayEnabled;

    partial void OnDebugOverlayEnabledChanged(bool value) => _ = SendDebugOverlayFlags();
    partial void OnDebugShowPathChanged(bool value) => _ = SendDebugOverlayFlags();
    partial void OnDebugShowIconRectsChanged(bool value) => _ = SendDebugOverlayFlags();
    partial void OnDebugShowZoneBandsChanged(bool value) => _ = SendDebugOverlayFlags();
    partial void OnDebugShowInfoPanelChanged(bool value) => _ = SendDebugOverlayFlags();

    private async Task SendDebugOverlayFlags()
    {
        foreach (var service in _d2dCompositionServices.Values)
        {
            try
            {
                await service.SendSetDebugOverlayFlagsAsync(
                    DebugOverlayEnabled, DebugShowPath, DebugShowIconRects,
                    DebugShowZoneBands, DebugShowInfoPanel);
            }
            catch (Exception ex) { Debug.WriteLine($"[DebugOverlay] SendFlags failed: {ex.Message}"); }
        }
    }

    [RelayCommand]
    private async Task ClearAllWallpapers()
    {
        Debug.WriteLine("[ClearAll] Stopping animations and clearing wallpapers");

        // Stop cross-screen animation if running
        if (IsCrossScreenRunning)
        {
            await StopCrossScreen();
        }

        // Stop and dispose all D2D composition services
        // Must await StopAsync before Dispose to avoid sync-over-async deadlock
        foreach (var kvp in _d2dCompositionServices)
        {
            try
            {
                await kvp.Value.StopAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClearAll] Error stopping D2D service for monitor {kvp.Key}: {ex.Message}");
            }

            try
            {
                kvp.Value.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClearAll] Error disposing D2D service for monitor {kvp.Key}: {ex.Message}");
            }
        }
        _d2dCompositionServices.Clear();

        // Dispose all LibVLC renderers
        foreach (var kvp in _localWallpaperRenderers)
        {
            try
            {
                if (kvp.Value is IDisposable disposable)
                    disposable.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClearAll] Error disposing renderer for monitor {kvp.Key}: {ex.Message}");
            }
        }
        _localWallpaperRenderers.Clear();

        // Reset animation indicators and preview on clients
        foreach (var client in Clients)
        {
            client.IsAnimating = false;
            client.IsCurrentAnimationTarget = false;
            client.ActiveAnimationName = null;
            client.ThumbnailImage = null;
            client.CurrentWallpaper = null;
        }

        // Stop fullscreen detection since no wallpapers are active
        StopFullscreenDetectionIfIdle();

        // Finally, kill any lingering Player.D2D processes that this UI has lost track of
        // (common after crashes, debug sessions, or stale state).
        int killed = KillOrphanPlayerProcesses();
        if (killed > 0)
            Debug.WriteLine($"[ClearAll] Killed {killed} orphan player process(es)");

        // Force IsCrossScreenRunning back to a sane state even if StopCrossScreen couldn't.
        IsCrossScreenRunning = false;
        OnPropertyChanged(nameof(HasActiveRenderer));

        Debug.WriteLine("[ClearAll] All wallpapers cleared");
    }

    [RelayCommand]
    private void SelectWallpaper(WallpaperItemViewModel wallpaper)
    {
        // Clear previous selection highlighting
        foreach (var w in Wallpapers)
            w.IsSelected = false;

        wallpaper.IsSelected = true;
        SelectedWallpaper = wallpaper;

        // Auto-detect background color if enabled
        if (IsAutoDetectBackground)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var color = await BackgroundColorDetector.DetectDominantEdgeColorAsync(wallpaper.FilePath);
                    await Dispatcher.UIThread.InvokeAsync(() => D2dBackgroundColor = color);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AutoDetect] Error detecting background color: {ex.Message}");
                }
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyWallpaperToSelected))]
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

                // Check if this is a local monitor (local-only mode or server's own monitors)
                if (IsLocalMonitor(client.ClientId))
                {
                    await ApplyWallpaperLocallyInternal(SelectedWallpaper, GetMonitorIndex(client.ClientId));
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
    /// Apply wallpaper using the Direct2D composition pipeline.
    /// Local monitors: direct D2DCompositionService.
    /// Remote clients: sends D2D LOAD command via gRPC, client renders locally with its own Player.D2D.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyWallpaperViaDirect2D))]
    private async Task ApplyWallpaperViaDirect2D()
    {
        if (SelectedWallpaper == null)
        {
            Debug.WriteLine("[Direct2D] No wallpaper selected");
            return;
        }

        try
        {
            var selectedClients = Clients.Where(c => c.IsSelected).ToList();
            if (selectedClients.Count == 0)
            {
                Debug.WriteLine("[Direct2D] No clients selected");
                return;
            }

            Debug.WriteLine($"[Direct2D] Applying '{SelectedWallpaper.Name}' via D2D to {selectedClients.Count} node(s)");

            foreach (var client in selectedClients)
            {
                if (IsLocalMonitor(client.ClientId))
                {
                    // LOCAL: Direct D2D rendering on this machine
                    var monitorIndex = GetMonitorIndex(client.ClientId);
                    Debug.WriteLine($"[Direct2D] Local monitor {monitorIndex}");
                    await ApplyWallpaperWithDirect2DAsync(SelectedWallpaper, monitorIndex);
                }
                else if (_service.IsServerRunning && _service.SyncCoordinator != null)
                {
                    // REMOTE: Send D2D LOAD command — client renders with its own Player.D2D
                    Debug.WriteLine($"[Direct2D] Remote client {client.Hostname}");
                    await _service.SyncCoordinator.LoadWallpaperD2DOnClientAsync(
                        client.ClientId,
                        SelectedWallpaper.WallpaperId,
                        SelectedWallpaper.FilePath,
                        D2dBackgroundColor,
                        SelectedFitModeIndex);
                }

                client.CurrentWallpaper = $"{SelectedWallpaper.Name} (D2D)";
            }

            Debug.WriteLine($"[Direct2D] Successfully applied to {selectedClients.Count} node(s)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Direct2D] Error: {ex.Message}");
            Debug.WriteLine($"[Direct2D] Stack trace: {ex.StackTrace}");
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
            var isLocal = IsLocalMonitor(targetClientId);
            var monitorIndex = isLocal ? GetMonitorIndex(targetClientId) : 0;

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
                // GIFs now use LibVLC (VideoWallpaperRenderer) for instant loading
                ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".flv" or ".gif"
                    => new VideoWallpaperRenderer(
                        AppLogger.CreateLogger<VideoWallpaperRenderer>(),
                        _desktopManager),
                ".jpg" or ".jpeg" or ".png" or ".bmp"
                    => new ImageWallpaperRendererLibVLC(
                        AppLogger.CreateLogger<ImageWallpaperRendererLibVLC>(),
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
            ClearAllWallpapersCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasActiveRenderer));

            // Set up thumbnail capture for live preview
            SetupThumbnailCapture(monitorIndex, renderer.WindowHandle, wallpaper.Name);

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
    /// INTERNAL: Apply Direct2D rendering using separate player process (Windows 11 24H2+ compatible).
    /// This uses D2DCompositionService + D2DPlayerHost instead of the old Direct2DRenderer.
    /// </summary>
    private async Task ApplyWallpaperWithDirect2DAsync(WallpaperItemViewModel wallpaper, int monitorIndex = 0)
    {
        try
        {
            Debug.WriteLine($"[Direct2D] Applying '{wallpaper.Name}' to monitor {monitorIndex} via D2D separate process");

            // Dispose previous composition service for this monitor if exists
            if (_d2dCompositionServices.TryRemove(monitorIndex, out var existingService))
            {
                Debug.WriteLine($"[Direct2D] Disposing existing D2D composition service for monitor {monitorIndex}");
                try
                {
                    existingService.Dispose();
                    Debug.WriteLine($"[Direct2D] Successfully disposed existing service for monitor {monitorIndex}");

                    // Force garbage collection to ensure processes are fully terminated
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    // Give the system a moment to clean up
                    await Task.Delay(500);
                }
                catch (Exception disposeEx)
                {
                    Debug.WriteLine($"[Direct2D] Error disposing existing service: {disposeEx.Message}");
                }
            }

            // Validate monitor index using native API (avoids WinForms Screen.AllScreens issue on .NET 9)
            var screens = WaBiBaBuSy.WallpaperEngine.Native.NativeMonitorInfo.GetAllMonitors();
            if (monitorIndex < 0 || monitorIndex >= screens.Length)
            {
                Debug.WriteLine($"[Direct2D] Invalid monitor index {monitorIndex}. Available monitors: {screens.Length}");
                return;
            }

            // Determine file type
            var extension = Path.GetExtension(wallpaper.FilePath).ToLowerInvariant();

            // OPTION 1 (DEFAULT): Use composition pipeline with LibVLC memory callbacks
            // GIFs/videos use memory callbacks for direct frame access (zero disk I/O, <1% CPU)
            // This enables GIFs in cross-screen animations and composition system

            // OPTION 2 (DISABLED): Direct LibVLC rendering (bypasses composition)
            // Uncomment this block to use direct LibVLC window rendering for GIFs
            // Pros: Simplest approach, guaranteed to work
            // Cons: Can't use GIFs in composition/cross-screen animations
            /*
            if (extension == ".gif")
            {
                Debug.WriteLine($"[Direct2D] GIF detected - using native LibVLC rendering (bypassing composition)");
                await ApplyGifWithLibVLCAsync(wallpaper, monitorIndex);
                return;
            }
            */

            // Check if file is supported for composition pipeline (images, videos, AND GIFs)
            bool isSupportedFile = extension switch
            {
                ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".flv" or ".gif"
                or ".jpg" or ".jpeg" or ".png" or ".bmp" => true,
                _ => false
            };

            if (!isSupportedFile)
            {
                Debug.WriteLine($"[Direct2D] File '{wallpaper.FilePath}' is not supported. Extension: {extension}");
                return;
            }

            // Get screen bounds for this monitor
            var screen = screens[monitorIndex];

            Debug.WriteLine($"[Direct2D] Selected monitor {monitorIndex}: Position=({screen.Bounds.X},{screen.Bounds.Y}), Size={screen.Bounds.Width}x{screen.Bounds.Height}");
            Debug.WriteLine($"[Direct2D] IsPrimary={screen.IsPrimary}, DeviceName={screen.DeviceName}");

            // Create screen configuration for the virtual canvas manager
            var screenConfig = new WaBiBaBuSy.WallpaperEngine.Composition.ScreenConfiguration
            {
                ClientId = $"LOCAL_MACHINE_MONITOR_{monitorIndex}",
                Order = monitorIndex, // Use actual monitor index, not always 0
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height,
                PhysicalDistanceCm = 0
            };

            // Create canvas manager for single screen
            var canvasManager = new VirtualCanvasManager(
                AppLogger.CreateLogger<VirtualCanvasManager>());
            canvasManager.CalculateLayout(new WaBiBaBuSy.WallpaperEngine.Composition.ScreenConfiguration[] { screenConfig });

            // Create background configuration (use auto-detected or user-specified color)
            var backgroundConfig = new BackgroundLayerConfig
            {
                Mode = BackgroundMode.SolidColor,
                ColorHex = D2dBackgroundColor
            };

            // Create animation configuration
            // SIMPLE PLAYBACK MODE: Center the content and keep it stationary
            var animationConfig = new AnimationLayerConfig
            {
                AnimationPath = wallpaper.FilePath,
                TargetHeight = screen.Bounds.Height,
                Loop = true,
                VerticalAlign = VerticalAlignment.Center,
                CenterInitialPosition = true,  // Start centered, not off-screen
                FitMode = (ContentFitMode)SelectedFitModeIndex
            };

            // Create composition renderer
            // Create D2D composition service (metadata-based, no CompositionRenderer needed in main process)
            var d2dService = new D2DCompositionService(
                AppLogger.CreateLogger<D2DCompositionService>(),
                AppLogger.Factory,
                _desktopManager);

            Debug.WriteLine($"[Direct2D] Initializing D2D composition service for monitor {monitorIndex} (metadata-based)");

            // Pass actual monitor bounds from Windows
            var actualBounds = new System.Drawing.Rectangle(
                screen.Bounds.X,
                screen.Bounds.Y,
                screen.Bounds.Width,
                screen.Bounds.Height);

            Debug.WriteLine($"[Direct2D] Passing actualBounds to service: X={actualBounds.X}, Y={actualBounds.Y}, W={actualBounds.Width}, H={actualBounds.Height}");

            // Initialize: sends LOAD_ANIMATION command with metadata to player
            await d2dService.InitializeAsync(canvasManager, backgroundConfig, animationConfig, actualBounds, monitorIndex);

            // Small delay to ensure player has loaded animation
            await Task.Delay(100);

            // Start playback (works for both static images and animations)
            // SIMPLE PLAYBACK MODE: pixelsPerSecond = 0 means STATIONARY (centered, no movement)
            // Animation mode would use pixelsPerSecond > 0 for cross-screen movement
            Debug.WriteLine($"[Direct2D] Starting animation playback (metadata-based, players render locally)");
            await d2dService.StartAsync(startTimestampMs: 0, pixelsPerSecond: 0);

            // Store the service for later cleanup
            _d2dCompositionServices[monitorIndex] = d2dService;
            ClearAllWallpapersCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasActiveRenderer));
            StartFullscreenDetectionIfNeeded();

            // Set up thumbnail capture for live preview
            SetupThumbnailCapture(monitorIndex, d2dService.PlayerHwnd, wallpaper.Name);

            // Update UI
            var localClient = Clients.FirstOrDefault(c => c.ClientId == $"LOCAL_MACHINE_MONITOR_{monitorIndex}");
            if (localClient != null)
            {
                localClient.CurrentWallpaper = $"{wallpaper.Name} (D2D)";
            }

            Debug.WriteLine($"[Direct2D] Successfully started D2D rendering for '{wallpaper.Name}' on monitor {monitorIndex}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Direct2D] Error: {ex.Message}");
            Debug.WriteLine($"[Direct2D] Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Apply GIF using native LibVLC rendering (bypasses composition pipeline for performance).
    /// LibVLC renders directly to a desktop window - no frame extraction needed.
    /// This eliminates the TakeSnapshot() overhead and memory leaks.
    /// </summary>
    private async Task ApplyGifWithLibVLCAsync(WallpaperItemViewModel wallpaper, int monitorIndex = 0)
    {
        try
        {
            Debug.WriteLine($"[LibVLC-GIF] Applying GIF '{wallpaper.Name}' to monitor {monitorIndex} via native LibVLC");

            // Dispose previous renderer for this monitor if exists
            if (_localWallpaperRenderers.TryRemove(monitorIndex, out var existingRenderer))
            {
                Debug.WriteLine($"[LibVLC-GIF] Disposing existing renderer for monitor {monitorIndex}");
                try
                {
                    await existingRenderer.StopAsync();
                    existingRenderer.Dispose();
                    await Task.Delay(200);  // Give it time to clean up
                }
                catch (Exception disposeEx)
                {
                    Debug.WriteLine($"[LibVLC-GIF] Error disposing existing renderer: {disposeEx.Message}");
                }
            }

            // Create VideoWallpaperRenderer in NON-headless mode
            // This creates a real visible window on the desktop that LibVLC renders to
            var renderer = new VideoWallpaperRenderer(
                AppLogger.CreateLogger<VideoWallpaperRenderer>(),
                _desktopManager);

            var config = new WallpaperConfig
            {
                FilePath = wallpaper.FilePath,
                Type = WaBiBaBuSy.Models.WallpaperType.Video,  // LibVLC treats GIFs as videos
                Loop = true,  // Enable looping
                HardwareAcceleration = true,
                MonitorIndex = monitorIndex,
                HeadlessMode = false  // CRITICAL: Create visible window for LibVLC rendering
            };

            Debug.WriteLine($"[LibVLC-GIF] Initializing renderer for monitor {monitorIndex}");
            await renderer.InitializeAsync(config);

            Debug.WriteLine($"[LibVLC-GIF] Starting playback");
            await renderer.StartAsync();

            // Store renderer
            _localWallpaperRenderers[monitorIndex] = renderer;
            ClearAllWallpapersCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasActiveRenderer));

            // Set up thumbnail capture for live preview
            SetupThumbnailCapture(monitorIndex, renderer.WindowHandle, wallpaper.Name);

            // Update UI
            var localClient = Clients.FirstOrDefault(c => c.ClientId == $"LOCAL_MACHINE_MONITOR_{monitorIndex}");
            if (localClient != null)
            {
                localClient.CurrentWallpaper = $"{wallpaper.Name} (LibVLC)";
            }

            Debug.WriteLine($"[LibVLC-GIF] Successfully applied GIF to monitor {monitorIndex}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LibVLC-GIF] Error: {ex.Message}");
            Debug.WriteLine($"[LibVLC-GIF] Stack trace: {ex.StackTrace}");
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
    private async Task FetchClientLogs()
    {
        // Find the first selected remote client
        var selectedRemote = Clients.FirstOrDefault(c => c.IsSelected
            && !c.ClientId.StartsWith("SERVER_LOCALHOST_MONITOR_")
            && !c.ClientId.StartsWith("LOCAL_MACHINE_MONITOR_"));

        if (selectedRemote == null)
        {
            Debug.WriteLine("[FetchClientLogs] No remote client selected");
            RemoteClientLogs = "No remote client selected. Select a remote client node first.";
            IsClientLogsVisible = true;
            return;
        }

        try
        {
            RemoteClientLogs = $"Requesting logs from {selectedRemote.Hostname}...";
            IsClientLogsVisible = true;

            await _service.RequestClientLogsAsync(selectedRemote.ClientId);
        }
        catch (Exception ex)
        {
            RemoteClientLogs = $"Error requesting logs: {ex.Message}";
            Debug.WriteLine($"[FetchClientLogs] Error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CloseClientLogs()
    {
        IsClientLogsVisible = false;
        RemoteClientLogs = string.Empty;
    }

    [RelayCommand]
    private async Task CheckForUpdate()
    {
        try
        {
            UpdateStatusText = "Checking for updates...";
            IsUpdateInProgress = true;

            var updateInfo = await _service.CheckForUpdateAsync();
            if (updateInfo != null)
            {
                _pendingUpdateInfo = updateInfo;
                IsUpdateAvailable = true;
                UpdateStatusText = $"Update available: v{updateInfo.Version} ({updateInfo.PackageSize / 1024 / 1024} MB)" +
                    (updateInfo.IsMandatory ? " [MANDATORY]" : "");
            }
            else
            {
                UpdateStatusText = "You are running the latest version.";
                IsUpdateAvailable = false;
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Update check failed: {ex.Message}";
            Debug.WriteLine($"[CheckForUpdate] Error: {ex.Message}");
        }
        finally
        {
            IsUpdateInProgress = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndApplyUpdate()
    {
        if (_pendingUpdateInfo == null)
        {
            UpdateStatusText = "No update available to download.";
            return;
        }

        try
        {
            IsUpdateInProgress = true;
            UpdateStatusText = "Downloading update...";

            var updatePath = await _service.DownloadUpdateAsync(_pendingUpdateInfo);
            if (updatePath == null)
            {
                UpdateStatusText = "Download failed.";
                return;
            }

            _downloadedUpdatePath = updatePath;
            UpdateStatusText = $"Update v{_pendingUpdateInfo.Version} downloaded. Ready to apply.";

            // Ask user confirmation then apply
            UpdateStatusText = "Applying update... Application will restart.";
            _service.ApplyUpdate(updatePath);
            // If we get here, the apply failed (it normally calls Environment.Exit)
            UpdateStatusText = "Update apply failed. Please restart manually.";
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Update failed: {ex.Message}";
            Debug.WriteLine($"[DownloadAndApplyUpdate] Error: {ex.Message}");
        }
        finally
        {
            IsUpdateInProgress = false;
        }
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        IsUpdateAvailable = false;
        UpdateStatusText = string.Empty;
    }

    private void OnUpdateAvailableFromServer(object? sender, WaBiBaBuSy.Core.Services.Networking.UpdateAvailableEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            _pendingUpdateInfo = new WaBiBaBuSy.Models.Update.UpdateInfo
            {
                Version = e.ServerVersion,
                BuildNumber = e.ServerBuildNumber,
                PackageSize = e.PackageSize,
                ReleaseNotes = e.Description
            };
            IsUpdateAvailable = true;
            UpdateStatusText = $"Update available: v{e.ServerVersion}";
        });
    }

    private void OnUpdateStatusChanged(object? sender, WaBiBaBuSy.Models.Update.UpdateStatusInfo e)
    {
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            UpdateProgressPercent = e.ProgressPercent;
            UpdateStatusText = e.Status switch
            {
                WaBiBaBuSy.Models.Update.UpdateStatusType.Checking => "Checking for updates...",
                WaBiBaBuSy.Models.Update.UpdateStatusType.Downloading => e.ProgressPercent == 0
                    ? "Server is preparing download..."
                    : $"Downloading... {e.ProgressPercent}%",
                WaBiBaBuSy.Models.Update.UpdateStatusType.Downloaded => "Download complete. Verifying...",
                WaBiBaBuSy.Models.Update.UpdateStatusType.Verifying => "Verifying package integrity...",
                WaBiBaBuSy.Models.Update.UpdateStatusType.Applying => "Applying update...",
                WaBiBaBuSy.Models.Update.UpdateStatusType.Applied => "Update applied successfully!",
                WaBiBaBuSy.Models.Update.UpdateStatusType.Failed => $"Update failed: {e.ErrorMessage}",
                _ => UpdateStatusText
            };
        });
    }

    private void OnClientLogsReceived(object? sender, WaBiBaBuSy.Grpc.Services.ClientLogsReceivedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            RemoteClientLogs = $"=== Logs from {e.ClientId} ===\n{e.LogContent}";
            IsClientLogsVisible = true;
        });
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
    /// Add a wallpaper from a file path (used by drag-and-drop and file picker)
    /// </summary>
    public void AddWallpaperFromPath(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;

            // Check if already in gallery
            if (Wallpapers.Any(w => string.Equals(w.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
                return;

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
            // For videos, generate thumbnail and read resolution using FFmpeg in background
            else if (type == WallpaperType.Video)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Debug.WriteLine($"Starting FFmpeg thumbnail generation for: {filePath}");
                        var thumb = await _thumbnailGenerator.GenerateThumbnail(filePath);
                        var videoRes = await _thumbnailGenerator.GetVideoResolution(filePath);

                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            var wallpaperItem = Wallpapers.FirstOrDefault(w => w.FilePath == filePath);
                            if (wallpaperItem != null)
                            {
                                if (!string.IsNullOrEmpty(thumb))
                                {
                                    wallpaperItem.ThumbnailPath = thumb;
                                    wallpaperItem.LoadThumbnail();
                                    Debug.WriteLine($"Thumbnail loaded for: {fileName}");
                                }
                                else
                                {
                                    Debug.WriteLine($"Thumbnail generation failed for: {filePath}");
                                }

                                if (!string.IsNullOrEmpty(videoRes))
                                    wallpaperItem.Resolution = videoRes;
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ERROR in thumbnail/resolution task: {ex.Message}");
                    }
                });
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
            Debug.WriteLine($"Error adding wallpaper from path: {ex.Message}");
        }
    }

    /// <summary>
    /// Add a wallpaper from a storage file (delegates to AddWallpaperFromPath)
    /// </summary>
    private Task AddWallpaperFromFile(IStorageFile file)
    {
        AddWallpaperFromPath(file.Path.LocalPath);
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

    [RelayCommand]
    private async Task ConnectToServer()
    {
        if (IsClientConnected || _service.IsClientConnected)
            return;

        IsConnecting = true;
        ClientConnectionStatus = "Discovering...";

        try
        {
            var address = ConnectServerAddress.Trim();
            var port = ConnectServerPort;
            if (string.IsNullOrEmpty(address)) address = "localhost";

            var connected = await _service.DiscoverAndConnectAsync(address, port, ApplyD2DFromRemoteAsync);
            if (connected)
            {
                _service.SetD2DCrossScreenApplyDelegate(ApplyCrossScreenD2DFromRemoteAsync);
                _service.SetD2DCrossScreenStopDelegate(StopRemoteCrossScreenD2DAsync);
            }

            if (connected)
            {
                IsClientConnected = true;
                ClientConnectionStatus = $"Connected";
                RefreshTopology();
            }
            else
            {
                ClientConnectionStatus = "Connection failed";
            }
        }
        catch (Exception ex)
        {
            ClientConnectionStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectFromServer()
    {
        try
        {
            await _service.DisconnectFromServerAsync();
            IsClientConnected = false;
            ClientConnectionStatus = "Disconnected";
            RefreshTopology();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DisconnectFromServer] Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Cross-screen D2D apply delegate. Called when the server sends a CROSSSCREEN D2D command.
    /// Creates a D2D player on this client with the server-provided virtual canvas parameters
    /// so all nodes share the same coordinate space and start timestamp.
    /// </summary>
    private async Task ApplyCrossScreenD2DFromRemoteAsync(CrossScreenApplyRequest req)
    {
        var monitorIndex = req.MonitorIndex;
        Debug.WriteLine($"[D2D-CrossScreen] Received: file={req.FilePath}, monitor={monitorIndex}, canvas={req.VirtualCanvasWidth}px, offset={req.MonitorOffsetX}px, ts={req.SharedStartTimestampMs}ms, speed={req.PixelsPerSecond}px/s, perMonitor={req.PerMonitorMode}, movType={req.MovementType}");

        // Dispose previous cross-screen service for this monitor
        if (_remoteD2DServices.TryRemove(monitorIndex, out var existing))
        {
            existing.Dispose();
            await Task.Delay(300);
        }

        var monitors = NativeMonitorInfo.GetAllMonitors();
        if (monitorIndex < 0 || monitorIndex >= monitors.Length)
        {
            Debug.WriteLine($"[D2D-CrossScreen] ERROR: Invalid monitor index {monitorIndex} (have {monitors.Length} monitors)");
            return;
        }

        var screen = monitors[monitorIndex];
        var actualBounds = new System.Drawing.Rectangle(
            screen.Bounds.X, screen.Bounds.Y,
            screen.Bounds.Width, screen.Bounds.Height);

        // Build a single-screen canvas manager (used for player host init only;
        // actual virtualCanvasWidth / monitorOffsetX are overridden via explicit params)
        var screenConfig = new WaBiBaBuSy.WallpaperEngine.Composition.ScreenConfiguration
        {
            ClientId = $"REMOTE_CLIENT_MONITOR_{monitorIndex}",
            Order = monitorIndex,
            Width = screen.Bounds.Width,
            Height = screen.Bounds.Height,
            PhysicalDistanceCm = 0
        };
        var canvasManager = new VirtualCanvasManager(AppLogger.CreateLogger<VirtualCanvasManager>());
        canvasManager.CalculateLayout(new[] { screenConfig });

        // ── Background: full config when transmitted, solid-color fallback otherwise ──
        var backgroundConfig = DeserializeRemoteConfig<BackgroundLayerConfig>(req.BackgroundJson, "BackgroundLayerConfig")
            ?? new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = req.BackgroundColor };
        if ((backgroundConfig.Mode == BackgroundMode.StretchedImage || backgroundConfig.Mode == BackgroundMode.TiledImage)
            && (string.IsNullOrEmpty(backgroundConfig.ImagePath) || !File.Exists(backgroundConfig.ImagePath)))
        {
            // Background images are not transferred to clients (only animation content is);
            // a server-local path won't exist here. Degrade gracefully to solid color.
            Debug.WriteLine($"[D2D-CrossScreen] Background image '{backgroundConfig.ImagePath}' not available locally — falling back to solid color");
            backgroundConfig = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = req.BackgroundColor };
        }

        // ── Animation: full config when transmitted (TargetHeight/SpeedMultiplier/
        //    VerticalAlign/RotateWithPath/PrecomputedPath/pattern/colors all matter
        //    for cross-machine determinism), legacy reconstruction otherwise ──
        var animationConfig = DeserializeRemoteConfig<AnimationLayerConfig>(req.AnimationJson, "AnimationLayerConfig");
        if (animationConfig != null)
        {
            // The animation file was downloaded into this client's cache — the config
            // still carries the server-local path, so always override it.
            animationConfig.AnimationPath = req.FilePath;
            var missing = animationConfig.AdditionalAnimationPaths.Where(p => !File.Exists(p)).ToList();
            if (missing.Count > 0)
            {
                // Multi-image sources are not transferred yet (only the primary file is).
                Debug.WriteLine($"[D2D-CrossScreen] Dropping {missing.Count} additional animation path(s) not available locally: {string.Join(", ", missing)}");
                animationConfig.AdditionalAnimationPaths = animationConfig.AdditionalAnimationPaths.Where(File.Exists).ToList();
            }
        }
        else
        {
            PatternConfig? pattern = DeserializeRemoteConfig<PatternConfig>(req.PatternJson, "PatternConfig");
            ColorGradingConfig colorGrading = DeserializeRemoteConfig<ColorGradingConfig>(req.ColorGradingJson, "ColorGradingConfig") ?? new();
            animationConfig = new AnimationLayerConfig
            {
                AnimationPath = req.FilePath,
                TargetHeight = screen.Bounds.Height,
                Loop = true,
                VerticalAlign = VerticalAlignment.Center,
                CenterInitialPosition = true,
                FitMode = (ContentFitMode)req.FitMode,
                Pattern = pattern,
                ColorGrading = colorGrading
            };
        }

        // ── Movement: full config when transmitted, legacy type+speed otherwise ──
        var movementConfig = DeserializeRemoteConfig<MovementConfig>(req.MovementJson, "MovementConfig")
            ?? (req.MovementType == 0
                ? null
                : new MovementConfig
                {
                    Type = (MovementType)req.MovementType,
                    SpeedPixelsPerSecond = req.PixelsPerSecond
                });

        Debug.WriteLine($"[D2D-CrossScreen] Initializing D2D service: bounds={actualBounds}, vcw={req.VirtualCanvasWidth}, offsetX={req.MonitorOffsetX}, perMonitor={req.PerMonitorMode}");
        var d2dService = new D2DCompositionService(
            AppLogger.CreateLogger<D2DCompositionService>(),
            AppLogger.Factory,
            _desktopManager);

        // Simultaneous (per-monitor) mode must NOT receive the spanning canvas
        // overrides — the service derives canvas = this monitor's width, offset = 0,
        // exactly like the local path does (see StartCrossScreenAnimation).
        await d2dService.InitializeAsync(
            canvasManager, backgroundConfig, animationConfig, actualBounds,
            monitorIndex, movementConfig,
            perMonitorMode: req.PerMonitorMode,
            explicitVirtualCanvasWidth: req.PerMonitorMode ? null : req.VirtualCanvasWidth,
            explicitMonitorOffsetX: req.PerMonitorMode ? null : req.MonitorOffsetX);

        await Task.Delay(100);

        // Same speed convention as the local start path: Static → 0.
        var pixelsPerSecond = movementConfig == null || movementConfig.Type == MovementType.Static
            ? 0
            : (int)movementConfig.SpeedPixelsPerSecond;

        Debug.WriteLine($"[D2D-CrossScreen] Starting playback at ts={req.SharedStartTimestampMs}ms, speed={pixelsPerSecond}px/s");
        await d2dService.StartAsync(startTimestampMs: req.SharedStartTimestampMs, pixelsPerSecond: pixelsPerSecond);

        _remoteD2DServices[monitorIndex] = d2dService;
        Debug.WriteLine($"[D2D-CrossScreen] SUCCESS: Cross-screen D2D started on monitor {monitorIndex}");
    }

    /// <summary>Deserialize a JSON config received from the server; null on empty or malformed input.</summary>
    private static T? DeserializeRemoteConfig<T>(string json, string name) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D2D-CrossScreen] Failed to deserialize {name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Stop delegate invoked when the server sends a Stop command for cross-screen D2D.
    /// Tears down all D2D services that were started via ApplyCrossScreenD2DFromRemoteAsync.
    /// </summary>
    private async Task StopRemoteCrossScreenD2DAsync()
    {
        Debug.WriteLine($"[D2D-CrossScreen] Stopping {_remoteD2DServices.Count} remote D2D service(s)");
        foreach (var kvp in _remoteD2DServices)
        {
            try { await kvp.Value.StopAsync(); } catch { }
            try { kvp.Value.Dispose(); } catch { }
        }
        _remoteD2DServices.Clear();
        Debug.WriteLine("[D2D-CrossScreen] All remote D2D services stopped");
    }

    /// <summary>
    /// D2D apply delegate called when the server sends a D2D LOAD command to this client.
    /// Creates a local D2DCompositionService and renders on this machine's desktop.
    /// </summary>
    private async Task ApplyD2DFromRemoteAsync(string filePath, int monitorIndex, string backgroundColor, int fitMode)
    {
        Debug.WriteLine($"[D2D-Remote] ApplyD2DFromRemoteAsync called: file={filePath}, monitor={monitorIndex}, bg={backgroundColor}, fit={fitMode}");

        // Dispose previous service for this monitor
        if (_remoteD2DServices.TryRemove(monitorIndex, out var existing))
        {
            Debug.WriteLine($"[D2D-Remote] Disposing previous D2D service for monitor {monitorIndex}");
            existing.Dispose();
            await Task.Delay(300);
        }

        var monitors = NativeMonitorInfo.GetAllMonitors();
        Debug.WriteLine($"[D2D-Remote] Found {monitors.Length} monitors");

        if (monitorIndex < 0 || monitorIndex >= monitors.Length)
        {
            Debug.WriteLine($"[D2D-Remote] ERROR: Invalid monitor index {monitorIndex} (have {monitors.Length} monitors)");
            return;
        }

        var screen = monitors[monitorIndex];
        Debug.WriteLine($"[D2D-Remote] Target monitor {monitorIndex}: {screen.Bounds.Width}x{screen.Bounds.Height} at ({screen.Bounds.X},{screen.Bounds.Y})");

        var screenConfig = new WaBiBaBuSy.WallpaperEngine.Composition.ScreenConfiguration
        {
            ClientId = $"REMOTE_CLIENT_MONITOR_{monitorIndex}",
            Order = monitorIndex,
            Width = screen.Bounds.Width,
            Height = screen.Bounds.Height,
            PhysicalDistanceCm = 0
        };

        var canvasManager = new VirtualCanvasManager(
            AppLogger.CreateLogger<VirtualCanvasManager>());
        canvasManager.CalculateLayout(new[] { screenConfig } as IEnumerable<WaBiBaBuSy.WallpaperEngine.Composition.ScreenConfiguration>);

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

        Debug.WriteLine($"[D2D-Remote] Creating D2DCompositionService for monitor {monitorIndex}");
        var d2dService = new D2DCompositionService(
            AppLogger.CreateLogger<D2DCompositionService>(),
            AppLogger.Factory,
            _desktopManager);

        var actualBounds = new System.Drawing.Rectangle(
            screen.Bounds.X, screen.Bounds.Y,
            screen.Bounds.Width, screen.Bounds.Height);

        Debug.WriteLine($"[D2D-Remote] Initializing D2D service with bounds {actualBounds}");
        await d2dService.InitializeAsync(canvasManager, backgroundConfig, animationConfig, actualBounds, monitorIndex);
        await Task.Delay(100);

        Debug.WriteLine($"[D2D-Remote] Starting D2D playback");
        await d2dService.StartAsync(startTimestampMs: 0, pixelsPerSecond: 0);

        _remoteD2DServices[monitorIndex] = d2dService;
        Debug.WriteLine($"[D2D-Remote] SUCCESS: Applied D2D wallpaper '{Path.GetFileName(filePath)}' on monitor {monitorIndex}");
    }

    /// <summary>
    /// Refresh client topology from server OR show local-only mode
    /// </summary>
    public async void RefreshTopology()
    {
        // Don't overwrite order while a reorder operation is in flight
        if (_isReorderingInProgress) return;

        try
        {
            if (_enableNetworkTopologyDebugOutput)
            {
                // Debug.WriteLine($"[RefreshTopology] Called - IsServerRunning: {_service.IsServerRunning}, IsClientConnected: {_service.IsClientConnected}");
            }

            // Always show at least the local machine
            if (_service.IsServerRunning)
            {
                // Server mode - get connected clients and add localhost monitors as server nodes
                var connectedClients = _service.GetConnectedClients().ToList();
                if (_enableNetworkTopologyDebugOutput)
                    Debug.WriteLine($"Server mode: Got {connectedClients.Count} connected clients");

                // Create a list that includes the server's local monitors as the first nodes
                var allNodes = new List<WaBiBaBuSy.Grpc.ConnectedClient>();

                // Enumerate local monitors for the server machine
                var screens = System.Windows.Forms.Screen.AllScreens;
                var nativeMonitorsServer = WaBiBaBuSy.WallpaperEngine.Native.NativeMonitorInfo.GetAllMonitors();
                var serverRefreshByDevice = nativeMonitorsServer.ToDictionary(m => m.DeviceName, m => m.RefreshRateHz);
                var serverScreenConfig = new WaBiBaBuSy.Grpc.ScreenConfiguration
                {
                    MonitorCount = screens.Length,
                    TotalWidth = System.Windows.Forms.SystemInformation.VirtualScreen.Width,
                    TotalHeight = System.Windows.Forms.SystemInformation.VirtualScreen.Height
                };
                for (int i = 0; i < screens.Length; i++)
                {
                    var screen = screens[i];
                    serverRefreshByDevice.TryGetValue(screen.DeviceName, out int serverRefreshHz);
                    serverScreenConfig.Monitors.Add(new WaBiBaBuSy.Grpc.MonitorInfo
                    {
                        Index = i,
                        Width = screen.Bounds.Width,
                        Height = screen.Bounds.Height,
                        X = screen.Bounds.X,
                        Y = screen.Bounds.Y,
                        IsPrimary = screen.Primary,
                        DeviceName = screen.DeviceName,
                        RefreshRate = serverRefreshHz
                    });
                }

                // Add one server node per local monitor
                for (int i = 0; i < screens.Length; i++)
                {
                    var screen = screens[i];
                    var nodeId = $"SERVER_LOCALHOST_MONITOR_{i}";
                    // Use persisted order if available, otherwise default to index
                    var persistedOrder = _service.GetServerLocalMonitorOrder(nodeId) ?? i;
                    var serverNode = new WaBiBaBuSy.Grpc.ConnectedClient
                    {
                        ClientId = nodeId,
                        Hostname = $"{Environment.MachineName} - Monitor {i + 1} (Server)",
                        IpAddress = screen.Primary ? "Primary Monitor (Server)" : $"Monitor {i + 1} (Server)",
                        Status = WaBiBaBuSy.Grpc.ClientStatusEnum.ClientConnected,
                        OrderPosition = persistedOrder,
                        PhysicalDistanceCm = 0,
                        ScreenConfig = serverScreenConfig
                    };
                    allNodes.Add(serverNode);
                    if (_enableNetworkTopologyDebugOutput)
                        Debug.WriteLine($"Added server monitor node: {serverNode.Hostname} at position {serverNode.OrderPosition}");
                }

                // Add connected clients - expand each client into per-monitor nodes
                int nextOrder = screens.Length;
                foreach (var client in connectedClients)
                {
                    int clientMonitorCount = client.ScreenConfig?.MonitorCount ?? 1;
                    if (clientMonitorCount > 1 && client.ScreenConfig?.Monitors.Count > 0)
                    {
                        // Expand into one node per monitor
                        for (int m = 0; m < client.ScreenConfig.Monitors.Count; m++)
                        {
                            var monitor = client.ScreenConfig.Monitors[m];
                            var expandedId = $"{client.ClientId}_MONITOR_{m}";
                            var expandedOrder = _service.GetServerLocalMonitorOrder(expandedId) ?? nextOrder;
                            nextOrder++;
                            allNodes.Add(new WaBiBaBuSy.Grpc.ConnectedClient
                            {
                                ClientId = expandedId,
                                Hostname = $"{client.Hostname} - Monitor {m + 1}",
                                IpAddress = client.IpAddress,
                                Status = client.Status,
                                OrderPosition = expandedOrder,
                                PhysicalDistanceCm = client.PhysicalDistanceCm,
                                ScreenConfig = client.ScreenConfig
                            });
                        }
                    }
                    else
                    {
                        // Single monitor client - use server's OrderPosition directly
                        allNodes.Add(new WaBiBaBuSy.Grpc.ConnectedClient
                        {
                            ClientId = client.ClientId,
                            Hostname = client.Hostname,
                            IpAddress = client.IpAddress,
                            Status = client.Status,
                            OrderPosition = client.OrderPosition,
                            PhysicalDistanceCm = client.PhysicalDistanceCm,
                            ScreenConfig = client.ScreenConfig
                        });
                    }
                }

                if (_enableNetworkTopologyDebugOutput)
                    Debug.WriteLine($"Total nodes to display: {allNodes.Count}");
                UpdateClientList(allNodes);
            }
            else if (_service.IsClientConnected)
            {
                // Client mode - get topology from server and expand into per-monitor nodes
                var topology = await _service.GetTopologyAsync();
                if (topology != null)
                {
                    if (_enableNetworkTopologyDebugOutput)
                        Debug.WriteLine($"Client mode: Got topology with {topology.Clients.Count} clients");

                    var expandedNodes = new List<WaBiBaBuSy.Grpc.ConnectedClient>();
                    int nextOrder = 0;
                    foreach (var client in topology.Clients.OrderBy(c => c.OrderPosition))
                    {
                        int clientMonitorCount = client.ScreenConfig?.MonitorCount ?? 1;
                        if (clientMonitorCount > 1 && client.ScreenConfig?.Monitors.Count > 0)
                        {
                            for (int m = 0; m < client.ScreenConfig.Monitors.Count; m++)
                            {
                                expandedNodes.Add(new WaBiBaBuSy.Grpc.ConnectedClient
                                {
                                    ClientId = $"{client.ClientId}_MONITOR_{m}",
                                    Hostname = $"{client.Hostname} - Monitor {m + 1}",
                                    IpAddress = client.IpAddress,
                                    Status = client.Status,
                                    OrderPosition = nextOrder++,
                                    PhysicalDistanceCm = client.PhysicalDistanceCm,
                                    ScreenConfig = client.ScreenConfig
                                });
                            }
                        }
                        else
                        {
                            expandedNodes.Add(new WaBiBaBuSy.Grpc.ConnectedClient
                            {
                                ClientId = client.ClientId,
                                Hostname = client.Hostname,
                                IpAddress = client.IpAddress,
                                Status = client.Status,
                                OrderPosition = nextOrder++,
                                PhysicalDistanceCm = client.PhysicalDistanceCm,
                                ScreenConfig = client.ScreenConfig
                            });
                        }
                    }
                    UpdateClientList(expandedNodes);
                }
            }
            else
            {
                // Local-only mode - show local machine node
                if (_enableNetworkTopologyDebugOutput)
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
        if (_enableNetworkTopologyDebugOutput)
            Debug.WriteLine($"[ShowLocalMachineNode] Detected {screens.Length} monitor(s)");

        var nativeMonitors = WaBiBaBuSy.WallpaperEngine.Native.NativeMonitorInfo.GetAllMonitors();
        var refreshByDevice = nativeMonitors.ToDictionary(m => m.DeviceName, m => m.RefreshRateHz);

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
            refreshByDevice.TryGetValue(screen.DeviceName, out int refreshHz);
            screenConfig.Monitors.Add(new WaBiBaBuSy.Grpc.MonitorInfo
            {
                Index = i,
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height,
                X = screen.Bounds.X,
                Y = screen.Bounds.Y,
                IsPrimary = screen.Primary,
                DeviceName = screen.DeviceName,
                RefreshRate = refreshHz
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
            if (_enableNetworkTopologyDebugOutput)
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
                if (_enableNetworkTopologyDebugOutput)
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
                    if (_enableNetworkTopologyDebugOutput)
                        Debug.WriteLine($"Updating existing client: {grpcClient.ClientId} at position {grpcClient.OrderPosition}");
                    existing.Hostname = grpcClient.Hostname;
                    existing.IpAddress = grpcClient.IpAddress;
                    existing.IsConnected = grpcClient.Status == WaBiBaBuSy.Grpc.ClientStatusEnum.ClientConnected ||
                                          grpcClient.Status == WaBiBaBuSy.Grpc.ClientStatusEnum.ClientPlaying;
                    existing.Status = grpcClient.Status.ToString();
                    existing.Order = grpcClient.OrderPosition;
                    existing.PhysicalDistanceCm = grpcClient.PhysicalDistanceCm;

                    // Update thumbnail if available (server mode only)
                    UpdateClientThumbnail(existing);
                }
                else
                {
                    // Add new client (positions are calculated by auto-wrap layout in the view)
                    var x = 0.0;
                    var y = 0.0;
                    if (_enableNetworkTopologyDebugOutput)
                        Debug.WriteLine($"[UpdateClientList] Adding new client: {grpcClient.ClientId} ({grpcClient.Hostname}) at X={x}, Y={y}");

                    // Extract monitor info for this specific node
                    int monitorIndex = -1;
                    string? monitorName = null;
                    int monitorWidth = 0;
                    int monitorHeight = 0;
                    bool isPrimary = false;
                    int monitorRefreshHz = 0;
                    float monitorPixelsPerCm = 0f;

                    // Check if this is a monitor-specific node (LOCAL_MACHINE_MONITOR_X, SERVER_LOCALHOST_MONITOR_X, or ClientId_MONITOR_X)
                    string? monitorSuffix = null;
                    if (grpcClient.ClientId.StartsWith("LOCAL_MACHINE_MONITOR_"))
                        monitorSuffix = grpcClient.ClientId.Replace("LOCAL_MACHINE_MONITOR_", "");
                    else if (grpcClient.ClientId.StartsWith("SERVER_LOCALHOST_MONITOR_"))
                        monitorSuffix = grpcClient.ClientId.Replace("SERVER_LOCALHOST_MONITOR_", "");
                    else if (grpcClient.ClientId.Contains("_MONITOR_"))
                        monitorSuffix = grpcClient.ClientId.Substring(grpcClient.ClientId.LastIndexOf("_MONITOR_") + "_MONITOR_".Length);

                    if (monitorSuffix != null && int.TryParse(monitorSuffix, out monitorIndex))
                    {
                        // Find the corresponding monitor info in screen config
                        if (grpcClient.ScreenConfig != null && grpcClient.ScreenConfig.Monitors.Count > monitorIndex)
                        {
                            var monitorInfo = grpcClient.ScreenConfig.Monitors[monitorIndex];
                            monitorName = monitorInfo.DeviceName;
                            monitorWidth = monitorInfo.Width;
                            monitorHeight = monitorInfo.Height;
                            isPrimary = monitorInfo.IsPrimary;
                            monitorRefreshHz = monitorInfo.RefreshRate;
                            monitorPixelsPerCm = monitorInfo.PixelsPerCm;
                        }
                    }
                    else if (grpcClient.ScreenConfig?.Monitors.Count == 1)
                    {
                        // Single monitor client - use its only monitor info
                        var monitorInfo = grpcClient.ScreenConfig.Monitors[0];
                        monitorIndex = 0;
                        monitorName = monitorInfo.DeviceName;
                        monitorWidth = monitorInfo.Width;
                        monitorHeight = monitorInfo.Height;
                        isPrimary = monitorInfo.IsPrimary;
                        monitorRefreshHz = monitorInfo.RefreshRate;
                        monitorPixelsPerCm = monitorInfo.PixelsPerCm;
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
                        MonitorRefreshHz = monitorRefreshHz,
                        IsPrimaryMonitor = isPrimary,
                        PixelsPerCm = monitorPixelsPerCm
                    };

                    Debug.WriteLine($"[UpdateClientList] About to add client to Clients collection");
                    Debug.WriteLine($"[UpdateClientList] Client details - ID: {newClient.ClientId}, Hostname: {newClient.Hostname}, DisplayName: {newClient.DisplayName}");
                    Debug.WriteLine($"[UpdateClientList] Monitor info - Index: {monitorIndex}, Size: {monitorWidth}x{monitorHeight}, Primary: {isPrimary}");
                    Debug.WriteLine($"[UpdateClientList] Position - X: {newClient.X}, Y: {newClient.Y}");
                    Debug.WriteLine($"[UpdateClientList] Status: {newClient.Status}, IsConnected: {newClient.IsConnected}");

                    // Subscribe to IsSelected changes for CanExecute updates
                    newClient.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(ClientNodeViewModel.IsSelected))
                            NotifyClientSelectionCommands();
                    };

                    Clients.Add(newClient);
                    Debug.WriteLine($"[UpdateClientList] Client added. Total clients now: {Clients.Count}");
                }
                index++;
            }

            ClientCount = Clients.Count;
            ConnectedClientCount = Clients.Count(c => c.IsConnected
                && !c.ClientId.StartsWith("SERVER_LOCALHOST_MONITOR_")
                && !c.ClientId.StartsWith("LOCAL_MACHINE_MONITOR_"));
            Debug.WriteLine($"[UpdateClientList] Complete. Final client count: {Clients.Count}");
            Debug.WriteLine($"[UpdateClientList] Clients in collection: {string.Join(", ", Clients.Select(c => c.Hostname))}");
        });
    }

    /// <summary>
    /// Update the thumbnail image for a client node.
    /// For remote clients: reads from server gRPC thumbnail cache.
    /// For local monitors: captures directly via ThumbnailCaptureService.
    /// </summary>
    private void UpdateClientThumbnail(ClientNodeViewModel client)
    {
        try
        {
            byte[]? jpegBytes = null;

            if (client.ClientId.StartsWith("LOCAL_MACHINE_MONITOR_"))
            {
                // Local monitor: capture thumbnail directly
                var monitorIndexStr = client.ClientId.Replace("LOCAL_MACHINE_MONITOR_", "");
                if (int.TryParse(monitorIndexStr, out var monitorIndex) &&
                    _thumbnailCaptureServices.TryGetValue(monitorIndex, out var captureService))
                {
                    jpegBytes = captureService.CaptureCurrentThumbnail();
                }
            }
            else
            {
                // Remote client: read from server gRPC thumbnail cache
                jpegBytes = _service.GetClientThumbnail(client.ClientId);
            }

            if (jpegBytes == null || jpegBytes.Length == 0)
            {
                return;
            }

            using var ms = new MemoryStream(jpegBytes);
            client.ThumbnailImage = new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateClientThumbnail] Error for {client.ClientId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Set up thumbnail capture for a local monitor after a wallpaper is applied.
    /// </summary>
    private void SetupThumbnailCapture(int monitorIndex, IntPtr hwnd, string wallpaperName)
    {
        if (hwnd == IntPtr.Zero)
        {
            Debug.WriteLine($"[Thumbnail] No HWND available for monitor {monitorIndex}, skipping thumbnail setup");
            return;
        }

        var captureService = _thumbnailCaptureServices.GetOrAdd(monitorIndex, _ =>
            new ThumbnailCaptureService(AppLogger.CreateLogger<ThumbnailCaptureService>()));

        captureService.SetWallpaperHwnd(hwnd, wallpaperName);
        Debug.WriteLine($"[Thumbnail] Set up capture for monitor {monitorIndex}: HWND={hwnd}, wallpaper={wallpaperName}");
    }

    /// <summary>
    /// Clear thumbnail capture for a local monitor when wallpaper is removed.
    /// </summary>
    private void ClearThumbnailCapture(int monitorIndex)
    {
        if (_thumbnailCaptureServices.TryGetValue(monitorIndex, out var captureService))
        {
            captureService.ClearWallpaperHwnd();
        }
    }

    private void OnRefreshTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        RefreshTopology();
    }

    private void OnServerStatusChanged(object? sender, Core.Services.Networking.ServerStatusChangedEventArgs e)
    {
        IsServerMode = e.IsRunning;
        ServerStatus = e.IsRunning ? "Running" : "Stopped";
        ServerPort = e.Port;

        // Refresh topology when server status changes
        Debug.WriteLine($"[OnServerStatusChanged] Server status changed to: {(e.IsRunning ? "Running" : "Stopped")}");
        RefreshTopology();
    }

    private void OnClientConnectionStatusChanged(object? sender, Core.Services.Networking.ConnectionStatusChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsClientConnected = e.IsConnected;
            if (e.IsConnected)
            {
                ClientConnectionStatus = $"Connected to {ConnectServerAddress}:{ConnectServerPort}";
            }
            else
            {
                IsServerMode = false;
                ClientConnectionStatus = "Disconnected";
            }
        });
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
                var config = ConfigurationManager.LoadServerConfiguration();
                ServerPort = config.Port;
                ServerStatus = "Running";
            }
            else
            {
                IsServerMode = false;
                ServerStatus = "Stopped";
            }

            // Update client connection state
            IsClientConnected = _service.IsClientConnected;
            if (IsClientConnected)
            {
                ClientConnectionStatus = $"Connected to {ConnectServerAddress}:{ConnectServerPort}";
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

        // Clear all thumbnail capture services
        foreach (var kvp in _thumbnailCaptureServices)
        {
            kvp.Value.ClearWallpaperHwnd();
        }
        _thumbnailCaptureServices.Clear();

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

            // Give the dialog access to owner window and gallery for the gallery picker
            viewModel.SetOwnerWindow(_mainWindow);
            viewModel.SetGalleryWallpapers(Wallpapers);

            // Pre-populate from selected wallpaper in gallery
            viewModel.PreSelectedWallpaper = SelectedWallpaper;

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
                        Mode = BackgroundMode.IconZone,
                        ColorHex = "#000000",
                        IconZonePaletteHexes = PaletteGenerator.GenerateHarmonious(8),
                        IconCorridorColorHex = "#1E1E1E"
                    },
                    Animation = new AnimationLayerConfig
                    {
                        AnimationPath = string.Empty,
                        TargetHeight = 720,
                        Loop = true,
                        VerticalAlign = VerticalAlignment.Center,
                        RotateWithPath = true
                    },
                    AnimationSpeedPxPerSecond = 500
                };
                viewModel.LoadFromConfig(_crossScreenConfig);
            }

            // Apply pre-selected wallpaper to auto-populate empty fields
            viewModel.ApplyPreSelectedWallpaper();

            // Auto-detect background color if still at default (saves the user a manual click)
            if ((string.IsNullOrEmpty(viewModel.BackgroundColor) || viewModel.BackgroundColor == "#000000")
                && (!string.IsNullOrEmpty(viewModel.AnimationPath) || !string.IsNullOrEmpty(viewModel.BackgroundImagePath)))
            {
                try { await viewModel.AutoDetectBackgroundColorCommand.ExecuteAsync(null); }
                catch (Exception ex) { Debug.WriteLine($"[CrossScreen] Auto-detect color failed: {ex.Message}"); }
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
                    HasAnimationConfig = !string.IsNullOrEmpty(_crossScreenConfig.Animation.AnimationPath);
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

    [RelayCommand(CanExecute = nameof(CanStartCrossScreen))]
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
            Debug.WriteLine("[CrossScreen] Starting cross-screen animation via D2D...");

            // Convert clients to screen configurations, filtering by selected monitors if configured
            var selectedMonitorIds = new HashSet<string>(_crossScreenConfig.SelectedMonitorIds);
            var clientsToUse = selectedMonitorIds.Count > 0
                ? Clients.Where(c => selectedMonitorIds.Contains(c.ClientId))
                : Clients;

            // Split clients into local monitors and remote nodes.
            // Use IsLocalMonitor() to cover both LOCAL_MACHINE_MONITOR_ (standalone) and
            // SERVER_LOCALHOST_MONITOR_ (server mode) so local nodes are never sent gRPC commands.
            var localClientsUnordered = clientsToUse
                .Where(c => IsLocalMonitor(c.ClientId))
                .ToList();
            var remoteClients = clientsToUse
                .Where(c => !IsLocalMonitor(c.ClientId))
                .ToList();

            List<ClientNodeViewModel> localClients;
            if (selectedMonitorIds.Count > 0)
            {
                // Order by position in SelectedMonitorIds list (preserves user's custom order)
                var orderMap = _crossScreenConfig.SelectedMonitorIds
                    .Select((id, idx) => (id, idx))
                    .ToDictionary(x => x.id, x => x.idx);
                localClients = localClientsUnordered
                    .OrderBy(c => orderMap.GetValueOrDefault(c.ClientId, int.MaxValue))
                    .ToList();
            }
            else
            {
                localClients = localClientsUnordered
                    .OrderBy(c => c.Order)
                    .ToList();
            }

            if (localClients.Count == 0 && remoteClients.Count == 0)
            {
                Debug.WriteLine("[CrossScreen] No monitors selected");
                return;
            }

            Debug.WriteLine($"[CrossScreen] Starting D2D animation on {localClients.Count} local + {remoteClients.Count} remote node(s)");

            // Query DPI for local monitors to derive physical pixels-per-cm for gap modeling.
            // Remote clients are assumed to have the same monitor model as the first local monitor.
            var nativeMonitors = WaBiBaBuSy.WallpaperEngine.Native.NativeMonitorInfo.GetAllMonitors();
            float fallbackPixelsPerCm = nativeMonitors.Length > 0 ? nativeMonitors[0].PixelsPerCm : 0f;

            // Build screen configurations for virtual canvas - ALL nodes (local + remote)
            var allClients = localClients.Concat(remoteClients).ToList();
            var screenConfigs = allClients.Select(c => new WallpaperEngine.Composition.ScreenConfiguration
            {
                ClientId = c.ClientId,
                Width = c.MonitorWidth > 0 ? c.MonitorWidth : 1920,
                Height = c.MonitorHeight > 0 ? c.MonitorHeight : 1080,
                Order = c.Order,
                PhysicalDistanceCm = c.PhysicalDistanceCm,
                Hostname = c.Hostname,
                MonitorIndex = c.MonitorIndex,
                // Local: look up by monitor index; Remote: use the DPI the client
                // reported at registration; first local monitor only as last resort.
                PixelsPerCm = c.ClientId.StartsWith("LOCAL_MACHINE_MONITOR_")
                    ? (c.MonitorIndex < nativeMonitors.Length ? nativeMonitors[c.MonitorIndex].PixelsPerCm : fallbackPixelsPerCm)
                    : (c.PixelsPerCm > 0f ? c.PixelsPerCm : fallbackPixelsPerCm)
            }).ToList();

            // Create virtual canvas spanning ALL nodes.
            // Gap pixels between physically spaced monitors are inserted automatically
            // from each screen's PixelsPerCm + PhysicalDistanceCm.
            var canvasManager = new VirtualCanvasManager(
                AppLogger.CreateLogger<VirtualCanvasManager>());
            canvasManager.CalculateLayout(screenConfigs);

            Debug.WriteLine($"[CrossScreen] Virtual canvas: {canvasManager.VirtualBounds.Width}x{canvasManager.VirtualBounds.Height}");

            // Get actual monitor bounds from Windows
            var screens = System.Windows.Forms.Screen.AllScreens;

            // Sequential + IconZone: compute one global A* path across the full virtual canvas
            // so all local monitors share the same coordinated corridor path.
            var perMonitor = _crossScreenConfig.DistributionMode == AnimationDistributionMode.Simultaneous;
            var animationConfig = _crossScreenConfig.Animation;
            if (!perMonitor && _crossScreenConfig.Background.Mode == BackgroundMode.IconZone && localClients.Count > 0)
            {
                try
                {
                    var iconService = new WaBiBaBuSy.Core.Services.Desktop.DesktopIconService();
                    var (cellW, cellH) = iconService.GetGridCellSize();
                    var allIcons = iconService.GetIconPositions(); // positions in virtual-desktop (absolute) coords

                    int firstMonitorIdx = GetMonitorIndex(localClients[0].ClientId);
                    var firstScreen = firstMonitorIdx < screens.Length ? screens[firstMonitorIdx] : screens[0];
                    int virtualH = firstScreen.Bounds.Height;

                    // Effective animation height depends on FitMode. Using the actual rendered height
                    // keeps the server-side path padding in sync with what Player.D2D will compute
                    // locally (pathPaddingPx = _animHeight / 2 + optional wave amplitude).
                    int effectiveHeight = ComputeEffectiveAnimationHeight(
                        _crossScreenConfig.Animation, firstScreen.Bounds.Width, virtualH);
                    int pathPaddingPx = effectiveHeight / 2;
                    if (_crossScreenConfig.Movement.Type == MovementType.SineWave)
                        pathPaddingPx += (int)Math.Ceiling(_crossScreenConfig.Movement.WaveAmplitudePixels);
                    var globalLayout = WaBiBaBuSy.WallpaperEngine.Desktop.ZonePlanner.Compute(
                        allIcons.Select(i => (i.PixelX, i.PixelY)),
                        cellW, cellH,
                        canvasManager.VirtualBounds.Width, virtualH,
                        _crossScreenConfig.Background.IconZonePaletteHexes,
                        _crossScreenConfig.Background.IconCorridorColorHex,
                        paddingPx: pathPaddingPx,
                        visualPaddingPx: Math.Max(4, cellW / 10));

                    // Clone animation config with global path attached
                    animationConfig = new AnimationLayerConfig
                    {
                        AnimationPath             = _crossScreenConfig.Animation.AnimationPath,
                        AdditionalAnimationPaths  = _crossScreenConfig.Animation.AdditionalAnimationPaths,
                        TargetHeight              = _crossScreenConfig.Animation.TargetHeight,
                        Loop                      = _crossScreenConfig.Animation.Loop,
                        VerticalAlign             = _crossScreenConfig.Animation.VerticalAlign,
                        CenterInitialPosition     = _crossScreenConfig.Animation.CenterInitialPosition,
                        SpeedMultiplier           = _crossScreenConfig.Animation.SpeedMultiplier,
                        FitMode                   = _crossScreenConfig.Animation.FitMode,
                        RotateWithPath            = _crossScreenConfig.Animation.RotateWithPath,
                        PrecomputedPath           = globalLayout.Path,
                        ColorGrading              = _crossScreenConfig.Animation.ColorGrading,
                        Pattern                   = _crossScreenConfig.Animation.Pattern,
                        MultiImageSpread          = _crossScreenConfig.Animation.MultiImageSpread,
                        MultiImagePhaseJitterMs   = _crossScreenConfig.Animation.MultiImagePhaseJitterMs
                    };

                    Debug.WriteLine($"[CrossScreen] IconZone sequential: computed global path with {globalLayout.Path.Count} waypoints across {canvasManager.VirtualBounds.Width}px virtual canvas");

                    // Save params for per-lap recompute
                    _seqCellW              = cellW;
                    _seqCellH              = cellH;
                    _seqVirtualCanvasWidth = canvasManager.VirtualBounds.Width;
                    _seqVirtualH           = virtualH;
                    _seqPathPaddingPx      = pathPaddingPx;
                    _seqVisualPaddingPx    = Math.Max(4, cellW / 10);
                    _seqPaletteHexes       = _crossScreenConfig.Background.IconZonePaletteHexes.ToList();
                    _seqCorridorColorHex   = _crossScreenConfig.Background.IconCorridorColorHex;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CrossScreen] Global icon path failed, players will compute locally: {ex.Message}");
                }
            }

            // Phase 1: Initialize all D2D players (load animation, extract GIF frames)
            var newServices = new List<(int monitorIndex, D2DCompositionService service)>();

            foreach (var client in localClients)
            {
                var monitorIndex = GetMonitorIndex(client.ClientId);

                // Clean up existing D2D service for this monitor if any
                if (_d2dCompositionServices.TryRemove(monitorIndex, out var existingService))
                {
                    try { await existingService.StopAsync(); } catch { }
                    existingService.Dispose();
                }

                // Find actual screen bounds
                var screen = monitorIndex < screens.Length ? screens[monitorIndex] : screens[0];
                var actualBounds = new System.Drawing.Rectangle(
                    screen.Bounds.X, screen.Bounds.Y,
                    screen.Bounds.Width, screen.Bounds.Height);

                Debug.WriteLine($"[CrossScreen] Monitor {monitorIndex}: {actualBounds.Width}x{actualBounds.Height} at ({actualBounds.X},{actualBounds.Y})");

                // Build a single-screen canvas for this monitor's service.
                // D2DCompositionService.InitializeAsync loops over ScreenMappings and spawns one
                // player per entry — passing the full multi-screen canvasManager would cause it to
                // spawn N players per service (N² total), all positioned at the same actualBounds.
                // Each service must own exactly ONE player for its own monitor.
                var thisCfg = screenConfigs.First(s => s.ClientId == client.ClientId);
                var singleCanvas = new VirtualCanvasManager(AppLogger.CreateLogger<VirtualCanvasManager>());
                singleCanvas.CalculateLayout(new[] { thisCfg });

                // In sequential mode the player needs the full virtual canvas width and this
                // monitor's X offset within it; derive both from the global canvasManager.
                var globalMapping = canvasManager.GetScreenByClientId(client.ClientId);
                int virtualOffsetX = globalMapping?.VirtualBounds.X ?? 0;

                // Create D2D service
                var d2dService = new D2DCompositionService(
                    AppLogger.CreateLogger<D2DCompositionService>(),
                    AppLogger.Factory,
                    _desktopManager);

                // Initialize: singleCanvas → exactly 1 player spawned for this monitor.
                // Pass explicit virtual-canvas width + offset so the player computes
                // movement correctly in sequential (spanning) mode.
                await d2dService.InitializeAsync(
                    singleCanvas,
                    _crossScreenConfig.Background,
                    animationConfig,
                    actualBounds,
                    monitorIndex,
                    _crossScreenConfig.Movement,
                    perMonitorMode: perMonitor,
                    explicitVirtualCanvasWidth: perMonitor ? null : canvasManager.VirtualBounds.Width,
                    explicitMonitorOffsetX: perMonitor ? null : virtualOffsetX);

                // Subscribe for lap-completion recompute in sequential IconZone mode
                if (!perMonitor && _crossScreenConfig.Background.Mode == BackgroundMode.IconZone)
                    d2dService.GlobalLapCompleted += OnSequentialLapCompleted;

                newServices.Add((monitorIndex, d2dService));
                Debug.WriteLine($"[CrossScreen] Monitor {monitorIndex} initialized (virtualOffsetX={virtualOffsetX}, vcw={canvasManager.VirtualBounds.Width})");
            }

            // Brief pause to let all players finish loading
            await Task.Delay(200);

            // Phase 2: Start ALL players with same shared timestamp.
            // Background always renders on all monitors simultaneously.
            // DistributionMode controls animation positioning:
            //   - Sequential: animation spans across virtual canvas (multi-monitor spanning)
            //   - Simultaneous: animation plays independently on each monitor
            var sharedStartTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pixelsPerSecond = _crossScreenConfig.Movement.Type == MovementType.Static
                ? 0
                : (int)_crossScreenConfig.Movement.SpeedPixelsPerSecond;

            var isSequential = _crossScreenConfig.DistributionMode == AnimationDistributionMode.Sequential;
            Debug.WriteLine($"[CrossScreen] Starting {(isSequential ? "SEQUENTIAL (spanning)" : "SIMULTANEOUS (per-monitor)")} animation on {newServices.Count} monitors, timestamp={sharedStartTimestamp}ms, speed={pixelsPerSecond}px/s");

            foreach (var (monitorIndex, d2dService) in newServices)
            {
                await d2dService.StartAsync(startTimestampMs: sharedStartTimestamp, pixelsPerSecond: pixelsPerSecond);
                _d2dCompositionServices[monitorIndex] = d2dService;
            }

            // Phase 3: Send cross-screen D2D commands to remote clients
            if (remoteClients.Count > 0 && _service.SyncCoordinator != null)
            {
                var contentId = System.IO.Path.GetFileName(_crossScreenConfig.Animation.AnimationPath);
                _crossScreenContentId = contentId;
                var bgColor = _crossScreenConfig.Background.ColorHex ?? "#000000";
                var movementTypeInt = (int)_crossScreenConfig.Movement.Type;

                foreach (var remoteClient in remoteClients)
                {
                    var screenMapping = canvasManager.GetScreenByClientId(remoteClient.ClientId);
                    var remoteOffsetX = screenMapping?.VirtualBounds.X ?? 0;

                    Debug.WriteLine($"[CrossScreen] Sending cross-screen D2D to remote {remoteClient.Hostname} ({remoteClient.ClientId}): offset={remoteOffsetX}px, canvas={canvasManager.VirtualBounds.Width}px");

                    await _service.SyncCoordinator.StartCrossScreenD2DOnClientAsync(
                        clientId: remoteClient.ClientId,
                        contentId: contentId,
                        filePath: _crossScreenConfig.Animation.AnimationPath,
                        backgroundColor: bgColor,
                        fitMode: (int)(_crossScreenConfig.Animation.FitMode),
                        virtualCanvasWidth: canvasManager.VirtualBounds.Width,
                        monitorOffsetX: remoteOffsetX,
                        sharedStartTimestampMs: sharedStartTimestamp,
                        pixelsPerSecond: pixelsPerSecond,
                        perMonitorMode: !isSequential,
                        movementType: movementTypeInt,
                        pattern: _crossScreenConfig.Animation.Pattern,
                        colorGrading: _crossScreenConfig.Animation.ColorGrading,
                        // Full configs so the remote computes identical deterministic math.
                        // animationConfig is the (possibly IconZone-cloned) config, so the
                        // precomputed global A* path now reaches remote machines too.
                        movement: _crossScreenConfig.Movement,
                        animation: animationConfig,
                        background: _crossScreenConfig.Background,
                        // MonitorIndex is -1 for whole-machine nodes → render on primary.
                        targetMonitorIndex: Math.Max(0, remoteClient.MonitorIndex));
                }
            }
            else if (remoteClients.Count > 0)
            {
                Debug.WriteLine("[CrossScreen] WARNING: Remote clients selected but server not running - cannot send cross-screen commands");
            }

            StartFullscreenDetectionIfNeeded();

            // Set up thumbnail capture for live preview in topology nodes
            var animName = Path.GetFileName(_crossScreenConfig.Animation.AnimationPath) ?? "Animation";
            foreach (var (monitorIndex, d2dService) in newServices)
            {
                SetupThumbnailCapture(monitorIndex, d2dService.PlayerHwnd, animName);
            }

            IsCrossScreenRunning = true;
            HasAnimationConfig = true;
            ClearAllWallpapersCommand.NotifyCanExecuteChanged();

            // Set animation indicators on participating clients
            var animFileName = Path.GetFileName(_crossScreenConfig?.Animation.AnimationPath ?? string.Empty);
            var selectedIds = new HashSet<string>(_crossScreenConfig?.SelectedMonitorIds ?? new List<string>());
            foreach (var client in Clients)
            {
                if (selectedIds.Count == 0 || selectedIds.Contains(client.ClientId))
                {
                    client.IsAnimating = true;
                    client.ActiveAnimationName = animFileName;
                }
            }

            Debug.WriteLine("[CrossScreen] D2D animation started successfully on all monitors");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CrossScreen] Error starting: {ex.Message}\n{ex.StackTrace}");
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

            // Stop and dispose all D2D composition services
            foreach (var kvp in _d2dCompositionServices)
            {
                kvp.Value.GlobalLapCompleted -= OnSequentialLapCompleted;
                try { await kvp.Value.StopAsync(); } catch { }
                try { kvp.Value.Dispose(); } catch { }
            }
            _d2dCompositionServices.Clear();
            _lastGlobalLap = -1;

            // Send stop command to remote clients that were part of this session
            if (_service.SyncCoordinator != null && _crossScreenConfig != null && !string.IsNullOrEmpty(_crossScreenContentId))
            {
                var selectedIds = new HashSet<string>(_crossScreenConfig.SelectedMonitorIds);
                var remoteToStop = Clients
                    .Where(c => !IsLocalMonitor(c.ClientId) && (selectedIds.Count == 0 || selectedIds.Contains(c.ClientId)))
                    .ToList();

                foreach (var remote in remoteToStop)
                {
                    try
                    {
                        await _service.SyncCoordinator.StopCrossScreenOnClientAsync(remote.ClientId, _crossScreenContentId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CrossScreen] Failed to send stop to remote {remote.ClientId}: {ex.Message}");
                    }
                }
            }
            _crossScreenContentId = null;

            IsCrossScreenRunning = false;
            ClearAllWallpapersCommand.NotifyCanExecuteChanged();
            StopFullscreenDetectionIfIdle();

            // Reset animation indicators on all clients
            foreach (var client in Clients)
            {
                client.IsAnimating = false;
                client.IsCurrentAnimationTarget = false;
                client.ActiveAnimationName = null;
            }

            Debug.WriteLine("[CrossScreen] Animation stopped");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CrossScreen] Error stopping: {ex.Message}");
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
            var logger = AppLogger.CreateLogger<MainWindowViewModel>();
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
            AppLogger.CreateLogger<MainWindowViewModel>()
                .LogError(ex, "[DistributedAnimation] Error in OnDistributedAnimationRender");
            throw;
        }
    }

    /// <summary>
    /// Called by each D2DCompositionService when a global lap completes in sequential IconZone mode.
    /// Recomputes the A* path with a new variation seed and broadcasts it to all players.
    /// </summary>
    private void OnSequentialLapCompleted(object? sender, int lapNum)
    {
        lock (_seqLapLock)
        {
            if (lapNum <= _lastGlobalLap) return;
            _lastGlobalLap = lapNum;
        }

        // F3a: when pattern + IconZone are combined, the pattern fills the desktop and zones
        // are used as a clip mask only — no A* path is needed, so skip the recompute/broadcast.
        if (_crossScreenConfig?.Animation.Pattern != null
            && _crossScreenConfig.Background.Mode == BackgroundMode.IconZone)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var iconService = new WaBiBaBuSy.Core.Services.Desktop.DesktopIconService();
                // GetIconPositions uses only P/Invoke (no STA COM) — safe on thread pool threads.
                var allIcons = iconService.GetIconPositions();
                var newLayout = WaBiBaBuSy.WallpaperEngine.Desktop.ZonePlanner.Compute(
                    allIcons.Select(i => (i.PixelX, i.PixelY)),
                    _seqCellW, _seqCellH,
                    _seqVirtualCanvasWidth, _seqVirtualH,
                    _seqPaletteHexes, _seqCorridorColorHex,
                    paddingPx: _seqPathPaddingPx,
                    visualPaddingPx: _seqVisualPaddingPx,
                    pathVariationSeed: lapNum);

                foreach (var (_, svc) in _d2dCompositionServices)
                    await svc.BroadcastNewPathAsync(newLayout.Path);

                Debug.WriteLine($"[SeqPath] Lap {lapNum}: broadcast {newLayout.Path.Count} waypoints");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SeqPath] Recompute failed for lap {lapNum}: {ex.Message}");
            }
        });
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

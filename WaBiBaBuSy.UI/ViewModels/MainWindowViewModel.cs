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
using WaBiBaBuSy.Models.Networking;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Native;
using WaBiBaBuSy.WallpaperEngine.Renderers;
using WaBiBaBuSy.WallpaperEngine.Services;
using WaBiBaBuSy.Core.Services.Desktop;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.UI.Services;

using WaBiBaBuSy.Models.Topology;

using WaBiBaBuSy.Models.Content;

using WaBiBaBuSy.UI.Controls;

namespace WaBiBaBuSy.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IRoomHost
{
    private readonly WaBiBaBuSyService _service;
    // The app's scene presets (the tray's store, so its Quick start menu follows every change)
    private readonly PresetStore _presets;
    private readonly System.Timers.Timer _refreshTimer;
    private readonly VideoThumbnailGenerator _thumbnailGenerator;
    // This machine's players, the running scene and the show: app-lifetime (owned by the tray, outlives this window)
    private readonly ServerPlaybackHost _server;
    // Set once the room re-marked the nodes of a scene that was already playing when this window opened
    private bool _liveMarksRestored;
    // The window closed: a refresh still in flight must not push this window's room into the host any more
    private volatile bool _detached;
    // Content cache manager for LRU eviction
    private ContentCacheManager? _cacheManager;
    // Debug flag: Enable/disable network topology debug output
    private static bool _enableNetworkTopologyDebugOutput = false;
    // Guard flag: prevents RefreshTopology from overwriting order during reorder operations
    private volatile bool _isReorderingInProgress;

    /// <summary>True while RoomView drags a tile (topology refresh paused); see <see cref="BeginNodeDrag"/>.</summary>
    private bool _nodeDragActive;

    [ObservableProperty]
    private ObservableCollection<ClientNodeViewModel> _clients = new();

    [ObservableProperty]
    private ObservableCollection<WallpaperItemViewModel> _wallpapers = new();

    [ObservableProperty]
    private int _clientCount = 0;

    [ObservableProperty]
    private ClientNodeViewModel? _selectedClient;

    /// <summary>The selected node becomes the Spotlight target.</summary>
    partial void OnSelectedClientChanged(ClientNodeViewModel? value) => Events.SelectSeat(value?.ClientId);

    /// <summary>The Events strip (one-shot show events).</summary>
    public ShowEventsViewModel Events { get; }

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

    /// <summary>True while a manual scene runs (a show never sets it).</summary>
    public bool IsCrossScreenRunning => _server.IsCrossScreenRunning;

    /// <summary>True once a scene was played (the toolbar Start replays it).</summary>
    public bool HasAnimationConfig => _server.LastScene != null;

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


    // ── Room / seat map (Tier 1.1) ──────────────────────────────────────────
    // The seat map breaks the ordered node chain into rows and decides how the path travels
    // between them (Ring/Snake/Parallel). Persisted in %APPDATA%\WaBiBaBuSy\seatmap.json.
    private SeatMap _seatMap = SeatMap.SingleRow();
    private bool _seatMapLoading;

    public SeatMap SeatMap => _seatMap;

    /// <summary>Bumped whenever the seat map changes so the topology view re-lays out its lanes.</summary>
    [ObservableProperty] private int _seatMapVersion;

    /// <summary>0 = Ring, 1 = Snake, 2 = Parallel (ComboBox order).</summary>
    [ObservableProperty] private int _roomTraversalIndex = 1;
    [ObservableProperty] private int _roomTurnGapCm = 150;
    [ObservableProperty] private int _roomRowGapCm = 120;
    [ObservableProperty] private bool _roomPhysicalUnits;
    /// <summary>0 = Center, 1 = Top, 2 = Bottom (ComboBox order).</summary>
    [ObservableProperty] private int _roomVerticalAnchorIndex;
    /// <summary>Row count for the "Split evenly" quick setup in the Room ⚙ popover.</summary>
    [ObservableProperty] private int _roomQuickRowCount = 2;

    /// <summary>
    /// Pixels per cm of the server's primary monitor — the reference DPI of a physical canvas.
    /// Cached (monitor DPI does not change at runtime). 0 when it cannot be queried.
    /// </summary>
    public float ReferencePixelsPerCm => LocalMonitors.ReferencePixelsPerCm;

    partial void OnRoomTraversalIndexChanged(int value) => UpdateRoomSettings();
    partial void OnRoomTurnGapCmChanged(int value) => UpdateRoomSettings();
    partial void OnRoomRowGapCmChanged(int value) => UpdateRoomSettings();
    partial void OnRoomPhysicalUnitsChanged(bool value)
    {
        UpdateRoomSettings();
        SceneEditor.UsePhysicalUnits = value;   // editor units follow the room
    }
    partial void OnRoomVerticalAnchorIndexChanged(int value) => UpdateRoomSettings();

    /// <summary>Short room description for the Room ⚙ button, e.g. "Ring · 150 cm turn · cm".</summary>
    public string RoomSummary
    {
        get
        {
            var parts = new List<string> { _seatMap.Traversal.ToString() };
            if (_seatMap.IsMultiRow) parts.Add($"{_seatMap.Rows.Count} rows · {_seatMap.TurnGapCm} cm turn");
            if (_seatMap.CanvasMode == CanvasMode.Physical)
                parts.Add(ReferencePixelsPerCm > 0f ? "cm" : "cm (reference DPI unknown!)");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Client ids in chain (topology) order — the input of every room edit.</summary>
    public IReadOnlyList<string> RoomOrder => Clients.OrderBy(c => c.Order).Select(c => c.ClientId).Distinct().ToList();

    private void LoadSeatMap()
    {
        _seatMapLoading = true;
        try
        {
            _seatMap = SeatMapStore.Load();
            RoomTraversalIndex = _seatMap.Traversal switch
            {
                TraversalMode.Ring => 0,
                TraversalMode.Parallel => 2,
                _ => 1
            };
            RoomTurnGapCm = _seatMap.TurnGapCm;
            RoomRowGapCm = _seatMap.RowGapCm;
            RoomPhysicalUnits = _seatMap.CanvasMode == CanvasMode.Physical;
            RoomVerticalAnchorIndex = _seatMap.VerticalAnchor switch { VerticalAnchor.Top => 1, VerticalAnchor.Bottom => 2, _ => 0 };
            RoomQuickRowCount = Math.Max(2, _seatMap.Rows.Count);
        }
        finally
        {
            _seatMapLoading = false;
        }
        OnPropertyChanged(nameof(SeatMap));
        OnPropertyChanged(nameof(RoomSummary));
    }

    /// <summary>Room-wide settings changed in the Room ⚙ popover: keep the rows, update the rest.</summary>
    private void UpdateRoomSettings()
    {
        if (_seatMapLoading) return;
        var map = SeatMapEditor.Clone(_seatMap);
        map.Traversal = RoomTraversalIndex switch { 0 => TraversalMode.Ring, 2 => TraversalMode.Parallel, _ => TraversalMode.Snake };
        map.TurnGapCm = Math.Max(0, RoomTurnGapCm);
        map.RowGapCm = Math.Max(0, RoomRowGapCm);
        map.CanvasMode = RoomPhysicalUnits ? CanvasMode.Physical : CanvasMode.Pixels;
        map.VerticalAnchor = RoomVerticalAnchorIndex switch { 1 => VerticalAnchor.Top, 2 => VerticalAnchor.Bottom, _ => VerticalAnchor.Center };
        CommitSeatMap(map);
    }

    /// <summary>Single write path for the seat map. Only user actions call this — a topology refresh never does.</summary>
    private void CommitSeatMap(SeatMap map)
    {
        _seatMap = map;
        SeatMapStore.Save(map);
        SyncRoom();
        SeatMapVersion++;
        OnPropertyChanged(nameof(SeatMap));
        OnPropertyChanged(nameof(RoomSummary));
    }

    /// <summary>Apply a room edit: new chain order onto the clients, then the seat map, then persist the order.</summary>
    public async Task ApplyRoomEditAsync(RoomEdit edit)
    {
        var byId = Clients.GroupBy(c => c.ClientId).ToDictionary(g => g.Key, g => g.First());
        for (int i = 0; i < edit.Order.Count; i++)
            if (byId.TryGetValue(edit.Order[i], out var client))
                client.Order = i;
        CommitSeatMap(edit.Map);
        await PersistClientOrderAsync();
    }

    [RelayCommand]
    private Task SplitRoomEvenly() => ApplyRoomEditAsync(SeatMapEditor.SplitEvenly(_seatMap, RoomOrder, RoomQuickRowCount));

    // ── Live preview of the running scene (Tier 2.1) ────────────────────────
    // Owned by the playback host: set when a scene or show item is applied, cleared when the players stop.
    public SeatMapLayoutResult? ActiveLayout => _server.ActiveLayout;
    public CrossScreenConfig? ActiveScene => _server.ActiveScene;
    public long ActiveSharedStartMs => _server.ActiveSharedStartMs;
    public int ActiveStartLeadMs => _server.ActiveStartLeadMs;
    /// <summary>Movement the players received (after cm→px).</summary>
    public MovementConfig? ActiveEffectiveMovement => _server.ActiveEffectiveMovement;
    /// <summary>Preview image for a layer's file (the gallery thumbnail for videos); the room view's resolver.</summary>
    public Func<string?, string?> SpriteImageResolver => ResolveSpriteImagePath;

    // ── Tier 1.3 show reliability ────────────────────────────────────────────

    /// <summary>One-line show health for the topology header.</summary>
    public string ShowHealthSummary
    {
        get
        {
            var remotes = Clients.Where(c => !IsLocalMonitor(c.ClientId)).ToList();
            if (remotes.Count == 0) return "";
            int connected = remotes.Count(c => c.IsConnected);
            int cached = remotes.Count(c => c.IsPrefetchComplete);
            int fetching = remotes.Count(c => c.PrefetchTotal > 0 && !c.IsPrefetchComplete);
            int stale = remotes.Count(c => c.DriftState == DriftState.Stale);
            var reporting = remotes.Where(c => c.DriftState is not DriftState.None and not DriftState.Stale).ToList();
            var parts = new List<string> { $"{connected}/{remotes.Count} remote online" };
            if (cached > 0 || fetching > 0) parts.Add(fetching > 0 ? $"{cached} cached · {fetching} fetching" : $"{cached} cached");
            if (stale > 0) parts.Add($"{stale} stale");
            if (reporting.Count > 0) parts.Add($"worst ping {reporting.Max(c => c.RttMs):F0} ms");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>"Now: Fish · next: Logo Rain in 0:42" while a show runs, else empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaylistNext))]
    private string _playlistNextLabel = "";
    public bool HasPlaylistNext => !string.IsNullOrEmpty(PlaylistNextLabel);

    private void UpdatePlaylistNextLabel()
    {
        // A non-looping show ends on its own: keep the tab's running flag honest.
        PlaylistEditor.IsShowRunning = _server.IsShowRunning;
        // The show ended (or was never started): the ▶ marker on the last-playing row is stale.
        if (!_server.IsShowRunning) PlaylistEditor.MarkPlaying(-1);
        PlaylistNextLabel = _server.ShowStatusLine;
    }

    [RelayCommand]
    private async Task ResyncAll()
    {
        var coordinator = _service.SyncCoordinator;
        if (coordinator == null) return;
        int n = await coordinator.ResyncAllAsync();
        Debug.WriteLine($"[Resync] re-sent active animation to {n} client(s)");
    }

    /// <summary>Preview image for a content path: the file itself, or the gallery thumbnail for videos.</summary>
    private string? ResolveSpriteImagePath(string? animationPath)
    {
        if (string.IsNullOrWhiteSpace(animationPath)) return null;
        var ext = Path.GetExtension(animationPath).ToLowerInvariant();
        if (ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".flv")
        {
            var item = Wallpapers.FirstOrDefault(w => string.Equals(w.FilePath, animationPath, StringComparison.OrdinalIgnoreCase));
            return item != null && !string.IsNullOrEmpty(item.ThumbnailPath) ? item.ThumbnailPath : null;
        }
        return animationPath;
    }

    /// <summary>
    /// Lay the given nodes out over the current seat map (topology order). Shared by the apply path,
    /// the topology lanes and the live preview so all three agree on where every node sits.
    /// </summary>
    public SeatMapLayoutResult BuildSeatLayout(IEnumerable<ClientNodeViewModel> clients)
    {
        var inputs = clients.OrderBy(c => c.Order).Select(c => new LayoutNodeInput
        {
            Id = c.ClientId,
            WidthPx = c.MonitorWidth > 0 ? c.MonitorWidth : 1920,
            HeightPx = c.MonitorHeight > 0 ? c.MonitorHeight : 1080,
            GapBeforeCm = c.PhysicalDistanceCm,
            PixelsPerCm = c.PixelsPerCm
        }).ToList();
        return SeatMapLayoutBuilder.Build(_seatMap, inputs, ReferencePixelsPerCm);
    }

    // Storage provider for file picker dialogs
    private IStorageProvider? _storageProvider;

    // Window reference for dialogs
    private Window? _mainWindow;

    public MainWindowViewModel(WaBiBaBuSyService service, ServerPlaybackHost server, PresetStore? presets = null)
    {
        _service = service;
        _presets = presets ?? new PresetStore();
        // Every player belongs to an app-lifetime host (this one or the tray's RemotePlaybackHost), so what
        // a closed window started keeps running and shows up here again. Prior-session orphans are killed
        // at app start. Detach() undoes these subscriptions when the window closes.
        _server = server;
        Events = new ShowEventsViewModel(server);
        _server.PropertyChanged += OnServerPropertyChanged;
        _server.SceneApplied += OnSceneApplied;
        _server.NodesStopped += OnNodesStopped;
        _server.ShowChanged += OnShowChanged;
        RefreshEventSeats();
        // Fields, not properties: loading must not re-send the flags to the players
        _debugOverlayEnabled = server.DebugOverlay.Enabled;
        _debugShowPath = server.DebugOverlay.ShowPath;
        _debugShowIconRects = server.DebugOverlay.ShowIconRects;
        _debugShowZoneBands = server.DebugOverlay.ShowZoneBands;
        _debugShowInfoPanel = server.DebugOverlay.ShowInfoPanel;

        _thumbnailGenerator = new VideoThumbnailGenerator(AppLogger.CreateLogger<VideoThumbnailGenerator>());

        // Load client config to pre-fill connect fields
        var clientConfig = ConfigurationManager.LoadClientConfiguration();
        ConnectServerAddress = clientConfig.ServerAddress;
        ConnectServerPort = clientConfig.ServerPort;

        // Subscribe to service events
        _service.ServerStatusChanged += OnServerStatusChanged;
        _service.ClientConnectionStatusChanged += OnClientConnectionStatusChanged;
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

        // Load wallpaper gallery from disk (the room preview takes video thumbnails from it)
        LoadWallpaperGallery();

        // Room layout (rows / ring) — single row until the user configures the room
        LoadSeatMap();

        // Docked Scene editor: default draft, units from the room, preview wiring
        InitSceneEditor();
        InitPresets();
        InitPlaylist();
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
            var needDetails = new List<WallpaperItemViewModel>();

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
                    DurationMs = item.DurationMs,
                    FrameCount = item.FrameCount,
                    FrameRate = item.FrameRate,
                    IsActive = item.IsActive
                };

                // Galleries saved before the metadata fields (or with "Unknown" resolution) are re-read once,
                // after the loop so a finished probe never saves a half-loaded gallery
                if (viewModel.NeedsMediaDetails)
                    needDetails.Add(viewModel);

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

            foreach (var vm in needDetails)
                ScheduleMediaDetails(vm);
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
                    DurationMs = vm.DurationMs,
                    FrameCount = vm.FrameCount,
                    FrameRate = vm.FrameRate,
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
    /// Reads resolution, length and frame data of a gallery item in the background
    /// (System.Drawing for images/GIFs, FFProbe for videos), then updates the item and saves the gallery.
    /// </summary>
    private void ScheduleMediaDetails(WallpaperItemViewModel item)
    {
        var path = item.FilePath;
        var type = item.Type;
        _ = Task.Run(async () =>
        {
            try
            {
                string? resolution = null;
                long durationMs = 0;
                int frameCount = 0;
                double frameRate = 0;

                if (type == WallpaperType.Video)
                {
                    var probe = await _thumbnailGenerator.ProbeVideo(path);
                    if (probe == null) return;
                    (resolution, durationMs, frameRate) = (probe.Resolution, probe.DurationMs, probe.FrameRate);
                }
                else if (type == WallpaperType.Gif)
                {
                    var gif = GifMetadataReader.Read(path);
                    if (gif == null) return;
                    (resolution, frameCount, durationMs) = gif.Value;
                }
                else
                {
                    using var image = System.Drawing.Image.FromFile(path);
                    resolution = $"{image.Width}x{image.Height}";
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!string.IsNullOrEmpty(resolution)) item.Resolution = resolution;
                    item.DurationMs = durationMs;
                    item.FrameCount = frameCount;
                    item.FrameRate = frameRate;
                    if (Wallpapers.Contains(item))
                        SaveWallpaperGallery();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not read media details for {path}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Set the storage provider for file picker dialogs (called from View)
    /// </summary>
    public void SetStorageProvider(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
        SceneEditor.SetStorageProvider(storageProvider);
        Events.SetStorageProvider(storageProvider);
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
        _service.ThumbnailsWanted = true; // clients resume thumbnail uploads with their next heartbeat
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
        _nodeDragActive = false;   // a drag cut short by the window closing must not restart the timer later
        _service.ThumbnailsWanted = false; // nobody looks: clients stop capturing + uploading
        _refreshTimer.Stop();
    }

    /// <summary>
    /// The window closed: stop refreshing and let go of the app-lifetime services, so this view model no
    /// longer reacts to them (or keeps itself alive through them). What plays keeps playing in the host.
    /// </summary>
    public void Detach()
    {
        _detached = true;
        Events.Detach();
        _server.ReleaseRoom(this);
        StopRefreshTimer();
        _refreshTimer.Elapsed -= OnRefreshTimerElapsed;
        _draftDebounce?.Stop();
        _server.PropertyChanged -= OnServerPropertyChanged;
        _server.SceneApplied -= OnSceneApplied;
        _server.NodesStopped -= OnNodesStopped;
        _server.ShowChanged -= OnShowChanged;
        _presets.Changed -= OnPresetsChanged;
        _presets.Layers.Changed -= OnPresetsChanged;
        _service.ServerStatusChanged -= OnServerStatusChanged;
        _service.ClientConnectionStatusChanged -= OnClientConnectionStatusChanged;
        _service.UpdateAvailable -= OnUpdateAvailableFromServer;
        _service.UpdateStatusChanged -= OnUpdateStatusChanged;
    }

    /// <summary>Hand the room (nodes, seat map, reference DPI) to the playback host; scenes are laid out over it.</summary>
    private void SyncRoom()
    {
        // Never an empty room (the window's first refresh has not run yet: keep what the last window pushed),
        // and never from a closed window (a newer one may already push its own).
        if (_detached || Clients.Count == 0) return;
        _server.UpdateRoom(this, RoomNodes(), _seatMap, ReferencePixelsPerCm);
    }

    /// <summary>Live one-shot events for the room preview.</summary>
    public IReadOnlyList<ShowEvent> RoomShowEvents => _server.ActiveShowEvents;

    /// <summary>Events strip: the room's seats (Spotlight follows the room selection) and the target picker.</summary>
    private void RefreshEventSeats()
    {
        var nodes = Clients.GroupBy(c => c.ClientId).Select(g => g.First()).OrderBy(c => c.Order).ToList();
        Events.RefreshSeats(nodes.Select(c => new SeatChoice(c.ClientId, $"#{c.Order}")).ToList());
        Events.SelectSeat(SelectedClient?.ClientId);
        Events.RefreshTargets(nodes.Select(c => (c.ClientId, $"#{c.Order} {c.DisplayName}")).ToList());
    }

    private void OnServerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ServerPlaybackHost.ActiveScene):
                OnPropertyChanged(nameof(ActiveScene));
                OnPropertyChanged(nameof(RoomScene));
                break;
            case nameof(ServerPlaybackHost.ActiveShowEvents):
                OnPropertyChanged(nameof(RoomShowEvents));
                break;
            case nameof(ServerPlaybackHost.ActiveSharedStartMs):
                OnPropertyChanged(nameof(ActiveSharedStartMs));
                OnPropertyChanged(nameof(RoomSharedStartMs));
                break;
            case nameof(ServerPlaybackHost.ActiveLayout):
                OnPropertyChanged(nameof(ActiveLayout));
                OnPropertyChanged(nameof(RoomLayout));
                break;
            case nameof(ServerPlaybackHost.ActiveStartLeadMs):
            case nameof(ServerPlaybackHost.ActiveEffectiveMovement):
            case nameof(ServerPlaybackHost.HasActiveRenderer):
            case nameof(ServerPlaybackHost.TestRunStatusText):
                OnPropertyChanged(e.PropertyName);
                break;
            case nameof(ServerPlaybackHost.IsCrossScreenRunning):
                OnPropertyChanged(nameof(IsCrossScreenRunning));
                OnPropertyChanged(nameof(IsAnythingPlaying));
                StartCrossScreenCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ServerPlaybackHost.LastScene):
                OnPropertyChanged(nameof(HasAnimationConfig));
                StartCrossScreenCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ServerPlaybackHost.IsStartingScene):
                OnPropertyChanged(nameof(IsStartingScene));
                StartCrossScreenCommand.NotifyCanExecuteChanged();
                PlayOnSelectionCommand.NotifyCanExecuteChanged();
                PlayOnAllCommand.NotifyCanExecuteChanged();
                PlayPlainOnSelectionCommand.NotifyCanExecuteChanged();
                PlayPlainOnAllCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    /// <summary>A scene or show item was applied: its targets animate in the room.</summary>
    private void OnSceneApplied(CrossScreenConfig config) => MarkAnimationTargets(config);

    /// <summary>Nodes stopped (null = all): drop their animation markers, and with Clear their preview too.</summary>
    private void OnNodesStopped(IReadOnlyCollection<string>? nodeIds, bool clearPreview)
    {
        var ids = nodeIds == null ? null : new HashSet<string>(nodeIds);
        foreach (var client in Clients)
        {
            if (ids != null && !ids.Contains(client.ClientId)) continue;
            client.IsAnimating = false;
            client.IsCurrentAnimationTarget = false;
            client.ActiveAnimationName = null;
            if (!clearPreview) continue;
            client.ThumbnailImage = null;
            client.CurrentWallpaper = null;
        }
    }

    /// <summary>The show started, stopped or changed item: toolbar line, Playlist tab flag and ▶ row.</summary>
    private void OnShowChanged()
    {
        UpdatePlaylistNextLabel();
        if (_server.IsShowRunning) PlaylistEditor.MarkPlaying(_server.ShowItemIndex);
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
    public bool HasActiveRenderer => _server.HasActiveRenderer;

    partial void OnSelectedWallpaperChanged(WallpaperItemViewModel? value)
    {
        ApplyWallpaperToSelectedCommand.NotifyCanExecuteChanged();
        ApplyWallpaperViaDirect2DCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Mark the nodes a scene targets as animating (empty selection = every node) and all others
    /// as idle. The room only paints the live sprite on animating tiles.
    /// </summary>
    private void MarkAnimationTargets(CrossScreenConfig? config)
    {
        var animFileName = Path.GetFileName(config?.FirstLayer?.Animation.AnimationPath ?? string.Empty);
        var selectedIds = new HashSet<string>(config?.SelectedMonitorIds ?? new List<string>());
        foreach (var client in Clients)
        {
            bool target = selectedIds.Count == 0 || selectedIds.Contains(client.ClientId);
            client.IsAnimating = target;
            client.ActiveAnimationName = target ? animFileName : null;
        }
    }

    /// <summary>
    /// Notify CanExecute and the selection bar when client selection changes.
    /// Called when a client's IsSelected property changes and after every topology update.
    /// </summary>
    private void NotifyClientSelectionCommands()
    {
        ApplyWallpaperToSelectedCommand.NotifyCanExecuteChanged();
        ApplyWallpaperViaDirect2DCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedNodeCount));
        OnPropertyChanged(nameof(HasNodeSelection));
        OnPropertyChanged(nameof(IsSingleNodeSelection));
        OnPropertyChanged(nameof(IsMultiNodeSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(TargetsSummary));
        PlayOnSelectionCommand.NotifyCanExecuteChanged();
        PlayPlainOnSelectionCommand.NotifyCanExecuteChanged();
        ScheduleDraftUpdate();   // draft targets = selection; also covers nodes joining/leaving (called after every refresh)
    }

    // ── Selection bar ────────────────────────────────────────────────────────
    /// <summary>Number of selected room nodes.</summary>
    public int SelectedNodeCount => Clients.Count(c => c.IsSelected);
    /// <summary>True while at least one node is selected (shows the selection bar).</summary>
    public bool HasNodeSelection => SelectedNodeCount > 0;
    /// <summary>True when exactly one node is selected.</summary>
    public bool IsSingleNodeSelection => SelectedNodeCount == 1;
    /// <summary>True when two or more nodes are selected.</summary>
    public bool IsMultiNodeSelection => SelectedNodeCount > 1;

    /// <summary>"SEEPC - Monitor 1" for one node, "3 selected" for several.</summary>
    public string SelectionSummary => SelectedNodeCount == 1
        ? Clients.First(c => c.IsSelected).DisplayName
        : $"{SelectedNodeCount} selected";

    private IReadOnlyCollection<string> SelectedNodeIds => Clients.Where(c => c.IsSelected).Select(c => c.ClientId).ToList();

    [RelayCommand]
    private Task ClearSelectedNodes() => ClearNodesAsync(SelectedNodeIds);

    [RelayCommand]
    private Task ResyncSelectedNodes() => ResyncNodesAsync(SelectedNodeIds);

    [RelayCommand]
    private Task MakeRowFromSelection() => ApplyRoomEditAsync(SeatMapEditor.MakeRowFromSelection(_seatMap, RoomOrder, SelectedNodeIds));

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var c in Clients) c.IsSelected = false;
        SelectedClient = null;
    }

    [RelayCommand]
    private void OpenSettings() => Views.SettingsWindow.ShowSingle();

    /// <summary>The gallery's "Get content ▾" menu, built from <see cref="ContentLinks"/>.</summary>
    public IReadOnlyList<ContentMenuEntry> ContentMenuEntries => _contentMenuEntries ??= ContentLinks.Groups
        .SelectMany(g => new[] { new ContentMenuEntry($"— {g.Title} —", null, null) }
            .Concat(g.Links.Select(l => new ContentMenuEntry(l.Name, l.Tip, OpenLinkCommand))))
        .Append(new ContentMenuEntry("💡 " + ContentLinks.Tip, null, null))
        .ToList();
    private IReadOnlyList<ContentMenuEntry>? _contentMenuEntries;

    /// <summary>Open a content link in the default browser.</summary>
    [RelayCommand]
    private void OpenLink(string? header)
    {
        var url = ContentLinks.Groups.SelectMany(g => g.Links).FirstOrDefault(l => l.Name == header)?.Url;
        if (url == null) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.CreateLogger<MainWindowViewModel>().LogWarning(ex, "Could not open {Url}", url); }
    }

    [RelayCommand]
    private void OpenLogFolder() => Process.Start(new ProcessStartInfo
    {
        FileName = WaBiBaBuSy.Common.PathHelper.GetLogsPath(),
        UseShellExecute = true
    });

    /// <summary>
    /// Stop what the given nodes show (local players, remote cross-screen Stop). A running playlist puts
    /// them back on its next item — use the toolbar Stop to end a show.
    /// </summary>
    public Task ClearNodesAsync(IReadOnlyCollection<string> nodeIds) => _server.ClearNodesAsync(nodeIds);

    /// <summary>Forget an offline seat (and the rest of its machine); the ring closes on the next refresh.</summary>
    public Task ForgetSeatAsync(string nodeId)
    {
        if (_service.ForgetSeat(nodeId)) RefreshTopology();
        return Task.CompletedTask;
    }

    /// <summary>A ghost seat (offline machine, see RoomNodeSources) and since when it is offline.</summary>
    private static (bool IsGhost, DateTime? Since) GhostState(WaBiBaBuSy.Grpc.ConnectedClient c) =>
        c.Status == WaBiBaBuSy.Grpc.ClientStatusEnum.ClientDisconnected
            ? (true, DateTimeOffset.FromUnixTimeSeconds(c.LastHeartbeat).UtcDateTime)
            : (false, null);

    /// <summary>Re-send the running animation to the given remote nodes (local monitors are in-process and need no resync).</summary>
    public async Task ResyncNodesAsync(IReadOnlyCollection<string> nodeIds)
    {
        var coordinator = _service.SyncCoordinator;
        if (coordinator == null) return;
        int sent = 0;
        foreach (var id in nodeIds.Where(id => !IsLocalMonitor(id)))
            if (await coordinator.ResyncClientAsync(id)) sent++;
        Debug.WriteLine($"[Resync] re-sent the active animation to {sent} of {nodeIds.Count} selected node(s)");
    }

    // ── IRoomHost (RoomView) ─────────────────────────────────────────────────
    private ClientNodeViewModel? FindClient(string id) => Clients.FirstOrDefault(c => c.ClientId == id);

    public void SelectOnly(string nodeId)
    {
        foreach (var c in Clients) c.IsSelected = c.ClientId == nodeId;
        SelectedClient = FindClient(nodeId);
    }

    public void ToggleSelection(string nodeId)
    {
        var c = FindClient(nodeId);
        if (c != null) c.IsSelected = !c.IsSelected;
    }

    public void AddToSelection(string nodeId)
    {
        var c = FindClient(nodeId);
        if (c != null) c.IsSelected = true;
    }

    public void SetRubberBandSelection(IReadOnlyCollection<string> nodeIds, bool additive)
    {
        var set = new HashSet<string>(nodeIds);
        foreach (var c in Clients)
        {
            if (set.Contains(c.ClientId)) c.IsSelected = true;
            else if (!additive) c.IsSelected = false;
        }
    }

    public void ClearSelection() => DeselectAll();

    /// <inheritdoc />
    public void BeginNodeDrag()
    {
        if (_nodeDragActive) return;
        _nodeDragActive = true;
        _refreshTimer.Stop();
    }

    /// <inheritdoc />
    public void EndNodeDrag()
    {
        if (!_nodeDragActive) return;   // never paused, or the window closed meanwhile
        _nodeDragActive = false;
        StartRefreshTimer();
    }

    public Task MoveNodeAsync(string nodeId, int row, int indexInRow)
        => ApplyRoomEditAsync(SeatMapEditor.MoveNode(_seatMap, RoomOrder, nodeId, row, indexInRow));
    public Task SplitRowAtAsync(string nodeId)
        => ApplyRoomEditAsync(SeatMapEditor.SplitRowAt(_seatMap, RoomOrder, nodeId));
    public Task MergeRowIntoPreviousAsync(int row)
        => ApplyRoomEditAsync(SeatMapEditor.MergeRowIntoPrevious(_seatMap, RoomOrder, row));
    public Task DeleteRowAsync(int row)
        => ApplyRoomEditAsync(SeatMapEditor.DeleteRow(_seatMap, RoomOrder, row));
    public Task MoveRowAsync(int row, int delta)
        => ApplyRoomEditAsync(SeatMapEditor.MoveRow(_seatMap, RoomOrder, row, delta));
    public Task RenameRowAsync(int row, string name)
        => ApplyRoomEditAsync(SeatMapEditor.RenameRow(_seatMap, RoomOrder, row, name));

    public Task ToggleRowFacingAsync(int row)
    {
        var current = row < _seatMap.Rows.Count ? _seatMap.Rows[row].Orientation : RowOrientation.Facing;
        var next = current == RowOrientation.Facing ? RowOrientation.SameSide : RowOrientation.Facing;
        return ApplyRoomEditAsync(SeatMapEditor.SetRowOrientation(_seatMap, RoomOrder, row, next));
    }

    public async Task SetGapCmAsync(string nodeId, int cm)
    {
        var c = FindClient(nodeId);
        if (c == null) return;
        c.PhysicalDistanceCm = SeatMapEditor.NormalizeGapCm(cm);
        _server.SessionGapCm[nodeId] = c.PhysicalDistanceCm;
        SyncRoom();
        await UpdateClientDistance(c);
        SeatMapVersion++;
    }

    public IReadOnlyList<string> TagsOf(string nodeId) => _server.Tags.Of(nodeId);

    public Task SetTagsAsync(IReadOnlyCollection<string> nodeIds, string tags)
    {
        _server.Tags.Set(nodeIds, NodeTagStore.Parse(tags));
        RefreshEventSeats();   // the target picker lists the tags
        SeatMapVersion++;      // tiles show their tags
        return _server.RefreshAgendaAsync();   // an "agenda" tag makes a seat a full-size agenda screen
    }

    public Task ShowNodeLogsAsync(string nodeId)
    {
        SelectOnly(nodeId);
        return FetchClientLogs();
    }

    public bool CanFetchLogs(string nodeId) => IsServerMode && !IsLocalMonitor(nodeId);

    public string? NodeWarning(string nodeId)
    {
        if (!IsLocalMonitor(nodeId))
        {
            var version = _service.GetConnectedClients().FirstOrDefault(c => c.ClientId == nodeId)?.AppVersion;
            return version == null || WaBiBaBuSy.Common.Version.VersionInfo.SupportsSceneLayers(version)
                ? null
                : $"Runs v{version} — needs update to {WaBiBaBuSy.Common.Version.VersionInfo.MinSceneLayersVersion}+ before it plays scenes.";
        }
        var local = Clients.Where(c => IsLocalMonitor(c.ClientId) && c.MonitorRefreshHz > 0).ToList();
        if (local.Select(c => c.MonitorRefreshHz).Distinct().Count() < 2) return null;
        var me = local.FirstOrDefault(c => c.ClientId == nodeId);
        int max = local.Max(c => c.MonitorRefreshHz);
        return me != null && me.MonitorRefreshHz < max
            ? $"Runs at {me.MonitorRefreshHz} Hz while another local monitor runs at {max} Hz — this panel may tear. Match refresh rates in Windows Display settings."
            : null;
    }

    /// <summary>
    /// Check if a client ID represents a local monitor (either local-only or server mode)
    /// </summary>
    private static bool IsLocalMonitor(string clientId) => NodeIds.IsLocalMonitor(clientId);

    /// <summary>
    /// Extract monitor index from a local monitor client ID
    /// </summary>
    private static int GetMonitorIndex(string clientId) => NodeIds.MonitorIndex(clientId);

    /// <summary>The room's nodes as plain records (scene planning, test node list).</summary>
    private List<RoomNode> RoomNodes() => Clients.Select(c => new RoomNode
    {
        Id = c.ClientId,
        Hostname = c.Hostname,
        Order = c.Order,
        MonitorIndex = c.MonitorIndex,
        WidthPx = c.MonitorWidth,
        HeightPx = c.MonitorHeight,
        RefreshHz = c.MonitorRefreshHz,
        GapBeforeCm = c.PhysicalDistanceCm,
        PixelsPerCm = c.PixelsPerCm,
        IsConnected = c.IsConnected,
        IsGhost = c.IsGhost,
        RttMs = c.RttMs,
        ClockOffsetMs = c.DriftMs
    }).ToList();

    private bool CanApplyWallpaperToSelected() =>
        SelectedWallpaper != null && Clients.Any(c => c.IsSelected);

    private bool CanApplyWallpaperViaDirect2D() =>
        SelectedWallpaper != null && Clients.Any(c => c.IsSelected);

    private bool CanStartCrossScreen() =>
        HasAnimationConfig && !IsCrossScreenRunning && !IsStartingScene;

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

    private Task SendDebugOverlayFlags() => _server.SetDebugOverlayAsync(new DebugOverlayFlags(
        DebugOverlayEnabled, DebugShowPath, DebugShowIconRects, DebugShowZoneBands, DebugShowInfoPanel));

    /// <summary>Toolbar "Clear all": stop the show and the scene and every player on this machine.</summary>
    [RelayCommand]
    private async Task ClearAllWallpapers()
    {
        await _server.WaitForStartAsync();   // a start in flight would otherwise come up after the clear
        await _server.ClearAllAsync();
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
                    await _server.ApplyLocalWallpaperAsync(SelectedWallpaper.FilePath, SelectedWallpaper.Name, GetMonitorIndex(client.ClientId));
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
                    await _server.ApplyLocalD2DAsync(SelectedWallpaper.FilePath, SelectedWallpaper.Name, monitorIndex,
                        D2dBackgroundColor, SelectedFitModeIndex);
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
                await _server.ApplyLocalWallpaperAsync(wallpaper.FilePath, wallpaper.Name, monitorIndex);
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

            // Shown from this call's own reply, not the ClientLogsReceived event: test runs fetch logs too
            // and must not pop this panel open.
            var logs = await _service.FetchClientLogsAsync(selectedRemote.ClientId, 0, 0, TimeSpan.FromSeconds(10));
            if (logs != null)
                RemoteClientLogs = $"=== Logs from {selectedRemote.Hostname} ===\n{logs.LogContent}";
            else
                // The remote's LogDirectory is its own setting; the server only knows the default.
                RemoteClientLogs = $"No reply from {selectedRemote.Hostname} within 10 s. Is it connected? Its logs are in its configured log folder on that machine (default %LOCALAPPDATA%\\WaBiBaBuSy\\Logs).";
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

            // Video length / GIF frames (and a video's resolution) are read in the background
            if (wallpaper.NeedsMediaDetails)
                ScheduleMediaDetails(wallpaper);

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

        var address = ConnectServerAddress.Trim();
        var port = ConnectServerPort;
        IsConnecting = true;
        ClientConnectionStatus = string.IsNullOrEmpty(address) ? "Discovering..." : $"Connecting to {address}...";

        try
        {
            // A typed address is used as-is; an empty field auto-discovers (mDNS) and falls back to localhost
            // The render delegates are registered once at app start (RemotePlaybackHost), for every connect path.
            bool connected = string.IsNullOrEmpty(address)
                ? await _service.DiscoverAndConnectAsync("localhost", port)
                : await _service.ConnectToServerAsync(address, port);

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

    /// <summary>
    /// Open the mDNS server browser; on selection, fill address/port and connect.
    /// </summary>
    [RelayCommand]
    private async Task FindServers()
    {
        if (_mainWindow == null) return;

        var viewModel = new ServerBrowserViewModel();
        try
        {
            var dialog = new Views.ServerBrowserDialog { DataContext = viewModel };
            viewModel.SetCloseAction(() => dialog.Close());

            await dialog.ShowDialog(_mainWindow);

            if (viewModel.DialogResult && viewModel.SelectedServer != null)
            {
                ConnectServerAddress = viewModel.SelectedServer.ConnectAddress;
                if (viewModel.SelectedServer.Port > 0)
                    ConnectServerPort = viewModel.SelectedServer.Port;
                await ConnectToServer();
            }
        }
        finally
        {
            viewModel.Dispose();
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
    /// Refresh client topology from server OR show local-only mode
    /// </summary>
    public async void RefreshTopology()
    {
        // Don't overwrite order while a reorder operation is in flight
        if (_isReorderingInProgress) return;
        if (System.Threading.Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0) return;

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

                // The server's local monitors first, then the clients (multi-monitor clients expanded)
                var allNodes = RoomNodeSources.ServerNodes(LocalMonitors.Snapshot(), connectedClients,
                    _service.GetServerLocalMonitorOrder, _service.GetServerLocalMonitorDistance, _server.SessionGapCm, _service.GetSeats());

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
                                var expandedId = $"{client.ClientId}_MONITOR_{m}";
                                expandedNodes.Add(new WaBiBaBuSy.Grpc.ConnectedClient
                                {
                                    ClientId = expandedId,
                                    Hostname = $"{client.Hostname} - Monitor {m + 1}",
                                    IpAddress = client.IpAddress,
                                    Status = client.Status,
                                    OrderPosition = nextOrder++,
                                    // The server topology only reports the base client's gap; keep this session's edit.
                                    PhysicalDistanceCm = _server.SessionGapCm.TryGetValue(expandedId, out var gap) ? gap : client.PhysicalDistanceCm,
                                    ScreenConfig = client.ScreenConfig,
                                    ClockOffsetMs = client.ClockOffsetMs,
                                    RttMs = client.RttMs,
                                    LastDriftReportUtc = client.LastDriftReportUtc
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
                                ScreenConfig = client.ScreenConfig,
                                ClockOffsetMs = client.ClockOffsetMs,
                                RttMs = client.RttMs,
                                LastDriftReportUtc = client.LastDriftReportUtc
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
        finally
        {
            System.Threading.Interlocked.Exchange(ref _refreshRunning, 0);
        }
    }

    // The 2 s timer fires on the thread pool: a slow client-mode GetTopologyAsync let refreshes overlap and an older
    // topology overwrite a newer one (review 2026-10-13, part 5). A tick that finds one running is skipped.
    private int _refreshRunning;

    /// <summary>
    /// Show local machine node(s) in local-only mode (when not connected to server/client)
    /// Creates one node per monitor for multi-monitor support
    /// </summary>
    private void ShowLocalMachineNode()
    {
        var nodes = RoomNodeSources.StandaloneNodes(LocalMonitors.Snapshot(), _server.SessionGapCm);
        if (_enableNetworkTopologyDebugOutput)
            Debug.WriteLine($"[ShowLocalMachineNode] {nodes.Count} monitor node(s)");
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
            if (_detached) return;   // a refresh that was in flight when the window closed

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
                    (existing.IsGhost, existing.OfflineSinceUtc) = GhostState(grpcClient);
                    existing.Order = grpcClient.OrderPosition;
                    existing.PhysicalDistanceCm = grpcClient.PhysicalDistanceCm;
                    existing.DriftMs = grpcClient.ClockOffsetMs;
                    existing.RttMs = grpcClient.RttMs;
                    existing.PrefetchReady = grpcClient.PrefetchReady;
                    existing.PrefetchTotal = grpcClient.PrefetchTotal;
                    existing.DriftState = DriftMonitor.Classify(
                        DriftMonitor.SyncErrorMs(grpcClient.RttMs),
                        grpcClient.LastDriftReportUtc,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

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

                    // Monitor info for this node (LOCAL_MACHINE_MONITOR_X, SERVER_LOCALHOST_MONITOR_X, ClientId_MONITOR_X, single-monitor client)
                    var (monitorIndex, monitorInfo) = RoomNodeSources.MonitorFor(grpcClient);
                    string? monitorName = monitorInfo?.DeviceName;
                    int monitorWidth = monitorInfo?.Width ?? 0;
                    int monitorHeight = monitorInfo?.Height ?? 0;
                    bool isPrimary = monitorInfo?.IsPrimary ?? false;
                    int monitorRefreshHz = monitorInfo?.RefreshRate ?? 0;
                    float monitorPixelsPerCm = monitorInfo?.PixelsPerCm ?? 0f;

                    var newClient = new ClientNodeViewModel
                    {
                        ClientId = grpcClient.ClientId,
                        Hostname = grpcClient.Hostname,
                        IpAddress = grpcClient.IpAddress,
                        IsConnected = grpcClient.Status == WaBiBaBuSy.Grpc.ClientStatusEnum.ClientConnected ||
                                     grpcClient.Status == WaBiBaBuSy.Grpc.ClientStatusEnum.ClientPlaying,
                        Status = grpcClient.Status.ToString(),
                        IsGhost = GhostState(grpcClient).IsGhost,
                        OfflineSinceUtc = GhostState(grpcClient).Since,
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
                        PixelsPerCm = monitorPixelsPerCm,
                        // Drift telemetry (server-local SERVER_LOCALHOST_MONITOR_*/LOCAL_MACHINE_MONITOR_*
                        // nodes never report → LastDriftReportUtc stays 0 → DriftState.None → label hidden)
                        DriftMs = grpcClient.ClockOffsetMs,
                        RttMs = grpcClient.RttMs,
                        PrefetchReady = grpcClient.PrefetchReady,
                        PrefetchTotal = grpcClient.PrefetchTotal,
                        DriftState = DriftMonitor.Classify(
                            DriftMonitor.SyncErrorMs(grpcClient.RttMs),
                            grpcClient.LastDriftReportUtc,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
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

            AssignGroupColors();
            SyncRoom();

            // A scene or show item already playing when this window opened: mark its nodes once.
            if (!_liveMarksRestored && Clients.Count > 0)
            {
                _liveMarksRestored = true;
                if (_server.ActiveScene is { } live) MarkAnimationTargets(live);
            }

            OnPropertyChanged(nameof(ShowHealthSummary));
            NotifyClientSelectionCommands();
            UpdatePlaylistNextLabel();
            RefreshEventSeats();   // seat labels for Spotlight and the target picker

            ClientCount = Clients.Count;
            ConnectedClientCount = Clients.Count(c => c.IsConnected
                && !c.ClientId.StartsWith("SERVER_LOCALHOST_MONITOR_")
                && !c.ClientId.StartsWith("LOCAL_MACHINE_MONITOR_"));
            Debug.WriteLine($"[UpdateClientList] Complete. Final client count: {Clients.Count}");
            Debug.WriteLine($"[UpdateClientList] Clients in collection: {string.Join(", ", Clients.Select(c => c.Hostname))}");
        });
    }

    // Palette of accent colors for multi-monitor machine groups.
    private static readonly Avalonia.Media.Color[] GroupColorPalette =
    [
        Avalonia.Media.Color.Parse("#E8A020"), // amber
        Avalonia.Media.Color.Parse("#3BAA6B"), // green
        Avalonia.Media.Color.Parse("#A04DC8"), // purple
        Avalonia.Media.Color.Parse("#20A8C8"), // teal
        Avalonia.Media.Color.Parse("#E05050"), // red
        Avalonia.Media.Color.Parse("#D07030"), // orange
    ];

    /// <summary>
    /// Assign a distinct accent color to every node that shares a physical machine with
    /// at least one other node. Single-monitor nodes get GroupColor = null.
    /// </summary>
    private void AssignGroupColors()
    {
        // Clear all group colors first
        foreach (var c in Clients)
            c.GroupColor = null;

        var groups = Clients
            .GroupBy(c => GetBaseMachineId(c.ClientId))
            .Where(g => g.Count() > 1)
            .ToList();

        for (int i = 0; i < groups.Count; i++)
        {
            var color = GroupColorPalette[i % GroupColorPalette.Length];
            foreach (var node in groups[i])
                node.GroupColor = color;
        }
    }

    /// <summary>
    /// Extract the base machine ID from a client ID (strips _MONITOR_N suffix).
    /// Two nodes with the same base ID are on the same physical machine.
    /// </summary>
    private static string GetBaseMachineId(string clientId)
    {
        var idx = clientId.LastIndexOf("_MONITOR_");
        return idx >= 0 ? clientId[..idx] : clientId;
    }

    /// <summary>
    /// Update the thumbnail image for a client node.
    /// For remote clients: reads from server gRPC thumbnail cache.
    /// For local monitors: captures directly via ThumbnailCaptureService.
    /// </summary>
    private readonly Dictionary<string, byte[]> _lastThumbnailBytes = new();
    private readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap> _retiredThumbnails = new();

    private void UpdateClientThumbnail(ClientNodeViewModel client)
    {
        try
        {
            byte[]? jpegBytes = null;

            if (client.ClientId.StartsWith("LOCAL_MACHINE_MONITOR_"))
            {
                // Local monitor: capture thumbnail directly
                jpegBytes = _server.CaptureLocalThumbnail(GetMonitorIndex(client.ClientId));
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
            // Unchanged picture (idle / static scene): keep the bitmap instead of decoding a new one every 2 s.
            if (_lastThumbnailBytes.TryGetValue(client.ClientId, out var last) && last.AsSpan().SequenceEqual(jpegBytes))
                return;
            _lastThumbnailBytes[client.ClientId] = jpegBytes;

            using var ms = new MemoryStream(jpegBytes);
            var replaced = client.ThumbnailImage;
            client.ThumbnailImage = new Avalonia.Media.Imaging.Bitmap(ms);
            // Review 2026-10-13 E17: replaced thumbnails were never disposed (native Skia memory, ~25/s with 50 nodes).
            // Dispose one refresh late, so a frame still drawing the previous bitmap never meets a disposed one.
            // ponytail: one-refresh grace instead of render-thread ownership tracking.
            if (_retiredThumbnails.Remove(client.ClientId, out var older)) older.Dispose();
            if (replaced != null) _retiredThumbnails[client.ClientId] = replaced;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateClientThumbnail] Error for {client.ClientId}: {ex.Message}");
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

    #region Cross-Screen Commands

    /// <summary>Toolbar Start: replay the last scene, or open the Scene editor when there is none.</summary>
    [RelayCommand(CanExecute = nameof(CanStartCrossScreen))]
    private async Task StartCrossScreen()
    {
        var scene = _server.LastScene;
        if (scene == null || string.IsNullOrEmpty(scene.FirstLayer?.Animation.AnimationPath))
        {
            Debug.WriteLine("[CrossScreen] No configuration — opening the Scene editor");
            OpenSceneEditor();
            return;
        }
        // Same Play guard as ▶ Play: a double click, or Start during a Play, would run two applies at once.
        if (!_server.TryBeginStart()) return;
        try
        {
            SyncRoom();
            await _server.StartSceneAsync(scene);
        }
        finally
        {
            _server.EndStart();
        }
    }

    /// <summary>Toolbar ■: stop the show and the running scene.</summary>
    [RelayCommand]
    private async Task StopCrossScreen()
    {
        await _server.WaitForStartAsync();   // stopping before the start registered its players would miss them
        await _server.StopAsync();
    }

    /// <summary>Start rotating the given playlist across all nodes (Playlist tab ▶).</summary>
    public void StartPlaylist(WaBiBaBuSy.Models.Wallpaper.Playlist playlist)
    {
        SyncRoom();
        _server.StartShow(playlist);
    }

    /// <summary>Stop the running show (if any).</summary>
    public Task StopPlaylistAsync() => _server.StopShowAsync();

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

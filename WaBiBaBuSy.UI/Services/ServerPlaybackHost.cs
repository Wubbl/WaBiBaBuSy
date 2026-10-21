using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Core.Services.Show;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Native;
using WaBiBaBuSy.WallpaperEngine.Renderers;
using WaBiBaBuSy.WallpaperEngine.Show;
using WaBiBaBuSy.WallpaperEngine.Services;
using AppVersionInfo = WaBiBaBuSy.Common.Version.VersionInfo;

namespace WaBiBaBuSy.UI.Services;

/// <summary>Debug overlay switches sent to the server's players (defaults match the player's).</summary>
public readonly record struct DebugOverlayFlags(bool Enabled, bool ShowPath, bool ShowIconRects, bool ShowZoneBands, bool ShowInfoPanel)
{
    public static DebugOverlayFlags Default => new(false, true, true, false, true);
}

/// <summary>
/// Server (and standalone) mode: this machine's players, the running scene and the playlist show. App-lifetime
/// (created once by the tray), so closing the control panel leaves what plays running and tracked: the next
/// window shows it and Stop / Clear all / ⏭ / Resync work on it, and test runs keep their host. Before
/// 2026-10-03 all of this lived in the main window's view model, and reopening the window killed the players
/// it could no longer see. The view model is a view over this host; it pushes the room (nodes, seat map) with
/// <see cref="UpdateRoom"/>, and while the window is closed scenes and show items use the last room it pushed;
/// tray Quick start rebuilds the room from its sources (<see cref="EnsureRoom"/>).
/// Every member runs on the UI thread unless noted.
/// </summary>
public sealed partial class ServerPlaybackHost : ObservableObject
{
    private readonly WaBiBaBuSyService _service;
    private readonly DesktopWindowManager _desktopManager;
    private readonly RemotePlaybackHost _remotePlayback;
    // Local players by monitor index (thread-safe for gRPC callbacks): D2D for scenes and plain D2D, LibVLC for plain wallpapers
    private readonly ConcurrentDictionary<int, D2DCompositionService> _players = new();
    private readonly ConcurrentDictionary<int, IWallpaperRenderer> _renderers = new();
    /// <summary>Players of the previous scene / show item, kept until the new ones appear (standby switch).</summary>
    private readonly RetiringPlayers _retiring = new();
    // Thumbnail capture per monitor for the room's live tiles
    private readonly ConcurrentDictionary<int, ThumbnailCaptureService> _thumbnailCaptureServices = new();
    // Pauses the players of a monitor a fullscreen app covers (created and started with the first local player)
    private FullscreenDetectionService? _fullscreenDetection;
    private volatile bool _pauseOnlyFullscreenMonitor;
    // Legacy renderers paused by the detection loop (its thread only; Stop clears it once the loop is gone)
    private readonly HashSet<IWallpaperRenderer> _pausedRenderers = new();
    private PlaylistOrchestrator? _playlistOrchestrator;
    private string? _crossScreenContentId;         // ContentId sent to remote clients at start (used for stop)

    // The room as the control panel last pushed it (UpdateRoom), or as EnsureRoom built it
    private volatile IReadOnlyList<RoomNode> _nodes = Array.Empty<RoomNode>();
    private SeatMap _seatMap = SeatMap.SingleRow();
    private float _referencePixelsPerCm;
    // The window that last pushed the room; null = no open window keeps it current (EnsureRoom rebuilds it).
    private object? _roomOwner;

    // Sequential IconZone lap-recompute state
    private int _lastGlobalLap = -1;
    private readonly object _seqLapLock = new();
    private int _seqCellW, _seqCellH, _seqVirtualCanvasWidth, _seqVirtualH;
    private int _seqPathPaddingPx, _seqVisualPaddingPx;
    private List<string> _seqPaletteHexes = new();
    private string _seqCorridorColorHex = "#1E1E1E";

    /// <summary>True while a manual scene runs (a show never sets it).</summary>
    [ObservableProperty] private bool _isCrossScreenRunning;

    /// <summary>
    /// True while a Play request is in flight (teardown of any running show/scene → start). Guards the Play
    /// buttons, the show ▶ and the test entry points against starting a second scene before the first has
    /// finished applying, which would leave its players / remote nodes running untracked.
    /// </summary>
    [ObservableProperty] private bool _isStartingScene;

    /// <summary>The scene last started by Play / Start (the toolbar Start replays it), or null.</summary>
    [ObservableProperty] private CrossScreenConfig? _lastScene;

    // ── Live state of what plays (scene or show item); set by ApplyCrossScreenConfigAsync, cleared when the players stop ──
    [ObservableProperty] private SeatMapLayoutResult? _activeLayout;
    [ObservableProperty] private CrossScreenConfig? _activeScene;
    [ObservableProperty] private long _activeSharedStartMs;
    [ObservableProperty] private int _activeStartLeadMs;
    /// <summary>Movement the players received (after cm→px); test runs compute expected positions from it.</summary>
    [ObservableProperty] private MovementConfig? _activeEffectiveMovement;

    /// <summary>Test-run progress / result line for the toolbar; survives the window.</summary>
    [ObservableProperty] private string? _testRunStatusText;

    // ── One-shot show events (roadmap 2.4) ──
    private readonly ShowEventLog _showEvents = new();
    private double _activeMaxRttMs;

    /// <summary>Events of the running scene that have not ended (the room preview draws them).</summary>
    [ObservableProperty] private IReadOnlyList<ShowEvent> _activeShowEvents = Array.Empty<ShowEvent>();

    /// <summary>Last event result or refusal, for the Events strip.</summary>
    [ObservableProperty] private string? _eventStatusText;

    /// <summary>Defaults of the Events strip and hotkeys.</summary>
    public ShowEventSettings EventSettings { get; private set; } = ShowEventSettingsStore.Load();

    /// <summary>An event was fired (after it went to the players and clients).</summary>
    public event Action<ShowEvent>? EventFired;

    /// <summary>
    /// Events fire with or without a scene: with none playing, a stand-in scene (each seat's own Windows wallpaper)
    /// runs on the target seats for the event and stops after it. Only a scene start in flight blocks them.
    /// </summary>
    public bool CanFireEvents => !IsStartingScene;

    /// <summary>Tags per node ("BiBaBu", "Guests"): event targets. App-lifetime, persisted in tags.json.</summary>
    public NodeTagStore Tags { get; } = NodeTagStore.Load();

    // The stand-in scene of an event fired while nothing played, and when its last event ends (UTC ms).
    private CrossScreenConfig? _idleScene;
    private long _idleUntilUtcMs;

    /// <summary>The event defaults were saved (the tray re-registers the hotkeys). Raised on the caller's thread.</summary>
    public event Action? EventSettingsChanged;

    /// <summary>Persist the event defaults and use them for the next event.</summary>
    public void SaveEventSettings(ShowEventSettings settings)
    {
        EventSettings = settings;
        ShowEventSettingsStore.Save(settings);
        EventSettingsChanged?.Invoke();
    }

    partial void OnActiveSceneChanged(CrossScreenConfig? value)
    {
        if (!ReferenceEquals(value, _idleScene)) _idleScene = null;   // a real scene (or none) replaced the stand-in
        OnPropertyChanged(nameof(CanFireEvents));
    }

    partial void OnIsStartingSceneChanged(bool value) => OnPropertyChanged(nameof(CanFireEvents));

    /// <summary>True while the stand-in scene of an event plays.</summary>
    public bool IsIdleScenePlaying => _idleScene != null && ReferenceEquals(ActiveScene, _idleScene);

    /// <summary>The debug overlay switches last sent to the players.</summary>
    public DebugOverlayFlags DebugOverlay { get; private set; } = DebugOverlayFlags.Default;

    /// <summary>A scene or show item was applied: its targets now animate.</summary>
    public event Action<CrossScreenConfig>? SceneApplied;

    /// <summary>
    /// Nodes stopped showing what they showed: the ids (null = every node), and whether their thumbnail and
    /// wallpaper label go too (Clear) or only the animation markers (Stop).
    /// </summary>
    public event Action<IReadOnlyCollection<string>?, bool>? NodesStopped;

    /// <summary>The show started, stopped or moved to another item.</summary>
    public event Action? ShowChanged;

    public ServerPlaybackHost(WaBiBaBuSyService service, DesktopWindowManager desktopManager, RemotePlaybackHost remotePlayback)
    {
        _service = service;
        _desktopManager = desktopManager;
        _remotePlayback = remotePlayback;
        _agenda = new AgendaDisplay(desktopManager, m => new[] { NodeIds.ServerLocalPrefix + m, NodeIds.LocalMachinePrefix + m });
        _service.AgendaProvider = () => CurrentAgenda;
        _bombGame = new BombGameService(UtcNowMs,
            e => Dispatcher.UIThread.InvokeAsync(() => e.Type == ShowEventType.BombStep ? DispatchEventAsync(e) : DispatchBombOutcomeAsync(e)),
            () => SyncTiming.ComputeStartLeadMs(_activeMaxRttMs, 0));
        _service.ShowActionHandler = r => _bombGame.OnActionAsync(r.ClientId, r.EventId, r.Action, r.Cable);
        _agenda.StandInsChanged += OnAgendaStandInsChanged;
        _remotePlayback.AgendaStandInsChanged += OnAgendaStandInsChanged;
        if (AgendaDocument.Enabled) _ = PublishAgendaAsync();
    }

    /// <summary>Stand-ins render for hours: fullscreen detection runs while any exist (and hides them under games).</summary>
    private void OnAgendaStandInsChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_agenda.HasStandIns || _remotePlayback.HasAgendaStandIns) StartFullscreenDetectionIfNeeded();
        else StopFullscreenDetectionIfIdle();
    });

    // Connected to another server as a client: that server's agenda shows (RemotePlaybackHost), not this machine's own.
    private bool _clientMode;

    /// <summary>The tray's client connection changed: hide this machine's own agenda while it is a client, restore it after.</summary>
    public Task SetClientModeAsync(bool connected)
    {
        _clientMode = connected;
        return _agenda.SetAsync(connected ? null : CurrentAgenda, _players, reconcile: !IsStartingScene);
    }

    // ── BiBaBo (bomb-defuse mini-game, design 2026-10-21-bibabo-bomb-game-design.md) ─────

    private readonly BombGameService _bombGame;
    private DispatcherTimer? _bombTicker;
    private BombInputWatcher? _bombInput;   // this machine's own seats

    private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>True while a BiBaBo game runs (the Events strip button reads "Abort").</summary>
    [ObservableProperty]
    private bool _isBiBaBoRunning;

    /// <summary>The referee (BombInputWatcher on this machine's own seats reports to it).</summary>
    internal BombGameService BombGame => _bombGame;

    /// <summary>
    /// Start BiBaBo on <paramref name="targetNodeIds"/> (null = every seat), or abort the running game. Like an event fired
    /// while nothing plays, an idle room gets the stand-in scene. Busy seats are skipped. UI thread.
    /// </summary>
    public async Task<bool> ToggleBiBaBoAsync(IReadOnlyList<string>? targetNodeIds)
    {
        if (_bombGame.IsRunning)
        {
            await _bombGame.AbortAsync();
            return true;
        }
        if (targetNodeIds is { Count: 0 })
        {
            EventStatusText = "No seat carries that tag.";
            return false;
        }
        var layout = ActiveLayout;
        if (ActiveScene == null || layout == null)
        {
            if (!await StartIdleSceneAsync(targetNodeIds)) return false;
            layout = ActiveLayout;
            if (layout == null) return false;
        }

        string id = Guid.NewGuid().ToString("N");
        _localBusy = MonitorBusyProbe.BusyMonitors();
        var (game, solution) = BombGameFactory.Create(new BombGameRequest(layout, targetNodeIds, IsSeatBusy, Random.Shared.Next(), id), out var error);
        if (game == null)
        {
            EventStatusText = $"BiBaBo: {error}";
            if (IsIdleScenePlaying && _idleUntilUtcMs == 0) await StopAsync();   // the stand-in started for nothing
            return false;
        }
        foreach (var cable in game.Cables)
            if (cable.Riddle is { Kind: RiddleKind.Math, Formula: { } formula })
                cable.Riddle.FormulaAspect = TextSprite.MeasureAspect(formula);

        (string? logoPath, game.LogoTimerBox) = BombLogo.Resolve();
        var evt = new ShowEvent
        {
            EventId = id,
            Type = ShowEventType.Bomb,
            T0Utc = UtcNowMs() + SyncTiming.ComputeStartLeadMs(_activeMaxRttMs, 0),
            DurationMs = ShowEventMath.ClampDuration(game.IntroMs + game.TimerMs + 15_000),   // + time for the outcome to arrive
            Seed = Random.Shared.Next(),
            ColorHex = "#FF1744",
            ImagePath = logoPath,
            TargetNodeIds = targetNodeIds?.ToList(),
            Bomb = game,
        };
        _bombGame.Begin(evt, solution!);
        IsBiBaBoRunning = true;
        if (NodeIds.IsLocalMonitor(game.BombNodeId) && layout.Get(game.BombNodeId) is { } bombNode)
        {
            _bombInput ??= new BombInputWatcher((gameId, action, cable) => _bombGame.OnActionAsync(BombSeats.ThisMachine, gameId, action, cable));
            _bombInput.Start(evt, bombNode, NodeIds.MonitorIndex(game.BombNodeId));
        }
        await DispatchEventAsync(evt);
        if (IsIdleScenePlaying) KeepIdleSceneUntil(evt);
        _bombTicker ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => _ = _bombGame.TickAsync());
        _bombTicker.Start();
        EventStatusText = $"BiBaBo: the bomb is on {game.BombNodeId} — 5:00 on the clock";
        AppLogger.CreateLogger<ServerPlaybackHost>().LogDebug("BiBaBo {Id}: bomb on {Node}, {Mode}", id, game.BombNodeId, game.Mode);
        return true;
    }

    /// <summary>The referee decided: show the outcome everywhere; an idle room goes back to its wallpaper after it.</summary>
    private async Task DispatchBombOutcomeAsync(ShowEvent outcome)
    {
        _bombTicker?.Stop();
        _bombInput?.Stop();
        IsBiBaBoRunning = false;
        await DispatchEventAsync(outcome);
        if (IsIdleScenePlaying) _idleUntilUtcMs = outcome.T0Utc + ShowEventMath.ClampDuration(outcome.DurationMs) + 300;
        EventStatusText = $"BiBaBo: {outcome.Bomb?.Result}";
    }

    /// <summary>
    /// A window covers this seat's monitor (BiBaBo skips it): this machine's monitors are probed now, a client's come
    /// from its last heartbeat (older than 10 s = unknown = free). A whole-machine node is busy when any monitor is.
    /// </summary>
    private bool IsSeatBusy(string nodeId)
    {
        if (NodeIds.IsLocalMonitor(nodeId))
            return _localBusy?.Contains(NodeIds.MonitorIndex(nodeId)) == true;
        string machine = BombSeats.MachineOf(nodeId);
        var client = _service.GetConnectedClients().FirstOrDefault(c => c.ClientId == machine);
        if (client == null || UtcNowMs() - client.BusyReportUtc > 10_000 || client.BusyMonitorIndexes.Count == 0) return false;
        var suffix = nodeId.Length > machine.Length ? nodeId[(machine.Length + "_MONITOR_".Length)..] : null;
        return suffix != null && int.TryParse(suffix, out int monitor) ? client.BusyMonitorIndexes.Contains(monitor) : true;
    }

    // This machine's busy monitors, probed once per BiBaBo start.
    private IReadOnlyList<int>? _localBusy;

    // ── Agenda (room timetable overlay) ─────────────────────────────────────

    /// <summary>The room agenda on this machine's monitors (stand-ins where no scene plays).</summary>
    private readonly AgendaDisplay _agenda;

    /// <summary>What the Agenda tab edits; persisted in agenda.json.</summary>
    public AgendaDocument AgendaDocument { get; private set; } = AgendaStore.Load();

    /// <summary>The agenda last published to this machine and the clients (null until one was); read from any thread.</summary>
    [ObservableProperty]
    private Agenda? _currentAgenda;

    /// <summary>Save the edited agenda and publish it here and to every connected client. Call on the UI thread.</summary>
    public async Task ApplyAgendaAsync(AgendaDocument doc)
    {
        AgendaDocument = doc;
        try { AgendaStore.Save(doc); }
        catch (Exception ex) { Debug.WriteLine($"[Agenda] Save failed: {ex.Message}"); }
        await PublishAgendaAsync();
    }

    /// <summary>Switch the agenda on or off with everything else as it is (tray menu).</summary>
    public Task SetAgendaEnabledAsync(bool enabled)
    {
        var d = AgendaDocument;
        return ApplyAgendaAsync(new AgendaDocument
        {
            Enabled = enabled, Title = d.Title, ItemsText = d.ItemsText, NotesText = d.NotesText, FirstDay = d.FirstDay, Corner = d.Corner,
        });
    }

    /// <summary>Re-publish the current document (seat tags changed: agenda screens may have moved).</summary>
    public Task RefreshAgendaAsync() => CurrentAgenda == null ? Task.CompletedTask : PublishAgendaAsync();

    private async Task PublishAgendaAsync()
    {
        try
        {
            var agenda = AgendaDocument.Build(TimeZoneInfo.Local, Tags.NodesWith("agenda"));
            CurrentAgenda = agenda;
            await _agenda.SetAsync(_clientMode ? null : agenda, _players, reconcile: !IsStartingScene);
            if (_service.SyncCoordinator is { } coordinator) await coordinator.BroadcastAgendaAsync(agenda);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Agenda] Publish failed: {ex}");
        }
    }

    /// <summary>After local players started or stopped: stand-ins on the monitors that have none.</summary>
    private Task SyncAgendaAsync() => _agenda.ReconcileAsync(_players);

    /// <summary>True when any local player (D2D or LibVLC) or a scene is active.</summary>
    public bool HasActiveRenderer => !_players.IsEmpty || !_renderers.IsEmpty || IsCrossScreenRunning;

    partial void OnIsCrossScreenRunningChanged(bool value)
    {
        if (!value) ClearActiveScene();
        OnPropertyChanged(nameof(HasActiveRenderer));
    }

    /// <summary>
    /// Bezel gaps set in the room that no store keeps (standalone monitors, expanded nodes in client mode).
    /// App-lifetime, so they survive closing the window.
    /// </summary>
    public ConcurrentDictionary<string, int> SessionGapCm { get; } = new();

    /// <summary>The room's nodes, seat map and reference DPI — what scenes and show items are laid out over.</summary>
    public void UpdateRoom(object owner, IReadOnlyList<RoomNode> nodes, SeatMap seatMap, float referencePixelsPerCm)
    {
        _roomOwner = owner;
        _nodes = nodes;
        _seatMap = seatMap;
        _referencePixelsPerCm = referencePixelsPerCm;
    }

    /// <summary>The window closed: its room is no longer kept current (a newer window's room stays).</summary>
    public void ReleaseRoom(object owner)
    {
        if (ReferenceEquals(_roomOwner, owner)) _roomOwner = null;
    }

    /// <summary>
    /// Make sure the room is current: an open window keeps it so; otherwise build it from the sources the window
    /// uses (server monitors + clients when the server runs, else this machine's monitors; stored seat map).
    /// </summary>
    public void EnsureRoom()
    {
        if (_roomOwner != null && _nodes.Count > 0) return;
        var local = LocalMonitors.Snapshot();
        var sources = _service.IsServerRunning
            ? RoomNodeSources.ServerNodes(local, _service.GetConnectedClients(),
                _service.GetServerLocalMonitorOrder, _service.GetServerLocalMonitorDistance, SessionGapCm, _service.GetSeats())
            : RoomNodeSources.StandaloneNodes(local, SessionGapCm);
        _nodes = sources.Select(RoomNodeSources.ToRoomNode).OrderBy(n => n.Order).ToList();
        _seatMap = SeatMapStore.Load();
        _referencePixelsPerCm = LocalMonitors.ReferencePixelsPerCm;
    }

    private List<string> AllNodeIds() => _nodes.Select(n => n.Id).Distinct().ToList();

    /// <summary>Stop the room's live preview: no scene, no shared clock, no layout.</summary>
    private void ClearActiveScene()
    {
        ActiveScene = null;
        ActiveSharedStartMs = 0;
        ActiveStartLeadMs = 0;
        ActiveEffectiveMovement = null;
        ActiveLayout = null;
        ClearShowEvents();
    }

    /// <summary>Forget the events (the clients forget theirs when they get Stop); UI thread.</summary>
    private void ClearShowEvents()
    {
        _showEvents.Clear();
        ActiveShowEvents = Array.Empty<ShowEvent>();
    }

    // ── Show events ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fire a one-shot event on the seats <paramref name="targetNodeIds"/> (null = every seat): this machine's players
    /// and the connected clients. With nothing playing, a stand-in scene starts on those seats first and stops
    /// once the event ends. Refused (false, with <see cref="EventStatusText"/>) when the event is incomplete or no
    /// seat can show it. Call on the UI thread.
    /// </summary>
    public async Task<bool> FireEventAsync(ShowEventType type, string? targetSeatId = null, IReadOnlyList<string>? targetNodeIds = null)
    {
        if (_bombGame.IsRunning)
        {
            EventStatusText = "BiBaBo is running — other events wait until it ends.";
            return false;
        }
        if (targetNodeIds is { Count: 0 })
        {
            EventStatusText = "No seat carries that tag.";
            return false;
        }
        var scene = ActiveScene;
        var layout = ActiveLayout;
        if (scene == null || layout == null)
        {
            if (!await StartIdleSceneAsync(targetNodeIds)) return false;
            scene = ActiveScene;
            layout = ActiveLayout;
            if (scene == null || layout == null) return false;
        }

        var settings = EventSettings;
        string? image = null;
        string? text = null;
        float aspect = 1f;
        if (type == ShowEventType.Traversal)
        {
            image = string.IsNullOrWhiteSpace(settings.TraversalImagePath) ? scene.FirstLayer?.Animation.AnimationPath ?? "" : settings.TraversalImagePath;
            var ext = Path.GetExtension(image).ToLowerInvariant();
            if (ext is not (".png" or ".gif" or ".jpg" or ".jpeg" or ".bmp"))
            {
                EventStatusText = "No picture to send around — pick a traversal image in the Events strip.";
                return false;
            }
            aspect = EventImages.Aspect(image);
        }
        if (type == ShowEventType.Announce)
        {
            text = settings.AnnounceText?.Trim() ?? "";   // the factory sends the trimmed text; measure the same
            aspect = TextSprite.MeasureAspect(text);
        }

        var request = new ShowEventRequest(type, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _activeMaxRttMs, layout,
            PerMonitor: scene.FirstLayer?.Distribution == AnimationDistributionMode.Simultaneous, targetSeatId,
            Seed: Random.Shared.Next(), EventId: Guid.NewGuid().ToString("N"), image, aspect, text);
        var evt = ShowEventFactory.Create(request, settings, out var error);
        if (evt == null)
        {
            EventStatusText = error;
            if (IsIdleScenePlaying && _idleUntilUtcMs == 0) await StopAsync();   // the stand-in started for nothing
            return false;
        }
        evt.TargetNodeIds = targetNodeIds?.ToList();
        if (type == ShowEventType.Identify) evt.Labels = IdentifyLabels();
        await DispatchEventAsync(evt);
        if (IsIdleScenePlaying) KeepIdleSceneUntil(evt);
        return true;
    }

    /// <summary>"7  GAMING-PC" per node: the number the room view's tile badge shows, and the node's name.</summary>
    private Dictionary<string, string> IdentifyLabels() =>
        _nodes.GroupBy(n => n.Id).Select(g => g.First())
            .ToDictionary(n => n.Id, n => $"{n.Order}  {(string.IsNullOrWhiteSpace(n.Hostname) ? n.Id : n.Hostname)}");

    /// <summary>
    /// Start the stand-in scene on <paramref name="nodeIds"/> (null = the whole room) for an event fired while nothing
    /// plays: every player draws its own Windows wallpaper, so a seat looks unchanged until the event reaches it. The
    /// tiny black image is the fallback and makes remote seats receive the scene (a scene without files is not sent).
    /// </summary>
    private async Task<bool> StartIdleSceneAsync(IReadOnlyList<string>? nodeIds)
    {
        if (IsShowRunning)
        {
            EventStatusText = "The show is switching items, try again in a moment.";
            return false;
        }
        if (!TryBeginStart())
        {
            EventStatusText = "A scene is starting, try again in a moment.";
            return false;
        }
        try
        {
            EnsureRoom();
            var idle = new CrossScreenConfig
            {
                Background = new BackgroundLayerConfig { Mode = BackgroundMode.StretchedImage, ImagePath = IdleImage.EnsurePath(), UseLocalDesktopWallpaper = true },
                Layers = new List<SceneLayer>(),
                SelectedMonitorIds = nodeIds?.ToList() ?? new List<string>(),
            };
            _idleScene = idle;   // before the apply: OnActiveSceneChanged keeps it when the apply sets ActiveScene
            _idleUntilUtcMs = 0;
            await ApplyCrossScreenConfigAsync(idle);
            if (!ReferenceEquals(ActiveScene, idle))
            {
                _idleScene = null;
                EventStatusText = "None of the target seats is in the room.";
                return false;
            }
            IsCrossScreenRunning = true;
            return true;
        }
        catch (Exception ex)
        {
            _idleScene = null;
            Debug.WriteLine($"[Events] Stand-in scene failed: {ex}");
            EventStatusText = $"Could not start the event stand-in scene: {ex.Message}";
            return false;
        }
        finally
        {
            EndStart();
        }
    }

    /// <summary>Keep the stand-in scene until <paramref name="evt"/> has ended (later events extend it), then stop it.</summary>
    private void KeepIdleSceneUntil(ShowEvent evt)
    {
        long end = evt.T0Utc + ShowEventMath.ClampDuration(evt.DurationMs) + 300;   // + one fade frame of slack
        if (end <= _idleUntilUtcMs) return;
        bool waiting = _idleUntilUtcMs > 0;
        _idleUntilUtcMs = end;
        if (!waiting) _ = StopIdleSceneWhenDoneAsync();
    }

    private async Task StopIdleSceneWhenDoneAsync()
    {
        try
        {
            long wait;
            while ((wait = _idleUntilUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) > 0)
                await Task.Delay((int)Math.Min(wait, 60_000));
            _idleUntilUtcMs = 0;
            if (IsIdleScenePlaying && IsCrossScreenRunning && !IsStartingScene) await StopAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Events] Stopping the stand-in scene failed: {ex.Message}");
        }
    }

    /// <summary>Test runs: send a prepared event (T0 already set by the runner). Call on the UI thread.</summary>
    public Task FireEventForTestAsync(ShowEvent evt) => DispatchEventAsync(evt);

    private async Task DispatchEventAsync(ShowEvent evt)
    {
        _showEvents.Add(evt);
        ActiveShowEvents = _showEvents.Active(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        foreach (var player in _players.Values)
        {
            try { await player.SendShowEventAsync(evt); }
            catch (Exception ex) { Debug.WriteLine($"[Events] Local player send failed: {ex.Message}"); }
        }

        int sent = 0;
        var coordinator = _service.SyncCoordinator;
        if (coordinator != null)
        {
            // Resume / Resync replay the running events to a client of the scene (set here: a coordinator only
            // exists while the server runs, and events only exist once one was dispatched).
            coordinator.ActiveShowEventsProvider = ActiveShowEventsWithAssets;
            var scene = ActiveScene ?? LastScene;
            sent = await coordinator.BroadcastShowEventAsync(evt, EventAsset(evt.ImagePath),
                scene == null ? null : RemoteTargetIds(scene));
        }

        EventStatusText = sent > 0 ? $"{evt.Type} → this machine + {sent} client(s)" : $"{evt.Type} → this machine";
        EventFired?.Invoke(evt);
    }

    private async Task ReplayEventsAsync(IEnumerable<D2DCompositionService> players)
    {
        var active = _showEvents.Active(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (active.Count == 0) return;
        foreach (var player in players)
            foreach (var evt in active)
            {
                try { await player.SendShowEventAsync(evt); }
                catch (Exception ex) { Debug.WriteLine($"[Events] Replay failed: {ex.Message}"); }
            }
    }

    /// <summary>The running events with their Traversal images, for the coordinator's resume / Resync replay. Any thread.</summary>
    private IReadOnlyList<(ShowEvent Event, ContentAssetRef? Asset)> ActiveShowEventsWithAssets() =>
        _showEvents.Active(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .Select(e => (e, EventAsset(e.ImagePath)))
            .ToList();

    /// <summary>
    /// The remote seats a scene plays on: its selected node ids (empty = every node of the room) without this
    /// machine's monitors. Stop and events use the same rule, so an event never reaches an idle client.
    /// </summary>
    private List<string> RemoteTargetIds(CrossScreenConfig scene)
    {
        var selectedIds = new HashSet<string>(scene.SelectedMonitorIds);
        return AllNodeIds()
            .Where(id => !NodeIds.IsLocalMonitor(id) && (selectedIds.Count == 0 || selectedIds.Contains(id)))
            .ToList();
    }

    private static ContentAssetRef? EventAsset(string? path) =>
        string.IsNullOrEmpty(path) || !File.Exists(path)
            ? null
            : new ContentAssetRef { ContentId = ContentIdentity.ComputeId(path), Role = AssetRole.Event, OriginalPath = path };

    /// <summary>Cache the Traversal image on every client ahead of time (when it is picked; a show's prefetch includes it).</summary>
    public async Task PrefetchEventImageAsync()
    {
        var asset = EventAsset(EventSettings.TraversalImagePath);
        if (asset == null || _service.SyncCoordinator == null) return;
        try { await _service.SyncCoordinator.BroadcastPrefetchAsync(new[] { asset }); }
        catch (Exception ex) { Debug.WriteLine($"[Events] Prefetch of the traversal image failed: {ex.Message}"); }
    }

    // ── Scenes ──────────────────────────────────────────────────────────────

    /// <summary>Take the Play guard; false when a start is already in flight.</summary>
    public bool TryBeginStart()
    {
        if (IsStartingScene) return false;
        IsStartingScene = true;
        return true;
    }

    /// <summary>Release the Play guard taken by <see cref="TryBeginStart"/>.</summary>
    public void EndStart() => IsStartingScene = false;

    /// <summary>
    /// Stop / Clear all / Exit pressed while a start is in flight: wait for it (at most <paramref name="maxWait"/>), so
    /// they act on its players instead of finding none and letting the scene start anyway. UI thread; never call
    /// while holding the guard (it would wait for itself).
    /// </summary>
    // ponytail: 50 ms polling of a UI-thread flag; a TaskCompletionSource per start if this ever shows up in a profile.
    public async Task WaitForStartAsync(TimeSpan? maxWait = null)
    {
        var deadline = DateTime.UtcNow + (maxWait ?? TimeSpan.FromSeconds(30));
        while (IsStartingScene && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    /// <summary>
    /// Play a scene: tears down a running show (its loop and its current item's players / remote nodes) or a
    /// running scene, then starts <paramref name="config"/> on its targets. The caller holds the Play guard.
    /// </summary>
    public async Task PlaySceneAsync(CrossScreenConfig config)
    {
        if (_playlistOrchestrator?.IsRunning == true)
        {
            // A show never sets IsCrossScreenRunning, so StopAsync alone would only cancel the playlist
            // loop and leave its current item's D2D players and remote nodes running.
            await StopShowAsync();
            await ClearNodesAsync(AllNodeIds());
        }
        if (IsCrossScreenRunning)
            await StopAsync();
        await StartSceneAsync(config);
    }

    /// <summary>
    /// Tray Quick start: play <paramref name="config"/> on every node of the room, window open or not. Same teardown
    /// as ▶ Play (a running show or scene stops first). False when a start is in flight or the room has no node.
    /// </summary>
    public async Task<bool> QuickStartAsync(CrossScreenConfig config)
    {
        if (!TryBeginStart()) return false;
        try
        {
            EnsureRoom();
            if (_nodes.Count == 0) return false;
            config.SelectedMonitorIds = new List<string>();
            await PlaySceneAsync(config);
            return IsCrossScreenRunning;
        }
        finally
        {
            EndStart();
        }
    }

    /// <summary>Start <paramref name="config"/> as the manual scene (toolbar Start; no-op while one runs).</summary>
    public async Task StartSceneAsync(CrossScreenConfig config)
    {
        if (IsIdleScenePlaying) await StopAsync();   // an event's stand-in yields to a real scene
        if (IsCrossScreenRunning)
        {
            Debug.WriteLine("[CrossScreen] Already running");
            return;
        }

        LastScene = config;
        try
        {
            Debug.WriteLine("[CrossScreen] Starting cross-screen animation via D2D...");
            await ApplyCrossScreenConfigAsync(config);
            IsCrossScreenRunning = true;
            SceneApplied?.Invoke(config);
            Debug.WriteLine("[CrossScreen] D2D animation started successfully on all monitors");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CrossScreen] Error starting: {ex.Message}\n{ex.StackTrace}");
            IsCrossScreenRunning = false;
        }
    }

    /// <summary>Toolbar ■: stop the show (if any) and the running scene on every node it plays on.</summary>
    public async Task StopAsync()
    {
        // Stop the playlist rotation loop first so it can't respawn D2D players after teardown.
        // Runs before the early-return because a playlist does not set IsCrossScreenRunning.
        if (_playlistOrchestrator != null)
            await StopShowAsync();

        if (_bombGame.IsRunning) await _bombGame.AbortAsync();   // Stop ends a running game quietly

        if (!IsCrossScreenRunning)
        {
            Debug.WriteLine("[CrossScreen] Not running");
            return;
        }

        try
        {
            Debug.WriteLine("[CrossScreen] Stopping cross-screen animation...");

            // Stop and dispose all D2D composition services (and the previous item's, if a switch is in flight)
            await _retiring.StopAllNowAsync();
            foreach (var kvp in _players)
            {
                kvp.Value.GlobalLapCompleted -= OnSequentialLapCompleted;
                try { await kvp.Value.StopAsync(); } catch { }
                try { kvp.Value.Dispose(); } catch { }
            }
            _players.Clear();
            _lastGlobalLap = -1;
            await SyncAgendaAsync();   // the agenda stays: stand-ins take over the now idle monitors

            // Send stop command to remote clients that were part of this session
            var stopped = ActiveScene ?? LastScene;   // the stand-in of an event is never LastScene
            if (_service.SyncCoordinator != null && stopped != null && !string.IsNullOrEmpty(_crossScreenContentId))
            {
                var remoteToStop = RemoteTargetIds(stopped);

                foreach (var remoteId in remoteToStop)
                {
                    try
                    {
                        await _service.SyncCoordinator.StopCrossScreenOnClientAsync(remoteId, _crossScreenContentId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CrossScreen] Failed to send stop to remote {remoteId}: {ex.Message}");
                    }
                }
            }
            _crossScreenContentId = null;

            IsCrossScreenRunning = false;
            StopFullscreenDetectionIfIdle();
            NodesStopped?.Invoke(null, false);

            Debug.WriteLine("[CrossScreen] Animation stopped");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CrossScreen] Error stopping: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop what the given nodes show: local monitors dispose their D2D player / LibVLC renderer,
    /// remote nodes get a cross-screen Stop. A running playlist puts them back on its next item —
    /// use the toolbar Stop to end a show.
    /// </summary>
    public async Task ClearNodesAsync(IReadOnlyCollection<string> nodeIds)
    {
        // Stopped clients drop their event log; keep the server's in step so a replay never diverges seats.
        if (nodeIds.Count > 0) ClearShowEvents();
        foreach (var id in nodeIds)
        {
            if (NodeIds.IsLocalMonitor(id))
            {
                int monitorIndex = NodeIds.MonitorIndex(id);
                if (_players.TryRemove(monitorIndex, out var d2d))
                {
                    d2d.GlobalLapCompleted -= OnSequentialLapCompleted;
                    try { await d2d.StopAsync(); } catch (Exception ex) { Debug.WriteLine($"[ClearNodes] Stop {id}: {ex.Message}"); }
                    try { d2d.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[ClearNodes] Dispose {id}: {ex.Message}"); }
                }
                if (_renderers.TryRemove(monitorIndex, out var renderer) && renderer is IDisposable disposable)
                {
                    try { disposable.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[ClearNodes] Renderer {id}: {ex.Message}"); }
                }
            }
            else if (_service.SyncCoordinator != null && !string.IsNullOrEmpty(_crossScreenContentId))
            {
                try { await _service.SyncCoordinator.StopCrossScreenOnClientAsync(id, _crossScreenContentId); }
                catch (Exception ex) { Debug.WriteLine($"[ClearNodes] Remote stop {id}: {ex.Message}"); }
            }
        }
        await SyncAgendaAsync();
        NodesStopped?.Invoke(nodeIds, true);
        OnPropertyChanged(nameof(HasActiveRenderer));
    }

    /// <summary>Clear all: stop the show and the scene, dispose every local player, stop client-mode players, kill strays.</summary>
    public async Task ClearAllAsync()
    {
        Debug.WriteLine("[ClearAll] Stopping animations and clearing wallpapers");

        // Stop the playlist rotation loop first so it can't respawn D2D players after teardown.
        if (_playlistOrchestrator != null)
            await StopShowAsync();

        if (_bombGame.IsRunning) await _bombGame.AbortAsync();

        // Stop cross-screen animation if running
        if (IsCrossScreenRunning)
            await StopAsync();

        // Stop and dispose all D2D composition services
        // Must await StopAsync before Dispose to avoid sync-over-async deadlock
        await _retiring.StopAllNowAsync();
        foreach (var kvp in _players)
        {
            kvp.Value.GlobalLapCompleted -= OnSequentialLapCompleted;
            try { await kvp.Value.StopAsync(); }
            catch (Exception ex) { Debug.WriteLine($"[ClearAll] Error stopping D2D service for monitor {kvp.Key}: {ex.Message}"); }
            try { kvp.Value.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[ClearAll] Error disposing D2D service for monitor {kvp.Key}: {ex.Message}"); }
        }
        _players.Clear();

        // Dispose all LibVLC renderers
        foreach (var kvp in _renderers)
        {
            try { kvp.Value.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[ClearAll] Error disposing renderer for monitor {kvp.Key}: {ex.Message}"); }
        }
        _renderers.Clear();

        // Reset animation indicators and preview on every node
        NodesStopped?.Invoke(null, true);

        // Stop fullscreen detection since no wallpapers are active
        StopFullscreenDetectionIfIdle();

        // Client mode: the server-driven players belong to the app-lifetime host; stop them properly first.
        await _remotePlayback.StopAllAsync();

        // The kill below takes every player process, the agenda stand-ins too: stop them properly, restart them after.
        await _agenda.StopAllAsync();
        await _remotePlayback.StopAgendaStandInsAsync();

        // Finally, kill any lingering Player.D2D processes nothing tracks (crashes, debug sessions, stale state).
        int killed = PlayerProcesses.Kill(priorSessionOnly: false);
        if (killed > 0)
            Debug.WriteLine($"[ClearAll] Killed {killed} orphan player process(es)");

        // Force IsCrossScreenRunning back to a sane state even if StopAsync couldn't.
        IsCrossScreenRunning = false;
        ClearActiveScene();   // also when only a playlist ran (it never sets IsCrossScreenRunning)
        OnPropertyChanged(nameof(HasActiveRenderer));
        await SyncAgendaAsync();                        // the agenda is not cleared: back on its stand-ins
        await _remotePlayback.ReconcileAgendaAsync();

        Debug.WriteLine("[ClearAll] All wallpapers cleared");
    }

    /// <summary>Exit: stop the show and every local player (no remote commands; the server stops next).</summary>
    public async Task ShutdownAsync()
    {
        await WaitForStartAsync(TimeSpan.FromSeconds(10));   // a start finishing after this would orphan its players

        if (_playlistOrchestrator != null)
        {
            try { await _playlistOrchestrator.StopAsync(); }
            catch (Exception ex) { Debug.WriteLine($"[Shutdown] Stopping the show failed: {ex.Message}"); }
        }

        await _retiring.StopAllNowAsync();
        foreach (var kvp in _players)
        {
            kvp.Value.GlobalLapCompleted -= OnSequentialLapCompleted;
            try { await kvp.Value.StopAsync(); } catch (Exception ex) { Debug.WriteLine($"[Shutdown] Stop monitor {kvp.Key}: {ex.Message}"); }
            try { kvp.Value.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[Shutdown] Dispose monitor {kvp.Key}: {ex.Message}"); }
        }
        _players.Clear();

        foreach (var kvp in _renderers)
        {
            try { kvp.Value.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[Shutdown] Renderer monitor {kvp.Key}: {ex.Message}"); }
        }
        _renderers.Clear();

        foreach (var capture in _thumbnailCaptureServices.Values)
            capture.ClearWallpaperHwnd();
        _thumbnailCaptureServices.Clear();

        await _agenda.StopAllAsync();
        await _remotePlayback.StopAgendaStandInsAsync();

        _fullscreenDetection?.Stop();
    }

    // ── Playlist show ───────────────────────────────────────────────────────

    /// <summary>True while a show runs.</summary>
    public bool IsShowRunning => _playlistOrchestrator?.IsRunning == true;

    /// <summary>Index (in the running playlist's items) of the item on screen, or -1.</summary>
    public int ShowItemIndex => IsShowRunning ? _playlistOrchestrator!.CurrentItemIndex : -1;

    /// <summary>Where the running show is (playlist, item, cycle order), or null when no show runs.</summary>
    public PlaylistResumePoint? ShowResumePoint => _playlistOrchestrator?.CaptureResumePoint();

    /// <summary>"Now: Fish · next: Logo Rain in 0:42" while a show runs, else empty.</summary>
    public string ShowStatusLine
    {
        get
        {
            var o = _playlistOrchestrator;
            if (o == null || !o.IsRunning || o.NextSwitchUtcMs <= 0) return "";
            return ShowStatus.Format(o.CurrentItem?.Name, o.NextItem?.Name,
                o.NextSwitchUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    private void RaiseShowChanged() => ShowChanged?.Invoke();

    /// <summary>Start rotating the given playlist across all nodes (ignored while a Play is in flight).</summary>
    public void StartShow(Playlist playlist)
    {
        if (IsStartingScene)
        {
            AppLogger.CreateLogger<ServerPlaybackHost>()
                .LogWarning("StartShow ignored: a scene is already starting (Play button in flight)");
            return;
        }

        if (_playlistOrchestrator == null)
        {
            _playlistOrchestrator = new PlaylistOrchestrator(
                AppLogger.CreateLogger<PlaylistOrchestrator>(),
                apply: config => Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var r = await ApplyCrossScreenConfigAsync(config, _playlistOrchestrator?.CurrentTransition);
                    SceneApplied?.Invoke(config);   // the room paints the live sprite on animating tiles only
                    return new ApplyMetrics(r.VirtualCanvasWidth, r.ContentWidthPx, r.StartLeadMs, r.EffectiveSpeedPx, r.CanvasWraps,
                        r.CanvasHeight, r.ContentHeightPx);
                }),
                seedProvider: () => Environment.TickCount,
                prefetch: PrefetchShowAssetsAsync,
                waitUntilNodesReady: WaitUntilRemotesPrefetchedAsync,
                fireEvent: type => Dispatcher.UIThread.InvokeAsync(() => FireEventAsync(type, EventSettings.LastTargetSeatId)));
            _playlistOrchestrator.ItemChanged += (_, _) => Dispatcher.UIThread.Post(RaiseShowChanged);
        }

        _playlistOrchestrator.Start(playlist);
        RaiseShowChanged();
    }

    /// <summary>Stop the running show (if any). Its current item's players keep running (Stop / Clear all end them).</summary>
    public async Task StopShowAsync()
    {
        if (_playlistOrchestrator != null)
            await _playlistOrchestrator.StopAsync();
        RaiseShowChanged();
    }

    /// <summary>Toolbar ⏭: advance the running show to its next item.</summary>
    public void SkipShowItem() => _playlistOrchestrator?.Skip();

    /// <summary>
    /// Start a show again at a captured point, like its ▶: the item restarts from its beginning, a shuffled
    /// cycle keeps its order. The caller tears down what plays. False when the show did not start.
    /// </summary>
    public bool ResumeShow(PlaylistResumePoint point)
    {
        // The point came from this orchestrator (ShowResumePoint), so it exists.
        if (_playlistOrchestrator == null) return false;
        _playlistOrchestrator.Resume(point);
        RaiseShowChanged();
        return _playlistOrchestrator.IsRunning;
    }

    // ── Tier 1.3 show reliability ────────────────────────────────────────────

    /// <summary>Content refs (hashed ids) for every existing file a scene references.</summary>
    private static List<ContentAssetRef> BuildAssetRefs(CrossScreenConfig config)
    {
        var refs = new List<ContentAssetRef>();
        foreach (var (path, role) in SceneAssets.CollectPaths(config))
        {
            if (!File.Exists(path)) continue;
            refs.Add(new ContentAssetRef { ContentId = ContentIdentity.ComputeId(path), Role = role, OriginalPath = path });
        }
        return refs;
    }

    /// <summary>Orchestrator hook: ask every client to cache the whole show before it starts.</summary>
    private async Task PrefetchShowAssetsAsync(IReadOnlyList<CrossScreenConfig> configs)
    {
        var coordinator = _service.SyncCoordinator;
        if (coordinator == null) return;
        var assets = configs.SelectMany(BuildAssetRefs)
            .GroupBy(a => a.ContentId).Select(g => g.First()).ToList();
        // The Traversal image rides in the same PREFETCH (a separate one would reset the clients' prefetch counters).
        if (EventAsset(EventSettings.TraversalImagePath) is { } eventImage && assets.All(a => a.ContentId != eventImage.ContentId))
            assets.Add(eventImage);
        if (assets.Count == 0) return;
        await coordinator.BroadcastPrefetchAsync(assets);
    }

    /// <summary>
    /// Orchestrator hook: wait until every connected remote reports its prefetch complete. Reads the server's
    /// client list directly, so it works with the window closed. Any thread.
    /// </summary>
    private async Task<bool> WaitUntilRemotesPrefetchedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            var remotes = _service.GetConnectedClients().Select(c => (
                c.Status is WaBiBaBuSy.Grpc.ClientStatusEnum.ClientConnected or WaBiBaBuSy.Grpc.ClientStatusEnum.ClientPlaying,
                c.PrefetchReady,
                c.PrefetchTotal));
            if (ShowStatus.RemotesReady(remotes)) return true;
        }
        return false;
    }

    // ── Plain wallpapers on this machine (Dev tools) ─────────────────────────

    /// <summary>Plain wallpaper through LibVLC on a local monitor. False when it could not start.</summary>
    public async Task<bool> ApplyLocalWallpaperAsync(string filePath, string name, int monitorIndex)
    {
        try
        {
            Debug.WriteLine($"[ApplyWallpaperLocally] Applying '{name}' to local machine monitor {monitorIndex}");

            // Dispose previous renderer for this monitor if exists (atomic operation)
            if (_renderers.TryRemove(monitorIndex, out var existingRenderer))
                existingRenderer.Dispose();

            var screens = System.Windows.Forms.Screen.AllScreens;
            if (monitorIndex < 0 || monitorIndex >= screens.Length)
            {
                Debug.WriteLine($"[ApplyWallpaperLocally] Invalid monitor index {monitorIndex}. Available monitors: {screens.Length}");
                return false;
            }

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            IWallpaperRenderer? renderer = extension switch
            {
                // GIFs use LibVLC (VideoWallpaperRenderer) for instant loading
                ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".flv" or ".gif"
                    => new VideoWallpaperRenderer(AppLogger.CreateLogger<VideoWallpaperRenderer>(), _desktopManager),
                ".jpg" or ".jpeg" or ".png" or ".bmp"
                    => new ImageWallpaperRendererLibVLC(AppLogger.CreateLogger<ImageWallpaperRendererLibVLC>(), _desktopManager),
                _ => null
            };
            if (renderer == null)
            {
                Debug.WriteLine($"[ApplyWallpaperLocally] Unsupported file type: {extension}");
                return false;
            }

            await renderer.InitializeAsync(new WallpaperConfig { FilePath = filePath, Loop = true, MonitorIndex = monitorIndex });
            await renderer.StartAsync();

            _renderers[monitorIndex] = renderer;
            OnPropertyChanged(nameof(HasActiveRenderer));
            SetupThumbnailCapture(monitorIndex, renderer.WindowHandle, name);

            Debug.WriteLine($"[ApplyWallpaperLocally] Successfully applied '{name}' to monitor {monitorIndex}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApplyWallpaperLocally] Error: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// Plain wallpaper through a D2D player process on a local monitor (centered, no movement).
    /// False when it could not start.
    /// </summary>
    public async Task<bool> ApplyLocalD2DAsync(string filePath, string name, int monitorIndex, string backgroundColor, int fitMode)
    {
        try
        {
            Debug.WriteLine($"[Direct2D] Applying '{name}' to monitor {monitorIndex} via D2D separate process");

            if (_players.TryRemove(monitorIndex, out var existingService))
            {
                try
                {
                    existingService.Dispose();
                    // Make sure the old player process is gone before the new one starts
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    await Task.Delay(500);
                }
                catch (Exception disposeEx)
                {
                    Debug.WriteLine($"[Direct2D] Error disposing existing service: {disposeEx.Message}");
                }
            }

            // Native API (avoids WinForms Screen.AllScreens issue on .NET 9)
            var screens = NativeMonitorInfo.GetAllMonitors();
            if (monitorIndex < 0 || monitorIndex >= screens.Length)
            {
                Debug.WriteLine($"[Direct2D] Invalid monitor index {monitorIndex}. Available monitors: {screens.Length}");
                return false;
            }

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            if (extension is not (".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".flv" or ".gif"
                or ".jpg" or ".jpeg" or ".png" or ".bmp"))
            {
                Debug.WriteLine($"[Direct2D] File '{filePath}' is not supported. Extension: {extension}");
                return false;
            }

            var screen = screens[monitorIndex];
            var screenConfig = new WallpaperEngine.Composition.ScreenConfiguration
            {
                ClientId = $"{NodeIds.LocalMachinePrefix}{monitorIndex}",
                Order = monitorIndex,
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height,
                PhysicalDistanceCm = 0
            };
            var canvasManager = new VirtualCanvasManager(AppLogger.CreateLogger<VirtualCanvasManager>());
            canvasManager.CalculateLayout(new[] { screenConfig });

            var backgroundConfig = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = backgroundColor };
            // Simple playback: one layer, content centered and stationary on this monitor
            var layer = new SceneLayer
            {
                Animation = new AnimationLayerConfig
                {
                    AnimationPath = filePath,
                    TargetHeight = screen.Bounds.Height,
                    Loop = true,
                    VerticalAlign = VerticalAlignment.Center,
                    CenterInitialPosition = true,
                    FitMode = (ContentFitMode)fitMode
                },
                Movement = new MovementConfig { Type = MovementType.Static },
                Distribution = AnimationDistributionMode.Simultaneous,
            };

            // Metadata-based: the player process renders, nothing is composed in this process
            var d2dService = new D2DCompositionService(
                AppLogger.CreateLogger<D2DCompositionService>(), AppLogger.Factory, _desktopManager);
            var actualBounds = new System.Drawing.Rectangle(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
            await d2dService.InitializeAsync(canvasManager, backgroundConfig, new[] { layer }, actualBounds, monitorIndex);

            // Small delay to ensure player has loaded animation
            await Task.Delay(100);
            // pixelsPerSecond = 0: stationary
            await d2dService.StartAsync(startTimestampMs: 0, pixelsPerSecond: 0);

            _players[monitorIndex] = d2dService;
            await _agenda.SendToAsync(monitorIndex, d2dService);
            await SyncAgendaAsync();
            OnPropertyChanged(nameof(HasActiveRenderer));
            StartFullscreenDetectionIfNeeded();
            SetupThumbnailCapture(monitorIndex, d2dService.PlayerHwnd, name);

            Debug.WriteLine($"[Direct2D] Successfully started D2D rendering for '{name}' on monitor {monitorIndex}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Direct2D] Error: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    // ── Thumbnails, debug overlay, fullscreen ────────────────────────────────

    /// <summary>JPEG of a local monitor's player window for the room tile, or null.</summary>
    public byte[]? CaptureLocalThumbnail(int monitorIndex) =>
        _thumbnailCaptureServices.TryGetValue(monitorIndex, out var capture) ? capture.CaptureCurrentThumbnail() : null;

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

    /// <summary>Send the debug overlay switches to every local player and remember them for the next window.</summary>
    public async Task SetDebugOverlayAsync(DebugOverlayFlags flags)
    {
        DebugOverlay = flags;
        foreach (var service in _players.Values)
        {
            try
            {
                await service.SendSetDebugOverlayFlagsAsync(
                    flags.Enabled, flags.ShowPath, flags.ShowIconRects, flags.ShowZoneBands, flags.ShowInfoPanel);
            }
            catch (Exception ex) { Debug.WriteLine($"[DebugOverlay] SendFlags failed: {ex.Message}"); }
        }
    }

    private void StartFullscreenDetectionIfNeeded()
    {
        if (_fullscreenDetection?.IsRunning == true) return;
        // Read when detection starts, so the "Pause on fullscreen" settings apply without an app restart.
        var config = ConfigurationManager.LoadClientConfiguration();
        if (!config.PauseOnFullscreen) return;
        _pauseOnlyFullscreenMonitor = config.PauseOnlyFullscreenMonitor;
        if (_fullscreenDetection == null)
        {
            _fullscreenDetection = new FullscreenDetectionService(
                AppLogger.CreateLogger<FullscreenDetectionService>(), MonitorBusyProbe.FullscreenMonitors);
            _fullscreenDetection.FullscreenMonitorsChecked += OnFullscreenMonitorsChecked;
        }
        _fullscreenDetection.Start();
    }

    private void StopFullscreenDetectionIfIdle()
    {
        if (_fullscreenDetection == null) return;

        bool hasActiveWallpaper = !_players.IsEmpty
            || _remotePlayback.HasPlayers
            || !_renderers.IsEmpty
            || IsCrossScreenRunning
            || _agenda.HasStandIns
            || _remotePlayback.HasAgendaStandIns;

        if (!hasActiveWallpaper)
        {
            _fullscreenDetection.Stop();
            _pausedRenderers.Clear();
            Debug.WriteLine("[Fullscreen] Detection stopped (no active wallpapers)");
        }
    }

    /// <summary>
    /// Every detection check (its loop thread): re-apply the pause to every player on this machine — scene players, the
    /// remote-driven ones, agenda stand-ins. Players dedupe, so this only sends changes and also catches players started
    /// since the last check. Networking is untouched: a paused seat stays connected and online.
    /// </summary>
    // ponytail: a player started mid-game renders until the next check (≤ 2 s, hidden behind the game anyway) and a
    // retiring one is not paused (stopped ≤ 15 s later); pause at creation if that ever shows up in the numbers.
    private void OnFullscreenMonitorsChecked(IReadOnlyList<int> fullscreen)
    {
        bool only = _pauseOnlyFullscreenMonitor;
        bool Paused(int monitor) => BusyRule.IsPausedMonitor(monitor, fullscreen, only);
        // Task.Run: Stop() raises the last check on the caller's (maybe UI) thread; blocking it on its own context would deadlock.
        try { Task.Run(() => PauseMonitorsAsync(Paused)).GetAwaiter().GetResult(); }
        catch (Exception ex) { Debug.WriteLine($"[Fullscreen] Pause update failed: {ex.Message}"); }
    }

    private async Task PauseMonitorsAsync(Func<int, bool> paused)
    {
        foreach (var (monitor, player) in _players)
            await player.SetPausedAsync(paused(monitor));
        await _agenda.PauseMonitorsAsync(paused);
        await _remotePlayback.PauseMonitorsAsync(paused);

        foreach (var (monitor, renderer) in _renderers)
        {
            bool pause = paused(monitor);
            if (pause == _pausedRenderers.Contains(renderer)) continue;
            try
            {
                if (pause) { await renderer.PauseAsync(); _pausedRenderers.Add(renderer); }
                else { await renderer.StartAsync(); _pausedRenderers.Remove(renderer); }
            }
            catch { /* best effort */ }
        }
    }

    // ── Test mode (ITestHost through TestHostAdapter); any thread ────────────

    /// <summary>Every node in seat order, from the room last pushed by the control panel.</summary>
    public Task<IReadOnlyList<TestNodeInfo>> TestNodesAsync() => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var versions = _service.GetConnectedClients().GroupBy(c => c.ClientId).ToDictionary(g => g.Key, g => g.First().AppVersion);
        return TestNodeList.Build(_nodes, versions, AppVersionInfo.AppVersion);
    }).GetTask();

    /// <summary>Same teardown + start as ▶ Play, holding the Play guard across the awaits.</summary>
    public Task<SceneStartInfo> PlaySceneForTestAsync(CrossScreenConfig config, IReadOnlyList<string> targetNodeIds, bool switchScene = false,
        SceneTransition? transition = null) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (!TryBeginStart())
                throw new InvalidOperationException("a scene is already starting — try again when the current start has finished");
            try
            {
                config.SelectedMonitorIds = targetNodeIds.ToList();
                var previousStart = ActiveSharedStartMs;
                if (switchScene && IsCrossScreenRunning)
                {
                    // The playlist item path: apply over the running scene, no stop first (standby switch).
                    LastScene = config;
                    await ApplyCrossScreenConfigAsync(config, transition);
                }
                else
                {
                    await PlaySceneAsync(config);
                }
                // A fresh start always moves the shared epoch; the reference check alone would pass when
                // the very same config object was left running after a failed stop.
                if (!ReferenceEquals(ActiveScene, config) || ActiveSharedStartMs == previousStart)
                    throw new InvalidOperationException("the scene did not start (see the server log)");

                return new SceneStartInfo
                {
                    SharedStartServerUtcMs = ActiveSharedStartMs,
                    StartLeadMs = ActiveStartLeadMs,
                    Layout = ActiveLayout,
                    // Marker checks follow the bottom layer (the probes report its position).
                    PerMonitor = config.FirstLayer?.Distribution == AnimationDistributionMode.Simultaneous,
                    EffectiveMovement = ActiveEffectiveMovement ?? config.FirstLayer?.Movement ?? new MovementConfig(),
                };
            }
            finally
            {
                EndStart();
            }
        });

    public Task StopAllForTestAsync() => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        if (!TryBeginStart())
            throw new InvalidOperationException("a scene is already starting — try again when the current start has finished");
        try
        {
            // Unconditional: it stops a running show first (which never sets IsCrossScreenRunning) and
            // returns early when no scene runs.
            await StopAsync();
            await ClearNodesAsync(AllNodeIds());
        }
        finally
        {
            EndStart();
        }
    });

    /// <summary>What to restore after a run: the running show (playlist + item), else the running scene, else nothing.</summary>
    public TestRestorePoint CaptureTestRestorePoint() => Dispatcher.UIThread.Invoke(() =>
    {
        if (ShowResumePoint is { } show) return new TestRestorePoint { Show = show };
        return IsCrossScreenRunning && ActiveScene is { } scene ? new TestRestorePoint { Scene = scene } : TestRestorePoint.Nothing;
    });

    /// <summary>
    /// After a run: tear down the run's last scene like <see cref="StopAllForTestAsync"/>, then start the show
    /// again at the item it was on, as ▶ on that playlist would. Holds the Play guard throughout.
    /// </summary>
    public Task ResumeShowForTestAsync(PlaylistResumePoint show) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        if (!TryBeginStart())
            throw new InvalidOperationException("a scene is already starting — try again when the current start has finished");
        try
        {
            await StopAsync();   // unconditional: also stops a show, returns early when no scene runs
            await ClearNodesAsync(AllNodeIds());
            if (!ResumeShow(show))
                throw new InvalidOperationException("the show could not be started again (see the server log)");
        }
        finally
        {
            EndStart();
        }
    });

    public async Task<bool> PrefetchForTestAsync(IReadOnlyList<CrossScreenConfig> scenes, TimeSpan timeout)
    {
        await PrefetchShowAssetsAsync(scenes);
        return await WaitUntilRemotesPrefetchedAsync(timeout);
    }

    public IReadOnlyList<ITestProbeTarget> LocalProbeTargets() =>
        _players.Values.Where(s => s.IsRunning).Cast<ITestProbeTarget>().ToList();

    /// <summary>Test-run progress for the toolbar. Any thread.</summary>
    public void ReportTestProgress(string text) => Dispatcher.UIThread.Post(() => TestRunStatusText = text);

    // ── Cross-screen apply (shared by Play, Start and the show) ──────────────

    /// <summary>
    /// Applies a cross-screen configuration: builds the virtual canvas, initializes local
    /// D2D players, starts them with a shared timestamp, broadcasts to remote clients, and
    /// wires up fullscreen detection + thumbnail capture for the freshly spawned players.
    /// Shared by the manual Start button and the playlist orchestrator. Does NOT toggle
    /// one-time "session started" state (IsCrossScreenRunning / SceneApplied) — that stays
    /// in the caller so items can be applied repeatedly.
    /// The applied config comes from the <paramref name="config"/> parameter.
    /// </summary>
    private async Task<CrossScreenApplyResult> ApplyCrossScreenConfigAsync(CrossScreenConfig config, SceneTransition? transition = null)
    {
        string transitionWire = (transition ?? SceneTransition.Cut).Encode();
        // Targets, local start order, per-node size/DPI, seat-map layout and cm→px (pure, CrossScreenPlanner).
        // Remote clients without a reported DPI are assumed to have the first local monitor's.
        var nativeMonitors = WaBiBaBuSy.WallpaperEngine.Native.NativeMonitorInfo.GetAllMonitors();
        var plan = CrossScreenPlanner.Plan(config, _nodes, _seatMap, _referencePixelsPerCm,
            nativeMonitors.Select(m => m.PixelsPerCm).ToList());
        if (plan.IsEmpty)
        {
            Debug.WriteLine("[CrossScreen] No monitors selected");
            return new CrossScreenApplyResult();
        }

        var localClients = plan.Local;
        var remoteClients = plan.Remote;
        await StopNodesOutsideAsync(localClients.Select(c => NodeIds.MonitorIndex(c.Node.Id)).ToHashSet(),
            remoteClients.Concat(plan.Ghosts).Select(c => c.Node.Id).ToHashSet());
        lock (_seqLapLock) _lastGlobalLap = -1;   // a new scene counts its laps from the start
        _activeSceneHasRemotes = remoteClients.Count > 0;
        var layout = plan.Layout;
        // Layer 0 drives everything scene-wide: IconZone path, lap signal, start speed, remote content id, thumbnails.
        var effScene = plan.EffectiveScene;
        var bottom = effScene.FirstLayer;
        var effMovement = bottom?.Movement ?? new MovementConfig { Type = MovementType.Static };
        bool bottomSequential = bottom?.Distribution != AnimationDistributionMode.Simultaneous;
        Debug.WriteLine($"[CrossScreen] Starting D2D animation on {localClients.Count} local + {remoteClients.Count} remote node(s)");
        Debug.WriteLine($"[CrossScreen] Virtual canvas: {layout.CanvasWidth}x{layout.CanvasHeight} ({_seatMap.Traversal}, {_seatMap.Rows.Count} row(s), wraps={layout.Wraps})");

        // Get actual monitor bounds from Windows
        var screens = System.Windows.Forms.Screen.AllScreens;

        // Sequential + IconZone: compute one global A* path across the full virtual canvas
        // so all local monitors share the same coordinated corridor path (layer 0 only follows it).
        var layers = effScene.Layers;
        if (bottom != null && bottomSequential && config.Background.Mode == BackgroundMode.IconZone && localClients.Count > 0)
        {
            try
            {
                var iconService = new WaBiBaBuSy.Core.Services.Desktop.DesktopIconService();
                var (cellW, cellH) = iconService.GetGridCellSize();
                var allIcons = iconService.GetIconPositions(); // positions in virtual-desktop (absolute) coords

                int firstMonitorIdx = NodeIds.MonitorIndex(localClients[0].Node.Id);
                var firstScreen = firstMonitorIdx < screens.Length ? screens[firstMonitorIdx] : screens[0];
                int virtualH = firstScreen.Bounds.Height;

                // Effective animation height depends on FitMode. Using the actual rendered height
                // keeps the server-side path padding in sync with what Player.D2D will compute
                // locally (pathPaddingPx = _animHeight / 2 + optional wave amplitude).
                int effectiveHeight = ComputeEffectiveAnimationSize(
                    bottom.Animation, firstScreen.Bounds.Width, virtualH).H;
                int pathPaddingPx = effectiveHeight / 2;
                if (effMovement.Type == MovementType.SineWave)
                    pathPaddingPx += (int)Math.Ceiling(effMovement.WaveAmplitudePixels);
                var globalLayout = WaBiBaBuSy.WallpaperEngine.Desktop.ZonePlanner.Compute(
                    allIcons.Select(i => (i.PixelX, i.PixelY)),
                    cellW, cellH,
                    layout.CanvasWidth, virtualH,
                    config.Background.IconZonePaletteHexes,
                    config.Background.IconCorridorColorHex,
                    paddingPx: pathPaddingPx,
                    visualPaddingPx: Math.Max(4, cellW / 10));

                // The bottom layer carries the global path (effScene is the planner's own copy). Attached to the
                // config itself, so effects and face-travel are no longer dropped by a field-by-field clone.
                bottom.Animation.PrecomputedPath = globalLayout.Path;

                Debug.WriteLine($"[CrossScreen] IconZone sequential: computed global path with {globalLayout.Path.Count} waypoints across {layout.CanvasWidth}px virtual canvas");

                // Save params for per-lap recompute
                _seqCellW              = cellW;
                _seqCellH              = cellH;
                _seqVirtualCanvasWidth = layout.CanvasWidth;
                _seqVirtualH           = virtualH;
                _seqPathPaddingPx      = pathPaddingPx;
                _seqVisualPaddingPx    = Math.Max(4, cellW / 10);
                _seqPaletteHexes       = config.Background.IconZonePaletteHexes.ToList();
                _seqCorridorColorHex   = config.Background.IconCorridorColorHex;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CrossScreen] Global icon path failed, players will compute locally: {ex.Message}");
            }
        }

        // Phase 1: Initialize all D2D players (load animation, extract GIF frames)
        var newServices = new List<(int monitorIndex, D2DCompositionService service)>();
        var retiring = new List<D2DCompositionService>();
        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var phase1Timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
        foreach (var planned in localClients)
        {
            var monitorIndex = NodeIds.MonitorIndex(planned.Node.Id);

            // Standby switch: the monitor's current player keeps showing until the new one appears at the shared
            // start (it keeps its window hidden until then); RetiringPlayers stops it after that.
            if (_players.TryRemove(monitorIndex, out var existingService))
            {
                existingService.GlobalLapCompleted -= OnSequentialLapCompleted;
                retiring.Add(existingService);
            }

            // Find actual screen bounds
            var screen = monitorIndex < screens.Length ? screens[monitorIndex] : screens[0];
            var actualBounds = new System.Drawing.Rectangle(
                screen.Bounds.X, screen.Bounds.Y,
                screen.Bounds.Width, screen.Bounds.Height);

            Debug.WriteLine($"[CrossScreen] Monitor {monitorIndex}: {actualBounds.Width}x{actualBounds.Height} at ({actualBounds.X},{actualBounds.Y})");

            // Build a single-screen canvas for this monitor's service.
            // D2DCompositionService.InitializeAsync loops over ScreenMappings and spawns one
            // player per entry — passing the full multi-screen layout would cause it to
            // spawn N players per service (N² total), all positioned at the same actualBounds.
            // Each service must own exactly ONE player for its own monitor.
            var thisCfg = new WallpaperEngine.Composition.ScreenConfiguration
            {
                ClientId = planned.Node.Id,
                Width = planned.WidthPx,
                Height = planned.HeightPx,
                Order = planned.Node.Order,
                PhysicalDistanceCm = planned.Node.GapBeforeCm,
                Hostname = planned.Node.Hostname,
                MonitorIndex = planned.Node.MonitorIndex,
                PixelsPerCm = planned.PixelsPerCm
            };
            var singleCanvas = new VirtualCanvasManager(AppLogger.CreateLogger<VirtualCanvasManager>());
            singleCanvas.CalculateLayout(new[] { thisCfg });

            // Sequential layers play on this node's seat-map layout; the player builds the per-monitor canvas of
            // Simultaneous layers itself.
            var nodeLayout = layout.Get(planned.Node.Id);

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
                effScene.Background,
                layers,
                actualBounds,
                monitorIndex,
                nodeLayout);

            // Subscribe for lap-completion recompute in sequential IconZone mode
            if (bottom != null && bottomSequential && config.Background.Mode == BackgroundMode.IconZone)
                d2dService.GlobalLapCompleted += OnSequentialLapCompleted;

            newServices.Add((monitorIndex, d2dService));
            Debug.WriteLine($"[CrossScreen] Monitor {monitorIndex} initialized (offsetX={nodeLayout?.OffsetX ?? 0}, vcw={layout.CanvasWidth})");
        }
        }
        catch
        {
            await DisposeUnstartedAsync(newServices);
            _retiring.RetireAt(retiring, 0, NowMs);   // failed switch: the old players go now (as before)
            throw;
        }

        // Brief pause to let all players finish loading
        await Task.Delay(200);

        // Phase 2: Start ALL players with same shared timestamp.
        // Background always renders on all monitors simultaneously. Each layer's Distribution controls its positioning:
        //   - Sequential: the layer spans the virtual canvas (multi-monitor spanning)
        //   - Simultaneous: the layer plays independently on each monitor
        // Tier 0.4: place the shared start in the future so remotes have received the command,
        // downloaded, spawned and decoded before the first frame — every node then starts on the
        // same frame instead of popping in mid-lap. Lead = 3×worst RTT + the local load time we
        // just measured, clamped to [800, 4000] ms.
        double maxRttMs = plan.MaxRemoteRttMs;
        _activeMaxRttMs = maxRttMs;   // events fired during this scene use the same lead rule
        int startLeadMs = SyncTiming.ComputeStartLeadMs(maxRttMs, (int)phase1Timer.ElapsedMilliseconds);
        var sharedStartTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + startLeadMs;
        var pixelsPerSecond = effMovement.Type == MovementType.Static
            ? 0
            : (int)effMovement.SpeedPixelsPerSecond;
        Debug.WriteLine($"[CrossScreen] Shared start scheduled {startLeadMs}ms ahead (maxRtt={maxRttMs:F0}ms, load={phase1Timer.ElapsedMilliseconds}ms)");

        Debug.WriteLine($"[CrossScreen] Starting {layers.Count} layer(s) on {newServices.Count} monitors, timestamp={sharedStartTimestamp}ms, bottom speed={pixelsPerSecond}px/s");

        try
        {
            foreach (var (monitorIndex, d2dService) in newServices)
            {
                await d2dService.SendTransitionAsync(incoming: true, transitionWire, sharedStartTimestamp);   // before its start
                await d2dService.StartAsync(startTimestampMs: sharedStartTimestamp, pixelsPerSecond: pixelsPerSecond);
                _players[monitorIndex] = d2dService;
                await _agenda.SendToAsync(monitorIndex, d2dService);
            }
            // Stand-ins under the new players retire at the shared start (no blink, no teardown on the start path).
            retiring.AddRange(_agenda.TakeStandIns(newServices.Select(s => s.monitorIndex)));
        }
        catch
        {
            await DisposeUnstartedAsync(newServices);
            _retiring.RetireAt(retiring, 0, NowMs);   // failed switch: the old players go now (as before)
            throw;
        }

        foreach (var old in retiring) await old.SendTransitionAsync(incoming: false, transitionWire, sharedStartTimestamp);
        _retiring.RetireAt(retiring, sharedStartTimestamp + TransitionMath.OutgoingHoldMs(transition ?? SceneTransition.Cut), NowMs);

        // An event still running (e.g. across a playlist item switch) continues on the new players at its position.
        await ReplayEventsAsync(newServices.Select(s => s.service));

        // Phase 3: Send cross-screen D2D commands to remote clients. The bottom layer's file is the command's primary
        // content (a background-only scene, e.g. a lone panorama, sends its background image); every other file travels as an asset.
        var primaryPath = layers.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Animation.AnimationPath))?.Animation.AnimationPath
            ?? (effScene.Background.UsesImage && !string.IsNullOrWhiteSpace(effScene.Background.ImagePath) ? effScene.Background.ImagePath : null);
        if (remoteClients.Count > 0 && _service.SyncCoordinator != null && primaryPath == null)
        {
            // ponytail: a scene without any file (solid color only) is not sent to remotes (the LOAD needs a primary file).
            Debug.WriteLine("[CrossScreen] Scene without files: remote nodes keep what they show");
        }
        else if (remoteClients.Count + plan.Ghosts.Count > 0 && _service.SyncCoordinator != null)
        {
            // Tier 1.3: ids identify bytes (name + hash) so renamed/re-exported files are never served stale.
            var contentId = ContentIdentity.ComputeId(primaryPath!);
            var sceneAssets = BuildAssetRefs(config);
            _crossScreenContentId = contentId;
            var sentScene = new CrossScreenConfig { Background = effScene.Background, Layers = layers };

            // All sends at once: a slow client (bounded by the write timeout) must not push the others past the shared start.
            var sends = new List<Task>();
            // Ghost seats get their command parked, not sent: a machine that comes back resumes at its seat mid-scene.
            foreach (var (remote, park) in remoteClients.Select(r => (r, false)).Concat(plan.Ghosts.Select(g => (g, true))))
            {
                var remoteClient = remote.Node;
                var remoteLayout = layout.Get(remoteClient.Id);

                Debug.WriteLine($"[CrossScreen] Sending cross-screen D2D to remote {remoteClient.Hostname} ({remoteClient.Id}): offset={remoteLayout?.OffsetX ?? 0}px, canvas={layout.CanvasWidth}px");

                sends.Add(_service.SyncCoordinator.StartCrossScreenD2DOnClientAsync(
                    clientId: remoteClient.Id,
                    contentId: contentId,
                    filePath: primaryPath!,
                    // Resolved scene incl. the precomputed global A* path, so remotes compute identical deterministic math.
                    scene: sentScene,
                    layout: remoteLayout,
                    sharedStartTimestampMs: sharedStartTimestamp,
                    // MonitorIndex is -1 for whole-machine nodes → render on primary.
                    targetMonitorIndex: Math.Max(0, remoteClient.MonitorIndex),
                    assets: sceneAssets,
                    parkOnly: park,
                    transition: transitionWire));
            }
            await Task.WhenAll(sends);
        }
        else if (remoteClients.Count > 0)
        {
            Debug.WriteLine("[CrossScreen] WARNING: Remote clients selected but server not running - cannot send cross-screen commands");
        }

        StartFullscreenDetectionIfNeeded();

        // Set up thumbnail capture for live preview in topology nodes
        var animName = string.IsNullOrEmpty(primaryPath) ? "Animation" : Path.GetFileName(primaryPath);
        foreach (var (monitorIndex, d2dService) in newServices)
        {
            SetupThumbnailCapture(monitorIndex, d2dService.PlayerHwnd, animName);
        }

        // Lap-snap geometry: the bottom layer's sprite as drawn (first screen for the fit modes).
        var firstBounds = screens.Length > 0 ? screens[0].Bounds : new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var contentSize = bottom == null ? (W: 0, H: 0) : ComputeEffectiveAnimationSize(bottom.Animation, firstBounds.Width, firstBounds.Height);

        // Feed the live preview: same layout, same config, same shared epoch as the players.
        // Epoch and layout first so the room never paints the new scene on a stale clock/layout.
        ActiveSharedStartMs = sharedStartTimestamp;
        ActiveStartLeadMs = startLeadMs;
        ActiveEffectiveMovement = effMovement;
        ActiveLayout = layout;
        ActiveScene = config;
        OnPropertyChanged(nameof(HasActiveRenderer));   // a show item never sets IsCrossScreenRunning
        _ = SyncAgendaAsync();   // after the remotes got their LOAD: stand-ins where this scene left a monitor

        return new CrossScreenApplyResult
        {
            VirtualCanvasWidth = layout.CanvasWidth,
            ContentWidthPx = contentSize.W,
            ContentHeightPx = contentSize.H,
            CanvasHeight = layout.CanvasHeight,
            EffectiveSpeedPx = effMovement.SpeedPixelsPerSecond,
            StartLeadMs = startLeadMs,
            SharedStartTimestampMs = sharedStartTimestamp,
            CanvasWraps = layout.Wraps,
        };
    }

    /// <summary>
    /// A failed apply: stop and dispose every player it created (registered or not), so no player process
    /// is left running untracked; the scene then counts as not running.
    /// </summary>
    private async Task DisposeUnstartedAsync(List<(int monitorIndex, D2DCompositionService service)> created)
    {
        foreach (var (monitorIndex, service) in created)
        {
            _players.TryRemove(new KeyValuePair<int, D2DCompositionService>(monitorIndex, service));
            service.GlobalLapCompleted -= OnSequentialLapCompleted;
            try { await service.StopAsync(); } catch { /* never started */ }
            try { service.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[CrossScreen] Dispose after failed apply: {ex.Message}"); }
        }
        await SyncAgendaAsync();
    }

    /// <summary>
    /// Before an apply: stop what plays on nodes the new scene does not cover — local players on other
    /// monitors, remote seats of the scene before it. A show item on fewer seats than the last one would
    /// otherwise leave the old item running there while the room shows those seats as idle.
    /// </summary>
    private async Task StopNodesOutsideAsync(HashSet<int> localMonitors, HashSet<string> remoteIds)
    {
        foreach (var kvp in _players.ToArray())
        {
            if (localMonitors.Contains(kvp.Key) || !_players.TryRemove(kvp)) continue;
            kvp.Value.GlobalLapCompleted -= OnSequentialLapCompleted;
            try { await kvp.Value.StopAsync(); } catch (Exception ex) { Debug.WriteLine($"[CrossScreen] Stop monitor {kvp.Key}: {ex.Message}"); }
            try { kvp.Value.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[CrossScreen] Dispose monitor {kvp.Key}: {ex.Message}"); }
        }

        var previous = ActiveScene ?? LastScene;
        if (previous == null || _service.SyncCoordinator == null || string.IsNullOrEmpty(_crossScreenContentId)) return;
        foreach (var id in RemoteTargetIds(previous).Where(id => !remoteIds.Contains(id)))
        {
            try { await _service.SyncCoordinator.StopCrossScreenOnClientAsync(id, _crossScreenContentId); }
            catch (Exception ex) { Debug.WriteLine($"[CrossScreen] Remote stop {id}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Computes the effective rendered animation height in pixels for a given FitMode,
    /// matching the sizing logic in Player.D2D's CalculateAnimationLayout. Used to size
    /// the server-side A* path padding to the actual animation bitmap.
    /// </summary>
    // ponytail: native size via System.Drawing (images / GIFs); a video falls back to TargetHeight × TargetHeight —
    // only the IconZone padding and lap-snap use this, read the size from the player's READY if they need it exact.
    private static (int W, int H) ComputeEffectiveAnimationSize(AnimationLayerConfig anim, int screenWidth, int screenHeight)
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
                return (anim.TargetHeight, anim.TargetHeight);

            double aspect = nativeW / (double)nativeH;
            if (anim.FitMode == ContentFitMode.Stretch) return (screenWidth, screenHeight);
            int h = anim.FitMode switch
            {
                ContentFitMode.Center  => nativeH,
                ContentFitMode.Fit     => (int)(nativeH * Math.Min((double)screenWidth / nativeW, (double)screenHeight / nativeH)),
                ContentFitMode.Fill    => (int)(nativeH * Math.Max((double)screenWidth / nativeW, (double)screenHeight / nativeH)),
                _                      => anim.TargetHeight
            };
            return ((int)Math.Round(h * aspect), h);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CrossScreen] Native-size probe failed for '{anim.AnimationPath}': {ex.Message}. Falling back to TargetHeight.");
            return (anim.TargetHeight, anim.TargetHeight);
        }
    }

    /// <summary>
    /// Called by each D2DCompositionService when a global lap completes in sequential IconZone mode.
    /// Recomputes the A* path with a new variation seed and broadcasts it to all players.
    /// </summary>
    /// <summary>The running scene also plays on remote clients (set per apply).</summary>
    private volatile bool _activeSceneHasRemotes;

    private void OnSequentialLapCompleted(object? sender, int lapNum)
    {
        lock (_seqLapLock)
        {
            if (lapNum <= _lastGlobalLap) return;
            _lastGlobalLap = lapNum;
        }

        // F3a: when pattern + IconZone are combined, the pattern fills the desktop and zones
        // are used as a clip mask only — no A* path is needed, so skip the recompute/broadcast.
        if (LastScene?.FirstLayer?.Animation.Pattern != null
            && LastScene.Background.Mode == BackgroundMode.IconZone)
        {
            return;
        }

        // Review 2026-10-13 E5: the new path only reaches this machine's players (remotes keep the path they got
        // with the scene), and each player switches when its message arrives — the wall would split into two
        // routes. With remote seats in the scene, keep the one shared path for the whole show.
        // ponytail: per-lap variation with remotes needs the path in a timed command (lap boundary as T0).
        if (_activeSceneHasRemotes)
        {
            Debug.WriteLine($"[SeqPath] Lap {lapNum}: remote seats in the scene — keeping the shared path");
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

                foreach (var (_, svc) in _players)
                    await svc.BroadcastNewPathAsync(newLayout.Path);

                Debug.WriteLine($"[SeqPath] Lap {lapNum}: broadcast {newLayout.Path.Count} waypoints");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SeqPath] Recompute failed for lap {lapNum}: {ex.Message}");
            }
        });
    }
}

/// <summary>
/// Metrics returned by applying a cross-screen config, used by the playlist orchestrator
/// to compute lap-snap timing. The content size is the bottom layer's sprite as the server estimates it.
/// </summary>
public sealed class CrossScreenApplyResult
{
    public int VirtualCanvasWidth { get; init; }
    public int ContentWidthPx { get; init; }
    public int ContentHeightPx { get; init; }
    public int CanvasHeight { get; init; }
    /// <summary>How far in the future the shared start was scheduled (Tier 0.4).</summary>
    public int StartLeadMs { get; init; }
    /// <summary>Canvas speed in px/s after cm→px resolution (Tier 1.2); drives playlist lap timing.</summary>
    public float EffectiveSpeedPx { get; init; }
    /// <summary>The shared UTC start timestamp all nodes were given.</summary>
    public long SharedStartTimestampMs { get; init; }
    /// <summary>Ring seat map: the sprite wraps instead of running in and out (lap = canvas width).</summary>
    public bool CanvasWraps { get; init; }
}

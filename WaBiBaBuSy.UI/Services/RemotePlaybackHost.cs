using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Native;
using WaBiBaBuSy.WallpaperEngine.Services;

namespace WaBiBaBuSy.UI.Services;

/// <summary>
/// Client mode: renders what the server sends on this machine — cross-screen D2D, plain D2D and Stop —
/// and feeds the thumbnail uploads and test probes from those players. App-lifetime (created once by the
/// tray), so it works for every connect path (tray, main window, reconnects) whether or not the main window
/// is open; before 2026-10-03 this lived in the main window's view model and a client connected from the
/// tray fell back to unsynchronized plain D2D.
/// </summary>
public sealed class RemotePlaybackHost
{
    private readonly WaBiBaBuSyService _service;
    private readonly DesktopWindowManager _desktopManager;
    private readonly ConcurrentDictionary<int, D2DCompositionService> _players = new();
    private readonly ShowEventLog _showEvents = new();
    /// <summary>Players of the previous scene, kept until the new ones appear (standby switch).</summary>
    private readonly RetiringPlayers _retiring = new();
    // Apply / stop run on separate command tasks: one at a time, so two applies for one monitor cannot both
    // create a player (one orphaned) and a stop cannot slip in before a running apply registers its player.
    private readonly System.Threading.SemaphoreSlim _gate = new(1, 1);

    public RemotePlaybackHost(WaBiBaBuSyService service, DesktopWindowManager desktopManager)
    {
        _service = service;
        _desktopManager = desktopManager;
        _agenda = new AgendaDisplay(desktopManager, AgendaNodeIds);
        _bombInput = new BombInputWatcher((game, action, cable) =>
            _service.Client?.ReportShowActionAsync(game, action, cable) ?? Task.FromResult(false));
    }

    /// <summary>BiBaBo: clicks and the Recycle Bin while this machine holds the bomb.</summary>
    private readonly BombInputWatcher _bombInput;

    /// <summary>The seat-map layout each monitor's player was started with (BiBaBo hit-testing).</summary>
    private readonly ConcurrentDictionary<int, NodeLayout> _layouts = new();

    /// <summary>A Bomb on one of this machine's monitors starts the input watcher; its outcome stops it.</summary>
    private void WatchBombInput(ShowEvent evt)
    {
        if (evt.Type == ShowEventType.BombOutcome && evt.Bomb is { } done && _bombInput.IsWatching(done.GameId))
        {
            _bombInput.Stop();
            return;
        }
        if (evt.Type != ShowEventType.Bomb || evt.Bomb is not { } game) return;
        foreach (var (monitor, layout) in _layouts)
            if (layout.Id == game.BombNodeId)   // the layout the server sent carries the node id
            {
                _bombInput.Start(evt, layout, monitor);
                return;
            }
    }

    /// <summary>The room agenda on this client's monitors (stand-ins where no scene plays).</summary>
    private readonly AgendaDisplay _agenda;

    /// <summary>The room ids a monitor of this client may have: the whole machine, or one of its expanded monitors.</summary>
    private IReadOnlyList<string> AgendaNodeIds(int monitorIndex)
    {
        var id = _service.Client?.ClientId;
        return string.IsNullOrEmpty(id) ? Array.Empty<string>() : new[] { id, $"{id}_MONITOR_{monitorIndex}" };
    }

    /// <summary>AGENDA from the server (times in this machine's clock), or null after an explicit disconnect.</summary>
    public Task OnAgendaAsync(Agenda? agenda) => SerializedAsync(() => _agenda.SetAsync(agenda, _players));

    /// <summary>True while agenda stand-ins run on this machine's idle monitors.</summary>
    public bool HasAgendaStandIns => _agenda.HasStandIns;

    /// <summary>Agenda stand-ins started or stopped (any thread): fullscreen detection follows them.</summary>
    public event Action? AgendaStandInsChanged
    {
        add => _agenda.StandInsChanged += value;
        remove => _agenda.StandInsChanged -= value;
    }

    /// <summary>Stop the agenda stand-ins (Clear all kills every player process; app exit).</summary>
    public Task StopAgendaStandInsAsync() => SerializedAsync(_agenda.StopAllAsync);

    /// <summary>Stand-ins back on the monitors without a scene (after Clear all).</summary>
    public Task ReconcileAgendaAsync() => SerializedAsync(() => _agenda.ReconcileAsync(_players));

    /// <summary>The capture service for this machine's thumbnail uploads (the remote players' windows).</summary>
    public ThumbnailCaptureService ThumbnailCapture { get; } = new(AppLogger.CreateLogger<ThumbnailCaptureService>());

    /// <summary>True while at least one remote-driven player runs on this machine.</summary>
    public bool HasPlayers => !_players.IsEmpty;

    /// <summary>
    /// Register the render delegates, probe targets and thumbnail capture on the service. Kept across
    /// connections, so call once at startup — before any connect.
    /// </summary>
    public void Register()
    {
        _service.SetD2DApplyDelegate(ApplyPlainAsync);
        _service.SetD2DCrossScreenApplyDelegate(ApplyCrossScreenAsync);
        _service.SetD2DCrossScreenStopDelegate(StopAllAsync);
        _service.ClientTestProbeTargets = () => _players.Values.Cast<ITestProbeTarget>().ToList();
        _service.ClientThumbnailCapture = ThumbnailCapture;
        _service.SetShowEventDelegate(OnShowEventAsync);
        _service.SetAgendaDelegate(OnAgendaAsync);
        _service.BusyMonitorsProvider = MonitorBusyProbe.BusyMonitors;   // BiBaBo skips seats a window covers
    }

    /// <summary>EVENT from the server (T0 in this machine's clock, image in the cache): keep it and send it to every player.</summary>
    public async Task OnShowEventAsync(ShowEvent evt)
    {
        if (!_showEvents.Add(evt)) return;
        WatchBombInput(evt);
        foreach (var player in _players.Values)
        {
            try { await player.SendShowEventAsync(evt); }
            catch (Exception ex) { Debug.WriteLine($"[Events] Player send failed: {ex.Message}"); }
        }
    }

    /// <summary>A new player continues the events that have not ended (playlist item switch, late start).</summary>
    private async Task ReplayEventsAsync(D2DCompositionService player)
    {
        long now = _service.Client?.NowUtcMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var evt in _showEvents.Active(now))
        {
            try { await player.SendShowEventAsync(evt); }
            catch (Exception ex) { Debug.WriteLine($"[Events] Replay failed: {ex.Message}"); }
        }
    }

    /// <summary>Hide or show every remote-driven player window (pause on fullscreen apps).</summary>
    public void SetPlayersVisible(bool visible)
    {
        foreach (var player in _players.Values)
            player.SetPlayersVisible(visible);
        _agenda.SetVisible(visible);
    }

    /// <summary>
    /// Cross-screen D2D apply delegate. Called when the server sends a CROSSSCREEN D2D command.
    /// Creates a D2D player on this client with the server-provided virtual canvas parameters
    /// so all nodes share the same coordinate space and start timestamp.
    /// </summary>
    public Task ApplyCrossScreenAsync(CrossScreenApplyRequest req) => SerializedAsync(() => ApplyCrossScreenCoreAsync(req));

    /// <summary>
    /// Stop delegate invoked when the server sends a Stop command for cross-screen D2D.
    /// Tears down every player started by ApplyCrossScreenAsync / ApplyPlainAsync.
    /// </summary>
    public Task StopAllAsync() => SerializedAsync(StopAllCoreAsync);

    /// <summary>
    /// D2D apply delegate called when the server sends a D2D LOAD command to this client.
    /// Creates a local D2DCompositionService and renders on this machine's desktop.
    /// </summary>
    public Task ApplyPlainAsync(string filePath, int monitorIndex, string backgroundColor, int fitMode) =>
        SerializedAsync(() => ApplyPlainCoreAsync(filePath, monitorIndex, backgroundColor, fitMode));

    private async Task SerializedAsync(Func<Task> action)
    {
        await _gate.WaitAsync();
        try { await action(); }
        finally { _gate.Release(); }
    }

    /// <summary>Initialize and start a new player; on failure it is disposed (its process would be orphaned otherwise).</summary>
    private static async Task StartOrDisposeAsync(D2DCompositionService player, Func<Task> initAndStart)
    {
        try { await initAndStart(); }
        catch
        {
            try { player.Dispose(); } catch { /* already broken */ }
            throw;
        }
    }

    private async Task ApplyCrossScreenCoreAsync(CrossScreenApplyRequest req)
    {
        var monitorIndex = req.MonitorIndex;
        Debug.WriteLine($"[D2D-CrossScreen] Received: file={req.FilePath}, monitor={monitorIndex}, ts={req.SharedStartTimestampMs}ms");

        // Standby switch: the previous player keeps showing until the new one appears at the shared start (a player
        // keeps its window hidden until then); it is retired after that, or right away if this apply fails.
        _players.TryRemove(monitorIndex, out var existing);
        var retiring = existing == null ? new List<D2DCompositionService>() : new List<D2DCompositionService> { existing };
        long NowMs() => _service.Client?.NowUtcMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool applied = false;
        try
        {
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

            // Build a single-screen canvas manager (used for player host init only; the canvas comes from the layout)
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

            // ── Scene (background + layers, cm already resolved): every server-side path is rewritten to this
            //    client's cache (Tier 1.3). A layer whose file could not be fetched is disabled here, a missing extra
            //    image falls back to its layer's file and a missing background image degrades to solid color. ──
            var remoteScene = DeserializeRemoteConfig<CrossScreenConfig>(req.SceneJson, "CrossScreenConfig");
            if (remoteScene == null)
            {
                Debug.WriteLine("[D2D-CrossScreen] ERROR: no scene in the LOAD command (server too old?) — nothing to play");
                return;
            }
            var scene = SceneAssets.RewriteToLocal(remoteScene, req.LocalAssetPaths, req.FilePath);
            Debug.WriteLine($"[D2D-CrossScreen] Assets: {req.LocalAssetPaths.Count} mapped, {scene.Layers.Count} layer(s) ({scene.Layers.Count(l => !l.Enabled)} disabled), bg={scene.Background.Mode}");

            // ── Node layout (seat map: offset, mirroring, ring wrap) — the canvas of Sequential layers ──
            var remoteNodeLayout = DeserializeRemoteConfig<NodeLayout>(req.LayoutJson, "NodeLayout");
            if (remoteNodeLayout != null) _layouts[monitorIndex] = remoteNodeLayout;

            Debug.WriteLine($"[D2D-CrossScreen] Initializing D2D service: bounds={actualBounds}, canvas={remoteNodeLayout?.CanvasWidth ?? 0}, offsetX={remoteNodeLayout?.OffsetX ?? 0}");
            var d2dService = new D2DCompositionService(
                AppLogger.CreateLogger<D2DCompositionService>(),
                AppLogger.Factory,
                _desktopManager);

            // Test mode (timecode / simulated skew) must reach a new player before its start command:
            // the skew shifts the player's clock, and the start is converted with the skewed offset.
            var testMode = _service.Client?.TestModeState ?? default;

            // Same speed convention as the local start path: the bottom layer's speed, Static → 0.
            var bottom = scene.FirstLayer?.Movement;
            var pixelsPerSecond = bottom == null || bottom.Type == MovementType.Static ? 0 : (int)bottom.SpeedPixelsPerSecond;

            await StartOrDisposeAsync(d2dService, async () =>
            {
                await d2dService.InitializeAsync(canvasManager, scene.Background, scene.Layers, actualBounds, monitorIndex, remoteNodeLayout);

                await Task.Delay(100);

                if (testMode.Timecode || testMode.ClockSkewMs != 0)
                    await d2dService.SetTestModeAsync(testMode.Timecode, testMode.ClockSkewMs);

                await d2dService.SendTransitionAsync(incoming: true, req.TransitionEffect, req.SharedStartTimestampMs);   // before its start

                Debug.WriteLine($"[D2D-CrossScreen] Starting playback at ts={req.SharedStartTimestampMs}ms, speed={pixelsPerSecond}px/s");
                await d2dService.StartAsync(startTimestampMs: req.SharedStartTimestampMs, pixelsPerSecond: pixelsPerSecond);
            });

            _players[monitorIndex] = d2dService;
            applied = true;
            await _agenda.SendToAsync(monitorIndex, d2dService);
            retiring.AddRange(_agenda.TakeStandIns(new[] { monitorIndex }));   // the panel stays until the new player appears
            foreach (var old in retiring) await old.SendTransitionAsync(incoming: false, req.TransitionEffect, req.SharedStartTimestampMs);
            _retiring.RetireAt(retiring, req.SharedStartTimestampMs + TransitionMath.OutgoingHoldMs(SceneTransition.Parse(req.TransitionEffect)), NowMs);
            await ReplayEventsAsync(d2dService);
            await CatchUpTestModeAsync(d2dService, testMode);
            AdoptRemoteThumbnail(monitorIndex, d2dService, req.FilePath);

            Debug.WriteLine($"[D2D-CrossScreen] SUCCESS: Cross-screen D2D started on monitor {monitorIndex}");
        }
        finally
        {
            if (!applied)
            {
                _retiring.RetireAt(retiring, 0, NowMs);   // no new player: the old one goes now (as before)
                await _agenda.ReconcileAsync(_players);   // …and a stand-in keeps the agenda on the bare monitor
            }
        }
    }

    /// <summary>
    /// Remote thumbnails: the client uploads this player's window — the primary monitor's when
    /// several play, otherwise whichever player starts while no live window is assigned.
    /// </summary>
    private void AdoptRemoteThumbnail(int monitorIndex, D2DCompositionService d2dService, string filePath)
    {
        if (ThumbnailCapture.ShouldAdopt(monitorIndex))
            ThumbnailCapture.SetWallpaperHwnd(d2dService.PlayerHwnd, Path.GetFileName(filePath));
    }

    /// <summary>
    /// A TEST_MODE that arrived while this player was being created fanned out without it (it was
    /// not registered yet): send the current state now if it differs from what the player got.
    /// </summary>
    private async Task CatchUpTestModeAsync(D2DCompositionService d2dService, (bool Timecode, int ClockSkewMs) applied)
    {
        var current = _service.Client?.TestModeState ?? default;
        if (current != applied)
            await d2dService.SetTestModeAsync(current.Timecode, current.ClockSkewMs);
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

    private async Task StopAllCoreAsync()
    {
        Debug.WriteLine($"[D2D-CrossScreen] Stopping {_players.Count} remote D2D service(s)");
        await _retiring.StopAllNowAsync();
        foreach (var kvp in _players)
        {
            try { await kvp.Value.StopAsync(); } catch { }
            try { kvp.Value.Dispose(); } catch { }
        }
        _players.Clear();
        _showEvents.Clear();
        _bombInput.Stop();
        ThumbnailCapture.ClearWallpaperHwnd();
        await _agenda.ReconcileAsync(_players);   // the agenda stays: stand-ins take over the now idle monitors
        Debug.WriteLine("[D2D-CrossScreen] All remote D2D services stopped");
    }

    private async Task ApplyPlainCoreAsync(string filePath, int monitorIndex, string backgroundColor, int fitMode)
    {
        bool applied = false;
        try
        {
            applied = await ApplyPlainBodyAsync(filePath, monitorIndex, backgroundColor, fitMode);
        }
        finally
        {
            if (!applied) await _agenda.ReconcileAsync(_players);   // a failed apply leaves a stand-in, not a bare monitor
        }
    }

    private async Task<bool> ApplyPlainBodyAsync(string filePath, int monitorIndex, string backgroundColor, int fitMode)
    {
        Debug.WriteLine($"[D2D-Remote] ApplyD2DFromRemoteAsync called: file={filePath}, monitor={monitorIndex}, bg={backgroundColor}, fit={fitMode}");

        // Dispose previous service for this monitor
        if (_players.TryRemove(monitorIndex, out var existing))
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
            return false;
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

        Debug.WriteLine($"[D2D-Remote] Creating D2DCompositionService for monitor {monitorIndex}");
        var d2dService = new D2DCompositionService(
            AppLogger.CreateLogger<D2DCompositionService>(),
            AppLogger.Factory,
            _desktopManager);

        var actualBounds = new System.Drawing.Rectangle(
            screen.Bounds.X, screen.Bounds.Y,
            screen.Bounds.Width, screen.Bounds.Height);

        // Same test-mode handling as the cross-screen path: state before the start, catch-up after registering.
        var testMode = _service.Client?.TestModeState ?? default;
        await StartOrDisposeAsync(d2dService, async () =>
        {
            Debug.WriteLine($"[D2D-Remote] Initializing D2D service with bounds {actualBounds}");
            await d2dService.InitializeAsync(canvasManager, backgroundConfig, new[] { layer }, actualBounds, monitorIndex);
            await Task.Delay(100);
            if (testMode.Timecode || testMode.ClockSkewMs != 0)
                await d2dService.SetTestModeAsync(testMode.Timecode, testMode.ClockSkewMs);
            Debug.WriteLine($"[D2D-Remote] Starting D2D playback");
            await d2dService.StartAsync(startTimestampMs: 0, pixelsPerSecond: 0);
        });

        _players[monitorIndex] = d2dService;
        await _agenda.SendToAsync(monitorIndex, d2dService);
        await _agenda.ReconcileAsync(_players);
        await ReplayEventsAsync(d2dService);
        await CatchUpTestModeAsync(d2dService, testMode);
        AdoptRemoteThumbnail(monitorIndex, d2dService, filePath);
        Debug.WriteLine($"[D2D-Remote] SUCCESS: Applied D2D wallpaper '{Path.GetFileName(filePath)}' on monitor {monitorIndex}");
        return true;
    }
}

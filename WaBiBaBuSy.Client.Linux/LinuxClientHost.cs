using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using WaBiBaBuSy.Client.Linux.Platform;
using WaBiBaBuSy.Client.Linux.Rendering;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Core.Services.Networking;
using MonitorInfo = WaBiBaBuSy.Grpc.MonitorInfo;
using ScreenConfiguration = WaBiBaBuSy.Grpc.ScreenConfiguration;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;

namespace WaBiBaBuSy.Client.Linux;

/// <summary>
/// Headless Linux client: discovers/connects to the WaBiBaBuSy server and renders whatever it
/// sends on this machine's monitors. Plays the role of the Windows RemotePlaybackHost (scenes, show events,
/// BiBaBo input, agenda + stand-ins) and the client side of the tray (connect, pause on fullscreen), minus the UI.
/// </summary>
public sealed class LinuxClientHost : IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly ILogger<LinuxClientHost> _logger = AppLogger.CreateLogger<LinuxClientHost>();
    private readonly X11Desktop _x11;
    private readonly IReadOnlyList<MonitorGeometry> _monitors;
    private readonly ConcurrentDictionary<int, (WallpaperScene Scene, WallpaperWindow Window)> _outputs = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);   // scene starts / stops / agenda reconcile, one at a time
    private ClientConfiguration _clientConfig = new();
    private WaBiBaBuSyService? _service;

    // Monitors playing a server scene (the rest may run an agenda stand-in)
    private readonly ConcurrentDictionary<int, bool> _sceneMonitors = new();
    private readonly ConcurrentDictionary<int, bool> _standIns = new();

    // Show events seen so far: replayed to an output created later (pruned once they ended)
    private readonly ConcurrentDictionary<string, ShowEvent> _showEvents = new();
    private Agenda? _agenda;

    // BiBaBo: the game this machine holds the bomb of, and the monitor it sits on
    private string? _bombGameId;
    private int _bombMonitor = -1;
    private CancellationTokenSource? _bombCts;

    public LinuxClientHost(ClientOptions options)
    {
        _options = options;
        _x11 = new X11Desktop(AppLogger.CreateLogger<X11Desktop>());
        _monitors = _x11.GetMonitors();
        for (int i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            _logger.LogInformation("Monitor {Index}: {Name} {W}x{H} at ({X},{Y}) {Hz}Hz{Primary}",
                i, m.Name, m.Width, m.Height, m.X, m.Y, m.RefreshHz, m.IsPrimary ? " (primary)" : "");
        }

        // Report the same left-to-right monitor layout a Windows client would
        WallpaperSyncClient.ScreenConfigurationProvider = BuildScreenConfiguration;
    }

    public Task StartAsync()
    {
        _ = Task.Run(() => DesktopWatchLoopAsync(_cts.Token));

        if (_options.Demo)
            return StartDemoAsync();

        _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    // ════════════════════════════════════════════════════════════════════
    // Connection
    // ════════════════════════════════════════════════════════════════════

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var serverConfig = ConfigurationManager.LoadServerConfiguration();
        _clientConfig = ConfigurationManager.LoadClientConfiguration();
        _service = new WaBiBaBuSyService(AppLogger.CreateLogger<WaBiBaBuSyService>(), serverConfig, _clientConfig);
        _service.ClientConnectionStatusChanged += (_, e) =>
            _logger.LogInformation("Connection status: {Connected}", e.IsConnected ? "connected" : "disconnected");

        // Everything the server may send, kept by the service for every (re)connect
        _service.SetD2DCrossScreenApplyDelegate(ApplyCrossScreenAsync);
        _service.SetD2DCrossScreenStopDelegate(StopAllAsync);
        _service.SetShowEventDelegate(OnShowEventAsync);
        _service.SetAgendaDelegate(OnAgendaAsync);
        // BiBaBo skips seats a window covers (read at connect, so before it)
        _service.BusyMonitorsProvider = () =>
        {
            try { return _x11.BusyMonitors(_monitors); }
            catch (Exception ex) { _logger.LogDebug(ex, "Busy-monitor probe failed"); return Array.Empty<int>(); }
        };

        // Initial connect; after that WallpaperSyncClient reconnects on its own (1..30 s backoff)
        while (!ct.IsCancellationRequested)
        {
            bool connected;
            if (_options.ServerAddress != null)
            {
                connected = await _service.ConnectToServerAsync(_options.ServerAddress, _options.ServerPort);
                if (connected) _service.SetD2DApplyDelegate(ApplyPlainAsync);
            }
            else
            {
                var fallback = string.IsNullOrEmpty(_clientConfig.ServerAddress) ? "localhost" : _clientConfig.ServerAddress;
                connected = await _service.DiscoverAndConnectAsync(fallback, _clientConfig.ServerPort, ApplyPlainAsync);
            }

            if (connected)
            {
                _logger.LogInformation("Connected — waiting for the server to start an animation");
                return;
            }

            _logger.LogWarning("No server found ({Reason}), retrying in 5 s (use --server HOST[:PORT] to skip discovery)",
                _service.LastConnectError ?? "no answer");
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Every 0.5 s keep the wallpaper above the shell's own desktop window; every 2 s pause the monitors a fullscreen
    /// app covers (mirrors the Windows FullscreenDetectionService + PauseOnlyFullscreenMonitor).
    /// </summary>
    private async Task DesktopWatchLoopAsync(CancellationToken ct)
    {
        bool loggedRaise = false;
        IReadOnlyList<int> lastFullscreen = Array.Empty<int>();
        for (long tick = 0; !ct.IsCancellationRequested; tick++)
        {
            try
            {
                await Task.Delay(500, ct);

                if (_options.Placement == WindowPlacement.Desktop && !_outputs.IsEmpty
                    && _x11.KeepAboveOtherDesktopWindows() && !loggedRaise)
                {
                    loggedRaise = true;
                    _logger.LogInformation("Another desktop window (e.g. plasmashell) was above the wallpaper — raised it back (logged once)");
                }

                if (tick % 4 != 0) continue;
                var fullscreen = _clientConfig.PauseOnFullscreen ? _x11.FullscreenMonitors(_monitors) : Array.Empty<int>();
                if (!fullscreen.SequenceEqual(lastFullscreen))
                    _logger.LogInformation("Fullscreen monitors: [{Monitors}]", string.Join(", ", fullscreen));
                lastFullscreen = fullscreen;
                bool onlyThat = _clientConfig.PauseOnlyFullscreenMonitor;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Re-applied every check, so outputs started in between pause too
                    foreach (var (monitor, (_, window)) in _outputs)
                        window.Paused = BusyRule.IsPausedMonitor(monitor, fullscreen, onlyThat);
                });
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Desktop watch check failed");
            }
        }
    }

    private ScreenConfiguration BuildScreenConfiguration()
    {
        var config = new ScreenConfiguration { MonitorCount = _monitors.Count };
        if (_monitors.Count > 0)
        {
            config.TotalWidth = _monitors.Max(m => m.X + m.Width) - _monitors.Min(m => m.X);
            config.TotalHeight = _monitors.Max(m => m.Y + m.Height) - _monitors.Min(m => m.Y);
        }
        for (int i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            config.Monitors.Add(new MonitorInfo
            {
                Index = i,
                Width = m.Width,
                Height = m.Height,
                X = m.X,
                Y = m.Y,
                IsPrimary = m.IsPrimary,
                DeviceName = m.Name,
                RefreshRate = m.RefreshHz,
                PixelsPerCm = m.WidthMm > 0 ? m.Width / (m.WidthMm / 10f) : 0f
            });
        }
        return config;
    }

    // ════════════════════════════════════════════════════════════════════
    // Scenes (mirror RemotePlaybackHost.ApplyCrossScreenCoreAsync / ApplyPlainCoreAsync / StopAllCoreAsync)
    // ════════════════════════════════════════════════════════════════════

    private async Task ApplyCrossScreenAsync(CrossScreenApplyRequest req)
    {
        int monitorIndex = req.MonitorIndex;
        _logger.LogInformation("[CrossScreen] file={File} monitor={Monitor} ts={Ts} transition={Transition}",
            Path.GetFileName(req.FilePath), monitorIndex, req.SharedStartTimestampMs, req.TransitionEffect);
        if (monitorIndex < 0 || monitorIndex >= _monitors.Count)
        {
            _logger.LogError("[CrossScreen] Invalid monitor index {Index} (have {Count})", monitorIndex, _monitors.Count);
            return;
        }

        // Scene (background + layers, cm already resolved): every server-side path is rewritten to this client's cache.
        // A layer whose file could not be fetched is disabled; a missing background image degrades to solid colour.
        var remoteScene = Deserialize<CrossScreenConfig>(req.SceneJson);
        if (remoteScene == null)
        {
            _logger.LogError("[CrossScreen] No scene in the LOAD command (server too old?) — nothing to play");
            return;
        }
        var scene = SceneAssets.RewriteToLocal(remoteScene, req.LocalAssetPaths, req.FilePath);
        var layout = Deserialize<NodeLayout>(req.LayoutJson);   // the canvas of Sequential layers
        _logger.LogInformation("[CrossScreen] {Count} layer(s) ({Disabled} disabled), bg={Bg}, seat={Seat}",
            scene.Layers.Count, scene.Layers.Count(l => !l.Enabled), scene.Background.Mode, layout?.Id ?? "(this monitor)");

        var bottom = scene.FirstLayer?.Movement;
        int pixelsPerSecond = bottom == null || bottom.Type == MovementType.Static ? 0 : (int)bottom.SpeedPixelsPerSecond;
        await StartSceneAsync(monitorIndex, LoadCommand(monitorIndex, scene.Background, scene.Layers, layout),
            new PlayerCommandStartAnimation { StartTimestampMs = req.SharedStartTimestampMs, PixelsPerSecond = pixelsPerSecond },
            req.TransitionEffect);
    }

    /// <summary>Plain "d2d" LOAD: one centred, static wallpaper on a solid background.</summary>
    private Task ApplyPlainAsync(string filePath, int monitorIndex, string backgroundColor, int fitMode)
    {
        if (monitorIndex < 0 || monitorIndex >= _monitors.Count) return Task.CompletedTask;
        var layer = new SceneLayer
        {
            Animation = new AnimationLayerConfig
            {
                AnimationPath = filePath,
                TargetHeight = _monitors[monitorIndex].Height,
                Loop = true,
                VerticalAlign = VerticalAlignment.Center,
                CenterInitialPosition = true,
                FitMode = (ContentFitMode)fitMode
            },
            Distribution = AnimationDistributionMode.Simultaneous,
        };
        var background = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = backgroundColor };
        return StartSceneAsync(monitorIndex, LoadCommand(monitorIndex, background, new[] { layer }, null),
            new PlayerCommandStartAnimation(), transition: "");
    }

    /// <summary>The player command the Windows D2DCompositionService would send for this scene.</summary>
    private static PlayerCommandLoadAnimation LoadCommand(int monitorIndex, BackgroundLayerConfig background, IReadOnlyList<SceneLayer> layers, NodeLayout? layout)
    {
        // Pattern + IconZone: zones become a clip mask, no path (layer 0 only)
        bool maskZones = background.Mode == BackgroundMode.IconZone && layers.Count > 0 && layers[0].Animation.Pattern != null;
        return new PlayerCommandLoadAnimation
        {
            Layers = layers.ToList(),
            BackgroundConfig = background,
            MonitorIndex = monitorIndex,
            Layout = layout,
            MaskZones = maskZones,
            UseZonePalette = background.IconZonePaletteEnabled,
            IconFadePaddingPx = maskZones ? background.IconFadePaddingPx : 0,
            IconZoneExpansionPx = maskZones ? background.IconZoneExpansionPx : 0
        };
    }

    private async Task StartSceneAsync(int monitorIndex, PlayerCommandLoadAnimation load, PlayerCommandStartAnimation start, string transition)
    {
        await _gate.WaitAsync();
        try
        {
            var (scene, window) = await GetOrCreateOutputAsync(monitorIndex);
            // Decoding can take a while for big GIFs — off the UI thread; the old scene keeps playing meanwhile
            await Task.Run(() => scene.Load(load));
            scene.Start(start, transition);
            _sceneMonitors[monitorIndex] = true;
            _standIns.TryRemove(monitorIndex, out _);   // the scene replaces the stand-in at its start
            ApplyAgenda(monitorIndex);
            ReplayEvents(monitorIndex);
            // A bomb that arrived before its seat's scene (the BiBaBo stand-in starts together with the game)
            var events = _showEvents.Values.ToList();
            foreach (var bomb in events.Where(e => e.Type == ShowEventType.Bomb && e.EventId != _bombGameId))
                if (!events.Any(e => e.Type == ShowEventType.BombOutcome && e.Bomb?.GameId == bomb.EventId))
                    await WatchBombInputAsync(bomb);
            await ShowAtStartAsync(window, start.StartTimestampMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Scene] Monitor {Monitor}: could not start the scene", monitorIndex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A hidden window appears at the shared start (like a Windows player), a visible one keeps the old scene until then.</summary>
    private async Task ShowAtStartAsync(WallpaperWindow window, long startMs)
    {
        bool visible = await Dispatcher.UIThread.InvokeAsync(() => window.IsVisible);
        if (visible) return;
        long wait = startMs - (long)WallpaperScene.NowUnixMs();
        if (wait > 0) _ = Task.Delay((int)Math.Min(wait, 60_000)).ContinueWith(_ => Dispatcher.UIThread.Post(window.ShowWallpaper));
        else await Dispatcher.UIThread.InvokeAsync(window.ShowWallpaper);
    }

    private async Task StopAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _logger.LogInformation("[Stop] Stopping {Count} output(s)", _sceneMonitors.Count);
            foreach (var monitor in _sceneMonitors.Keys)
                if (_outputs.TryGetValue(monitor, out var output)) output.Scene.Stop();
            _sceneMonitors.Clear();
            _showEvents.Clear();
            await StopBombInputAsync();
            await ReconcileStandInsAsync();   // the agenda stays: stand-ins take over the now idle monitors
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(WallpaperScene Scene, WallpaperWindow Window)> GetOrCreateOutputAsync(int monitorIndex)
    {
        if (_outputs.TryGetValue(monitorIndex, out var existing)) return existing;

        var monitor = _monitors[monitorIndex];
        var output = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var scene = new WallpaperScene(monitor.Width, monitor.Height, AppLogger.CreateLogger($"WallpaperScene[{monitorIndex}]"));
            var window = new WallpaperWindow(scene, monitor, _x11, _options.Placement, AppLogger.CreateLogger<WallpaperWindow>());
            return (scene, window);
        });
        return _outputs.GetOrAdd(monitorIndex, output);
    }

    private T? Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to deserialize {Type}: {Message}", typeof(T).Name, ex.Message);
            return null;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Show events + BiBaBo input (mirror RemotePlaybackHost.OnShowEventAsync / WatchBombInput)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>EVENT from the server (T0 in this machine's clock, image in the cache): keep it and hand it to every monitor.</summary>
    private Task OnShowEventAsync(ShowEvent evt)
    {
        if (!_showEvents.TryAdd(evt.EventId, evt)) return Task.CompletedTask;
        long now = (long)WallpaperScene.NowUnixMs();
        foreach (var (id, e) in _showEvents)
            if (e.T0Utc + Math.Max(0, e.DurationMs) + 60_000 < now) _showEvents.TryRemove(id, out _);

        foreach (var (scene, _) in _outputs.Values) scene.AddShowEvent(evt);
        return WatchBombInputAsync(evt);
    }

    private void ReplayEvents(int monitorIndex)
    {
        if (!_outputs.TryGetValue(monitorIndex, out var output)) return;
        foreach (var evt in _showEvents.Values.OrderBy(e => e.T0Utc)) output.Scene.AddShowEvent(evt);
    }

    /// <summary>A Bomb on one of this machine's monitors makes that window take clicks and keys; its outcome stops it.</summary>
    private async Task WatchBombInputAsync(ShowEvent evt)
    {
        if (evt.Type == ShowEventType.BombOutcome && evt.Bomb is { } done && done.GameId == _bombGameId)
        {
            await StopBombInputAsync();
            return;
        }
        if (evt.Type != ShowEventType.Bomb || evt.Bomb is not { } game) return;

        foreach (var (monitor, (scene, window)) in _outputs)
        {
            if (scene.EventLayout is not { } node || node.Id != game.BombNodeId) continue;
            await StopBombInputAsync();
            _bombGameId = evt.EventId;
            _bombMonitor = monitor;
            var cts = _bombCts = new CancellationTokenSource();
            long untilMs = evt.T0Utc + game.IntroMs + game.TimerMs + 1000 - (long)WallpaperScene.NowUnixMs();
            cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, untilMs)));
            cts.Token.Register(() => Dispatcher.UIThread.Post(window.StopInput));
            _logger.LogInformation("[BiBaBo] The bomb is on monitor {Monitor} ({Node}) — watching clicks and keys", monitor, node.Id);

            await Dispatcher.UIThread.InvokeAsync(() => window.WatchInput(
                (x, y) =>
                {
                    if (cts.IsCancellationRequested) return;
                    if (game.Mode == BombMode.Keypad)
                    {
                        int key = BombHit.KeyAt(game, node, x, y);
                        if (key >= 0) Report(evt.EventId, "key", key);
                        return;
                    }
                    int cable = BombHit.CableAt(game, node, x, y);
                    _logger.LogDebug("[BiBaBo] Click at {X:F0},{Y:F0} → cable {Cable}", x, y, cable);
                    if (cable >= 0) Report(evt.EventId, "cut", cable);
                },
                key =>
                {
                    if (cts.IsCancellationRequested || game.Mode != BombMode.Keypad) return;
                    int k = BombKeys.FromKey(key);
                    if (k >= 0) Report(evt.EventId, "key", k);
                }));
            return;
        }
    }

    private void Report(string gameId, string action, int cable) => _ = Task.Run(async () =>
    {
        try
        {
            if (_service?.Client is { } client) await client.ReportShowActionAsync(gameId, action, cable);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[BiBaBo] Report {Action} failed", action); }
    });

    private async Task StopBombInputAsync()
    {
        _bombCts?.Cancel();
        _bombCts = null;
        _bombGameId = null;
        if (_bombMonitor >= 0 && _outputs.TryGetValue(_bombMonitor, out var output))
            await Dispatcher.UIThread.InvokeAsync(output.Window.StopInput);
        _bombMonitor = -1;
    }

    // ════════════════════════════════════════════════════════════════════
    // Agenda (mirror AgendaDisplay: every monitor gets it, idle monitors a stand-in with this desktop's wallpaper)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>AGENDA from the server (times in this machine's clock), or null after an explicit disconnect.</summary>
    private async Task OnAgendaAsync(Agenda? agenda)
    {
        _agenda = agenda;
        _logger.LogInformation("[Agenda] enabled={Enabled}, {Count} item(s)", agenda?.Enabled ?? false, agenda?.Items.Count ?? 0);
        await _gate.WaitAsync();
        try
        {
            foreach (var monitor in _outputs.Keys) ApplyAgenda(monitor);
            await ReconcileStandInsAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ApplyAgenda(int monitorIndex)
    {
        if (!_outputs.TryGetValue(monitorIndex, out var output)) return;
        var id = _service?.Client?.ClientId;
        var nodeIds = string.IsNullOrEmpty(id) ? Array.Empty<string>() : new[] { id, $"{id}_MONITOR_{monitorIndex}" };
        output.Scene.SetAgenda(_agenda, _agenda?.IsScreen(nodeIds) ?? false);
    }

    /// <summary>Stand-ins where no scene plays (agenda on), none elsewhere; a monitor with neither is hidden. Caller holds the gate.</summary>
    private async Task ReconcileStandInsAsync()
    {
        var (start, stop) = AgendaStandIns.Plan(_agenda is { Enabled: true }, _monitors.Count,
            _sceneMonitors.Keys.ToHashSet(), _standIns.Keys.ToHashSet());
        foreach (var monitor in stop)
        {
            _standIns.TryRemove(monitor, out _);
            if (_outputs.TryGetValue(monitor, out var output)) output.Scene.Stop();
        }
        foreach (var monitor in start)
        {
            try
            {
                var (scene, _) = await GetOrCreateOutputAsync(monitor);
                var background = new BackgroundLayerConfig { Mode = BackgroundMode.StretchedImage, UseLocalDesktopWallpaper = true };
                await Task.Run(() => scene.Load(LoadCommand(monitor, background, Array.Empty<SceneLayer>(), null)));
                scene.Start(new PlayerCommandStartAnimation());
                _standIns[monitor] = true;
                ApplyAgenda(monitor);
                _logger.LogInformation("[Agenda] Stand-in started on monitor {Monitor}", monitor);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Agenda] Stand-in on monitor {Monitor} failed", monitor);
            }
        }

        // Show what plays, hide what does not (the user's normal wallpaper shows again)
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var (monitor, (scene, window)) in _outputs)
            {
                if (scene.IsPlaying) window.ShowWallpaper();
                else window.Hide();
            }
        });
    }

    // ════════════════════════════════════════════════════════════════════
    // Demo mode: local test animation, no server — for checking window placement
    // ════════════════════════════════════════════════════════════════════

    private async Task StartDemoAsync()
    {
        var demoPath = Path.Combine(Path.GetTempPath(), "wabibabusy-demo.png");
        using (var surface = SKSurface.Create(new SKImageInfo(240, 240)))
        {
            var c = surface.Canvas;
            c.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0x8C, 0x00) };
            c.DrawCircle(120, 120, 110, paint);
            paint.Color = SKColors.White;
            c.DrawCircle(85, 95, 18, paint);
            c.DrawCircle(155, 95, 18, paint);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(demoPath, data.ToArray());
        }

        // One animation sweeping across all monitors left to right (Sequential mode)
        int minX = _monitors.Min(m => m.X);
        int canvasWidth = _monitors.Max(m => m.X + m.Width) - minX;
        int canvasHeight = _monitors.Max(m => m.Height);
        long ts = (long)WallpaperScene.NowUnixMs() + 500;
        var layer = new SceneLayer
        {
            Animation = new AnimationLayerConfig
            {
                AnimationPath = demoPath,
                FitMode = ContentFitMode.Center,
                ColorGrading = new ColorGradingConfig { Mode = ColorGradingMode.Rainbow }
            },
            Movement = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 400 },
            Distribution = AnimationDistributionMode.Sequential,
        };

        for (int i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            var layout = new NodeLayout
            {
                Id = $"demo-{i}", Order = i, OffsetX = m.X - minX, OffsetY = 0, Width = m.Width, Height = m.Height,
                CanvasWidth = canvasWidth, CanvasHeight = canvasHeight, Scale = 1f,
            };
            var background = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = "#1A3A5C" };
            await StartSceneAsync(i, LoadCommand(i, background, new[] { layer }, layout),
                new PlayerCommandStartAnimation { StartTimestampMs = ts, PixelsPerSecond = 400 }, transition: "");
        }
        _logger.LogInformation("[Demo] Running on {Count} monitor(s) — the wallpaper should sit behind all windows", _monitors.Count);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _bombCts?.Cancel();
        if (_service != null)
        {
            try { await _service.DisconnectFromServerAsync(); } catch { /* shutting down */ }
            _service.Dispose();
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var (scene, window) in _outputs.Values)
            {
                window.Close();
                scene.Dispose();
            }
        });
        _x11.Dispose();
    }
}

/// <summary>BiBaBo keypad keys from the keyboard: number row and numpad digits, Backspace / Delete / C = clear (10).</summary>
public static class BombKeys
{
    public static int FromKey(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        Key.Back or Key.Delete or Key.C => 10,
        _ => -1,
    };
}

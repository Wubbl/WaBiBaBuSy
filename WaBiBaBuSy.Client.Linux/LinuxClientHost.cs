using System.Collections.Concurrent;
using System.Text.Json;
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
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;

namespace WaBiBaBuSy.Client.Linux;

/// <summary>
/// Headless Linux client: discovers/connects to the WaBiBaBuSy server and renders whatever it
/// sends on this machine's monitors. Plays the role of the Windows MainWindowViewModel's
/// client path + D2DCompositionService, minus the UI.
/// </summary>
public sealed class LinuxClientHost : IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly ILogger<LinuxClientHost> _logger = AppLogger.CreateLogger<LinuxClientHost>();
    private readonly X11Desktop _x11;
    private readonly IReadOnlyList<MonitorGeometry> _monitors;
    private readonly ConcurrentDictionary<int, (WallpaperScene Scene, WallpaperWindow Window)> _outputs = new();
    private readonly CancellationTokenSource _cts = new();
    private WaBiBaBuSyService? _service;

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
        var clientConfig = ConfigurationManager.LoadClientConfiguration();
        _service = new WaBiBaBuSyService(AppLogger.CreateLogger<WaBiBaBuSyService>(), serverConfig, clientConfig);
        _service.ClientConnectionStatusChanged += (_, e) =>
            _logger.LogInformation("Connection status: {Connected}", e.IsConnected ? "connected" : "disconnected");

        // Initial connect; after that WallpaperSyncClient reconnects on its own (1..30 s backoff)
        while (!ct.IsCancellationRequested)
        {
            bool connected;
            if (_options.ServerAddress != null)
            {
                connected = await _service.ConnectToServerAsync(_options.ServerAddress, _options.ServerPort);
                if (connected) _service.SetD2DApplyDelegate(ApplySimpleAsync);
            }
            else
            {
                var fallback = string.IsNullOrEmpty(clientConfig.ServerAddress) ? "localhost" : clientConfig.ServerAddress;
                connected = await _service.DiscoverAndConnectAsync(fallback, clientConfig.ServerPort, ApplySimpleAsync);
            }

            if (connected)
            {
                _service.SetD2DCrossScreenApplyDelegate(ApplyCrossScreenAsync);
                _service.SetD2DCrossScreenStopDelegate(StopAllAsync);
                _logger.LogInformation("Connected — waiting for the server to start an animation");
                return;
            }

            _logger.LogWarning("No server found, retrying in 5 s (use --server HOST[:PORT] to skip discovery)");
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Every 0.5 s keep the wallpaper above the shell's own desktop window; every 2 s pause drawing
    /// while a fullscreen app has focus (mirrors the Windows FullscreenDetectionService).
    /// </summary>
    private async Task DesktopWatchLoopAsync(CancellationToken ct)
    {
        bool paused = false;
        bool loggedRaise = false;
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
                bool fullscreen = _x11.IsActiveWindowFullscreen();
                if (fullscreen != paused)
                {
                    paused = fullscreen;
                    _logger.LogInformation(paused ? "Fullscreen app detected — pausing wallpaper rendering" : "Fullscreen app gone — resuming");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var (_, window) in _outputs.Values) window.Paused = paused;
                    });
                }
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
    // Server commands (mirror MainWindowViewModel.ApplyCrossScreenD2DFromRemoteAsync etc.)
    // ════════════════════════════════════════════════════════════════════

    private async Task ApplyCrossScreenAsync(CrossScreenApplyRequest req)
    {
        int monitorIndex = req.MonitorIndex;
        _logger.LogInformation("[CrossScreen] file={File} monitor={Monitor} canvas={VCW}px offset={Off}px ts={Ts} perMonitor={PerMonitor}",
            Path.GetFileName(req.FilePath), monitorIndex, req.VirtualCanvasWidth, req.MonitorOffsetX,
            req.SharedStartTimestampMs, req.PerMonitorMode);

        if (monitorIndex < 0 || monitorIndex >= _monitors.Count)
        {
            _logger.LogError("[CrossScreen] Invalid monitor index {Index} (have {Count})", monitorIndex, _monitors.Count);
            return;
        }
        var monitor = _monitors[monitorIndex];

        // Background: full config when transmitted, solid-colour fallback otherwise
        var backgroundConfig = Deserialize<BackgroundLayerConfig>(req.BackgroundJson)
            ?? new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = req.BackgroundColor };
        if ((backgroundConfig.Mode == BackgroundMode.StretchedImage || backgroundConfig.Mode == BackgroundMode.TiledImage)
            && (string.IsNullOrEmpty(backgroundConfig.ImagePath) || !File.Exists(backgroundConfig.ImagePath)))
        {
            // Background images are not transferred to clients; the server-local path won't exist here
            backgroundConfig = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = req.BackgroundColor };
        }

        var animationConfig = Deserialize<AnimationLayerConfig>(req.AnimationJson);
        if (animationConfig != null)
        {
            // The file was downloaded into this client's cache — always override the server-local path
            animationConfig.AnimationPath = req.FilePath;
            animationConfig.AdditionalAnimationPaths = animationConfig.AdditionalAnimationPaths.Where(File.Exists).ToList();
        }
        else
        {
            animationConfig = new AnimationLayerConfig
            {
                AnimationPath = req.FilePath,
                TargetHeight = monitor.Height,
                Loop = true,
                VerticalAlign = VerticalAlignment.Center,
                CenterInitialPosition = true,
                FitMode = (ContentFitMode)req.FitMode,
                Pattern = Deserialize<PatternConfig>(req.PatternJson),
                ColorGrading = Deserialize<ColorGradingConfig>(req.ColorGradingJson) ?? new()
            };
        }

        var movementConfig = Deserialize<MovementConfig>(req.MovementJson)
            ?? (req.MovementType == 0
                ? null
                : new MovementConfig { Type = (MovementType)req.MovementType, SpeedPixelsPerSecond = req.PixelsPerSecond });

        bool maskZones = backgroundConfig.Mode == BackgroundMode.IconZone && animationConfig.Pattern != null;
        var loadCmd = new PlayerCommandLoadAnimation
        {
            AnimationConfig = animationConfig,
            BackgroundConfig = backgroundConfig,
            MonitorIndex = monitorIndex,
            VirtualCanvasHeight = monitor.Height,
            // Simultaneous: this monitor is the whole canvas. Sequential: server-provided spanning canvas.
            VirtualCanvasWidth = req.PerMonitorMode ? monitor.Width : req.VirtualCanvasWidth,
            MonitorOffsetX = req.PerMonitorMode ? 0 : req.MonitorOffsetX,
            MovementConfig = movementConfig,
            MaskZones = maskZones,
            UseZonePalette = backgroundConfig.IconZonePaletteEnabled,
            IconFadePaddingPx = maskZones ? backgroundConfig.IconFadePaddingPx : 0,
            IconZoneExpansionPx = maskZones ? backgroundConfig.IconZoneExpansionPx : 0
        };

        int pixelsPerSecond = movementConfig == null || movementConfig.Type == MovementType.Static
            ? 0
            : (int)movementConfig.SpeedPixelsPerSecond;

        await LoadAndStartAsync(monitorIndex, loadCmd,
            new PlayerCommandStartAnimation { StartTimestampMs = req.SharedStartTimestampMs, PixelsPerSecond = pixelsPerSecond });
    }

    /// <summary>Plain "d2d" LOAD: one centered, static wallpaper on a solid background.</summary>
    private async Task ApplySimpleAsync(string filePath, int monitorIndex, string backgroundColor, int fitMode)
    {
        if (monitorIndex < 0 || monitorIndex >= _monitors.Count) return;
        var monitor = _monitors[monitorIndex];
        var loadCmd = new PlayerCommandLoadAnimation
        {
            AnimationConfig = new AnimationLayerConfig
            {
                AnimationPath = filePath,
                TargetHeight = monitor.Height,
                Loop = true,
                VerticalAlign = VerticalAlignment.Center,
                CenterInitialPosition = true,
                FitMode = (ContentFitMode)fitMode
            },
            BackgroundConfig = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = backgroundColor },
            MonitorIndex = monitorIndex,
            VirtualCanvasHeight = monitor.Height,
            VirtualCanvasWidth = monitor.Width,
            MonitorOffsetX = 0
        };
        await LoadAndStartAsync(monitorIndex, loadCmd, new PlayerCommandStartAnimation { StartTimestampMs = 0, PixelsPerSecond = 0 });
    }

    private async Task StopAllAsync()
    {
        _logger.LogInformation("[Stop] Stopping {Count} output(s)", _outputs.Count);
        foreach (var (scene, _) in _outputs.Values) scene.Stop();
        // Hide the windows so the user's normal wallpaper shows again (like the Windows client disposing its players)
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var (_, window) in _outputs.Values) window.Hide();
        });
    }

    private async Task LoadAndStartAsync(int monitorIndex, PlayerCommandLoadAnimation load, PlayerCommandStartAnimation start)
    {
        var (scene, window) = await GetOrCreateOutputAsync(monitorIndex);

        // Decoding can take a while for big GIFs — keep it off the UI thread
        await Task.Run(() => scene.Load(load));

        scene.Start(start);

        await Dispatcher.UIThread.InvokeAsync(window.ShowWallpaper);
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
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var movement = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 400 };

        for (int i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            var load = new PlayerCommandLoadAnimation
            {
                AnimationConfig = new AnimationLayerConfig
                {
                    AnimationPath = demoPath,
                    FitMode = ContentFitMode.Center,
                    ColorGrading = new ColorGradingConfig { Mode = ColorGradingMode.Rainbow }
                },
                BackgroundConfig = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = "#1A3A5C" },
                MonitorIndex = i,
                VirtualCanvasHeight = m.Height,
                VirtualCanvasWidth = canvasWidth,
                MonitorOffsetX = m.X - minX,
                MovementConfig = movement
            };
            await LoadAndStartAsync(i, load, new PlayerCommandStartAnimation { StartTimestampMs = ts, PixelsPerSecond = 400 });
        }
        _logger.LogInformation("[Demo] Running on {Count} monitor(s) — the wallpaper should sit behind all windows", _monitors.Count);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
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

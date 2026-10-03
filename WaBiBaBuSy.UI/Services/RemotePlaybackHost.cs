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

    public RemotePlaybackHost(WaBiBaBuSyService service, DesktopWindowManager desktopManager)
    {
        _service = service;
        _desktopManager = desktopManager;
    }

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
    }

    /// <summary>Hide or show every remote-driven player window (pause on fullscreen apps).</summary>
    public void SetPlayersVisible(bool visible)
    {
        foreach (var player in _players.Values)
            player.SetPlayersVisible(visible);
    }

    /// <summary>
    /// Cross-screen D2D apply delegate. Called when the server sends a CROSSSCREEN D2D command.
    /// Creates a D2D player on this client with the server-provided virtual canvas parameters
    /// so all nodes share the same coordinate space and start timestamp.
    /// </summary>
    public async Task ApplyCrossScreenAsync(CrossScreenApplyRequest req)
    {
        var monitorIndex = req.MonitorIndex;
        Debug.WriteLine($"[D2D-CrossScreen] Received: file={req.FilePath}, monitor={monitorIndex}, canvas={req.VirtualCanvasWidth}px, offset={req.MonitorOffsetX}px, ts={req.SharedStartTimestampMs}ms, speed={req.PixelsPerSecond}px/s, perMonitor={req.PerMonitorMode}, movType={req.MovementType}");

        // Dispose previous cross-screen service for this monitor
        if (_players.TryRemove(monitorIndex, out var existing))
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

        // ── Animation: full config when transmitted (TargetHeight/SpeedMultiplier/
        //    VerticalAlign/RotateWithPath/PrecomputedPath/pattern/colors all matter
        //    for cross-machine determinism), legacy reconstruction otherwise ──
        var animationConfig = DeserializeRemoteConfig<AnimationLayerConfig>(req.AnimationJson, "AnimationLayerConfig");
        if (animationConfig != null)
        {
            // Tier 1.3: every server-side path (primary, additional images, background image) is
            // rewritten to this client's cache. Anything that could not be fetched is dropped and a
            // missing background image degrades to solid color.
            var (localAnim, localBg) = SceneAssets.RewriteToLocal(animationConfig, backgroundConfig, req.LocalAssetPaths, req.FilePath);
            animationConfig = localAnim;
            backgroundConfig = localBg;
            Debug.WriteLine($"[D2D-CrossScreen] Assets: {req.LocalAssetPaths.Count} mapped, {animationConfig.AdditionalAnimationPaths.Count} additional image(s), bg={backgroundConfig.Mode}");
        }
        else
        {
            PatternConfig? pattern = DeserializeRemoteConfig<PatternConfig>(req.PatternJson, "PatternConfig");
            ColorGradingConfig colorGrading = DeserializeRemoteConfig<ColorGradingConfig>(req.ColorGradingJson, "ColorGradingConfig") ?? new();
            backgroundConfig = SceneAssets.RewriteToLocal(new AnimationLayerConfig(), backgroundConfig, req.LocalAssetPaths, null).Background;
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

        // ── Node layout (seat map: mirroring, ring wrap); null for legacy servers ──
        var remoteNodeLayout = DeserializeRemoteConfig<NodeLayout>(req.LayoutJson, "NodeLayout");

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
            explicitMonitorOffsetX: req.PerMonitorMode ? null : req.MonitorOffsetX,
            // 0 = older server that did not send a canvas height → fall back to this monitor's.
            explicitVirtualCanvasHeight: req.PerMonitorMode || req.VirtualCanvasHeight <= 0 ? null : req.VirtualCanvasHeight,
            explicitMonitorOffsetY: req.PerMonitorMode ? null : req.MonitorOffsetY,
            nodeOrder: req.NodeOrder,
            layout: req.PerMonitorMode ? null : remoteNodeLayout);

        await Task.Delay(100);

        // Test mode (timecode / simulated skew) must reach a new player before its start command:
        // the skew shifts the player's clock, and the start is converted with the skewed offset.
        var testMode = _service.Client?.TestModeState ?? default;
        if (testMode.Timecode || testMode.ClockSkewMs != 0)
            await d2dService.SetTestModeAsync(testMode.Timecode, testMode.ClockSkewMs);

        // Same speed convention as the local start path: Static → 0.
        var pixelsPerSecond = movementConfig == null || movementConfig.Type == MovementType.Static
            ? 0
            : (int)movementConfig.SpeedPixelsPerSecond;

        Debug.WriteLine($"[D2D-CrossScreen] Starting playback at ts={req.SharedStartTimestampMs}ms, speed={pixelsPerSecond}px/s");
        await d2dService.StartAsync(startTimestampMs: req.SharedStartTimestampMs, pixelsPerSecond: pixelsPerSecond);

        _players[monitorIndex] = d2dService;
        await CatchUpTestModeAsync(d2dService, testMode);
        AdoptRemoteThumbnail(monitorIndex, d2dService, req.FilePath);

        Debug.WriteLine($"[D2D-CrossScreen] SUCCESS: Cross-screen D2D started on monitor {monitorIndex}");
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

    /// <summary>
    /// Stop delegate invoked when the server sends a Stop command for cross-screen D2D.
    /// Tears down every player started by ApplyCrossScreenAsync / ApplyPlainAsync.
    /// </summary>
    public async Task StopAllAsync()
    {
        Debug.WriteLine($"[D2D-CrossScreen] Stopping {_players.Count} remote D2D service(s)");
        foreach (var kvp in _players)
        {
            try { await kvp.Value.StopAsync(); } catch { }
            try { kvp.Value.Dispose(); } catch { }
        }
        _players.Clear();
        ThumbnailCapture.ClearWallpaperHwnd();
        Debug.WriteLine("[D2D-CrossScreen] All remote D2D services stopped");
    }

    /// <summary>
    /// D2D apply delegate called when the server sends a D2D LOAD command to this client.
    /// Creates a local D2DCompositionService and renders on this machine's desktop.
    /// </summary>
    public async Task ApplyPlainAsync(string filePath, int monitorIndex, string backgroundColor, int fitMode)
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

        // Same test-mode handling as the cross-screen path: state before the start, catch-up after registering.
        var testMode = _service.Client?.TestModeState ?? default;
        if (testMode.Timecode || testMode.ClockSkewMs != 0)
            await d2dService.SetTestModeAsync(testMode.Timecode, testMode.ClockSkewMs);

        Debug.WriteLine($"[D2D-Remote] Starting D2D playback");
        await d2dService.StartAsync(startTimestampMs: 0, pixelsPerSecond: 0);

        _players[monitorIndex] = d2dService;
        await CatchUpTestModeAsync(d2dService, testMode);
        AdoptRemoteThumbnail(monitorIndex, d2dService, filePath);
        Debug.WriteLine($"[D2D-Remote] SUCCESS: Applied D2D wallpaper '{Path.GetFileName(filePath)}' on monitor {monitorIndex}");
    }
}

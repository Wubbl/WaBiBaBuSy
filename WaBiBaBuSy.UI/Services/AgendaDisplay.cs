using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.UI.Services;

/// <summary>
/// This machine's side of the room agenda (design 2026-10-21-agenda-overlay-design.md §4): keeps the latest agenda,
/// sends it to every scene player, and runs a stand-in player (the seat's own desktop wallpaper, no layers) on each
/// monitor that has no scene while the agenda is on. Stand-ins are not scene players: no thumbnails, no "playing" state.
/// One per playback host; calls are serialized internally.
/// </summary>
public sealed class AgendaDisplay
{
    private readonly DesktopWindowManager _desktopManager;
    private readonly Func<int, IReadOnlyList<string>> _nodeIdsOfMonitor;
    private readonly Dictionary<int, D2DCompositionService> _standIns = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _visible = true;

    /// <param name="nodeIdsOfMonitor">The room ids a monitor may be known by (agenda screens are tagged by node id).</param>
    public AgendaDisplay(DesktopWindowManager desktopManager, Func<int, IReadOnlyList<string>> nodeIdsOfMonitor)
    {
        _desktopManager = desktopManager;
        _nodeIdsOfMonitor = nodeIdsOfMonitor;
    }

    /// <summary>The agenda last set (null = none).</summary>
    public Agenda? Current { get; private set; }

    private bool IsOn => Current is { Enabled: true };

    /// <summary>True while at least one stand-in runs (fullscreen detection must run then too).</summary>
    public bool HasStandIns { get { lock (_standIns) return _standIns.Count > 0; } }

    /// <summary>Raised after stand-ins started or stopped (any thread).</summary>
    public event Action? StandInsChanged;

    /// <summary>Hide / show the stand-ins (a fullscreen app is in front), like the scene players.</summary>
    public void SetVisible(bool visible)
    {
        _visible = visible;
        foreach (var (_, player) in Snapshot()) player.SetPlayersVisible(visible);
    }

    /// <summary>
    /// Hand over the stand-ins of <paramref name="monitors"/> (a scene is starting there): the caller retires them at the
    /// shared start, so the panel stays until the new players appear and no stand-in teardown sits on the start path.
    /// </summary>
    public IReadOnlyList<D2DCompositionService> TakeStandIns(IEnumerable<int> monitors)
    {
        var taken = new List<D2DCompositionService>();
        lock (_standIns)
            foreach (var m in monitors)
                if (_standIns.Remove(m, out var player)) taken.Add(player);
        if (taken.Count > 0) StandInsChanged?.Invoke();
        return taken;
    }

    /// <summary>A new agenda (or null): sent to the scene players and the stand-ins, then the stand-ins are reconciled.</summary>
    /// <param name="reconcile">False while a scene is starting: its players are not registered yet; the start reconciles afterwards.</param>
    public async Task SetAsync(Agenda? agenda, IReadOnlyDictionary<int, D2DCompositionService> scenePlayers, bool reconcile = true)
    {
        await _gate.WaitAsync();
        try
        {
            Current = agenda;
            foreach (var (monitor, player) in scenePlayers) await SendCoreAsync(monitor, player);
            foreach (var (monitor, player) in Snapshot()) await SendCoreAsync(monitor, player);
            if (reconcile) await ReconcileCoreAsync(scenePlayers);
        }
        finally { _gate.Release(); }
    }

    /// <summary>A scene player just started on <paramref name="monitorIndex"/>: give it the agenda.</summary>
    public async Task SendToAsync(int monitorIndex, D2DCompositionService player)
    {
        await _gate.WaitAsync();
        try { await SendCoreAsync(monitorIndex, player); }
        finally { _gate.Release(); }
    }

    /// <summary>After any scene player start / stop: stand-ins where no scene plays (agenda on), none elsewhere.</summary>
    public async Task ReconcileAsync(IReadOnlyDictionary<int, D2DCompositionService> scenePlayers)
    {
        await _gate.WaitAsync();
        try { await ReconcileCoreAsync(scenePlayers); }
        finally { _gate.Release(); }
    }

    /// <summary>Stop every stand-in (app exit).</summary>
    public async Task StopAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var (monitor, _) in Snapshot()) await StopStandInAsync(monitor);
            StandInsChanged?.Invoke();
        }
        finally { _gate.Release(); }
    }

    private List<KeyValuePair<int, D2DCompositionService>> Snapshot() { lock (_standIns) return _standIns.ToList(); }

    /// <summary>Always sends, null too: a null agenda (explicit disconnect) must clear the panel on the scene players.</summary>
    private async Task SendCoreAsync(int monitorIndex, D2DCompositionService player)
    {
        try { await player.SendAgendaAsync(Current, Current?.IsScreen(_nodeIdsOfMonitor(monitorIndex)) ?? false); }
        catch (Exception ex) { Debug.WriteLine($"[Agenda] Send to monitor {monitorIndex} failed: {ex.Message}"); }
    }

    private async Task ReconcileCoreAsync(IReadOnlyDictionary<int, D2DCompositionService> scenePlayers)
    {
        var monitors = NativeMonitorInfo.GetAllMonitors();
        var withScene = scenePlayers.Where(kv => kv.Value.IsRunning).Select(kv => kv.Key).ToHashSet();
        var (start, stop) = AgendaStandIns.Plan(IsOn, monitors.Length, withScene, Snapshot().Select(kv => kv.Key).ToHashSet());
        foreach (var monitor in stop) await StopStandInAsync(monitor);
        foreach (var monitor in start)
        {
            try { await StartStandInAsync(monitor, monitors[monitor]); }
            catch (Exception ex) { Debug.WriteLine($"[Agenda] Stand-in on monitor {monitor} failed: {ex.Message}"); }
        }
        if (start.Count > 0 || stop.Count > 0) StandInsChanged?.Invoke();
    }

    private async Task StartStandInAsync(int monitorIndex, NativeMonitorInfo screen)
    {
        var bounds = new System.Drawing.Rectangle(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
        var canvasManager = new VirtualCanvasManager(AppLogger.CreateLogger<VirtualCanvasManager>());
        canvasManager.CalculateLayout(new[]
        {
            new ScreenConfiguration { ClientId = $"AGENDA_MONITOR_{monitorIndex}", Order = monitorIndex, Width = bounds.Width, Height = bounds.Height },
        });
        var background = new BackgroundLayerConfig
        {
            Mode = BackgroundMode.StretchedImage,
            ImagePath = IdleImage.EnsurePath(),
            UseLocalDesktopWallpaper = true,
        };
        var player = new D2DCompositionService(AppLogger.CreateLogger<D2DCompositionService>(), AppLogger.Factory, _desktopManager);
        try
        {
            await player.InitializeAsync(canvasManager, background, Array.Empty<SceneLayer>(), bounds, monitorIndex);
            await Task.Delay(100);
            await player.StartAsync(startTimestampMs: 0, pixelsPerSecond: 0);
        }
        catch
        {
            try { player.Dispose(); } catch { /* already broken */ }
            throw;
        }
        lock (_standIns) _standIns[monitorIndex] = player;
        if (!_visible) player.SetPlayersVisible(false);
        await SendCoreAsync(monitorIndex, player);
        Debug.WriteLine($"[Agenda] Stand-in started on monitor {monitorIndex}");
    }

    private async Task StopStandInAsync(int monitorIndex)
    {
        D2DCompositionService? player;
        lock (_standIns)
            if (!_standIns.Remove(monitorIndex, out player)) return;
        try { await player.StopAsync(); } catch { /* best effort */ }
        try { player.Dispose(); } catch { /* best effort */ }
        Debug.WriteLine($"[Agenda] Stand-in stopped on monitor {monitorIndex}");
    }
}

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

    /// <param name="nodeIdsOfMonitor">The room ids a monitor may be known by (agenda screens are tagged by node id).</param>
    public AgendaDisplay(DesktopWindowManager desktopManager, Func<int, IReadOnlyList<string>> nodeIdsOfMonitor)
    {
        _desktopManager = desktopManager;
        _nodeIdsOfMonitor = nodeIdsOfMonitor;
    }

    /// <summary>The agenda last set (null = none).</summary>
    public Agenda? Current { get; private set; }

    private bool IsOn => Current is { Enabled: true };

    /// <summary>A new agenda (or null): sent to the scene players and the stand-ins, then the stand-ins are reconciled.</summary>
    public async Task SetAsync(Agenda? agenda, IReadOnlyDictionary<int, D2DCompositionService> scenePlayers)
    {
        await _gate.WaitAsync();
        try
        {
            Current = agenda;
            foreach (var (monitor, player) in scenePlayers) await SendCoreAsync(monitor, player);
            foreach (var (monitor, player) in _standIns) await SendCoreAsync(monitor, player);
            await ReconcileCoreAsync(scenePlayers);
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
            foreach (var monitor in _standIns.Keys.ToList()) await StopStandInAsync(monitor);
        }
        finally { _gate.Release(); }
    }

    private async Task SendCoreAsync(int monitorIndex, D2DCompositionService player)
    {
        if (Current == null) return;   // nothing was ever set: a fresh player has nothing to hide
        try { await player.SendAgendaAsync(Current, Current.IsScreen(_nodeIdsOfMonitor(monitorIndex))); }
        catch (Exception ex) { Debug.WriteLine($"[Agenda] Send to monitor {monitorIndex} failed: {ex.Message}"); }
    }

    private async Task ReconcileCoreAsync(IReadOnlyDictionary<int, D2DCompositionService> scenePlayers)
    {
        var monitors = NativeMonitorInfo.GetAllMonitors();
        var withScene = scenePlayers.Where(kv => kv.Value.IsRunning).Select(kv => kv.Key).ToHashSet();
        var (start, stop) = AgendaStandIns.Plan(IsOn, monitors.Length, withScene, _standIns.Keys.ToHashSet());
        foreach (var monitor in stop) await StopStandInAsync(monitor);
        foreach (var monitor in start)
        {
            try { await StartStandInAsync(monitor, monitors[monitor]); }
            catch (Exception ex) { Debug.WriteLine($"[Agenda] Stand-in on monitor {monitor} failed: {ex.Message}"); }
        }
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
        _standIns[monitorIndex] = player;
        await SendCoreAsync(monitorIndex, player);
        Debug.WriteLine($"[Agenda] Stand-in started on monitor {monitorIndex}");
    }

    private async Task StopStandInAsync(int monitorIndex)
    {
        if (!_standIns.Remove(monitorIndex, out var player)) return;
        try { await player.StopAsync(); } catch { /* best effort */ }
        try { player.Dispose(); } catch { /* best effort */ }
        Debug.WriteLine($"[Agenda] Stand-in stopped on monitor {monitorIndex}");
    }
}

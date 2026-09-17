using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>Metrics an apply callback returns so the orchestrator can compute lap-snap timing.</summary>
public readonly record struct ApplyMetrics(int VirtualCanvasWidth, int ContentWidthPx, int StartLeadMs = 0);

/// <summary>
/// Drives playlist rotation: applies each item's config via a caller-supplied delegate (the same
/// broadcast path the manual Start button uses), waits the resolved dwell, then advances.
/// Server-authority-only. Timing decisions come from PlaylistScheduler (pure, tested).
/// </summary>
public class PlaylistOrchestrator
{
    private readonly ILogger<PlaylistOrchestrator> _logger;
    private readonly Func<CrossScreenConfig, Task<ApplyMetrics>> _apply;
    private readonly Func<int> _seedProvider;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>Delay before advancing after an apply failure, to avoid busy-looping on persistent errors.</summary>
    private const int FailureRetryDelayMs = 2000;

    /// <summary>The item currently on screen (for session-resume + UI), or null when stopped.</summary>
    public PlaylistItem? CurrentItem { get; private set; }

    /// <summary>Index of the current item within the playlist's Items list, or -1.</summary>
    public int CurrentItemIndex { get; private set; } = -1;

    /// <summary>True while a show is running.</summary>
    public bool IsRunning => _loopTask is { IsCompleted: false };

    /// <summary>Raised (item, index) whenever a new item becomes current — for UI highlight.</summary>
    public event Action<PlaylistItem, int>? ItemChanged;

    /// <param name="apply">Applies a config across all nodes and returns canvas/content metrics.</param>
    /// <param name="seedProvider">Supplies a fresh shuffle seed per cycle (e.g. () => Environment.TickCount).</param>
    public PlaylistOrchestrator(
        ILogger<PlaylistOrchestrator> logger,
        Func<CrossScreenConfig, Task<ApplyMetrics>> apply,
        Func<int> seedProvider)
    {
        _logger = logger;
        _apply = apply;
        _seedProvider = seedProvider;
    }

    /// <summary>Start rotating the given playlist. No-op if already running or the list is empty.</summary>
    public void Start(Playlist playlist)
    {
        if (IsRunning) { _logger.LogWarning("Playlist already running; ignoring Start"); return; }
        if (playlist.Items.Count == 0) { _logger.LogWarning("Playlist has no items; not starting"); return; }

        _cts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(playlist, _cts.Token);
    }

    /// <summary>Stop the show. Safe to call when already stopped.</summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loopTask != null)
        {
            try { await _loopTask; } catch (OperationCanceledException) { }
        }
        _loopTask = null;
        _cts?.Dispose();
        _cts = null;
        CurrentItem = null;
        CurrentItemIndex = -1;
    }

    private async Task RunLoopAsync(Playlist playlist, CancellationToken ct)
    {
        try
        {
            do
            {
                var order = PlaylistScheduler.BuildCycleOrder(
                    playlist.Items.Count, playlist.Shuffle, _seedProvider());

                foreach (var index in order)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = playlist.Items[index];

                    CurrentItem = item;
                    CurrentItemIndex = index;
                    ItemChanged?.Invoke(item, index);

                    ApplyMetrics metrics;
                    try
                    {
                        metrics = await _apply(item.Config);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Apply failed for item '{Name}'; pausing {Delay}ms before next item", item.Name, FailureRetryDelayMs);
                        await Task.Delay(FailureRetryDelayMs, ct);
                        continue;
                    }

                    int lapMs = PlaylistScheduler.ComputeLapMs(
                        item.Config.Movement, metrics.VirtualCanvasWidth, metrics.ContentWidthPx);
                    int dwell = PlaylistScheduler.ResolveDwellMs(item, playlist.DefaultItemDurationMs, lapMs);

                    // The apply path schedules the shared start StartLeadMs in the future so all
                    // nodes begin together; the dwell is measured from that start, not from now.
                    int lead = Math.Max(0, metrics.StartLeadMs);
                    _logger.LogInformation(
                        "Playlist item '{Name}' (idx {Index}) dwell={Dwell}ms (lap={Lap}ms, snap={Snap}, startLead={Lead}ms)",
                        item.Name, index, dwell, lapMs, item.SnapToLap, lead);

                    await Task.Delay(dwell + lead, ct);
                }
            }
            while (playlist.Loop && !ct.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Playlist rotation cancelled");
        }
    }
}

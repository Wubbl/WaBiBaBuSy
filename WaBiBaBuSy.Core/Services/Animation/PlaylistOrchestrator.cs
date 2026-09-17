using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Wallpaper;

using System.Collections.Generic;

using System.Linq;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>Metrics an apply callback returns so the orchestrator can compute lap-snap timing.</summary>
public readonly record struct ApplyMetrics(int VirtualCanvasWidth, int ContentWidthPx, int StartLeadMs = 0, float SpeedPxPerSecond = 0f);

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
    private readonly Func<IReadOnlyList<CrossScreenConfig>, Task>? _prefetch;
    private readonly Func<TimeSpan, Task<bool>>? _waitUntilNodesReady;

    /// <summary>How long Start waits for every node to report its prefetch complete before going anyway.</summary>
    public TimeSpan PrefetchTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>UTC ms of the next scheduled item switch, 0 while idle. For the UI countdown.</summary>
    public long NextSwitchUtcMs { get; private set; }

    /// <summary>The item that will follow the current one, or null at the end of a non-looping list.</summary>
    public PlaylistItem? NextItem { get; private set; }

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
    /// <param name="prefetch">Optional: ask every node to cache the assets of all given configs (Tier 1.3).</param>
    /// <param name="waitUntilNodesReady">Optional: wait (bounded) until every node reports its prefetch complete.</param>
    public PlaylistOrchestrator(
        ILogger<PlaylistOrchestrator> logger,
        Func<CrossScreenConfig, Task<ApplyMetrics>> apply,
        Func<int> seedProvider,
        Func<IReadOnlyList<CrossScreenConfig>, Task>? prefetch = null,
        Func<TimeSpan, Task<bool>>? waitUntilNodesReady = null)
    {
        _logger = logger;
        _apply = apply;
        _seedProvider = seedProvider;
        _prefetch = prefetch;
        _waitUntilNodesReady = waitUntilNodesReady;
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
        NextItem = null;
        NextSwitchUtcMs = 0;
    }

    private async Task RunLoopAsync(Playlist playlist, CancellationToken ct)
    {
        try
        {
            // Tier 1.3: cache the whole show on every node before the first item, so no switch
            // ever waits for a download. Bounded wait — a straggler back-dates as before.
            if (_prefetch != null)
            {
                try
                {
                    await _prefetch(playlist.Items.Select(i => i.Config).ToList());
                    if (_waitUntilNodesReady != null)
                    {
                        bool ready = await _waitUntilNodesReady(PrefetchTimeout);
                        _logger.LogInformation(ready
                            ? "Prefetch: all nodes ready"
                            : "Prefetch: timed out after {Timeout}s — starting anyway", PrefetchTimeout.TotalSeconds);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger.LogWarning(ex, "Prefetch failed; starting without it"); }
            }

            do
            {
                var order = PlaylistScheduler.BuildCycleOrder(
                    playlist.Items.Count, playlist.Shuffle, _seedProvider());

                for (int k = 0; k < order.Length; k++)
                {
                    var index = order[k];
                    ct.ThrowIfCancellationRequested();
                    var item = playlist.Items[index];
                    NextItem = k + 1 < order.Length ? playlist.Items[order[k + 1]]
                             : playlist.Loop && order.Length > 0 ? playlist.Items[order[0]] : null;

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

                    // SpeedPxPerSecond is the canvas speed after cm→px resolution (physical canvas).
                    int lapMs = PlaylistScheduler.ComputeLapMs(
                        item.Config.Movement, metrics.VirtualCanvasWidth, metrics.ContentWidthPx, metrics.SpeedPxPerSecond);
                    int dwell = PlaylistScheduler.ResolveDwellMs(item, playlist.DefaultItemDurationMs, lapMs);

                    // The apply path schedules the shared start StartLeadMs in the future so all
                    // nodes begin together; the dwell is measured from that start, not from now.
                    int lead = Math.Max(0, metrics.StartLeadMs);
                    _logger.LogInformation(
                        "Playlist item '{Name}' (idx {Index}) dwell={Dwell}ms (lap={Lap}ms, snap={Snap}, startLead={Lead}ms)",
                        item.Name, index, dwell, lapMs, item.SnapToLap, lead);

                    NextSwitchUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + dwell + lead;
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

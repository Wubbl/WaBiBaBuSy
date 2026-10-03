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
/// Where a running show is: the playlist it runs, the item order of its current cycle (shuffled or not) and
/// the position of the current item in that order. <see cref="PlaylistOrchestrator.Resume"/> starts the show
/// again there (used to bring a show back after an automated test run).
/// </summary>
public sealed record PlaylistResumePoint(Playlist Playlist, IReadOnlyList<int> CycleOrder, int CyclePosition)
{
    /// <summary>Index of the current item in <see cref="Playlist.Items"/>.</summary>
    public int ItemIndex => CycleOrder[CyclePosition];
}

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

    /// <summary>Cancels only the current dwell wait (⏭). Null outside a dwell.</summary>
    private volatile CancellationTokenSource? _skipCts;

    /// <summary>Delay before advancing after an apply failure, to avoid busy-looping on persistent errors.</summary>
    private const int FailureRetryDelayMs = 2000;

    /// <summary>The item currently on screen (for session-resume + UI), or null when stopped.</summary>
    public PlaylistItem? CurrentItem { get; private set; }

    /// <summary>Index of the current item within the playlist's Items list, or -1.</summary>
    public int CurrentItemIndex { get; private set; } = -1;

    /// <summary>True while a show is running.</summary>
    public bool IsRunning => _loopTask is { IsCompleted: false };

    /// <summary>Published as one immutable value so a reader on another thread never sees a torn position.</summary>
    private volatile PlaylistResumePoint? _position;

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
    public void Start(Playlist playlist) => StartCore(playlist, firstOrder: null, firstPosition: 0, "Start");

    /// <summary>
    /// Where the running show is (playlist, cycle order, current position), or null when no show runs.
    /// Before the first item is on screen (prefetch) the position is the first item of the first cycle.
    /// </summary>
    public PlaylistResumePoint? CaptureResumePoint() => IsRunning ? _position : null;

    /// <summary>
    /// Start the show at a point captured by <see cref="CaptureResumePoint"/>: the item at that position starts
    /// from its beginning (full dwell), the rest of that cycle keeps the captured order (a shuffled show keeps
    /// its shuffle), later cycles are built as usual. A point whose order does not fit the playlist starts the
    /// show from the top. Runs the prefetch first, like <see cref="Start"/>. No-op if already running.
    /// </summary>
    public void Resume(PlaylistResumePoint point)
    {
        var playlist = point.Playlist;
        var order = point.CycleOrder.ToArray();
        bool fits = order.Length == playlist.Items.Count
                 && order.Order().SequenceEqual(Enumerable.Range(0, order.Length))
                 && point.CyclePosition >= 0 && point.CyclePosition < order.Length;
        if (!fits)
            _logger.LogWarning("Playlist resume point does not fit '{Name}'; starting from the top", playlist.Name);
        StartCore(playlist, fits ? order : null, fits ? point.CyclePosition : 0, "Resume");
    }

    private void StartCore(Playlist playlist, int[]? firstOrder, int firstPosition, string caller)
    {
        if (IsRunning) { _logger.LogWarning("Playlist already running; ignoring {Caller}", caller); return; }
        if (playlist.Items.Count == 0) { _logger.LogWarning("Playlist has no items; not starting"); return; }

        // Built before the loop starts so a capture during the prefetch already has a position.
        var order = firstOrder ?? PlaylistScheduler.BuildCycleOrder(playlist.Items.Count, playlist.Shuffle, _seedProvider());
        _position = new PlaylistResumePoint(playlist, order, firstPosition);
        _cts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(playlist, order, firstPosition, _cts.Token);
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
        _position = null;
    }

    /// <summary>Advance to the next item now (ends the current dwell). No-op when no show is running.</summary>
    public void Skip()
    {
        try { _skipCts?.Cancel(); }
        catch (ObjectDisposedException) { /* the dwell ended at the same moment */ }
    }

    /// <param name="order">Item order of the first cycle.</param>
    /// <param name="startPosition">Position in <paramref name="order"/> the first cycle starts at (0 = the top).</param>
    private async Task RunLoopAsync(Playlist playlist, int[] order, int startPosition, CancellationToken ct)
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

            while (true)
            {
                for (int k = startPosition; k < order.Length; k++)
                {
                    var index = order[k];
                    ct.ThrowIfCancellationRequested();
                    var item = playlist.Items[index];
                    // Across a cycle boundary the next cycle's order is not built yet: with shuffle on, its
                    // first item may differ from this guess (unchanged behaviour).
                    NextItem = k + 1 < order.Length ? playlist.Items[order[k + 1]]
                             : playlist.Loop && order.Length > 0 ? playlist.Items[order[0]] : null;

                    CurrentItem = item;
                    CurrentItemIndex = index;
                    _position = new PlaylistResumePoint(playlist, order, k);
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
                    using (var skip = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        _skipCts = skip;
                        try
                        {
                            await Task.Delay(dwell + lead, skip.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            _logger.LogInformation("Playlist item '{Name}' skipped", item.Name);
                        }
                        finally
                        {
                            _skipCts = null;
                        }
                    }
                }

                if (!playlist.Loop || ct.IsCancellationRequested) break;
                startPosition = 0;
                order = PlaylistScheduler.BuildCycleOrder(playlist.Items.Count, playlist.Shuffle, _seedProvider());
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Playlist rotation cancelled");
        }
    }
}

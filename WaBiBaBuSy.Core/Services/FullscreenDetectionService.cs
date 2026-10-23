using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.Core.Services;

/// <summary>
/// Polls which of this machine's monitors a fullscreen app covers (the probe, e.g. <c>MonitorBusyProbe.FullscreenMonitors</c>
/// — every window, not just the foreground one, so alt-tabbing to a chat on the other screen keeps the game's screen
/// paused) and reports the set on every check, so the caller can re-apply it to players started in between.
/// </summary>
public class FullscreenDetectionService : IDisposable
{
    private const int CHECK_INTERVAL_MS = 2000;

    private readonly ILogger<FullscreenDetectionService> _logger;
    private readonly Func<IReadOnlyList<int>> _fullscreenMonitors;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private IReadOnlyList<int> _last = Array.Empty<int>();

    /// <summary>
    /// Raised after every check (detection loop thread) with the monitor indexes a fullscreen app covers; an empty
    /// list once more on <see cref="Stop"/>.
    /// </summary>
    public event Action<IReadOnlyList<int>>? FullscreenMonitorsChecked;

    /// <summary>Whether the detection loop is currently running.</summary>
    public bool IsRunning => _monitorTask != null;

    /// <param name="fullscreenMonitors">The monitors a fullscreen app covers right now (any thread, never throws).</param>
    public FullscreenDetectionService(ILogger<FullscreenDetectionService> logger, Func<IReadOnlyList<int>> fullscreenMonitors)
    {
        _logger = logger;
        _fullscreenMonitors = fullscreenMonitors;
    }

    /// <summary>Start monitoring for fullscreen applications.</summary>
    public void Start()
    {
        if (_monitorTask != null)
            return;

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
        _logger.LogInformation("Fullscreen detection started (checking every {Interval}ms)", CHECK_INTERVAL_MS);
    }

    /// <summary>Stop monitoring; reports "no monitor covered" so everything paused resumes.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _monitorTask = null;

        if (_last.Count > 0)
        {
            _last = Array.Empty<int>();
            FullscreenMonitorsChecked?.Invoke(_last);
        }

        _logger.LogInformation("Fullscreen detection stopped");
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CHECK_INTERVAL_MS, ct);
                var now = _fullscreenMonitors();
                if (!now.SequenceEqual(_last))
                    _logger.LogInformation("Fullscreen monitors: [{Monitors}]", string.Join(", ", now));
                _last = now;
                FullscreenMonitorsChecked?.Invoke(now);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in fullscreen detection loop");
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

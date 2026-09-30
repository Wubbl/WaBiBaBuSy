using System.Collections.Concurrent;

namespace WaBiBaBuSy.Models.Networking;

/// <summary>
/// Sync-quality classification for a client's residual sync error.
/// The raw clock offset is NOT the error: clients subtract it from every server
/// timestamp, so what remains is the estimate's uncertainty (RTT/2).
/// </summary>
public enum DriftState
{
    /// <summary>No drift report received yet (or a pre-telemetry client).</summary>
    None = 0,

    /// <summary>Sync error ≤ 25ms — comfortably inside the ±50ms sync tolerance.</summary>
    Ok = 1,

    /// <summary>25ms &lt; sync error ≤ 50ms — approaching tolerance.</summary>
    Warn = 2,

    /// <summary>Sync error &gt; 50ms — sync tolerance breached (network too slow for a reliable offset).</summary>
    Breach = 3,

    /// <summary>Last report is older than 2× the heartbeat interval — value untrustworthy.</summary>
    Stale = 4,
}

/// <summary>What changed with a client's latest drift report (for log-once warnings).</summary>
[Flags]
public enum DriftEvent
{
    None = 0,

    /// <summary>The sync error newly crossed into <see cref="DriftState.Breach"/>.</summary>
    Breach = 1,

    /// <summary>
    /// The clock offset jumped between two reports: one machine's wall clock stepped
    /// (e.g. a w32tm resync). A scene already running there is off by the jump.
    /// </summary>
    ClockStep = 2,
}

/// <summary>
/// Server-side drift-telemetry aggregator. Stores the latest clock-offset/RTT
/// report per client (fed from heartbeats) and classifies the residual sync error
/// (<see cref="SyncErrorMs"/>) against the ±50ms tolerance. Passive telemetry only — never corrects playback;
/// the d2d_crossscreen path stays purely deterministic.
/// Thread-safe: heartbeat handlers write concurrently.
/// </summary>
public class DriftMonitor
{
    /// <summary>Sync error above this is <see cref="DriftState.Warn"/>.</summary>
    public const double WarnThresholdMs = 25.0;

    /// <summary>Sync error above this is <see cref="DriftState.Breach"/> (the ±50ms sync tolerance).</summary>
    public const double BreachThresholdMs = 50.0;

    /// <summary>An offset change larger than this between two reports is a <see cref="DriftEvent.ClockStep"/>.</summary>
    public const double ClockStepThresholdMs = BreachThresholdMs;

    /// <summary>Mirrors ClientConfiguration.HeartbeatIntervalSeconds' default; the server does not know each client's actual setting.</summary>
    public const int DefaultHeartbeatIntervalSeconds = 5;

    /// <summary>A report older than this many heartbeat intervals is <see cref="DriftState.Stale"/>.</summary>
    public const int StalenessMultiplier = 2;

    /// <summary>One client's latest drift report.</summary>
    public sealed record DriftReport(double OffsetMs, double RttMs, long ReportUtcMs);

    private readonly ConcurrentDictionary<string, DriftReport> _reports = new();

    /// <summary>
    /// Residual sync error after the client's offset correction: the true offset lies
    /// within ±RTT/2 of the NTP-style estimate, however far apart the clocks are.
    /// </summary>
    public static double SyncErrorMs(double rttMs) => Math.Max(0.0, rttMs) / 2.0;

    /// <summary>
    /// Store a client's latest report. Flags <see cref="DriftEvent.Breach"/> only when
    /// this report newly crosses into breach and <see cref="DriftEvent.ClockStep"/> when
    /// the offset jumped since the previous report — callers log each once instead of
    /// every heartbeat.
    /// </summary>
    public DriftEvent Record(string clientId, double offsetMs, double rttMs, long reportUtcMs)
    {
        _reports.TryGetValue(clientId, out var previous);
        _reports[clientId] = new DriftReport(offsetMs, rttMs, reportUtcMs);

        var events = DriftEvent.None;
        var wasBreached = previous != null && SyncErrorMs(previous.RttMs) > BreachThresholdMs;
        if (SyncErrorMs(rttMs) > BreachThresholdMs && !wasBreached)
            events |= DriftEvent.Breach;
        if (previous != null && Math.Abs(offsetMs - previous.OffsetMs) > ClockStepThresholdMs)
            events |= DriftEvent.ClockStep;
        return events;
    }

    /// <summary>Latest report for a client, or null if none received.</summary>
    public DriftReport? GetDrift(string clientId)
        => _reports.TryGetValue(clientId, out var report) ? report : null;

    /// <summary>Forget a client (disconnect / dead-client sweep).</summary>
    public void Remove(string clientId) => _reports.TryRemove(clientId, out _);

    /// <summary>
    /// Classify a sync error (see <see cref="SyncErrorMs"/>). <paramref name="lastReportUtcMs"/> == 0
    /// means "no report ever" (proto3 default) and yields <see cref="DriftState.None"/> —
    /// absent data must never display as perfectly synced.
    /// </summary>
    public static DriftState Classify(
        double syncErrorMs,
        long lastReportUtcMs,
        long nowUtcMs,
        int heartbeatIntervalSeconds = DefaultHeartbeatIntervalSeconds)
    {
        if (lastReportUtcMs <= 0)
            return DriftState.None;

        if (nowUtcMs - lastReportUtcMs > heartbeatIntervalSeconds * StalenessMultiplier * 1000L)
            return DriftState.Stale;

        var abs = Math.Abs(syncErrorMs);
        if (abs <= WarnThresholdMs) return DriftState.Ok;
        if (abs <= BreachThresholdMs) return DriftState.Warn;
        return DriftState.Breach;
    }
}

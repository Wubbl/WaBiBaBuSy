using System.Collections.Concurrent;

namespace WaBiBaBuSy.Models.Networking;

/// <summary>
/// Sync-quality classification for a client's reported clock offset.
/// </summary>
public enum DriftState
{
    /// <summary>No drift report received yet (or a pre-telemetry client).</summary>
    None = 0,

    /// <summary>|offset| ≤ 25ms — comfortably inside the ±50ms sync tolerance.</summary>
    Ok = 1,

    /// <summary>25ms &lt; |offset| ≤ 50ms — approaching tolerance.</summary>
    Warn = 2,

    /// <summary>|offset| &gt; 50ms — sync tolerance breached.</summary>
    Breach = 3,

    /// <summary>Last report is older than 2× the heartbeat interval — value untrustworthy.</summary>
    Stale = 4,
}

/// <summary>
/// Server-side drift-telemetry aggregator. Stores the latest clock-offset/RTT
/// report per client (fed from heartbeats) and classifies sync quality against
/// the ±50ms tolerance. Passive telemetry only — never corrects playback;
/// the d2d_crossscreen path stays purely deterministic.
/// Thread-safe: heartbeat handlers write concurrently.
/// </summary>
public class DriftMonitor
{
    /// <summary>|offset| above this is <see cref="DriftState.Warn"/>.</summary>
    public const double WarnThresholdMs = 25.0;

    /// <summary>|offset| above this is <see cref="DriftState.Breach"/> (the ±50ms sync tolerance).</summary>
    public const double BreachThresholdMs = 50.0;

    /// <summary>Mirrors ClientConfiguration.HeartbeatIntervalSeconds' default; the server does not know each client's actual setting.</summary>
    public const int DefaultHeartbeatIntervalSeconds = 5;

    /// <summary>A report older than this many heartbeat intervals is <see cref="DriftState.Stale"/>.</summary>
    public const int StalenessMultiplier = 2;

    /// <summary>One client's latest drift report.</summary>
    public sealed record DriftReport(double OffsetMs, double RttMs, long ReportUtcMs);

    private readonly ConcurrentDictionary<string, DriftReport> _reports = new();

    /// <summary>
    /// Store a client's latest report. Returns true only when this report newly
    /// crosses into <see cref="DriftState.Breach"/> — callers use it to log the
    /// breach once instead of every heartbeat.
    /// </summary>
    public bool Record(string clientId, double offsetMs, double rttMs, long reportUtcMs)
    {
        _reports.TryGetValue(clientId, out var previous);
        _reports[clientId] = new DriftReport(offsetMs, rttMs, reportUtcMs);

        var wasBreached = previous != null && Math.Abs(previous.OffsetMs) > BreachThresholdMs;
        var isBreached = Math.Abs(offsetMs) > BreachThresholdMs;
        return isBreached && !wasBreached;
    }

    /// <summary>Latest report for a client, or null if none received.</summary>
    public DriftReport? GetDrift(string clientId)
        => _reports.TryGetValue(clientId, out var report) ? report : null;

    /// <summary>Forget a client (disconnect / dead-client sweep).</summary>
    public void Remove(string clientId) => _reports.TryRemove(clientId, out _);

    /// <summary>
    /// Classify a reported offset. <paramref name="lastReportUtcMs"/> == 0 means
    /// "no report ever" (proto3 default) and yields <see cref="DriftState.None"/> —
    /// absent data must never display as perfectly synced.
    /// </summary>
    public static DriftState Classify(
        double offsetMs,
        long lastReportUtcMs,
        long nowUtcMs,
        int heartbeatIntervalSeconds = DefaultHeartbeatIntervalSeconds)
    {
        if (lastReportUtcMs <= 0)
            return DriftState.None;

        if (nowUtcMs - lastReportUtcMs > heartbeatIntervalSeconds * StalenessMultiplier * 1000L)
            return DriftState.Stale;

        var abs = Math.Abs(offsetMs);
        if (abs <= WarnThresholdMs) return DriftState.Ok;
        if (abs <= BreachThresholdMs) return DriftState.Warn;
        return DriftState.Breach;
    }
}

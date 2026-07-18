using WaBiBaBuSy.Models.Networking;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class DriftMonitorTests
{
    private const long Now = 1_000_000_000L; // arbitrary UTC ms reference

    // --- Classify: report presence ---

    [Fact]
    public void Classify_NoReportTimestamp_IsNone()
    {
        // proto3 default 0 must never render as "perfectly synced"
        Assert.Equal(DriftState.None, DriftMonitor.Classify(0, lastReportUtcMs: 0, nowUtcMs: Now));
    }

    // --- Classify: thresholds (|offset|, boundaries inclusive on the lower state) ---

    [Theory]
    [InlineData(0.0, DriftState.Ok)]
    [InlineData(25.0, DriftState.Ok)]      // boundary: ≤25 is Ok
    [InlineData(25.1, DriftState.Warn)]
    [InlineData(50.0, DriftState.Warn)]    // boundary: ≤50 is Warn
    [InlineData(50.1, DriftState.Breach)]
    [InlineData(-60.0, DriftState.Breach)] // negative offsets use absolute value
    [InlineData(-25.0, DriftState.Ok)]
    public void Classify_FreshReport_ByOffset(double offsetMs, DriftState expected)
    {
        Assert.Equal(expected, DriftMonitor.Classify(offsetMs, lastReportUtcMs: Now, nowUtcMs: Now));
    }

    // --- Classify: staleness (2 × heartbeat interval; default interval 5s → 10 000ms) ---

    [Fact]
    public void Classify_ReportOlderThanTwoIntervals_IsStale_EvenIfOffsetSmall()
    {
        Assert.Equal(DriftState.Stale,
            DriftMonitor.Classify(10.0, lastReportUtcMs: Now - 10_001, nowUtcMs: Now));
    }

    [Fact]
    public void Classify_ExactlyTwoIntervalsOld_IsNotStale()
    {
        Assert.Equal(DriftState.Ok,
            DriftMonitor.Classify(10.0, lastReportUtcMs: Now - 10_000, nowUtcMs: Now));
    }

    [Fact]
    public void Classify_CustomHeartbeatInterval_ChangesStalenessWindow()
    {
        // 2s interval → stale after 4 000ms
        Assert.Equal(DriftState.Stale,
            DriftMonitor.Classify(10.0, lastReportUtcMs: Now - 4_001, nowUtcMs: Now, heartbeatIntervalSeconds: 2));
        Assert.Equal(DriftState.Ok,
            DriftMonitor.Classify(10.0, lastReportUtcMs: Now - 4_000, nowUtcMs: Now, heartbeatIntervalSeconds: 2));
    }

    // --- Record: breach-transition signal (for log-once-per-breach behavior) ---

    [Fact]
    public void Record_FirstBreach_ReturnsTrue_RepeatBreach_ReturnsFalse()
    {
        var monitor = new DriftMonitor();
        Assert.True(monitor.Record("c1", offsetMs: 60, rttMs: 5, reportUtcMs: Now));   // enters breach
        Assert.False(monitor.Record("c1", offsetMs: 70, rttMs: 5, reportUtcMs: Now));  // still breached
        Assert.False(monitor.Record("c1", offsetMs: 10, rttMs: 5, reportUtcMs: Now));  // recovers
        Assert.True(monitor.Record("c1", offsetMs: 60, rttMs: 5, reportUtcMs: Now));   // re-enters breach
    }

    [Fact]
    public void Record_NonBreach_ReturnsFalse()
    {
        var monitor = new DriftMonitor();
        Assert.False(monitor.Record("c1", offsetMs: 10, rttMs: 5, reportUtcMs: Now));
        Assert.False(monitor.Record("c1", offsetMs: 50, rttMs: 5, reportUtcMs: Now)); // 50 is Warn, not Breach
    }

    // --- Record/GetDrift/Remove: storage ---

    [Fact]
    public void GetDrift_ReturnsLatestReport()
    {
        var monitor = new DriftMonitor();
        monitor.Record("c1", offsetMs: 12.5, rttMs: 3.5, reportUtcMs: Now);
        var report = monitor.GetDrift("c1");
        Assert.NotNull(report);
        Assert.Equal(12.5, report!.OffsetMs);
        Assert.Equal(3.5, report.RttMs);
        Assert.Equal(Now, report.ReportUtcMs);
    }

    [Fact]
    public void GetDrift_UnknownClient_ReturnsNull()
    {
        Assert.Null(new DriftMonitor().GetDrift("nobody"));
    }

    [Fact]
    public void Remove_ClearsReport()
    {
        var monitor = new DriftMonitor();
        monitor.Record("c1", offsetMs: 12, rttMs: 3, reportUtcMs: Now);
        monitor.Remove("c1");
        Assert.Null(monitor.GetDrift("c1"));
    }
}

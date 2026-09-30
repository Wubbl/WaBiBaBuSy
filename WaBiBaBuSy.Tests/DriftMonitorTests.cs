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

    // --- Classify: thresholds (|sync error|, boundaries inclusive on the lower state) ---

    [Theory]
    [InlineData(0.0, DriftState.Ok)]
    [InlineData(25.0, DriftState.Ok)]      // boundary: ≤25 is Ok
    [InlineData(25.1, DriftState.Warn)]
    [InlineData(50.0, DriftState.Warn)]    // boundary: ≤50 is Warn
    [InlineData(50.1, DriftState.Breach)]
    [InlineData(-60.0, DriftState.Breach)] // negative values use absolute value
    [InlineData(-25.0, DriftState.Ok)]
    public void Classify_FreshReport_BySyncError(double syncErrorMs, DriftState expected)
    {
        Assert.Equal(expected, DriftMonitor.Classify(syncErrorMs, lastReportUtcMs: Now, nowUtcMs: Now));
    }

    // --- SyncErrorMs: the offset is corrected, only the RTT/2 uncertainty remains ---

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(120.0, 60.0)]
    [InlineData(-5.0, 0.0)] // garbage RTT never yields a negative error
    public void SyncErrorMs_IsHalfTheRoundTrip(double rttMs, double expected)
    {
        Assert.Equal(expected, DriftMonitor.SyncErrorMs(rttMs));
    }

    [Fact]
    public void LargeCorrectedClockOffset_OnFastLan_ClassifiesOk()
    {
        // Two PCs 2.1 s apart on a 1 ms LAN: the offset is compensated, so sync is fine.
        const double rttMs = 1.0;
        Assert.Equal(DriftState.Ok,
            DriftMonitor.Classify(DriftMonitor.SyncErrorMs(rttMs), lastReportUtcMs: Now, nowUtcMs: Now));
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
    public void Record_FirstBreach_Flagged_RepeatBreach_NotFlagged()
    {
        // Breach = RTT/2 > 50 ms, i.e. RTT > 100 ms. Offsets stay constant: no clock step.
        var monitor = new DriftMonitor();
        Assert.Equal(DriftEvent.Breach, monitor.Record("c1", offsetMs: 0, rttMs: 120, reportUtcMs: Now)); // enters breach
        Assert.Equal(DriftEvent.None, monitor.Record("c1", offsetMs: 0, rttMs: 140, reportUtcMs: Now));   // still breached
        Assert.Equal(DriftEvent.None, monitor.Record("c1", offsetMs: 0, rttMs: 2, reportUtcMs: Now));     // recovers
        Assert.Equal(DriftEvent.Breach, monitor.Record("c1", offsetMs: 0, rttMs: 120, reportUtcMs: Now)); // re-enters breach
    }

    [Fact]
    public void Record_LargeButSteadyOffset_IsNotABreach()
    {
        // The reported ±2153 ms case: clocks apart, but corrected and steady.
        var monitor = new DriftMonitor();
        Assert.Equal(DriftEvent.None, monitor.Record("c1", offsetMs: 2153, rttMs: 1, reportUtcMs: Now));
        Assert.Equal(DriftEvent.None, monitor.Record("c1", offsetMs: 2154, rttMs: 1, reportUtcMs: Now));
    }

    [Fact]
    public void Record_NonBreach_NotFlagged()
    {
        var monitor = new DriftMonitor();
        Assert.Equal(DriftEvent.None, monitor.Record("c1", offsetMs: 10, rttMs: 5, reportUtcMs: Now));
        Assert.Equal(DriftEvent.None, monitor.Record("c1", offsetMs: 10, rttMs: 100, reportUtcMs: Now)); // RTT/2 = 50 is Warn
    }

    // --- Record: clock-step signal ---

    [Fact]
    public void Record_OffsetJump_FlagsClockStep()
    {
        var monitor = new DriftMonitor();
        monitor.Record("c1", offsetMs: 2153, rttMs: 1, reportUtcMs: Now);
        Assert.Equal(DriftEvent.ClockStep, monitor.Record("c1", offsetMs: 12, rttMs: 1, reportUtcMs: Now)); // w32tm resync
    }

    [Fact]
    public void Record_OffsetChangeWithinThreshold_IsNotAClockStep()
    {
        var monitor = new DriftMonitor();
        monitor.Record("c1", offsetMs: 100, rttMs: 1, reportUtcMs: Now);
        Assert.Equal(DriftEvent.None, monitor.Record("c1", offsetMs: 100 + DriftMonitor.ClockStepThresholdMs, rttMs: 1, reportUtcMs: Now));
    }

    [Fact]
    public void Record_FirstReport_IsNeverAClockStep()
    {
        Assert.Equal(DriftEvent.None, new DriftMonitor().Record("c1", offsetMs: 5000, rttMs: 1, reportUtcMs: Now));
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

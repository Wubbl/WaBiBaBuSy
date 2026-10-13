using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Tier 0 timing helpers: future start lead (so 20 nodes start on the same frame) and
/// per-node phase offsets (Wave mode). Both are pure functions of their inputs.
/// </summary>
public class SyncTimingTests
{
    [Theory]
    [InlineData(0, 0, 800)]          // nothing measured → floor
    [InlineData(50, 300, 800)]       // 3×50 + 300 = 450 → floor
    [InlineData(200, 600, 1200)]     // 3×200 + 600 = 1200
    [InlineData(400, 2000, 3200)]    // 3×400 + 2000 = 3200
    [InlineData(5000, 5000, 4000)]   // → ceiling
    [InlineData(-10, -10, 800)]      // garbage inputs → floor
    public void ComputeStartLeadMs_ClampsBetweenFloorAndCeiling(double maxRtt, int applyEstimate, int expected)
    {
        Assert.Equal(expected, SyncTiming.ComputeStartLeadMs(maxRtt, applyEstimate));
    }

    [Fact]
    public void ApplyNodePhase_NoDelay_ReturnsElapsedUnchanged()
    {
        Assert.Equal(12345L, SyncTiming.ApplyNodePhase(12345L, nodeOrder: 7, nodePhaseDelayMs: 0, perMonitorMode: true));
    }

    [Fact]
    public void ApplyNodePhase_SequentialMode_IgnoresDelay()
    {
        // A spanning animation must stay one shared world state; phase only applies per-monitor.
        Assert.Equal(5000L, SyncTiming.ApplyNodePhase(5000L, nodeOrder: 3, nodePhaseDelayMs: 250, perMonitorMode: false));
    }

    [Fact]
    public void ApplyNodePhase_PerMonitor_SubtractsOrderTimesDelay()
    {
        Assert.Equal(5000L - 3 * 250L, SyncTiming.ApplyNodePhase(5000L, nodeOrder: 3, nodePhaseDelayMs: 250, perMonitorMode: true));
    }

    [Fact]
    public void ApplyNodePhase_CanGoNegative_SoTheNodeWaitsForTheWave()
    {
        Assert.True(SyncTiming.ApplyNodePhase(100L, nodeOrder: 5, nodePhaseDelayMs: 250, perMonitorMode: true) < 0);
    }

    [Theory]
    [InlineData(-1L, false)]
    [InlineData(0L, true)]
    [InlineData(1L, true)]
    public void ShouldDrawAnimation_OnlyAtOrAfterStart(long elapsed, bool expected)
    {
        Assert.Equal(expected, SyncTiming.ShouldDrawAnimation(elapsed));
    }

    [Theory]
    [InlineData(-1L, 1000L, 999L)]
    [InlineData(-1000L, 1000L, 0L)]
    [InlineData(2500L, 1000L, 500L)]
    [InlineData(0L, 1000L, 0L)]
    public void PositiveModulo_NeverNegative(long value, long period, long expected)
    {
        Assert.Equal(expected, SyncTiming.PositiveModulo(value, period));
    }

    [Theory]
    [InlineData(0.0f, false, false)]     // no motion → keep previous facing
    [InlineData(0.2f, false, false)]     // below hysteresis → keep
    [InlineData(-0.2f, true, true)]      // below hysteresis → keep
    [InlineData(-1.0f, false, true)]     // clearly leftward → face left
    [InlineData(1.0f, true, false)]      // clearly rightward → face right
    public void ResolveFacingLeft_UsesHysteresis(float dx, bool previous, bool expected)
    {
        Assert.Equal(expected, SyncTiming.ResolveFacingLeft(dx, previous));
    }

    // Review 2026-10-13 A9: video follows the shared clock.
    [Theory]
    [InlineData(-500, 10_000, true, 0)]         // before the start
    [InlineData(2_500, 10_000, true, 2_500)]
    [InlineData(32_500, 10_000, true, 2_500)]   // third lap
    [InlineData(32_500, 10_000, false, 10_000)] // no loop: held on the last frame
    [InlineData(5_000, 0, true, 0)]             // duration unknown
    public void VideoTargetMs_FoldsOrHolds(long elapsed, long duration, bool loop, long expected) =>
        Assert.Equal(expected, SyncTiming.VideoTargetMs(elapsed, duration, loop));

    [Theory]
    [InlineData(2_600, 2_500, 10_000, true, 100)]
    [InlineData(9_900, 100, 10_000, true, -200)]   // across the loop seam: the short way round
    [InlineData(100, 9_900, 10_000, true, 200)]
    [InlineData(9_900, 100, 10_000, false, 9_800)]
    public void VideoDriftMs_TakesTheShortWayRound(long video, long target, long duration, bool loop, long expected) =>
        Assert.Equal(expected, SyncTiming.VideoDriftMs(video, target, duration, loop));

    [Theory]
    [InlineData(0, 1f)]
    [InlineData(15, 1f)]       // deadband
    [InlineData(100, 0.9f)]    // ahead → slower
    [InlineData(-50, 1.05f)]   // behind → faster
    [InlineData(-190, 1.1f)]   // clamped
    public void VideoCorrectionRate_NudgesTowardsTheClock(long drift, float expected) =>
        Assert.Equal(expected, SyncTiming.VideoCorrectionRate(drift), 3);
}

using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class FrameIntervalTrackerTests
{
    // ticksPerSecond = 1000 → timestamps are plain milliseconds
    private static FrameIntervalTracker Tracker(int capacity = 120) => new(capacity, ticksPerSecond: 1000);

    [Fact]
    public void Empty_OrSingleTimestamp_HasNoStats()
    {
        var t = Tracker();
        Assert.Null(t.Snapshot(60));
        t.Record(1000);
        Assert.Null(t.Snapshot(60));
    }

    [Fact]
    public void Steady60Hz_NoDrops()
    {
        var t = Tracker();
        for (int i = 0; i <= 60; i++) t.Record(1000 + (long)Math.Round(i * 1000.0 / 60));
        var s = t.Snapshot(60)!;
        Assert.Equal(60, s.Count);
        Assert.InRange(s.MeanFps, 59.5, 60.5);
        Assert.Equal(0, s.Dropped);
        Assert.InRange(s.MaxMs, 16, 18);
    }

    [Fact]
    public void Hitch_CountsAsDroppedAndShowsInMaxAndP99()
    {
        var t = Tracker();
        long ts = 0;
        t.Record(ts);
        for (int i = 0; i < 99; i++) t.Record(ts += 16);
        t.Record(ts += 50);
        var s = t.Snapshot(60)!;
        Assert.Equal(1, s.Dropped);          // 50 ms > 1.5 × 16.7 ms
        Assert.Equal(50, s.MaxMs);
        Assert.Equal(16, s.P50Ms);
        Assert.Equal(16, s.P99Ms);           // nearest rank: 99th of 100 sorted values — only the 100th is the hitch
    }

    [Fact]
    public void Capacity_KeepsOnlyTheNewestIntervals()
    {
        var t = Tracker(capacity: 3);
        long ts = 0;
        t.Record(ts);
        t.Record(ts += 100);
        for (int i = 0; i < 3; i++) t.Record(ts += 10);
        var s = t.Snapshot(0)!;
        Assert.Equal(3, s.Count);
        Assert.Equal(10, s.MaxMs);
        Assert.Equal(0, s.Dropped);          // refreshHz 0 = unknown → no drop counting
    }
    [Fact]
    public void Reset_ForgetsIntervals_AndTheLastTimestamp()
    {
        var t = Tracker();
        t.Record(0);
        t.Record(16);
        t.Record(32);
        t.Reset();
        Assert.Null(t.Snapshot(60));
        Assert.Equal(0, t.MeanIntervalMs);

        t.Record(10_000);                    // no interval across the reset gap
        Assert.Null(t.Snapshot(60));
        t.Record(10_020);
        var s = t.Snapshot(60)!;
        Assert.Equal(1, s.Count);
        Assert.Equal(20, s.MaxMs);
    }

    [Fact]
    public void MeanIntervalMs_MatchesSnapshotMeanFps()
    {
        var t = Tracker(capacity: 4);
        Assert.Equal(0, t.MeanIntervalMs);   // no interval yet
        long ts = 0;
        t.Record(ts);
        foreach (var d in new[] { 100, 10, 20, 30, 40 }) t.Record(ts += d);   // 100 drops out of the ring
        Assert.Equal(25, t.MeanIntervalMs, 9);
        Assert.Equal(1000.0 / 25, t.Snapshot(0)!.MeanFps, 9);
    }

    [Fact]
    public void Snapshot_AllocatesOnlyTheResult_MeanIntervalMsNothing()
    {
        // Called from the player's render thread for every estimated probe.
        var t = Tracker();
        long ts = 0;
        for (int i = 0; i < 200; i++) t.Record(ts += 16 + i % 3);
        t.Snapshot(60);
        _ = t.MeanIntervalMs;                // warm up (JIT)

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = t.MeanIntervalMs;
        long meanBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        var s = t.Snapshot(60);
        long snapshotBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(s);
        Assert.Equal(0, meanBytes);
        Assert.InRange(snapshotBytes, 1, 128);   // the FrameIntervalStats object, no copy of the 120 intervals
    }
}

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
}

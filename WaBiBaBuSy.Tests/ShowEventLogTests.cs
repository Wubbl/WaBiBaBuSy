using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ShowEventLogTests
{
    private static ShowEvent E(string id, long t0, int duration = 1000) => new() { EventId = id, T0Utc = t0, DurationMs = duration };

    [Fact]
    public void Add_SameEventId_IsIgnored()
    {
        var log = new ShowEventLog();
        Assert.True(log.Add(E("a", 0)));
        Assert.False(log.Add(E("a", 500)));
        Assert.Equal(1, log.Count);
    }

    [Fact]
    public void Add_EmptyId_IsRejected() => Assert.False(new ShowEventLog().Add(E("", 0)));

    [Fact]
    public void Add_BeyondCapacity_DropsTheOldest()
    {
        var log = new ShowEventLog();
        for (int i = 0; i < ShowEventLog.Capacity + 3; i++) log.Add(E($"e{i}", i));
        Assert.Equal(ShowEventLog.Capacity, log.Count);
        Assert.False(log.Contains("e0"));
        Assert.True(log.Contains($"e{ShowEventLog.Capacity + 2}"));
    }

    [Fact]
    public void Active_ExcludesEnded_IncludesFuture()
    {
        var log = new ShowEventLog();
        log.Add(E("past", 0, 1000));      // ended at 1000
        log.Add(E("now", 4500, 1000));
        log.Add(E("future", 9000, 1000));
        var ids = log.Active(5000).Select(e => e.EventId).ToList();
        Assert.Equal(new[] { "now", "future" }, ids);
    }

    [Fact]
    public void Prune_KeepsEndedEventsForAMinute()
    {
        var log = new ShowEventLog();
        log.Add(E("a", 0, 1000));
        Assert.Equal(0, log.Prune(1000 + ShowEventLog.KeepAfterEndMs - 1));
        Assert.Equal(1, log.Prune(1000 + ShowEventLog.KeepAfterEndMs));
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void CopyTo_FillsTheBufferInOrder()
    {
        var log = new ShowEventLog();
        log.Add(E("a", 0)); log.Add(E("b", 0));
        var buffer = new ShowEvent[ShowEventLog.Capacity];
        Assert.Equal(2, log.CopyTo(buffer));
        Assert.Equal("a", buffer[0].EventId);
        Assert.Equal("b", buffer[1].EventId);
    }

    [Fact]
    public void Clear_Empties()
    {
        var log = new ShowEventLog();
        log.Add(E("a", 0));
        log.Clear();
        Assert.Equal(0, log.Count);
    }
}

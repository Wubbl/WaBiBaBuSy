using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>BiBaBo timeline and drawing math shared by the players and the room preview.</summary>
public class BombMathTests
{
    private static BombGame Game() => new()
    {
        GameId = "g", BombOrder = 3, RouletteOrders = new[] { 0, 5, 2, 7, 1, 4, 6, 2, 3 }, IntroMs = 8000, TimerMs = 300_000,
    };

    private static NodeLayout Node(int index, int count, bool wraps = false) => new()
    {
        Id = $"n{index}", OffsetX = index * 1000, Width = 1000, Height = 600, CanvasWidth = count * 1000, CanvasHeight = 600, Order = index, Wraps = wraps,
    };

    [Fact]
    public void LogoTimerRect_FollowsTheFittedLogo()
    {
        var g = Game();
        g.BombSize = 100;
        Assert.Null(BombMath.LogoTimerRect(g, 800, 1000));                       // no box
        g.LogoTimerBox = new[] { 0.25f, 0.8f, 0.5f, 0.1f };
        var r = BombMath.LogoTimerRect(g, 800, 1000)!.Value;                     // drawn 80 × 100, centred
        Assert.Equal(-20f, r.Dx, 3);
        Assert.Equal(30f, r.Dy, 3);
        Assert.Equal(40f, r.W, 3);
        Assert.Equal(10f, r.H, 3);
    }

    [Fact]
    public void Phase_Boundaries()
    {
        var g = Game();
        Assert.Equal(BombPhase.Blackout, BombMath.Phase(g, 0));
        Assert.Equal(BombPhase.Roulette, BombMath.Phase(g, 1000));
        Assert.Equal(BombPhase.Drop, BombMath.Phase(g, 4000));
        Assert.Equal(BombPhase.Cables, BombMath.Phase(g, 5000));
        Assert.Equal(BombPhase.Countdown, BombMath.Phase(g, 8000));
        Assert.Equal(BombPhase.Hold, BombMath.Phase(g, 308_000));
        Assert.Equal(BombPhase.Ended, BombMath.Phase(g, -1));
    }

    [Fact]
    public void Remaining_CountsDown_HoldsAtZero()
    {
        var g = Game();
        Assert.Equal(300_000, BombMath.RemainingMs(g, 100));
        Assert.Equal(300_000, BombMath.RemainingMs(g, 8000));
        Assert.Equal(1, BombMath.RemainingMs(g, 307_999));
        Assert.Equal(0, BombMath.RemainingMs(g, 400_000));
    }

    [Fact]
    public void Roulette_LandsOnBombBeforeDrop()
    {
        var g = Game();
        Assert.Equal(g.BombOrder, BombMath.RouletteOrder(g, 3999));
        Assert.Equal(g.RouletteOrders[0], BombMath.RouletteOrder(g, 1000));
        Assert.Equal(-1, BombMath.RouletteOrder(g, 500));
        Assert.Equal(-1, BombMath.RouletteOrder(g, 4500));
        // decelerating: the first half of the time covers more than half of the hops
        int mid = Array.IndexOf(g.RouletteOrders, BombMath.RouletteOrder(g, 2500));
        Assert.True(mid >= g.RouletteOrders.Length / 2);
    }

    [Fact]
    public void SevenSegment_Digits()
    {
        Assert.All(BombMath.SevenSegment(8), Assert.True);
        Assert.Equal(new[] { false, true, true, false, false, false, false }, BombMath.SevenSegment(1));
        Assert.Equal(new[] { true, true, true, true, true, true, false }, BombMath.SevenSegment(0));
    }

    [Fact]
    public void TimerVisible_BlinksOnlyInTheLastThirtySeconds()
    {
        var g = Game();
        Assert.True(BombMath.TimerVisible(g, 100_000));
        Assert.True(BombMath.TimerVisible(g, 100_300));
        long last = 8000 + 300_000 - 20_000;
        Assert.NotEqual(BombMath.TimerVisible(g, last), BombMath.TimerVisible(g, last + 250));
    }

    [Fact]
    public void CableProgress_GrowsDuringCablesPhase()
    {
        var g = Game();
        Assert.Equal(0f, BombMath.CableProgress(g, 4000));
        Assert.Equal(0.5f, BombMath.CableProgress(g, 6500), 3);
        Assert.Equal(1f, BombMath.CableProgress(g, 9000));
        float[] pts = { 0, 0, 10, 0, 20, 0, 30, 0, 40, 0 };
        Assert.Equal(1, BombMath.PrefixPointCount(pts, 0f));
        Assert.Equal(3, BombMath.PrefixPointCount(pts, 0.5f));
        Assert.Equal(5, BombMath.PrefixPointCount(pts, 1f));
    }

    [Fact]
    public void SegmentsOnNode_OnlyVisibleSegments()
    {
        float[] pts = { 100, 50, 900, 60, 1500, 70, 2500, 80 };   // segments 0: 100-900, 1: 900-1500, 2: 1500-2500
        Span<(int I, float Shift)> output = stackalloc (int, float)[16];
        int n = BombMath.SegmentsOnNode(pts, 4, Node(1, 3), 10f, output);
        Assert.Equal(new[] { 1, 2 }, output[..n].ToArray().Select(o => o.I));
        Assert.All(output[..n].ToArray(), o => Assert.Equal(0f, o.Shift));
    }

    [Fact]
    public void SegmentsOnNode_WrapCopies()
    {
        float[] pts = { 500, 50, -300, 60 };   // from node 0 back across the seam
        Span<(int I, float Shift)> output = stackalloc (int, float)[8];
        int n = BombMath.SegmentsOnNode(pts, 2, Node(2, 3, wraps: true), 10f, output);
        Assert.Equal(1, n);
        Assert.Equal(3000f, output[0].Shift);
        Assert.Equal(0, BombMath.SegmentsOnNode(pts, 2, Node(2, 3, wraps: false), 10f, output));
    }

    [Fact]
    public void BombHidden_AfterOutcomeT0()
    {
        var g = Game();
        Assert.False(BombMath.BombVisible(g, 3000, null, 0));
        Assert.True(BombMath.BombVisible(g, 5000, null, 0));
        var outcome = new ShowEvent { Type = ShowEventType.BombOutcome };
        Assert.True(BombMath.BombVisible(g, 5000, outcome, -10));
        Assert.False(BombMath.BombVisible(g, 5000, outcome, 0));
    }

    [Fact]
    public void Shake_DecaysToZero()
    {
        var (x, y) = BombMath.Shake(42, 100);
        Assert.True(Math.Abs(x) <= 6 && Math.Abs(y) <= 6);
        Assert.True(Math.Abs(x) + Math.Abs(y) > 0);
        Assert.Equal((0f, 0f), BombMath.Shake(42, 1600));
    }

    [Fact]
    public void Shockwave_GrowsWithTime()
    {
        Assert.Equal(0f, BombMath.ShockwaveRadius(0, 2f));
        Assert.Equal(2000f, BombMath.ShockwaveRadius(1000, 2f));
    }

    [Fact]
    public void FormatTimer_MinutesSeconds()
    {
        Assert.Equal("5:00", BombMath.FormatTimer(300_000));
        Assert.Equal("2:31", BombMath.FormatTimer(150_100));
        Assert.Equal("0:00", BombMath.FormatTimer(0));
    }
}

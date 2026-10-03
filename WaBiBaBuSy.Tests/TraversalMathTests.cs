using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class TraversalMathTests
{
    private const int W = 6000, H = 1000;

    private static ShowEvent Lap(bool reverse = false, int duration = 10_000) => new()
    {
        EventId = "t", Type = ShowEventType.Traversal, DurationMs = duration, HeightFraction = 0.5f, SpriteAspect = 1.2f, Reverse = reverse,
    };

    [Fact]
    public void SpriteSize_FromHeightFractionAndAspect()
    {
        var (w, h) = TraversalMath.SpriteSize(Lap(), H);
        Assert.Equal(500f, h, 3);
        Assert.Equal(600f, w, 3);
    }

    [Theory]
    [InlineData(50f, float.NaN, 1000f, 1000f)]
    [InlineData(0f, 0f, 50f, 50f)]
    [InlineData(0.5f, 1000f, 500f * 20f, 500f)]
    public void SpriteSize_ClampsAbsurdValues(float fraction, float aspect, float expectedW, float expectedH)
    {
        var e = Lap(); e.HeightFraction = fraction; e.SpriteAspect = aspect;
        var (w, h) = TraversalMath.SpriteSize(e, H);
        Assert.Equal(expectedH, h, 3);
        Assert.Equal(expectedW, w, 3);
    }

    [Fact]
    public void NonWrapping_StartsAndEndsOffCanvas()
    {
        var start = TraversalMath.Pose(Lap(), W, H, wraps: false, localMs: 0)!.Value;
        var end = TraversalMath.Pose(Lap(), W, H, wraps: false, localMs: 9999)!.Value;
        Assert.True(start.VirtualX + start.Width <= 0.01f);
        Assert.True(end.VirtualX >= W - 2f);
        Assert.Equal((H - start.Height) / 2f, start.VirtualY, 3);
    }

    [Fact]
    public void Ring_RunsExactlyOneLap()
    {
        var start = TraversalMath.Pose(Lap(), W, H, wraps: true, localMs: 0)!.Value;
        var end = TraversalMath.Pose(Lap(), W, H, wraps: true, localMs: 9999)!.Value;
        Assert.InRange(end.VirtualX - start.VirtualX, W - 2f, W);   // one full canvas width further (within the last frame)
    }

    [Fact]
    public void Reverse_MirrorsThePath()
    {
        var fwd = TraversalMath.Pose(Lap(), W, H, false, 3000)!.Value;
        var rev = TraversalMath.Pose(Lap(reverse: true), W, H, false, 3000)!.Value;
        Assert.Equal(W - fwd.Width - fwd.VirtualX, rev.VirtualX, 2);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(10_000L)]
    public void OutsideTheEvent_NoPose(long local) => Assert.Null(TraversalMath.Pose(Lap(), W, H, false, local));

    [Theory]
    [InlineData(6000, 600f, false, 600f, 11_000)]
    [InlineData(6000, 600f, true, 600f, 10_000)]
    [InlineData(100, 10f, false, 600f, 2000)]
    [InlineData(1_000_000, 10f, false, 600f, 120_000)]
    [InlineData(6000, 600f, false, 0f, 11_000)]           // speed 0 → default 600 px/s
    public void DurationMs_OneLap(int canvasW, float spriteW, bool wraps, float speed, int expected) =>
        Assert.Equal(expected, TraversalMath.DurationMs(canvasW, spriteW, wraps, speed));
}

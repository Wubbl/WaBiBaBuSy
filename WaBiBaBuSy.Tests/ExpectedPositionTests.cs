using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ExpectedPositionTests
{
    private static readonly MovementConfig Static = new() { Type = MovementType.Static };

    private static NodeLayout Slice(int offsetX, int canvasWidth, int width = 1920, int height = 1080, bool mirrored = false, float scale = 1f) => new()
    {
        Id = $"n{offsetX}", OffsetX = offsetX, OffsetY = 0, Width = width, Height = height,
        CanvasWidth = canvasWidth, CanvasHeight = height, Mirrored = mirrored, Scale = scale,
    };

    [Fact]
    public void SingleNode_StaticMarker_IsCentered()
    {
        var e = ExpectedPosition.Compute(Static, 1000, 64, 64, ExpectedPosition.PerMonitorLayout("a", 1920, 1080));
        Assert.Equal(MarkerVisibility.Visible, e.Visibility);
        Assert.Equal(928, e.X);
        Assert.Equal(508, e.Y);
        Assert.Equal(960, e.CenterX);
    }

    [Fact]
    public void NegativeElapsed_IsNotOnScreen()
    {
        var e = ExpectedPosition.Compute(Static, -1, 64, 64, ExpectedPosition.PerMonitorLayout("a", 1920, 1080));
        Assert.Equal(MarkerVisibility.NotStarted, e.Visibility);
    }

    [Fact]
    public void StraddlingEdge_IsPartial()
    {
        // Two 1920 nodes, static sprite centered on the 3840 canvas → sits on the bezel.
        Assert.Equal(MarkerVisibility.Partial, ExpectedPosition.Compute(Static, 1000, 64, 64, Slice(0, 3840)).Visibility);
        Assert.Equal(MarkerVisibility.Partial, ExpectedPosition.Compute(Static, 1000, 64, 64, Slice(1920, 3840)).Visibility);
    }

    [Fact]
    public void OtherNodesSlice_IsOffScreen()
    {
        // Three nodes, center of the 5760 canvas is on node 2; node 3 sees nothing.
        Assert.Equal(MarkerVisibility.OffScreen, ExpectedPosition.Compute(Static, 1000, 64, 64, Slice(3840, 5760)).Visibility);
        Assert.Equal(MarkerVisibility.Visible, ExpectedPosition.Compute(Static, 1000, 64, 64, Slice(1920, 5760)).Visibility);
    }

    [Fact]
    public void MirroredSlice_FlipsXInsideTheSlice()
    {
        var linear = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 300 };
        for (long t = 0; t < 20_000; t += 700)
        {
            var normal = ExpectedPosition.Compute(linear, t, 64, 64, Slice(1920, 5760));
            var mirrored = ExpectedPosition.Compute(linear, t, 64, 64, Slice(1920, 5760, mirrored: true));
            Assert.Equal(normal.Visibility, mirrored.Visibility);
            if (normal.Visibility == MarkerVisibility.Visible)
                Assert.Equal(1920 - normal.X - 64, mirrored.X, 3);
        }
    }

    [Fact]
    public void PhysicalScale_ScalesToDevicePixels()
    {
        var one = ExpectedPosition.Compute(Static, 1000, 64, 64, Slice(0, 1920, scale: 1f));
        var two = ExpectedPosition.Compute(Static, 1000, 64, 64, Slice(0, 1920, scale: 2f));
        Assert.Equal(one.X * 2, two.X, 3);
        Assert.Equal(one.Y * 2, two.Y, 3);
        Assert.Equal(128, two.Width);
    }
    private static NodeLayout RingSlice(int offsetX) => new()
    {
        Id = $"r{offsetX}", OffsetX = offsetX, OffsetY = 0, Width = 1920, Height = 1080,
        CanvasWidth = 3840, CanvasHeight = 1080, Wraps = true,
    };

    // Ring lap = one perimeter: x = 1000 px/s × t mod 3840.
    private static readonly MovementConfig RingLinear = new() { Type = MovementType.Linear, SpeedPixelsPerSecond = 1000 };

    [Fact]
    public void Ring_SpriteOnTheSeam_IsPartialOnBothEnds_ViaTheSecondCopy()
    {
        // x = 3808: the sprite covers 3808..3872 → 32 px on the last seat, 32 px re-entered on seat 0.
        var first = ExpectedPosition.Compute(RingLinear, 3808, 64, 64, RingSlice(0));
        var last = ExpectedPosition.Compute(RingLinear, 3808, 64, 64, RingSlice(1920));
        Assert.Equal(MarkerVisibility.Partial, first.Visibility);
        Assert.Equal(-32, first.X, 3);         // only the wrapped copy (x − 3840) touches seat 0
        Assert.Equal(MarkerVisibility.Partial, last.Visibility);
        Assert.Equal(1888, last.X, 3);

        // Without the wrap the same position is nowhere near seat 0.
        var open = RingSlice(0);
        open.Wraps = false;
        Assert.Equal(MarkerVisibility.OffScreen, ExpectedPosition.Compute(new MovementConfig { Type = MovementType.Linear, StartX = 3808, EndX = 3808 }, 0, 64, 64, open).Visibility);
    }

    [Fact]
    public void Ring_AfterOneLap_IsBackOnSeatZero()
    {
        var e = ExpectedPosition.Compute(RingLinear, 3840 + 100, 64, 64, RingSlice(0));
        Assert.Equal(MarkerVisibility.Visible, e.Visibility);
        Assert.Equal(100, e.X, 3);
        Assert.Equal(MarkerVisibility.OffScreen, ExpectedPosition.Compute(RingLinear, 3840 + 100, 64, 64, RingSlice(1920)).Visibility);
    }

    [Theory]
    [InlineData(-32f, MarkerVisibility.Partial)]    // top half above the screen
    [InlineData(1050f, MarkerVisibility.Partial)]   // bottom part below the screen
    [InlineData(1080f, MarkerVisibility.OffScreen)] // entirely below
    [InlineData(-64f, MarkerVisibility.OffScreen)]  // entirely above
    public void YEdge_PartialOrOffScreen(float y, MarkerVisibility expected)
    {
        var at = new MovementConfig { Type = MovementType.Linear, StartX = 900, EndX = 900, StartY = y, EndY = y };
        var e = ExpectedPosition.Compute(at, 1000, 64, 64, ExpectedPosition.PerMonitorLayout("a", 1920, 1080));
        Assert.Equal(expected, e.Visibility);
    }

    [Fact]
    public void CanvasHeightUnset_FallsBackToTheSliceHeight_LikeThePlayer()
    {
        var unset = Slice(0, 1920);
        unset.CanvasHeight = 0;
        var e = ExpectedPosition.Compute(Static, 1000, 64, 64, unset);
        Assert.Equal(MarkerVisibility.Visible, e.Visibility);
        Assert.Equal(508, e.Y);   // centered on the 1080 slice, not at (0 − 64) / 2
    }
}

using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SpotlightMathTests
{
    private static NodeLayout Node(int order, int offsetX, bool mirrored = false, bool wraps = false, int canvasW = 10_000) => new()
    {
        Id = $"n{order}", Order = order, OffsetX = offsetX, Width = 1000, Height = 500,
        CanvasWidth = canvasW, CanvasHeight = 500, Mirrored = mirrored, Wraps = wraps,
    };

    private static ShowEvent Spot(int targetOrder, float targetCenterX) => new()
    {
        EventId = "s", Type = ShowEventType.Spotlight, DurationMs = 8000, TargetOrder = targetOrder, TargetCenterX = targetCenterX,
    };

    [Fact]
    public void Target_GetsAPulsingBorder_NoDim()
    {
        var v = SpotlightMath.Visual(Spot(2, 2500), Node(2, 2000), sharedCanvas: true, localMs: 4000);
        Assert.True(v.IsTarget);
        Assert.Equal(0f, v.DimOpacity);
        Assert.Equal(SpotlightMath.BorderWidth, v.BorderWidth);
        Assert.InRange(v.BorderOpacity, 0.2f, 1f);
    }

    [Fact]
    public void Others_AreDimmedTo40Percent_WithAChevron()
    {
        var v = SpotlightMath.Visual(Spot(2, 2500), Node(0, 0), sharedCanvas: true, localMs: 4000);
        Assert.False(v.IsTarget);
        Assert.Equal(0.6f, v.DimOpacity, 3);
        Assert.Equal(1, v.ChevronDirection);                    // target is to the right
        Assert.Equal(250f, v.ChevronCenterY, 3);
        Assert.Equal(0.18f * 500, v.ChevronSize, 3);
        Assert.InRange(v.ChevronCenterX, 500f - 41f, 500f + 41f);
    }

    [Fact]
    public void Chevron_IsFlippedOnMirroredNodes() =>
        Assert.Equal(-1, SpotlightMath.ChevronDirection(Spot(2, 2500), Node(0, 0, mirrored: true), sharedCanvas: true));

    [Fact]
    public void Chevron_PointsLeftWhenTheTargetIsBehind() =>
        Assert.Equal(-1, SpotlightMath.ChevronDirection(Spot(0, 500), Node(5, 5000), sharedCanvas: true));

    [Fact]
    public void Chevron_OnARing_TakesTheShorterWayAcrossTheSeam()
    {
        // Node near the end of the ring (center 9500), target near the start (500): 1000 to the right across the seam.
        Assert.Equal(1, SpotlightMath.ChevronDirection(Spot(0, 500), Node(9, 9000, wraps: true), sharedCanvas: true));
        Assert.Equal(-1, SpotlightMath.ChevronDirection(Spot(0, 500), Node(9, 9000, wraps: false), sharedCanvas: true));
    }

    [Fact]
    public void Chevron_WithoutASharedCanvas_ComparesOrders() =>
        Assert.Equal(1, SpotlightMath.ChevronDirection(Spot(4, 0), Node(1, 0), sharedCanvas: false));

    [Theory]
    [InlineData(-1L)]
    [InlineData(8000L)]
    public void OutsideTheEvent_DrawsNothing(long local) =>
        Assert.Equal(default, SpotlightMath.Visual(Spot(2, 2500), Node(0, 0), true, local));

    [Fact]
    public void NoTarget_DrawsNothing() =>
        Assert.Equal(default, SpotlightMath.Visual(Spot(-1, 0), Node(0, 0), true, 4000));
}

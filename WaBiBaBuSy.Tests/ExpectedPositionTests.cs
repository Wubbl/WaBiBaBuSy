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
}

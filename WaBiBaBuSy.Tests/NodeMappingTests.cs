using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class NodeMappingTests
{
    private static NodeLayout Node(int offsetX, bool mirrored = false, bool wraps = false, int canvasW = 10000)
        => new() { Id = "n", OffsetX = offsetX, OffsetY = 0, Width = 1920, Height = 1080, CanvasWidth = canvasW, CanvasHeight = 1080, Mirrored = mirrored, Wraps = wraps };

    [Fact]
    public void ToLocalX_Plain_SubtractsOffset()
    {
        Assert.Equal(100f, NodeMapping.ToLocalX(2020f, 200f, Node(1920)));
    }

    [Fact]
    public void ToLocalX_Mirrored_FlipsInsideSlice()
    {
        // Sprite left edge 100 px into the slice, width 200 → mirrored right edge 100 px from the right.
        Assert.Equal(1920f - 100f - 200f, NodeMapping.ToLocalX(2020f, 200f, Node(1920, mirrored: true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToVirtualX_IsInverse(bool mirrored)
    {
        var n = Node(3840, mirrored);
        float v = 4123.5f;
        Assert.Equal(v, NodeMapping.ToVirtualX(NodeMapping.ToLocalX(v, 300f, n), 300f, n), 3);
    }

    [Fact]
    public void WrapCopies_NonWrapping_SinglePosition()
    {
        Span<float> buf = stackalloc float[2];
        int n = NodeMapping.WrapCopies(9950f, 200f, Node(0, wraps: false), buf);
        Assert.Equal(1, n);
        Assert.Equal(9950f, buf[0]);
    }

    [Fact]
    public void WrapCopies_Straddling_GivesSecondCopyAtMinusPerimeter()
    {
        Span<float> buf = stackalloc float[2];
        int n = NodeMapping.WrapCopies(9950f, 200f, Node(0, wraps: true), buf);
        Assert.Equal(2, n);
        Assert.Equal(9950f, buf[0]);
        Assert.Equal(-50f, buf[1]);
    }

    [Fact]
    public void WrapCopies_FoldsOutOfRangeValues()
    {
        Span<float> buf = stackalloc float[2];
        int n = NodeMapping.WrapCopies(10100f, 200f, Node(0, wraps: true), buf);
        Assert.Equal(1, n);
        Assert.Equal(100f, buf[0]);

        n = NodeMapping.WrapCopies(-100f, 200f, Node(0, wraps: true), buf);
        Assert.Equal(2, n);          // 9900 + 200 > 10000 → straddles
        Assert.Equal(9900f, buf[0]);
        Assert.Equal(-100f, buf[1]);
    }

    [Fact]
    public void IsVisible_EdgeCases()
    {
        var n = Node(0);
        Assert.True(NodeMapping.IsVisible(-100f, 200f, n));
        Assert.True(NodeMapping.IsVisible(1900f, 200f, n));
        Assert.False(NodeMapping.IsVisible(-200f, 200f, n));
        Assert.False(NodeMapping.IsVisible(1920f, 200f, n));
    }

    [Fact]
    public void MirrorLocalX_Roundtrips()
    {
        var m = Node(0, mirrored: true);
        Assert.Equal(1920f - 10f - 50f, NodeMapping.MirrorLocalX(10f, 50f, m));
        Assert.Equal(10f, NodeMapping.MirrorLocalX(10f, 50f, Node(0)));
    }
}

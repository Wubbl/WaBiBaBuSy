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
    public void PanoramaSource_CutsTheNodesSliceScaledToTheImage()
    {
        // 10 000 px canvas, image at half resolution: node 2 (offset 3840) shows x 1920..2880 of a 5000 px image.
        Assert.Equal(((1920, 0, 960, 540), (0, 0, 1920, 1080)), NodeMapping.PanoramaSource(Node(3840), 5000, 540));
        // Exactly authored at canvas size: 1:1.
        Assert.Equal(((1920, 0, 1920, 1080), (0, 0, 1920, 1080)), NodeMapping.PanoramaSource(Node(1920), 10000, 1080));
    }

    [Fact]
    public void PanoramaSource_Stretch_FillsTheRowsTallestScreen_ShorterScreensCropCentred()
    {
        // Row of a 1440 px and a 1080 px screen on a 19 200 px canvas, image 19200 × 1080: ky = 1080 / 1440.
        var tall = new NodeLayout { OffsetX = 0, Width = 2560, Height = 1440, RowHeight = 1440, CanvasWidth = 19200, CanvasHeight = 2600 };
        var low = new NodeLayout { OffsetX = 2560, OffsetY = 1200, Width = 1920, Height = 1080, RowHeight = 1440, CanvasWidth = 19200, CanvasHeight = 2600 };
        Assert.Equal((0, 0, 2560, 1080), NodeMapping.PanoramaSource(tall, 19200, 1080, PanoramaFit.Stretch).Src);
        Assert.Equal((2560, 135, 1920, 810), NodeMapping.PanoramaSource(low, 19200, 1080, PanoramaFit.Stretch).Src);   // 180 px trimmed top and bottom
    }

    [Fact]
    public void PanoramaSource_FitHeight_KeepsAspectAndCentres()
    {
        // Two 1920 screens, a 20-seat image (19200 × 1080): no squeeze, the middle 3840 px show.
        var left = new NodeLayout { OffsetX = 0, Width = 1920, Height = 1080, CanvasWidth = 3840, CanvasHeight = 1080 };
        var right = new NodeLayout { OffsetX = 1920, Width = 1920, Height = 1080, CanvasWidth = 3840, CanvasHeight = 1080 };
        Assert.Equal(((7680, 0, 1920, 1080), (0, 0, 1920, 1080)), NodeMapping.PanoramaSource(left, 19200, 1080));
        Assert.Equal(((9600, 0, 1920, 1080), (0, 0, 1920, 1080)), NodeMapping.PanoramaSource(right, 19200, 1080));
        // Stretch squeezes the whole image onto the two screens instead.
        Assert.Equal((0, 0, 9600, 1080), NodeMapping.PanoramaSource(left, 19200, 1080, PanoramaFit.Stretch).Src);
    }

    [Fact]
    public void PanoramaSource_Fit_LeavesBandsAroundANarrowImage_FillCrops()
    {
        // 3840 × 1080 canvas, a 1920 × 1080 image.
        var left = new NodeLayout { OffsetX = 0, Width = 1920, Height = 1080, CanvasWidth = 3840, CanvasHeight = 1080 };
        // Fit: 1:1, centred → the left screen shows the image's left half on its right half.
        Assert.Equal(((0, 0, 960, 1080), (960, 0, 960, 1080)), NodeMapping.PanoramaSource(left, 1920, 1080, PanoramaFit.Fit));
        // Fill: ×2 to cover 3840 wide; 2160 tall, the middle 1080 (image rows 270..810) show.
        Assert.Equal(((0, 270, 960, 540), (0, 0, 1920, 1080)), NodeMapping.PanoramaSource(left, 1920, 1080, PanoramaFit.Fill));
    }

    [Fact]
    public void PanoramaSource_ClampsToTheImage_AndFallsBackToWholeImage()
    {
        Assert.Equal(((9000, 0, 1000, 1080), (0, 0, 1000, 1080)), NodeMapping.PanoramaSource(Node(9000), 10000, 1080));   // slice past the canvas end
        Assert.Equal(((0, 0, 800, 200), null), NodeMapping.PanoramaSource(null, 800, 200));                                // no layout
        Assert.Equal(0, NodeMapping.PanoramaSource(Node(20000), 800, 200).Src.Width);                                     // the image misses this screen
    }

    [Fact]
    public void MirrorLocalX_Roundtrips()
    {
        var m = Node(0, mirrored: true);
        Assert.Equal(1920f - 10f - 50f, NodeMapping.MirrorLocalX(10f, 50f, m));
        Assert.Equal(10f, NodeMapping.MirrorLocalX(10f, 50f, Node(0)));
    }
}

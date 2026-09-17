using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Seat-map layout: the ordered node chain broken into rows, laid out as a 1D path (Snake/Ring)
/// or stacked (Parallel). Mirroring/wrap rules from the seat-map design doc §3.
/// </summary>
public class SeatMapLayoutBuilderTests
{
    private const float Ppcm = 40f;   // 150 cm turn gap → 6000 px

    private static List<LayoutNodeInput> Nodes(int count, int w = 1920, int h = 1080, int gapCm = 0)
        => Enumerable.Range(0, count).Select(i => new LayoutNodeInput
        {
            Id = $"n{i}", WidthPx = w, HeightPx = h, GapBeforeCm = gapCm, PixelsPerCm = Ppcm
        }).ToList();

    [Fact]
    public void SingleRow_Snake_MatchesClassicLeftToRightLayout()
    {
        var nodes = Nodes(3, gapCm: 5); // 5 cm × 40 = 200 px bezel gaps
        var r = SeatMapLayoutBuilder.Build(SeatMap.SingleRow(), nodes);

        Assert.False(r.Wraps);
        Assert.Equal(3 * 1920 + 2 * 200, r.CanvasWidth);
        Assert.Equal(1080, r.CanvasHeight);
        Assert.Equal(new[] { 0, 2120, 4240 }, r.Nodes.Select(n => n.OffsetX));
        Assert.All(r.Nodes, n => Assert.False(n.Mirrored));
        Assert.All(r.Nodes, n => Assert.Equal(0, n.OffsetY));
        Assert.Equal(new[] { 0, 1, 2 }, r.Nodes.Select(n => n.Order));
    }

    [Fact]
    public void MixedHeights_AreVerticallyCentered()
    {
        var nodes = new List<LayoutNodeInput>
        {
            new() { Id = "a", WidthPx = 1920, HeightPx = 1080, PixelsPerCm = Ppcm },
            new() { Id = "b", WidthPx = 2560, HeightPx = 1440, PixelsPerCm = Ppcm },
        };
        var r = SeatMapLayoutBuilder.Build(SeatMap.SingleRow(), nodes);
        Assert.Equal(1440, r.CanvasHeight);
        Assert.Equal(180, r.Get("a")!.OffsetY);
        Assert.Equal(0, r.Get("b")!.OffsetY);
    }

    [Fact]
    public void TwoFacingRows_Ring_NothingMirrored_ClosedPerimeter()
    {
        var r = SeatMapLayoutBuilder.Build(SeatMap.TwoRows(10, facing: true), Nodes(20));

        Assert.True(r.Wraps);
        // 20 screens + turn between rows + closing turn
        Assert.Equal(20 * 1920 + 2 * 6000, r.CanvasWidth);
        Assert.All(r.Nodes, n => Assert.False(n.Mirrored));
        Assert.Equal(Enumerable.Range(0, 20), r.Nodes.Select(n => n.Order));
        Assert.All(r.Nodes.Take(10), n => Assert.Equal(0, n.RowIndex));
        Assert.All(r.Nodes.Skip(10), n => Assert.Equal(1, n.RowIndex));

        // Row 1 is traversed backwards: first node of row 1 sits at the far end physically.
        Assert.Equal(9, r.Get("n10")!.IndexInRow);
        Assert.Equal(0, r.Get("n19")!.IndexInRow);

        // Offsets increase monotonically along the path; the row turn adds the gap.
        Assert.Equal(10 * 1920 + 6000, r.Get("n10")!.OffsetX);
        Assert.Equal(9 * 1920, r.Get("n9")!.OffsetX);
    }

    [Fact]
    public void TwoSameSideRows_Ring_SecondRowMirrored()
    {
        var r = SeatMapLayoutBuilder.Build(SeatMap.TwoRows(10, facing: false), Nodes(20));
        Assert.All(r.Nodes.Take(10), n => Assert.False(n.Mirrored));
        Assert.All(r.Nodes.Skip(10), n => Assert.True(n.Mirrored));
    }

    [Fact]
    public void Snake_DoesNotWrap_AndHasNoClosingGap()
    {
        var map = SeatMap.TwoRows(10);
        map.Traversal = TraversalMode.Snake;
        var r = SeatMapLayoutBuilder.Build(map, Nodes(20));
        Assert.False(r.Wraps);
        Assert.Equal(20 * 1920 + 6000, r.CanvasWidth);
    }

    [Fact]
    public void ThreeRows_AlternateDirection()
    {
        var map = new SeatMap
        {
            Traversal = TraversalMode.Ring,
            Rows =
            {
                new SeatRow { SeatCount = 2, Orientation = RowOrientation.SameSide },
                new SeatRow { SeatCount = 2, Orientation = RowOrientation.SameSide },
                new SeatRow { SeatCount = 0, Orientation = RowOrientation.SameSide },
            }
        };
        var r = SeatMapLayoutBuilder.Build(map, Nodes(6));
        Assert.Equal(new[] { false, false, true, true, false, false }, r.Nodes.Select(n => n.Mirrored));
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2 }, r.Nodes.Select(n => n.RowIndex));
    }

    [Fact]
    public void Remainder_GoesToLastRow()
    {
        var r = SeatMapLayoutBuilder.Build(SeatMap.TwoRows(10), Nodes(25));
        Assert.Equal(10, r.Nodes.Count(n => n.RowIndex == 0));
        Assert.Equal(15, r.Nodes.Count(n => n.RowIndex == 1));
    }

    [Fact]
    public void FewerNodesThanSeats_StillWorks()
    {
        var r = SeatMapLayoutBuilder.Build(SeatMap.TwoRows(10), Nodes(3));
        Assert.Equal(3, r.Nodes.Count);
        Assert.All(r.Nodes, n => Assert.Equal(0, n.RowIndex));
        Assert.True(r.Wraps);
        Assert.Equal(3 * 1920 + 6000, r.CanvasWidth); // only the closing turn
    }

    [Fact]
    public void Parallel_StacksRows_AndMirrorsFacingRow()
    {
        var map = SeatMap.TwoRows(2, facing: true);
        map.Traversal = TraversalMode.Parallel;
        map.RowGapCm = 100; // 4000 px
        var r = SeatMapLayoutBuilder.Build(map, Nodes(4));

        Assert.False(r.Wraps);
        Assert.Equal(2 * 1920, r.CanvasWidth);
        Assert.Equal(1080 + 4000 + 1080, r.CanvasHeight);
        Assert.Equal(0, r.Get("n2")!.OffsetX);           // row 1 restarts at X = 0
        Assert.Equal(1080 + 4000, r.Get("n2")!.OffsetY);
        Assert.True(r.Get("n2")!.Mirrored);
        Assert.False(r.Get("n0")!.Mirrored);
    }

    [Fact]
    public void UnknownDpi_InsertsNoGaps()
    {
        var nodes = Nodes(2, gapCm: 10).Select(n => new LayoutNodeInput
            { Id = n.Id, WidthPx = n.WidthPx, HeightPx = n.HeightPx, GapBeforeCm = n.GapBeforeCm, PixelsPerCm = 0f }).ToList();
        var r = SeatMapLayoutBuilder.Build(SeatMap.TwoRows(1), nodes);
        Assert.Equal(2 * 1920, r.CanvasWidth);
    }

    [Fact]
    public void InvalidNodes_AreSkipped_EmptyInputIsSafe()
    {
        var r = SeatMapLayoutBuilder.Build(SeatMap.SingleRow(), new List<LayoutNodeInput>
        {
            new() { Id = "bad", WidthPx = 0, HeightPx = 0 }
        });
        Assert.Empty(r.Nodes);
        Assert.Equal(0, r.CanvasWidth);
    }
}

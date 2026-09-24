using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Room view geometry: lanes top to bottom, tiles left to right by physical seat (IndexInRow), so a
/// reversed (odd Ring/Snake) row shows its chain right-to-left. Drop targets are chain positions.
/// Coordinates: margin 12, header 26, lane pad 8, tile 184×158, gap 24, lane spacing 12.
/// </summary>
public class RoomGridTests
{
    private static RoomGrid Grid(TraversalMode traversal = TraversalMode.Ring, int nodes = 6)
    {
        var map = SeatMap.TwoRows(3);
        map.Traversal = traversal;
        var inputs = Enumerable.Range(0, nodes)
            .Select(i => new LayoutNodeInput { Id = $"n{i}", WidthPx = 1920, HeightPx = 1080 }).ToList();
        return RoomGrid.Build(map, SeatMapLayoutBuilder.Build(map, inputs));
    }

    [Fact]
    public void Lanes_AreStackedWithHeaderAndBody()
    {
        var g = Grid();
        Assert.Equal(2, g.Lanes.Count);
        Assert.Equal(12, g.Lanes[0].Header.Y);
        Assert.Equal(38, g.Lanes[0].Body.Y);
        Assert.Equal(212, g.Lanes[0].Body.Bottom);
        Assert.Equal(224, g.Lanes[1].Header.Y);
        Assert.Equal(436, g.NewRowZone.Y);
        Assert.Equal(488, g.Height);
        Assert.Equal(640, g.Width);
    }

    [Fact]
    public void ReversedRow_ShowsChainRightToLeft()
    {
        var g = Grid();
        var row1 = g.Tiles.Where(t => t.Row == 1).OrderBy(t => t.Tile.X).Select(t => t.Id);
        Assert.Equal(new[] { "n5", "n4", "n3" }, row1);
        Assert.True(g.Lanes[1].Reversed);
        Assert.Equal(2, g.Tiles.Single(t => t.Id == "n5").ChainIndex);
    }

    [Fact]
    public void ParallelRows_AreNotReversed()
    {
        var g = Grid(TraversalMode.Parallel);
        var row1 = g.Tiles.Where(t => t.Row == 1).OrderBy(t => t.Tile.X).Select(t => t.Id);
        Assert.Equal(new[] { "n3", "n4", "n5" }, row1);
    }

    [Fact]
    public void HitTile_FindsTile_AndMissesGap()
    {
        var g = Grid();
        Assert.Equal("n0", g.HitTile(30, 50)?.Id);
        Assert.Null(g.HitTile(210, 50));
    }

    [Fact]
    public void ResolveDrop_ForwardRow_BetweenFirstAndSecond()
    {
        var d = Grid().ResolveDrop(230, 100);
        Assert.Equal(0, d.Row);
        Assert.Equal(1, d.ChainIndex);
        Assert.Equal(216, d.MarkerX);
    }

    [Fact]
    public void ResolveDrop_ReversedRow_LeftEdgeIsEndOfChain_RightEdgeIsStart()
    {
        var g = Grid();
        Assert.Equal(new DropTarget(1, 3, 8, 258, 158), g.ResolveDrop(5, 300));
        Assert.Equal(0, g.ResolveDrop(630, 300).ChainIndex);
    }

    [Fact]
    public void ResolveDrop_LaneSpacing_SplitsBetweenLanes()
    {
        var g = Grid();
        Assert.Equal(0, g.ResolveDrop(100, 215).Row);
        Assert.Equal(1, g.ResolveDrop(100, 219).Row);
    }

    [Fact]
    public void ResolveDrop_BelowLastLane_IsNewRow()
    {
        var d = Grid().ResolveDrop(100, 450);
        Assert.Equal(2, d.Row);
        Assert.Equal(0, d.ChainIndex);
    }

    [Fact]
    public void Gap_HolderIsTheLaterNodeInTheChain()
    {
        var g = Grid();
        Assert.Equal("n1", g.HitGap(215, 95)?.HolderId);     // row 0: n0 → n1
        Assert.Equal("n5", g.HitGap(215, 305)?.HolderId);    // row 1 physical n5|n4: chain n4 → n5
        Assert.Null(g.HitGap(215, 50));                       // above the handle
    }

    [Fact]
    public void Chips_SitAtRightEndOfHeader()
    {
        var lane = Grid().Lanes[1];
        Assert.True(lane.MenuChip.Right <= lane.Header.Right);
        Assert.True(lane.FacingChip.Right <= lane.MenuChip.X);
        Assert.True(lane.Header.Contains(lane.FacingChip.X + 1, lane.FacingChip.Y + 1));
    }

    [Fact]
    public void EmptyLayout_HasOnlyNewRowZone()
    {
        var g = RoomGrid.Build(SeatMap.SingleRow(), new SeatMapLayoutResult());
        Assert.Empty(g.Lanes);
        Assert.Empty(g.Tiles);
        Assert.Equal(0, g.ResolveDrop(50, 20).Row);
        Assert.True(g.Height > 0);
    }
}

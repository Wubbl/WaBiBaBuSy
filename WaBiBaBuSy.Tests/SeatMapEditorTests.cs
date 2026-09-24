using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Room edits on the ordered node chain: rows are consecutive slices of the chain, every edit
/// returns a normalized seat map (no empty rows, last row SeatCount = 0) plus the new order.
/// </summary>
public class SeatMapEditorTests
{
    private static List<string> Ids(int n) => Enumerable.Range(0, n).Select(i => $"n{i}").ToList();

    /// <summary>Ring map with the given explicit seat counts; the last row always absorbs the rest.</summary>
    private static SeatMap Map(params int[] counts)
    {
        var map = new SeatMap { Traversal = TraversalMode.Ring };
        for (int i = 0; i < counts.Length; i++)
            map.Rows.Add(new SeatRow
            {
                Name = $"Row {i + 1}",
                Orientation = i == 0 ? RowOrientation.SameSide : RowOrientation.Facing,
                SeatCount = i == counts.Length - 1 ? 0 : counts[i]
            });
        return map;
    }

    /// <summary>"n0,n1|n2" — the non-empty rows of an edit, in chain order.</summary>
    private static string Rows(RoomEdit e) => string.Join("|",
        SeatMapEditor.Slice(e.Map, e.Order).Where(s => s.Ids.Count > 0).Select(s => string.Join(",", s.Ids)));

    private static void AssertNormalized(RoomEdit e)
    {
        Assert.NotEmpty(e.Map.Rows);
        Assert.Equal(0, e.Map.Rows[^1].SeatCount);
        Assert.All(e.Map.Rows.Take(e.Map.Rows.Count - 1), r => Assert.True(r.SeatCount > 0));
        Assert.Equal(e.Order.Count, e.Order.Distinct().Count());
        // Round trip: slicing the result reproduces exactly its rows, none empty.
        var slices = SeatMapEditor.Slice(e.Map, e.Order);
        Assert.Equal(e.Map.Rows.Count, slices.Count);
        if (e.Order.Count > 0) Assert.All(slices, s => Assert.NotEmpty(s.Ids));
    }

    [Fact]
    public void Slice_SplitsByCounts_LastRowTakesRemainder()
    {
        var slices = SeatMapEditor.Slice(Map(3, 0), Ids(7));
        Assert.Equal(new[] { "n0", "n1", "n2" }, slices[0].Ids);
        Assert.Equal(new[] { "n3", "n4", "n5", "n6" }, slices[1].Ids);
    }

    [Fact]
    public void Slice_NewNodeJoinsLastRow()
    {
        var map = Map(3, 0);
        Assert.Equal(3, SeatMapEditor.Slice(map, Ids(6))[1].Ids.Count);
        Assert.Equal("n6", SeatMapEditor.Slice(map, Ids(7))[1].Ids[^1]);
    }

    [Fact]
    public void Slice_FewerNodesThanSeats_LeavesTrailingRowsEmpty()
    {
        var slices = SeatMapEditor.Slice(Map(3, 3, 0), Ids(2));
        Assert.Equal(3, slices.Count);
        Assert.Equal(2, slices[0].Ids.Count);
        Assert.Empty(slices[1].Ids);
        Assert.Empty(slices[2].Ids);
    }

    [Fact]
    public void Layout_AfterNodeLeaves_DoesNotThrow()
    {
        var inputs = Ids(2).Select(id => new LayoutNodeInput { Id = id, WidthPx = 1920, HeightPx = 1080 }).ToList();
        var layout = SeatMapLayoutBuilder.Build(Map(3, 3, 0), inputs);
        Assert.Equal(2, layout.Nodes.Count);
        Assert.All(layout.Nodes, n => Assert.Equal(0, n.RowIndex));
    }

    [Fact]
    public void MoveNode_WithinRow_Forward_InsertsBeforeDisplayedTarget()
    {
        var e = SeatMapEditor.MoveNode(Map(3, 0), Ids(6), "n0", 0, 2);
        Assert.Equal("n1,n0,n2|n3,n4,n5", Rows(e));
        AssertNormalized(e);
    }

    [Fact]
    public void MoveNode_WithinRow_Backward()
    {
        var e = SeatMapEditor.MoveNode(Map(3, 0), Ids(6), "n2", 0, 0);
        Assert.Equal("n2,n0,n1|n3,n4,n5", Rows(e));
    }

    [Fact]
    public void MoveNode_ToOtherRow_UpdatesSeatCounts()
    {
        var e = SeatMapEditor.MoveNode(Map(3, 0), Ids(6), "n1", 1, 1);
        Assert.Equal("n0,n2|n3,n1,n4,n5", Rows(e));
        Assert.Equal(2, e.Map.Rows[0].SeatCount);
        Assert.Equal(new[] { "n0", "n2", "n3", "n1", "n4", "n5" }, e.Order);
        AssertNormalized(e);
    }

    [Fact]
    public void MoveNode_ToNewRow_AppendsFacingRow()
    {
        var e = SeatMapEditor.MoveNode(Map(3, 0), Ids(6), "n5", 2, 0);
        Assert.Equal("n0,n1,n2|n3,n4|n5", Rows(e));
        Assert.Equal(3, e.Map.Rows.Count);
        Assert.Equal(RowOrientation.Facing, e.Map.Rows[2].Orientation);
        Assert.Equal("Row 3", e.Map.Rows[2].Name);
        AssertNormalized(e);
    }

    [Fact]
    public void MoveNode_LastNodeOutOfRow_DropsEmptyRow()
    {
        var e = SeatMapEditor.MoveNode(Map(1, 0), Ids(3), "n0", 1, 0);
        Assert.Equal("n0,n1,n2", Rows(e));
        Assert.Single(e.Map.Rows);
        AssertNormalized(e);
    }

    [Fact]
    public void MoveNode_UnknownId_ReturnsNormalizedCopy()
    {
        var e = SeatMapEditor.MoveNode(Map(3, 0), Ids(6), "nope", 0, 0);
        Assert.Equal("n0,n1,n2|n3,n4,n5", Rows(e));
    }

    [Fact]
    public void SplitRowAt_MiddleNode_CreatesRowAfter_AndRenumbersDefaultNames()
    {
        var e = SeatMapEditor.SplitRowAt(Map(3, 0), Ids(6), "n1");
        Assert.Equal("n0|n1,n2|n3,n4,n5", Rows(e));
        Assert.Equal(new[] { "Row 1", "Row 2", "Row 3" }, e.Map.Rows.Select(r => r.Name));
        AssertNormalized(e);
    }

    [Fact]
    public void SplitRowAt_FirstNodeOfRow_NoChange()
    {
        var e = SeatMapEditor.SplitRowAt(Map(3, 0), Ids(6), "n3");
        Assert.Equal("n0,n1,n2|n3,n4,n5", Rows(e));
    }

    [Fact]
    public void MergeRowIntoPrevious_JoinsRows()
    {
        var e = SeatMapEditor.MergeRowIntoPrevious(Map(3, 0), Ids(6), 1);
        Assert.Equal("n0,n1,n2,n3,n4,n5", Rows(e));
        Assert.Single(e.Map.Rows);
    }

    [Fact]
    public void MergeRowIntoPrevious_RowZero_NoChange()
    {
        var e = SeatMapEditor.MergeRowIntoPrevious(Map(3, 0), Ids(6), 0);
        Assert.Equal("n0,n1,n2|n3,n4,n5", Rows(e));
    }

    [Fact]
    public void DeleteRow_First_MergesIntoNext()
    {
        var e = SeatMapEditor.DeleteRow(Map(2, 2, 0), Ids(6), 0);
        Assert.Equal("n0,n1,n2,n3|n4,n5", Rows(e));
        AssertNormalized(e);
    }

    [Fact]
    public void DeleteRow_OnlyRow_NoChange()
    {
        var e = SeatMapEditor.DeleteRow(Map(0), Ids(3), 0);
        Assert.Equal("n0,n1,n2", Rows(e));
    }

    [Fact]
    public void MoveRow_SwapsRowsAndChainOrder()
    {
        var e = SeatMapEditor.MoveRow(Map(3, 0), Ids(6), 1, -1);
        Assert.Equal("n3,n4,n5|n0,n1,n2", Rows(e));
        Assert.Equal(new[] { "n3", "n4", "n5", "n0", "n1", "n2" }, e.Order);
    }

    [Fact]
    public void MoveRow_OutOfRange_NoChange()
    {
        var e = SeatMapEditor.MoveRow(Map(3, 0), Ids(6), 0, -1);
        Assert.Equal("n0,n1,n2|n3,n4,n5", Rows(e));
    }

    [Fact]
    public void MakeRowFromSelection_KeepsChainOrder()
    {
        var e = SeatMapEditor.MakeRowFromSelection(Map(3, 0), Ids(6), new[] { "n4", "n1" });
        Assert.Equal("n0,n2|n3,n5|n1,n4", Rows(e));
        AssertNormalized(e);
    }

    [Fact]
    public void SplitEvenly_DistributesRemainderToFirstRows()
    {
        Assert.Equal("n0,n1,n2|n3,n4|n5,n6", Rows(SeatMapEditor.SplitEvenly(SeatMap.SingleRow(), Ids(7), 3)));
        var twenty = SeatMapEditor.SplitEvenly(SeatMap.SingleRow(), Ids(20), 2);
        Assert.Equal(10, twenty.Map.Rows[0].SeatCount);
        Assert.Equal(RowOrientation.Facing, twenty.Map.Rows[1].Orientation);
    }

    [Fact]
    public void SplitEvenly_MoreRowsThanNodes_Clamps()
    {
        Assert.Equal("n0|n1", Rows(SeatMapEditor.SplitEvenly(SeatMap.SingleRow(), Ids(2), 5)));
    }

    [Fact]
    public void SetRowOrientation_And_RenameRow()
    {
        var e = SeatMapEditor.SetRowOrientation(Map(3, 0), Ids(6), 1, RowOrientation.SameSide);
        Assert.Equal(RowOrientation.SameSide, e.Map.Rows[1].Orientation);
        var r = SeatMapEditor.RenameRow(e.Map, e.Order, 1, "  Window side  ");
        Assert.Equal("Window side", r.Map.Rows[1].Name);
        var blank = SeatMapEditor.RenameRow(r.Map, r.Order, 1, "   ");
        Assert.Equal("Window side", blank.Map.Rows[1].Name);
    }

    [Fact]
    public void Clone_IsDeep_AndKeepsRoomSettings()
    {
        var map = Map(3, 0);
        map.TurnGapCm = 222; map.CanvasMode = CanvasMode.Physical;
        var copy = SeatMapEditor.Clone(map);
        copy.Rows[0].Name = "changed";
        Assert.Equal("Row 1", map.Rows[0].Name);
        Assert.Equal(222, copy.TurnGapCm);
        Assert.Equal(CanvasMode.Physical, copy.CanvasMode);
    }

    [Fact]
    public void Edit_AgreesWithLayoutBuilder()
    {
        var e = SeatMapEditor.MoveNode(Map(3, 0), Ids(6), "n1", 1, 1);
        var inputs = e.Order.Select(id => new LayoutNodeInput { Id = id, WidthPx = 1920, HeightPx = 1080 }).ToList();
        var layout = SeatMapLayoutBuilder.Build(e.Map, inputs);
        var layoutRows = layout.Nodes.GroupBy(n => n.RowIndex).OrderBy(g => g.Key)
            .Select(g => string.Join(",", g.OrderBy(n => n.Order).Select(n => n.Id)));
        Assert.Equal(Rows(e), string.Join("|", layoutRows));
    }

    [Fact]
    public void IsRowReversed_MatchesTraversal()
    {
        Assert.False(SeatMapEditor.IsRowReversed(Map(3, 0), 0));
        Assert.True(SeatMapEditor.IsRowReversed(Map(3, 0), 1));
        Assert.False(SeatMapEditor.IsRowReversed(new SeatMap { Traversal = TraversalMode.Parallel }, 1));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(12, 12)]
    [InlineData(900, 500)]
    public void NormalizeGapCm_Clamps(int input, int expected)
        => Assert.Equal(expected, SeatMapEditor.NormalizeGapCm(input));
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace WaBiBaBuSy.Models.Topology;

/// <summary>One node's physical facts, in traversal (topology) order.</summary>
public sealed class LayoutNodeInput
{
    public required string Id { get; init; }
    public required int WidthPx { get; init; }
    public required int HeightPx { get; init; }

    /// <summary>Bezel/physical gap to the previous node in the same row (cm). Ignored for the first node of a row.</summary>
    public int GapBeforeCm { get; init; }

    /// <summary>Physical pixels per cm; 0 = unknown (no gaps are inserted before this node).</summary>
    public float PixelsPerCm { get; init; }
}

/// <summary>Result of laying the ordered chain out over a seat map.</summary>
public sealed class SeatMapLayoutResult
{
    public int CanvasWidth { get; init; }
    public int CanvasHeight { get; init; }
    public bool Wraps { get; init; }
    public IReadOnlyList<NodeLayout> Nodes { get; init; } = Array.Empty<NodeLayout>();

    public NodeLayout? Get(string id) => Nodes.FirstOrDefault(n => n.Id == id);
}

/// <summary>
/// Pure layout math: takes the topology-ordered node chain and a <see cref="SeatMap"/> and produces
/// per-node <see cref="NodeLayout"/>s. Used by the server apply path and by the live preview, so both
/// agree on where every node sits.
///
/// Geometry (see the seat-map design doc §3):
/// <list type="bullet">
/// <item>Ring/Snake: one 1D path. Row r is traversed in direction <c>dir_r = +1</c> (even) / <c>−1</c> (odd).
/// Between rows a turn gap is inserted; Ring adds a closing turn gap and sets <c>Wraps</c>.</item>
/// <item>Mirrored(node) = <c>dir_r × screenAxis_r &lt; 0</c>, where <c>screenAxis_r</c> is +1 for row 0 and
/// SameSide rows, −1 for Facing rows. Two facing rows in a Ring therefore mirror nothing.</item>
/// <item>Parallel: rows stacked in Y, each starting at X = 0; <c>dir_r = +1</c> for all rows.</item>
/// </list>
/// </summary>
public static class SeatMapLayoutBuilder
{
    public static SeatMapLayoutResult Build(SeatMap map, IReadOnlyList<LayoutNodeInput> orderedNodes)
    {
        var nodes = orderedNodes.Where(n => n.WidthPx > 0 && n.HeightPx > 0).ToList();
        if (nodes.Count == 0)
            return new SeatMapLayoutResult();

        var rows = AssignRows(map, nodes);
        return map.Traversal == TraversalMode.Parallel
            ? BuildParallel(map, rows)
            : BuildPath(map, rows, wraps: map.Traversal == TraversalMode.Ring);
    }

    /// <summary>Split the ordered chain into rows by SeatCount; the last row takes the remainder.</summary>
    private static List<List<LayoutNodeInput>> AssignRows(SeatMap map, List<LayoutNodeInput> nodes)
    {
        var rowDefs = map.Rows.Count > 0 ? map.Rows : new List<SeatRow> { new() { SeatCount = 0 } };
        var rows = new List<List<LayoutNodeInput>>();
        int idx = 0;
        for (int r = 0; r < rowDefs.Count; r++)
        {
            bool last = r == rowDefs.Count - 1;
            int take = last || rowDefs[r].SeatCount <= 0 ? nodes.Count - idx : Math.Min(rowDefs[r].SeatCount, nodes.Count - idx);
            if (take <= 0) break;
            rows.Add(nodes.GetRange(idx, take));
            idx += take;
        }
        if (idx < nodes.Count) rows[^1].AddRange(nodes.Skip(idx));
        return rows;
    }

    private static int GapPx(int cm, float ppcm) => cm > 0 && ppcm > 0f ? (int)(cm * ppcm) : 0;

    private static int ScreenAxis(SeatMap map, int rowIndex)
    {
        if (rowIndex == 0) return +1;
        var def = rowIndex < map.Rows.Count ? map.Rows[rowIndex] : map.Rows[^1];
        return def.Orientation == RowOrientation.Facing ? -1 : +1;
    }

    private static SeatMapLayoutResult BuildPath(SeatMap map, List<List<LayoutNodeInput>> rows, bool wraps)
    {
        var layouts = new List<NodeLayout>();
        int x = 0, order = 0;
        int maxH = rows.SelectMany(r => r).Max(n => n.HeightPx);

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            int dir = r % 2 == 0 ? +1 : -1;
            bool mirrored = dir * ScreenAxis(map, r) < 0;

            for (int i = 0; i < row.Count; i++)
            {
                var n = row[i];
                if (i == 0)
                {
                    if (r > 0) x += GapPx(map.TurnGapCm, n.PixelsPerCm);
                }
                else
                {
                    x += GapPx(n.GapBeforeCm, n.PixelsPerCm);
                }

                layouts.Add(new NodeLayout
                {
                    Id = n.Id,
                    OffsetX = x,
                    OffsetY = (maxH - n.HeightPx) / 2,
                    Width = n.WidthPx,
                    Height = n.HeightPx,
                    Mirrored = mirrored,
                    Wraps = wraps,
                    Order = order++,
                    RowIndex = r,
                    // Physical seat index as seen from row 0's side: odd rows are traversed backwards.
                    IndexInRow = dir > 0 ? i : row.Count - 1 - i
                });
                x += n.WidthPx;
            }
        }

        if (wraps)
            x += GapPx(map.TurnGapCm, rows[0][0].PixelsPerCm);   // closing turn back to seat 0

        foreach (var l in layouts) { l.CanvasWidth = x; l.CanvasHeight = maxH; }
        return new SeatMapLayoutResult { CanvasWidth = x, CanvasHeight = maxH, Wraps = wraps, Nodes = layouts };
    }

    private static SeatMapLayoutResult BuildParallel(SeatMap map, List<List<LayoutNodeInput>> rows)
    {
        var layouts = new List<NodeLayout>();
        int y = 0, order = 0, canvasW = 0;

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            bool mirrored = ScreenAxis(map, r) < 0;
            int rowH = row.Max(n => n.HeightPx);
            if (r > 0) y += GapPx(map.RowGapCm, row[0].PixelsPerCm);

            int x = 0;
            for (int i = 0; i < row.Count; i++)
            {
                var n = row[i];
                if (i > 0) x += GapPx(n.GapBeforeCm, n.PixelsPerCm);
                layouts.Add(new NodeLayout
                {
                    Id = n.Id,
                    OffsetX = x,
                    OffsetY = y + (rowH - n.HeightPx) / 2,
                    Width = n.WidthPx,
                    Height = n.HeightPx,
                    Mirrored = mirrored,
                    Wraps = false,
                    Order = order++,
                    RowIndex = r,
                    IndexInRow = i
                });
                x += n.WidthPx;
            }
            canvasW = Math.Max(canvasW, x);
            y += rowH;
        }

        foreach (var l in layouts) { l.CanvasWidth = canvasW; l.CanvasHeight = y; }
        return new SeatMapLayoutResult { CanvasWidth = canvasW, CanvasHeight = y, Wraps = false, Nodes = layouts };
    }
}

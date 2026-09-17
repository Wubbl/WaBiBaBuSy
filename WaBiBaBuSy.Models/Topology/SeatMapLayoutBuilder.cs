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
    public TraversalMode Traversal { get; init; } = TraversalMode.Snake;

    /// <summary>Reference pixels per cm of the canvas (Physical mode); 0 = pixel canvas.</summary>
    public float RefPixelsPerCm { get; init; }
    public bool IsPhysical => RefPixelsPerCm > 0f;

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
    /// <summary>A node's dimensions in canvas units after the physical scale has been applied.</summary>
    private readonly record struct Sized(LayoutNodeInput Node, int W, int H, float Scale);

    /// <param name="map">Room description.</param>
    /// <param name="orderedNodes">Nodes in traversal order.</param>
    /// <param name="referencePixelsPerCm">
    /// Pixels per cm of the reference monitor. Used only when <see cref="SeatMap.CanvasMode"/> is
    /// Physical; 0 or a Pixels canvas → every node has Scale 1 and gaps use each node's own DPI (legacy).
    /// </param>
    public static SeatMapLayoutResult Build(SeatMap map, IReadOnlyList<LayoutNodeInput> orderedNodes, float referencePixelsPerCm = 0f)
    {
        var nodes = orderedNodes.Where(n => n.WidthPx > 0 && n.HeightPx > 0).ToList();
        if (nodes.Count == 0)
            return new SeatMapLayoutResult();

        float refPpcm = map.CanvasMode == CanvasMode.Physical && referencePixelsPerCm > 0f ? referencePixelsPerCm : 0f;
        var sized = nodes.Select(n =>
        {
            float scale = refPpcm > 0f && n.PixelsPerCm > 0f ? n.PixelsPerCm / refPpcm : 1f;
            return new Sized(n, Math.Max(1, (int)Math.Round(n.WidthPx / scale)), Math.Max(1, (int)Math.Round(n.HeightPx / scale)), scale);
        }).ToList();

        var rows = AssignRows(map, sized);
        return map.Traversal == TraversalMode.Parallel
            ? BuildParallel(map, rows, refPpcm)
            : BuildPath(map, rows, wraps: map.Traversal == TraversalMode.Ring, refPpcm);
    }

    private static int AnchorY(SeatMap map, int bandHeight, int nodeHeight) => map.VerticalAnchor switch
    {
        VerticalAnchor.Top => 0,
        VerticalAnchor.Bottom => bandHeight - nodeHeight,
        _ => (bandHeight - nodeHeight) / 2
    };

    /// <summary>Gap in canvas px: cm × reference DPI on a physical canvas, cm × the node's own DPI otherwise.</summary>
    private static int GapPx(int cm, float nodePpcm, float refPpcm)
    {
        float ppcm = refPpcm > 0f ? refPpcm : nodePpcm;
        return cm > 0 && ppcm > 0f ? (int)(cm * ppcm) : 0;
    }

    /// <summary>Split the ordered chain into rows by SeatCount; the last row takes the remainder.</summary>
    private static List<List<Sized>> AssignRows(SeatMap map, List<Sized> nodes)
    {
        var rowDefs = map.Rows.Count > 0 ? map.Rows : new List<SeatRow> { new() { SeatCount = 0 } };
        var rows = new List<List<Sized>>();
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

    private static int ScreenAxis(SeatMap map, int rowIndex)
    {
        if (rowIndex == 0) return +1;
        var def = rowIndex < map.Rows.Count ? map.Rows[rowIndex] : map.Rows[^1];
        return def.Orientation == RowOrientation.Facing ? -1 : +1;
    }

    private static SeatMapLayoutResult BuildPath(SeatMap map, List<List<Sized>> rows, bool wraps, float refPpcm)
    {
        var layouts = new List<NodeLayout>();
        int x = 0, order = 0;
        int maxH = rows.SelectMany(r => r).Max(n => n.H);

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
                    if (r > 0) x += GapPx(map.TurnGapCm, n.Node.PixelsPerCm, refPpcm);
                }
                else
                {
                    x += GapPx(n.Node.GapBeforeCm, n.Node.PixelsPerCm, refPpcm);
                }

                layouts.Add(new NodeLayout
                {
                    Id = n.Node.Id,
                    OffsetX = x,
                    OffsetY = AnchorY(map, maxH, n.H),
                    Width = n.W,
                    Height = n.H,
                    Mirrored = mirrored,
                    Wraps = wraps,
                    Order = order++,
                    RowIndex = r,
                    // Physical seat index as seen from row 0's side: odd rows are traversed backwards.
                    IndexInRow = dir > 0 ? i : row.Count - 1 - i,
                    Scale = n.Scale,
                    RefPixelsPerCm = refPpcm
                });
                x += n.W;
            }
        }

        if (wraps)
            x += GapPx(map.TurnGapCm, rows[0][0].Node.PixelsPerCm, refPpcm);   // closing turn back to seat 0

        foreach (var l in layouts) { l.CanvasWidth = x; l.CanvasHeight = maxH; }
        return new SeatMapLayoutResult { CanvasWidth = x, CanvasHeight = maxH, Wraps = wraps, Traversal = map.Traversal, RefPixelsPerCm = refPpcm, Nodes = layouts };
    }

    private static SeatMapLayoutResult BuildParallel(SeatMap map, List<List<Sized>> rows, float refPpcm)
    {
        var layouts = new List<NodeLayout>();
        int y = 0, order = 0, canvasW = 0;

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            bool mirrored = ScreenAxis(map, r) < 0;
            int rowH = row.Max(n => n.H);
            if (r > 0) y += GapPx(map.RowGapCm, row[0].Node.PixelsPerCm, refPpcm);

            int x = 0;
            for (int i = 0; i < row.Count; i++)
            {
                var n = row[i];
                if (i > 0) x += GapPx(n.Node.GapBeforeCm, n.Node.PixelsPerCm, refPpcm);
                layouts.Add(new NodeLayout
                {
                    Id = n.Node.Id,
                    OffsetX = x,
                    OffsetY = y + AnchorY(map, rowH, n.H),
                    Width = n.W,
                    Height = n.H,
                    Mirrored = mirrored,
                    Wraps = false,
                    Order = order++,
                    RowIndex = r,
                    IndexInRow = i,
                    Scale = n.Scale,
                    RefPixelsPerCm = refPpcm
                });
                x += n.W;
            }
            canvasW = Math.Max(canvasW, x);
            y += rowH;
        }

        foreach (var l in layouts) { l.CanvasWidth = canvasW; l.CanvasHeight = y; }
        return new SeatMapLayoutResult { CanvasWidth = canvasW, CanvasHeight = y, Wraps = false, Traversal = TraversalMode.Parallel, RefPixelsPerCm = refPpcm, Nodes = layouts };
    }
}

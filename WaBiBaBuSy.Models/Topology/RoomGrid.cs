using System;
using System.Collections.Generic;
using System.Linq;

namespace WaBiBaBuSy.Models.Topology;

/// <summary>Axis-aligned box in room-view pixels (half-open: right/bottom edges excluded).</summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    /// <summary>X of the right edge (exclusive).</summary>
    public double Right => X + W;
    /// <summary>Y of the bottom edge (exclusive).</summary>
    public double Bottom => Y + H;
    /// <summary>True when the point lies inside the box (right/bottom edges excluded).</summary>
    public bool Contains(double px, double py) => px >= X && px < X + W && py >= Y && py < Y + H;
}

/// <summary>A node tile. <see cref="IndexInRow"/> = physical seat (left→right), <see cref="ChainIndex"/> = position along the path within the row.</summary>
public sealed record TileBox(string Id, int Row, int IndexInRow, int ChainIndex, Box Tile, Box Scene);

/// <summary>One row lane: header strip (name, direction, chips) above the tile body.</summary>
public sealed record LaneBox(int Row, bool Reversed, int Count, Box Header, Box Body, Box FacingChip, Box MenuChip);

/// <summary>Clickable gap handle between two adjacent tiles; edits <see cref="HolderId"/>'s gap to its previous node in the chain.</summary>
public sealed record GapBox(string HolderId, int Row, Box Hit);

/// <summary>Where a dragged node would land: row + chain insert index (<c>Row == Lanes.Count</c> = new row) and the insert marker.</summary>
public readonly record struct DropTarget(int Row, int ChainIndex, double MarkerX, double MarkerTop, double MarkerHeight);

/// <summary>
/// Pure geometry of the room view, built from the same <see cref="SeatMapLayoutResult"/> the players
/// use: lanes stacked top to bottom (one per row), tiles placed by physical seat so a reversed row
/// shows its chain right-to-left, a "+ new row" drop zone below the last lane. No UI types — the
/// Avalonia control only draws these boxes and routes pointer input through the hit tests.
/// </summary>
public sealed class RoomGrid
{
    /// <summary>Outer margin around the whole room (px).</summary>
    public const double Margin = 12;
    /// <summary>Height of a lane's header strip (name, direction, chips).</summary>
    public const double HeaderH = 26;
    /// <summary>Padding between a lane body's border and its tiles.</summary>
    public const double LanePad = 8;
    /// <summary>Vertical space between two lanes.</summary>
    public const double LaneSpacing = 12;
    /// <summary>Tile width.</summary>
    public const double TileW = 184;
    /// <summary>Tile height (scene preview plus caption lines).</summary>
    public const double TileH = 158;
    /// <summary>Width of the scene preview inside a tile.</summary>
    public const double SceneW = 176;
    /// <summary>Height of the scene preview inside a tile (16:9 of <see cref="SceneW"/>).</summary>
    public const double SceneH = 99;
    /// <summary>Offset of the scene preview from the tile top.</summary>
    public const double SceneTop = 4;
    /// <summary>Horizontal space between two tiles; also the gap handle's width.</summary>
    public const double TileGap = 24;
    /// <summary>Height of the "+ new row" drop zone.</summary>
    public const double NewRowH = 40;
    /// <summary>Height of the header chips (facing toggle, ⋯ menu).</summary>
    public const double ChipH = 18;
    /// <summary>Minimum lane width, so an empty or short row still has room for its header.</summary>
    public const double MinLaneW = 420;
    private const double GapHandleH = 24;

    /// <summary>One lane per non-empty seat-map row, top to bottom.</summary>
    public IReadOnlyList<LaneBox> Lanes { get; }
    /// <summary>Every node tile, lane by lane in physical seat order.</summary>
    public IReadOnlyList<TileBox> Tiles { get; }
    /// <summary>Gap handles between adjacent tiles of the same lane.</summary>
    public IReadOnlyList<GapBox> Gaps { get; }
    /// <summary>The "+ new row" drop zone below the last lane.</summary>
    public Box NewRowZone { get; }
    /// <summary>Total width of the room content (px), margins included.</summary>
    public double Width { get; }
    /// <summary>Total height of the room content (px), margins included.</summary>
    public double Height { get; }

    private RoomGrid(List<LaneBox> lanes, List<TileBox> tiles, List<GapBox> gaps, Box newRow, double width, double height)
    {
        Lanes = lanes; Tiles = tiles; Gaps = gaps; NewRowZone = newRow; Width = width; Height = height;
    }

    /// <summary>Lay out lanes, tiles and gap handles for a seat-map layout.</summary>
    /// <remarks>
    /// <paramref name="layout"/> must be built from the same <paramref name="map"/> (see
    /// <see cref="SeatMapLayoutBuilder"/>): row indices and reversed rows are read from both.
    /// </remarks>
    public static RoomGrid Build(SeatMap map, SeatMapLayoutResult layout)
    {
        var lanes = new List<LaneBox>();
        var tiles = new List<TileBox>();
        var gaps = new List<GapBox>();

        var rows = layout.Nodes.GroupBy(n => n.RowIndex).OrderBy(g => g.Key).ToList();
        int maxCount = rows.Count == 0 ? 0 : rows.Max(g => g.Count());
        double content = 2 * LanePad + maxCount * TileW + Math.Max(0, maxCount - 1) * TileGap;
        double laneW = Math.Max(MinLaneW, content);

        double y = Margin;
        foreach (var g in rows)
        {
            int r = g.Key;
            var physical = g.OrderBy(n => n.IndexInRow).ToList();
            int count = physical.Count;
            bool reversed = SeatMapEditor.IsRowReversed(map, r);

            var header = new Box(Margin, y, laneW, HeaderH);
            var menu = new Box(header.Right - 28, y + (HeaderH - ChipH) / 2, 24, ChipH);
            var facing = new Box(menu.X - 90, menu.Y, 84, ChipH);
            var body = new Box(Margin, y + HeaderH, laneW, TileH + 2 * LanePad);
            lanes.Add(new LaneBox(r, reversed, count, header, body, facing, menu));

            var rowTiles = new List<TileBox>(count);
            for (int p = 0; p < count; p++)
            {
                double tx = body.X + LanePad + p * (TileW + TileGap);
                double ty = body.Y + LanePad;
                int chain = reversed ? count - 1 - p : p;
                var tile = new TileBox(physical[p].Id, r, p, chain,
                    new Box(tx, ty, TileW, TileH),
                    new Box(tx + (TileW - SceneW) / 2, ty + SceneTop, SceneW, SceneH));
                rowTiles.Add(tile);
                tiles.Add(tile);
            }
            for (int p = 0; p + 1 < count; p++)
            {
                var a = rowTiles[p];
                var b = rowTiles[p + 1];
                string holder = a.ChainIndex > b.ChainIndex ? a.Id : b.Id;
                double hy = a.Scene.Y + SceneH / 2 - GapHandleH / 2;
                gaps.Add(new GapBox(holder, r, new Box(a.Tile.Right, hy, TileGap, GapHandleH)));
            }
            y = body.Bottom + LaneSpacing;
        }

        var newRow = new Box(Margin, y, laneW, NewRowH);
        return new RoomGrid(lanes, tiles, gaps, newRow, laneW + 2 * Margin, newRow.Bottom + Margin);
    }

    /// <summary>The tile under the point, or null.</summary>
    public TileBox? HitTile(double x, double y) => Tiles.FirstOrDefault(t => t.Tile.Contains(x, y));

    /// <summary>The gap handle under the point, or null.</summary>
    public GapBox? HitGap(double x, double y) => Gaps.FirstOrDefault(g => g.Hit.Contains(x, y));

    /// <summary>
    /// Drop target for a pointer position: the lane under the pointer (the spacing between two lanes
    /// is split in half), the physical slot before the first tile whose center lies right of the
    /// pointer, converted to a chain index (reversed rows count from the right). Below the last lane
    /// → new row.
    /// </summary>
    public DropTarget ResolveDrop(double x, double y)
    {
        if (Lanes.Count == 0 || y >= Lanes[^1].Body.Bottom + LaneSpacing / 2)
            return new DropTarget(Lanes.Count, 0, NewRowZone.X + LanePad, NewRowZone.Y, NewRowZone.H);

        var lane = Lanes.FirstOrDefault(l => y < l.Body.Bottom + LaneSpacing / 2) ?? Lanes[^1];
        double left = lane.Body.X + LanePad;
        int slot = (int)Math.Floor((x - left - TileW / 2) / (TileW + TileGap)) + 1;
        slot = Math.Clamp(slot, 0, lane.Count);
        int chain = lane.Reversed ? lane.Count - slot : slot;
        double markerX = left + slot * (TileW + TileGap) - TileGap / 2;
        return new DropTarget(lane.Row, chain, markerX, lane.Body.Y + LanePad, TileH);
    }
}

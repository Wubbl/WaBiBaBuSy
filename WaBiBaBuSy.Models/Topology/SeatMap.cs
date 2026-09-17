using System.Collections.Generic;

namespace WaBiBaBuSy.Models.Topology;

/// <summary>How the 1D animation path visits the rows of the room.</summary>
public enum TraversalMode
{
    /// <summary>Row 0 forward, row 1 backward, … — open path; the sprite restarts at seat 0 (today's behavior generalized).</summary>
    Snake,

    /// <summary>Like Snake but closed: the path wraps from the last seat back to seat 0 through a turn gap. The sprite circles the room forever.</summary>
    Ring,

    /// <summary>Rows stacked vertically as one tall canvas; a diagonal Bounce crosses from one table to the other.</summary>
    Parallel
}

/// <summary>Orientation of a row relative to row 0.</summary>
public enum RowOrientation
{
    /// <summary>Screens point the same way as row 0 (classroom layout).</summary>
    SameSide,

    /// <summary>People sit across the table; this row's screens' local +x runs opposite to row 0's.</summary>
    Facing
}

/// <summary>
/// One table row. Seats are taken from the ordered node chain: row 0 gets the first
/// <see cref="SeatCount"/> nodes, row 1 the next, … The last row absorbs any remainder.
/// </summary>
public class SeatRow
{
    public string Name { get; set; } = "Row";
    public RowOrientation Orientation { get; set; } = RowOrientation.Facing;

    /// <summary>Number of seats (nodes) in this row. 0 on the last row = "all remaining".</summary>
    public int SeatCount { get; set; } = 10;
}

/// <summary>
/// Describes the room: how the ordered node chain (topology order) is broken into rows, how the
/// rows face each other, and how the path travels between them. Persisted server-side
/// (<c>%APPDATA%\WaBiBaBuSy\seatmap.json</c>). A seat map with a single row reproduces the
/// classic left-to-right canvas exactly.
/// </summary>
public class SeatMap
{
    public string Name { get; set; } = "Room";
    public TraversalMode Traversal { get; set; } = TraversalMode.Ring;

    /// <summary>Physical distance (cm) the sprite crosses between the end of one row and the start of the next (Ring/Snake), and for the closing turn of a Ring.</summary>
    public int TurnGapCm { get; set; } = 150;

    /// <summary>Vertical distance (cm) between rows in Parallel traversal.</summary>
    public int RowGapCm { get; set; } = 120;

    public List<SeatRow> Rows { get; set; } = new();

    /// <summary>The classic single-row, left-to-right wall (behavior identical to pre-seat-map layouts).</summary>
    public static SeatMap SingleRow() => new()
    {
        Name = "Single row",
        Traversal = TraversalMode.Snake,
        Rows = { new SeatRow { Name = "Row 1", Orientation = RowOrientation.SameSide, SeatCount = 0 } }
    };

    /// <summary>Two rows of <paramref name="seatsPerRow"/> seats, closed ring, rows facing each other (the LAN-party default).</summary>
    public static SeatMap TwoRows(int seatsPerRow, bool facing = true) => new()
    {
        Name = "Two rows",
        Traversal = TraversalMode.Ring,
        Rows =
        {
            new SeatRow { Name = "Row 1", Orientation = RowOrientation.SameSide, SeatCount = seatsPerRow },
            new SeatRow { Name = "Row 2", Orientation = facing ? RowOrientation.Facing : RowOrientation.SameSide, SeatCount = 0 }
        }
    };

    /// <summary>True when more than one row is configured (i.e. rows/turns actually matter).</summary>
    public bool IsMultiRow => Rows.Count > 1;
}

/// <summary>
/// Everything a player needs to place itself on the shared canvas. Replaces the loose
/// VirtualCanvasWidth / MonitorOffsetX pair (which stay populated for one release for older clients).
/// </summary>
public class NodeLayout
{
    /// <summary>Client/node id this layout belongs to.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>This node's slice on the 1D path (or on its row for Parallel), in canvas px.</summary>
    public int OffsetX { get; set; }

    /// <summary>Vertical offset inside the canvas (centering on mixed heights; row stacking for Parallel).</summary>
    public int OffsetY { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }

    /// <summary>The path runs against this screen's local +x: virtual→local X is mirrored inside the slice.</summary>
    public bool Mirrored { get; set; }

    /// <summary>Positions are taken modulo <see cref="CanvasWidth"/> (Ring); a sprite straddling the seam is drawn twice.</summary>
    public bool Wraps { get; set; }

    /// <summary>Traversal index (0-based) — drives Wave-mode phase.</summary>
    public int Order { get; set; }

    public int RowIndex { get; set; }

    /// <summary>Physical position within the row (0 = leftmost seat as seen from row 0's side).</summary>
    public int IndexInRow { get; set; }

    /// <summary>Reserved for the physical canvas (rpx → device px). 1.0 = pixel canvas.</summary>
    public float Scale { get; set; } = 1f;
}

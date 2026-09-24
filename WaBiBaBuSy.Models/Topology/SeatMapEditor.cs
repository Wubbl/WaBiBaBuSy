using System;
using System.Collections.Generic;
using System.Linq;

namespace WaBiBaBuSy.Models.Topology;

/// <summary>One table row during an edit: its settings plus the node ids it holds, in chain (traversal) order.</summary>
public sealed record RowSlice(SeatRow Row, List<string> Ids);

/// <summary>Result of a room edit: the normalized seat map plus the new chain (topology) order.</summary>
public sealed record RoomEdit(SeatMap Map, IReadOnlyList<string> Order);

/// <summary>
/// Pure edits on the room: rows are consecutive slices of the ordered node chain
/// (see <see cref="SeatRow"/>). Every edit slices the chain, changes the slices and normalizes the
/// result: empty rows are dropped, every row but the last stores its exact seat count and the last
/// row stores 0 ("all remaining") so nodes that join later land at the end of the last row.
/// <see cref="SeatMapLayoutBuilder"/> is untouched — it simply reads the new counts.
/// </summary>
public static class SeatMapEditor
{
    /// <summary>Largest gap between two adjacent screens that the UI accepts (cm).</summary>
    public const int MaxGapCm = 500;

    /// <summary>Clamp a user-entered gap to 0..<see cref="MaxGapCm"/>.</summary>
    public static int NormalizeGapCm(int cm) => Math.Clamp(cm, 0, MaxGapCm);

    /// <summary>
    /// True when the path runs right-to-left through this row (odd rows of Ring/Snake), matching
    /// <see cref="SeatMapLayoutBuilder"/>'s <c>dir = r % 2 == 0 ? +1 : -1</c>.
    /// </summary>
    public static bool IsRowReversed(SeatMap map, int rowIndex)
        => map.Traversal != TraversalMode.Parallel && rowIndex % 2 == 1;

    /// <summary>Deep copy (rows included) of a seat map.</summary>
    public static SeatMap Clone(SeatMap map) => new()
    {
        Name = map.Name,
        Traversal = map.Traversal,
        TurnGapCm = map.TurnGapCm,
        RowGapCm = map.RowGapCm,
        CanvasMode = map.CanvasMode,
        VerticalAnchor = map.VerticalAnchor,
        Rows = map.Rows.Select(CloneRow).ToList()
    };

    /// <summary>
    /// Split the chain into rows exactly like <see cref="SeatMapLayoutBuilder"/> does, but keep
    /// trailing empty rows so slice indices stay aligned with <see cref="SeatMap.Rows"/>.
    /// </summary>
    public static List<RowSlice> Slice(SeatMap map, IReadOnlyList<string> order)
    {
        var defs = map.Rows.Count > 0
            ? map.Rows
            : new List<SeatRow> { new() { Name = "Row 1", Orientation = RowOrientation.SameSide, SeatCount = 0 } };
        var slices = new List<RowSlice>(defs.Count);
        int idx = 0;
        for (int r = 0; r < defs.Count; r++)
        {
            bool last = r == defs.Count - 1;
            int remaining = order.Count - idx;
            int take = last || defs[r].SeatCount <= 0 ? remaining : Math.Min(defs[r].SeatCount, remaining);
            slices.Add(new RowSlice(CloneRow(defs[r]), order.Skip(idx).Take(take).ToList()));
            idx += take;
        }
        return slices;
    }

    /// <summary>Move a node to <paramref name="targetRow"/> at <paramref name="targetIndex"/> (chain order, as displayed before the move). A row index past the end appends a new row.</summary>
    public static RoomEdit MoveNode(SeatMap map, IReadOnlyList<string> order, string id, int targetRow, int targetIndex)
    {
        var slices = Slice(map, order);
        var (r0, i0) = Find(slices, id);
        if (r0 < 0) return Compose(map, slices);

        targetRow = Math.Max(0, targetRow);
        if (targetRow == r0 && targetIndex > i0) targetIndex--;
        slices[r0].Ids.RemoveAt(i0);

        if (targetRow >= slices.Count)
            slices.Add(new RowSlice(NewRow(NonEmptyCount(slices)), new List<string> { id }));
        else
            slices[targetRow].Ids.Insert(Math.Clamp(targetIndex, 0, slices[targetRow].Ids.Count), id);
        return Compose(map, slices);
    }

    /// <summary>Make <paramref name="id"/> the first seat of a new row inserted after its current row. No-op for a node that already starts a row.</summary>
    public static RoomEdit SplitRowAt(SeatMap map, IReadOnlyList<string> order, string id)
    {
        var slices = Slice(map, order);
        var (r, i) = Find(slices, id);
        if (r < 0 || i == 0) return Compose(map, slices);
        var tail = slices[r].Ids.GetRange(i, slices[r].Ids.Count - i);
        slices[r].Ids.RemoveRange(i, tail.Count);
        slices.Insert(r + 1, new RowSlice(NewRow(r + 1), tail));
        return Compose(map, slices);
    }

    /// <summary>Append row <paramref name="row"/>'s seats to the previous row. No-op for row 0.</summary>
    public static RoomEdit MergeRowIntoPrevious(SeatMap map, IReadOnlyList<string> order, int row)
    {
        var slices = Slice(map, order);
        if (row <= 0 || row >= slices.Count) return Compose(map, slices);
        slices[row - 1].Ids.AddRange(slices[row].Ids);
        slices.RemoveAt(row);
        return Compose(map, slices);
    }

    /// <summary>Remove a row; its seats merge into the previous row (row 0: into the next). No-op when only one row holds nodes.</summary>
    public static RoomEdit DeleteRow(SeatMap map, IReadOnlyList<string> order, int row)
    {
        var slices = Slice(map, order);
        if (NonEmptyCount(slices) <= 1 || row < 0 || row >= slices.Count) return Compose(map, slices);
        if (row > 0) return MergeRowIntoPrevious(map, order, row);
        slices[1].Ids.InsertRange(0, slices[0].Ids);
        slices.RemoveAt(0);
        return Compose(map, slices);
    }

    /// <summary>Swap a row with its neighbour (<paramref name="delta"/> = −1 up, +1 down).</summary>
    public static RoomEdit MoveRow(SeatMap map, IReadOnlyList<string> order, int row, int delta)
    {
        var slices = Slice(map, order);
        int target = row + delta;
        int count = NonEmptyCount(slices);
        if (row < 0 || row >= count || target < 0 || target >= count) return Compose(map, slices);
        (slices[row], slices[target]) = (slices[target], slices[row]);
        return Compose(map, slices);
    }

    /// <summary>Move the given nodes (in their chain order) into a new last row.</summary>
    public static RoomEdit MakeRowFromSelection(SeatMap map, IReadOnlyList<string> order, IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids);
        var selected = order.Where(set.Contains).ToList();
        var slices = Slice(map, order);
        if (selected.Count == 0) return Compose(map, slices);
        foreach (var s in slices) s.Ids.RemoveAll(set.Contains);
        slices.Add(new RowSlice(NewRow(NonEmptyCount(slices)), selected));
        return Compose(map, slices);
    }

    /// <summary>Quick setup: split the chain into <paramref name="rowCount"/> rows of (nearly) equal size, keeping existing row settings where rows exist.</summary>
    public static RoomEdit SplitEvenly(SeatMap map, IReadOnlyList<string> order, int rowCount)
    {
        int n = order.Count;
        int rows = Math.Clamp(rowCount, 1, Math.Max(1, n));
        int baseSize = n / rows, extra = n % rows, idx = 0;
        var slices = new List<RowSlice>(rows);
        for (int r = 0; r < rows; r++)
        {
            int take = baseSize + (r < extra ? 1 : 0);
            var meta = r < map.Rows.Count ? CloneRow(map.Rows[r]) : NewRow(r);
            slices.Add(new RowSlice(meta, order.Skip(idx).Take(take).ToList()));
            idx += take;
        }
        return Compose(map, slices);
    }

    /// <summary>Set one row's orientation (Facing / SameSide).</summary>
    public static RoomEdit SetRowOrientation(SeatMap map, IReadOnlyList<string> order, int row, RowOrientation orientation)
    {
        var slices = Slice(map, order);
        if (row >= 0 && row < slices.Count) slices[row].Row.Orientation = orientation;
        return Compose(map, slices);
    }

    /// <summary>Rename one row; blank names are ignored.</summary>
    public static RoomEdit RenameRow(SeatMap map, IReadOnlyList<string> order, int row, string name)
    {
        var slices = Slice(map, order);
        var trimmed = name?.Trim() ?? string.Empty;
        if (row >= 0 && row < slices.Count && trimmed.Length > 0) slices[row].Row.Name = trimmed;
        return Compose(map, slices);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static SeatRow CloneRow(SeatRow r) => new() { Name = r.Name, Orientation = r.Orientation, SeatCount = r.SeatCount };

    private static SeatRow NewRow(int index) => new()
    {
        Name = $"Row {index + 1}",
        Orientation = index == 0 ? RowOrientation.SameSide : RowOrientation.Facing
    };

    private static bool IsDefaultName(string name)
        => name.StartsWith("Row ", StringComparison.Ordinal) && int.TryParse(name.AsSpan(4), out _);

    private static int NonEmptyCount(List<RowSlice> slices) => slices.Count(s => s.Ids.Count > 0);

    private static (int Row, int Index) Find(List<RowSlice> slices, string id)
    {
        for (int r = 0; r < slices.Count; r++)
        {
            int i = slices[r].Ids.IndexOf(id);
            if (i >= 0) return (r, i);
        }
        return (-1, -1);
    }

    /// <summary>Normalize slices into a seat map + order (see class remarks).</summary>
    private static RoomEdit Compose(SeatMap map, List<RowSlice> slices)
    {
        var kept = slices.Where(s => s.Ids.Count > 0).ToList();
        var result = Clone(map);
        result.Rows.Clear();

        if (kept.Count == 0)
        {
            var only = slices.Count > 0 ? CloneRow(slices[0].Row) : NewRow(0);
            only.SeatCount = 0;
            result.Rows.Add(only);
            return new RoomEdit(result, Array.Empty<string>());
        }

        for (int i = 0; i < kept.Count; i++)
        {
            var row = CloneRow(kept[i].Row);
            row.SeatCount = i == kept.Count - 1 ? 0 : kept[i].Ids.Count;
            if (IsDefaultName(row.Name)) row.Name = $"Row {i + 1}";
            result.Rows.Add(row);
        }
        return new RoomEdit(result, kept.SelectMany(s => s.Ids).ToList());
    }
}

# UI Redesign — Plan 1: Toolbar + Room View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the main window's topology canvas, Room strip and crowded toolbar with a room-first
layout: a compact toolbar with a `⋯` menu, a `RoomView` control (row lanes, drag nodes between rows,
scene painted inside every tile, context menus, inline gap editing) and a selection bar.

**Architecture:** All row logic is pure and unit-tested in `WaBiBaBuSy.Models/Topology`
(`SeatMapEditor` for edits, `RoomGrid` for geometry + hit testing). `RoomView` is one custom-drawn
Avalonia control that renders from `RoomGrid` and paints the scene with `ScenePainter` (extracted
from `ScenePreviewControl`). All edits go through `IRoomHost`, which `MainWindowViewModel`
implements; the seat-map JSON and topology formats and `SeatMapLayoutBuilder` do not change.

**Tech Stack:** .NET 9, Avalonia 12.1.2, CommunityToolkit.Mvvm 8.4.2, xUnit 2.9.3.

**Spec:** [`.docs/plans/2026-09-24-ui-redesign-design.md`](2026-09-24-ui-redesign-design.md) —
this plan covers rollout steps 1–2 (§2 toolbar/selection bar, §3 room, §6 `⋯` menu, §7
`SeatMapEditor` / `ScenePainter` / `RoomView`). Steps 3–5 (docked Scene editor, Playlist tab,
Settings sidebar) get their own plans after this one ships.

## Global Constraints

- .NET 9, Avalonia **12.1.2**; **no new NuGet packages**.
- C# 12+, file-scoped namespaces, nullable enabled, XML docs on public APIs, MVVM in the UI layer.
- `WaBiBaBuSy.Models` stays UI-free (no Avalonia types); `WaBiBaBuSy.Tests` references only `WaBiBaBuSy.Models`.
- `SeatMapLayoutBuilder` stays the single layout authority; `seatmap.json` / `topology.json` formats are unchanged.
- Do not touch player / movement / pattern / color math (Architectural Invariants in `CLAUDE.md`).
- Gap values are clamped to **0–500 cm**.
- After code changes run `./tools/graphify-update.ps1` (never bare `graphify update .`).
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.

## Out of scope for this plan (deliberate, with reason)

- **Docked Scene editor, Playlist tab, draft preview + "draft / live" badge and its ⏸ ⟲ 1× controls,
  "Use as scene targets"** — Plan 2 (spec steps 3–4). Until then the room shows only the **live**
  scene; `Scene…` still opens `CrossScreenConfigDialog` with its own preview.
- **`[+ row]` header button** — an empty row would be dropped immediately by `SeatMapEditor`'s
  normalization; the always-visible "+ new row" drop zone covers the same need.
- **Identify, Scale…, Remove from room** in the node menu — no backing feature exists yet
  (spec §3.3 marks Identify optional; Scale/Remove need a definition first).

## Review Focus

1. **The node list changes during a drag** (a client connects or drops while a tile is dragged) → the drag cancels, the refresh timer restarts, nothing moves. Owned by Task 6 (`OnClientsChanged` → `CancelGesture`), manual check in Task 6 Step 6.
2. **A new node joins after rows were edited** → it appears at the end of the last row (last row keeps `SeatCount = 0`). Test `Slice_NewNodeJoinsLastRow` in Task 1.
3. **A node leaves the topology** → rows shorten without a crash and `seatmap.json` is not rewritten by a refresh. Test `Slice_FewerNodesThanSeats_LeavesTrailingRowsEmpty` + `Layout_AfterNodeLeaves_DoesNotThrow` in Task 1; only user actions call `CommitSeatMap` (Task 3).
4. **Right-click on a node that is not selected** → it becomes the only selection first, so the menu acts on it, not on the old selection. Code in Task 6 `OnPointerPressed`, manual check in Task 7 Step 5.
5. **Gap input out of range or blank** → clamped to 0–500 cm, blank = 0. Test `NormalizeGapCm_Clamps` in Task 1; `(int)(nud.Value ?? 0)` in Task 7.

---

### Task 0: Commit the pending Multi Monitor fix

The working tree holds the verified fix from this session (`CrossScreenConfigDialog.axaml.cs` uses the
generated `PreviewControl` field; `ConfigureCrossScreen` logs failures).

- [ ] **Step 1: Commit only the two fix files**

```bash
git add WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs
git commit -m "fix: Multi Monitor dialog failed to open (FindControl during InitializeComponent)

The preview speed ComboBox raises SelectionChanged inside InitializeComponent,
before the name scope exists; FindControl threw and the dialog constructor
failed silently. Use the generated field and log dialog failures.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 1: `SeatMapEditor` — pure row edits

**Files:**
- Create: `WaBiBaBuSy.Models/Topology/SeatMapEditor.cs`
- Test: `WaBiBaBuSy.Tests/SeatMapEditorTests.cs`

**Interfaces:**
- Consumes: `SeatMap`, `SeatRow`, `RowOrientation`, `TraversalMode` (existing, `SeatMap.cs`).
- Produces:
  - `public sealed record RowSlice(SeatRow Row, List<string> Ids)`
  - `public sealed record RoomEdit(SeatMap Map, IReadOnlyList<string> Order)`
  - `public static class SeatMapEditor` with: `const int MaxGapCm = 500`, `int NormalizeGapCm(int)`,
    `bool IsRowReversed(SeatMap, int)`, `SeatMap Clone(SeatMap)`, `List<RowSlice> Slice(SeatMap, IReadOnlyList<string>)`,
    and edits returning `RoomEdit`: `MoveNode(map, order, id, targetRow, targetIndex)`, `SplitRowAt(map, order, id)`,
    `MergeRowIntoPrevious(map, order, row)`, `DeleteRow(map, order, row)`, `MoveRow(map, order, row, delta)`,
    `MakeRowFromSelection(map, order, ids)`, `SplitEvenly(map, order, rowCount)`,
    `SetRowOrientation(map, order, row, orientation)`, `RenameRow(map, order, row, name)`.
  - `targetIndex` in `MoveNode` is the insert position **in the row as currently displayed (chain order, before removing the moved node)**; `targetRow >= row count` appends a new row.

- [ ] **Step 1: Write the failing tests**

```csharp
// WaBiBaBuSy.Tests/SeatMapEditorTests.cs
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~SeatMapEditorTests`
Expected: build FAILS with `CS0103: The name 'SeatMapEditor' does not exist` (and `RoomEdit`).

- [ ] **Step 3: Implement `SeatMapEditor`**

```csharp
// WaBiBaBuSy.Models/Topology/SeatMapEditor.cs
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~SeatMapEditorTests`
Expected: all SeatMapEditorTests PASS. Then `dotnet test WaBiBaBuSy.Tests` — all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Topology/SeatMapEditor.cs WaBiBaBuSy.Tests/SeatMapEditorTests.cs
git commit -m "feat: SeatMapEditor pure room edits (move, split, merge, rows)

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `RoomGrid` — room view geometry and hit testing

**Files:**
- Create: `WaBiBaBuSy.Models/Topology/RoomGrid.cs`
- Test: `WaBiBaBuSy.Tests/RoomGridTests.cs`

**Interfaces:**
- Consumes: `SeatMapLayoutResult`, `NodeLayout` (existing), `SeatMapEditor.IsRowReversed` (Task 1).
- Produces:
  - `public readonly record struct Box(double X, double Y, double W, double H)` with `Right`, `Bottom`, `Contains(x, y)`.
  - `public sealed record TileBox(string Id, int Row, int IndexInRow, int ChainIndex, Box Tile, Box Scene)`
  - `public sealed record LaneBox(int Row, bool Reversed, int Count, Box Header, Box Body, Box FacingChip, Box MenuChip)`
  - `public sealed record GapBox(string HolderId, int Row, Box Hit)` — `HolderId` is the node whose `GapBeforeCm` (= `ClientNodeViewModel.PhysicalDistanceCm`) this gap edits.
  - `public readonly record struct DropTarget(int Row, int ChainIndex, double MarkerX, double MarkerTop, double MarkerHeight)` — feeds `SeatMapEditor.MoveNode(…, Row, ChainIndex)`; `Row == Lanes.Count` means "new row".
  - `public sealed class RoomGrid` with constants (`TileW = 184`, `TileH = 158`, `SceneW = 176`, `SceneH = 99`, …), `Lanes`, `Tiles`, `Gaps`, `NewRowZone`, `Width`, `Height`, `static RoomGrid Build(SeatMap map, SeatMapLayoutResult layout)`, `TileBox? HitTile(x, y)`, `GapBox? HitGap(x, y)`, `DropTarget ResolveDrop(x, y)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// WaBiBaBuSy.Tests/RoomGridTests.cs
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~RoomGridTests`
Expected: build FAILS with `CS0246: The type or namespace name 'RoomGrid' could not be found`.

- [ ] **Step 3: Implement `RoomGrid`**

```csharp
// WaBiBaBuSy.Models/Topology/RoomGrid.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace WaBiBaBuSy.Models.Topology;

/// <summary>Axis-aligned box in room-view pixels (half-open: right/bottom edges excluded).</summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
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
    public const double Margin = 12;
    public const double HeaderH = 26;
    public const double LanePad = 8;
    public const double LaneSpacing = 12;
    public const double TileW = 184;
    public const double TileH = 158;
    public const double SceneW = 176;
    public const double SceneH = 99;
    public const double SceneTop = 4;
    public const double TileGap = 24;
    public const double NewRowH = 40;
    public const double ChipH = 18;
    public const double MinLaneW = 420;
    private const double GapHandleH = 24;

    public IReadOnlyList<LaneBox> Lanes { get; }
    public IReadOnlyList<TileBox> Tiles { get; }
    public IReadOnlyList<GapBox> Gaps { get; }
    public Box NewRowZone { get; }
    public double Width { get; }
    public double Height { get; }

    private RoomGrid(List<LaneBox> lanes, List<TileBox> tiles, List<GapBox> gaps, Box newRow, double width, double height)
    {
        Lanes = lanes; Tiles = tiles; Gaps = gaps; NewRowZone = newRow; Width = width; Height = height;
    }

    /// <summary>Lay out lanes, tiles and gap handles for a seat-map layout.</summary>
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

    public TileBox? HitTile(double x, double y) => Tiles.FirstOrDefault(t => t.Tile.Contains(x, y));

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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~RoomGridTests`
Expected: all RoomGridTests PASS. (Check of the reversed-row case: `ResolveDrop(5, 300)` → slot 0, chain `3 − 0 = 3`, marker `20 − 12 = 8`, top `250 + 8 = 258`.)

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Topology/RoomGrid.cs WaBiBaBuSy.Tests/RoomGridTests.cs
git commit -m "feat: RoomGrid geometry and hit testing for the room view

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: View model room settings + Room ⚙ popover (replaces the Room strip)

**Files:**
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs:162-286` (room / seat map region)
- Modify: `WaBiBaBuSy.UI/Views/MainWindow.axaml:215-252` (Room strip)

**Interfaces:**
- Consumes: `SeatMapEditor.Clone`, `SeatMapEditor.SplitEvenly`, `RoomEdit` (Task 1).
- Produces (public on `MainWindowViewModel`, used by Tasks 4, 6, 7):
  - `IReadOnlyList<string> RoomOrder { get; }` — client ids in chain order.
  - `Task ApplyRoomEditAsync(RoomEdit edit)` — writes `Order` onto clients, saves the seat map, bumps `SeatMapVersion`, persists the order.
  - `string RoomSummary { get; }`, `int RoomRowGapCm`, `int RoomQuickRowCount`, `SplitRoomEvenlyCommand`.
  - Removed: `RoomRowCount`, `RoomSeatsPerRow`, `RoomRowsFacing`, `IsRoomMultiRow`, `SeatMapSummary`, `RebuildSeatMap`.

- [ ] **Step 1: Replace the room properties in the view model**

In `MainWindowViewModel.cs`, replace everything from the line
`[ObservableProperty] private int _roomRowCount = 1;` through the end of `RebuildSeatMap()` (the
closing brace before `// ── Live preview of the running scene (Tier 2.1)`) with the block below, but
**keep** the `_referencePixelsPerCm` field and the `ReferencePixelsPerCm` property exactly where
they are (move them to the top of this block):

```csharp
    /// <summary>0 = Ring, 1 = Snake, 2 = Parallel (ComboBox order).</summary>
    [ObservableProperty] private int _roomTraversalIndex = 1;
    [ObservableProperty] private int _roomTurnGapCm = 150;
    [ObservableProperty] private int _roomRowGapCm = 120;
    [ObservableProperty] private bool _roomPhysicalUnits;
    /// <summary>0 = Center, 1 = Top, 2 = Bottom (ComboBox order).</summary>
    [ObservableProperty] private int _roomVerticalAnchorIndex;
    /// <summary>Row count for the "Split evenly" quick setup in the Room ⚙ popover.</summary>
    [ObservableProperty] private int _roomQuickRowCount = 2;

    // (keep _referencePixelsPerCm + ReferencePixelsPerCm here, unchanged)

    partial void OnRoomTraversalIndexChanged(int value) => UpdateRoomSettings();
    partial void OnRoomTurnGapCmChanged(int value) => UpdateRoomSettings();
    partial void OnRoomRowGapCmChanged(int value) => UpdateRoomSettings();
    partial void OnRoomPhysicalUnitsChanged(bool value) => UpdateRoomSettings();
    partial void OnRoomVerticalAnchorIndexChanged(int value) => UpdateRoomSettings();

    /// <summary>Short room description for the Room ⚙ button, e.g. "Ring · 150 cm turn · cm".</summary>
    public string RoomSummary
    {
        get
        {
            var parts = new List<string> { _seatMap.Traversal.ToString() };
            if (_seatMap.IsMultiRow) parts.Add($"{_seatMap.Rows.Count} rows · {_seatMap.TurnGapCm} cm turn");
            if (_seatMap.CanvasMode == CanvasMode.Physical)
                parts.Add(ReferencePixelsPerCm > 0f ? "cm" : "cm (reference DPI unknown!)");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Client ids in chain (topology) order — the input of every room edit.</summary>
    public IReadOnlyList<string> RoomOrder => Clients.OrderBy(c => c.Order).Select(c => c.ClientId).Distinct().ToList();

    private void LoadSeatMap()
    {
        _seatMapLoading = true;
        try
        {
            _seatMap = SeatMapStore.Load();
            RoomTraversalIndex = _seatMap.Traversal switch
            {
                TraversalMode.Ring => 0,
                TraversalMode.Parallel => 2,
                _ => 1
            };
            RoomTurnGapCm = _seatMap.TurnGapCm;
            RoomRowGapCm = _seatMap.RowGapCm;
            RoomPhysicalUnits = _seatMap.CanvasMode == CanvasMode.Physical;
            RoomVerticalAnchorIndex = _seatMap.VerticalAnchor switch { VerticalAnchor.Top => 1, VerticalAnchor.Bottom => 2, _ => 0 };
            RoomQuickRowCount = Math.Max(2, _seatMap.Rows.Count);
        }
        finally
        {
            _seatMapLoading = false;
        }
        OnPropertyChanged(nameof(SeatMap));
        OnPropertyChanged(nameof(RoomSummary));
    }

    /// <summary>Room-wide settings changed in the Room ⚙ popover: keep the rows, update the rest.</summary>
    private void UpdateRoomSettings()
    {
        if (_seatMapLoading) return;
        var map = SeatMapEditor.Clone(_seatMap);
        map.Traversal = RoomTraversalIndex switch { 0 => TraversalMode.Ring, 2 => TraversalMode.Parallel, _ => TraversalMode.Snake };
        map.TurnGapCm = Math.Max(0, RoomTurnGapCm);
        map.RowGapCm = Math.Max(0, RoomRowGapCm);
        map.CanvasMode = RoomPhysicalUnits ? CanvasMode.Physical : CanvasMode.Pixels;
        map.VerticalAnchor = RoomVerticalAnchorIndex switch { 1 => VerticalAnchor.Top, 2 => VerticalAnchor.Bottom, _ => VerticalAnchor.Center };
        CommitSeatMap(map);
    }

    /// <summary>Single write path for the seat map. Only user actions call this — a topology refresh never does.</summary>
    private void CommitSeatMap(SeatMap map)
    {
        _seatMap = map;
        SeatMapStore.Save(map);
        SeatMapVersion++;
        OnPropertyChanged(nameof(SeatMap));
        OnPropertyChanged(nameof(RoomSummary));
    }

    /// <summary>Apply a room edit: new chain order onto the clients, then the seat map, then persist the order.</summary>
    public async Task ApplyRoomEditAsync(RoomEdit edit)
    {
        var byId = Clients.GroupBy(c => c.ClientId).ToDictionary(g => g.Key, g => g.First());
        for (int i = 0; i < edit.Order.Count; i++)
            if (byId.TryGetValue(edit.Order[i], out var client))
                client.Order = i;
        CommitSeatMap(edit.Map);
        await PersistClientOrderAsync();
    }

    [RelayCommand]
    private Task SplitRoomEvenly() => ApplyRoomEditAsync(SeatMapEditor.SplitEvenly(_seatMap, RoomOrder, RoomQuickRowCount));
```

- [ ] **Step 2: Replace the Room strip in `MainWindow.axaml`**

Replace the whole `<Border Grid.Row="1" Background="#252526" CornerRadius="4" Padding="10,6" Margin="0,0,0,8">…</Border>`
block (the "Room layout (seat map)" strip, including its comment) with:

```xml
                    <!-- Room-wide settings live in a popover; rows are edited directly in the room view. -->
                    <StackPanel Grid.Row="1" Orientation="Horizontal" Spacing="8" Margin="0,0,0,8">
                        <Button Padding="8,4" FontSize="11" ToolTip.Tip="Room-wide layout: path, gaps, units, alignment">
                            <TextBlock Text="{Binding RoomSummary, StringFormat='Room ⚙   {0}'}"/>
                            <Button.Flyout>
                                <Flyout Placement="BottomEdgeAlignedLeft">
                                    <Grid ColumnDefinitions="Auto,Auto" RowDefinitions="Auto,Auto,Auto,Auto,Auto,Auto,Auto"
                                          RowSpacing="8" ColumnSpacing="10" Margin="4">
                                        <TextBlock Grid.Row="0" Grid.Column="0" Text="Path" VerticalAlignment="Center" FontSize="12"/>
                                        <ComboBox Grid.Row="0" Grid.Column="1" SelectedIndex="{Binding RoomTraversalIndex}" Width="150" FontSize="12">
                                            <ComboBoxItem Content="Ring (loop)"/>
                                            <ComboBoxItem Content="Snake"/>
                                            <ComboBoxItem Content="Parallel"/>
                                        </ComboBox>
                                        <TextBlock Grid.Row="1" Grid.Column="0" Text="Turn gap (cm)" VerticalAlignment="Center" FontSize="12"
                                                   ToolTip.Tip="Distance the sprite travels between the end of one row and the start of the next"/>
                                        <NumericUpDown Grid.Row="1" Grid.Column="1" Value="{Binding RoomTurnGapCm}" Minimum="0" Maximum="3000" Increment="10" Width="150"/>
                                        <TextBlock Grid.Row="2" Grid.Column="0" Text="Row gap (cm)" VerticalAlignment="Center" FontSize="12"
                                                   ToolTip.Tip="Vertical distance between rows (Parallel path only)"/>
                                        <NumericUpDown Grid.Row="2" Grid.Column="1" Value="{Binding RoomRowGapCm}" Minimum="0" Maximum="3000" Increment="10" Width="150"/>
                                        <CheckBox Grid.Row="3" Grid.Column="0" Grid.ColumnSpan="2" IsChecked="{Binding RoomPhysicalUnits}"
                                                  Content="Physical units (cm)" FontSize="12"
                                                  ToolTip.Tip="Canvas in reference pixels of this server's primary monitor: a sprite is the same number of centimeters and moves at the same cm/s on every monitor."/>
                                        <TextBlock Grid.Row="4" Grid.Column="0" Text="Align" VerticalAlignment="Center" FontSize="12"/>
                                        <ComboBox Grid.Row="4" Grid.Column="1" SelectedIndex="{Binding RoomVerticalAnchorIndex}" Width="150" FontSize="12"
                                                  ToolTip.Tip="Where a monitor shorter than the canvas sits">
                                            <ComboBoxItem Content="Center"/>
                                            <ComboBoxItem Content="Top"/>
                                            <ComboBoxItem Content="Bottom"/>
                                        </ComboBox>
                                        <Border Grid.Row="5" Grid.ColumnSpan="2" Height="1" Background="#3E3E42"/>
                                        <StackPanel Grid.Row="6" Grid.ColumnSpan="2" Orientation="Horizontal" Spacing="6">
                                            <TextBlock Text="Split evenly into" VerticalAlignment="Center" FontSize="12"/>
                                            <NumericUpDown Value="{Binding RoomQuickRowCount}" Minimum="1" Maximum="8" Increment="1" Width="100"/>
                                            <TextBlock Text="rows" VerticalAlignment="Center" FontSize="12"/>
                                            <Button Content="Split" Command="{Binding SplitRoomEvenlyCommand}" FontSize="12"/>
                                        </StackPanel>
                                    </Grid>
                                </Flyout>
                            </Button.Flyout>
                        </Button>
                    </StackPanel>
```

- [ ] **Step 3: Build and fix remaining references**

Run: `dotnet build WaBiBaBuSy.UI -v q -nologo`
Expected: 0 errors. If an error names `RoomRowCount`, `RoomSeatsPerRow`, `RoomRowsFacing`,
`IsRoomMultiRow` or `SeatMapSummary`, it is a leftover of the removed strip — delete that XAML
attribute/element (the existing canvas code in `MainWindow.axaml.cs` uses only `SeatMap`,
`SeatMapVersion` and `BuildSeatLayout`, which remain).

- [ ] **Step 4: Run the app and check the popover**

Run: `dotnet run --project WaBiBaBuSy.UI`, tray icon → Open Control Panel.
Expected: the Room strip is gone; "Room ⚙ Snake" (or the saved traversal) button opens the popover;
"Split evenly into 2 rows → Split" shows two lanes in the existing topology canvas; changing Path to
Ring updates the button text; closing and reopening the app keeps both (seatmap.json).

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs WaBiBaBuSy.UI/Views/MainWindow.axaml
git commit -m "feat: Room settings popover and room-edit plumbing in the view model

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Toolbar, `⋯` menu, selection bar, per-node Clear / Resync

**Files:**
- Modify: `WaBiBaBuSy.Core/Services/WallpaperSyncCoordinator.cs:548-562` (add `ResyncClientAsync` after `ResyncAllAsync`)
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (selection properties, commands, `ClearNodesAsync`, `ResyncNodesAsync`)
- Modify: `WaBiBaBuSy.UI/Views/MainWindow.axaml:25-162` (top bar), left panel grid rows, add selection bar

**Interfaces:**
- Consumes: `ApplyRoomEditAsync`, `RoomOrder` (Task 3), `SeatMapEditor.MakeRowFromSelection` (Task 1).
- Produces (public on `MainWindowViewModel`, used by Task 6/7 `IRoomHost`):
  - `Task ClearNodesAsync(IReadOnlyCollection<string> nodeIds)`
  - `Task ResyncNodesAsync(IReadOnlyCollection<string> nodeIds)`
  - `int SelectedNodeCount`, `bool HasNodeSelection`, `bool IsSingleNodeSelection`, `bool IsMultiNodeSelection`, `string SelectionSummary`
  - Commands: `ClearSelectedNodesCommand`, `ResyncSelectedNodesCommand`, `MakeRowFromSelectionCommand`, `DeselectAllCommand`, `OpenSettingsCommand`, `OpenLogFolderCommand`
  - On `WallpaperSyncCoordinator`: `Task<bool> ResyncClientAsync(string clientId)`

- [ ] **Step 1: Add `ResyncClientAsync` to the coordinator**

Insert directly after `ResyncAllAsync()` in `WallpaperSyncCoordinator.cs`:

```csharp
    /// <summary>
    /// Re-send the active cross-screen command to one client. False when that client has no active
    /// command or the send failed.
    /// </summary>
    public async Task<bool> ResyncClientAsync(string clientId)
    {
        if (_syncService == null || !_activeCrossScreenCommands.TryGetValue(clientId, out var command))
            return false;
        try
        {
            return await _syncService.SendCommandToClientAsync(clientId, command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resync failed for {ClientId}", clientId);
            return false;
        }
    }
```

- [ ] **Step 2: Add selection state and node actions to the view model**

Replace `NotifyClientSelectionCommands()` in `MainWindowViewModel.cs` with:

```csharp
    /// <summary>
    /// Notify CanExecute and the selection bar when client selection changes.
    /// Called when a client's IsSelected property changes and after every topology update.
    /// </summary>
    private void NotifyClientSelectionCommands()
    {
        ApplyWallpaperToSelectedCommand.NotifyCanExecuteChanged();
        ApplyWallpaperViaDirect2DCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedNodeCount));
        OnPropertyChanged(nameof(HasNodeSelection));
        OnPropertyChanged(nameof(IsSingleNodeSelection));
        OnPropertyChanged(nameof(IsMultiNodeSelection));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    // ── Selection bar ────────────────────────────────────────────────────────
    public int SelectedNodeCount => Clients.Count(c => c.IsSelected);
    public bool HasNodeSelection => SelectedNodeCount > 0;
    public bool IsSingleNodeSelection => SelectedNodeCount == 1;
    public bool IsMultiNodeSelection => SelectedNodeCount > 1;

    /// <summary>"SEEPC - Monitor 1" for one node, "3 selected" for several.</summary>
    public string SelectionSummary => SelectedNodeCount == 1
        ? Clients.First(c => c.IsSelected).DisplayName
        : $"{SelectedNodeCount} selected";

    private IReadOnlyCollection<string> SelectedNodeIds => Clients.Where(c => c.IsSelected).Select(c => c.ClientId).ToList();

    [RelayCommand]
    private Task ClearSelectedNodes() => ClearNodesAsync(SelectedNodeIds);

    [RelayCommand]
    private Task ResyncSelectedNodes() => ResyncNodesAsync(SelectedNodeIds);

    [RelayCommand]
    private Task MakeRowFromSelection() => ApplyRoomEditAsync(SeatMapEditor.MakeRowFromSelection(_seatMap, RoomOrder, SelectedNodeIds));

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var c in Clients) c.IsSelected = false;
        SelectedClient = null;
    }

    [RelayCommand]
    private void OpenSettings() => new Views.SettingsWindow().Show();

    [RelayCommand]
    private void OpenLogFolder() => Process.Start(new ProcessStartInfo
    {
        FileName = WaBiBaBuSy.Common.PathHelper.GetLogsPath(),
        UseShellExecute = true
    });

    /// <summary>
    /// Stop what the given nodes show: local monitors dispose their D2D player / LibVLC renderer,
    /// remote nodes get a cross-screen Stop. A running playlist puts them back on its next item —
    /// use the toolbar Stop to end a show.
    /// </summary>
    public async Task ClearNodesAsync(IReadOnlyCollection<string> nodeIds)
    {
        foreach (var id in nodeIds)
        {
            if (IsLocalMonitor(id))
            {
                int monitorIndex = GetMonitorIndex(id);
                if (_d2dCompositionServices.TryRemove(monitorIndex, out var d2d))
                {
                    d2d.GlobalLapCompleted -= OnSequentialLapCompleted;
                    try { await d2d.StopAsync(); } catch (Exception ex) { Debug.WriteLine($"[ClearNodes] Stop {id}: {ex.Message}"); }
                    try { d2d.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[ClearNodes] Dispose {id}: {ex.Message}"); }
                }
                if (_localWallpaperRenderers.TryRemove(monitorIndex, out var renderer) && renderer is IDisposable disposable)
                {
                    try { disposable.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[ClearNodes] Renderer {id}: {ex.Message}"); }
                }
            }
            else if (_service.SyncCoordinator != null && !string.IsNullOrEmpty(_crossScreenContentId))
            {
                try { await _service.SyncCoordinator.StopCrossScreenOnClientAsync(id, _crossScreenContentId); }
                catch (Exception ex) { Debug.WriteLine($"[ClearNodes] Remote stop {id}: {ex.Message}"); }
            }

            var client = Clients.FirstOrDefault(c => c.ClientId == id);
            if (client != null)
            {
                client.IsAnimating = false;
                client.IsCurrentAnimationTarget = false;
                client.ActiveAnimationName = null;
                client.ThumbnailImage = null;
                client.CurrentWallpaper = null;
            }
        }
        OnPropertyChanged(nameof(HasActiveRenderer));
        ClearAllWallpapersCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Re-send the running animation to the given remote nodes (local monitors are in-process and need no resync).</summary>
    public async Task ResyncNodesAsync(IReadOnlyCollection<string> nodeIds)
    {
        var coordinator = _service.SyncCoordinator;
        if (coordinator == null) return;
        int sent = 0;
        foreach (var id in nodeIds.Where(id => !IsLocalMonitor(id)))
            if (await coordinator.ResyncClientAsync(id)) sent++;
        Debug.WriteLine($"[Resync] re-sent the active animation to {sent} of {nodeIds.Count} selected node(s)");
    }
```

In `UpdateClientList`, directly after `OnPropertyChanged(nameof(ShowHealthSummary));` add:

```csharp
            NotifyClientSelectionCommands();
```

- [ ] **Step 3: Replace the top bar in `MainWindow.axaml`**

Replace the whole `<!-- Top Bar with Server Controls -->` `<Border Grid.Row="0" …>…</Border>` block with:

```xml
        <!-- Top bar: role + status (left) · show controls (center) · ⋯ everything rare (right) -->
        <Border Grid.Row="0" Background="#2D2D30" Padding="15,8">
            <Grid ColumnDefinitions="Auto,*,Auto">
                <StackPanel Grid.Column="0" Orientation="Horizontal" Spacing="6" VerticalAlignment="Center">
                    <Button Command="{Binding ToggleServerModeCommand}"
                            Padding="8,4" FontSize="11" VerticalAlignment="Center"
                            IsEnabled="{Binding !IsClientConnected}">
                        <StackPanel Orientation="Horizontal" Spacing="4">
                            <TextBlock Text="&#9654; Start Server" IsVisible="{Binding !IsServerMode}"/>
                            <TextBlock Text="&#9632; Stop Server" IsVisible="{Binding IsServerMode}"/>
                        </StackPanel>
                    </Button>
                    <Border Background="#00AA44" CornerRadius="3" Padding="8,4" IsVisible="{Binding IsServerMode}">
                        <TextBlock Text="Running" Foreground="White" FontSize="11" FontWeight="SemiBold"/>
                    </Border>
                    <Border Background="#0078D4" CornerRadius="3" Padding="6,4" IsVisible="{Binding IsServerMode}">
                        <TextBlock Text="{Binding ServerPort, StringFormat='Port: {0}'}" Foreground="White" FontSize="11"/>
                    </Border>
                    <Border Background="#555555" CornerRadius="3" Padding="6,4" IsVisible="{Binding IsServerMode}">
                        <TextBlock Text="{Binding ConnectedClientCount, StringFormat='Clients: {0}'}" Foreground="White" FontSize="11"/>
                    </Border>
                    <Border Background="#00AA44" CornerRadius="3" Padding="6,4" IsVisible="{Binding IsClientConnected}">
                        <TextBlock Text="Connected" Foreground="White" FontSize="11" FontWeight="SemiBold"/>
                    </Border>
                    <Border Background="#AA4400" CornerRadius="3" Padding="6,4" IsVisible="{Binding IsConnecting}">
                        <TextBlock Text="Connecting..." Foreground="White" FontSize="11" FontWeight="SemiBold"/>
                    </Border>
                </StackPanel>

                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6"
                            HorizontalAlignment="Center" VerticalAlignment="Center">
                    <Button Content="Scene…" Command="{Binding ConfigureCrossScreenCommand}"
                            Height="28" Padding="10,4" FontSize="11"
                            ToolTip.Tip="Configure the multi-monitor animation"/>
                    <Button Content="&#9654; Start" Command="{Binding StartCrossScreenCommand}"
                            IsVisible="{Binding HasAnimationConfig}" Height="28" Padding="10,4" FontSize="11"/>
                    <Button Content="&#9632; Stop" Command="{Binding StopCrossScreenCommand}"
                            IsVisible="{Binding IsCrossScreenRunning}" Height="28" Padding="10,4" FontSize="11"/>
                    <Border Width="1" Height="20" Background="#3E3E42" VerticalAlignment="Center"/>
                    <Button Content="Playlist…" Command="{Binding OpenPlaylistCommand}"
                            Height="28" Padding="10,4" FontSize="11"/>
                    <TextBlock Text="{Binding PlaylistNextLabel}" Foreground="#FFC800" FontSize="11"
                               VerticalAlignment="Center" IsVisible="{Binding HasPlaylistNext}"/>
                    <Button Content="Clear all" Command="{Binding ClearAllWallpapersCommand}"
                            IsVisible="{Binding HasActiveRenderer}" Height="28" Padding="10,4" FontSize="11"
                            ToolTip.Tip="Stop every animation and clear all wallpapers"/>
                </StackPanel>

                <Button Grid.Column="2" Content="&#8943;" FontSize="16" Height="28" Padding="10,0"
                        VerticalAlignment="Center"
                        ToolTip.Tip="Connect to a server, settings, logs, developer tools">
                    <Button.Flyout>
                        <Flyout Placement="BottomEdgeAlignedRight">
                            <StackPanel Spacing="8" Width="360" Margin="4">
                                <StackPanel Spacing="4" IsVisible="{Binding !IsServerMode}">
                                    <TextBlock Text="Connect to a server" FontWeight="SemiBold" FontSize="12"/>
                                    <StackPanel Orientation="Horizontal" Spacing="4">
                                        <TextBox Text="{Binding ConnectServerAddress}" PlaceholderText="Server IP"
                                                 Width="130" FontSize="11" IsEnabled="{Binding !IsClientConnected}"/>
                                        <TextBox Text="{Binding ConnectServerPort}" PlaceholderText="Port"
                                                 Width="60" FontSize="11" TextAlignment="Center" IsEnabled="{Binding !IsClientConnected}"/>
                                        <Button Content="Connect" Command="{Binding ConnectToServerCommand}" FontSize="11"
                                                IsEnabled="{Binding !IsConnecting}" IsVisible="{Binding !IsClientConnected}"/>
                                        <Button Content="Find…" Command="{Binding FindServersCommand}" FontSize="11"
                                                IsEnabled="{Binding !IsConnecting}" IsVisible="{Binding !IsClientConnected}"
                                                ToolTip.Tip="Browse servers discovered on the local network (mDNS)"/>
                                        <Button Content="Disconnect" Command="{Binding DisconnectFromServerCommand}" FontSize="11"
                                                IsVisible="{Binding IsClientConnected}"/>
                                    </StackPanel>
                                    <Border Height="1" Background="#3E3E42" Margin="0,4,0,0"/>
                                </StackPanel>
                                <Button Content="Settings…" Command="{Binding OpenSettingsCommand}" HorizontalAlignment="Stretch"/>
                                <Button Content="Open log folder" Command="{Binding OpenLogFolderCommand}" HorizontalAlignment="Stretch"/>
                                <Expander Header="Developer tools" HorizontalAlignment="Stretch">
                                    <StackPanel Spacing="8">
                                        <StackPanel Orientation="Horizontal" Spacing="6">
                                            <TextBlock Text="Composition" VerticalAlignment="Center" FontSize="11" Foreground="#AAAAAA"/>
                                            <ComboBox SelectedIndex="{Binding SelectedFitModeIndex}" Width="90" FontSize="11">
                                                <ComboBoxItem Content="Stretch"/>
                                                <ComboBoxItem Content="Center"/>
                                                <ComboBoxItem Content="Fit"/>
                                                <ComboBoxItem Content="Fill"/>
                                            </ComboBox>
                                            <Button Content="Apply D2D" Command="{Binding ApplyWallpaperViaDirect2DCommand}" FontSize="11"/>
                                            <Button Content="Apply via LibVLC" Command="{Binding ApplyWallpaperToSelectedCommand}" FontSize="11"/>
                                        </StackPanel>
                                        <StackPanel Orientation="Horizontal" Spacing="6">
                                            <TextBlock Text="Background" VerticalAlignment="Center" FontSize="11" Foreground="#AAAAAA"/>
                                            <Border Width="18" Height="18" CornerRadius="9" BorderBrush="#666666" BorderThickness="1" VerticalAlignment="Center">
                                                <Border.Background>
                                                    <SolidColorBrush Color="{Binding D2dBackgroundColor, Converter={StaticResource HexToColor}}"/>
                                                </Border.Background>
                                            </Border>
                                            <TextBox Text="{Binding D2dBackgroundColor}" Width="80" FontSize="11" TextAlignment="Center"/>
                                            <CheckBox IsChecked="{Binding IsAutoDetectBackground}" Content="Auto" FontSize="11"/>
                                        </StackPanel>
                                        <WrapPanel Orientation="Horizontal">
                                            <TextBlock Text="Debug overlay" VerticalAlignment="Center" FontSize="11" Foreground="#AAAAAA" Margin="0,0,8,0"/>
                                            <CheckBox Content="On" IsChecked="{Binding DebugOverlayEnabled}" FontSize="11" Margin="0,0,8,0" ToolTip.Tip="Enable debug overlay (also F11)"/>
                                            <CheckBox Content="Path" IsChecked="{Binding DebugShowPath}" FontSize="11" Margin="0,0,8,0" ToolTip.Tip="Show A* path waypoints"/>
                                            <CheckBox Content="Rects" IsChecked="{Binding DebugShowIconRects}" FontSize="11" Margin="0,0,8,0" ToolTip.Tip="Show icon zone rectangles"/>
                                            <CheckBox Content="Zones" IsChecked="{Binding DebugShowZoneBands}" FontSize="11" Margin="0,0,8,0" ToolTip.Tip="Show zone band outlines"/>
                                            <CheckBox Content="Info" IsChecked="{Binding DebugShowInfoPanel}" FontSize="11" ToolTip.Tip="Show info panel (FPS, position, etc.)"/>
                                        </WrapPanel>
                                    </StackPanel>
                                </Expander>
                            </StackPanel>
                        </Flyout>
                    </Button.Flyout>
                </Button>
            </Grid>
        </Border>
```

- [ ] **Step 4: Add the selection bar and re-number the left panel rows**

In the left panel (`<Border Grid.Column="0" Background="#1E1E1E" Padding="15">`):

1. Change its grid to `<Grid RowDefinitions="Auto,Auto,Auto,*,Auto,Auto,Auto">`.
2. Rename the header text `Network Topology` to `Room`.
3. Delete the "Animation Controls in Topology Panel" `<StackPanel Grid.Row="3" …>` with the
   *Stop Animation* / *Clear Wallpaper* buttons (both live in the toolbar now).
4. Change the "Selected Client Details" border to `Grid.Row="5"` and the "Remote Client Log Viewer" border to `Grid.Row="6"`.
5. Insert the selection bar right after the topology border (`Name="TopologyBorder"`):

```xml
                    <!-- Selection bar: only while nodes are selected -->
                    <Border Grid.Row="4" Background="#252526" CornerRadius="5" Padding="10,6" Margin="0,8,0,0"
                            IsVisible="{Binding HasNodeSelection}">
                        <StackPanel Orientation="Horizontal" Spacing="6">
                            <TextBlock Text="{Binding SelectionSummary}" FontWeight="SemiBold" FontSize="12"
                                       VerticalAlignment="Center" Margin="0,0,6,0"/>
                            <Button Content="Clear" Command="{Binding ClearSelectedNodesCommand}" Padding="8,3" FontSize="11"
                                    ToolTip.Tip="Stop what the selected nodes show"/>
                            <Button Content="Resync" Command="{Binding ResyncSelectedNodesCommand}" Padding="8,3" FontSize="11"
                                    IsVisible="{Binding IsServerMode}"
                                    ToolTip.Tip="Re-send the running animation to the selected nodes"/>
                            <Button Content="Logs" Command="{Binding FetchClientLogsCommand}" Padding="8,3" FontSize="11"
                                    IsVisible="{Binding IsSingleNodeSelection}"/>
                            <Button Content="Make row from selection" Command="{Binding MakeRowFromSelectionCommand}" Padding="8,3" FontSize="11"
                                    IsVisible="{Binding IsMultiNodeSelection}"/>
                            <Button Content="Deselect" Command="{Binding DeselectAllCommand}" Padding="8,3" FontSize="11"/>
                        </StackPanel>
                    </Border>
```

- [ ] **Step 5: Build, run tests, run the app**

Run: `dotnet build -v q -nologo` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → all PASS.
Run the app, open the control panel. Expected:
- One toolbar row: server button + badges left; `Scene…`, `Playlist…` centered; `⋯` right.
- `⋯` opens: (client mode) connect row, Settings…, Open log folder, collapsed "Developer tools" holding composition, background color, debug checkboxes.
- Clicking a node shows the selection bar with its name; Ctrl+click a second node → "2 selected" + "Make row from selection"; clicking it creates a new lane.
- `Scene…` opens the Animation Configuration dialog (regression check for Task 0).

- [ ] **Step 6: Commit**

```bash
git add WaBiBaBuSy.Core/Services/WallpaperSyncCoordinator.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs WaBiBaBuSy.UI/Views/MainWindow.axaml
git commit -m "feat: compact toolbar with more menu, selection bar, per-node clear and resync

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Extract `ScenePainter` + `SceneClock` from `ScenePreviewControl`

Pure refactor: the dialog preview and the main window's "Active Animation" preview must look and
behave exactly as before.

**Files:**
- Create: `WaBiBaBuSy.UI/Controls/SceneClock.cs`
- Create: `WaBiBaBuSy.UI/Controls/ScenePainter.cs`
- Modify (rewrite): `WaBiBaBuSy.UI/Controls/ScenePreviewControl.cs`

**Interfaces:**
- Produces (used by `RoomView`, Task 6):
  - `SceneClock`: `long SharedStartUtcMs { get; set; }`, `bool IsLive`, `double Speed { get; set; }`, `bool IsPaused { get; set; }`, `void Restart()`, `long ElapsedMs()`.
  - `ScenePainter`: `string? SpriteImagePath { get; set; }`, `static CrossScreenConfig? Resolve(CrossScreenConfig? scene, SeatMapLayoutResult layout)`, `static bool HasSprite(CrossScreenConfig? scene)`, `void PaintBackground(DrawingContext ctx, Rect rect, double s, CrossScreenConfig? scene)`, `bool PaintSprite(DrawingContext ctx, Rect rect, double s, NodeLayout node, CrossScreenConfig scene, long elapsedMs)` (pushes its own clip; returns true when the sprite's center is on this node), `(int W, int H) SpriteSizeFor(CrossScreenConfig? scene, NodeLayout node)`, `static string BuildInfoLine(…)`, `static Color ParseHex(string?)`.
  - `rect` is the node's full screen area in view pixels, `s` = view pixels per canvas pixel.

- [ ] **Step 1: Create `SceneClock`**

```csharp
// WaBiBaBuSy.UI/Controls/SceneClock.cs
using System;
using System.Diagnostics;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>
/// Clock for scene previews. <b>Design</b> mode: a local stopwatch × <see cref="Speed"/>, pausable,
/// restarted on config changes. <b>Live</b> mode (<see cref="SharedStartUtcMs"/> &gt; 0): elapsed =
/// now − the shared start the players use, so the preview shows where the sprite <i>is</i>.
/// </summary>
public sealed class SceneClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private double _accumulatedMs;
    private double _speed = 1.0;
    private bool _paused;

    /// <summary>Shared UTC start (ms). &gt; 0 switches to the live clock.</summary>
    public long SharedStartUtcMs { get; set; }

    public bool IsLive => SharedStartUtcMs > 0;

    /// <summary>Design-clock speed multiplier (min 0.01). Time run so far is kept at the old speed.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            _accumulatedMs += RunningMs();
            _stopwatch.Restart();
            if (_paused) _stopwatch.Reset();
            _speed = Math.Max(0.01, value);
        }
    }

    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (value == _paused) return;
            if (value)
            {
                _accumulatedMs += RunningMs();
                _stopwatch.Reset();
            }
            else
            {
                _stopwatch.Start();
            }
            _paused = value;
        }
    }

    /// <summary>Restart the design clock at t = 0.</summary>
    public void Restart()
    {
        _accumulatedMs = 0;
        _stopwatch.Restart();
        if (_paused) _stopwatch.Reset();
    }

    public long ElapsedMs() => IsLive
        ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - SharedStartUtcMs
        : (long)(_accumulatedMs + RunningMs());

    private double RunningMs() => _paused ? 0 : _stopwatch.Elapsed.TotalMilliseconds * _speed;
}
```

- [ ] **Step 2: Create `ScenePainter`** (code moved from `ScenePreviewControl`, per-node)

```csharp
// WaBiBaBuSy.UI/Controls/ScenePainter.cs
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>
/// Paints a cross-screen scene onto one node's rectangle with the very same pure functions the D2D
/// players run (<see cref="MovementCalculator"/>, <see cref="PatternLayout"/>, <see cref="ColorGrader"/>,
/// <see cref="NodeMapping"/>, <see cref="SyncTiming"/>). Geometry + color only: GIF frame timing,
/// IconZone path-following and video are not simulated. Shared by <see cref="ScenePreviewControl"/>
/// and <see cref="RoomView"/>; holds a one-entry sprite bitmap cache.
/// </summary>
public sealed class ScenePainter
{
    private const int MaxPreviewCellsPerNode = 400;
    private static readonly IBrush MonoCellBrush = new SolidColorBrush(Color.Parse("#BBBBBB"));

    private string? _spritePath;
    private Bitmap? _bitmap;
    private bool _loaded;

    /// <summary>Image drawn for the sprite (the file for images/GIFs, a thumbnail for videos).</summary>
    public string? SpriteImagePath
    {
        get => _spritePath;
        set
        {
            if (value == _spritePath) return;
            _spritePath = value;
            _bitmap = null;
            _loaded = false;
        }
    }

    private Bitmap? SpriteBitmap
    {
        get
        {
            if (_loaded) return _bitmap;
            _loaded = true;
            try
            {
                if (!string.IsNullOrEmpty(_spritePath) && File.Exists(_spritePath))
                    _bitmap = new Bitmap(_spritePath);
            }
            catch
            {
                _bitmap = null;
            }
            return _bitmap;
        }
    }

    public static bool HasSprite(CrossScreenConfig? scene) => scene != null && !string.IsNullOrEmpty(scene.Animation.AnimationPath);

    /// <summary>Resolve cm → canvas px exactly like the apply path does, so preview and wall agree.</summary>
    public static CrossScreenConfig? Resolve(CrossScreenConfig? scene, SeatMapLayoutResult layout)
    {
        if (scene == null) return null;
        var (mv, an) = PhysicalUnits.ResolveForCanvas(scene, layout.RefPixelsPerCm);
        return new CrossScreenConfig
        {
            Background = scene.Background, Movement = mv, Animation = an,
            DistributionMode = scene.DistributionMode, SelectedMonitorIds = scene.SelectedMonitorIds,
            AnimationSpeedPxPerSecond = scene.AnimationSpeedPxPerSecond
        };
    }

    /// <summary>Background color (and ThreeZone bands) of the scene over the node rect.</summary>
    public void PaintBackground(DrawingContext ctx, Rect rect, double s, CrossScreenConfig? scene)
    {
        var bg = scene?.Background;
        var color = ParseHex(bg == null ? "#000000" :
            bg.Mode == BackgroundMode.IconZone ? bg.IconCorridorColorHex :
            bg.Mode == BackgroundMode.ThreeZone ? bg.CorridorColorHex : bg.ColorHex);
        ctx.FillRectangle(new SolidColorBrush(color), rect);
        if (bg?.Mode != BackgroundMode.ThreeZone) return;

        double top = rect.Top + bg.CorridorTopPx * s;
        double h = bg.CorridorHeightPx * s;
        ctx.FillRectangle(new SolidColorBrush(ParseHex(bg.TopZoneColorHex)),
            new Rect(rect.Left, rect.Top, rect.Width, Math.Max(0, top - rect.Top)));
        ctx.FillRectangle(new SolidColorBrush(ParseHex(bg.BottomZoneColorHex)),
            new Rect(rect.Left, Math.Min(rect.Bottom, top + h), rect.Width, Math.Max(0, rect.Bottom - (top + h))));
    }

    /// <summary>Sprite size after FitMode — mirrors the player's CalculateAnimationLayout for the given node.</summary>
    public (int W, int H) SpriteSizeFor(CrossScreenConfig? scene, NodeLayout node)
    {
        if (scene == null) return (0, 0);
        int nativeW = 400, nativeH = 300;
        var bmp = SpriteBitmap;
        if (bmp != null && bmp.PixelSize.Width > 0 && bmp.PixelSize.Height > 0)
        {
            nativeW = bmp.PixelSize.Width; nativeH = bmp.PixelSize.Height;
        }
        else if (scene.Animation.TargetHeight > 0)
        {
            nativeH = scene.Animation.TargetHeight; nativeW = (int)(nativeH * 16 / 9.0);
        }
        switch (scene.Animation.FitMode)
        {
            case ContentFitMode.Fit:
            {
                double k = Math.Min((double)node.Width / nativeW, (double)node.Height / nativeH);
                return ((int)(nativeW * k), (int)(nativeH * k));
            }
            case ContentFitMode.Fill:
            {
                double k = Math.Max((double)node.Width / nativeW, (double)node.Height / nativeH);
                return ((int)(nativeW * k), (int)(nativeH * k));
            }
            case ContentFitMode.Stretch:
                return (node.Width, node.Height);
            case ContentFitMode.TargetHeight:
            {
                int th = Math.Max(1, scene.Animation.TargetHeight);
                return (Math.Max(1, (int)Math.Round(nativeW * (double)th / nativeH)), th);
            }
            default:
                return (nativeW, nativeH);
        }
    }

    /// <summary>Draws the scene's sprite/pattern on one node (clipped to <paramref name="rect"/>); true when the sprite's center is on this node.</summary>
    public bool PaintSprite(DrawingContext ctx, Rect rect, double s, NodeLayout node, CrossScreenConfig scene, long elapsedMs)
    {
        using var clip = ctx.PushClip(rect);
        bool perMonitor = scene.DistributionMode == AnimationDistributionMode.Simultaneous;
        long e = SyncTiming.ApplyNodePhase(elapsedMs, node.Order, scene.Movement.NodePhaseDelayMs, perMonitor);
        if (!SyncTiming.ShouldDrawAnimation(e)) return false;

        var eff = perMonitor
            ? new NodeLayout { Id = node.Id, OffsetX = 0, OffsetY = 0, Width = node.Width, Height = node.Height, CanvasWidth = node.Width, CanvasHeight = node.Height, Order = node.Order }
            : node;
        var (animW, animH) = SpriteSizeFor(scene, node);
        if (animW <= 0 || animH <= 0) return false;

        var (vx, vy) = MovementCalculator.Calculate(scene.Movement, e, animW, animH, eff.CanvasWidth, eff.CanvasHeight,
            tileAlignStepX: 0f, canvasWraps: eff.Wraps);
        float ly = NodeMapping.ToLocalY(vy, eff);

        var bg = scene.Background;
        if (bg.Mode == BackgroundMode.ThreeZone && bg.CorridorHeightPx > 0)
        {
            float minY = bg.CorridorTopPx, maxY = bg.CorridorTopPx + bg.CorridorHeightPx - animH;
            if (maxY < minY) maxY = minY;
            ly = Math.Clamp(ly, minY, maxY);
        }

        var grading = scene.Animation.ColorGrading;
        bool traveling = grading != null && grading.Mode is ColorGradingMode.TravelingRainbow or ColorGradingMode.TravelingList or ColorGradingMode.TravelingRandom;
        Color? tint = null;
        if (grading != null && grading.Mode != ColorGradingMode.None && !traveling)
        {
            var (r, g, b) = ColorGrader.ComputeCurrentColor(grading, e);
            tint = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        if (scene.Animation.Pattern != null)
        {
            var cells = PatternLayout.Compute(scene.Animation.Pattern, vx, ly, animW, animH,
                eff.CanvasWidth, eff.CanvasHeight, eff.OffsetX, eff.Width, eff.Height, 1);
            int stride = Math.Max(1, cells.Count / MaxPreviewCellsPerNode);
            double cw = Math.Max(2, animW * s), ch = Math.Max(2, animH * s);
            for (int i = 0; i < cells.Count; i += stride)
            {
                var c = cells[i];
                float cx = NodeMapping.MirrorLocalX(c.ScreenX, animW, eff);
                if (!NodeMapping.IsVisible(cx, animW, eff) || c.ScreenY + animH < 0 || c.ScreenY > eff.Height) continue;
                IBrush brush;
                if (traveling && grading != null)
                {
                    var rgb = ColorGrader.IsCellColored(grading, c.LogicalI, c.LogicalJ) ? ColorGrader.ComputeCellColor(grading, c.LogicalI, c.LogicalJ) : null;
                    brush = rgb.HasValue ? new SolidColorBrush(Color.FromRgb((byte)(rgb.Value.R * 255), (byte)(rgb.Value.G * 255), (byte)(rgb.Value.B * 255))) : MonoCellBrush;
                }
                else brush = tint.HasValue ? new SolidColorBrush(tint.Value) : MonoCellBrush;
                ctx.FillRectangle(brush, new Rect(rect.Left + cx * s, rect.Top + c.ScreenY * s, Math.Max(1, cw - 1), Math.Max(1, ch - 1)), 1);
            }
            return false;
        }

        bool hit = false;
        Span<float> copies = stackalloc float[2];
        int n = NodeMapping.WrapCopies(vx, animW, eff, copies);
        var bmp = SpriteBitmap;
        for (int i = 0; i < n; i++)
        {
            float lx = NodeMapping.ToLocalX(copies[i], animW, eff);
            if (!NodeMapping.IsVisible(lx, animW, eff)) continue;
            var dest = new Rect(rect.Left + lx * s, rect.Top + ly * s, Math.Max(1, animW * s), Math.Max(1, animH * s));
            if (bmp != null)
            {
                ctx.DrawImage(bmp, new Rect(bmp.Size), dest);
                if (tint.HasValue)
                    ctx.FillRectangle(new SolidColorBrush(tint.Value, 0.45), dest);
            }
            else
            {
                ctx.FillRectangle(new SolidColorBrush(tint ?? Color.Parse("#E0E0E0")), dest, 2);
            }
            float center = lx + animW / 2f;
            if (center >= 0 && center < eff.Width) hit = true;
        }
        return hit;
    }

    /// <summary>One-line legend: time, lap, canvas size, sprite owner.</summary>
    public static string BuildInfoLine(CrossScreenConfig? scene, SeatMapLayoutResult layout, long elapsed, int animW, string? hotId, IReadOnlyDictionary<string, string>? labels)
    {
        var parts = new List<string> { $"t = {Math.Max(0, elapsed) / 1000.0:0.0} s" };
        if (scene != null)
        {
            int lap = PlaylistScheduler.ComputeLapMs(scene.Movement, layout.CanvasWidth, animW);
            if (lap > 0) parts.Add($"lap ≈ {lap / 1000.0:0} s");
            parts.Add(layout.IsPhysical
                ? $"{PhysicalUnits.PxToCm(layout.CanvasWidth, layout.RefPixelsPerCm) / 100f:0.0} m canvas (physical)" + (layout.Wraps ? " · ring" : "")
                : $"{layout.CanvasWidth:N0} px canvas" + (layout.Wraps ? " · ring" : ""));
            if (layout.IsPhysical && animW > 0)
                parts.Add($"sprite {PhysicalUnits.PxToCm(animW, layout.RefPixelsPerCm):0.0} cm wide");
            if (scene.Background.Mode == BackgroundMode.IconZone) parts.Add("IconZone path not previewed");
            if (hotId != null)
            {
                string name = labels != null && labels.TryGetValue(hotId, out var l) ? l : hotId;
                var node = layout.Get(hotId);
                parts.Add($"sprite on: {name}" + (node != null ? $" (#{node.Order + 1})" : ""));
            }
        }
        return string.Join("   ·   ", parts);
    }

    public static Color ParseHex(string? hex)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? Colors.Black : Color.Parse(hex); }
        catch { return Colors.Black; }
    }
}
```

- [ ] **Step 3: Rewrite `ScenePreviewControl` on top of the painter and clock**

Replace the entire file content with:

```csharp
// WaBiBaBuSy.UI/Controls/ScenePreviewControl.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>
/// Live preview of a cross-screen scene over the room's seat map: every node as a rectangle in its
/// lane, the scene painted by <see cref="ScenePainter"/> on a <see cref="SceneClock"/> (design clock by
/// default, live clock when <see cref="SharedStartUtcMs"/> &gt; 0).
/// </summary>
public class ScenePreviewControl : Control
{
    public static readonly StyledProperty<SeatMapLayoutResult?> LayoutProperty =
        AvaloniaProperty.Register<ScenePreviewControl, SeatMapLayoutResult?>(nameof(Layout));

    public static readonly StyledProperty<CrossScreenConfig?> SceneProperty =
        AvaloniaProperty.Register<ScenePreviewControl, CrossScreenConfig?>(nameof(Scene));

    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> LabelsProperty =
        AvaloniaProperty.Register<ScenePreviewControl, IReadOnlyDictionary<string, string>?>(nameof(Labels));

    public static readonly StyledProperty<string?> SpriteImagePathProperty =
        AvaloniaProperty.Register<ScenePreviewControl, string?>(nameof(SpriteImagePath));

    /// <summary>Shared UTC start (ms). &gt; 0 switches to the live clock.</summary>
    public static readonly StyledProperty<long> SharedStartUtcMsProperty =
        AvaloniaProperty.Register<ScenePreviewControl, long>(nameof(SharedStartUtcMs));

    public static readonly StyledProperty<double> ClockSpeedProperty =
        AvaloniaProperty.Register<ScenePreviewControl, double>(nameof(ClockSpeed), 1.0);

    public static readonly StyledProperty<bool> IsPausedProperty =
        AvaloniaProperty.Register<ScenePreviewControl, bool>(nameof(IsPaused));

    public SeatMapLayoutResult? Layout { get => GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public CrossScreenConfig? Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public IReadOnlyDictionary<string, string>? Labels { get => GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public string? SpriteImagePath { get => GetValue(SpriteImagePathProperty); set => SetValue(SpriteImagePathProperty, value); }
    public long SharedStartUtcMs { get => GetValue(SharedStartUtcMsProperty); set => SetValue(SharedStartUtcMsProperty, value); }
    public double ClockSpeed { get => GetValue(ClockSpeedProperty); set => SetValue(ClockSpeedProperty, value); }
    public bool IsPaused { get => GetValue(IsPausedProperty); set => SetValue(IsPausedProperty, value); }

    private readonly SceneClock _clock = new();
    private readonly ScenePainter _painter = new();
    private DispatcherTimer? _timer;

    private const double Pad = 10;
    private const double RowLabelH = 14;
    private const double LegendH = 16;

    private static readonly IPen NodePen = new Pen(new SolidColorBrush(Color.Parse("#3A3A3A")), 1);
    private static readonly IPen NodePenHot = new Pen(new SolidColorBrush(Color.Parse("#00CC66")), 1.5);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush SeamBrush = new SolidColorBrush(Color.Parse("#555555"));
    private static readonly Typeface Font = Typeface.Default;

    static ScenePreviewControl()
    {
        AffectsRender<ScenePreviewControl>(LayoutProperty, SceneProperty, LabelsProperty, SpriteImagePathProperty, SharedStartUtcMsProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ScenePreviewControl>(true);
    }

    /// <summary>Restart the design clock at t = 0 (call after every config change).</summary>
    public void RestartClock()
    {
        _clock.Restart();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClockSpeedProperty) _clock.Speed = ClockSpeed;
        else if (change.Property == IsPausedProperty) _clock.IsPaused = IsPaused;
        else if (change.Property == SpriteImagePathProperty) _painter.SpriteImagePath = SpriteImagePath;
        else if (change.Property == SharedStartUtcMsProperty) _clock.SharedStartUtcMs = SharedStartUtcMs;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            if (IsEffectivelyVisible && Bounds.Width > 0) InvalidateVisual();
        });
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private sealed record LaneNode(NodeLayout Node, Rect Rect);

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var bounds = Bounds;
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#141414")), new Rect(bounds.Size));

        var layout = Layout;
        if (layout == null || layout.Nodes.Count == 0)
        {
            DrawText(ctx, "No nodes selected — the preview shows the sprite travelling across the selected monitors.", new Point(Pad, Pad), LabelBrush, 11);
            return;
        }

        var rows = layout.Nodes.GroupBy(n => n.RowIndex).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        var rowMinX = rows.Select(r => r.Min(n => n.OffsetX)).ToList();
        var rowW = rows.Select((r, i) => r.Max(n => n.OffsetX + n.Width) - rowMinX[i]).ToList();
        var rowMinY = rows.Select(r => r.Min(n => n.OffsetY)).ToList();
        var rowH = rows.Select((r, i) => r.Max(n => n.OffsetY + n.Height) - rowMinY[i]).ToList();

        double totalW = rowW.Max();
        double rowGapVirtual = rows.Count > 1 ? rowH.Max() * 0.35 : 0;
        double totalH = rowH.Sum() + rowGapVirtual * (rows.Count - 1);
        double availW = Math.Max(1, bounds.Width - 2 * Pad);
        double availH = Math.Max(1, bounds.Height - 2 * Pad - LegendH - rows.Count * RowLabelH);
        double s = Math.Min(availW / totalW, availH / totalH);
        if (s <= 0 || double.IsNaN(s) || double.IsInfinity(s)) return;

        var lanes = new List<LaneNode>();
        double y = Pad;
        for (int r = 0; r < rows.Count; r++)
        {
            y += RowLabelH;
            foreach (var n in rows[r])
            {
                var rect = new Rect(
                    Pad + (n.OffsetX - rowMinX[r]) * s,
                    y + (n.OffsetY - rowMinY[r]) * s,
                    Math.Max(1, n.Width * s),
                    Math.Max(1, n.Height * s));
                lanes.Add(new LaneNode(n, rect));
            }
            DrawText(ctx, RowCaption(r, rows.Count, layout), new Point(Pad, y - RowLabelH + 1), LabelBrush, 10);
            y += rowH[r] * s + rowGapVirtual * s;
        }

        var scene = ScenePainter.Resolve(Scene, layout);
        long elapsed = _clock.ElapsedMs();
        string? hotNodeId = null;
        var (animW, _) = _painter.SpriteSizeFor(scene, lanes[0].Node);

        foreach (var lane in lanes)
        {
            _painter.PaintBackground(ctx, lane.Rect, s, scene);
            ctx.DrawRectangle(null, NodePen, lane.Rect);
        }

        if (ScenePainter.HasSprite(scene))
        {
            foreach (var lane in lanes)
                if (_painter.PaintSprite(ctx, lane.Rect, s, lane.Node, scene!, elapsed))
                    hotNodeId = lane.Node.Id;
        }

        if (layout.Wraps && lanes.Count > 1)
        {
            var first = lanes.First(l => l.Node.Order == lanes.Min(x => x.Node.Order));
            var last = lanes.First(l => l.Node.Order == lanes.Max(x => x.Node.Order));
            var seamPen = new Pen(SeamBrush, 1, new DashStyle(new double[] { 2, 2 }, 0));
            double fx = first.Node.Mirrored ? first.Rect.Right : first.Rect.Left;
            double lx = last.Node.Mirrored ? last.Rect.Left : last.Rect.Right;
            ctx.DrawLine(seamPen, new Point(fx, first.Rect.Top), new Point(fx, first.Rect.Bottom));
            ctx.DrawLine(seamPen, new Point(lx, last.Rect.Top), new Point(lx, last.Rect.Bottom));
        }

        var labels = Labels;
        foreach (var lane in lanes)
        {
            bool hot = lane.Node.Id == hotNodeId;
            if (hot) ctx.DrawRectangle(null, NodePenHot, lane.Rect);
            string name = labels != null && labels.TryGetValue(lane.Node.Id, out var l) ? l : lane.Node.Id;
            string caption = $"#{lane.Node.Order + 1} {Shorten(name, Math.Max(3, (int)(lane.Rect.Width / 6.5)))}";
            if (lane.Rect.Width >= 28)
                DrawText(ctx, caption, new Point(lane.Rect.Left + 2, lane.Rect.Bottom - 12), hot ? InfoBrush : LabelBrush, 9);
        }

        DrawText(ctx, ScenePainter.BuildInfoLine(scene, layout, elapsed, animW, hotNodeId, labels), new Point(Pad, bounds.Height - LegendH - 1), InfoBrush, 10);
    }

    private static string RowCaption(int r, int rows, SeatMapLayoutResult layout)
    {
        if (rows == 1) return "";
        string dir = layout.Traversal == TraversalMode.Parallel ? "" : (r % 2 == 0 ? "  →" : "  ←");
        return $"Row {r + 1}{dir}";
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..Math.Max(1, max - 1)] + "…";

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush, double size)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush);
        ctx.DrawText(ft, at);
    }
}
```

- [ ] **Step 4: Build and compare visually**

Run: `dotnet build WaBiBaBuSy.UI -v q -nologo` → 0 errors.
Run the app → `Scene…`: the dialog preview animates as before (Pause/Restart/1×-4×-16× work). Start
the animation: the main window's "Active Animation" preview follows the live clock as before.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.UI/Controls/SceneClock.cs WaBiBaBuSy.UI/Controls/ScenePainter.cs WaBiBaBuSy.UI/Controls/ScenePreviewControl.cs
git commit -m "refactor: extract ScenePainter and SceneClock from ScenePreviewControl

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: `RoomView` — lanes, tiles with live scene, selection, drag between rows

**Files:**
- Create: `WaBiBaBuSy.UI/Controls/IRoomHost.cs`
- Create: `WaBiBaBuSy.UI/Controls/RoomView.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (implement `IRoomHost`)
- Modify (rewrite): `WaBiBaBuSy.UI/Views/MainWindow.axaml.cs` (topology canvas code removed)
- Modify: `WaBiBaBuSy.UI/Views/MainWindow.axaml` (canvas → `RoomView`, refresh banner removed)

**Interfaces:**
- Consumes: `RoomGrid`, `TileBox`, `LaneBox`, `DropTarget` (Task 2); `SeatMapEditor` (Task 1); `ApplyRoomEditAsync`, `RoomOrder` (Task 3); `ClearNodesAsync`, `ResyncNodesAsync` (Task 4); `ScenePainter`, `SceneClock` (Task 5).
- Produces:
  - `IRoomHost` (below), implemented by `MainWindowViewModel`.
  - `RoomView` styled properties `Host`, `Scene`, `SharedStartUtcMs`, `SpriteImagePath`.
  - Partial hooks for Task 7: `partial void HandleSecondaryPress(TileBox tile);` and `partial void HandleChromePress(Point p, ref bool handled);`.

- [ ] **Step 1: Create `IRoomHost`**

```csharp
// WaBiBaBuSy.UI/Controls/IRoomHost.cs
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>
/// Everything <see cref="RoomView"/> reads and changes. Implemented by MainWindowViewModel so the
/// control stays free of networking and persistence. Row indices are seat-map row indices; node ids
/// are client ids.
/// </summary>
public interface IRoomHost : INotifyPropertyChanged
{
    ObservableCollection<ClientNodeViewModel> Clients { get; }
    SeatMap SeatMap { get; }

    /// <summary>Bumped on every seat-map change; the view re-lays out when it changes.</summary>
    int SeatMapVersion { get; }

    SeatMapLayoutResult BuildSeatLayout(IEnumerable<ClientNodeViewModel> clients);

    /// <summary>Short warning shown as ⚠ on the tile (e.g. mixed refresh rates), or null.</summary>
    string? NodeWarning(string nodeId);

    /// <summary>True when remote logs can be requested for this node (server mode, remote node).</summary>
    bool CanFetchLogs(string nodeId);

    void SelectOnly(string nodeId);
    void ToggleSelection(string nodeId);
    void AddToSelection(string nodeId);
    void SetRubberBandSelection(IReadOnlyCollection<string> nodeIds, bool additive);
    void ClearSelection();

    /// <summary>Pause topology refreshes while a tile is dragged.</summary>
    void BeginNodeDrag();
    void EndNodeDrag();

    Task MoveNodeAsync(string nodeId, int row, int indexInRow);
    Task SplitRowAtAsync(string nodeId);
    Task MergeRowIntoPreviousAsync(int row);
    Task DeleteRowAsync(int row);
    Task MoveRowAsync(int row, int delta);
    Task ToggleRowFacingAsync(int row);
    Task RenameRowAsync(int row, string name);
    Task SetGapCmAsync(string nodeId, int cm);

    Task ClearNodesAsync(IReadOnlyCollection<string> nodeIds);
    Task ResyncNodesAsync(IReadOnlyCollection<string> nodeIds);
    Task ShowNodeLogsAsync(string nodeId);
}
```

- [ ] **Step 2: Implement `IRoomHost` on the view model**

Change the class declaration to `public partial class MainWindowViewModel : ViewModelBase, IRoomHost`
(add `using WaBiBaBuSy.UI.Controls;`) and add this region after `ResyncNodesAsync` (Task 4):

```csharp
    // ── IRoomHost (RoomView) ─────────────────────────────────────────────────
    private ClientNodeViewModel? FindClient(string id) => Clients.FirstOrDefault(c => c.ClientId == id);

    public void SelectOnly(string nodeId)
    {
        foreach (var c in Clients) c.IsSelected = c.ClientId == nodeId;
        SelectedClient = FindClient(nodeId);
    }

    public void ToggleSelection(string nodeId)
    {
        var c = FindClient(nodeId);
        if (c != null) c.IsSelected = !c.IsSelected;
    }

    public void AddToSelection(string nodeId)
    {
        var c = FindClient(nodeId);
        if (c != null) c.IsSelected = true;
    }

    public void SetRubberBandSelection(IReadOnlyCollection<string> nodeIds, bool additive)
    {
        var set = new HashSet<string>(nodeIds);
        foreach (var c in Clients)
        {
            if (set.Contains(c.ClientId)) c.IsSelected = true;
            else if (!additive) c.IsSelected = false;
        }
    }

    public void ClearSelection() => DeselectAll();

    public void BeginNodeDrag() => StopRefreshTimer();
    public void EndNodeDrag() => StartRefreshTimer();

    public Task MoveNodeAsync(string nodeId, int row, int indexInRow)
        => ApplyRoomEditAsync(SeatMapEditor.MoveNode(_seatMap, RoomOrder, nodeId, row, indexInRow));
    public Task SplitRowAtAsync(string nodeId)
        => ApplyRoomEditAsync(SeatMapEditor.SplitRowAt(_seatMap, RoomOrder, nodeId));
    public Task MergeRowIntoPreviousAsync(int row)
        => ApplyRoomEditAsync(SeatMapEditor.MergeRowIntoPrevious(_seatMap, RoomOrder, row));
    public Task DeleteRowAsync(int row)
        => ApplyRoomEditAsync(SeatMapEditor.DeleteRow(_seatMap, RoomOrder, row));
    public Task MoveRowAsync(int row, int delta)
        => ApplyRoomEditAsync(SeatMapEditor.MoveRow(_seatMap, RoomOrder, row, delta));
    public Task RenameRowAsync(int row, string name)
        => ApplyRoomEditAsync(SeatMapEditor.RenameRow(_seatMap, RoomOrder, row, name));

    public Task ToggleRowFacingAsync(int row)
    {
        var current = row < _seatMap.Rows.Count ? _seatMap.Rows[row].Orientation : RowOrientation.Facing;
        var next = current == RowOrientation.Facing ? RowOrientation.SameSide : RowOrientation.Facing;
        return ApplyRoomEditAsync(SeatMapEditor.SetRowOrientation(_seatMap, RoomOrder, row, next));
    }

    public async Task SetGapCmAsync(string nodeId, int cm)
    {
        var c = FindClient(nodeId);
        if (c == null) return;
        c.PhysicalDistanceCm = SeatMapEditor.NormalizeGapCm(cm);
        await UpdateClientDistance(c);
        SeatMapVersion++;
    }

    public Task ShowNodeLogsAsync(string nodeId)
    {
        SelectOnly(nodeId);
        return FetchClientLogs();
    }

    public bool CanFetchLogs(string nodeId) => IsServerMode && !IsLocalMonitor(nodeId);

    public string? NodeWarning(string nodeId)
    {
        if (!IsLocalMonitor(nodeId)) return null;
        var local = Clients.Where(c => IsLocalMonitor(c.ClientId) && c.MonitorRefreshHz > 0).ToList();
        if (local.Select(c => c.MonitorRefreshHz).Distinct().Count() < 2) return null;
        var me = local.FirstOrDefault(c => c.ClientId == nodeId);
        int max = local.Max(c => c.MonitorRefreshHz);
        return me != null && me.MonitorRefreshHz < max
            ? $"Runs at {me.MonitorRefreshHz} Hz while another local monitor runs at {max} Hz — this panel may tear. Match refresh rates in Windows Display settings."
            : null;
    }
```

- [ ] **Step 3: Create `RoomView`**

```csharp
// WaBiBaBuSy.UI/Controls/RoomView.cs
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Models.Networking;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>
/// The room: one lane per seat-map row, one tile per node, the scene painted inside every tile with
/// the players' own math (<see cref="ScenePainter"/>). Geometry and hit testing come from
/// <see cref="RoomGrid"/>; every change goes through <see cref="IRoomHost"/>.
/// Click selects, Ctrl+click toggles, Shift+click adds, dragging empty space draws a selection
/// rectangle, dragging a tile moves it within or between rows (or onto "+ new row").
/// </summary>
public partial class RoomView : Control
{
    public static readonly StyledProperty<IRoomHost?> HostProperty =
        AvaloniaProperty.Register<RoomView, IRoomHost?>(nameof(Host));
    public static readonly StyledProperty<CrossScreenConfig?> SceneProperty =
        AvaloniaProperty.Register<RoomView, CrossScreenConfig?>(nameof(Scene));
    public static readonly StyledProperty<long> SharedStartUtcMsProperty =
        AvaloniaProperty.Register<RoomView, long>(nameof(SharedStartUtcMs));
    public static readonly StyledProperty<string?> SpriteImagePathProperty =
        AvaloniaProperty.Register<RoomView, string?>(nameof(SpriteImagePath));

    public IRoomHost? Host { get => GetValue(HostProperty); set => SetValue(HostProperty, value); }
    /// <summary>Scene painted in the tiles (the running scene for now; the editor draft in Plan 2).</summary>
    public CrossScreenConfig? Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    /// <summary>&gt; 0 = live clock (players' shared start).</summary>
    public long SharedStartUtcMs { get => GetValue(SharedStartUtcMsProperty); set => SetValue(SharedStartUtcMsProperty, value); }
    public string? SpriteImagePath { get => GetValue(SpriteImagePathProperty); set => SetValue(SpriteImagePathProperty, value); }

    private const double DragThreshold = 5;

    private static readonly IBrush ViewBackground = new SolidColorBrush(Color.Parse("#1A1A1A"));
    private static readonly IBrush LaneFill = new SolidColorBrush(Color.Parse("#202023"));
    private static readonly IPen LanePen = new Pen(new SolidColorBrush(Color.Parse("#333338")), 1);
    private static readonly IBrush SceneBg = new SolidColorBrush(Color.Parse("#141414"));
    private static readonly IBrush Caption = new SolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush Faint = new SolidColorBrush(Color.Parse("#666666"));
    private static readonly IBrush ChipFill = new SolidColorBrush(Color.Parse("#2D2D30"));
    private static readonly IPen ChipPen = new Pen(new SolidColorBrush(Color.Parse("#4A4A50")), 1);
    private static readonly IBrush BadgeBrush = new SolidColorBrush(Color.Parse("#0078D4"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FFD080"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#0078D4"));
    private static readonly IBrush AccentFaint = new SolidColorBrush(Color.Parse("#0078D4"), 0.2);
    private static readonly IPen DashedPen = new Pen(new SolidColorBrush(Color.Parse("#4A4A50")), 1, new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IPen DashedHotPen = new Pen(new SolidColorBrush(Color.Parse("#0078D4")), 2, new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#00CC66"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF4444"));
    private static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#FFC800"));

    private readonly ScenePainter _painter = new();
    private readonly SceneClock _clock = new();
    private DispatcherTimer? _timer;
    private SeatMapLayoutResult _layout = new();
    private RoomGrid _grid = RoomGrid.Build(SeatMap.SingleRow(), new SeatMapLayoutResult());
    private IRoomHost? _host;
    private System.Collections.ObjectModel.ObservableCollection<ClientNodeViewModel>? _clients;
    private readonly List<ClientNodeViewModel> _watched = new();

    private enum Gesture { None, PendingTile, DraggingTile, RubberBand }
    private Gesture _gesture;
    private Point _pressPoint;
    private Point _pointer;
    private string? _pressedId;
    private KeyModifiers _pressModifiers;
    private DropTarget? _drop;
    private string? _hoverId;

    static RoomView()
    {
        AffectsRender<RoomView>(SceneProperty, SharedStartUtcMsProperty, SpriteImagePathProperty);
        FocusableProperty.OverrideDefaultValue<RoomView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<RoomView>(true);
    }

    // ── host wiring ──────────────────────────────────────────────────────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HostProperty) AttachHost(Host);
        else if (change.Property == SpriteImagePathProperty) _painter.SpriteImagePath = SpriteImagePath;
        else if (change.Property == SharedStartUtcMsProperty) _clock.SharedStartUtcMs = SharedStartUtcMs;
        else if (change.Property == SceneProperty && !_clock.IsLive) _clock.Restart();
    }

    private void AttachHost(IRoomHost? host)
    {
        if (_host != null) _host.PropertyChanged -= OnHostPropertyChanged;
        if (_clients != null) _clients.CollectionChanged -= OnClientsChanged;
        UnwatchClients();

        _host = host;
        _clients = host?.Clients;
        if (_host != null) _host.PropertyChanged += OnHostPropertyChanged;
        if (_clients != null) _clients.CollectionChanged += OnClientsChanged;
        WatchClients();
        Relayout();
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IRoomHost.SeatMapVersion)) OnUi(Relayout);
        else if (e.PropertyName == nameof(IRoomHost.Clients)) OnUi(() => AttachHost(_host));
    }

    private void OnClientsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnUi(() =>
    {
        CancelGesture();   // a node joined or left mid-drag: never apply a stale move
        UnwatchClients();
        WatchClients();
        Relayout();
    });

    private void WatchClients()
    {
        if (_clients == null) return;
        foreach (var c in _clients)
        {
            c.PropertyChanged += OnClientPropertyChanged;
            _watched.Add(c);
        }
    }

    private void UnwatchClients()
    {
        foreach (var c in _watched) c.PropertyChanged -= OnClientPropertyChanged;
        _watched.Clear();
    }

    private void OnClientPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnUi(() =>
    {
        if (e.PropertyName is nameof(ClientNodeViewModel.Order) or nameof(ClientNodeViewModel.MonitorWidth)
            or nameof(ClientNodeViewModel.MonitorHeight) or nameof(ClientNodeViewModel.PhysicalDistanceCm)
            or nameof(ClientNodeViewModel.PixelsPerCm))
        {
            if (_gesture == Gesture.None) Relayout();
        }
        else InvalidateVisual();
    });

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void Relayout()
    {
        var host = _host;
        _layout = host == null || host.Clients.Count == 0 ? new SeatMapLayoutResult() : host.BuildSeatLayout(host.Clients);
        _grid = RoomGrid.Build(host?.SeatMap ?? SeatMap.SingleRow(), _layout);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(_grid.Width, _grid.Height);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            if (IsEffectivelyVisible && (ScenePainter.HasSprite(Scene) || _gesture != Gesture.None)) InvalidateVisual();
        });
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        CancelGesture();
        base.OnDetachedFromVisualTree(e);
    }

    // ── rendering ────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        ctx.FillRectangle(ViewBackground, new Rect(Bounds.Size));
        var host = _host;
        if (host == null) return;

        if (_grid.Tiles.Count == 0)
            DrawText(ctx, "No nodes yet — start the server or connect clients.", new Point(RoomGrid.Margin, RoomGrid.Margin + 4), Dim, 12);

        var scene = ScenePainter.Resolve(Scene, _layout);
        long elapsed = _clock.ElapsedMs();
        var byId = host.Clients.GroupBy(c => c.ClientId).ToDictionary(g => g.Key, g => g.First());

        foreach (var lane in _grid.Lanes) DrawLane(ctx, lane, host.SeatMap);
        foreach (var tile in _grid.Tiles) DrawTile(ctx, tile, byId.GetValueOrDefault(tile.Id), scene, elapsed);
        foreach (var gap in _grid.Gaps) DrawGap(ctx, gap, byId.GetValueOrDefault(gap.HolderId));
        DrawNewRowZone(ctx);

        if (_gesture == Gesture.DraggingTile)
        {
            if (_drop is { } d && d.Row < _grid.Lanes.Count)
                ctx.FillRectangle(Accent, new Rect(d.MarkerX - 1.5, d.MarkerTop, 3, d.MarkerHeight));
            var ghost = new Rect(_pointer.X - RoomGrid.TileW / 2, _pointer.Y - 30, RoomGrid.TileW, RoomGrid.TileH);
            ctx.DrawRectangle(AccentFaint, new Pen(Accent, 2), ghost, 8, 8);
        }
        else if (_gesture == Gesture.RubberBand)
        {
            ctx.DrawRectangle(AccentFaint, new Pen(Accent, 1.5), BandRect());
        }
    }

    private void DrawLane(DrawingContext ctx, LaneBox lane, SeatMap map)
    {
        ctx.DrawRectangle(LaneFill, LanePen, ToRect(lane.Body), 6, 6);
        var def = lane.Row < map.Rows.Count ? map.Rows[lane.Row] : null;
        string name = def?.Name ?? $"Row {lane.Row + 1}";
        string arrow = map.Traversal == TraversalMode.Parallel ? "" : lane.Reversed ? "   ←" : "   →";
        string ring = map.Traversal == TraversalMode.Ring && lane.Row == 0 && _grid.Lanes.Count > 1 ? "   · ring" : "";
        DrawText(ctx, name + arrow + ring, new Point(lane.Header.X + 4, lane.Header.Y + 5), Caption, 12, bold: true);
        if (lane.Row > 0)
            DrawChip(ctx, lane.FacingChip, def?.Orientation == RowOrientation.Facing ? "↕ facing" : "⇉ same side");
        DrawChip(ctx, lane.MenuChip, "⋯");
    }

    private static void DrawChip(DrawingContext ctx, Box box, string text)
    {
        ctx.DrawRectangle(ChipFill, ChipPen, ToRect(box), 4, 4);
        var ft = Format(text, Caption, 10, bold: false);
        ctx.DrawText(ft, new Point(box.X + (box.W - ft.Width) / 2, box.Y + (box.H - ft.Height) / 2));
    }

    private void DrawTile(DrawingContext ctx, TileBox t, ClientNodeViewModel? c, CrossScreenConfig? scene, long elapsed)
    {
        bool dragged = _gesture == Gesture.DraggingTile && t.Id == _pressedId;
        using var opacity = ctx.PushOpacity(dragged ? 0.35 : 1.0);

        var (fill, pen) = TileStyle(c, t.Id == _hoverId);
        ctx.DrawRectangle(fill, pen, ToRect(t.Tile), 8, 8);

        var sceneBox = ToRect(t.Scene);
        ctx.FillRectangle(SceneBg, sceneBox, 4);
        var node = _layout.Get(t.Id);
        if (node != null && ScenePainter.HasSprite(scene))
        {
            double s = Math.Min(sceneBox.Width / node.Width, sceneBox.Height / node.Height);
            var screen = new Rect(sceneBox.X + (sceneBox.Width - node.Width * s) / 2,
                                  sceneBox.Y + (sceneBox.Height - node.Height * s) / 2,
                                  node.Width * s, node.Height * s);
            _painter.PaintBackground(ctx, screen, s, scene);
            _painter.PaintSprite(ctx, screen, s, node, scene!, elapsed);
        }
        else if (c?.ThumbnailImage is { } thumb)
        {
            using var clip = ctx.PushClip(sceneBox);
            double k = Math.Max(sceneBox.Width / thumb.Size.Width, sceneBox.Height / thumb.Size.Height);
            var dest = new Rect(sceneBox.Center.X - thumb.Size.Width * k / 2, sceneBox.Center.Y - thumb.Size.Height * k / 2,
                                thumb.Size.Width * k, thumb.Size.Height * k);
            ctx.DrawImage(thumb, new Rect(thumb.Size), dest);
        }
        else
        {
            var ft = Format("idle", Faint, 10, bold: false);
            ctx.DrawText(ft, new Point(sceneBox.Center.X - ft.Width / 2, sceneBox.Center.Y - ft.Height / 2));
        }

        var badge = new Rect(sceneBox.X + 4, sceneBox.Y + 4, 28, 15);
        ctx.FillRectangle(BadgeBrush, badge, 3);
        DrawText(ctx, $"#{c?.Order ?? t.ChainIndex}", new Point(badge.X + 4, badge.Y + 1), Brushes.White, 9, bold: true);

        if (_host?.NodeWarning(t.Id) != null)
            DrawText(ctx, "⚠", new Point(sceneBox.Right - 16, sceneBox.Y + 1), WarnBrush, 12);

        double x = t.Tile.X + 8, w = t.Tile.W - 16, y = sceneBox.Bottom + 5;
        DrawText(ctx, c?.DisplayName ?? t.Id, new Point(x, y), Brushes.White, 12, bold: true, maxWidth: w);
        y += 16;
        string res = c == null || c.MonitorWidth <= 0 ? c?.IpAddress ?? "" :
            c.MonitorRefreshHz > 0 ? $"{c.MonitorWidth}×{c.MonitorHeight} @ {c.MonitorRefreshHz} Hz" : $"{c.MonitorWidth}×{c.MonitorHeight}";
        DrawText(ctx, res, new Point(x, y), Dim, 10, maxWidth: w - 14);
        ctx.DrawEllipse(c?.IsConnected == true ? Green : Red, null, new Point(t.Tile.Right - 12, y + 7), 4, 4);
        y += 14;
        var (status, statusBrush) = StatusLine(c);
        if (status.Length > 0) DrawText(ctx, status, new Point(x, y), statusBrush, 9, maxWidth: w);
    }

    /// <summary>Drift label first, then prefetch progress, then the running animation name (same wording as the old topology).</summary>
    private static (string Text, IBrush Brush) StatusLine(ClientNodeViewModel? c)
    {
        if (c == null) return ("", Dim);
        switch (c.DriftState)
        {
            case DriftState.Stale: return ("sync: —", Dim);
            case DriftState.Ok: return ($"±{Math.Abs(c.DriftMs):F0}ms", Green);
            case DriftState.Warn: return ($"±{Math.Abs(c.DriftMs):F0}ms", Yellow);
            case DriftState.None: break;
            default: return ($"±{Math.Abs(c.DriftMs):F0}ms", Red);
        }
        if (c.PrefetchTotal > 0)
            return c.IsPrefetchComplete ? ("✓ cached", Green) : ($"⬇ {c.PrefetchReady}/{c.PrefetchTotal}", Yellow);
        if (c.IsAnimating && !string.IsNullOrEmpty(c.ActiveAnimationName)) return (c.ActiveAnimationName!, Green);
        return ("", Dim);
    }

    private static (IBrush Fill, IPen Pen) TileStyle(ClientNodeViewModel? c, bool hover)
    {
        if (c?.IsCurrentAnimationTarget == true) return (Solid("#4E4A2E"), new Pen(Solid("#FFD700"), 3));
        if (c?.IsAnimating == true) return (Solid("#2E4A3E"), new Pen(Solid("#00AA44"), 3));
        if (c?.IsSelected == true) return (Solid("#3E4A5E"), new Pen(Solid("#0078D4"), 3));
        if (c?.GroupColor is Color gc) return (new SolidColorBrush(Color.FromArgb(30, gc.R, gc.G, gc.B)), new Pen(new SolidColorBrush(gc), 2));
        return (Solid(hover ? "#35353A" : "#2A2A2E"), new Pen(Solid("#555555"), 1.5));
    }

    private void DrawGap(DrawingContext ctx, GapBox gap, ClientNodeViewModel? holder)
    {
        int cm = holder?.PhysicalDistanceCm ?? 0;
        var ft = Format(cm > 0 ? $"{cm}" : "·", cm > 0 ? Caption : Faint, 9, bold: false);
        ctx.DrawText(ft, new Point(gap.Hit.X + (gap.Hit.W - ft.Width) / 2, gap.Hit.Y + (gap.Hit.H - ft.Height) / 2));
    }

    private void DrawNewRowZone(DrawingContext ctx)
    {
        bool hot = _gesture == Gesture.DraggingTile && _drop?.Row == _grid.Lanes.Count;
        var zone = ToRect(_grid.NewRowZone);
        ctx.DrawRectangle(hot ? AccentFaint : null, hot ? DashedHotPen : DashedPen, zone, 6, 6);
        var ft = Format("+ new row — drop a node here", hot ? Caption : Faint, 11, bold: false);
        ctx.DrawText(ft, new Point(zone.X + 12, zone.Y + (zone.Height - ft.Height) / 2));
    }

    // ── pointer input ────────────────────────────────────────────────────────

    /// <summary>Right-click on a tile (Task 7: node context menu). The tile is already selected.</summary>
    partial void HandleSecondaryPress(TileBox tile);

    /// <summary>Left press on lane chips or gap handles (Task 7). Set <paramref name="handled"/> to stop tile/rubber-band handling.</summary>
    partial void HandleChromePress(Point p, ref bool handled);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var host = _host;
        if (host == null) return;
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        Focus();
        var tile = _grid.HitTile(p.X, p.Y);

        if (props.IsRightButtonPressed)
        {
            if (tile != null)
            {
                // Windows convention: right-clicking outside the selection selects just that node first.
                if (!IsSelected(tile.Id)) host.SelectOnly(tile.Id);
                HandleSecondaryPress(tile);
            }
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        bool handled = false;
        HandleChromePress(p, ref handled);
        if (handled) { e.Handled = true; return; }

        _pressPoint = _pointer = p;
        _pressModifiers = e.KeyModifiers;
        if (tile != null)
        {
            _gesture = Gesture.PendingTile;
            _pressedId = tile.Id;
        }
        else
        {
            _gesture = Gesture.RubberBand;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) host.ClearSelection();
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _pointer = p;
        switch (_gesture)
        {
            case Gesture.PendingTile:
                if (Math.Abs(p.X - _pressPoint.X) > DragThreshold || Math.Abs(p.Y - _pressPoint.Y) > DragThreshold)
                {
                    _gesture = Gesture.DraggingTile;
                    _host?.BeginNodeDrag();
                    goto case Gesture.DraggingTile;
                }
                break;
            case Gesture.DraggingTile:
                _drop = _grid.ResolveDrop(p.X, p.Y);
                InvalidateVisual();
                break;
            case Gesture.RubberBand:
            {
                var band = BandRect();
                var ids = _grid.Tiles.Where(t => band.Intersects(ToRect(t.Tile))).Select(t => t.Id).ToList();
                _host?.SetRubberBandSelection(ids, additive: _pressModifiers.HasFlag(KeyModifiers.Control));
                InvalidateVisual();
                break;
            }
            default:
                UpdateHover(p);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var host = _host;
        var gesture = _gesture;
        var id = _pressedId;
        var drop = _drop;
        var modifiers = _pressModifiers;
        ResetGesture();
        e.Pointer.Capture(null);
        if (host == null) return;

        if (gesture == Gesture.PendingTile && id != null)
        {
            if (modifiers.HasFlag(KeyModifiers.Control)) host.ToggleSelection(id);
            else if (modifiers.HasFlag(KeyModifiers.Shift)) host.AddToSelection(id);
            else host.SelectOnly(id);
        }
        else if (gesture == Gesture.DraggingTile)
        {
            _ = RunSafe(FinishDragAsync(host, id, drop));
        }
    }

    /// <summary>
    /// Persist the move first, then resume topology refreshes — resuming first would let a refresh
    /// write the server's old order back onto the clients before the new one is stored.
    /// </summary>
    private static async Task FinishDragAsync(IRoomHost host, string? id, DropTarget? drop)
    {
        try
        {
            if (id != null && drop is { } d)
                await host.MoveNodeAsync(id, d.Row, d.ChainIndex);
        }
        finally
        {
            host.EndNodeDrag();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_gesture != Gesture.None) CancelGesture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _gesture != Gesture.None)
        {
            CancelGesture();
            e.Handled = true;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverId != null) { _hoverId = null; ToolTip.SetTip(this, null); InvalidateVisual(); }
    }

    private void CancelGesture()
    {
        if (_gesture == Gesture.DraggingTile) _host?.EndNodeDrag();
        ResetGesture();
    }

    private void ResetGesture()
    {
        _gesture = Gesture.None;
        _pressedId = null;
        _drop = null;
        InvalidateVisual();
    }

    private void UpdateHover(Point p)
    {
        var id = _grid.HitTile(p.X, p.Y)?.Id;
        if (id == _hoverId) return;
        _hoverId = id;
        ToolTip.SetTip(this, id == null ? null : TooltipFor(id));
        InvalidateVisual();
    }

    private string TooltipFor(string id)
    {
        var c = _host?.Clients.FirstOrDefault(x => x.ClientId == id);
        if (c == null) return id;
        var lines = new List<string> { c.DisplayName, c.IpAddress, c.MonitorDisplayName };
        var warning = _host?.NodeWarning(id);
        if (warning != null) lines.Add("⚠ " + warning);
        return string.Join(Environment.NewLine, lines.Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    private bool IsSelected(string id) => _host?.Clients.Any(c => c.ClientId == id && c.IsSelected) == true;

    private Rect BandRect() => new(
        Math.Min(_pressPoint.X, _pointer.X), Math.Min(_pressPoint.Y, _pointer.Y),
        Math.Abs(_pointer.X - _pressPoint.X), Math.Abs(_pointer.Y - _pressPoint.Y));

    private static async Task RunSafe(Task task)
    {
        try { await task; }
        catch (Exception ex) { AppLogger.CreateLogger<RoomView>().LogError(ex, "Room edit failed"); }
    }

    // ── drawing helpers ──────────────────────────────────────────────────────

    private static Rect ToRect(Box b) => new(b.X, b.Y, b.W, b.H);

    private static SolidColorBrush Solid(string hex) => new(Color.Parse(hex));

    private static FormattedText Format(string text, IBrush brush, double size, bool bold) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), size, brush);

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush, double size, bool bold = false, double maxWidth = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = Format(text, brush, size, bold);
        if (maxWidth > 0)
        {
            ft.MaxTextWidth = maxWidth;
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        ctx.DrawText(ft, at);
    }
}
```

- [ ] **Step 4: Swap the canvas for `RoomView` in `MainWindow.axaml`**

1. Delete the mixed-refresh banner (`<Border Grid.Row="2" … IsVisible="{Binding HasLocalRefreshRateMismatch}">…</Border>`) — the warning is now a per-tile ⚠ with tooltip.
2. Replace the topology border (`<Border Grid.Row="3" … Name="TopologyBorder">…</Border>`) with:

```xml
                    <Border Grid.Row="3" Background="#1A1A1A" CornerRadius="5" ClipToBounds="True">
                        <ScrollViewer HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto">
                            <controls:RoomView Host="{Binding}"
                                               Scene="{Binding ActiveScene}"
                                               SharedStartUtcMs="{Binding ActiveSharedStartMs}"
                                               SpriteImagePath="{Binding ActiveSpriteImagePath}"/>
                        </ScrollViewer>
                    </Border>
```

- [ ] **Step 5: Rewrite `MainWindow.axaml.cs` without the topology code**

Replace the whole file with (everything topology-related moved into `RoomView`):

```csharp
// Same using set as before the rewrite (file-drop helpers such as TryGetFiles are extension methods).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.UI.ViewModels;
using WaBiBaBuSy.WallpaperEngine.Services;

namespace WaBiBaBuSy.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Pre-initialize LibVLC in background to eliminate ~9s delay on first wallpaper
        _ = LibVLCPreloader.PreloadAsync(AppLogger.CreateLogger<MainWindow>());

        // Drag-and-drop of files onto the window adds them to the gallery
        AddHandler(DragDrop.DropEvent, OnFileDrop);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);

        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        // Click on empty gallery area deselects the wallpaper
        var galleryScrollViewer = this.FindControl<ScrollViewer>("GalleryScrollViewer");
        if (galleryScrollViewer != null)
        {
            galleryScrollViewer.PointerPressed -= OnGalleryPointerPressed;
            galleryScrollViewer.PointerPressed += OnGalleryPointerPressed;
        }
    }

    /// <summary>Handle click on gallery background to deselect wallpaper.</summary>
    private void OnGalleryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is ScrollViewer or ScrollContentPresenter or ItemsControl or WrapPanel or Border { Name: "GalleryScrollViewer" })
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                foreach (var w in viewModel.Wallpapers)
                    w.IsSelected = false;
                viewModel.SelectedWallpaper = null;
            }
        }
    }

    private void OnWindowOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetStorageProvider(StorageProvider);
            viewModel.SetMainWindow(this);
            viewModel.UpdateServerStatus();
            viewModel.StartRefreshTimer();
        }
    }

    private void OnWindowClosed(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.StopRefreshTimer();
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files == null) return;
        foreach (var item in files)
        {
            var path = item.Path?.LocalPath;
            if (!string.IsNullOrEmpty(path))
                vm.AddWallpaperFromPath(path);
        }
    }
}
```

- [ ] **Step 6: Build, test, run and check the room**

Run: `dotnet build -v q -nologo` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → all PASS.
Run the app, Start Server, open the control panel. Expected:
- Lanes with headers ("Row 1 →"), one tile per node with #n, name, resolution, status dot.
- Room ⚙ → Split evenly 2 → two lanes; row 2 header shows "←" and a "↕ facing" chip.
- Click / Ctrl+click / Shift+click / rubber band behave as described; the selection bar follows.
- Drag a tile to another spot in its lane → insert marker, drop reorders (the #n badges renumber).
- Drag a tile into the other lane, and onto "+ new row" → lanes update and survive an app restart.
- Press Escape mid-drag → nothing moves.
- **Review Focus 1:** with a remote client connected, start dragging a tile and disconnect the client
  (close its app) before releasing. If its tile disappears from the room, the drag cancels and nothing
  moves; if it stays (shown offline, red dot), the drop applies normally. Either way the topology keeps
  refreshing afterwards (node status updates again within ~5 s).
- Drop a tile, then wait for two refresh cycles → the new order stays (no jump back).
- Start the animation → the scene plays inside the tiles (live clock); mixed-refresh local monitors show ⚠ with a tooltip.

- [ ] **Step 7: Commit**

```bash
git add WaBiBaBuSy.UI/Controls/IRoomHost.cs WaBiBaBuSy.UI/Controls/RoomView.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs WaBiBaBuSy.UI/Views/MainWindow.axaml WaBiBaBuSy.UI/Views/MainWindow.axaml.cs
git commit -m "feat: RoomView with row lanes, live scene in tiles and drag between rows

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: `RoomView` menus — node context menu, lane chips, gap editor

**Files:**
- Create: `WaBiBaBuSy.UI/Controls/RoomView.Menus.cs`

**Interfaces:**
- Consumes: partial hooks `HandleSecondaryPress(TileBox)`, `HandleChromePress(Point, ref bool)` and private helpers `_grid`, `_host`, `RunSafe`, `IsSelected` (Task 6); `IRoomHost` members; `SeatMapEditor.MaxGapCm` (Task 1).
- Produces: nothing new for later tasks.

- [ ] **Step 1: Implement the partial hooks**

```csharp
// WaBiBaBuSy.UI/Controls/RoomView.Menus.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>Menus and inline editors of the room view: node context menu, lane chips, gap editor.</summary>
public partial class RoomView
{
    partial void HandleSecondaryPress(TileBox tile)
    {
        var host = _host;
        if (host == null) return;
        var ids = host.Clients.Where(c => c.IsSelected).Select(c => c.ClientId).ToList();
        if (ids.Count == 0) ids.Add(tile.Id);
        string suffix = ids.Count > 1 ? $" ({ids.Count})" : "";

        var items = new List<Control>
        {
            Item("Start new row here", () => host.SplitRowAtAsync(tile.Id), tile.ChainIndex > 0),
            Item("Merge into previous row", () => host.MergeRowIntoPreviousAsync(tile.Row), tile.Row > 0),
            new Separator(),
            Item("Clear wallpaper" + suffix, () => host.ClearNodesAsync(ids)),
            Item("Resync" + suffix, () => host.ResyncNodesAsync(ids)),
            Item("View logs", () => host.ShowNodeLogsAsync(tile.Id), host.CanFetchLogs(tile.Id)),
        };
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    partial void HandleChromePress(Point p, ref bool handled)
    {
        var host = _host;
        if (host == null) return;

        var gap = _grid.HitGap(p.X, p.Y);
        if (gap != null)
        {
            OpenGapEditor(gap);
            handled = true;
            return;
        }

        foreach (var lane in _grid.Lanes)
        {
            if (lane.Row > 0 && lane.FacingChip.Contains(p.X, p.Y))
            {
                _ = RunSafe(host.ToggleRowFacingAsync(lane.Row));
                handled = true;
                return;
            }
            if (lane.MenuChip.Contains(p.X, p.Y))
            {
                OpenLaneMenu(lane);
                handled = true;
                return;
            }
        }
    }

    private void OpenLaneMenu(LaneBox lane)
    {
        var host = _host!;
        var items = new List<Control>
        {
            Item("Rename…", () => { OpenRenameEditor(lane); return Task.CompletedTask; }),
            Item("Move row up", () => host.MoveRowAsync(lane.Row, -1), lane.Row > 0),
            Item("Move row down", () => host.MoveRowAsync(lane.Row, +1), lane.Row < _grid.Lanes.Count - 1),
            new Separator(),
            Item("Delete row (seats join the neighbouring row)", () => host.DeleteRowAsync(lane.Row), _grid.Lanes.Count > 1),
        };
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    private void OpenGapEditor(GapBox gap)
    {
        var host = _host!;
        var client = host.Clients.FirstOrDefault(c => c.ClientId == gap.HolderId);
        if (client == null) return;

        var input = new NumericUpDown
        {
            Minimum = 0, Maximum = SeatMapEditor.MaxGapCm, Increment = 1,
            Value = client.PhysicalDistanceCm, FormatString = "0", Width = 130
        };
        var ok = new Button { Content = "Set" };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = $"Gap before {client.DisplayName} (cm)", FontSize = 12 },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { input, ok } }
                }
            }
        };

        void Commit()
        {
            flyout.Hide();
            _ = RunSafe(host.SetGapCmAsync(gap.HolderId, (int)(input.Value ?? 0)));   // blank → 0, clamped in the host
        }
        ok.Click += (_, _) => Commit();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        flyout.ShowAt(this, showAtPointer: true);
    }

    private void OpenRenameEditor(LaneBox lane)
    {
        var host = _host!;
        string current = lane.Row < host.SeatMap.Rows.Count ? host.SeatMap.Rows[lane.Row].Name : $"Row {lane.Row + 1}";
        var input = new TextBox { Text = current, Width = 180 };
        var ok = new Button { Content = "Rename" };
        var flyout = new Flyout
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { input, ok } }
        };

        void Commit()
        {
            flyout.Hide();
            _ = RunSafe(host.RenameRowAsync(lane.Row, input.Text ?? string.Empty));
        }
        ok.Click += (_, _) => Commit();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        flyout.ShowAt(this, showAtPointer: true);
    }

    private MenuItem Item(string header, Func<Task> action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => _ = RunSafe(action());
        return item;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build WaBiBaBuSy.UI -v q -nologo`
Expected: 0 errors. If `ContextMenu.Open(Control)` or `FlyoutBase.ShowAt(Control, bool)` do not
resolve in Avalonia 12.1.2, check the 12.x API (`ContextMenu.Open(control)` / `flyout.ShowAt(control, true)`)
with `dotnet build` output and adapt only the call, not the behavior.

- [ ] **Step 3: Run the app and check the menus**

Expected:
- Right-click a tile → "Start new row here" (disabled on the first seat of a row), "Merge into previous row" (disabled in row 1), Clear, Resync, View logs (enabled only for remote nodes in server mode).
- "Start new row here" on the 3rd tile of a 5-tile row → a new lane with the last 3 nodes.
- Lane "↕ facing" chip toggles to "⇉ same side" and back; lane `⋯` → Rename… / Move row up/down / Delete row.
- Click the small number between two tiles → gap editor; enter 12, Enter → the gap shows 12.

- [ ] **Step 4: Check the review-focus cases**

- **Review Focus 4:** select tiles A and B, right-click tile C → only C is selected, "Clear wallpaper" has no "(n)" suffix and clears only C.
- **Review Focus 5:** open the gap editor, clear the field and press Set → gap becomes 0; type 900 → stored as 500.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.UI/Controls/RoomView.Menus.cs
git commit -m "feat: room context menus, lane chips and inline gap editor

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: Remove superseded panels, update docs, end-to-end check

**Files:**
- Modify: `WaBiBaBuSy.UI/Views/MainWindow.axaml` (remove "Selected Client Details" and "Active Animation" panels)
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (remove dead members)
- Modify: `CLAUDE.md`, `.docs/UI_ARCHITECTURE.md`, `.docs/RECENT_UPDATES.md`, `.docs/plans/2026-09-24-ui-redesign-design.md` (status line)
- Update: `graphify-out/GRAPH_REPORT.md` (via wrapper)

- [ ] **Step 1: Remove the superseded XAML**

In `MainWindow.axaml` delete:
1. The `<!-- Selected Client Details -->` border (`Grid.Row="5"`): order ▲/▼ → drag, distance → gap editor, LibVLC → Dev tools, logs → selection bar / context menu.
2. The `<!-- Active Animation Info … -->` border in the gallery pane (`IsVisible="{Binding ShowAnimationInfo}"`), including its `ScenePreviewControl` — the room tiles show the live scene; the "next:" label is in the toolbar.

- [ ] **Step 2: Remove dead view-model members**

In `MainWindowViewModel.cs` delete `MoveClientUp`, `MoveClientDown`, `ShowAnimationInfo`,
`ActiveAnimationFileName`, `ActiveDistributionMode`, `ActiveAnimationSpeed`, `ActiveBackgroundColor`,
`LocalRefreshRateMismatchWarning`, `HasLocalRefreshRateMismatch`, and every
`OnPropertyChanged(nameof(<one of these>))` line (in `OnSelectedWallpaperChanged` and
`OnIsCrossScreenRunningChanged`). Keep `ActiveLayout`, `ActiveScene`, `ActiveLabels`,
`ActiveSpriteImagePath`, `ActiveSharedStartMs` (bound by `RoomView`) and `UpdateClientDistance`
(used by `SetGapCmAsync`).

Run: `dotnet build -v q -nologo` → 0 errors (a remaining reference means a binding was missed in Step 1);
`dotnet test WaBiBaBuSy.Tests` → all PASS.

- [ ] **Step 3: Update the docs**

- `CLAUDE.md` → Key Capabilities: replace the "Room / seat map" bullet's second half with
  "rows are edited directly in the room view (drag nodes between lanes, right-click to split/merge, lane chips for facing), room-wide settings in the Room ⚙ popover"; replace the "Live preview" bullet with
  "Live preview: the room view paints the running scene inside every node tile with the players' own deterministic math (`ScenePainter`); the config dialog keeps its own preview until the docked editor (UI redesign Plan 2)".
  Add under Feature Designs: `- **[UI Redesign (2026-09-24)](.docs/plans/2026-09-24-ui-redesign-design.md)** — room-first main window; Plan 1 (toolbar + RoomView) implemented`.
- `.docs/UI_ARCHITECTURE.md` → main window section: describe toolbar (status · show controls · `⋯`), `RoomView` + `IRoomHost`, selection bar, Room ⚙ popover; note `SeatMapEditor` / `RoomGrid` in `WaBiBaBuSy.Models/Topology`.
- `.docs/RECENT_UPDATES.md` → new top entry "2026-09-24 — UI redesign Plan 1: room view" listing: Multi Monitor dialog fix, compact toolbar + `⋯`, Room ⚙ popover + split evenly, RoomView (lanes, live scene in tiles, drag between rows, context menus, gap editor), selection bar with per-node Clear/Resync, ScenePainter/SceneClock extraction, removed panels.
- Spec status line → `**Status:** Plan 1 (rollout steps 1–2) implemented 2026-09-24; Plans 2–3 pending`.

- [ ] **Step 4: Refresh the code graph**

Run: `./tools/graphify-update.ps1`
Expected: "Code graph updated."

- [ ] **Step 5: End-to-end check through UI Automation + screenshot**

Build, launch the app, open the control panel from the tray, take a screenshot of the main window:

```powershell
Get-Process WaBiBaBuSy* -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build WaBiBaBuSy.UI -v q -nologo
$exe = Get-ChildItem WaBiBaBuSy.UI\bin\Debug -Recurse -Filter WaBiBaBuSy.UI.exe | Select-Object -First 1
$p = Start-Process $exe.FullName -PassThru; Start-Sleep 8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
$A=[System.Windows.Automation.AutomationElement]; $S=[System.Windows.Automation.TreeScope]; $root=$A::RootElement
$tray=$root.FindFirst($S::Descendants,(New-Object System.Windows.Automation.PropertyCondition($A::NameProperty,' WaBiBaBuSy - Wallpaper Sync')))
$tray.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 4
$w=$root.FindFirst($S::Children,(New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty,$p.Id)))
$r=$w.Current.BoundingRectangle
$bmp=New-Object System.Drawing.Bitmap([int]$r.Width,[int]$r.Height)
$g=[System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X,[int]$r.Y,0,0,$bmp.Size)
$out="$env:TEMP\wbb_room_plan1.png"; $bmp.Save($out); "saved $out"
```

Expected: the screenshot shows the one-row toolbar, "Room ⚙" button, row lanes with tiles and the
"+ new row" zone, and the gallery on the right. Read the PNG and compare with spec §2. Then invoke
`Scene…` via UI Automation (name `Scene…`) and confirm the "Animation Configuration" dialog opens
(found as a child of the main window, as in the Task 0 check).

- [ ] **Step 6: Commit**

```bash
git add -A WaBiBaBuSy.UI CLAUDE.md .docs/UI_ARCHITECTURE.md .docs/RECENT_UPDATES.md .docs/plans/2026-09-24-ui-redesign-design.md graphify-out/GRAPH_REPORT.md
git commit -m "chore: remove superseded topology panels, document the room view

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

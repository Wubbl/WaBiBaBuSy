# Seat Map & Ring Topology — Design

**Created:** 2026-09-17 · **Status:** Proposal · **Roadmap:** Tier 1.1 (+ Tier 0.2 / 0.3)
**Solves:** F1 (1D canvas), F2 (order/identity lost on restart) in
[`2026-09-17-lan-party-roadmap.md`](2026-09-17-lan-party-roadmap.md)

---

## 1. Goal

Model the real room — two rows of ten monitors — and let one animation travel **around** it:
down row 1, across the end, back along row 2, and (optionally) across the start back to row 1
forever. Keep the order, the bezel distances and the machine identities across restarts of server
and clients so the setup is done once per party, not once per crash.

Non-goals: arbitrary 2D free placement of nodes (rows are enough for tables), per-row independent
animations (use two playlists / monitor selections for that).

## 2. Concepts

| Term | Meaning |
|---|---|
| **Seat** | One monitor slot in the room. Has a stable `SeatId`, a bound identity (ClientId + hostname + monitor index), a label ("Max"), and a bezel/gap distance to the previous seat in its row. |
| **Row** | Ordered list of seats along one table. Has an **orientation** relative to row 0: `Facing` (people sit across the table, so screens' local +x runs opposite to row 0's) or `SameSide`. |
| **Seat map** | Rows + traversal mode + turn gaps. Persisted; replaces the in-memory `OrderPosition`. |
| **Traversal** | How the 1D animation path visits the rows: `Ring` (closed loop), `Snake` (row 1 → row 2 → back, no wrap; today's behavior generalized), `Parallel` (rows stacked in Y; the sprite can move diagonally between them). |
| **Node layout** | What a player needs: its slice `[OffsetX, OffsetX+Width)` on the 1D path, `OffsetY`, `Mirrored`, canvas size, `Wraps`. |

## 3. Geometry

### 3.1 Ring / Snake → a 1D path
The path visits row 0 in physical order, then row 1 in **reverse** physical order, row 2 forward,
and so on. Between rows a `TurnGapCm` is inserted (converted to px like bezel gaps today via
`PixelsPerCm`). `Ring` closes the path with a final turn gap back to seat 0; `Snake` does not.

```
perimeter P = Σ_seats width + Σ bezel gaps + Σ turn gaps (+ closing turn gap if Ring)
```

Each node gets `OffsetX` on this path exactly like `VirtualCanvasManager.CalculateLayout` does
today — this is a **generalization**, not a rewrite. `VirtualCanvasWidth = P`.

### 3.2 Mirroring
A node is `Mirrored` when the path direction through it runs against its screen's local +x:

```
dir_r          = +1 for even rows (forward), −1 for odd rows (backward)
screenAxis_r   = +1 for row 0, and for rows with Orientation == SameSide
               = −1 for rows with Orientation == Facing
Mirrored(node) = dir_r * screenAxis_r < 0
```

Consequences (worth stating because they are counter-intuitive):
- **Two facing rows, Ring:** nothing is mirrored. A fish that goes "down the table" is seen
  left→right by everyone on both sides. This is the LAN-party default.
- **Two same-side rows (classroom), Ring:** row 1 is mirrored — the fish comes back right→left.

Mirroring affects **coordinate mapping only**, never bitmap content (a logo with text stays
readable). The sprite's facing is handled separately by the "face travel direction" flip
(roadmap 0.5), which looks at the sprite's velocity in *screen* space and therefore does the right
thing on mirrored nodes automatically.

Mapping in the player:
```
local = Mirrored ? (Width − (virtualX − OffsetX) − spriteW) : (virtualX − OffsetX)
```
For pattern cells: compute as today, then `screenX' = Width − screenX − cellW` when mirrored.

### 3.3 Wrap (Ring only)
`Wraps = true` means positions are taken modulo `P`. `MovementCalculator.CalculateLinear` and
`CalculateSineWave` get a `canvasWraps` parameter: when set, the auto start/end become `0 → P`
(instead of `−animW → canvasW`) and the loop period is `P` (snapped to the tile step for traveling
patterns, exactly like the existing `tileAlignStepX` snapping). A sprite straddling the seam
(`x + w > P`) is drawn twice by the player (at `x` and `x − P`) so the closing turn is seamless.

Bounce, Circular and RandomWalk ignore `Wraps` (they never leave the canvas).

### 3.4 Parallel
Rows are stacked: row r has `OffsetY = Σ_{k<r} (rowHeight_k + RowGapPx)`, all rows start at X = 0,
`VirtualCanvasWidth = max row width`, `VirtualCanvasHeight = Σ rows`. Mirroring follows §3.2 with
`dir_r = +1` for all rows. This is what makes "Bounce at 30°" cross from one table to the other.
Requires roadmap 0.1 (canvas height handed to players) — Parallel is meaningless while each player
uses its own monitor height.

## 4. Data model (`WaBiBaBuSy.Models/Topology/`)

```csharp
public enum TraversalMode { Snake, Ring, Parallel }
public enum RowOrientation { SameSide, Facing }

public class SeatMap
{
    public string Name { get; set; } = "Room";
    public TraversalMode Traversal { get; set; } = TraversalMode.Ring;
    public int TurnGapCm { get; set; } = 150;   // table-end crossing, Ring/Snake
    public int RowGapCm  { get; set; } = 120;   // Y distance between rows, Parallel
    public List<SeatRow> Rows { get; set; } = new();
    /// Nodes that connected but are not placed yet (shown on a "bench" in the UI).
    public List<Seat> Unseated { get; set; } = new();
}

public class SeatRow
{
    public string Name { get; set; } = "Row 1";
    public RowOrientation Orientation { get; set; } = RowOrientation.Facing;
    public List<Seat> Seats { get; set; } = new();  // physical order
}

public class Seat
{
    public string SeatId { get; set; } = Guid.NewGuid().ToString("N");
    public string? Label { get; set; }              // "Max"
    public string? ClientId { get; set; }           // primary binding (persistent, see §6)
    public string? Hostname { get; set; }           // fallback binding
    public int MonitorIndex { get; set; }           // which monitor of that machine
    public int DistanceToPreviousCm { get; set; }   // bezel gap within the row
}

/// Everything a player needs to place itself on the canvas. Replaces the loose
/// VirtualCanvasWidth / MonitorOffsetX pair everywhere (IPC and gRPC).
public class NodeLayout
{
    public int OffsetX, OffsetY, Width, Height;
    public int CanvasWidth, CanvasHeight;
    public bool Mirrored, Wraps;
    public int Order;               // traversal index (0..N-1) — used by Wave mode (roadmap 0.7)
    public float Scale = 1f;        // reserved for the physical canvas (see physical-canvas design)
}
```

`ScreenMapping` gains `Mirrored`, `OffsetY`; `VirtualCanvasManager` gains
`CalculateLayout(SeatMap map, IReadOnlyList<ScreenConfiguration> nodes)` that produces the
mappings + `BuildNodeLayout(clientId)`. The existing `CalculateLayout(IEnumerable<ScreenConfiguration>)`
stays and is equivalent to a single `SameSide` row in `Snake` mode — **existing configs behave
identically**.

## 5. Transport

- `SyncParameters`: add `string layout_json = 22;` (serialized `NodeLayout`). Keep fields 10/11
  populated for one release for older clients; the client prefers `layout_json` when present.
- `PlayerCommandLoadAnimation`: add `NodeLayout? Layout`; player prefers it over the loose fields.
- `ConnectedClient`: add `string seat_id = 12; int32 row_index = 13; int32 seat_index = 14;`
  so the topology UI can render lanes from the server's view.
- New RPC `UpdateSeatMap(SeatMapUpdate) returns (SeatMapResponse)` replacing
  `UpdateClientOrder`/`UpdateClientDistance` for seat-map-aware UIs (keep the old two for now).

## 6. Persistent identity and seat binding (roadmap 0.2 / 0.3)

**Client:** `ClientConfiguration.ClientId` (string, empty = unassigned). On first successful
registration store `AssignedClientId`; on every later registration send it. Do **not** null it in
`DisconnectAsync` (`WallpaperSyncClient.cs:178`) — clear only the channel. Result: the same machine
is the same node forever, and session-resume matches after a client app restart.

**Server:** `SeatMapStore` (JSON at `%APPDATA%\WaBiBaBuSy\seatmap.json`, same pattern as
`PlaylistStore`). `RegisterClient`:
1. find a seat with `ClientId == request.ClientId`;
2. else a seat with `Hostname == request.Hostname && MonitorIndex == n` (re-imaged machine);
3. else append to `Unseated`.
`OrderPosition` becomes a **derived** value (traversal index) — the counter
`_nextClientOrder++` (`WallpaperSyncService.cs:158`) goes away.

Local server monitors (`SERVER_LOCALHOST_MONITOR_n`) are seats too; they bind by the
`LOCAL_MACHINE`/`SERVER_LOCALHOST` prefix + monitor index.

## 7. UI

### 7.1 Topology as lanes
Replace the auto-wrap grid in `MainWindow.axaml.cs` (`CalculateAutoWrapPositions`) with one lane
per row plus an **Unseated bench** at the bottom. Drag a node within a lane to reorder, between
lanes to move rows, from the bench to seat it. Arrows follow the traversal (`Ring` draws the
closing arc). Each node shows seat label, order badge, refresh rate, drift label (unchanged).
Row header: name, orientation toggle (Facing / Same side), "+ row", "− row". Global: traversal
mode, turn gap, row gap.

### 7.2 "Walk the room" ordering
Two small tools that make seating 20 machines a two-minute job:
- **Identify**: per-node button (and "Identify all") sends `cmd_identify` to the player, which
  draws a huge seat number + label over the wallpaper for N seconds. The debug overlay's
  DirectWrite info panel is the drawing primitive to reuse.
- **Click-to-order**: press "Order by clicking"; every node shows its number; you click nodes in
  the topology in the order you see them in the room; each click assigns the next seat. Esc cancels.

### 7.3 Config dialog
"Target Monitors" list is replaced by the seat map: check rows or individual seats. The traversal
mode is a **seat-map** property, not a per-animation one; the animation dialog only shows a
read-only "Path: Ring, 20 seats, perimeter 41.3 m, lap ≈ 62 s at current speed" line (lap time
falls out of `PlaylistScheduler.ComputeLapMs` once it knows `P`).

## 8. Player changes (`Program.cs`)

- Store `NodeLayout _layout`; derive `_virtualCanvasWidth`, `_monitorOffsetX` from it.
- `UpdateAnimationPosition`: pass `canvasWraps` to `MovementCalculator`; apply mirrored mapping.
- `DrawAnimationLayer`: if `Wraps` and the sprite crosses the seam, draw a second copy at `x − P`.
  Pattern cells: mirror `ScreenX` when `Mirrored`.
- `cmd_identify`: draw label for `DurationMs`.
- The IconZone precomputed global path is computed over the 1D path exactly as today; with
  `Mirrored` nodes the path's local mapping goes through the same helper.

## 9. Tests (`WaBiBaBuSy.Tests`)

- `SeatMapLayoutTests`: ring of 2×10 equal nodes → perimeter, offsets increase along traversal,
  row-1 offsets are in reverse physical order, `Mirrored` false for Facing, true for SameSide.
- `WrapPositionTests`: Linear with `Wraps` at `t` such that `x + w > P` → player-side helper yields
  two draw positions; at `t = P/speed` position equals `t = 0`.
- `SeatBindingTests`: register by ClientId / hostname fallback / unseated.
- `SeatMapStoreTests`: JSON roundtrip.
- Back-compat: single-row `SameSide` `Snake` layout equals the current `CalculateLayout` output.

## 10. Migration & rollout

1. Roadmap 0.2 + 0.3 (identity + persistence) — ship alone, low risk.
2. Models + layout builder + tests (pure, no UI).
3. Transport (`layout_json`, `Layout` in IPC) + player mirrored/wrap drawing. Default seat map =
   one row → zero behavior change.
4. Topology lanes + Identify + click-to-order.
5. Preview (authoring design) consumes the same `NodeLayout` list — no extra work there.

## 11. Open questions

- Turn gap default: 150 cm is a guess for "end of two tables"; make it visible in the lane header.
- Should `Ring` be allowed with a single row? Yes — a 10-screen loop with a teleport-free wrap is
  itself an improvement over today's jump from node 10 back to node 1 (the gap is then the
  turn gap, i.e. the sprite spends ~1 s "off-screen" instead of teleporting).

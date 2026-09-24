# UI Redesign — Room-first main window — Design

**Date:** 2026-09-24 · **Status:** approved design, not implemented
**Supersedes:** §3 (dialog restructure) and §6 (main window) of
[Authoring UX & Live Preview](2026-09-17-authoring-ux-and-preview-design.md). §2 (preview math),
§4 (presets) and §5 (hidden parameters, done) of that doc still apply.

## 1. Goal and use case

- **At home:** build playlists (scene editing is the main work).
- **At the LAN party:** heavy tinkering while machines and monitors are connected and arranged,
  then mostly start/stop shows. The **topology (room) is the main view**.

Success = one screen where every node sits in its room position with the scene playing inside
it, rows are rearranged by direct manipulation, and a docked editor next to it changes the scene
with instant preview. No modal 900-px scrolling dialog, no controls that are visible but
irrelevant to the current selection.

Problems in the current UI (screenshots 2026-09-24):
- Top toolbar mixes developer tools (Composition, Apply D2D, background color, Debug toggles)
  with show controls (Multi Monitor, Playlist).
- The Room strip (rows, seats/row, facing, path, turn gap, physical units, align) takes a third
  of the topology panel and is abstract ("Seats / row") instead of spatial.
- The room is drawn twice: the topology canvas and a separate `ScenePreviewControl` (main window
  "Active Animation" panel and the top of the config dialog).
- `CrossScreenConfigDialog` is one long modal scroll; grey help paragraphs take ~⅓ of its
  height; per-field px/cm unit dropdowns; a "Target Monitors" list that duplicates the topology.
- `PlaylistDialog` is modal, so it cannot be used together with scene editing.
- Selection-specific buttons ("Clear Wallpaper", "Stop Animation", distance editing) are
  scattered and not tied to the selection.

## 2. Main window layout

```
┌ Toolbar ──────────────────────────────────────────────────────────────┐
│ ● Server running · 12 nodes   │ Show: [Party ▾] ▶ ■ ⏭  Now: Ring Fish │  ⋯
├───────────────────────────────────────────────┬───────────────────────┤
│ ROOM   Room ⚙ Ring · 150 cm       [+ row]     │ [ Scene | Playlist ][⟨]│
│ ┌ Row A → ↕ facing ─────────────────────────┐ │ ┌ gallery strip ────┐ │
│ │ [#0 🐟][#1   ][#2   ][#3   ][#4   ]       │ │ └───────────────────┘ │
│ └───────────────────────────────────────────┘ │ Content│Motion│Look│BG│
│ ┌ Row B ← ──────────────────────────────────┐ │  …fields for tab…     │
│ │ [#9   ][#8   ][#7   ][#6   ][#5   ]       │ │                       │
│ └───────────────────────────────────────────┘ │ Targets: 3 selected   │
│ ┈ + new row ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈ │ ▶ Play on selection   │
│ ◉ Preview: draft (not live)   ⏸ ⟲ 1×          │   Play on all         │
├───────────────────────────────────────────────┤ ＋ Save to playlist ▾ │
│ 3 selected: Clear · Resync · Make row · …     │ ⟲ Revert              │
│ Health: 11 ready · 1 stale · drift ≤ 12 ms    │                       │
└───────────────────────────────────────────────┴───────────────────────┘
```

- **Toolbar:** server state + node count; show controls (playlist picker, ▶ ■ ⏭, "Now: … /
  Next: …"); "Stop animation" / "Clear all" appear only while something is running.
  `⋯` menu holds everything rare (§6).
- **Room (left, large):** `RoomView` (§3). Preview badge shows whether the tiles show the
  **draft** (editor) or **live** (shared clock); clicking it toggles. Pause / restart / 1×-4×-16×
  apply to the draft clock only.
- **Selection bar:** appears only when nodes are selected (§3.4). Health strip stays under it.
- **Right panel:** `[Scene | Playlist]` tabs (§4, §5), collapsible to a thin rail with `⟨`.
  GridSplitter between room and panel stays.
- **Removed from the main window:** Room strip, "Selected Client" panel, "Active Animation"
  panel + its `ScenePreviewControl`, "Selected Wallpaper" panel, full-width gallery pane,
  mixed-refresh banner (becomes a per-tile ⚠).

## 3. Room interactions (`RoomView`)

### 3.1 Lanes
- One lane per `SeatRow`. Header: name, travel arrow (→/← derived from traversal, not editable
  directly), `↕ facing` toggle (`SeatRow.Orientation`), `⋯` (Rename · Move row up/down ·
  Delete row → its nodes merge into the previous row).
- Dashed **"+ new row"** drop zone under the last lane; `[+ row]` in the header also appends an
  empty row that nodes can be dropped into. Empty rows are dropped on save.
- **Room ⚙ popover:** traversal (Ring / Snake / Parallel), turn gap cm, row gap cm, physical
  units (canvas mode), vertical anchor. Header summary shows e.g. "Ring · 150 cm".

### 3.2 Nodes
- Click selects; Ctrl/Shift+click extends; rubber band on empty space (all existing behaviour).
- **Drag a tile:** reorder inside a lane, drop into another lane, or onto "+ new row". An insert
  marker shows the target slot. Implemented as chain-order + `SeatRow.SeatCount` changes via
  `SeatMapEditor` — `SeatMapLayoutBuilder` and the `seatmap.json` / `topology.json` formats are
  unchanged.
- **Gap handles:** the gap between two adjacent tiles in a lane shows the distance ("12 cm");
  click → inline number edit. Replaces the "Physical Distance (cm)" box and "Update Distance".
- **Tile content:** the scene painted inside the tile (§7 `ScenePainter`), with `#n`, hostname,
  resolution@Hz, status dot and drift label on the bottom edge; ⚠ badge + tooltip for
  mixed-refresh or stale state.

### 3.3 Context menu (right-click a node)
Start new row here · Merge into previous row · Identify* · Clear wallpaper · Resync ·
View logs · Scale… (physical canvas only).
*Identify (flash `#n` on that machine) only if the player can do it cheaply; otherwise omitted
in the first cut.

### 3.4 Selection bar
| Selection | Actions |
|---|---|
| none | hidden |
| 1 node | name · Clear · Resync · Logs · Remove from room |
| N nodes | "N selected" · Clear · Resync · Make row from selection · Use as scene targets |

### 3.5 Dropped
▲/▼ order buttons (drag replaces them). "Apply via LibVLC" moves to Dev (§6).

## 4. Scene editor (right panel, "Scene" tab)

### 4.1 Principles
- Only fields relevant to the current choices are shown (mode-specific hiding stays); every tab
  has a collapsed **Advanced ▸** section.
- Help paragraphs become `ⓘ` tooltips. Validation becomes one-line amber chips
  (e.g. "Endless needs a Traveling color mode", lap time < 5 s or > 10 min, pattern with video).
- **Units follow the room:** no per-field unit dropdowns. When Room ⚙ physical units are on,
  size/speed are shown and stored in cm / cm/s (`SizeUnit.Centimeters`,
  `SpeedUnit.CentimetersPerSecond`), else px / px/s. The config already stores both values
  (`TargetHeight` + `TargetHeightCm`, `SpeedPixelsPerSecond` + `SpeedCmPerSecond`): the editor
  shows the one matching the room, `BuildConfig` sets the unit enums from the room, and the other
  value is carried through untouched — no conversion, nothing lost.
- **Targets = room selection.** The "Target Monitors" list is removed; the editor footer shows
  "Targets: N selected" (click → highlights them) or "All nodes".

### 4.2 Header
Gallery strip: horizontally scrolling thumbnails with type badge, `+ Add`, file drag-drop.
Click → primary animation file; Ctrl+click → add as extra source image. Background-image modes
pick from the same strip via a "choose from strip" state on the image field.

### 4.3 Tabs
| Tab | Default | Advanced ▸ |
|---|---|---|
| **Content** | primary file + extra images as removable chips · Fit mode (Target height reveals height) · Vertical alignment · Loop · Face travel direction | Playback speed multiplier · multi-image spread / phase jitter |
| **Motion** | Pattern type · Speed slider + lap-time readout · Distribution Sequential/Simultaneous (segmented toggle) · Direction angle (Linear/Bounce) · Reverse | Endless · amplitude / frequency / radius / iteration steps · seed + step interval + Randomize · wave delay per node (Simultaneous) |
| **Look** | Color grading mode + its main fields · Pattern on/off + Fill vs count | colored-cells %, color seed · spacing, margin, random offset/rotation, zone size, fade border |
| **Background** | Mode + main fields (color + auto-detect, image, corridor/zone colors, palette randomize) | Three-zone px values · Rotate with path (IconZone) |

"Show icon-zone palette" moves to Dev → Debug.

### 4.4 Footer (sticky)
Targets summary · `▶ Play on selection` · `Play on all` · `＋ Save to playlist ▾`
(new item / update the loaded item) · `⟲ Revert` (to what is live, or to the loaded playlist item).

### 4.5 Draft model
The editor always holds one draft `CrossScreenConfig` (`CrossScreenConfigViewModel.BuildConfig`).
Changes debounce (150 ms, as today) into the room preview only; nothing reaches the machines
until Play. Opening the panel shows the draft in the room; collapsing it switches to live.

## 5. Playlist ("Playlist" tab)

- Header: playlist picker (`Party ▾`, New, Save), Loop / Shuffle toggles, default duration in
  **seconds**.
- Item list: thumbnail · name · duration (inline edit, seconds) · drag handle to reorder.
  Right-click: Duplicate · Remove. Replaces the Add/Edit/Duplicate/Remove/Up/Down column.
- Selecting an item loads it into the Scene tab as the draft; "Save to playlist → update"
  writes it back. `＋ Add current scene` at the bottom captures the draft.
- Show controls live only in the toolbar. Lap-snap and other existing per-item options keep
  their current fields (in the item's inline expander).
- `PlaylistDialog` is removed; `PlaylistStore` / JSON format unchanged.

## 6. Settings and `⋯` menu

- `⋯`: Connect to server… / Find… (client role) · Settings · Open log folder ·
  **Dev ▸** Composition + Apply D2D · background color + Auto · Apply via LibVLC ·
  debug overlay (On, Path, Rects, Zones, Info) · icon-zone palette.
- Tray menu unchanged.
- **Settings dialog:** left sidebar (Server · Client · Wallpaper · Logging) instead of one scroll;
  rarely changed fields (service name, heartbeat, max cache size, max clients) behind Advanced.
  No setting is added or removed.

## 7. Code structure

| Unit | Location | Responsibility |
|---|---|---|
| `SeatMapEditor` | `WaBiBaBuSy.Models/Topology` | Pure functions on `(SeatMap, IReadOnlyList<string> order)`: `MoveNode(id, rowIndex, indexInRow)`, `SplitRowAt(id)`, `MergeRowIntoPrevious(rowIndex)`, `DeleteRow`, `MakeRowFromSelection(ids)`, `SetGap(a, b, cm)`. Returns new order + rows; no UI. |
| `ScenePainter` | `WaBiBaBuSy.UI/Controls` | Drawing code extracted from `ScenePreviewControl`: paints a scene into given node rects for a clock value. Used by `RoomView`. |
| `RoomView` | `WaBiBaBuSy.UI/Controls` | Lanes, tiles, drag/drop, gap handles, context menu, selection; layout from `SeatMapLayoutBuilder`. Replaces the topology code in `MainWindow.axaml.cs`. |
| `SceneEditorPanel` | `WaBiBaBuSy.UI/Views` | UserControl over `CrossScreenConfigViewModel` (existing `LoadFromConfig`/`BuildConfig`). |
| `PlaylistPanel` | `WaBiBaBuSy.UI/Views` | UserControl over `PlaylistViewModel`. |

`MainWindowViewModel` gains selection-bar / toolbar properties and loses the dialog-opening
code; no broader refactor of it in this work. `ScenePreviewControl` is deleted once nothing uses it.

## 8. Rollout (each step builds, runs and is committed on its own)

1. Toolbar + `⋯` menu + selection bar.
2. `SeatMapEditor` + tests → `RoomView` with scene painted in tiles; remove Room strip,
   Selected Client, Active Animation panels.
3. Docked Scene editor (tabs, Advanced, units follow room, targets = selection); remove
   `CrossScreenConfigDialog`.
4. Playlist tab + toolbar show controls; remove `PlaylistDialog`.
5. Settings sidebar.

## 9. Testing

- `SeatMapEditorTests`: move within/between rows, split, merge, delete row, make-row-from-
  selection, gap edit; order + `SeatCount` invariants (sum of seat counts = node count, no empty
  rows after normalize, Ring wrap unaffected).
- Existing layout/preview tests must stay green.
- UI steps: launch the app and drive it through UI Automation (tray → control panel → actions),
  screenshot after each rollout step for review.

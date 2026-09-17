# Authoring UX & Live Preview — Design

**Created:** 2026-09-17 · **Status:** §2 preview implemented 2026-09-17 (`WaBiBaBuSy.UI/Controls/ScenePreviewControl.cs`); §3 tabs, §4 presets, §6 show-health still open · **Roadmap:** Tier 2.1 (preview, done) + 2.2 (UX) + 0.6 (done)
**Solves:** F5 in [`2026-09-17-lan-party-roadmap.md`](2026-09-17-lan-party-roadmap.md)

---

## 1. Goal

Make it possible to **design an animation at the desk and know what it will do on 20 screens**
before pressing Start, and to get from "idea" to "running on the wall" in under a minute at the
party. Three parts: a live preview, a restructured dialog with presets, and exposing the
parameters the model already has.

## 2. Live preview

### 2.1 Why it is cheap
Every visual is a pure function in `WaBiBaBuSy.Models` (`MovementCalculator`, `PatternLayout`,
`ColorGrader`, `PlaylistScheduler`) with no D2D dependency. A preview only has to (a) lay out the
seat map as rectangles, (b) evaluate those functions at a preview clock, and (c) draw colored
rectangles or thumbnails with Avalonia. It never touches the player process.

### 2.2 `ScenePreviewControl` (new, `WaBiBaBuSy.UI/Controls/`)
Inputs (bindable): `IReadOnlyList<NodeLayout> Nodes` (from the seat-map layout builder),
`CrossScreenConfig Scene`, `IPreviewClock Clock`, `double Zoom`.

Rendering (custom `Render(DrawingContext)` at ~30 fps via `DispatcherTimer` or
`TopLevel.RequestAnimationFrame`):
- Room: rows as horizontal bands, nodes as rectangles at `OffsetX/OffsetY × Zoom`, bezel/turn gaps
  visible as dark space, seat labels, mirrored nodes marked with ‹ ›, seam of a Ring drawn as a
  dashed line. Fits 20 nodes in ~900 px wide.
- Per layer, per node: call the **same** code the player calls — `MovementCalculator.Calculate`
  with that node's canvas/offset (Sequential vs Simultaneous per layer), `PatternLayout.Compute`
  for pattern layers (cap cells for the preview at e.g. 400), `ColorGrader.Compute/ComputeForCell`
  for tints. Draw the sprite as its thumbnail (gallery already generates thumbnails) tinted with
  the color matrix approximated as a solid tint, or as a colored rounded rectangle when no
  thumbnail exists.
- Mirroring, wrap seam duplication and per-node `Scale` use the same helpers as the player
  (move those helpers into `WaBiBaBuSy.Models/Wallpaper/NodeMapping.cs` so both share them).
- Overlay: current position readout, lap time, "sprite is on: Row 2 · Seat 7 (Max)".

### 2.3 Two clocks
- **Design clock** (config dialog): starts at 0 when the dialog opens, has play/pause, a speed
  multiplier (1×, 4×, 16×) and a scrubber. Restarts on every config change (debounced 150 ms).
- **Live clock** (main window): `elapsed = nowUtc − sharedStartTimestamp` of the running show —
  the same epoch the players use. This *is* the "live position dot" from OPEN_ITEMS §4.4, only
  with the whole scene instead of a dot. During a playlist show it switches items with the
  orchestrator (`PlaylistOrchestrator.ItemChanged`).

### 2.4 Fidelity notes (say what it does not show)
The preview is geometry + color, not pixels: no GIF frame timing, no icon zones (draw the icon-zone
bands if a global path was computed, as the debug overlay does), no LibVLC. State this in a
tooltip so nobody expects the fish's fins to move.

## 3. Dialog restructure (`CrossScreenConfigDialog`)

Replace the single 750-px scroll with a two-pane window: **preview on top** (or right), **tabs
below**. Same `CrossScreenConfigViewModel` properties, regrouped — this is mostly XAML.

| Tab | Contents (existing fields unless marked new) |
|---|---|
| **Content** | animation file(s) from gallery, fit mode, size (px / *cm*), vertical alignment, loop, speed multiplier *(new: currently hidden)* |
| **Motion** | movement type, speed (px/s / *cm/s* with lap-time readout), reversed, endless, type-specific params, *seed + step interval + "randomize"* (new: currently hardcoded 42/1000 in `BuildConfig`), distribution (Sequential/Simultaneous), *wave phase delay* (roadmap 0.7) |
| **Look** | color grading, pattern multiplier, multi-image spread/jitter, *effects* (facing, trail, pulse, bezel effects — effects design) |
| **Background** | mode + all mode-specific settings (unchanged) |
| **Targets** | seat map with row/seat checkboxes (replaces the flat monitor list) |
| **Layers** *(when scene layers land)* | list on the left of the tabs; tabs edit the selected layer |

Validation hints (inline, non-blocking): Endless without a Traveling color mode; Pattern with video
content; SineWave amplitude larger than half the canvas height; speed that gives a lap time under
5 s or over 10 min; Simultaneous + Ring (fine, but say the sprite will not travel).

## 4. Presets

- `PresetStore` (`%APPDATA%\WaBiBaBuSy\presets\<name>.json`), same pattern as `PlaylistStore`.
  A preset is a `CrossScreenConfig` **without** `SelectedMonitorIds` (targets belong to the room).
- Dialog: "Presets ▾" (load), "Save as preset…", "Export…/Import…" (single JSON file, e.g. to
  share with the club). Playlist "Add" gets "from preset".
- Tray menu: "Quick start ▸ <preset list>" — start a preset on all seats without opening a window.
- **Starter presets** shipped in the installer (`/Presets/*.json`), copied on first run:
  *Ring Fish* (Linear ring, face travel, trail), *Logo Rain* (Pattern Fill, Endless,
  TravelingRainbow 30 %), *Mexican Wave* (Simultaneous Bounce, wave phase 250 ms), *Orbit*
  (Circular per node), *Party Colors* (CycleColorList on a static logo, fast).

## 5. Expose hidden parameters (roadmap 0.6, S)

`CrossScreenConfigViewModel.BuildConfig()` hardcodes `RandomSeed = 42` and
`RandomStepIntervalMs = 1000f`; `SpeedMultiplier`, `StartX/Y`, `EndX/Y`, `CenterInitialPosition`
are never set. Add observable properties + `LoadFromConfig` round-trip for seed, step interval and
speed multiplier now; leave Start/End coordinates for the path designer (Tier 3) since raw canvas
coordinates are not usable without the preview anyway — with the preview, allow dragging start/end
handles directly on it.

Also remove the duplicate `CrossScreenConfig.AnimationSpeedPxPerSecond` (always equals
`Movement.SpeedPixelsPerSecond`).

## 6. Main window

- Replace the "Active Animation Info" text panel with the live `ScenePreviewControl` (compact
  height), toggleable.
- Show-health strip (from the show-reliability design): nodes ready / playing / stale / drift.
- The topology lanes (seat-map design) and the preview share the layout, so a node selected in one
  highlights in the other.

## 7. Tests

- `NodeMappingTests`: shared mirror/wrap/scale helper used by player and preview.
- `PresetStoreTests`: roundtrip, name sanitization, import of a playlist item's config.
- Preview is UI; verify manually with the starter presets on a fake 2×10 seat map (add a
  "Demo room" seat map for exactly this purpose — it also makes screenshots for the README).

## 8. Rollout

1. 0.6 hidden parameters (S).
2. `NodeMapping` helper + `ScenePreviewControl` with design clock, embedded in the current dialog
   above the form (no restructure yet) — immediate value.
3. Tabs + validation.
4. Presets + tray quick start + starter presets.
5. Live clock in the main window; show-health strip.

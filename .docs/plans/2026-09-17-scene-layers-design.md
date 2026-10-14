# Scene Layers (multiple sprites per scene) — Design

**Created:** 2026-09-17 · **Revised:** 2026-10-14 (agreed spec for slice 1) · **Status:** Approved for planning
**Roadmap:** Tier 2.3 (XL) · **Solves:** F4 in [`2026-09-17-lan-party-roadmap.md`](2026-09-17-lan-party-roadmap.md)
**Depends on:** show reliability (multi-file transfer — done), live preview (done), crossing effects (done)

---

## 0. Decisions (2026-10-14)

| # | Decision | Choice |
|---|---|---|
| D1 | Scope of slice 1 | Image/GIF layers end-to-end: model, player, transport, editor, preview. Video stays on layer 0 only |
| D2 | Data model | **Full replacement**: `CrossScreenConfig.Layers` is the only sprite store; `Animation`, `Movement`, `DistributionMode`, `AnimationSpeedPxPerSecond` are removed. Legacy JSON migrates on read |
| D3 | Draw order | List order (index 0 = bottom). No `ZOrder` field |
| D4 | Distribution | Per layer, resolved inside the player from `NodeLayout` + own monitor size. Scene-wide `perMonitorMode` is removed |
| D5 | Wire format | One `scene_json` field; the per-part JSON fields and loose movement/layout fields are removed (`reserved`). Server and clients must run the same version |
| D6 | Layer-0 privileges | Only layer 0 may be a video and only layer 0 follows the IconZone A* path / clip mask |

**Out of slice 1 (follow-ups):** video on layers > 0 (per-layer LibVLC, cap 2), spawn / despawn windows,
layer presets ("Add layer from preset"), Suggest "add as layer".

## 1. Goal

A **scene** is a background plus **N layers**, each an independent sprite (or pattern grid) with its own
source file(s), movement, color grading, effects, size, opacity, distribution and phase offset.
Example for the party:

| Layer (bottom → top) | Source | Movement | Look |
|---|---|---|---|
| Logo rain | clan logo PNG | Linear, Endless, 40 px/s | Pattern Fill, TravelingRainbow |
| Bubbles | bubble GIF ×3 | Bounce 60°, 120 px/s, phase +7 s | 60 % opacity |
| The fish | fish GIF | Linear around the ring, 25 cm/s | face travel, trail |
| Sponsor | sponsor PNG | Static, bottom-right of each node (Simultaneous) | 50 % opacity |

Success: such a scene plays in sync on every seat and in the room preview; every existing preset,
playlist and scene file keeps working.

## 2. Data model and migration

```csharp
public class SceneLayer
{
    public string Name { get; set; } = "Layer";
    public bool Enabled { get; set; } = true;
    public float Opacity { get; set; } = 1f;                      // 0..1, multiplied into the sprite draw
    public AnimationLayerConfig Animation { get; set; } = new(); // source(s), size, fit, pattern, colors, Effects
    public MovementConfig Movement { get; set; } = new();
    public AnimationDistributionMode Distribution { get; set; } = AnimationDistributionMode.Sequential;
    public long PhaseOffsetMs { get; set; }                       // layer clock = elapsed + PhaseOffsetMs
}

public class CrossScreenConfig : IJsonOnDeserialized
{
    public BackgroundLayerConfig Background { get; set; } = new();
    public List<SceneLayer> Layers { get; set; } = new();
    public List<string> SelectedMonitorIds { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Legacy { get; set; }
    void IJsonOnDeserialized.OnDeserialized() => LegacySceneMigration.Apply(this);
}
```

- Effects already live on `AnimationLayerConfig.Effects` — no separate per-layer effects type.
- **Migration on read, forever:** old keys (`Animation`, `Movement`, `DistributionMode`,
  `AnimationSpeedPxPerSecond`) have no property and land in `Legacy`. `LegacySceneMigration.Apply`:
  if `Layers` is empty and `Legacy` holds `Animation` or `Movement`, build `Layers[0]` from them
  (`Distribution` ← `DistributionMode`, name = file name without extension); then clear `Legacy`.
  Covers every deserialization path (presets, playlists, scene files, clones) without a migration step.
- A scene with no layers is valid (background only).
- Repo files (`TestScenarios/scenes`, `TestScenarios/showcase/scenes`) are rewritten once by load + save
  through `SceneFile`. `%APPDATA%` presets/playlists upgrade on their next save and always load.

**Consumers to migrate** (all reads/writes of `config.Animation` / `.Movement` / `.DistributionMode`):
`CrossScreenPlanner`, `PhysicalUnits`, `SceneChecks`, `SceneAssets`, `SpriteEffects` callers,
`SceneRecipes` / `SceneSuggester` / `PlaylistBuilder` (write `Layers[0]`), `PresetSeeder`, `PresetStore`,
`PresetBundle`, `PlaylistStore`, `PlaylistOrchestrator`, `ShowStatus`, `SceneFile`, `ITestHost`,
`WaBiBaBuSyService`, `ServerPlaybackHost`, `RemotePlaybackHost`, `ScenePainter`, `CrossScreenConfigViewModel`,
`MainWindowViewModel*`, `PlaylistItemRow`, `PlaylistViewModel`, `TrayViewModel`.

## 3. Player (`WaBiBaBuSy.Player.D2D`)

New file `LayerRuntime.cs` — one instance per layer, holding what are today per-sprite statics in
`Program.cs`: frames / delays / total duration per source, native and draw size, fit mode, tile-align
step, traveling flag, position (`X`, `Y`, `VirtualX`), rotation, facing state, endless cell offset,
trail samples. Methods are today's bodies moved mechanically (statics → instance fields):

| `LayerRuntime` | From |
|---|---|
| `Load(ctx)` | `HandleLoadAnimationCommand` sprite part, `LoadAdditionalAnimationSources`, `CalculateAnimationLayout` |
| `UpdatePosition(elapsed)` | `UpdateAnimationPosition` |
| `Draw(ctx, elapsed)` | `DrawAnimationLayer`, `DrawTrail`, `DrawBezelEffect` |

**Stays static in `Program`:** window, devices, background, IconZone / corridor / mask state, LibVLC video,
shared clock, probes, show events, the shared `ColorMatrixEffect` and brushes (each layer sets its matrix
before drawing; one render thread).

**Render loop:**
```
DrawBackground()
foreach layer in _layers:                       // index order = draw order
    if layer.Enabled: layer.UpdatePosition(elapsed + PhaseOffsetMs); layer.Draw(...)
DrawShowEvents()                                // unchanged, on top
```

**Distribution per layer:** the player receives the shared `NodeLayout` and knows its own monitor size.
`Sequential` → layout canvas width / offset (today's path); `Simultaneous` → own width, offset 0 (today's
per-monitor path). `D2DCompositionService.InitializeAsync` loses its `perMonitorMode` parameter.

**Layer 0 only:** video (static LibVLC, drawn as layer 0's bitmap) and IconZone path following + clip
mask (A* state stays static; only layer 0's `UpdatePosition` reads it).

**Probes / debug:** `AnimationStateSnapshot` becomes per layer so exact-frame captures restore all layers.
F11 overlay draws layer 0's path and trail; the info panel lists every layer's position.

**IPC:** `PlayerCommandLoadAnimation` carries `List<SceneLayer> Layers` (resolved, pixels only) instead
of `AnimationConfig` / `MovementConfig`.

**Refactor order** (build + run existing scenes after each):
1. Single `LayerRuntime` (`_layers[0]`), drop the `_d2dGifFrames` / `_d2dGifDelays` duplicates. Behavior-neutral —
   determinism tests and `parity-matrix` stay green.
2. N layers + IPC `Layers`.
3. Per-layer distribution.

## 4. Apply path and transport

**Planner (`CrossScreenPlanner`):** seat map / canvas built once. `PhysicalUnits.ResolveForCanvas` becomes
`(SceneLayer, refPixelsPerCm) → SceneLayer` and runs per layer. `CrossScreenPlan.EffectiveLayers`
replaces `EffectiveMovement` / `EffectiveAnimation`; `PerMonitor` is removed. IconZone A* path is
computed for layer 0 with its distribution.

**gRPC `SyncParameters`:** new `string scene_json = 33` = resolved scene (`Background` + effective
`Layers`, no `SelectedMonitorIds`); `layout_json` (25) and `assets` (26) unchanged. Removed and
`reserved`: 9 `fit_mode`, 10 `virtual_canvas_width`, 11 `monitor_offset_x`, 13 `pixels_per_second`,
14 `per_monitor_mode`, 15 `movement_type`, 16–20 `*_json`, 22 `virtual_canvas_height`,
23 `monitor_offset_y` (all covered by `layout_json` / `scene_json`; 2026-10-14 grep: outside the
cross-screen path only a log line in `WallpaperSyncClient` reads `fit_mode`). `CrossScreenApplyRequest` replaces its
five `*Json` strings with `SceneJson`; `RemotePlaybackHost` = deserialize → rewrite paths → apply.

**Content:** `SceneAssets.CollectPaths` walks every layer's `AnimationPath` + `AdditionalAnimationPaths`
plus the background image (roles unchanged: `primary` = layer 0's file, `additional` = any other sprite
file; the client maps by `OriginalPath`). `RewriteToLocal(scene, map)` works on the whole scene:
missing additional image → that layer's primary (existing rule, keeps `hash % imageCount` stable);
missing background → solid color (existing); **missing layer primary → that layer `Enabled = false`**
on this machine, reported in the health strip. Throttle, prefetch, readiness unchanged.

**Version gate:** registration already compares versions but only flags `updateAvailable`. Add: the
server does not send `LOAD` / `PREFETCH` to a client whose version is older than the server's; the node
shows "needs update" in the health strip until auto-update brings it level.

## 5. Scene editor and preview

**`CrossScreenConfigViewModel`:** existing Content / Motion / Look / Effects fields become the editor for
the **selected layer** (tab XAML unchanged). Split `LoadFromConfig` / `BuildConfig` into
`LoadLayer(SceneLayer)` / `BuildLayer()` + the scene-level part (background, room units, monitors).
New: `ObservableCollection<LayerRow> Layers`, `SelectedLayerIndex`. On selection change: `BuildLayer()`
into the old index, `LoadLayer()` from the new. `BuildConfig()` flushes the selected layer, then assembles.
Validation, preview badge, dirty tracking, Suggest keep working through `BuildConfig` / `LoadFromConfig`.
`PhysicalUnits.ApplyRoomUnits` iterates layers.

**Layers strip** (`SceneEditorPanel.axaml`, between media strip and tabs):
```
Layers  [☑ Logo rain][☑ Fish][☐ Sponsor]   [+] [⧉] [✕] [◀] [▶]
┌ Content │ Motion │ Look │ Effects │ Background ┐
```
Chip click selects, checkbox enables, right-click renames; `+` add empty, `⧉` duplicate, `✕` remove
(not the last), `◀ ▶` reorder (left = bottom). Motion tab gains **Phase offset** and **Distribution**
(per layer); Look tab gains **Opacity**. Background stays scene-wide.

**`SceneChecks`** new warnings: video on a layer > 0; more than 6 layers or more than 2 pattern layers;
enabled layer without a file.

**Suggest / PlaylistBuilder:** still build single-layer scenes; Keep replaces the whole draft.

**`ScenePainter`:** single `_spritePath` / `_bitmap` → bitmap cache keyed by path (dropped when no longer
referenced). `Resolve` resolves each layer via the same `PhysicalUnits` helper as the planner. Sprite pass
loops over enabled layers with `elapsed + PhaseOffsetMs`, per-layer canvas (Sequential = shared layout,
Simultaneous = tile's own width, offset 0) and opacity. `HasSprite` = any enabled layer with a file. Status
line describes the selected layer.

**Other:** playlist row summary "Logo rain + 2 layers"; presets and tray Quick start store the whole
scene and need no change.

## 6. Determinism

Nothing new. Each layer's position is `MovementCalculator(layer config, elapsed + PhaseOffsetMs, …)`;
pattern, colors and effects are per layer with their own seeds. Layers never read each other's state;
no `Random` / `DateTime.Now` / per-machine state enters the per-layer paths. The video and A* exceptions
are layer-0-only and unchanged.

## 7. Tests

- `LegacySceneMigrationTests` (new): legacy JSON → one layer with all fields; `Layers` JSON untouched;
  saved JSON has no legacy keys; every repo scene file loads to the same layer as before the rewrite (golden).
- `ConfigJsonRoundtripTests`: 3-layer scene (pattern + Simultaneous static + phase-offset sprite).
- `ContentPipelineTests`: `CollectPaths` over all layers, duplicates once; `RewriteToLocal` disables a
  layer with a missing primary, leaves the others.
- `PhysicalCanvasTests` / planner: cm resolved per layer, distribution kept per layer.
- `LayerScheduleTests` (new): a layer's position at `elapsed + phase` equals `MovementCalculator` at the
  shifted time; adding / removing layer A does not change layer B's position.
- Existing determinism guards (`MovementCalculator*`, `PatternLayout`, `SpriteEffects`, `RandomWalkMemo`)
  pass unchanged — the behavior-neutral check for player step 1.
- Test mode: `parity-matrix` and `smoke-local` pixel-identical before / after player step 1; new
  `TestScenarios/showcase/scenes/layers-party.json` (the four-layer example) added to `parity-matrix`.

## 8. Rollout

Worktree, branch `feat/scene-layers`, one commit per step, each step builds and passes tests:

1. Model (`SceneLayer`, `Opacity`, migration), consumers, repo scene files rewritten, tests.
2. Player step 1 — single `LayerRuntime`, behavior-neutral.
3. Player steps 2–3 + IPC — N layers, per-layer distribution.
4. Transport — `scene_json`, proto cleanup, version gate, `RemotePlaybackHost`.
5. Editor Layers strip + VM split + multi-layer `ScenePainter`.
6. Docs — this spec's status, `SOFTWARE_ARCHITECTURE.md`, CLAUDE.md capabilities, `RECENT_UPDATES.md`,
   roadmap Tier 2.3.

## 9. Performance budget

Per node: N layers × (cells or 1) draw calls per frame. The pattern path already draws hundreds of cells
at 60 fps; a few extra single-sprite layers are negligible. Soft limits via `SceneChecks`: 6 layers,
2 pattern layers; video only on layer 0.

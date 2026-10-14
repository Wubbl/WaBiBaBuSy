# Scene Layers (slice 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A scene is a background plus N image/GIF layers, each with its own source, movement, look, effects, opacity, distribution and phase offset — synchronized on every seat and in the room preview.

**Architecture:** `CrossScreenConfig.Layers` replaces the single `Animation`/`Movement`/`DistributionMode` (legacy JSON migrates on read through `[JsonExtensionData]` + `IJsonOnDeserialized`). The player keeps its proven single-sprite code and swaps each layer's state (`LayerRuntime`) into the existing statics before update/draw; layer 0 is the resting state, so every non-layer code path (probes, debug overlay, IconZone, video) sees exactly what it saw before. Transport carries one `scene_json`.

**Tech Stack:** .NET 9, System.Text.Json, Vortice Direct2D, Avalonia 11 + CommunityToolkit.Mvvm, gRPC, xUnit.

**Spec:** [`2026-09-17-scene-layers-design.md`](2026-09-17-scene-layers-design.md) (revised 2026-10-14)

## Global Constraints

- Determinism invariant (CLAUDE.md): no `Random` without shared seed, no `DateTime.Now`, no per-machine state in per-layer math. A layer never reads another layer's state.
- Layer 0 alone: video, IconZone A* path following, IconZone clip mask, ThreeZone corridor clamp.
- Draw order = list order (index 0 bottom). No `ZOrder` field.
- Legacy keys `Animation`, `Movement`, `DistributionMode`, `AnimationSpeedPxPerSecond` must load forever (any case, enum as name or number).
- Players only see pixels: cm resolved per layer by `PhysicalUnits` before IPC / broadcast.
- Zero compiler errors and `dotnet test WaBiBaBuSy.Tests` green after every task.

## Review Focus

1. **Legacy JSON with lower-case keys** (`"animation": {...}`, scene files are read case-insensitively) → migrates the same as PascalCase. Test in Task 1.
2. **Scene with zero layers** (background only, or all layers removed in JSON) → plays the background on every seat, no crash, remote stop still works. Tests in Task 1 (load) and Task 4 (planner); player guards in Task 2.
3. **Layer whose file is missing on a remote** → that layer disabled, others play. Test in Task 3.
4. **Video on layer > 0** → player skips it with a warning; `SceneChecks` warns in the editor. Test in Task 6 (checks).
5. **Exact-frame probe on a multi-layer scene** → all layers drawn, every layer's facing state restored afterwards. Covered by the Task 2 swap design; verified with the `layers-party` scene in Task 7.

---

### Task 1: Model, migration, consumers

**Files:**
- Create: `WaBiBaBuSy.Models/Wallpaper/SceneLayer.cs`, `WaBiBaBuSy.Models/Wallpaper/LegacySceneMigration.cs`, `WaBiBaBuSy.Tests/LegacySceneMigrationTests.cs`
- Modify: `WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs`, `PhysicalUnits.cs`, `SceneChecks.cs`, `Content/ContentAssetRef.cs`, `Testing/SceneFile.cs`, `Suggest/*`, `Topology/CrossScreenPlanner.cs` (compile only; behavior in Task 4), every compile error in Core/UI/Tests
- Rewrite: `TestScenarios/scenes/*.json`, `TestScenarios/showcase/scenes/*.json` (load + save)

**Interfaces (produced):**
```csharp
public class SceneLayer { string Name; bool Enabled = true; float Opacity = 1f;
    AnimationLayerConfig Animation; MovementConfig Movement;
    AnimationDistributionMode Distribution = Sequential; long PhaseOffsetMs; }
CrossScreenConfig { BackgroundLayerConfig Background; List<SceneLayer> Layers; List<string> SelectedMonitorIds;
    [JsonExtensionData] Dictionary<string, JsonElement>? Legacy; }
CrossScreenConfig.FirstLayer            // Layers.Count > 0 ? Layers[0] : null — convenience for single-sprite callers
CrossScreenConfig.Single(SceneLayer)    // static factory: new scene with one layer
LegacySceneMigration.Apply(CrossScreenConfig)
PhysicalUnits.ResolveLayer(SceneLayer, float refPpcm) -> SceneLayer   // replaces ResolveForCanvas
PhysicalUnits.ApplyRoomUnits(CrossScreenConfig, bool)                // iterates layers
SceneAssets.CollectPaths(CrossScreenConfig)                          // all layers
```

- [ ] Write `LegacySceneMigrationTests`: PascalCase legacy → one layer (all fields incl. `DistributionMode` as name and as number, layer name = file name w/o extension); lower-case legacy keys; JSON with `Layers` untouched and legacy keys ignored when `Layers` present; serialized output has no legacy keys; legacy with no `Animation`/`Movement` → zero layers; every repo scene file loads with ≥ 1 layer and its first layer's `AnimationPath` ends with the same file name as the pre-rewrite JSON (golden captured via `JsonNode` on the git `HEAD` copy is overkill — compare against the expected list embedded in the test).
- [ ] Run: `dotnet test WaBiBaBuSy.Tests --filter LegacySceneMigrationTests` → FAIL (types missing).
- [ ] Implement model + migration (static `JsonSerializerOptions { PropertyNameCaseInsensitive, JsonStringEnumConverter }`, case-insensitive key lookup).
- [ ] Fix every compile error: single-sprite readers use `FirstLayer`; writers (`SceneRecipes`, `SceneSuggester`, `PlaylistBuilder`, `PresetSeeder`, tests) build `Layers[0]`. `SceneFile.Load` null checks + absolute paths per layer.
- [ ] Rewrite repo scene files via a throwaway test-host snippet (load with `SceneFile.JsonOptions`, re-save with relative paths preserved — write `Layers` keeping the original relative strings).
- [ ] `dotnet build` + `dotnet test` → green. Commit `feat(layers): SceneLayer model, legacy migration on read, consumers on Layers`.

### Task 2: Player — `LayerRuntime` swap + N layers + per-layer distribution + IPC

**Files:**
- Create: `WaBiBaBuSy.Player.D2D/Program.Layers.cs`
- Modify: `WaBiBaBuSy.Player.D2D/Program.cs`, `WaBiBaBuSy.Player.Common/Messages/PlayerCommandLoadAnimation.cs`, `WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs`, `WaBiBaBuSy.Models/Show/SyncTiming.cs` (if `ApplyNodePhase` needs a raw-elapsed overload)

**Interfaces:**
```csharp
PlayerCommandLoadAnimation { List<SceneLayer> Layers; BackgroundLayerConfig BackgroundConfig; NodeLayout? Layout; int MonitorIndex; ...zone fields }
  // removed: AnimationConfig, MovementConfig, VirtualCanvasWidth/Height, MonitorOffsetX/Y, PerMonitorMode, NodeOrder
D2DCompositionService.InitializeAsync(VirtualCanvasManager, BackgroundLayerConfig, IReadOnlyList<SceneLayer> layers,
    Rectangle actualMonitorBounds, int monitorIndex, NodeLayout layout, CancellationToken)
sealed class LayerRuntime   // Program.Layers.cs: one field per swapped static + SceneLayer Config
static void ActivateLayer(int index)   // SaveInto(_layers[_activeLayer]); LoadFrom(_layers[index])
static NodeLayout LayoutFor(AnimationDistributionMode)  // Sequential → _sequentialLayout; Simultaneous → own monitor, offset 0, scale 1
```

Swapped statics (complete list; `ponytail:` comment on the class names the ceiling — new per-sprite statics must join it):
`_animationConfig _movementConfig _d2dGifFrames _d2dGifDelays _d2dGifTotalDurationMs _contentNativeWidth _contentNativeHeight _gifSpeedMultiplier _d2dFramesPerSource _d2dDelaysPerSource _d2dTotalDurationPerSource _sourceDimensions _animWidth _animHeight _animX _animY _animVirtualX _fitMode _centerInitialPosition _rotateWithPath _animRotationRad _tileAlignStepX _isTravelingMode _endlessCellOffsetI _facingLeft _prevFacingX _hasPrevFacingX _lastFlipX _traverseCount _maskZones _layout _cw _ch _virtualCanvasWidth _virtualCanvasHeight _monitorOffsetX _canvasOffsetY _nodeOrder _perMonitorMode` + new `_layerOpacity _layerPhaseOffsetMs _layerFollowsBackground`.

- [ ] Make the four per-source lists non-readonly (swappable references).
- [ ] `Program.Layers.cs`: `LayerRuntime`, `SaveInto/LoadFrom`, `ActivateLayer`, `LayoutFor`, `ApplyLayout(NodeLayout)` (the existing post-layout block from `HandleLoadAnimationCommand`), `DrawExtraLayers(long effectiveElapsed, long rawElapsed, bool live)`, `DisposeExtraLayers()`, `SnapshotExtraLayers()/RestoreExtraLayers()`.
- [ ] `HandleLoadAnimationCommand`: dispose extras; apply layer 0's layout; load layer 0 sprite (extracted `LoadActiveLayerSprite(AnimationLayerConfig, bool allowVideo)` = today's branch bodies minus `InitializeBackground`), `InitializeBackground` once (layer 0 active, as today); per extra layer: fresh lists, `ApplyLayout`, `LoadActiveLayerSprite(..., allowVideo:false)`, tile-align, `SaveInto`. `_useNativeD2DComposition` true when any layer loaded. Empty layer list → background only.
- [ ] Opacity: `alpha *= _layerOpacity` at the top of `DrawCellWithOptionalGrading`.
- [ ] Phase: `DrawNativeGifScene` and the video branch update/draw at `elapsed + _layerPhaseOffsetMs`; `ShouldDrawAnimation` stays on unshifted elapsed. Extra layers: own node phase from raw elapsed (`SyncTiming.ApplyNodePhase(raw, _nodeOrder, delay, _perMonitorMode)`).
- [ ] `_layerFollowsBackground` gates `FollowsIconPath` branch, `ClampToCorridor`, corridor centering in `CalculateAnimationLayout`; `_maskZones` only for layer 0.
- [ ] Render loop: video branch when `_useNativeD2DVideo`, then GIF branch when `_useNativeD2DComposition`; both call `DrawExtraLayers` after layer 0; `try/finally ActivateLayer(0)`.
- [ ] Exact frames: `SnapshotExtraLayers` before, restore after; `DrawExtraLayers(elapsed, elapsed, live:false)`.
- [ ] `D2DCompositionService` new signature; old callers (simple playback in `ServerPlaybackHost`/`RemotePlaybackHost` ~l.870/385) pass `[new SceneLayer { Animation = a, Movement = m }]` and a synthesized single-monitor layout.
- [ ] Build; run determinism tests; commit `feat(layers): player renders N layers via LayerRuntime swap, per-layer distribution`.

### Task 3: Content assets per layer

**Files:** `WaBiBaBuSy.Models/Content/ContentAssetRef.cs`, `WaBiBaBuSy.Tests/ContentPipelineTests.cs`

**Interfaces:** `SceneAssets.RewriteToLocal(CrossScreenConfig scene, IReadOnlyDictionary<string,string> originalToLocal) -> CrossScreenConfig` (clone; replaces the tuple overload).

- [ ] Tests: `CollectPaths` over 3 layers (layer 0 file = `primary`, others `additional`, duplicates once); `RewriteToLocal` maps all layers; missing additional → that layer's primary; missing layer primary → `Enabled=false`, other layers untouched; missing background → solid.
- [ ] Implement; green; commit `feat(layers): collect and rewrite assets for every layer`.

### Task 4: Planner + server apply + transport

**Files:** `WaBiBaBuSy.Models/Topology/CrossScreenPlanner.cs`, `WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs`, `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto`, `WaBiBaBuSy.Core/Services/WallpaperSyncCoordinator.cs`, `WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs`, `WaBiBaBuSy.Models/Wallpaper/CrossScreenApplyRequest.cs`, `WaBiBaBuSy.UI/Services/RemotePlaybackHost.cs`, `WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs` (version gate), `WaBiBaBuSy.Core/Services/Testing/*` (`SceneStartInfo`), tests `PhysicalCanvasTests.cs`, `ServerPlaybackLogicTests.cs`

**Interfaces:**
```csharp
CrossScreenPlan.EffectiveLayers : IReadOnlyList<SceneLayer>   // replaces EffectiveMovement/EffectiveAnimation/PerMonitor
CrossScreenPlan.EffectiveScene : CrossScreenConfig            // Background + EffectiveLayers
proto SyncParameters: string scene_json = 33; reserved 9,10,11,13,14,15,16,17,18,19,20,22,23;
CrossScreenApplyRequest.SceneJson : string (replaces PatternJson, ColorGradingJson, MovementJson, AnimationJson, BackgroundJson + loose canvas fields)
WallpaperSyncCoordinator.StartCrossScreenD2DOnClientAsync(clientId, contentId, CrossScreenConfig scene, NodeLayout? layout,
    long sharedStartTimestampMs, int targetMonitorIndex, IReadOnlyList<ContentAssetRef> assets)
```

- [ ] Planner tests: cm resolved per layer; each layer keeps `Distribution`; zero-layer scene plans (empty `EffectiveLayers`, layout still built).
- [ ] Planner; `ServerPlaybackHost.ApplyCrossScreenConfigAsync`: IconZone global path for layer 0 (JSON clone + `PrecomputedPath`, fixes the dropped `Effects`/`FaceTravelDirection`), lap subscription when layer 0 is Sequential, one `InitializeAsync(… layers, layout)` per monitor, `pixelsPerSecond` from layer 0 (0 when none), content id = first layer file or the scene JSON hash when no layers, remote sends with `scene_json`.
- [ ] Proto + coordinator + `WallpaperPlaybackService` mapping + `RemotePlaybackHost`: deserialize → `RewriteToLocal` → `InitializeAsync`.
- [ ] Version gate: in the LOAD / PREFETCH send path skip clients whose registered version `IsNewerVersion(client, server)` is true; health strip text "needs update".
- [ ] `SceneStartInfo`: `EffectiveLayers` instead of `EffectiveMovement`/`PerMonitor`; test-mode marker checks use layer 0.
- [ ] Build + tests; commit `feat(layers): plan, apply and broadcast every layer via scene_json`.

### Task 5: Room preview (`ScenePainter`)

**Files:** `WaBiBaBuSy.UI/Controls/ScenePainter.cs`, its callers (`RoomView`/preview control)

- [ ] Bitmap cache `Dictionary<string, Bitmap?>` replacing `_spritePath/_bitmap`; `SetScene(CrossScreenConfig?)` keeps only referenced paths (video layers use the existing thumbnail lookup for layer 0).
- [ ] `Resolve` → per layer via `PhysicalUnits.ResolveLayer`.
- [ ] Sprite pass loops enabled layers: elapsed + `PhaseOffsetMs`, layout per `Distribution` (Simultaneous: tile width, offset 0), opacity via `ctx.PushOpacity`.
- [ ] `HasSprite` = any enabled layer with a file. Status line = selected layer (`SelectedLayerIndex` passed from the editor; live scene → layer 0).
- [ ] Build; manual check in the app; commit `feat(layers): room preview paints every layer`.

### Task 6: Scene editor Layers strip

**Files:** `WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs`, `WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml(.cs)`, `WaBiBaBuSy.Models/Wallpaper/SceneChecks.cs`, `WaBiBaBuSy.UI/ViewModels/PlaylistItemRow.cs`, tests `SceneChecks*`

**Interfaces:**
```csharp
public partial class LayerRow : ObservableObject { [ObservableProperty] string _name; [ObservableProperty] bool _enabled; SceneLayer Layer; }
CrossScreenConfigViewModel.Layers : ObservableCollection<LayerRow>; SelectedLayerIndex : int
  LoadLayer(SceneLayer) / BuildLayer() : SceneLayer; commands AddLayer, DuplicateLayer, RemoveLayer, MoveLayerDown(◀), MoveLayerUp(▶)
  new fields: LayerOpacity (Look), PhaseOffsetMs + Distribution already per layer (Motion)
SceneChecks: "Layer N is a video — only the bottom layer can play video", "More than 6 layers", "More than 2 pattern layers", "Layer N has no file"
```

- [ ] `SceneChecks` tests for the four warnings; implement.
- [ ] VM split + commands; switching layers flushes/loads; `BuildConfig` flushes selected layer.
- [ ] XAML strip (ItemsControl of chips with CheckBox + name, rename via context menu TextBox flyout, buttons); Opacity slider in Look; Phase offset NumericUpDown in Motion; Distribution combo moved under Motion (per layer).
- [ ] Playlist row summary "`<first layer name>` + N layers".
- [ ] Build + tests + manual GUI check; commit `feat(layers): Scene editor layers strip`.

### Task 7: Showcase scene, scenarios, docs

- [ ] `TestScenarios/showcase/scenes/layers-party.json` (logo rain pattern, bubbles ×3 bounce 60 % phase +7 s, fish linear face-travel trail, sponsor static Simultaneous 50 %) using existing `TestScenarios/assets`; add to `parity-matrix.json`.
- [ ] Run `--test-run TestScenarios\smoke-local.json --exit` locally.
- [ ] Docs: spec status → Implemented (slice 1); roadmap Tier 2.3; `SOFTWARE_ARCHITECTURE.md` (scene model, player swap, scene_json); `CLAUDE.md` capabilities line; `RECENT_UPDATES.md`; `2026.07_OPEN_ITEMS.md` validation item.
- [ ] `./tools/graphify-update.ps1`; commit `docs(layers): scene layers slice 1`.

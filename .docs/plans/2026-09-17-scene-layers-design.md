# Scene Layers (multiple sprites per scene) — Design

**Created:** 2026-09-17 · **Status:** Proposal · **Roadmap:** Tier 2.3 (XL)
**Solves:** F4 in [`2026-09-17-lan-party-roadmap.md`](2026-09-17-lan-party-roadmap.md)
**Depends on:** show-reliability design (multi-file transfer), ideally authoring design (preview)

---

## 1. Goal

A **scene** is a background plus **N layers**, each an independent sprite (or pattern grid) with
its own source file(s), movement, color grading, size, z-order, phase offset and optional spawn
window. Example for the party:

| z | Layer | Source | Movement | Look |
|---|---|---|---|---|
| 0 | Logo rain | clan logo PNG | Linear, Endless, 40 px/s | Pattern Fill, TravelingRainbow, 30 % colored |
| 1 | Bubbles | bubble GIF ×3 | Bounce 60°, 120 px/s, phase +7 s | none, 60 % opacity |
| 2 | The fish | fish GIF | Linear around the ring, 25 cm/s | face travel, trail |
| 3 | Sponsor | sponsor PNG | Static, bottom-right of each node (Simultaneous) | 50 % opacity, spawn 20:00–20:05 |

Today `CrossScreenConfig` has exactly one `Animation` + `Movement`, and the player keeps one
layer's state in static fields (`_animationConfig`, `_movementConfig`, `_d2dFramesPerSource`,
`_animX/_animY`, `_tileAlignStepX`, …). This design turns that into a list without changing any
deterministic function.

## 2. Data model

```csharp
public class SceneLayer
{
    public string Name { get; set; } = "Layer";
    public bool Enabled { get; set; } = true;
    public int ZOrder { get; set; }                       // draw order, low first
    public float Opacity { get; set; } = 1f;
    public AnimationLayerConfig Animation { get; set; } = new();   // source(s), size, fit, pattern, colors
    public MovementConfig Movement { get; set; } = new();
    public AnimationDistributionMode Distribution { get; set; } = Sequential; // per layer!
    public long PhaseOffsetMs { get; set; }               // elapsed' = elapsed + PhaseOffsetMs
    public long? SpawnAtMs { get; set; }                  // null = from start
    public long? DespawnAtMs { get; set; }                // null = forever
    public LayerEffects Effects { get; set; } = new();    // see effects design (trail, facing, pulse…)
}

public class CrossScreenConfig
{
    public BackgroundLayerConfig Background { get; set; } = new();
    public List<SceneLayer> Layers { get; set; } = new();
    // Legacy single-layer properties stay for one release, marked [Obsolete]:
    public AnimationLayerConfig Animation { get; set; } = new();
    public MovementConfig Movement { get; set; } = new();
    public AnimationDistributionMode DistributionMode { get; set; }
    public int AnimationSpeedPxPerSecond { get; set; }   // remove (duplicate of Movement.Speed)
    public List<string> SelectedMonitorIds { get; set; } = new();
}
```

**Normalization rule** (one helper, `SceneNormalizer.Normalize(config)`): if `Layers` is empty,
wrap `Animation + Movement + DistributionMode` into a single layer. Every consumer (apply path,
player, preview, playlist summary) calls it, so old JSON (playlist items) keeps working and the
legacy properties can be deleted later.

Per-layer `Distribution` is the interesting freedom: the sponsor logo is `Simultaneous` (same
spot on every node) while the fish is `Sequential` (travels). The player already supports both;
it is just `canvasWidth/offset` selection per layer.

## 3. Player refactor (`Program.cs`)

This is the bulk of the work. Extract the per-layer static state into a class:

```csharp
sealed class LayerRuntime : IDisposable
{
    public SceneLayer Config;
    public List<ID2D1Bitmap[]> FramesPerSource; public List<List<int>> DelaysPerSource; …
    public int AnimWidth, AnimHeight; public float TileAlignStepX; public bool IsTraveling;
    public float X, Y, RotationRad; public int EndlessCellOffsetI;
    public VideoSource? Video;                 // LibVLC per video layer (one player each)
    public void Load(ID2D1DeviceContext ctx);  // = today's HandleLoadAnimationCommand body
    public void UpdatePosition(long elapsedMs, NodeLayout layout, CorridorConstraint? c);
    public void Draw(ID2D1DeviceContext ctx, long elapsedMs, ColorMatrixEffect fx);
}
```

`RenderLoop` becomes: draw background → `foreach layer in ZOrder: if spawned(elapsed) →
UpdatePosition(elapsed + Phase) → Draw`. IconZone path-following and the clip mask apply to the
layers that opt in (`AnimationLayerConfig.FollowIconPath`, default true for the first layer to keep
today's behavior).

Static → instance conversion should be done mechanically, in this order, each step building and
running the current single-layer configs unchanged:
1. Move the source caches + layout fields into `LayerRuntime`, keep a single `_layers[0]`.
2. Move position/update/draw. Remove `_d2dGifFrames`/`_d2dGifDelays` duplicates (the
   `_d2dFramesPerSource[0]` path already covers them).
3. Loop over N layers.
4. Per-layer LibVLC (video layers) — `_vlcPlayer` becomes `LayerRuntime.Video`. Cap video layers
   at 2 per node in the UI (decode cost).

`PlayerCommandLoadAnimation` gets `List<SceneLayer> Layers` (+ `NodeLayout`); `AnimationConfig` /
`MovementConfig` stay for one release and are normalized on receive.

## 4. Apply path & transport

- `ApplyCrossScreenConfigAsync`: builds the canvas once (seat map), then for **each layer** picks
  `canvasWidth/offset` by that layer's `Distribution` and, if IconZone + Sequential, computes the
  global A* path for the layers that follow it (reuse the existing block).
- gRPC: replace the five per-part JSON fields (`pattern_json … background_json`) with one
  `scene_json` = the normalized `CrossScreenConfig` (minus `SelectedMonitorIds`) plus
  `layout_json`. The client-side reconstruction in `ApplyCrossScreenD2DFromRemoteAsync` collapses
  to "deserialize, rewrite content paths, apply." Keep the old fields populated for one release.
- Content: every layer's `GetAllAnimationPaths()` plus the background image become content ids;
  the client downloads all before applying (show-reliability design §3). `CrossScreenApplyRequest`
  carries `Dictionary<string contentId, string localPath>`.

## 5. Determinism

Nothing new. Each layer's position is `MovementCalculator(config, elapsed + phase, …)`; pattern and
colors are per layer with their own seeds. Layers never read each other's state. Spawn windows
compare `elapsed` (shared clock) against constants. The only rule to enforce in code review: no
layer state may leak into another layer's math.

## 6. UI

In the tabbed dialog (authoring design): a **Layers** panel on the left (list with enable checkbox,
z-order drag, add/duplicate/remove, "▲▼"), and the existing Content / Motion / Look tabs edit the
*selected* layer. Background stays a scene-level tab. Preview draws all layers.

Presets can be per layer ("Bubbles", "Logo rain") as well as per scene — a layer preset is just a
`SceneLayer` JSON; "Add layer from preset" is the fast way to compose scenes.

## 7. Performance budget

Per node: N layers × (cells or 1) draw calls per frame. The pattern path already draws hundreds of
cells at 60 fps; two or three extra single-sprite layers are negligible. Set soft limits in the
UI: 6 layers, 2 pattern layers, 2 video layers, and show the estimated draw count.

## 8. Tests

- `SceneNormalizerTests`: legacy config → one layer, byte-identical apply payload.
- `ConfigJsonRoundtripTests`: extend with a 3-layer scene.
- `LayerScheduleTests`: spawn/despawn windows and phase offsets are pure functions of elapsed.
- Manual: the four-layer example above on two local monitors; then remote.

## 9. Rollout

1. Model + normalizer + roundtrip tests (no behavior change).
2. Player refactor steps 1–3 with a single layer (behavior-neutral; verify with the existing
   configs and the long-run tests).
3. `scene_json` transport + multi-content transfer.
4. UI layers panel + preview.
5. Video layers, limits, presets.

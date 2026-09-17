# Physical Canvas (heterogeneous monitors) — Design

**Created:** 2026-09-17 · **Status:** Proposal · **Roadmap:** Tier 1.2 (+ Tier 0.1)
**Solves:** F3 in [`2026-09-17-lan-party-roadmap.md`](2026-09-17-lan-party-roadmap.md)

---

## 1. Problem

Twenty friends bring twenty different monitors. Today:

1. **Vertical desync.** Each player receives `VirtualCanvasHeight = actualMonitorBounds.Height`
   (`D2DCompositionService.cs:155`) and `UpdateAnimationPosition` passes its own `_height` as
   `canvasHeight` into `MovementCalculator`. `centerY`, SineWave amplitude clamps and Bounce
   ranges therefore differ per node. A sprite crossing from a 1080-px to a 1440-px monitor jumps.
2. **Physical size and speed differ.** The canvas is in pixels. `TargetHeight = 720` is 720 px
   everywhere: 18 cm on a 24" 1080p panel, 13 cm on a 27" 1440p panel. Speed in px/s means the
   fish visibly accelerates on the high-DPI monitor and shrinks. `PixelsPerCm` (already reported
   by every client since 2026-07) is used only for bezel gaps.

## 2. Approach: reference pixels + per-node scale

Keep every deterministic function (`MovementCalculator`, `PatternLayout`, `ColorGrader`) exactly
as it is, operating in **reference pixels** (rpx). Give each node one extra number, `Scale`, and
let the player apply it as a D2D transform. This is the smallest change that makes the math
physically consistent, and it does not touch the determinism invariant.

```
refPpcm          = PixelsPerCm of the reference monitor (server primary by default; configurable)
node.Scale       = node.PixelsPerCm / refPpcm             // 1.0 on the reference monitor
node.WidthRpx    = node.WidthPx  / node.Scale              // its slice on the canvas, in rpx
node.HeightRpx   = node.HeightPx / node.Scale
CanvasWidth      = Σ WidthRpx + gaps (gaps are cm → rpx via refPpcm, not via the next node's ppcm)
CanvasHeight     = max HeightRpx  (or per row for Parallel)
OffsetY(node)    = anchor-dependent (see §3)
```

A sprite with `TargetHeightCm = 15` becomes `15 × refPpcm` rpx; the player draws it at
`rpx × Scale` device pixels — the same 15 cm on every monitor. Speed entered in cm/s becomes
`cm/s × refPpcm` rpx/s and is the same cm/s everywhere. Both are the same fish to the eye.

Node with `PixelsPerCm == 0` (unknown DPI) falls back to `Scale = 1` and a warning badge in the
topology.

## 3. Vertical anchoring

With `CanvasHeight = max HeightRpx`, shorter monitors must decide where their slice sits:

```
VerticalAnchor: Center (default) | Top | Bottom
OffsetY(node)  = Center: (CanvasHeight − HeightRpx) / 2
                 Top:    0
                 Bottom: CanvasHeight − HeightRpx
```

Center is right for tables (monitors stand on the same surface at roughly the same eye height,
their centers align better than their bottoms once stands differ). The player subtracts `OffsetY`
just as it subtracts `OffsetX`. Roadmap 0.1 is this section alone with `Scale = 1`.

## 4. Config changes

`AnimationLayerConfig`
- `SizeUnit` : `Pixels` (legacy default) | `Centimeters`
- `TargetHeightCm` (float, used when `SizeUnit == Centimeters`); `TargetHeight` stays for px.

`MovementConfig`
- `SpeedUnit` : `PixelsPerSecond` (legacy default) | `CentimetersPerSecond`
- `SpeedCmPerSecond` (float). The dialog shows both and converts live.

`CrossScreenConfig`
- `Canvas` : new `CanvasSettings { Mode = Pixels | Physical, VerticalAnchor, ReferenceClientId? }`.
  `Pixels` reproduces today's layout bit-for-bit (`Scale = 1`, gaps via next node's ppcm).

`NodeLayout` (from the seat-map design) already reserves `Scale`; add `OffsetY` (present) and
`RefPixelsPerCm` so the player can convert cm values itself if needed for effects.

## 5. Player changes

- `HandleLoadAnimationCommand`: read `Scale`, `OffsetY`; compute `_widthRpx = _width / Scale`,
  `_heightRpx = _height / Scale`. All layout (`CalculateAnimationLayout`) and movement math run in
  rpx using `_widthRpx/_heightRpx` and `CanvasWidth/CanvasHeight`.
- `RenderLoop` / `DrawAnimationLayer`: `SetTransform(Matrix3x2.CreateScale(Scale))` around the
  animation layer draw (mirroring from the seat-map design composes with it). Backgrounds that
  are per-monitor (icon zones, solid color) stay in device pixels; `ThreeZone` corridor values
  are user px today — convert them once on load (`CorridorTopPx / Scale`) so the corridor is at the
  same physical height on all nodes.
- GIF frames are uploaded at native size and drawn scaled anyway (`ComputeDrawSizeForSource`), so
  no extra resource work. Bilinear interpolation is already the D2D default.
- IconZone A* global path is computed in rpx on the server and mapped per node with the same
  transform; icon rectangles detected locally are converted to rpx (`/Scale`) before feeding the
  planner.

## 6. Where the DPI comes from

`MonitorInfo.pixels_per_cm` is reported at registration via `GetDpiForMonitor`
(`MonitorDpiHelper` already asks for `MDT_RAW_DPI` first and falls back to `MDT_EFFECTIVE_DPI`).
Two caveats to surface in the topology UI:
- Raw DPI is derived from the panel's EDID; some monitors/KVMs/adapters report nothing, and the
  effective-DPI fallback then reflects the user's 125 % scaling instead of physical density.
  Record which source produced the value (`raw | effective | manual`) in `MonitorInfo` and show it
  on the node so a suspicious readout is visible.
- Manual override per seat: `Seat.PanelDiagonalInch` (optional). The seat-map dialog offers
  "enter your monitor's diagonal in inches" and derives ppcm from resolution + aspect ratio.

## 7. UI

- Animation dialog: size field with unit toggle (px / cm), speed field with unit toggle (px/s /
  cm/s), live derived text ("= 15.0 cm on the reference monitor", "lap ≈ 62 s").
- Topology node: shows `WidthCm × HeightCm` and `Scale` (e.g. "×0.83") when physical mode is on;
  warning badge when DPI is unknown.
- Canvas settings live with the seat map (they describe the room, not one animation).

## 8. Tests

- `PhysicalLayoutTests`: two nodes 1920×1080@44 ppcm and 2560×1440@43 ppcm → equal physical
  widths (≈ 43.6 cm vs 59.5 cm) reflected in rpx; `Scale` ≈ 1.0 and 0.98; sprite 15 cm → same cm on
  both after `× Scale`.
- `VerticalAnchorTests`: Center/Top/Bottom offsets for a 1080/1440 pair.
- `LegacyModeTests`: `CanvasSettings.Mode = Pixels` reproduces `VirtualCanvasManager` output
  exactly for the existing fixtures.
- Movement long-run tests unchanged (math is unit-agnostic).

## 9. Rollout

1. Roadmap 0.1: canvas height + `OffsetY` with `Scale = 1` (fixes the jump on mixed heights).
2. `Scale` in `NodeLayout` + player transform, physical mode off by default.
3. cm units in the dialog; EDID lookup; per-seat override.
4. Flip the default to Physical once verified on two different monitors at the desk.

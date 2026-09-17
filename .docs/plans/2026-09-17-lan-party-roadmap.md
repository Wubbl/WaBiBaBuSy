# LAN-Party Roadmap — Analysis & Improvement Plan (2026-09-17)

**Target scenario:** 20 machines at a LAN party, arranged as **2 rows × 10 monitors**, running one
coherent animation that travels from screen to screen around all 20.
**Author's brief:** configuration and animation-authoring possibilities are the main pain point;
multi-machine E2E testing is still pending.
**Status:** Analysis + design proposals. Nothing here is implemented. Each Tier-1/2 item links to its
own design doc in this folder.

---

## 1. How this was produced

Full read of the animation configuration and distribution path, verified against code (not docs):

| Area | Files read |
|---|---|
| Config model | `WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs`, `PatternConfig.cs`, `ColorGradingConfig.cs`, `Playlist.cs`, `PlaylistScheduler.cs` |
| Deterministic math | `MovementCalculator.cs`, `PatternLayout.cs`, `ColorGrader.cs` |
| Canvas layout | `WaBiBaBuSy.WallpaperEngine/Composition/VirtualCanvasManager.cs`, `ScreenMapping.cs`, `D2DCompositionService.cs` |
| Apply path | `MainWindowViewModel.ApplyCrossScreenConfigAsync` (server side), `ApplyCrossScreenD2DFromRemoteAsync` (client side), `WallpaperSyncCoordinator.StartCrossScreenD2DOnClientAsync`, `WallpaperPlaybackService` LOAD handling |
| Player | `WaBiBaBuSy.Player.D2D/Program.cs` — `HandleLoadAnimationCommand`, `HandleStartAnimationCommand`, `UpdateAnimationPosition`, `DrawAnimationLayer` |
| UI | `CrossScreenConfigDialog.axaml` + `CrossScreenConfigViewModel.cs`, `MainWindow.axaml(.cs)` topology, `PlaylistDialog`, `ClientNodeViewModel` |
| Transport | `wabibabusy.proto` (`SyncParameters`, `ConnectedClient`, `ClientOrderUpdate`), `WallpaperSyncService.RegisterClient`, `WallpaperSyncClient` identity/download |
| Tracking docs | `2026.07_OPEN_ITEMS.md`, `2026.07_FEATURE_OVERVIEW.md`, `RECENT_UPDATES.md`, playlist design/plan |

## 2. What already works (and is worth protecting)

- **The determinism model is right.** Position, pattern layout and colors are pure functions of
  `(config, sharedStartTimestamp, elapsedMs, cell identity)`. No per-frame network traffic; a
  late joiner lands mid-animation by back-dating the epoch. This scales to 20 nodes for free.
- **Tier 1 reliability landed** (2026-07): full configs travel as JSON, clock-offset estimation,
  reconnect + session resume, dead-client sweep, remote DPI reporting.
- **Playlist / Party Mode** exists (rotation with per-item duration, shuffle, lap-snap).
- **Rich single-sprite vocabulary**: 6 movement types, pattern grid, 8 color modes, 5 background
  modes incl. icon-avoiding A* paths.

Everything below builds on this; nothing proposes changing the determinism invariant.

## 3. Findings — what blocks the 2×10 scenario today

Ranked by impact on "20 machines, two rows, one animation goes around."

### F1. The canvas is one straight line (blocking)
`VirtualCanvasManager.CalculateLayout` lays nodes out left-to-right with `Y = 0` for all. There is
no row concept, no traversal order across rows, and no way to express "end of row 1 connects to end
of row 2." For two facing rows the natural figure is a **ring** (down one row, back along the other,
forever). Today you can only fake it by ordering all 20 in one line, and the sprite visibly
teleports from node 20 back to node 1.
→ Design: [`2026-09-17-seat-map-and-ring-topology-design.md`](2026-09-17-seat-map-and-ring-topology-design.md)

### F2. Topology order and client identity do not survive restarts (blocking at scale)
- Server assigns `OrderPosition = _nextClientOrder++` on registration
  (`WallpaperSyncService.cs:158`) and keeps it only in memory. Restart the server → 20 nodes come
  back in connection order and someone has to drag 20 nodes again.
- Client identity is a GUID assigned by the server (`WallpaperSyncClient.cs:289`), nulled on
  disconnect (`:178`), never persisted in `ClientConfiguration`. Restart a client app → new node,
  order/distance lost, session-resume cannot match it.
With 20 machines and a long evening this is the first thing that will hurt.
→ Design: seat map (same doc as F1), section "Persistent identity."

### F3. Mixed monitors break vertical alignment and physical speed (likely at any LAN party)
- The canvas height handed to each player is **that monitor's own height**
  (`D2DCompositionService.cs:155` → `VirtualCanvasHeight = actualMonitorBounds.Height`;
  `Program.cs UpdateAnimationPosition` passes `_height` as `canvasHeight`). A 1080p and a 1440p
  node compute different `centerY`, so a SineWave/Bounce sprite jumps vertically at the bezel.
- The canvas is in **pixels**; `PixelsPerCm` is used only for bezel gaps. Speed is px/s, so the
  same sprite moves at different cm/s and has different physical size on a 24" 1080p vs a 27"
  1440p monitor. Twenty friends' monitors will not match.
→ Design: [`2026-09-17-physical-canvas-design.md`](2026-09-17-physical-canvas-design.md)

### F4. One sprite per scene (main authoring limitation)
`CrossScreenConfig` holds exactly one `Animation` + one `Movement`. Multi-image only distributes
images into pattern cells or jitters them around one anchor. You cannot have "a big fish swimming
the ring **and** a slow rainbow logo grid behind it **and** small bubbles bouncing." The player
(`Program.cs`, 3474 lines) keeps all animation state in static fields for a single layer.
→ Design: [`2026-09-17-scene-layers-design.md`](2026-09-17-scene-layers-design.md)

### F5. No way to see what a config does without 20 machines (main authoring pain)
There is no preview. The config dialog is a 750px-tall scrolling form with ~60 fields; you press OK
and look at the real wall. Every parameter change is a round trip through spawning D2D players.
Hidden parameters: `RandomSeed` and `RandomStepIntervalMs` are hardcoded in `BuildConfig()` (42 /
1000), `SpeedMultiplier`, `StartX/Y`, `EndX/Y` are not exposed. No preset library outside playlist
items, no import/export of a single config.
→ Design: [`2026-09-17-authoring-ux-and-preview-design.md`](2026-09-17-authoring-ux-and-preview-design.md)

### F6. Nothing happens at the bezel (the "wow" gap)
`SyncParameters.transition_effect` is unused. Sprites cross screen edges silently, do not face
their travel direction (a fish bouncing back swims backwards), have no trail, no entry glow, no
squash. There are no one-shot events (spotlight a machine, celebration, "the logo swims once").
→ Design: [`2026-09-17-crossing-effects-and-events-design.md`](2026-09-17-crossing-effects-and-events-design.md)

### F7. Show start/switch is "now" and content arrives late (visible with 20 nodes)
- `sharedStartTimestamp = UtcNow` at apply time (`MainWindowViewModel.ApplyCrossScreenConfigAsync`).
  Remotes must still receive the command, download the file, spawn a player and decode GIF frames,
  then back-date. On 20 machines the first seconds of every playlist item are a ragged start.
- Content cache is an in-memory dictionary (`WallpaperPlaybackService.cs:178`); a client restart
  re-downloads. `contentId` is the bare file name (collisions, stale content).
- Only the primary animation file is transferred; background images and additional images are
  dropped on remotes with a warning.
- Playlist item switches are broadcast at the boundary; no pre-announce, so no cross-fade possible.
→ Design: [`2026-09-17-show-reliability-design.md`](2026-09-17-show-reliability-design.md)

### F8. Smaller observations
- `CrossScreenConfig.AnimationSpeedPxPerSecond` duplicates `Movement.SpeedPixelsPerSecond`
  (both set from the same slider). Candidate for removal when the model is next touched.
- `StartOrchestrationAnimation` (`MainWindowViewModel.cs:3365`) drives the older
  `AnimationOrchestrator`/`AnimationSchedule` path with a hardcoded 5000ms duration. Confirm it is
  unreachable from the UI and archive it with the other legacy code, or document why it stays.
- `PlaylistDialog` reopened during a show shows a fresh VM (noted in OPEN_ITEMS §4.1).
- Video content still lacks pattern / traveling colors (known).

## 4. Roadmap

Sizes: **S** = a focused session, **M** = 2–3 sessions, **L** = a week of sessions, **XL** = multi-week.
Dependencies point at what must exist first.

### Tier 0 — Quick wins (do these first, each is S)
| # | Item | Why | Where |
|---|---|---|---|
| 0.1 | Pass the **virtual canvas height** (not monitor height) to players; align each node vertically to the canvas center | Fixes vertical jumps on mixed heights (F3, part 1) | `D2DCompositionService.InitializeAsync`, `Program.cs UpdateAnimationPosition` |
| 0.2 | **Persist ClientId** in `ClientConfiguration`; send it on register | Stable identity across restarts (F2) | `WallpaperSyncClient`, `ClientConfiguration`, `ConfigurationManager` |
| 0.3 | **Persist topology** (order, distance) server-side keyed by ClientId+hostname; re-apply on register | No re-dragging 20 nodes (F2) | `WallpaperSyncService.RegisterClient`, new `TopologyStore` |
| 0.4 | **Future T0**: `sharedStartTimestamp = now + leadMs` (default ~1500ms, clamp to max observed RTT×3); player holds first frame until `elapsed ≥ 0` | Clean simultaneous start on 20 nodes (F7) | `ApplyCrossScreenConfigAsync`, `Program.cs HandleStartAnimationCommand/RenderLoop` |
| 0.5 | **Face travel direction**: flip sprite horizontally when `dx < 0` (finite difference of the deterministic position) | Fish never swims backwards (F6) | `Program.cs DrawAnimationLayer` |
| 0.6 | Expose **RandomSeed / step interval / SpeedMultiplier** in the dialog; add "Randomize seed" | Unhides existing capability (F5) | `CrossScreenConfigViewModel`, dialog XAML |
| 0.7 | **Wave mode**: per-node phase offset `elapsed − order × phaseDelayMs` in Simultaneous mode | Cheapest new "20-machine" effect: a bounce/pulse runs down the row (F6) | `MovementConfig.NodePhaseDelayMs`, `PlayerCommandLoadAnimation.NodeOrder`, `Program.cs` |

### Tier 1 — Make the 2×10 ring possible (blocking for the scenario)
| # | Item | Size | Depends on | Design |
|---|---|---|---|---|
| 1.1 | **Seat map + ring/snake topology** — rows, per-row direction (facing / same side), turn gap, ring wrap with seam-safe rendering, persistent seat map keyed by identity, "walk the room" ordering wizard | L | 0.2, 0.3 | seat-map doc |
| 1.2 | **Physical canvas** — canvas in reference pixels, per-node scale from `PixelsPerCm`, speed shown in cm/s, lap time readout | M | 0.1 | physical-canvas doc |
| 1.3 | **Show reliability** — prefetch all playlist assets on Start Show, transfer background/additional images, content-hash ids, persistent cache index, pre-announced item switch (`T0_next`) | M | 0.4 | show-reliability doc |

### Tier 2 — Make it look like a show (the authoring & wow layer)
| # | Item | Size | Depends on | Design |
|---|---|---|---|---|
| 2.1 | **Live preview** — an Avalonia canvas that renders the whole seat map and animates sprites with the same `MovementCalculator`/`PatternLayout`/`ColorGrader`; embedded in the config dialog (design-time clock) and main window (live shared clock → "the fish is on Max's machine") | M | 1.1 | authoring doc |
| 2.2 | **Authoring UX** — tabbed dialog, preset library (`%APPDATA%\WaBiBaBuSy\presets\`), import/export, tray quick-presets, validation hints, starter presets | M | — | authoring doc |
| 2.3 | **Scene layers** — N independent sprites per scene, each with own source/movement/look/z-order/phase/spawn window; player refactor from static single-layer state to `LayerRuntime` instances | XL | 1.3 (multi-file transfer) | scene-layers doc |
| 2.4 | **Crossing effects + events** — bezel entry/exit glow/flash/ripple, deterministic trails, scale pulse/spin, one-shot events (spotlight machine, celebration burst, "logo swims once"), scheduled interrupt shows, hotkeys | L | 1.1 (ring), 1.3 (pre-announce) | effects doc |
| 2.5 | Cross-item **transitions** (fade / wipe along the ring) on playlist switches | S–M | 1.3 (pre-announce), 2.4 | effects doc §5 |

### Tier 3 — Stretch
- Audio-reactive mode (server taps audio → broadcasts BPM + phase; clients stay deterministic).
- Video parity for pattern / traveling colors.
- Per-seat personalization (name plate / avatar per seat from the seat map; deterministic per-node accent color).
- Mobile/web remote for triggering events from the floor.
- Path designer (draw waypoints on the preview; today only `PrecomputedPath` for IconZone exists).

## 5. Suggested order of attack

1. **Tier 0 in one or two sessions.** Each is small, and 0.1–0.4 remove the failure modes that
   would otherwise poison the first real 20-machine test.
2. **1.1 Seat map + ring** — this is the feature that turns "20 monitors in a line" into "the room."
3. **2.1 Live preview** early, before 2.3 — it makes every later feature (layers, effects) testable
   at the desk and doubles as the "where is the fish" display.
4. **1.2 Physical canvas** before the party if monitors are known to differ; otherwise after.
5. **1.3 Show reliability**, then **2.4 effects**, then **2.3 layers** (largest refactor; benefits
   most from having preview + effects to validate against).

Do the pending **E2E multi-client test** (OPEN_ITEMS §1) after Tier 0 — it validates the July work
and exercises 0.2–0.4 at the same time.

## 6. Decisions

Confirmed by Patrick on 2026-09-17:

| Question | Decision |
|---|---|
| Row arrangement | The two rows **face each other** (people across the table). Keep the per-row orientation flag in the seat map, default `Facing`. Consequence: no node is mirrored in the default room. |
| Default traversal | **Ring** (closed loop, sprite circles the room forever). Snake and Parallel stay as secondary modes. |
| Units | **Physical units** (cm, cm/s) with the **server's primary monitor** as the reference. Pixel mode remains as legacy. |
| First implementation session | **All seven Tier 0 quick wins** (0.1–0.7), then Tier 1.1 seat map + ring. |

Still assumed (minor, change if wrong):

| Question | Assumption |
|---|---|
| Where presets live | `%APPDATA%\WaBiBaBuSy\presets\*.json`, same pattern as playlists |
| `AnimationOrchestrator` / `StartOrchestrationAnimation` path | Archive with the other legacy code unless a live entry point turns up |

## 7. Where things live after this

- This roadmap: `.docs/plans/2026-09-17-lan-party-roadmap.md`
- Design docs: the six `2026-09-17-*-design.md` files in `.docs/plans/`
- When an item is implemented: move its line to `RECENT_UPDATES.md`, tick it here, and update
  `2026.07_OPEN_ITEMS.md §4` (Tier 2 list) so the two stay consistent.

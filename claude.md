# WaBiBaBuSy - Wallpaper Synchronization System

**Project Name:** WallpaperBiBaBuSync (BiBaBu = our club name)
**Version:** 2.7.0 | **Framework:** .NET 9.0 | **Status:** MVP complete + post-MVP visual features + LAN-party Tier 0 / 1.1 / 2.1 / 2.3
**Last Updated:** 2026-10-21 | **Next:** E2E Multi-Client Testing (first session 2026-10-09 evening, runbook below), then roadmap Tier 2.4 Portal + transitions (events done); 2.3 scene layers slice 1 done

WaBiBaBuSy synchronizes animated wallpapers across 50+ Windows machines with <5% server CPU, ±50ms drift tolerance, and distributed client-side rendering. Supports images (JPG/PNG/BMP), videos (MP4/AVI/MKV), and GIFs across multi-monitor setups.

## Key Capabilities

- Synchronized wallpaper playback across multiple Windows machines
- Server-client architecture with visual network topology management (client ordering, physical distance/bezel gaps, refresh-rate display); order + distances persist across restarts (`topology.json`), clients keep a persistent identity
- Room / seat map: the ordered node chain split into table rows with Ring (closed loop, seam-safe wrap), Snake or Parallel traversal; rows are edited directly in the room view (drag nodes between lanes, right-click to split/merge, lane chips for facing), room-wide settings in the Room ⚙ popover
- Physical canvas (opt-in per room): canvas in reference pixels of the server's primary monitor, per-node `Scale`, sizes in cm and speeds in cm/s resolved by `PhysicalUnits` before broadcast — the same sprite is the same centimeters and cm/s on every monitor
- Live preview: the room view paints the running scene or the docked Scene editor's draft inside every node tile with the players' own deterministic math (`ScenePainter`: every layer, pattern cells and multi-image spread with the real images); the preview badge switches draft ↔ live
- Hardware-accelerated Direct2D rendering with separate player process
- Precise timing synchronization via shared UTC start timestamp + deterministic math (±50ms tolerance)
- Multi-monitor support: Sequential (spanning) and Simultaneous (per-monitor) modes
- Scene layers: a scene is a background plus N image/GIF/video layers (bottom first, at most 2 videos), each with its own file(s), movement, look, effects, opacity, Sequential/Simultaneous distribution and phase offset — Layers strip in the Scene editor (▾ add a layer preset from `presets\layers\` or the layers of a scene preset / save a layer as layer preset, Suggest "＋ as layer", per-layer spawn window `ShowEveryMs`/`ShowForMs`), every layer in the room preview; only the bottom layer may follow the IconZone path (`SceneLayer`, `Program.Layers.cs`, upper-layer video `Program.LayerVideo.cs`)
- 6 movement types (Static, Linear, Bounce, SineWave, Circular, RandomWalk) with Reversed/Endless options
- Sprite effects: trail, pulse, spin and bezel-crossing Glow/Flash/Ripple/Portal (a door at the screen edge, opened ahead of the sprite on both seats) — pure functions of the shared clock (`SpriteEffects`), drawn by the players and the room preview (Effects tab)
- One-shot show events: Spotlight a seat, Celebration burst, Traversal (an image laps the ring once), Blackout, Announce text lap, Identify (seat number + machine name on every seat) — aimed at everyone, a tag or one seat (`ShowEvent.TargetNodeIds`, tags in `tags.json`); with nothing playing a stand-in scene carries them (each seat draws its own Windows wallpaper, `BackgroundLayerConfig.UseLocalDesktopWallpaper`); Spotlight aims at the room's selected node; shared T0 + duration, drawn above the scene by every player and the room preview (`WaBiBaBuSy.Models/Show`), fired from the Events strip, Ctrl+Alt+1..5 (server only) or playlist interrupts; `SyncCommand` EVENT
- Pattern grid multiplier: tile the animation into an infinite deterministic world-space grid (spacing, jitter, rotation, multi-image)
- Color grading: 8 modes — time-based (Rainbow, RandomColors, Gradient, CycleColorList) and per-cell Traveling Colors (Rainbow/List/Random)
- Backgrounds: SolidColor, StretchedImage, TiledImage, ThreeZone corridor, IconZone (A* pathfinding around real desktop icons), Panorama (one wide image over the whole seat-map canvas, stretched to the tallest screen of each row — shorter screens crop centred, `NodeLayout.RowHeight`; each player crops its slice on the CPU — `NodeMapping.PanoramaSource`)
- Debug overlay in player (F11 / IPC): A* path, icon rects, zone bands, movement trail, info panel
- BiBaBo ("Bier, Bart und Bombe") mini-game: 💣 BiBaBo in the Events strip / Ctrl+Alt+B (again = abort) — every free seat goes black, a bomb lands on a random free machine with a 5:00 LED timer, three cables (red/green/blue) run along the seat chain (optionally through a colour-swapping junction box) to riddle cards of seven kinds that each yield a digit (math, sequence, tangle, labyrinth, logic gates, mirrored clock, dots); each game rolls its mode — classic (rolled rule, one live cable), cut order (two cables in order) or keypad (3-digit code) — and may put the RULES on a spare seat; the bomb machine's user clicks on the desktop (Raw Input sink, observe only) or drags the Recycle Bin onto the bomb; the server referees (live cable never sent, first action wins, 300 ms grace) and fires BOOM / DEFUSED / RECYCLED everywhere. Busy seats (a window covers ≥ 80 %) are skipped via the heartbeat. `Models/Show/Bomb*.cs`, `BombGameService`, player `Program.Bomb.cs`, `BombInputWatcher`, `RawMouseSink`, `RecycleBinLocator`, `MonitorBusyProbe`; showcase `TestScenarios/showcase/bibabo.json`; logo = the BiBaBo logo (bombs instead of Busen) shipped as `WaBiBaBuSy.UI/Content/bibabo-bomb.gif` (made by BiBaBu `tools/bibabo/bomb_art.py`; the countdown is drawn into its timer box, `BombGame.LogoTimerBox`, `BombLogo.Resolve`), overridable by `%APPDATA%\WaBiBaBuSy\bibabo-bomb.gif|png` (timer above the bomb then); BOOM plays `Content/bibabo-boom.gif` once on the bomb seat, DEFUSED `Content/bibabo-beer.gif` (the logo's Bier morphs into a cartoon beer, "YOU EARNED A BEER!") (`boom_art.py` / `beer_art.py`, `BombLogo.OutcomeImage`, `BombGame.OutcomeImageScale`)
- Agenda overlay: LAN-day timetable (Fri–Sun, notes) as a corner panel on every connected seat, full size on seats tagged `agenda`; Agenda tab + tray toggle, kept through Stop / Clear / idle via stand-in players (own desktop wallpaper), own `CommandType.AGENDA` transport with clock-offset shift (`Models/Show/Agenda*.cs`, `AgendaDisplay`, player `Program.Agenda.cs`)
- Playlist / Party Mode: rotate animation configs across all machines on a loop (per-item duration + global default, shuffle, opt-in lap-snap to whole movement periods (Linear / SineWave / Bounce / Circular, `PlaylistScheduler.ComputeLapMs`), transitions Cut / Fade through black / ring Wipe per item or playlist default — `SceneTransition`, player `Program.Transitions.cs`; Playlist tab in the right panel, show controls in the toolbar; JSON-persisted under `%APPDATA%\WaBiBaBuSy\playlists\`)
- Scene presets: save / load / import / export scenes as presets (`%APPDATA%\WaBiBaBuSy\presets\`, scene-file format, imported assets bundled relative), add them to playlists; tray "Quick start ▸" plays one on all seats with the window closed; 14 starter presets seeded on first run from the showcase scenes
- Auto-suggest: ✨ Suggest in the Scene editor profiles the picked media (size vs monitor, aspect, transparency, mono vs colourful, dominant colours, GIF loop length) and offers four recipe-built scenes as cards (`WaBiBaBuSy.Models/Wallpaper/Suggest/`; a wide opaque still ≥ 3:1 and 1.5 screens wide is a panorama → Panorama recipe, picked sprites ride across it); click previews in the room view, Shuffle, Keep / ✕ revert
- Auto from folder: the Playlist tab builds a party playlist from a folder (full-screen files / videos as Showcases, look-alike transparent sprites in groups of 3–5, the rest solo; interleaved, no recipe twice in a row, GIF dwell on whole loops — `PlaylistBuilder`); the gallery's "Get content ▾" links free content sites, AI generators and clean-up tools (`Models/Content/ContentLinks.cs`)
- Show reliability: hashed content ids, persistent client cache, every scene asset transferred, playlist prefetch with per-node readiness, server download throttle, health strip + Resync all, gapless item switches (standby: the old player stays until the new one appears at the shared start, `RetiringPlayers`)
- Pause on fullscreen: a monitor one window covers completely (game, borderless fullscreen) pauses its players — no draw, no Present, videos held (`cmd_pause`, player `Program.Pause.cs`); whole machine by default, or only that screen (`ClientConfiguration.PauseOnlyFullscreenMonitor`); the seat stays connected and online (`FullscreenDetectionService` + `MonitorBusyProbe.FullscreenMonitors`, re-applied every 2 s by `ServerPlaybackHost`)
- Auto-update system with SHA-256 verification and rollback
- System tray operation with minimal UI footprint

## Project Map

| Project | Role |
|---|---|
| `WaBiBaBuSy.Models` | Pure data + deterministic math (`MovementCalculator`, `ColorGrader`, `PatternLayout`, `PhysicalUnits`, `SpriteEffects`), topology, show events, test-mode models. No I/O |
| `WaBiBaBuSy.Core` | Services: networking client/server host, mDNS, animation/playlist orchestration, presets, content cache, update, logging, test runner |
| `WaBiBaBuSy.Grpc` | `wabibabusy.proto` + `WallpaperSyncService` (server side of the sync stream) |
| `WaBiBaBuSy.WallpaperEngine` | Desktop/WorkerW integration, Win32 interop, `D2DPlayerHost` (spawns players), LibVLC renderers |
| `WaBiBaBuSy.Player.D2D` | Separate render process (`Program*.cs`, Vortice Direct2D) — metadata IPC in, frames out |
| `WaBiBaBuSy.Player.Common` | Player IPC command types (JSON lines on stdin; replies `READY` / `ERROR:` on stdout, signals on stderr) |
| `WaBiBaBuSy.UI` | Avalonia 11 + CommunityToolkit.Mvvm app (tray, room view, docked Scene editor, `ServerPlaybackHost` / `RemotePlaybackHost`) |
| `WaBiBaBuSy.Updater` | Standalone updater exe (file replace + rollback) |
| `WaBiBaBuSy.Common` | Small shared helpers (paths, IO, version, update) |
| `WaBiBaBuSy.Tests` | xUnit tests incl. determinism guards |

Other: `TestScenarios/` (test-mode scenarios), `Installer/` (Inno Setup), `tools/`, `_archive/` (removed legacy GDI+ stack). Git-ignored: `publish/`, `packages/`, `Lively-reference/`, `.worktrees/`.

## Documentation Map

> **For detailed architecture, project structure, and technical design, see the linked docs below.**

### Core Architecture
- **[Software Architecture](.docs/SOFTWARE_ARCHITECTURE.md)** — Full system architecture, project structure, technology stack, rendering pipeline, networking, configuration reference
- **[UI Architecture](.docs/UI_ARCHITECTURE.md)** — Views, ViewModels, user flows, UI features
- **[D2D Rendering Architecture](.docs/2025.12_DIRECT2D_RENDERING_ARCHITECTURE.md)** — Composition pipeline, HeadlessMode, Windows 11 24H2+ compatibility (**read before touching rendering**)
- **[Distributed Animation System](.docs/2025.11_DISTRIBUTED_ANIMATION_SYSTEM.md)** — Phases 1-4 implementation guide
- **[Animation Movement System](.docs/ANIMATION_MOVEMENT_SYSTEM.md)** — Deterministic position calculation

### Project Tracking
- **[Open Items / Session Handoff](.docs/2026.07_OPEN_ITEMS.md)** — Consolidated validation list, code follow-ups, limitations, roadmap pointer (start here, refreshed 2026-10-09)
- **[Feature Overview & Roadmap](.docs/2026.07_FEATURE_OVERVIEW.md)** — Verified inventory of all animation features + feature-completeness roadmap (2026-07 audit)
- **[Open Issues](.docs/2025.12_OpenIssues.md)** — Active bugs and items needing validation
- **[Missing Features](.docs/2025.12_MissingFeatures.md)** — Feature roadmap and TODO tracking
- **[Recent Updates](.docs/RECENT_UPDATES.md)** — Historical changelog of all milestones
- **[IconZone Animation Issues](.docs/2026.04_IconZoneAnimation_TODO.md)** — Open IconZone bugs (April 2026)
- **[D2D Issues & Fixes](.docs/2026.02_D2D_ISSUES.md)** — D2D-specific bug fixes (2026-02)
- **[Archived Task List](.docs/2026.01_TODO_ACTIVE.md)** — Jan–Mar 2026 sprint (historical)

### Feature Designs (`.docs/plans/`)
- **[UI Redesign (2026-09-24)](.docs/plans/2026-09-24-ui-redesign-design.md)** — room-first main window; Plans 1–3 implemented (toolbar + RoomView, docked Scene editor + Playlist tab, Settings sidebar); GUI checks pending
- **[LAN-Party Roadmap (2026-09-17)](.docs/plans/2026-09-17-lan-party-roadmap.md)** — Analysis for the 2×10-machine scenario: findings F1–F8, Tier 0–3 plan, links to the six design docs below (**start here for animation/config work**)
  - [Seat Map & Ring Topology](.docs/plans/2026-09-17-seat-map-and-ring-topology-design.md) · [Physical Canvas](.docs/plans/2026-09-17-physical-canvas-design.md) · [Scene Layers](.docs/plans/2026-09-17-scene-layers-design.md) · [Authoring UX & Preview](.docs/plans/2026-09-17-authoring-ux-and-preview-design.md) · [Crossing Effects & Events](.docs/plans/2026-09-17-crossing-effects-and-events-design.md) · [Show Reliability](.docs/plans/2026-09-17-show-reliability-design.md)
  - [Seat Choreography (2026-09-27)](.docs/plans/2026-09-27-seat-choreography-design.md) — Hop movement + seeded random seat hopping, seat light, speed profiles, convoy, row mosaic, tempo grid, motion segments (roadmap Tier 2.6, proposal)
- **[Post-Party Plans (2026-10-17)](.docs/plans/2026-10-17-post-party-plans.md)** — proposals for after the first party: ghost seats (dropouts keep the layout), per-client opt-out, standby / two-phase switch, transitions, lap-snap for all movements, Portal, layer follow-ups, per-seat DPI override
- **[Remote Session Runbook (2026-10-09)](.docs/plans/2026-10-09-remote-session-runbook.md)** — order, commands and time budget for the first multi-machine session; pre-flight results (single machine, all green); tick-boxes stay in the [2026-10-04 checklist](.docs/plans/2026-10-04-manual-test-checklist.md)
- **[Automated Test Mode (2026-09-30)](.docs/plans/2026-09-30-automated-test-mode-design.md)** — scenario-driven multi-machine test runs: timing probes (present-time drift), marker position checks, exact-frame pixel parity, screenshots + logs → `report.json` / `report.html` (spec 1 implemented, multi-machine validation pending; spec 2 = show reliability + failure injection)
- **[Corridor Animation System](.docs/plans/corridor-animation-system.md)** — ThreeZone background + corridor-constrained animation (implemented 2026-04)
- **[IconZone Path Variation](.docs/plans/2026-04-24-iconzone-path-variation-plan.md)** / **[Variation Rotation](.docs/plans/2026-04-24-iconzone-variation-rotation-design.md)** — IconZone A* path design
- **[Traveling Colors](.docs/plans/2026-05-01-traveling-colors.md)** — Per-cell color grading design (implemented 2026-05)

### Historical Reference
- **[`.docs/_archive/`](.docs/_archive/)** — 34 archived docs (planning, diagnostics, session summaries, superseded designs)

**When documenting:**
- **New bugs** → `.docs/2025.12_OpenIssues.md`
- **Missing features** → `.docs/2025.12_MissingFeatures.md`
- **Architecture changes** → Update `CLAUDE.md` or `.docs/SOFTWARE_ARCHITECTURE.md`
- **Completed fixes** → `.docs/RECENT_UPDATES.md`

## Development Guidelines

**Code Standards:**
- C# 12+, nullable reference types, XML docs for public APIs
- Async/await for all I/O (file, network, rendering)
- File-scoped namespaces, MVVM in UI layer

**Architecture Patterns:**
- Clean Architecture: Core independent from UI/infrastructure
- MVVM: Strict View (XAML) / ViewModel (C#) separation
- Dependency Injection: Constructor injection only
- Interface-based design, single-responsibility services

**Error Handling:**
- Network: Exponential backoff (1s, 2s, 4s, 8s, max 30s)
- Rendering: Graceful degradation with fallbacks
- Config: Validate on load, provide sensible defaults

**Code Delivery:**
- Complete, working code files with imports and namespaces
- Ensure zero compilation errors before delivery

## Architectural Invariants (DO NOT BREAK)

> **READ THIS SECTION BEFORE making any changes to the animation, rendering, or D2D pipeline.**

### D2D Player Architecture
- **Separate process**: `WaBiBaBuSy.Player.D2D.exe` runs in its own process. DXGI swap chain windows in the main process crash `explorer.exe` on Windows 11 24H2+. NEVER move DXGI rendering back into the main process.
- **Metadata-based IPC**: Main process sends animation metadata (file path, canvas size, offsets, movement config) to player processes via stdin/stdout JSON. Players render locally. Main process does NOT compose or send frames.
- **Deterministic positioning**: All player processes independently calculate animation position from `elapsedTime + MovementCalculator`. No per-frame position IPC. This is what enables multi-monitor sync.
- **Deterministic visuals everywhere**: Pattern layout (`PatternLayout.Hash3`), color grading (`ColorGrader`/`ComputeForCell`), and RandomWalk are all pure seeded functions of `(config, elapsedMs, cell identity)`. NEVER introduce `Random` without a shared seed, `DateTime.Now`, or per-machine state into these paths — remote machines must compute pixel-identical results.

### Animation Distribution Modes
**There are exactly two modes. Their meaning is precise:**

The mode is chosen **per scene layer** (`SceneLayer.Distribution`, 2026-10-14):

| Mode | UI Name | What it means | Virtual canvas | MonitorOffsetX |
|------|---------|---------------|----------------|----------------|
| **Sequential** | "Sequential" | The layer **spans across all monitors** as one big canvas | `VirtualCanvasWidth = total width of all monitors combined` (seat-map `NodeLayout`) | `= this monitor's X offset in virtual space` |
| **Simultaneous** | "Simultaneous" | The layer plays **independently on each monitor** | `VirtualCanvasWidth = this monitor's width only` | `= 0` |

**Implementation path:**
1. Docked Scene editor (`SceneEditorPanel` → `CrossScreenConfigViewModel.BuildConfig`) → user picks the mode of the selected layer → `SceneLayer.Distribution`
2. `ServerPlaybackHost.ApplyCrossScreenConfigAsync()` (Play, toolbar Start and every show item; app-lifetime, owned by the tray) → `CrossScreenPlanner` resolves every layer (cm → px) and builds the seat-map layout
3. `D2DCompositionService.InitializeAsync(layers, layout)` → one LOAD with all layers + this node's `NodeLayout`; remotes get the same via `SyncParameters.scene_json` + `layout_json`
4. `Player.D2D` picks the canvas per layer (`LayoutFor`: Sequential = `NodeLayout`, Simultaneous = own monitor, offset 0) → `MovementCalculator` at `SyncTiming.LayerElapsed` → subtracts `MonitorOffsetX` from virtual position

**CRITICAL:** Every layer of every player starts from the same shared timestamp. The modes differ ONLY in how `VirtualCanvasWidth` and `MonitorOffsetX` are set for that layer.

### Scene Layers (roadmap 2.3)
- A scene is `Background` + `List<SceneLayer> Layers` (index 0 = bottom, list order = draw order). Old single-sprite JSON (`Animation` / `Movement` / `DistributionMode`) migrates to layer 0 **on every read** (`LegacySceneMigration`, `[JsonExtensionData]` + `IJsonOnDeserialized`) — never re-add those properties.
- **Layer 0 only**: IconZone A* path + clip mask, ThreeZone corridor clamp; scene-wide things (show events, test-mode markers, lap snap, remote content id) follow layer 0.
- **Video**: at most `SceneChecks.MaxVideoLayers` (2) per scene. Layer 0's video keeps its own render branch (`_vlcPlayer`, `_useNativeD2DVideo`); an upper layer's video is a `LayerVideo` on its `LayerRuntime` (own LibVLC, own sync timer on `SyncTiming.LayerElapsed`), drawn through `DrawAnimationLayer(videoBitmapOverride)`.
- Player: each layer's per-sprite statics live in a `LayerRuntime` and are swapped in before it updates/draws (`Program.Layers.cs`); layer 0 is the resting state. A **new per-sprite static must be added to `LayerRuntime` + `SaveInto`/`LoadFrom`**, or upper layers share it with layer 0.
- Layers never read each other's state; a layer's clock is `SyncTiming.LayerElapsed` (node phase, start gate, `PhaseOffsetMs`) in the player and the room preview alike.

### Logging Architecture
- **`AppLogger`** is a static factory using volatile `LoggingConfiguration`. `ApplyConfig()` takes effect immediately.
- **`FileLoggerProvider`** creates rolling daily files in `LogDirectory`. Errors written to `Console.Error`.
- **Config persistence**: `%APPDATA%\WaBiBaBuSy\logging-config.json` (separate from main config).

### Windows Desktop Integration
- **WorkerW technique**: Wallpaper windows are parented to the desktop behind icons.
- **WS_EX_LAYERED + SetLayeredWindowAttributes**: NOT `WS_EX_TRANSPARENT` (which crashes explorer on 24H2+).
- **Z-order**: `SetParent` first, then `SetWindowPos` with DefView reference.

## Quick Start

```bash
dotnet restore && dotnet build
dotnet run --project WaBiBaBuSy.UI
dotnet test WaBiBaBuSy.Tests
# test mode (exit 0 = pass, 1 = step failed, 2 = aborted / not enough clients)
.\WaBiBaBuSy.UI.exe --test-run TestScenarios\smoke-local.json --exit
```

**Server:** Tray icon → Start Server → Open Server Control Panel
**Client:** Tray icon → Connect to Server → Enter IP or use auto-discovery

## Current Work (Priority Order)

1. **E2E Multi-Client Testing** — follow `.docs/plans/2026-10-09-remote-session-runbook.md`: `TestScenarios/parity-matrix`, `clock-skew`, `showcase/events`, `sync-basic`, `recovery` (fault injection, added 2026-10-10) on 2+ machines, then the hands-on checklist `.docs/plans/2026-10-04-manual-test-checklist.md` §5–§8 (ring, physical canvas, show events, show reliability, reconnection <5s); server-alone GUI checks §1–§3 any other day
2. **VALIDATE: File logging + Installer** — LogToFile at `%LOCALAPPDATA%\WaBiBaBuSy\Logs\`; clean Windows 10/11 install
3. **Roadmap Tier 2** — 2.4 Portal + transitions (events done); 2.3 scene layers slice 1 done 2026-10-14 (GUI check + multi-machine parity pending; follow-ups: video on upper layers, spawn windows, layer presets) — see `.docs/plans/2026-09-17-lan-party-roadmap.md`

Full list: `.docs/2026.07_OPEN_ITEMS.md` (refreshed 2026-10-09).

## MVP Success Criteria (6/6 Implemented, E2E validation pending)

- 2+ machines sync video wallpaper playback
- Drift under 50ms for 10+ minutes
- CPU <15%, GPU <10%
- Server UI allows client ordering and content selection
- Graceful network disconnect recovery — implemented 2026-07-07 (backoff reconnect + session resume), needs E2E validation
- Installer works on clean Windows 10/11 (Inno Setup, see `/Installer/`)

## Performance Targets

| Metric | Target | Status |
|--------|--------|--------|
| Sync Accuracy | ±50ms drift | Achieved |
| CPU Usage | <5% idle, <15% playing | Achieved |
| GPU Usage | <10% | Achieved |
| Memory | <200MB per client | Achieved |
| Network | <1 Mbps during sync | Achieved |
| Startup | <3 seconds | Achieved |
| Reconnection | <5 seconds | Implemented (backoff + session resume), needs E2E measurement |

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- ALWAYS read graphify-out/GRAPH_REPORT.md before reading any source files, running grep/glob searches, or answering codebase questions. The graph is your primary map of the codebase.
- IF graphify-out/wiki/index.md EXISTS, navigate it instead of reading raw files
- For cross-module "how does X relate to Y" questions, prefer `graphify query "<question>"`, `graphify path "<A>" "<B>"`, or `graphify explain "<concept>"` over grep — these traverse the graph's EXTRACTED + INFERRED edges instead of scanning files
- After modifying code, run `./tools/graphify-update.ps1` to keep the graph current (AST-only, no API cost). Use the wrapper, **not** bare `graphify update .` — it pins `PYTHONHASHSEED` so community numbering is reproducible. Without it every rebuild renumbers ~70% of communities and rewrites ~31k lines of `graph.json` with no code change.
- `.graphifyignore` keeps `obj/`, `bin/`, `.worktrees/` and `.history/` out of the graph. After editing it, rebuild with `-Fresh` (`graphify update` unions into the existing graph, so dropped files leave stale nodes behind).
- Only `GRAPH_REPORT.md` is tracked. `graph.json`, `manifest.json`, `cache/` and `graph.html` are local artifacts — if `graph.json` is missing (fresh clone), run the wrapper once before using `graphify query/path/explain`.

# WaBiBaBuSy - Recent Updates & Changelog

**Last Updated:** 2026-09-17

---

## 2026-09-17 — Show reliability: content pipeline (Tier 1.3)

Design: `.docs/plans/2026-09-17-show-reliability-design.md` §3 + §5 (§2 future T0 landed in Tier 0; §4 standby/two-phase switch and §6 auto-start deferred). 9 new unit tests (141 total).

- **Content ids identify bytes** (`WaBiBaBuSy.Models/Content/ContentIdentity`): `{fileName}-{sha256[..16]}`, hash cached per (path, size, mtime). Same name + new bytes → new id → clients fetch the new version; no more stale content after re-exporting a GIF under the same name.
- **Persistent client cache index** (`ContentCacheIndex`, `cache-index.json` in the cache dir): the playback service restores contentId → path on startup and saves after every download/registration. A client restart no longer re-downloads the whole show. LRU touch on cache hits.
- **All assets travel**: `SceneAssets.CollectPaths` lists primary + additional images + background image; the apply path registers each (`ContentAssetRef`) and ships them in `SyncParameters.assets` (proto `ContentRef`, field 26). The client downloads every asset before applying and `SceneAssets.RewriteToLocal` rewrites the configs to local cache paths — multi-image presets and image backgrounds now render on remotes instead of degrading. Missing assets still degrade gracefully (dropped / solid color).
- **Prefetch**: new `CommandType.PREFETCH` (9); `PlaylistOrchestrator.Start` asks every client to cache the whole show (`WallpaperSyncCoordinator.BroadcastPrefetchAsync`) and waits up to 15 s until all remote nodes report ready via heartbeat (`prefetch_ready/total`, HeartbeatRequest 7/8, ConnectedClient 12/13); item switches then never wait for a download. Client downloads are serialized (a prefetch cannot starve a LOAD).
- **Server throttle**: at most 6 concurrent `DownloadContent` streams (`SemaphoreSlim`), so 20 first-time fetches queue instead of thrashing.
- **Show health**: topology header shows "N/M remote online · cached/fetching · stale · worst ±X ms"; nodes carry a "⬇ 3/7" / "✓ cached" badge; **Resync all** re-sends every active cross-screen command (original shared start) — fixes a node that missed a command. Active Animation panel shows "next: <item> in m:ss" while a show runs (`PlaylistOrchestrator.NextItem/NextSwitchUtcMs`).
- **Known**: downloaded files keep their original file name in the cache, so two different files with the same name overwrite each other on disk (the index then points both ids at the newest bytes — acceptable, documented).

## 2026-09-17 — Physical canvas for mixed monitors (Tier 1.2)

Design: `.docs/plans/2026-09-17-physical-canvas-design.md`. 15 new unit tests (132 total).

- **Model**: `SeatMap.CanvasMode` (Pixels | Physical) and `SeatMap.VerticalAnchor` (Center | Top | Bottom); `NodeLayout.Scale` (= node ppcm / reference ppcm) and `RefPixelsPerCm`. `SeatMapLayoutBuilder.Build(map, nodes, referencePixelsPerCm)`: on a physical canvas each node's slice is `px / Scale` reference pixels and bezel/turn gaps use the reference DPI; unknown DPI → Scale 1. Pixel mode is bit-identical to before (tested).
- **Units**: `AnimationLayerConfig.SizeUnit` + `TargetHeightCm`, `MovementConfig.SpeedUnit` + `SpeedCmPerSecond`. New fit mode `ContentFitMode.TargetHeight` ("scale to a fixed height") — the first fit mode where the height field affects the main sprite (Center = native, Fit/Fill/Stretch derive from the screen; the dialog now hides the height field for those). `PhysicalUnits.ResolveForCanvas` converts cm → canvas px right before broadcast, so players and the deterministic math only ever see pixel values.
- **Reference DPI**: the server's primary monitor (`MainWindowViewModel.ReferencePixelsPerCm`, cached); shown in the Room summary, with a warning when unknown.
- **Player**: animation-layer math (layout, fit, movement, pattern, IconZone planner inputs, corridor clamp) runs in canvas units `_cw × _ch = layout.Width × layout.Height`; `DrawCellWithOptionalGrading` applies `Scale` as a D2D transform (composed after flip/rotation). Zone bands are converted back to device px for drawing. Per-monitor (Simultaneous) mode has no layout and therefore no scale.
- **Playlist**: `ApplyMetrics.SpeedPxPerSecond` / `PlaylistScheduler.ComputeLapMs(..., speedPxOverride)` so lap-snap uses the resolved canvas speed.
- **UI**: Room panel gains "Physical units (cm)" and "Align" (Center/Top/Bottom); animation dialog gains the "Target height" fit mode, a px/cm toggle on the height, a px/s / cm/s toggle on the speed (own slider 1–150 cm/s); topology node caption shows the monitor's physical size in cm; the preview resolves units the same way and reports canvas length in metres and sprite width in cm.
- **Not covered**: EDID-based DPI verification / per-seat DPI override (design §6) — `MonitorDpiHelper` already prefers raw DPI; add the override if a monitor reports nonsense.

## 2026-09-17 — Live preview (Tier 2.1)

Design: `.docs/plans/2026-09-17-authoring-ux-and-preview-design.md` §2 (the preview part; tabs/presets are still open).

- **`ScenePreviewControl`** (`WaBiBaBuSy.UI/Controls/`): custom Avalonia control that draws the room's lanes from a `SeatMapLayoutResult` and animates the scene with the players' own pure functions (`MovementCalculator` incl. ring wrap, `NodeMapping` mirroring + seam copies, `PatternLayout` cells with traveling colors via the new `ColorGrader.ComputeCellColor`/`IsCellColored`, time-based tints via `ComputeCurrentColor`, `SyncTiming` node phase / future-start gating, ThreeZone corridor clamp). Sprite drawn from the image file (gallery thumbnail for videos), tinted when grading is active. Legend: t, lap time, canvas size/ring, "sprite on: <host> (#n)"; the node currently holding the sprite gets a green outline.
- **Config dialog**: preview pane above the form with Pause/Restart and 1×/4×/16× design clock; every view-model change (incl. monitor selection) rebuilds scene + layout after a 150 ms debounce and restarts the clock. Layout comes from `MainWindowViewModel.LayoutForSelection` → identical to what Start will use.
- **Main window**: the "Active Animation" panel now contains the live view on the players' shared clock (`ActiveLayout/ActiveScene/ActiveSharedStartMs` set by the apply path) — this is the "where is the fish right now" display from the roadmap.
- **Not simulated** (stated in the UI): GIF frame timing, IconZone path-following, video frames, face-travel flip.

## 2026-09-17 — Seat map & ring topology (Tier 1.1)

Design: `.docs/plans/2026-09-17-seat-map-and-ring-topology-design.md` (implemented as the "row breaks over the ordered chain" variant — see the status note in that doc). 29 new unit tests (112 total).

- **Model** (`WaBiBaBuSy.Models/Topology/`): `SeatMap` (rows with `SeatCount` + `Orientation`, `Traversal` Ring/Snake/Parallel, turn/row gaps), `NodeLayout` (offset, canvas size, `Mirrored`, `Wraps`, order, row/index-in-row), pure `SeatMapLayoutBuilder` (path or stacked layout, mirroring rule `dir_r × screenAxis_r < 0`, turn gaps, Ring closing gap), `NodeMapping` (virtual→local incl. mirroring, seam `WrapCopies`), `SeatMapStore` (`%APPDATA%\WaBiBaBuSy\seatmap.json`, enums by name).
- **Movement**: `MovementCalculator.Calculate(..., canvasWraps)` — Linear/SineWave laps are exactly one perimeter on a Ring (0 → P, no off-screen run-in); Bounce/Circular/RandomWalk unchanged.
- **Transport**: `SyncParameters.layout_json` (field 25), `CrossScreenApplyRequest.LayoutJson`, `PlayerCommandLoadAnimation.Layout`; loose width/offset fields stay populated for older clients.
- **Player**: keeps `_layout` + `_animVirtualX`; all local X mapping goes through `NodeMapping` (mirrored nodes flip inside their slice, pattern cells too); a seam-straddling sprite is drawn at x and x − P.
- **Apply path**: `ApplyCrossScreenConfigAsync` builds the canvas with `SeatMapLayoutBuilder` over the topology order (a single-row seat map reproduces the old `VirtualCanvasManager` layout exactly, incl. bezel gaps and vertical centering); IconZone global path uses the same canvas width.
- **UI**: "Room" panel above the topology (rows, seats per row, rows face each other, path Ring/Snake/Parallel, turn gap, summary); topology renders one lane per row with odd rows placed right-to-left so the ring reads as a loop, row captions with direction, stacked-turn arrows and a closing Ring arrow; drag-reorder maps lanes back to traversal order. `MainWindowViewModel.BuildSeatLayout` is shared by apply path, lanes and preview.
- **Defaults**: fresh installs stay single-row (Snake) = today's behavior. For the party: Rows = 2, Seats/row = 10, facing on, Path = Ring.
- **Deferred from the design**: per-seat labels/bindings, Identify + click-to-order tools, `UpdateSeatMap` RPC (the seat map is server-local; clients only receive their `NodeLayout`).

## 2026-09-17 — Tier 0 quick wins (LAN-party roadmap)

Roadmap: `.docs/plans/2026-09-17-lan-party-roadmap.md` §4 Tier 0. All seven items; 30 new unit tests (83 total).

- **0.1 Canvas height + vertical centering** — `VirtualCanvasManager` now centers each screen inside the canvas height (`ScreenMapping.VirtualBounds.Y`); `D2DCompositionService` sends the wall's tallest height as `VirtualCanvasHeight` plus `MonitorOffsetY`; the player runs `MovementCalculator` against the canvas height and subtracts its Y offset. Fixes the vertical jump between a 1080-px and a 1440-px node. Remote transport: `SyncParameters.virtual_canvas_height` / `monitor_offset_y` (fields 22/23).
- **0.2 Persistent client identity** — `ClientConfiguration.ClientId` stores the server-assigned id; `WallpaperSyncClient` sends it on every registration, persists it via `ConfigurationManager.UpdateClientId`, and no longer clears it on disconnect. A restarted client is the same node.
- **0.3 Persistent topology** — new `TopologyStore` (`%APPDATA%\WaBiBaBuSy	opology.json`): order + bezel distance per node, bound by ClientId with hostname fallback (re-imaged machine keeps its seat). `WallpaperSyncService.RegisterClient` re-attaches known machines and appends new ones; order/distance edits (gRPC and server-mode direct) persist.
- **0.4 Future start timestamp** — `SyncTiming.ComputeStartLeadMs` (3×worst RTT + measured local load time, clamped 800–4000 ms) schedules the shared start ahead; players draw background only until `elapsed ≥ 0`, so all nodes reveal the sprite on the same frame. `PlaylistOrchestrator` measures dwell from the scheduled start (`ApplyMetrics.StartLeadMs`).
- **0.5 Face travel direction** — `AnimationLayerConfig.FaceTravelDirection`: the single-sprite draw path mirrors the bitmap when screen-space dx < 0 (0.5 px hysteresis, loop wraps ignored). Off by default; checkbox in the dialog.
- **0.6 Exposed parameters** — RandomWalk seed + step interval (with "Randomize"), playback speed multiplier; these were hardcoded (42 / 1000) or unreachable in `CrossScreenConfigViewModel.BuildConfig`.
- **0.7 Wave mode** — `MovementConfig.NodePhaseDelayMs`: in Simultaneous distribution each node shifts its clock by `order × delay` (`SyncTiming.ApplyNodePhase`), so a bounce/orbit runs down the row like a stadium wave. `NodeOrder` travels in IPC and gRPC (`node_order`, field 24). Ignored in Sequential mode.
- Tests: `SyncTimingTests`, `TopologyStoreTests`, roundtrip coverage for the two new config fields. E2E on real remotes still pending (see OPEN_ITEMS §1).

## 2026-07-18 — Playlist / Party Mode (Tier 2 #7)

Rotate a set of animation configs across all synced machines as a "show." Plan: `.docs/plans/2026-07-18-playlist-party-mode-plan.md`, design: `.docs/plans/2026-07-18-playlist-party-mode-design.md`.

- **Data model** (`WaBiBaBuSy.Models/Wallpaper/Playlist.cs`): `Playlist` (Name, Loop, Shuffle, DefaultItemDurationMs, Items) + `PlaylistItem` (Name, embedded `CrossScreenConfig`, `DurationMs?`, `SnapToLap`). Each item embeds a full self-contained config — this is the first on-disk persistence of `CrossScreenConfig`.
- **Scheduling** (`PlaylistScheduler.cs`, pure + unit-tested): `ComputeLapMs` (Linear-only lap period, mirrors `MovementCalculator.CalculateLinear`), `ResolveDwellMs` (per-item duration with global default; opt-in lap-snap rounds up to a whole lap), `BuildCycleOrder` (seeded Fisher–Yates shuffle, server-authority-only — never a render input).
- **Orchestration** (`WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs`): server-side rotation loop that re-invokes the existing shared-timestamp broadcast path per item, waits the resolved dwell, advances; loop/shuffle/stop; pauses on apply failure to avoid busy-looping. Tracks `CurrentItem` so the existing session-resume path (`_activeCrossScreenCommands`) lands a reconnecting client on the current item.
- **Apply path refactor**: extracted `MainWindowViewModel.ApplyCrossScreenConfigAsync(CrossScreenConfig)` from `StartCrossScreen()` (behavior-neutral) so both the manual Start button and the orchestrator share it.
- **Persistence** (`PlaylistStore.cs`): JSON under `%APPDATA%\WaBiBaBuSy\playlists\`.
- **UI**: dedicated `PlaylistDialog` (`PlaylistViewModel` + `PlaylistItemRow`) — add/edit (reuses the CrossScreen config dialog), reorder, duplicate, remove; Loop/Shuffle/default duration; Start/Stop Show; Save. Entry-point button next to the cross-screen actions in `MainWindow`.
- **UX decisions**: per-item duration + global default; hard-cut with opt-in lap-snap; remotes auto-follow (no per-client opt-out — use the existing per-monitor selection to exclude); own dialog.
- **Known limitations (v1):** lap-snap is Linear-only (other movement types hard-cut); `ContentWidthPx` is best-effort 0 so lap-snap uses canvas-width traversal distance; consecutive items targeting *different* monitor sets leave stale D2D services on dropped monitors until Stop (fine when all items use the same monitor set); scheduled interrupt-shows, per-client opt-out, and cross-item transitions are deferred.
- Tests: 11 new playlist unit tests (models roundtrip, lap/dwell math, shuffle). Full run pending E2E multi-client validation.

## 2026-07-18 — Legacy code cleanup (GDI+ composition stack + frame streaming)
- **Archived** the dead GDI+ composition stack to `_archive/legacy-gdi-composition/` (outside the build): `CompositionRenderer`, `AnimationLayerRenderer`, `BackgroundLayerRenderer`, `GifWallpaperRenderer`, `CrossScreenFrameRenderer`. See the folder's README for the dead-code verification.
- **Removed** the server-side frame-streaming gRPC path end-to-end: `StreamCrossScreenFrames` RPC, `CrossScreenFrame`/`FrameAcknowledgment` messages, `CompressionType` enum, `CROSSSCREEN_START/STOP` command types (tags 6/7 `reserved`), client frame-stream machinery, coordinator `SendCrossScreenFrameAsync`, `UseDistributedRendering` config flag, and the player's never-initialized "video fallback" render branch (incl. `ConvertBitmapToD2D`).
- **Removed** the unimplemented `DistributionMode.GroupedSequential` enum value (2D topology covers the underlying need).
- **Kept** `VideoWallpaperRenderer` + `ImageWallpaperRendererLibVLC` — the 2026-07 audit mislabeled them dead; they drive simple per-monitor playback.
- Verified: full solution builds 0 errors, 37/37 tests green.

## 2026-07-18 — Drift telemetry (replaces TimingSynchronizer)
- Clients report clock offset + RTT via heartbeat (`has_drift_report` guards the sample-less first beat)
- Server stores reports on `ConnectedClient`; `DriftMonitor` logs once per new ±50ms breach
- Topology nodes show color-coded "±Xms" (green ≤25 / yellow ≤50 / red >50 / grey stale)
- Deleted dead `TimingSynchronizer`, `BroadcastAnimationTimingSync` RPC + proto message, and the orphaned `BroadcastCommandAsync` helper

## 2026-07-07 — Movement Polish + mDNS Server Browser (Tier 2 start)

- **FIXED: Long-uptime float precision** — `MovementCalculator` folds elapsed time into each movement's period in double before float math; positions stay sub-pixel accurate after weeks of uptime (previously multi-pixel stutter after ~2 days, unit-tested at 40 days).
- **FIXED: RandomWalk seed-rotation teleport** — boundary waypoints pinned to the previous iteration's seed; the walk is now continuous across seed rotations while still never repeating.
- **FIXED: Drift telemetry computed as 0** in `ClientAnimationRenderer`; `TimingSynchronizer` latent bugs fixed (session-scoped clients, CS1998) — the loop itself remains unwired (orchestrator path only).
- **NEW: mDNS Server Browser** — "Find..." button next to Connect opens a live dialog of discovered servers (`ServerBrowserDialog`); double-click connects. Uses the existing `MdnsClientDiscoveryService`.
- Tests: 19 total (7 new movement determinism/precision tests).

## 2026-07-07 — Tier 1 Reliability: Trustworthy Multi-Machine Sync

Plan: `.docs/plans/2026-07-07-tier1-reliability.md`. New `WaBiBaBuSy.Tests` xunit project (12 tests).

- **NEW: Clock-offset compensation** — every heartbeat feeds an NTP-style min-RTT `ClockOffsetEstimator` (Models.Networking); incoming command timestamps (`TimestampUtc`, `SharedStartTimestampMs`) are converted from server-clock to local-clock terms at the sync-stream boundary. The ±50ms target no longer requires machines to have synced Windows clocks.
- **NEW: Auto-reconnection** — `WallpaperSyncClient` reconnects with 1s..30s exponential backoff on sync-stream loss or 3 consecutive heartbeat failures; fires `ConnectionStatusChanged` immediately (previously it stayed a "connected" zombie).
- **NEW: Session resume** — the coordinator remembers the active cross-screen command per client and re-sends it when the client re-registers; epoch back-dating lands the rejoining machine at the correct mid-animation position.
- **FIXED: Remote parameter parity** — `SyncParameters` gains `movement_json`/`animation_json`/`background_json`/`target_monitor_index`; remote clients now receive the full `MovementConfig` (Reversed/Endless/wave/orbit/seed), `AnimationLayerConfig` (TargetHeight, SpeedMultiplier, precomputed IconZone A* path, pattern, color grading) and `BackgroundLayerConfig`. Delegate is now `Func<CrossScreenApplyRequest, Task>` (new Models DTO).
- **FIXED: Simultaneous mode on remotes** — per-monitor mode no longer receives the spanning canvas/offset overrides (was silently rendering as Sequential on remote machines).
- **FIXED: Server hardening** — bind failures surface (`await app.StartAsync()`); per-client `SemaphoreSlim` serializes gRPC stream writes (overlapping writes silently dropped commands); 10s sweep removes clients with heartbeat >30s old; client/host `Dispose` no longer risks UI deadlock.
- **NEW: Real remote monitor geometry** — clients report physical pixels-per-cm per monitor (`GetDpiForMonitor`) at registration; server gap math uses it instead of assuming its own monitor model.
- **Known remaining gaps:** background images / multi-image sources are not file-transferred to remotes (graceful fallback); TimingSynchronizer drift loop still dead code; float-precision folding + RandomWalk seed-rotation continuity still open (see OpenIssues).

## 2026-07-07 — Documentation Audit (retroactive changelog)

The changelog was not maintained between 2026-03-29 and 2026-05-19. The entries below reconstruct that period from git history. Version 2.0 → 2.6.3 during this window.

## 2026-05-13 to 2026-05-19 — Remote Networking Fixes + Maintenance

- **FIXED: Remote networking — Pattern and Coloring modes** (`237cc4d`) — `PatternConfig` and `ColorGradingConfig` now serialized as JSON (`pattern_json` / `color_grading_json` in `SyncParameters`) so remote clients render the same pattern grid and colors as local monitors. **Known gap:** full `MovementConfig` (Reversed/Endless/wave/orbit/seed) and non-solid backgrounds are still NOT transmitted to remote clients — only `movement_type` int + bg color.
- **Updater fixed again** (`8437cde`), **NuGet packages updated** (`abcad3d`), **Graphify knowledge graph set up** (`f338c21`).

## 2026-05-02 to 2026-05-11 — Traveling Colors + Pattern Polish

- **NEW: Traveling Colors** (plan: `.docs/plans/2026-05-01-traveling-colors.md`) — three per-cell `ColorGradingMode` values (`TravelingRainbow`, `TravelingList`, `TravelingRandom`): each pattern cell gets a fixed color from its `(LogicalI, LogicalJ)` grid identity via `ColorGrader.ComputeForCell`, so colors travel with elements across monitors instead of cycling uniformly. Fixes in the series: hue distribution, TravelingList overflow, stale matrix in non-pattern paths.
- **Pattern fixes** — Pattern Life fixed, pattern gradient + white icon zones fixed, pattern fading improvements, "best fading logic".
- **Reverse option** extended to all applicable movement types (Linear, SineWave, Circular) (`81d6a21`).
- **Remote node fixes** (`0c27fd6`); refresh rate now always shown in topology nodes (`97d707e`) with mismatch warning (see tearing issue in OpenIssues).

## 2026-04-04 to 2026-04-24 — Corridor + IconZone Animation Systems

- **NEW: Corridor Animation System** (v1 → v2.3; plan: `.docs/plans/corridor-animation-system.md`) — `ThreeZone` background mode (top zone / darker middle corridor / bottom zone) constrains the animation path to a user-defined horizontal band so it avoids desktop-icon areas.
- **NEW: IconZone Animation** (v2.4 → v2.5; open issues: `.docs/2026.04_IconZoneAnimation_TODO.md`) — `IconZone` background mode reads real desktop icon positions (`SysListView32` / `LVM_GETITEMPOSITION`), builds occupancy zones (`ZonePlanner`), and routes the animation along an **A\* path** through icon-free space. Dynamic zone sizing, zone merge/expansion, feathered fades, per-cluster palettes (debug), desktop-refresh handling.
- **Sequential pathing** — corridor/A* path regenerates per traverse; RandomWalk iteration seed rotation; traverse-detection fix preventing 1-frame position jumps; Bounce traverse formula fix; sequential pathing reset fix.
- **FIXED: Flickering** — `ValidateRect` in `WM_PAINT` prevents invalidation feedback loop.
- **Debug overlay** — per-flag controls in main window toolbar (path, icon rects, zone band outlines, info panel); `ShowZoneBandOutlines` rendering implemented; `ToggleDebugOverlay` state sync fixed.
- **UI** — Animation Config dialog rework, uniform 28px toolbar control heights, icon zone config layout fixes.
- **Maintenance** — NuGets with vulnerabilities updated; `.worktrees/` gitignored.

## 2026-03-30 to 2026-03-31 — Static Images + Updater + Remote Animation

- **FIXED: Static image rendering** — JPG/PNG/BMP load as a single Magick.NET frame through the native D2D GIF pipeline.
- **Updater fixes** (multiple rounds), player deployment issues fixed, Bezier arrow head in topology view fixed, remote animation fixes.
- **Docs updated** with rendering architecture (`ab1b1bc`).

---

## 2026-03-29 — File Logging Fix + Remote Client D2D Wiring

### FIXED: File logging empty on remote clients
All service loggers in `TrayViewModel`, `MainWindowViewModel`, `WaBiBaBuSyService`, and all sub-services (WallpaperSyncClient, WallpaperPlaybackService, WallpaperSyncCoordinator, etc.) were constructed with private `LoggerFactory.Create(...AddConsole())` instances. These bypass `AppLogger` entirely — nothing reached the file provider.

**Fix:** Removed all `LoggerFactory.Create()` calls in production code. All logger creation now uses `AppLogger.CreateLogger<T>()` or `AppLogger.Factory`. Added `AppLogger.Factory` static property to expose the underlying `ILoggerFactory` for services that require it.

**Rule:** Never call `LoggerFactory.Create()` directly anywhere in the UI or Core layers.

### FIXED: D2D wallpaper not applied on remote clients (race condition)
`WallpaperSyncClient.ConnectAsync()` calls `StartSyncStream()` before returning, so gRPC commands can arrive immediately. Previously `D2DApplyDelegate` was set *after* `ConnectToServerAsync()` returned — a LOAD command arriving in that window saw `null` and silently fell back to the non-functional LibVLC path.

**Fix:** `ConnectToServerAsync(address, port, d2dApplyDelegate)` now accepts the delegate as a parameter and sets it on `WallpaperPlaybackService` immediately after construction, before returning. Both `TrayViewModel` and `MainWindowViewModel` pass the delegate at call time.

**Rule:** Always pass the D2D delegate to `ConnectToServerAsync`. Never call `SetD2DApplyDelegate` separately after connection. See Software Architecture doc for details.

### FIXED: `TrayViewModel.Connect()` async anti-pattern
Was `void Connect()` using `Task.Delay(2000).ContinueWith(async _ => {...})`. The `async` lambda inside `ContinueWith` returns `Task<Task>` — the outer task completes when the lambda starts, not when the body finishes. Exceptions were silently swallowed.

**Fix:** Changed to `async Task Connect()` with `await Task.Delay(2000)`.

### Tray menu simplified
Removed "Server Mode" submenu (Start Server / Stop Server) — server machine always opens main window. Removed "Client Mode" submenu — Connect and Disconnect are now top-level tray items.

---

## 2026-03-20 — Animation Mode Fix + Logging Diagnostics

- **FIXED: Distribution mode ignored** — `StartCrossScreen()` was always starting all monitors in spanning mode (regression from D2D architecture refactor). Now checks `DistributionMode` and passes `perMonitorMode` flag to `D2DCompositionService.InitializeAsync()`.
- **FIXED: File logging silent failure** — `FileLoggerProvider.GetWriter()` had empty `catch` block that swallowed all file creation errors. Now logs errors to `Console.Error`.
- **Added: Logging diagnostics** — `AppLogger.ApplyConfig()` attempts file creation when `LogToFile=true` and reports success/failure to console.
- **FIXED: CrossScreenConfigDialog PlatformImpl null** — Added input event guards for Avalonia timing issue.
- **Added: Architectural Invariants section** in `CLAUDE.md`.

## 2026-03-17 — UI Redesign: Animation Controls & Gallery

- **FIXED: Background color (ISSUE-011)** — `BackgroundColorDetector` auto-detects dominant edge color; manual hex override with preview.
- **Gallery selection highlighting** — Blue border (`#0078D4`) via `Classes.selected` binding.
- **Removed confusing Animation toggle** — Standard wallpaper controls always visible; added "Multi Monitor Animation" button.
- **"From Gallery..." stubs replaced** — Dialog auto-populates from `PreSelectedWallpaper`.
- **Topology animation indicators** — Green (`#00AA44`) for animating, gold (`#FFD700`) for current target.
- **Active Animation Info Panel** — Shows file, mode, speed, background color.
- **Clear Wallpaper command** — Stops animations, disposes all D2D/LibVLC renderers.
- **Logging UI complete (TASK-012)** — Settings panel with log level, component toggles, file output.

## 2026-02-26 — Native D2D Composition (All P0 Fixed)

- **Native D2D Composition** — Replaced GDI+ pipeline with pure Direct2D for GIF rendering in Player.D2D.
- **Persistent ID2D1DeviceContext** — No per-frame render target recreation.
- **GPU-resident GIF frames** — Magick.NET → `ID2D1Bitmap[]` (zero per-frame allocation).
- **FIXED: GIF speed (ISSUE-004)** — SpeedMultiplier applied to elapsed time.
- **FIXED: GIF looping (ISSUE-005)** — Modulo-based seamless infinite looping.
- **FIXED: Memory leak (ISSUE-007)** — No GDI+ Bitmap allocation in render loop.
- **FIXED: High CPU (TASK-009)** — No GDI+→D2D conversion per frame.
- **FIXED: UI freeze (ISSUE-002)** — Proper disposal via `DisposeNativeD2DResources()`.
- **Security:** Magick.NET upgraded to 14.10.3 (fixes 36 Dependabot alerts).

## 2026-02-05 — DXGI Flip Model + Explorer Crash Fixes

- **FIXED: Only first frame visible** — D2D render target recreated after every `Present()` for FlipSequential buffer rotation.
- **FIXED: Explorer crash on loop #5** — Replaced `WS_EX_TRANSPARENT` with `WS_EX_LAYERED + SetLayeredWindowAttributes`.
- **FIXED: Player crash on GIF loop #4-5** — Removed synchronous Stop/Play from `OnMediaEndReached`; uses seek-based looping.

## 2025-12-27 — Separate Player Process (Windows 11 24H2+)

- **CRITICAL FIX:** DXGI swap chain windows crash explorer when parented to desktop on 24H2+.
- **New `WaBiBaBuSy.Player.D2D` project** — Runs DXGI rendering in isolated process.
- **IPC Protocol** — stdin/stdout JSON for PARENT, COLOR, LOAD, EXIT commands.
- **D2DPlayerHost** — Spawns player processes, manages lifecycle.

## 2025-12-24 — Native Win32 Architecture

- **Native Win32 window creation** — Eliminated Windows Forms dependency that caused freezing.
- **D2DVorticeRenderer rewrite** — `CreateWindowEx`, `RegisterClassEx`, native window procedure.
- **WS_EX_TRANSPARENT** — Mouse clicks pass through to desktop icons.

## 2025-12-11 — Direct2D Architecture Fix

- **Windows 11 24H2+ compatibility** — D2D renderer creates dedicated window instead of `GetDC(WorkerW)`.
- **HeadlessMode** — `WallpaperConfig.HeadlessMode` for composition pipeline (no window).
- **Timing sync fixed** — Animation position and frame selection use same elapsed time.
- **Timestamp overflow fixed** — No more `int.MinValue` overflow.

## 2025-11-07 — GIF & Video Support

- **GIF animation** — `GetFrameAtPosition()` with frame timing calculation.
- **Video playback** — Frame caching + LRU eviction (MVP uses placeholder frames).

## 2025-11-03 to 2025-11-05 — Distributed Animation System

- **Phases 1-4 complete** — Animation distribution, timing sync, sequential/simultaneous modes, UI integration.
- **Gallery-based selection** — Multi-select dialog for animation + background.
- **Auto-update system** — Version detection, chunked download, SHA-256 verification, standalone updater.
- **LibVLC pre-initialization** — Startup time 9s → instant.
- **Network topology** — Rectangle drag + Ctrl+Click multi-select.

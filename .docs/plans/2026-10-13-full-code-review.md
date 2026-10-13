# Full Code Review — 2026-10-13

Whole-codebase review on `a3745d2` (~50k lines production C#/XAML, tests excluded except as evidence). Split into 8 parts by subsystem, each reviewed independently for correctness bugs, invariant violations (CLAUDE.md "Architectural Invariants"), resource/thread-safety issues, security at trust boundaries, and dead code.

| Part | Scope | ~Lines |
|---|---|---|
| 1. Deterministic core | `Models/Wallpaper`, `Models/Topology`, `Models/Show`, `Models/Animation`, `Models/Content`, `Models/Configuration`, other `Models/*.cs` | 5.5k |
| 2. Networking & sync | `Grpc/` (proto + `WallpaperSyncService`), `Core/Services/Networking`, `WallpaperSyncCoordinator`, `WaBiBaBuSyService`, `Models/Networking`, `ClientPlaybackDelegates`, `RoomNodeSources`, `Show/ShowEventCodec` | 6.4k |
| 3. Player & rendering | `Player.D2D`, `Player.Common`, `WallpaperEngine/Direct2D`, `Desktop`, `Native`, `Composition`, `Helpers`, `Show`, `Services` | 9k |
| 4. Core animation / content | `Core/Services/Animation`, `ContentCacheManager`, `ConfigurationService`, `WallpaperPlaybackService`, `Desktop`, `FullscreenDetectionService`, `ThumbnailCaptureService` | 4.4k |
| 5. UI hosts & main VM | `UI/Services` (`ServerPlaybackHost`, `RemotePlaybackHost`, hotkeys, …), `MainWindowViewModel*.cs` | 5.4k |
| 6. UI rest | `UI/Controls` (RoomView, ScenePainter), other ViewModels, Views (`.axaml`), Converters, App/Program | 5.5k |
| 7. Test mode | `Core/Services/Testing`, `Models/Testing`, `UI/Services/Testing`, `TestScenarios/*.json` | 4k |
| 8. Update, logging, legacy | `Core/Services/Update`, `Updater/`, `Common/`, `Models/Update`, `Core/Services/Logging`, `WallpaperEngine/Renderers`, `Player.Image`, `D2DTest`, `Installer/` | 4.5k |

Output: one findings list per part (severity, file:line, failure scenario), consolidated below. Fixes are a separate step, ordered by severity.

## Results

8/8 parts done. ✔ = re-verified against the code by the coordinator; everything else is reviewer-traced, verify before fixing. Part refs in brackets (P1–P8). Grouped into fix batches, in recommended order.

### Batch A — before the multi-machine session (test results must be trustworthy)

**Status 2026-10-13: A1–A10 fixed on `fix/review-batch-a`** (934 tests green). Open: A5's mixed-resolution parity warning has no unit test; A9 (video on the shared clock: seek > 200 ms drift, ±10 % rate nudge below, LibVLC-internal loop) is build- and math-tested only — test-mode probes report clock time, not the video position, so check by eye in the session: one video scene on 2+ machines, frames line up, still aligned after a loop and after 10 min.

| # | Sev | Finding | Where |
|---|---|---|---|
| A1 ✔ | HIGH | `Main` is `void` → process exit code always 0; `--test-run --exit` can never report 1/2 [P6] | `UI/Program.cs:13` |
| A2 ✔ | HIGH | Only `Fail` → exit 1; missing / non-answering nodes, lost nodes, all-Skipped runs end Warn/Skipped → exit 0 (incl. `recovery.json` rejoin probes) [P7] | `TrayViewModel.cs:302`, `DriftAnalyzer.cs:56,86`, `TestRunner.cs:194` |
| A3 ✔ | HIGH | Render-time-fallback nodes excluded from the drift verdict when ≥2 nodes have present times (`renderSpread` informational only) [P7] | `DriftAnalyzer.cs:75-83` |
| A4 | MED | Crashed local player / short remote reply → no sample at all, never "missing" [P7] | `ServerPlaybackHost.cs:1067`, `TestRunner.cs:586` |
| A5 | MED | Lost capture → Skipped; parity groups by resolution, mixed resolutions → 0 comparisons = Skipped [P7] | `TestRunner.cs:726-733` |
| A6 | MED | Scenario/scene JSON ignores unknown fields (typo `minRemoteNode` → passes with 0 clients) [P7] | `ScenarioLoader.cs:10`, `SceneFile.cs:13` |
| A7 | MED | Null CPU/GPU perf values pass thresholds silently [P7] | `TestRunner.cs:802` |
| A8 | LOW | `clock-skew.json` lists 2 remotes but README says 1+ → fails with one client [P7] | `TestScenarios/clock-skew.json:5` |
| A9 ✔ | HIGH | **Video is not on the shared clock** — `Play()` at load, no seek anywhere; per-machine loop. MVP criterion 1 will fail [P3] | `Player.D2D/Program.cs:1660` |
| A10 ✔ | MED-HIGH | `HexToRgb` `byte.Parse` throws on bad hex → typo'd grading color throws every frame on every node [P1] | `ColorGrader.cs:205` |

### Batch B — security / trust boundary (plain-HTTP, unauthenticated gRPC)

**Decision 2026-10-13: trusted LAN** — no auth / signing. **Status: B1, B2, B4, B5 fixed** on `fix/review-batch-a` (B2 also fixes E2: cache files live under `{cache}\{hash16}\{name}`). **B3 deferred by the decision** (its authenticity parts only matter against a malicious server); the update bugs that break honest updates stay in batch E (E1, E4) and the installed-updater self-lock in F.

Trust model: anything that can answer as "the server" (rogue LAN host, MITM) is fully trusted; SHA-256 checks only catch corruption. Decide: accept for trusted LAN, or add a shared-secret HMAC / signed packages.

| # | Sev | Finding | Where |
|---|---|---|---|
| B1 ✔ | HIGH | Server `TransferContent`: client-supplied filename → arbitrary file write on the server; RPC has no callers → delete [P2] | `WallpaperSyncService.cs:463` |
| B2 ✔ | HIGH | Client download: server filename → `Path.Combine` → arbitrary write before hash check; empty hash skips check [P2,P4] | `WallpaperSyncClient.cs:757` |
| B3 | HIGH | Update package `updater/` exe run elevated, never hash-checked; unlisted `binaries/**` applied; TOCTOU in user-writable extract dir; downgrade allowed; version string used raw in paths; fail-open when hash missing [P8] | `UpdateApplicator.cs:39-84`, `UpdateManager.cs`, `FileReplacer.cs` |
| B4 | MED | Imported scene/preset may reference `..\..\any\file` → bundled + distributed to all clients [P4] | `PresetStore.cs:97`, `SceneFile.cs:108` |
| B5 | LOW | `SubmitProbeResult` accepted with test mode off, buffers 128 MB before checks [P7] | `WallpaperSyncService.cs:1380` |

### Batch C — command ordering & lifecycle races (one recurring pattern: start/stop/apply not serialized)

| # | Sev | Finding | Where |
|---|---|---|---|
| C1 | HIGH | Client runs each sync command in its own `Task.Run`; STOP during a LOAD download, or two LOADs, finish out of order → stopped/old scene comes back [P2,P4] | `WallpaperPlaybackService.cs:210-225` |
| C2 | HIGH | Server apply failure leaks local players, host left "not running" (■ no-op) [P5] | `ServerPlaybackHost.cs:1182-1287` |
| C3 | HIGH | Toolbar Start bypasses the Play guard → concurrent applies orphan players; Stop / Clear all during an in-flight start are ignored [P5] | `MainWindowViewModel.cs:1073,2265`, `ServerPlaybackHost.cs:426` |
| C4 | MED-HIGH | `RemotePlaybackHost` apply/stop unserialized, no dispose on failure [P5] | `RemotePlaybackHost.cs:96-279` |
| C5 | MED | Playlist Start/Stop unserialized; loop exception skips cleanup [P4] | `PlaylistOrchestrator.cs:128-304` |
| C6 | MED | Item switch only stops the new item's monitors/seats; others keep the old item [P5] | `ServerPlaybackHost.cs:1182` |
| C7 | MED | Heartbeat sweep removes a freshly re-registered session (no compare-and-remove) [P2] | `WallpaperSyncService.cs:109` |
| C8 | MED | One stalled client blocks broadcasts to everyone after it (no write timeout, sequential awaits); no HTTP/2 keep-alive → dead streams pile up [P2] | `WallpaperSyncService.cs:774`, `WallpaperSyncServerHost.cs:128` |
| C9 | MED | Disconnect ignored during reconnect; Connect then builds a 2nd client with the same id → they kick each other [P2] | `WaBiBaBuSyService.cs:324,385` |
| C10 | HIGH | No single-instance guard; 2nd instance kills the 1st's players [P6] | `App.axaml.cs`, `TrayViewModel.cs:87` |
| C11 | MED | Ack sets status `Syncing` → node treated offline up to 5 s [P2] | `WallpaperSyncService.cs:363`, `RoomNodeSources.cs:141` |

### Batch D — player robustness

| # | Sev | Finding | Where |
|---|---|---|---|
| D1 | HIGH | LOAD disposes D2D resources / frees video buffers on the stdin thread while the render thread draws → use-after-free crash on scene switch [P3] | `Program.cs:3896-4233` vs `1167-1360` |
| D2 | HIGH | No device-lost (TDR / RECREATE_TARGET) handling → permanent freeze + log flood [P3] | `Program.cs:1167-1395` |
| D3 | HIGH | Crashed/exited player never respawned (explorer restart) [P3] | `D2DPlayerHost.cs:704` |
| D4 | MED | Positional stdout reply pairing desyncs after any stray `READY`; LOAD/START/PARENT reads have no timeout [P3] | `D2DPlayerHost.cs:259-622` |
| D5 | MED | No WorkerW → window shown as top-level popup instead of failing [P3] | `D2DPlayerHost.cs:232` |
| D6 | MED | Icon-zone fade mask stale after icon refresh; `SendMessage` to explorer on render thread w/o timeout [P3] | `Program.cs:3431, 229` |
| D7 ✔ | LOW | `ReadProcessMemory` `out uint` for `SIZE_T` → 4-byte stack overrun on x64 [P3] | `Program.cs:201`, `Win32Interop.cs:377` |
| D8 | MED | LibVLC renderer frees frame buffer before `Stop()`; post-dispose work items [P8] | `VideoWallpaperRenderer.cs:959-1008` |

### Batch E — correctness / determinism / persistence

| # | Sev | Finding | Where |
|---|---|---|---|
| E1 ✔ | HIGH | Auto-update never starts: install dir ends in `\` → `"…\"` swallows the rest of the args [P8] | `UpdateApplicator.cs:68` |
| E2 | HIGH | Client cache files named by original filename → same-named assets of different scenes overwrite each other [P2,P4] | `WallpaperSyncClient.cs:758` |
| E3 | HIGH | Cache eviction deletes `cache-index.json` and current-show assets; `EnsureSpace(0)` [P4] | `ContentCacheManager.cs:107` |
| E4 | HIGH | Update rollback restores only `WaBiBaBuSy.*`, not deps → half-replaced install [P8] | `FileReplacer.cs:154,190` |
| E5 | MED | Sequential IconZone per-lap path variation reaches local players only → remotes diverge [P5] | `ServerPlaybackHost.cs:1428` |
| E6 | MED | Endless + pattern jitter/rotation hash on reset `i` → grid pops each step [P1] | `PatternLayout.cs:125` |
| E7 | MED | `ComputeLapMs` ignores Ring wrap → SnapToLap cuts mid-lap [P1] | `PlaylistScheduler.cs:27` |
| E8 | MED | Missing extra image silently dropped → different `srcCount` on one client [P1] | `ContentAssetRef.cs:74` |
| E9 | MED | Scene editor round-trip drops Start/End/Orbit points, `CenterInitialPosition` [P6] | `CrossScreenConfigViewModel.cs:554` |
| E10 | MED | Preview ≠ player for Traveling + Pattern + Linear (tileAlignStepX / cellLogicalOffsetI) [P6] | `ScenePainter.cs:155` |
| E11 | MED | Non-atomic `File.WriteAllText` for cache index, config, topology, seatmap, playlists; config parse error → defaults overwrite user config [P1,P4] | multiple — one shared atomic-write helper |
| E12 | LOW-MED | `new Random(seed)` in RandomWalk not guaranteed stable across runtimes [P1] | `MovementCalculator.cs:300` |
| E13 | LOW-MED | Duplicate hostname steals the other machine's seat [P1] | `TopologyStore.cs:87` |
| E14 | LOW | `SeatMapStore`, `ShowEventSettingsStore`, `PresetStore`, `PlaylistStore` bypass `AppDataPaths` test redirect [P1] | — |
| E15 | MED | Tray "Connect" falls back to `localhost` instead of the saved server address [P6] | `TrayViewModel.cs:194` |
| E16 | LOW | Uninstall deletes presets/playlists/topology; `.iss` fallback version 2.6.3 vs csproj 2.6.5 [P8] | `WaBiBaBuSy.iss:7,119` |
| E17 | LOW | Bitmaps never disposed (thumbnails every 2 s, preview), 33 ms preview timer runs while minimized; log retention unbounded [P5,P6,P8] | — |

### Batch F — dead code (verify no callers, then delete)

- `WaBiBaBuSy.Player.Image/` + UI csproj reference/copy targets (confirmed: never launched) [P8]
- `WaBiBaBuSy.D2DTest/` + `WallpaperEngine/Direct2D/D2DVorticeRenderer.cs`, `Direct2DInterop.cs` (main-process DXGI — violates the invariant) [P3]
- `Player.Common/ProcessCommunicator.cs` + `PlayerMessageConverter` + legacy message types [P3]
- `D2DPlayerHost.SetFrameAsync/SetColorAsync/EnableTestModeAsync`, player `TEST/COLOR` branches [P3]
- `AnimationService`, `AnimationFileDownloader`, `ClientAnimationRenderer` (never instantiated); `AnimationOrchestrator`/`AnimationDistributor` + Phase-3 RPC stubs (check callers) [P2,P4]
- `TransferContent` RPC + client/service wrappers (B1) [P2]
- Legacy coordinator senders (`BroadcastLoadWallpaperAsync`, …) — check `MainWindowViewModel` callers [P2]
- `ServerPlaybackHost.ApplyWallpaperLocally` (no callers) [P8]
- Models: `SyncSchedule`, `WallpaperConfig`, `ScreenConfiguration`, `Animation/AnimationMetadata.BackgroundLayerConfig` (duplicate name), `AnimationTimingSync` [P1]
- `FileReplacer.CleanupBackup`, `ProcessMonitor.IsProcessRunning` — but **use** `UpdateApplicator.ExtractUpdaterToTemp` (fixes the installed-updater self-lock) [P8]
- Duplicate sanitizers (`PlaylistStore.Sanitize` → `PresetStore.Sanitize`), 4× copy-pasted player teardown loop, 4× first-frame show-window block [P3,P4,P5]

### Coverage gaps

Not reviewed: `Core/Services/Desktop/` (DesktopIconService, PaletteGenerator), `Core/Interfaces/`, `Player.D2D/DwmTiming.cs`, second half of `Program.Effects.cs`, `UI/Converters`, `.axaml` files (compiled bindings catch missing properties at build).

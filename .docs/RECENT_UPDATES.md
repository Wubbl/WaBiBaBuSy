# WaBiBaBuSy - Recent Updates & Changelog

**Last Updated:** 2026-03-29

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

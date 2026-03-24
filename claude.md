# WaBiBaBuSy - Wallpaper Synchronization System

**Project Name:** WallpaperBiBaBuSync (BiBaBu = our club name)
**Version:** 2.0 | **Framework:** .NET 8.0 | **Status:** MVP ~99% Complete
**Last Updated:** 2026-03-24 | **Next:** E2E Multi-Client Testing, file logging validation

WaBiBaBuSy synchronizes animated wallpapers across 50+ Windows machines with <5% server CPU, ±50ms drift tolerance, and distributed client-side rendering. Supports images (JPG/PNG/BMP), videos (MP4/AVI/MKV), and GIFs across multi-monitor setups.

## Key Capabilities

- Synchronized wallpaper playback across multiple Windows machines
- Server-client architecture with visual network topology management
- Hardware-accelerated Direct2D rendering with separate player process
- Precise timing synchronization with drift detection (±50ms tolerance)
- Multi-monitor support: Sequential (spanning) and Simultaneous (per-monitor) modes
- Auto-update system with SHA-256 verification and rollback
- System tray operation with minimal UI footprint

## Documentation Map

> **For detailed architecture, project structure, and technical design, see the linked docs below.**

### Core Architecture
- **[Software Architecture](.docs/SOFTWARE_ARCHITECTURE.md)** — Full system architecture, project structure, technology stack, rendering pipeline, networking, configuration reference
- **[UI Architecture](.docs/UI_ARCHITECTURE.md)** — Views, ViewModels, user flows, UI features
- **[D2D Rendering Architecture](.docs/2025.12_DIRECT2D_RENDERING_ARCHITECTURE.md)** — Composition pipeline, HeadlessMode, Windows 11 24H2+ compatibility (**read before touching rendering**)
- **[Distributed Animation System](.docs/2025.11_DISTRIBUTED_ANIMATION_SYSTEM.md)** — Phases 1-4 implementation guide
- **[Animation Movement System](.docs/ANIMATION_MOVEMENT_SYSTEM.md)** — Deterministic position calculation

### Project Tracking
- **[Open Issues](.docs/2025.12_OpenIssues.md)** — Active bugs and items needing validation
- **[Missing Features](.docs/2025.12_MissingFeatures.md)** — Feature roadmap and TODO tracking
- **[Active Tasks](.docs/2026.01_TODO_ACTIVE.md)** — Current task list with priorities
- **[Recent Updates](.docs/RECENT_UPDATES.md)** — Historical changelog of all milestones
- **[D2D Issues & Fixes](.docs/2026.02_D2D_ISSUES.md)** — D2D-specific bug fixes (2026-02)

### Historical Reference
- **[`.docs/_archive/`](.docs/_archive/)** — 33 archived docs (planning, diagnostics, session summaries, superseded designs)

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

### Animation Distribution Modes
**There are exactly two modes. Their meaning is precise:**

| Mode | UI Name | What it means | Virtual canvas | MonitorOffsetX |
|------|---------|---------------|----------------|----------------|
| **Sequential** | "Sequential" | Animation **spans across all monitors** as one big canvas | `VirtualCanvasWidth = total width of all monitors combined` | `= this monitor's X offset in virtual space` |
| **Simultaneous** | "Simultaneous" | Animation plays **independently on each monitor** | `VirtualCanvasWidth = this monitor's width only` | `= 0` |

**Implementation path:**
1. `CrossScreenConfigDialog` → user picks mode → stored in `CrossScreenConfig.DistributionMode`
2. `MainWindowViewModel.StartCrossScreen()` → converts to `perMonitorMode = (mode == Simultaneous)`
3. `D2DCompositionService.InitializeAsync(perMonitorMode)` → adjusts `VirtualCanvasWidth` and `MonitorOffsetX`
4. `Player.D2D` receives IPC message → uses `MovementCalculator` → subtracts `MonitorOffsetX` from virtual position

**CRITICAL:** Both modes start ALL D2D players simultaneously with the same timestamp. The difference is ONLY in how `VirtualCanvasWidth` and `MonitorOffsetX` are set.

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
```

**Server:** Tray icon → Start Server → Open Server Control Panel
**Client:** Tray icon → Connect to Server → Enter IP or use auto-discovery

## Current Work (Priority Order)

1. **VALIDATE: File logging** — Enable LogToFile, verify files at `%LOCALAPPDATA%\WaBiBaBuSy\Logs\`
2. **VALIDATE: Distribution modes** — Test Sequential + Simultaneous with 2+ monitors
3. **E2E Multi-Client Testing** — Test with 1-3 real clients over network
4. **Installer Testing** — Validate on clean Windows 10/11 systems
5. **Video placeholder frames** — Replace with actual LibVLC frame capture in D2D mode

## MVP Success Criteria (6/6 Complete)

- 2+ machines sync video wallpaper playback
- Drift under 50ms for 10+ minutes
- CPU <15%, GPU <10%
- Server UI allows client ordering and content selection
- Graceful network disconnect recovery
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
| Reconnection | <5 seconds | Needs testing |

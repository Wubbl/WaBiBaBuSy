# WaBiBaBuSy - Software Architecture

**Last Updated:** 2026-03-24

This document describes the complete software architecture of WaBiBaBuSy. For project overview and development guidelines, see the root `CLAUDE.md`.

---

## System Overview

WaBiBaBuSy is a distributed wallpaper synchronization system. A single **server** orchestrates playback timing and content distribution; multiple **clients** render wallpapers locally on their desktops. Communication uses gRPC over LAN.

```
┌─────────────────────────────────────────────────────────────┐
│                    WaBiBaBuSy Ecosystem                      │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│  ┌──────────────┐                    ┌──────────────┐       │
│  │   Server     │◄──────gRPC────────►│   Client 1   │       │
│  │   Machine    │                    └──────────────┘       │
│  │              │                                           │
│  │  - Timing    │                    ┌──────────────┐       │
│  │  - Ordering  │◄──────gRPC────────►│   Client 2   │       │
│  │  - Content   │                    └──────────────┘       │
│  │    Dist.     │                                           │
│  └──────────────┘                    ┌──────────────┐       │
│                      ◄──────gRPC────►│   Client N   │       │
│                                      └──────────────┘       │
└─────────────────────────────────────────────────────────────┘
```

---

## Project Structure

```
WaBiBaBuSy/
├── WaBiBaBuSy.Core/              # Core business logic (no UI dependency)
│   ├── Interfaces/
│   │   └── IWallpaperRenderer.cs       # Renderer abstraction
│   └── Services/
│       ├── WaBiBaBuSyService.cs        # Main orchestration service
│       ├── WallpaperPlaybackService.cs # Playback control & drift correction
│       ├── WallpaperSyncCoordinator.cs # Sync coordination
│       ├── ConfigurationService.cs     # Config management
│       ├── ThumbnailCaptureService.cs  # Thumbnail generation
│       ├── Animation/                  # Distributed animation system
│       │   ├── AnimationDistributor.cs       # Server-side distribution
│       │   ├── AnimationOrchestrator.cs      # Sequential/simultaneous scheduling
│       │   ├── AnimationService.cs           # Animation lifecycle
│       │   ├── AnimationFileDownloader.cs    # Client-side file caching
│       │   ├── ClientAnimationRenderer.cs    # Client-side rendering
│       │   └── TimingSynchronizer.cs         # Timing broadcast & drift correction
│       ├── Networking/                 # gRPC communication layer
│       │   ├── WallpaperSyncClient.cs        # Client-side gRPC
│       │   ├── WallpaperSyncServerHost.cs    # Server-side gRPC host
│       │   ├── MdnsServerService.cs          # mDNS server advertisement
│       │   └── MdnsClientDiscoveryService.cs # mDNS client discovery
│       ├── Logging/                    # Logging infrastructure
│       │   ├── AppLogger.cs                  # Static factory (volatile config)
│       │   └── FileLoggerProvider.cs         # Rolling daily file logs
│       └── Update/                     # Auto-update system
│           ├── UpdateManager.cs              # Update lifecycle orchestration
│           ├── UpdateDownloader.cs           # gRPC download with progress
│           ├── UpdateVerifier.cs             # SHA-256 verification
│           └── UpdateApplicator.cs           # Launch standalone updater
│
├── WaBiBaBuSy.Grpc/              # gRPC contracts & services
│   ├── Protos/
│   │   └── wabibabusy.proto            # All service contracts
│   └── Services/
│       └── WallpaperSyncService.cs     # gRPC service implementation
│
├── WaBiBaBuSy.WallpaperEngine/   # Wallpaper rendering engine
│   ├── Renderers/
│   │   ├── VideoWallpaperRenderer.cs         # LibVLC video (MP4/AVI/MKV/...)
│   │   ├── ImageWallpaperRendererLibVLC.cs   # LibVLC image (JPG/PNG/BMP)
│   │   ├── GifWallpaperRenderer.cs           # GIF with Magick.NET frame extraction
│   │   └── CrossScreenFrameRenderer.cs       # Cross-screen frame coordination
│   ├── Composition/                          # Direct2D composition pipeline
│   │   ├── D2DCompositionService.cs          # Orchestrates D2D players
│   │   ├── CompositionRenderer.cs            # Background + content compositing
│   │   ├── AnimationLayerRenderer.cs         # Content layer (GIF/image/video)
│   │   ├── BackgroundLayerRenderer.cs        # Background layer (color/gradient)
│   │   ├── VirtualCanvasManager.cs           # Multi-screen layout
│   │   └── ScreenMapping.cs                  # Monitor geometry mapping
│   ├── Direct2D/
│   │   ├── D2DPlayerHost.cs                  # Spawns/manages D2D player processes
│   │   ├── D2DVorticeRenderer.cs             # Vortice Direct3D11/Direct2D renderer
│   │   └── Direct2DInterop.cs               # Windows API interop
│   ├── Native/
│   │   ├── DesktopWindowManager.cs           # WorkerW desktop integration
│   │   └── Win32Interop.cs                   # Win32 P/Invoke declarations
│   ├── Services/
│   │   ├── BackgroundColorDetector.cs        # Edge pixel color auto-detection
│   │   └── LibVLCPreloader.cs                # LibVLC pre-initialization
│   └── Helpers/
│       └── WindowUtil.cs                     # Window utility functions
│
├── WaBiBaBuSy.Player.D2D/        # Separate DXGI player process
│   └── Program.cs                      # Win32 window + DXGI swap chain + IPC
│
├── WaBiBaBuSy.Player.Common/     # Shared player IPC protocol
│   ├── ProcessCommunicator.cs          # stdin/stdout JSON IPC
│   └── Messages/                       # 11 IPC message types
│
├── WaBiBaBuSy.UI/                # Avalonia User Interface
│   ├── Views/
│   │   ├── MainWindow.axaml                  # Main window (server panel + gallery)
│   │   ├── SettingsWindow.axaml              # Settings dialog
│   │   ├── CrossScreenConfigDialog.axaml     # Animation mode configuration
│   │   └── WallpaperMultiSelectDialog.axaml  # Gallery multi-select
│   ├── ViewModels/
│   │   ├── MainWindowViewModel.cs            # Primary UI logic
│   │   ├── TrayViewModel.cs                  # System tray menu
│   │   ├── SettingsViewModel.cs              # Settings management
│   │   ├── CrossScreenConfigViewModel.cs     # Animation config logic
│   │   ├── WallpaperMultiSelectDialogViewModel.cs
│   │   ├── WallpaperItemViewModel.cs         # Gallery item model
│   │   ├── ClientNodeViewModel.cs            # Topology node model
│   │   └── ViewModelBase.cs                  # Base class
│   ├── Converters/                           # Value converters
│   │   ├── BoolToTextConverter.cs
│   │   └── HexToColorConverter.cs
│   ├── Services/
│   │   └── VideoThumbnailGenerator.cs        # Video thumbnail extraction
│   └── Assets/                               # Icons, images
│
├── WaBiBaBuSy.Models/            # Shared data models
│   ├── AppConfiguration.cs             # Root configuration
│   ├── ClientInfo.cs                   # Client identity + status
│   ├── ClientStatus.cs                 # Connection state enum
│   ├── OperationMode.cs                # Server/Client mode enum
│   ├── ScreenConfiguration.cs          # Monitor geometry
│   ├── SyncSchedule.cs                 # Sync timing schedule
│   ├── WallpaperConfig.cs              # Wallpaper settings
│   ├── WallpaperState.cs               # Current playback state
│   ├── WallpaperType.cs                # Image/Video/GIF enum
│   ├── Configuration/
│   │   ├── ConfigurationManager.cs     # JSON config load/save
│   │   ├── ServerConfiguration.cs      # Server-specific config
│   │   ├── ClientConfiguration.cs      # Client-specific config
│   │   └── LoggingConfiguration.cs     # Logging config model
│   ├── Wallpaper/
│   │   ├── CrossScreenConfig.cs        # Multi-monitor config
│   │   ├── MovementCalculator.cs       # Deterministic position math
│   │   ├── WallpaperGallery.cs         # Gallery model
│   │   └── WallpaperGalleryItem.cs     # Gallery item model
│   ├── Animation/
│   │   ├── AnimationMetadata.cs        # Animation file metadata
│   │   ├── AnimationTimingSync.cs      # Timing sync data
│   │   └── AnimationCompleteReport.cs  # Completion report
│   └── Update/
│       ├── UpdateInfo.cs               # Available update metadata
│       ├── UpdateManifest.cs           # Package manifest + checksums
│       └── UpdateStatus.cs             # Update operation status
│
├── WaBiBaBuSy.Common/            # Shared utilities
│   ├── PathHelper.cs                   # Path resolution helpers
│   └── Version/
│       └── VersionInfo.cs              # Semantic version comparison
│
├── WaBiBaBuSy.Updater/           # Standalone updater application
│   ├── Program.cs                      # CLI updater logic
│   ├── ProcessMonitor.cs              # Process lifecycle management
│   └── FileReplacer.cs               # Safe file replacement + rollback
│
├── WaBiBaBuSy.D2DTest/           # D2D diagnostic/test project
├── WaBiBaBuSy.Player.Image/      # Legacy Windows Forms image player
└── Installer/                     # Inno Setup installer scripts
```

---

## Technology Stack

| Component | Technology |
|-----------|-----------|
| **Framework** | .NET 8.0 |
| **UI** | Avalonia 11.x + CommunityToolkit.Mvvm |
| **Communication** | gRPC + Protobuf |
| **Rendering** | Vortice.Windows (Direct3D11/Direct2D) + LibVLCSharp (video) + Magick.NET (GIF) |
| **Windowing** | Native Win32 API (no Windows Forms) |
| **DI / Config** | Microsoft.Extensions.* |
| **Discovery** | Makaretu.Dns (mDNS) |
| **Logging** | Custom AppLogger (volatile config, rolling files) |

---

## Core Architecture Patterns

### Clean Architecture
- **Core** is independent of UI and infrastructure
- **Models** are shared across all layers
- **WallpaperEngine** implements rendering (infrastructure)
- **UI** depends on Core for orchestration

### MVVM (UI Layer)
- Views (AXAML) bind to ViewModels (C#)
- ViewModels use CommunityToolkit.Mvvm `[ObservableProperty]` and `[RelayCommand]`
- No code-behind logic in views

### Dependency Injection
- Constructor injection only
- Services registered in `App.axaml.cs`

---

## Rendering Pipeline

### Layer Architecture

The rendering system has three tiers:

1. **Standalone Renderers** (Video, Image, GIF) - Create their own windows, parent to WorkerW
2. **D2D Composition Pipeline** - For multi-monitor animations with background compositing
3. **Separate Player Process** (`Player.D2D`) - DXGI swap chain in isolated process (Windows 11 24H2+ fix)

### D2D Player Architecture (Critical)

```
Main Process (WaBiBaBuSy.UI)              Player Process (Player.D2D)
┌──────────────────────┐                  ┌──────────────────────┐
│ D2DCompositionService│                  │ Program.cs           │
│   ├─ Picks mode      │    stdin/stdout  │   ├─ DXGI swap chain │
│   ├─ Calculates      │───── JSON IPC──►│   ├─ Magick.NET GIF  │
│   │  canvas + offsets │                  │   ├─ MovementCalc    │
│   └─ Spawns players  │                  │   └─ D2D rendering   │
└──────────────────────┘                  └──────────────────────┘
```

**Key principle:** Main process sends animation **metadata** (file path, canvas size, offsets, movement config). Players render locally. No per-frame data crosses the IPC boundary.

**Why separate process:** DXGI swap chain windows crash `explorer.exe` when parented to desktop on Windows 11 24H2+. See `.docs/2025.12_DIRECT2D_RENDERING_ARCHITECTURE.md`.

### Windows Desktop Integration

- **WorkerW technique** parents wallpaper windows behind desktop icons
- Uses `WS_EX_LAYERED + SetLayeredWindowAttributes` (NOT `WS_EX_TRANSPARENT` which crashes explorer on 24H2+)
- Z-order: `SetParent` first, then `SetWindowPos` with DefView reference

---

## Networking Architecture

### gRPC Services

| RPC | Direction | Purpose |
|-----|-----------|---------|
| `RegisterClient` | Client → Server | Registration with version info |
| `Heartbeat` | Bidirectional | 5s health checks |
| `SyncStream` | Server → Client | LOAD, PLAY, PAUSE, SEEK, STOP commands |
| `TransferContent` | Server → Client | Chunked file transfer + SHA-256 |
| `GetTopology` | Client → Server | Network topology query |
| `UpdateClientOrder` | Client → Server | Topology reordering |
| `CheckForUpdates` | Client → Server | Version check |
| `DownloadUpdate` | Server → Client | Update package streaming |

### mDNS Auto-Discovery
- Server advertises `_wabibabusy._tcp.local` via mDNS
- Clients browse for service on LAN
- Fallback: manual IP entry in UI

### Content Distribution
- Server streams content in chunks to clients
- Clients cache files locally in `%LOCALAPPDATA%\WaBiBaBuSy\Cache`
- SHA-256 integrity verification on all transfers

---

## Distributed Animation System

**Architecture:** Server orchestrates timing, clients render locally.

### Two Distribution Modes

| Mode | Description | Virtual Canvas | MonitorOffsetX |
|------|-------------|----------------|----------------|
| **Sequential** | Animation spans across all monitors as one canvas | Total width of all monitors | Monitor's X offset |
| **Simultaneous** | Same animation plays independently on each monitor | Single monitor width | 0 |

### Data Flow

1. `CrossScreenConfigDialog` → user picks mode → `CrossScreenConfig.DistributionMode`
2. `MainWindowViewModel.StartCrossScreen()` → `perMonitorMode = (mode == Simultaneous)`
3. `D2DCompositionService.InitializeAsync(perMonitorMode)` → sets `VirtualCanvasWidth` + `MonitorOffsetX`
4. `Player.D2D` receives IPC → `MovementCalculator` positions animation deterministically

### Timing Synchronization
- Server broadcasts timing every 1 second
- Clients independently calculate position from `elapsedTime + MovementCalculator`
- Drift detection: checks every 1s, corrects if >50ms

See `.docs/2025.11_DISTRIBUTED_ANIMATION_SYSTEM.md` for the complete implementation guide.

---

## Auto-Update System

```
Server                          Client                        Updater
  │                               │                              │
  │◄─── RegisterClient ──────────│                              │
  │  (includes version info)      │                              │
  │                               │                              │
  │── UpdateAvailable ──────────►│                              │
  │                               │                              │
  │◄─── DownloadUpdate ─────────│                              │
  │── chunks + SHA-256 ─────────►│                              │
  │                               │── Launch updater ──────────►│
  │                               │   (close main app)          │
  │                               │                              │── Replace files
  │                               │                              │── Verify
  │                               │◄──────── Relaunch ─────────│
```

- Semantic versioning (Major.Minor.Patch + build number)
- Standalone updater replaces binaries while main app is closed
- Automatic rollback on failure

---

## Logging Architecture

- **`AppLogger`** is a static factory with volatile `LoggingConfiguration`
- `ApplyConfig()` takes effect immediately (no factory rebuild)
- **`FileLoggerProvider`** creates rolling daily files in configured `LogDirectory`
- Config persisted to `%APPDATA%\WaBiBaBuSy\logging-config.json` (separate from main config)
- 7 component toggles for fine-grained log filtering

---

## Configuration

**Location:** `%APPDATA%\WaBiBaBuSy\config.json`

```json
{
  "Mode": "Client",
  "Server": {
    "Port": 50051,
    "MaxClients": 10,
    "ContentDirectory": "C:\\WaBiBaBuSy\\Content",
    "EnableAutoDiscovery": true,
    "UpdateManagement": { ... }
  },
  "Client": {
    "ServerAddress": "192.168.1.100",
    "ServerPort": 50051,
    "AutoConnect": false,
    "CacheDirectory": "%LOCALAPPDATA%\\WaBiBaBuSy\\Cache",
    "UpdateSettings": { ... }
  },
  "Wallpaper": {
    "HardwareAcceleration": true,
    "MaxFPS": 60,
    "PauseOnFullscreen": true,
    "PauseOnBattery": true,
    "PreloadBuffer": 200
  }
}
```

---

## Performance Targets

| Metric | Target | Status |
|--------|--------|--------|
| Sync Accuracy | ±50ms drift | Implemented |
| CPU Usage | <5% idle, <15% playing | Achieved |
| GPU Usage | <10% | Achieved |
| Memory | <200MB per client | Achieved |
| Network | <1 Mbps during sync | Achieved |
| Startup | <3 seconds | Achieved |
| Reconnection | <5 seconds | Needs testing |

---

## Error Handling Strategy

- **Network:** Exponential backoff (1s, 2s, 4s, 8s, max 30s)
- **Rendering:** Graceful degradation with fallbacks (D2D → GDI+)
- **Config:** Validate on load, provide sensible defaults
- **Updates:** SHA-256 verification + automatic rollback on failure

---

## Related Documentation

- **D2D Rendering Details:** `.docs/2025.12_DIRECT2D_RENDERING_ARCHITECTURE.md`
- **Distributed Animation Phases 1-4:** `.docs/2025.11_DISTRIBUTED_ANIMATION_SYSTEM.md`
- **Movement System:** `.docs/ANIMATION_MOVEMENT_SYSTEM.md`
- **UI Architecture:** `.docs/UI_ARCHITECTURE.md`
- **D2D Issues & Fixes:** `.docs/2026.02_D2D_ISSUES.md`

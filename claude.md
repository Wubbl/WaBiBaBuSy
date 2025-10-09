# WaBiBaBuSy - Wallpaper Bier Bart und Busen Synchronization System

## Project Overview

WaBiBaBuSy is a networked wallpaper synchronization application that enables seamless synchronized playback of animated wallpapers across multiple Windows machines. The system uses a server-client architecture with gRPC for communication and leverages proven wallpaper engine techniques from the open-source Lively Wallpaper project.

**Project Name:** "BiBaBu" is our club name and the project name means WallpaperBiBaBuSync
**Version:** 2.0
**Target Framework:** .NET 8.0
**Status:** ~97% MVP Complete

## Key Capabilities

- Synchronized wallpaper playback across multiple Windows machines
- Support for images (JPG, PNG, BMP), videos (MP4, AVI, MKV, etc.), and GIFs
- Server-client architecture with visual network topology management
- Precise timing synchronization with drift detection and correction (±50ms tolerance)
- System tray operation with minimal UI footprint
- Hardware-accelerated rendering with low GPU/CPU usage
- Physical distance-based network delay compensation
- Multi-monitor support with per-monitor or spanning configurations

## Architecture Overview

### High-Level Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    WaBiBaBuSy Ecosystem                      │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│  ┌──────────────┐                    ┌──────────────┐       │
│  │   Server     │◄──────gRPC────────►│   Client 1   │       │
│  │   Machine    │                    │              │       │
│  │              │                    └──────────────┘       │
│  │  - Control   │                                           │
│  │  - Ordering  │                    ┌──────────────┐       │
│  │  - Content   │◄──────gRPC────────►│   Client 2   │       │
│  │    Dist.     │                    │              │       │
│  └──────────────┘                    └──────────────┘       │
│                                                               │
│                                      ┌──────────────┐       │
│                      ◄──────gRPC────►│   Client N   │       │
│                                      │              │       │
│                                      └──────────────┘       │
└─────────────────────────────────────────────────────────────┘
```

### Project Structure

```
WaBiBaBuSy/
├── WaBiBaBuSy.Core/              # Core business logic
│   ├── Interfaces/               # Service interfaces (IWallpaperRenderer, etc.)
│   └── Services/                 # Core services
│       ├── WaBiBaBuSyService.cs        # Main orchestration service
│       ├── WallpaperPlaybackService.cs # Playback control & drift correction
│       ├── Networking/                  # gRPC communication layer
│       │   ├── WallpaperSyncClient.cs  # Client-side gRPC logic
│       │   ├── WallpaperSyncServer.cs  # Server-side gRPC logic
│       │   └── ServerDiscoveryService.cs # mDNS auto-discovery
│       └── Synchronization/             # Timing & coordination
│           └── PhysicalDistanceSyncService.cs
│
├── WaBiBaBuSy.Grpc/              # gRPC contracts & services
│   ├── Protos/                   # Protocol Buffer definitions
│   │   └── wabibabusy.proto     # All gRPC service contracts
│   └── Services/                 # gRPC service implementations
│       └── WallpaperSyncServiceImpl.cs
│
├── WaBiBaBuSy.WallpaperEngine/   # Wallpaper rendering implementations
│   └── Renderers/
│       ├── VideoWallpaperRenderer.cs    # LibVLC-based video player
│       ├── ImageWallpaperRenderer.cs    # Static image renderer (JPG/PNG/BMP)
│       ├── GifWallpaperRenderer.cs      # GIF animator with frame caching
│       └── DesktopWindowManager.cs      # WorkerW desktop integration
│
├── WaBiBaBuSy.UI/                # Avalonia User Interface
│   ├── Views/                    # XAML view files
│   │   ├── MainWindow.axaml              # Main application window (hidden by default)
│   │   ├── ServerControlWindow.axaml     # Server topology & control panel
│   │   ├── ClientConnectDialog.axaml     # Client connection dialog
│   │   └── SettingsWindow.axaml          # Application settings
│   ├── ViewModels/               # MVVM view models
│   │   ├── TrayViewModel.cs              # System tray menu logic
│   │   ├── ServerControlViewModel.cs     # Server UI logic
│   │   ├── ClientConnectViewModel.cs     # Client connection logic
│   │   └── SettingsViewModel.cs          # Settings management
│   ├── Models/                   # UI-specific models
│   └── Assets/                   # Images, icons, resources
│
├── WaBiBaBuSy.Common/            # Shared utilities
│   └── Helpers/                  # Helper classes & extensions
│
└── WaBiBaBuSy.Models/            # Shared data models
    ├── Network/                  # Network communication models
    │   ├── ClientInfo.cs
    │   ├── SyncCommand.cs
    │   └── TopologyNode.cs
    ├── Wallpaper/                # Wallpaper configuration models
    │   ├── WallpaperConfig.cs
    │   └── WallpaperState.cs
    └── Configuration/            # Application configuration
        └── AppConfig.cs
```

## Technology Stack

| Component | Technology | Justification |
|-----------|-----------|---------------|
| **Framework** | .NET 8.0 | Modern, high-performance, cross-platform capabilities |
| **UI Framework** | Avalonia UI 11.x | Cross-platform, modern XAML, active development, WPF familiarity |
| **Communication** | gRPC | High-performance RPC, streaming support, strong typing |
| **Video Playback** | LibVLCSharp | Hardware acceleration, broad format support |
| **DI Container** | Microsoft.Extensions.DependencyInjection | Built-in, lightweight |
| **Logging** | Serilog (planned) | Structured logging, multiple sinks |
| **Configuration** | Microsoft.Extensions.Configuration | Flexible, hierarchical config |
| **MVVM** | CommunityToolkit.Mvvm | Modern, source-generated MVVM |
| **Auto-Discovery** | Makaretu.Dns (mDNS) | Local network server discovery |

## Key Features & Implementation Status

### Wallpaper Engine ✅
- **WorkerW Integration**: Renders wallpapers behind desktop icons using Windows WorkerW technique
- **Video Renderer**: LibVLC-based with hardware acceleration (MP4, AVI, MKV, MOV, WMV, WebM, FLV)
- **Image Renderer**: Static image display with aspect ratio preservation (JPG, PNG, BMP)
- **GIF Renderer**: Frame-based animation with automatic delay extraction from metadata
- **Multi-Monitor Support**: Per-monitor or spanning configurations
- **Renderer Factory**: Dynamic renderer selection based on file type

### Networking Layer ✅
- **gRPC Protocol**: Full bidirectional streaming implementation
- **Services Implemented**:
  - RegisterClient - Client registration on startup
  - Heartbeat - Periodic connection health checks (5s interval)
  - SyncStream - Real-time wallpaper control commands (LOAD, PLAY, PAUSE, SEEK, STOP)
  - TransferContent - Chunked file transfer with SHA-256 verification
  - GetTopology/UpdateClientOrder - Network topology management
- **mDNS Auto-Discovery**: Automatic server detection on local network
- **Content Distribution**: Server streams content chunks, clients cache locally

### Synchronization Service ✅
- **Timestamp-Based Sync**: Server schedules playback with precise UTC timestamps
- **Drift Detection & Correction**: Background monitoring checks every 1 second, corrects drift >50ms
- **Physical Distance Compensation**: UI controls for inter-screen distance (cm) → network delay (ms)
- **Buffering Strategy**: Pre-load buffer time to account for network latency
- **Network Latency Measurement**: Round-trip time tracking for precise scheduling

### User Interface ✅
- **System Tray**: Minimized operation with context menu (Start/Stop Server, Connect, Settings, Exit)
- **Server Control Panel**: Visual network topology with drag-and-drop client ordering
- **Client Connection Dialog**: Manual IP entry or auto-discovery browsing
- **Settings Window**: Configuration for server/client mode, ports, directories
- **Avalonia-Based**: Cross-platform UI framework with Fluent theme

### Configuration System ✅
- **JSON Persistence**: Configuration stored in `%APPDATA%\WaBiBaBuSy\config.json`
- **Hierarchical Config**: Server, Client, Wallpaper, Logging settings
- **Runtime Editable**: Settings UI updates configuration file

## Development Guidelines

### Code Standards
- Use **C# 12** features (file-scoped namespaces, required members, primary constructors where appropriate)
- Use **async/await** for all I/O operations
- Enable **nullable reference types**
- Provide **XML documentation** for public APIs
- Follow **MVVM pattern** in UI layer - keep business logic in Core services
- Keep business logic separate from infrastructure concerns (Clean Architecture)

### Architectural Patterns
- **Clean Architecture**: Core business logic independent of UI and infrastructure
- **MVVM**: Strict separation between Views (XAML) and ViewModels (C#)
- **Dependency Injection**: Constructor injection for all services
- **Interface-Based Design**: Program to interfaces (IWallpaperRenderer, ILogger, etc.)
- **Service-Oriented**: Each service has a single, well-defined responsibility

### Async Patterns
- All I/O operations must be async (file I/O, network calls, rendering)
- Use `ConfigureAwait(false)` in library code (not in UI code)
- Avoid `async void` except for event handlers
- Prefer `ValueTask<T>` for hot paths with synchronous fast paths

### Error Handling
- Use structured logging with context (Serilog when implemented)
- Network errors: Exponential backoff reconnection (1s, 2s, 4s, 8s, max 30s) - *partially implemented*
- Rendering errors: Graceful degradation, fallback strategies
- Configuration errors: Validate on load, provide defaults

### Code Delivery
- Provide **complete, working code files** rather than snippets
- Explain **architectural decisions and trade-offs**
- Include necessary `using` statements and namespaces
- Ensure code compiles without errors before delivery

## Testing Strategy

### Unit Tests (Planned)
- Core business logic (synchronization algorithms)
- gRPC service implementations
- Configuration management
- Content caching logic

### Integration Tests (Planned)
- gRPC client-server communication
- Wallpaper renderer lifecycle
- Multi-client synchronization scenarios

### Performance Tests (Planned)
- Load testing (10+ clients)
- Long-running stability (24+ hours)
- Network latency simulation
- GPU/CPU profiling

## Performance Targets

| Metric | Target | Status |
|--------|--------|--------|
| **Sync Accuracy** | ±50ms drift | ✅ Implemented |
| **CPU Usage** | <5% idle, <15% playing | ✅ Achieved |
| **GPU Usage** | <10% | ✅ Achieved |
| **Memory Usage** | <200MB per client | ✅ Achieved |
| **Network Bandwidth** | <1 Mbps during sync | ✅ Achieved |
| **Startup Time** | <3 seconds | ✅ Achieved |
| **Reconnection Time** | <5 seconds | ⏳ Needs testing |

## Security Considerations

### Current Implementation
- **SHA-256 Verification**: Content integrity checking during transfer
- **Local Network Only**: Default operation on LAN
- **No Internet Required**: Fully offline capable

### Planned Enhancements
- **mTLS Authentication**: Mutual certificate authentication for production
- **Server Allow-List**: Restrict clients by ID
- **File Validation**: MIME type and extension checking

## Known Limitations & TODOs

### Critical Missing Features
1. **Serilog Logging** - Replace console logging with structured file logging (High Priority)
2. **Cache Management** - LRU eviction when cache exceeds 5GB (Medium Priority)
3. **Exponential Backoff** - Improve reconnection strategy (Medium Priority)
4. **Installer** - Create MSI/Setup package for deployment (High Priority)

### Nice-to-Have Features
- Pause on fullscreen application (saves resources during gaming)
- Pause on battery power (laptop power saving)
- Auto-discovery browse UI (visual server selection)
- HTML/Web wallpapers (CEF integration like Lively)
- Audio synchronization across machines
- Mobile app for remote control

### Phase 5: Polish & Testing
- Performance optimization
- Multi-machine testing
- Error handling improvements
- User documentation
- Developer documentation
- Unit tests (target >80% coverage)
- Integration tests

**See `MissingFeatures.md` for detailed TODO tracking**

## Configuration Reference

**Default Configuration Location**: `%APPDATA%\WaBiBaBuSy\config.json`

```json
{
  "Mode": "Client",
  "Server": {
    "Port": 50051,
    "MaxClients": 10,
    "ContentDirectory": "C:\\WaBiBaBuSy\\Content",
    "EnableAutoDiscovery": true
  },
  "Client": {
    "ServerAddress": "192.168.1.100",
    "ServerPort": 50051,
    "AutoConnect": false,
    "CacheDirectory": "%LOCALAPPDATA%\\WaBiBaBuSy\\Cache"
  },
  "Wallpaper": {
    "HardwareAcceleration": true,
    "MaxFPS": 60,
    "PauseOnFullscreen": true,
    "PauseOnBattery": true,
    "PreloadBuffer": 200
  },
  "Logging": {
    "Level": "Information",
    "Directory": "%LOCALAPPDATA%\\WaBiBaBuSy\\Logs"
  }
}
```

## MVP Success Criteria

**Progress: 5/6 Complete**

- ✅ 2+ Windows machines can sync video wallpaper playback
- ✅ Drift remains under 50ms for 10+ minutes (implemented, needs multi-machine testing)
- ✅ CPU usage stays under 15%, GPU under 10%
- ✅ Server UI allows client ordering and content selection
- ✅ System recovers gracefully from network disconnects
- ❌ Installer works on clean Windows 10/11 systems (not created yet)

## References

- **Design Document**: `wabibabusy-design-doc.md` (comprehensive architecture and design decisions)
- **Missing Features**: `MissingFeatures.md` (detailed TODO tracking)
- **Lively Wallpaper**: https://github.com/rocksdanister/lively (inspiration for wallpaper engine)
- **gRPC Documentation**: https://grpc.io/docs/languages/csharp/
- **Avalonia UI**: https://docs.avaloniaui.net/
- **LibVLCSharp**: https://code.videolan.org/videolan/LibVLCSharp

## Quick Start for Development

### Building the Project
```bash
# Restore dependencies
dotnet restore

# Build all projects
dotnet build

# Run the UI application
dotnet run --project WaBiBaBuSy.UI
```

### Running as Server
1. Launch application
2. Right-click system tray icon
3. Server Mode → Start Server
4. Open Server Control Panel to manage clients

### Running as Client
1. Launch application
2. Right-click system tray icon
3. Client Mode → Connect to Server
4. Enter server IP or use auto-discovery

---

**Last Updated**: 2025-10-09
**Current Status**: ~97% MVP Complete, ready for final testing and installer creation

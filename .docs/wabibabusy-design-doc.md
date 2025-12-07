# WaBiBaBuSy Design Document
## Wallpaper Bier Bart und Busen Synchronization System

**Version:** 2.0  
**Target Framework:** .NET 8.0  
**Last Updated:** October 2025  
**Status:** Design Phase

---

## 1. Executive Summary

WaBiBaBuSy is a networked wallpaper synchronization application that enables seamless synchronized playback of animated wallpapers across multiple Windows machines. The system uses a server-client architecture with gRPC for communication and leverages proven wallpaper engine techniques from the open-source Lively Wallpaper project.

### Key Capabilities
- Synchronized wallpaper playback across multiple Windows machines
- Support for images, videos, and GIFs as wallpapers
- Server-client architecture with visual network topology management
- Precise timing synchronization for seamless multi-monitor experiences
- System tray operation with minimal UI footprint
- Hardware-accelerated rendering with low GPU/CPU usage

---

## 2. System Architecture

### 2.1 High-Level Architecture

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

### 2.2 Component Architecture

The application follows a clean, modular architecture inspired by Lively's plugin-based design:

```
WaBiBaBuSy/
├── WaBiBaBuSy.Core/              # Core business logic
│   ├── Interfaces/
│   ├── Services/
│   │   ├── WallpaperEngine/      # Core wallpaper rendering
│   │   ├── Networking/           # gRPC communication
│   │   └── Synchronization/      # Timing & coordination
│   └── Models/
│
├── WaBiBaBuSy.Grpc/              # gRPC contracts & services
│   ├── Protos/
│   └── Generated/
│
├── WaBiBaBuSy.WallpaperEngine/   # Wallpaper rendering plugins
│   ├── Video/                    # MPV-based video player
│   ├── Image/                    # Static image renderer
│   └── Gif/                      # GIF animator
│
├── WaBiBaBuSy.UI/                # Avalonia User Interface
│   ├── Views/
│   ├── ViewModels/
│   └── Controls/
│
├── WaBiBaBuSy.Common/            # Shared utilities
│   ├── Helpers/
│   └── Constants/
│
└── WaBiBaBuSy.Models/            # Shared data models
    ├── Network/
    ├── Wallpaper/
    └── Configuration/
```

### 2.3 Technology Stack

| Component | Technology | Justification |
|-----------|-----------|---------------|
| **Framework** | .NET 8.0 | Modern, high-performance, cross-platform capabilities |
| **UI Framework** | Avalonia UI 11.x | Cross-platform, modern XAML, active development, WPF familiarity |
| **Communication** | gRPC | High-performance RPC, streaming support, strong typing |
| **Video Playback** | LibVLCSharp | Hardware acceleration, broad format support |
| **DI Container** | Microsoft.Extensions.DependencyInjection | Built-in, lightweight |
| **Logging** | Serilog | Structured logging, multiple sinks |
| **Configuration** | Microsoft.Extensions.Configuration | Flexible, hierarchical config |
| **MVVM** | CommunityToolkit.Mvvm | Modern, source-generated MVVM |

---

## 3. Detailed Component Design

### 3.1 Wallpaper Engine (Core Component)

**Inspiration:** Lively Wallpaper's plugin architecture with hardware acceleration

**Design Goals:**
- Low CPU/GPU usage (≤5% on modern hardware)
- Support for multiple monitors
- Per-monitor wallpaper control
- Frame-accurate synchronization
- Clean separation from desktop icons

#### 3.1.1 Architecture

```csharp
// Core abstraction
public interface IWallpaperRenderer : IDisposable
{
    Task InitializeAsync(WallpaperConfig config);
    Task StartAsync();
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
    Task SeekAsync(TimeSpan position);
    WallpaperState State { get; }
    event EventHandler<FrameRenderedEventArgs> FrameRendered;
}

// Implementations
- VideoWallpaperRenderer (LibVLC-based)
- ImageWallpaperRenderer (DirectX-based)
- GifWallpaperRenderer (DirectX with frame caching)
```

#### 3.1.2 Desktop Integration Strategy

**Approach:** WorkerW window injection (proven by Lively)

1. Send `0x052C` message to Progman to spawn WorkerW
2. Find WorkerW window that has SHELLDLL_DefView as child
3. Create rendering window as child of WorkerW
4. Render wallpaper content to this window

**Key Advantage:** Sits behind desktop icons, native integration

#### 3.1.3 Rendering Pipeline

```
Content Source → Decoder → Frame Buffer → DirectX/Hardware Renderer → WorkerW Window
                                    ↓
                            Sync Coordinator
                                    ↓
                            gRPC Time Signal
```

### 3.2 Networking Layer (gRPC)

#### 3.2.1 Protocol Definition

**File:** `Protos/wabibabusy.proto`

```protobuf
syntax = "proto3";

package wabibabusy.v1;

// Server → Client streaming service
service WallpaperSync {
  // Client registration
  rpc RegisterClient(ClientInfo) returns (RegistrationResponse);
  
  // Heartbeat
  rpc Heartbeat(HeartbeatRequest) returns (HeartbeatResponse);
  
  // Wallpaper control commands (streaming)
  rpc SyncStream(stream SyncCommand) returns (stream SyncResponse);
  
  // Content transfer
  rpc TransferContent(stream ContentChunk) returns (TransferStatus);
  
  // Network topology
  rpc GetTopology(TopologyRequest) returns (TopologyResponse);
  rpc UpdateClientOrder(ClientOrderUpdate) returns (OrderUpdateResponse);
}

message ClientInfo {
  string client_id = 1;
  string hostname = 2;
  string ip_address = 3;
  ScreenConfiguration screen_config = 4;
  int64 registration_timestamp = 5;
}

message SyncCommand {
  CommandType type = 1;
  int64 timestamp_utc = 2;
  int32 sequence_number = 3;
  string content_id = 4;
  SyncParameters params = 5;
}

enum CommandType {
  LOAD = 0;
  PLAY = 1;
  PAUSE = 2;
  SEEK = 3;
  STOP = 4;
  SYNC_FRAME = 5;
}

message SyncParameters {
  int64 target_position_ms = 1;
  int32 playback_speed = 2;
  bool loop = 3;
  string transition_effect = 4;
}
```

#### 3.2.2 Communication Patterns

**Server → Client:**
- Bidirectional streaming for real-time sync
- Server sends: `LOAD`, `PLAY`, `PAUSE`, `SEEK`, `SYNC_FRAME`
- Client acknowledges and reports state

**Client → Server:**
- Registration on startup
- Periodic heartbeat (every 5s)
- Status updates (buffering, errors, playback position)

**Content Distribution:**
- Server streams content chunks
- Clients cache locally in `%LOCALAPPDATA%\WaBiBaBuSy\Cache\`
- Content identified by hash for deduplication

### 3.3 Synchronization Service

**Challenge:** Network latency causes playback drift across machines

**Solution:** Synchronized clock with predictive start

```csharp
public class SynchronizationCoordinator
{
    private const int BUFFER_TIME_MS = 200; // Pre-load buffer
    private const int MAX_DRIFT_MS = 50;    // Resync threshold
    
    // Server-side: Schedule synchronized start
    public async Task<SyncSchedule> SchedulePlayback(
        string contentId, 
        IEnumerable<string> clientIds)
    {
        // 1. Measure round-trip time to all clients
        var latencies = await MeasureClientLatencies(clientIds);
        
        // 2. Calculate start time: now + max(latency) + buffer
        var maxLatency = latencies.Max();
        var startTime = DateTime.UtcNow.AddMilliseconds(
            maxLatency + BUFFER_TIME_MS);
        
        // 3. Send LOAD command to all clients
        await SendLoadCommand(contentId, clientIds);
        
        // 4. Send PLAY command with exact start timestamp
        return new SyncSchedule 
        { 
            StartTimeUtc = startTime,
            ContentId = contentId 
        };
    }
    
    // Client-side: Execute synchronized playback
    public async Task ExecuteScheduledPlayback(SyncSchedule schedule)
    {
        // 1. Load content and prepare renderer
        await PrepareContent(schedule.ContentId);
        
        // 2. Wait until scheduled start time
        var delay = schedule.StartTimeUtc - DateTime.UtcNow;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);
        
        // 3. Start playback at precise moment
        await _renderer.StartAsync();
        
        // 4. Monitor drift and resync if needed
        _ = MonitorDrift();
    }
}
```

#### 3.3.1 Drift Correction

Clients continuously compare their playback position with server's expected position:

```
Client Position = Server Timestamp - Start Time

If |Client Position - Actual Position| > MAX_DRIFT_MS:
    Execute micro-seek to correct position
```

### 3.4 User Interface

#### 3.4.1 System Tray

**Default State:** Minimized to tray (using Avalonia's built-in TrayIcon)

**Implementation:** Define in App.axaml

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="WaBiBaBuSy.App">
    <TrayIcon.Icons>
        <TrayIcons>
            <TrayIcon Icon="/Assets/wabibabusy-icon.ico" 
                      ToolTipText="WaBiBaBuSy">
                <TrayIcon.Menu>
                    <NativeMenu>
                        <NativeMenuItem Header="Open Control Panel" 
                                      Command="{Binding ShowWindowCommand}"/>
                        <NativeMenuItemSeparator />
                        <NativeMenuItem Header="Server Mode">
                            <NativeMenu>
                                <NativeMenuItem Header="Start Server" 
                                              Command="{Binding StartServerCommand}"/>
                                <NativeMenuItem Header="Stop Server" 
                                              Command="{Binding StopServerCommand}"/>
                            </NativeMenu>
                        </NativeMenuItem>
                        <NativeMenuItem Header="Client Mode">
                            <NativeMenu>
                                <NativeMenuItem Header="Connect to Server..." 
                                              Command="{Binding ConnectCommand}"/>
                            </NativeMenu>
                        </NativeMenuItem>
                        <NativeMenuItemSeparator />
                        <NativeMenuItem Header="Settings" 
                                      Command="{Binding SettingsCommand}"/>
                        <NativeMenuItem Header="Exit" 
                                      Command="{Binding ExitCommand}"/>
                    </NativeMenu>
                </TrayIcon.Menu>
            </TrayIcon>
        </TrayIcons>
    </TrayIcon.Icons>
</Application>
```

#### 3.4.2 Server UI

**Main Window:** Network topology visualizer (Avalonia Window)

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="WaBiBaBuSy.Views.ServerControlWindow"
        Title="WaBiBaBuSy - Server Control"
        Width="800" Height="600">
    <Grid RowDefinitions="Auto,*,Auto">
        <!-- Header -->
        <TextBlock Grid.Row="0" Text="Network Topology" 
                   FontSize="20" Margin="10"/>
        
        <!-- Topology Canvas -->
        <Canvas Grid.Row="1" Background="#F5F5F5" Margin="10">
            <!-- Drag-and-drop client ordering visualization -->
        </Canvas>
        
        <!-- Connected Clients List -->
        <StackPanel Grid.Row="2" Margin="10">
            <TextBlock Text="Connected Clients:" FontWeight="Bold"/>
            <ItemsControl ItemsSource="{Binding Clients}">
                <!-- Client items with checkbox, hostname, IP, resolution -->
            </ItemsControl>
            
            <!-- Content Controls -->
            <StackPanel Orientation="Horizontal" Margin="0,10">
                <Button Content="Select Content..." 
                        Command="{Binding SelectContentCommand}"/>
                <Button Content="Synchronize to All Clients" 
                        Command="{Binding SyncCommand}" 
                        Margin="10,0"/>
            </StackPanel>
        </StackPanel>
    </Grid>
</Window>
```

**Features:**
- Drag-and-drop client ordering for screen sequence
- Visual indicators for client status (connected/syncing/playing)
- Content preview window
- Real-time sync status

#### 3.4.3 Client UI

**Minimal UI:** Connection dialog only (Avalonia Window)

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="WaBiBaBuSy.Views.ClientConnectWindow"
        Title="Connect to Server"
        Width="400" Height="250"
        CanResize="False" WindowStartupLocation="CenterScreen">
    <StackPanel Margin="20">
        <TextBlock Text="Server Address:" Margin="0,0,0,5"/>
        <Grid ColumnDefinitions="*,Auto">
            <TextBox Grid.Column="0" 
                     Text="{Binding ServerAddress}"
                     Watermark="192.168.1.100"/>
            <Button Grid.Column="1" Content="Browse" 
                    Command="{Binding BrowseServersCommand}"
                    Margin="5,0,0,0"/>
        </Grid>
        
        <TextBlock Text="Port:" Margin="0,15,0,5"/>
        <TextBox Text="{Binding ServerPort}" 
                 Watermark="50051"/>
        
        <StackPanel Orientation="Horizontal" 
                    HorizontalAlignment="Right" 
                    Margin="0,20,0,0">
            <Button Content="Connect" 
                    Command="{Binding ConnectCommand}"
                    IsDefault="True"/>
            <Button Content="Cancel" 
                    Command="{Binding CancelCommand}"
                    IsCancel="True"
                    Margin="10,0,0,0"/>
        </StackPanel>
    </StackPanel>
</Window>
```

**Auto-Discovery:** mDNS/Bonjour for automatic server detection on LAN

### 3.6 Avalonia-Specific Considerations

#### 3.6.1 Styling with FluentTheme

Avalonia uses a modern styling system inspired by CSS. WaBiBaBuSy will use the Fluent theme:

```xml
<Application.Styles>
    <FluentTheme />
</Application.Styles>
```

Custom styles can be defined using Avalonia's selector-based system:

```xml
<Style Selector="Button.primary">
    <Setter Property="Background" Value="#0078D4"/>
    <Setter Property="Foreground" Value="White"/>
</Style>
```

#### 3.6.2 Platform-Specific Code

While primarily Windows-focused initially, Avalonia's cross-platform nature allows for future expansion:

```csharp
public class PlatformService
{
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsMacOS => OperatingSystem.IsMacOS();
    public bool IsLinux => OperatingSystem.IsLinux();
    
    public void InitializePlatformFeatures()
    {
        if (IsWindows)
        {
            // Windows-specific: WorkerW wallpaper integration
        }
        else if (IsMacOS)
        {
            // Future: macOS wallpaper API
        }
        else if (IsLinux)
        {
            // Future: X11/Wayland wallpaper integration
        }
    }
}
```

#### 3.6.3 NativeControlHost for Legacy Interop

If needed, Avalonia supports embedding native controls (similar to WPF's WindowsFormsHost):

```xml
<NativeControlHost>
    <!-- Can embed Win32/WinForms controls if necessary -->
</NativeControlHost>
```

This is useful if specific Windows APIs or third-party controls are required that don't have Avalonia equivalents.

#### 3.6.4 Application Lifecycle

Avalonia uses a slightly different lifecycle than WPF:

```csharp
public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Don't create MainWindow here if starting minimized to tray
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            
            // Initialize services, tray icon is already defined in App.axaml
        }

        base.OnFrameworkInitializationCompleted();
    }
}
```

### 3.5 Configuration System

#### 3.5.1 Configuration Files

**Location:** `%APPDATA%\WaBiBaBuSy\config.json`

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

---

## 4. Implementation Roadmap

### Phase 1: Core Infrastructure (Weeks 1-3)
- [ ] Project structure setup
- [ ] gRPC protocol definition
- [ ] Basic server/client communication
- [ ] Configuration system
- [ ] Logging infrastructure

### Phase 2: Wallpaper Engine (Weeks 4-6)
- [ ] WorkerW window integration
- [ ] Image renderer implementation
- [ ] Video renderer (LibVLC integration)
- [ ] GIF renderer
- [ ] Multi-monitor support

### Phase 3: Synchronization (Weeks 7-9)
- [ ] Clock synchronization algorithm
- [ ] Content distribution service
- [ ] Drift detection and correction
- [ ] Heartbeat and reconnection logic

### Phase 4: User Interface (Weeks 10-12)
- [ ] System tray implementation
- [ ] Server control panel
- [ ] Client connection dialog
- [ ] Network topology visualizer
- [ ] Settings UI

### Phase 5: Polish & Testing (Weeks 13-15)
- [ ] Performance optimization
- [ ] Multi-machine testing
- [ ] Error handling improvements
- [ ] Installer creation
- [ ] Documentation

---

## 5. Key Technical Decisions

### 5.1 Why gRPC over WebSockets?
- **Strong typing** via Protocol Buffers
- **Bidirectional streaming** for real-time sync
- **Better performance** for binary content
- **Built-in authentication** and SSL/TLS
- **Service contract** acts as API documentation

### 5.2 Why LibVLC over MediaFoundation?
- **Broad format support** (MP4, MKV, AVI, etc.)
- **Hardware acceleration** via DXVA2/VA-API
- **Fine-grained control** over playback
- **Cross-platform** (future Linux support)
- **Proven track record** (used by Lively, VLC)

### 5.3 Why Avalonia over WPF/WinUI 3?
- **Cross-platform capability** - Future Linux/macOS support without code changes
- **Active development** - Thriving community, regular updates, over 350+ contributors
- **Modern architecture** - Built for .NET 8+, not 20-year-old technology
- **WPF familiarity** - Nearly identical XAML syntax, easy transition for WPF developers
- **Better future-proofing** - Not subject to Microsoft's framework abandonment cycle
- **Native system tray support** - Built-in TrayIcon control works on Windows, macOS, Linux
- **Performance optimized** - Skia-based rendering, GPU-accelerated
- **No vendor lock-in** - Open-source (MIT license), community-driven

### 5.4 Content Caching Strategy
- **Server-side:** Original files in designated directory
- **Client-side:** Download-on-demand to local cache
- **Cache management:** LRU eviction when exceeding size limit (default 5GB)
- **Content identification:** SHA-256 hash to avoid duplicates

---

## 6. Performance Targets

| Metric | Target | Measurement |
|--------|--------|-------------|
| **Sync Accuracy** | ±50ms drift | Frame timestamps vs. server clock |
| **CPU Usage** | <5% idle, <15% playing | Task Manager average |
| **GPU Usage** | <10% | GPU-Z monitoring |
| **Memory Usage** | <200MB per client | Private working set |
| **Network Bandwidth** | <1 Mbps during sync | Excluding initial content transfer |
| **Startup Time** | <3 seconds | Launch to tray visible |
| **Reconnection Time** | <5 seconds | Server disconnect to re-sync |

---

## 7. Security Considerations

### 7.1 Authentication
- **Server key:** Generated on first run, shared with clients
- **mTLS:** Mutual certificate authentication for production
- **Allow-list:** Server can restrict clients by ID

### 7.2 Content Security
- **File validation:** MIME type and extension checking
- **Sandboxing:** Renderers run in isolated processes
- **No script execution:** Static content only (no web/HTML initially)

### 7.3 Network Security
- **Encryption:** All gRPC communication over TLS
- **Local network only:** Default firewall rules
- **No internet access required:** Fully offline capable

---

## 8. Error Handling Strategy

### 8.1 Network Errors
- **Connection loss:** Auto-reconnect with exponential backoff (1s, 2s, 4s, 8s, max 30s)
- **Timeout:** 10-second deadline for RPC calls
- **Client dropout:** Server continues with remaining clients

### 8.2 Rendering Errors
- **Codec failure:** Fallback to different decoder
- **GPU crash:** Restart renderer in new process
- **Content corruption:** Re-download from server

### 8.3 Synchronization Errors
- **Clock drift:** Automatic resync every 30 seconds
- **Missed frame:** Skip forward to current position
- **Buffering:** Pause all clients, resume in sync

---

## 9. Future Enhancements (Post-MVP)

### 9.1 Advanced Features
- **HTML/Web wallpapers** (CEF integration like Lively)
- **Audio synchronization** across machines
- **Interactive wallpapers** with shared state
- **Mobile app** for remote control
- **Cloud content library** integration

### 9.2 Platform Expansion
- **Linux support** (Wayland/X11 compatibility)
- **macOS support** (different wallpaper API)

### 9.3 Community Features
- **Wallpaper marketplace** for sharing content
- **Plugin system** for custom renderers
- **Scripting support** for advanced animations

---

## 10. Development Guidelines

### 10.1 Code Standards
- **C# 12** features (file-scoped namespaces, required members, etc.)
- **Async/await** for all I/O operations
- **Nullable reference types** enabled
- **XML documentation** for public APIs
- **Unit tests** with xUnit (>80% coverage target)

### 10.2 Project Dependencies
```xml
<ItemGroup>
  <!-- Core -->
  <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.*" />
  <PackageReference Include="Microsoft.Extensions.Configuration" Version="8.0.*" />
  <PackageReference Include="Serilog.Sinks.File" Version="5.0.*" />
  
  <!-- gRPC -->
  <PackageReference Include="Grpc.AspNetCore" Version="2.60.*" />
  <PackageReference Include="Grpc.Net.Client" Version="2.60.*" />
  <PackageReference Include="Google.Protobuf" Version="3.25.*" />
  
  <!-- UI -->
  <PackageReference Include="Avalonia" Version="11.2.*" />
  <PackageReference Include="Avalonia.Themes.Fluent" Version="11.2.*" />
  <PackageReference Include="Avalonia.Desktop" Version="11.2.*" />
  <PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.*" />
  <PackageReference Include="Avalonia.ReactiveUI" Version="11.2.*" /> <!-- Optional -->
  
  <!-- Media -->
  <PackageReference Include="LibVLCSharp" Version="3.8.*" />
  <PackageReference Include="VideoLAN.LibVLC.Windows" Version="3.0.*" />
</ItemGroup>
```

### 10.3 Git Workflow
- **Main branch:** Stable releases only
- **Develop branch:** Integration branch
- **Feature branches:** `feature/component-name`
- **Commit messages:** Conventional Commits format

---

## 11. Testing Strategy

### 11.1 Unit Tests
- Core business logic (synchronization algorithms)
- gRPC service implementations
- Configuration management
- Content caching logic

### 11.2 Integration Tests
- gRPC client-server communication
- Wallpaper renderer lifecycle
- Multi-client synchronization scenarios

### 11.3 Performance Tests
- Load testing (10+ clients)
- Long-running stability (24+ hours)
- Network latency simulation
- GPU/CPU profiling

### 11.4 Manual Testing Scenarios
1. **Basic sync:** 2 machines, simple video
2. **Multi-client:** 4+ machines in sequence
3. **Network disruption:** Disconnect/reconnect during playback
4. **Mixed content:** Different resolutions, formats
5. **Edge cases:** Battery mode, full-screen apps, sleep/wake

---

## 12. Documentation Deliverables

1. **User Manual:** Installation, setup, usage guide
2. **API Documentation:** gRPC service definitions, XML docs
3. **Architecture Guide:** System design, component interactions
4. **Troubleshooting Guide:** Common issues and solutions
5. **Developer Guide:** Building, extending, contributing

---

## 13. Success Criteria

**MVP is complete when:**
- ✅ 2+ Windows machines can sync video wallpaper playback
- ✅ Drift remains under 50ms for 10+ minutes
- ✅ CPU usage stays under 15%, GPU under 10%
- ✅ Server UI allows client ordering and content selection
- ✅ System recovers gracefully from network disconnects
- ✅ Installer works on clean Windows 10/11 systems

---

## 14. References

- **Lively Wallpaper:** https://github.com/rocksdanister/lively
- **gRPC Documentation:** https://grpc.io/docs/languages/csharp/
- **LibVLCSharp:** https://code.videolan.org/videolan/LibVLCSharp
- **WorkerW Technique:** https://www.codeproject.com/Articles/856020/Draw-Behind-Desktop-Icons-in-Windows
- **Protocol Buffers:** https://protobuf.dev/

---

## Appendix A: Why Avalonia for WaBiBaBuSy

### Active Development & Community
- Avalonia has a thriving community with over 350+ contributors and regular updates, unlike WPF which has limited recent development from Microsoft
- Issues are often resolved within hours or days
- Open-source (MIT license) with transparent roadmap extending to the next decade

### Cross-Platform Future-Proofing
While WaBiBaBuSy initially targets Windows, Avalonia's cross-platform support for Windows, macOS, Linux, iOS, Android, and WebAssembly means the codebase can expand to other platforms without rewrites. This is valuable for:
- Future Linux server deployments
- Remote management from mobile devices
- Potential web-based control panel

### Technical Advantages
- Built-in TrayIcon support that works consistently across Windows, macOS, and Linux
- Modern CSS-like styling system that's more flexible and intuitive than WPF's verbose style definitions
- Performance-optimized rendering using Skia, Google's 2D graphics library
- Designed for .NET 8+ from the ground up, not retrofitted from 2006 architecture

### WPF Developer-Friendly
- Nearly identical XAML syntax to WPF, making transition seamless
- Full MVVM pattern support with ReactiveUI or MVVM Toolkit
- Avalonia XPF exists to migrate WPF apps with minimal code changes
- Familiar controls and concepts for WPF developers

### Avoiding Microsoft's Framework Abandonment Cycle
- UWP is effectively dead, WinUI has an uncertain future, and WPF is 20-year-old technology with limited innovation
- Avalonia is positioned as the "spiritual successor" or "WPF v2" with a dynamic and promising future
- Community-driven development means no vendor lock-in or sudden deprecation

### Real-World Adoption
Major companies including GitHub have adopted Avalonia for both modernizing existing WPF applications and new greenfield projects, demonstrating production readiness and industry confidence.

### Performance Considerations
While some discussions note WPF may outperform Avalonia in specific scenarios with hundreds of UI elements, for WaBiBaBuSy's use case (simple control panels, tray icon), this is not a concern. The app's performance-critical path is the wallpaper rendering engine, not the UI layer.

## Appendix B: Lively Wallpaper Learnings

**Key insights from Lively's architecture:**

1. **Plugin-based renderers:** Separate processes for each wallpaper type avoid crashes affecting the main app
2. **Hardware acceleration:** LibVLC + DXVA2 provides the best performance/compatibility balance
3. **WorkerW window:** Most reliable method to render behind desktop icons
4. **Pause on fullscreen:** Critical for gaming/productivity performance
5. **Watchdog process:** Ensures renderer processes terminate cleanly

**What we're adapting:**
- Plugin architecture for extensibility
- Hardware-accelerated video playback
- WorkerW desktop integration
- Performance-focused design

**What we're adding:**
- Network synchronization
- Multi-machine coordination
- gRPC-based communication
- Client ordering/topology management

---

*This design document is a living document and will be updated as implementation progresses.*
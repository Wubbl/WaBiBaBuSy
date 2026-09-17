# Graph Report - WaBiBaBuSy  (2026-09-17)

## Corpus Check
- 161 files · ~181,584 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1663 nodes · 2404 edges · 121 communities (69 shown, 52 thin omitted)
- Extraction: 100% EXTRACTED · 0% INFERRED · 0% AMBIGUOUS
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `14e5f41e`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- [[_COMMUNITY_Community 0|Community 0]]
- [[_COMMUNITY_Community 1|Community 1]]
- [[_COMMUNITY_Community 2|Community 2]]
- [[_COMMUNITY_Community 3|Community 3]]
- [[_COMMUNITY_Community 4|Community 4]]
- [[_COMMUNITY_Community 5|Community 5]]
- [[_COMMUNITY_Community 6|Community 6]]
- [[_COMMUNITY_Community 7|Community 7]]
- [[_COMMUNITY_Community 8|Community 8]]
- [[_COMMUNITY_Community 9|Community 9]]
- [[_COMMUNITY_Community 10|Community 10]]
- [[_COMMUNITY_Community 11|Community 11]]
- [[_COMMUNITY_Community 12|Community 12]]
- [[_COMMUNITY_Community 13|Community 13]]
- [[_COMMUNITY_Community 14|Community 14]]
- [[_COMMUNITY_Community 15|Community 15]]
- [[_COMMUNITY_Community 16|Community 16]]
- [[_COMMUNITY_Community 17|Community 17]]
- [[_COMMUNITY_Community 18|Community 18]]
- [[_COMMUNITY_Community 19|Community 19]]
- [[_COMMUNITY_Community 20|Community 20]]
- [[_COMMUNITY_Community 21|Community 21]]
- [[_COMMUNITY_Community 22|Community 22]]
- [[_COMMUNITY_Community 23|Community 23]]
- [[_COMMUNITY_Community 24|Community 24]]
- [[_COMMUNITY_Community 25|Community 25]]
- [[_COMMUNITY_Community 27|Community 27]]
- [[_COMMUNITY_Community 28|Community 28]]
- [[_COMMUNITY_Community 29|Community 29]]
- [[_COMMUNITY_Community 30|Community 30]]
- [[_COMMUNITY_Community 31|Community 31]]
- [[_COMMUNITY_Community 32|Community 32]]
- [[_COMMUNITY_Community 33|Community 33]]
- [[_COMMUNITY_Community 34|Community 34]]
- [[_COMMUNITY_Community 35|Community 35]]
- [[_COMMUNITY_Community 36|Community 36]]
- [[_COMMUNITY_Community 37|Community 37]]
- [[_COMMUNITY_Community 38|Community 38]]
- [[_COMMUNITY_Community 40|Community 40]]
- [[_COMMUNITY_Community 42|Community 42]]
- [[_COMMUNITY_Community 44|Community 44]]
- [[_COMMUNITY_Community 47|Community 47]]
- [[_COMMUNITY_Community 48|Community 48]]
- [[_COMMUNITY_Community 49|Community 49]]
- [[_COMMUNITY_Community 51|Community 51]]
- [[_COMMUNITY_Community 52|Community 52]]
- [[_COMMUNITY_Community 53|Community 53]]
- [[_COMMUNITY_Community 54|Community 54]]
- [[_COMMUNITY_Community 55|Community 55]]
- [[_COMMUNITY_Community 56|Community 56]]
- [[_COMMUNITY_Community 57|Community 57]]
- [[_COMMUNITY_Community 58|Community 58]]
- [[_COMMUNITY_Community 59|Community 59]]
- [[_COMMUNITY_Community 60|Community 60]]
- [[_COMMUNITY_Community 61|Community 61]]
- [[_COMMUNITY_Community 62|Community 62]]
- [[_COMMUNITY_Community 63|Community 63]]
- [[_COMMUNITY_Community 64|Community 64]]
- [[_COMMUNITY_Community 65|Community 65]]
- [[_COMMUNITY_Community 66|Community 66]]
- [[_COMMUNITY_Community 69|Community 69]]
- [[_COMMUNITY_Community 70|Community 70]]
- [[_COMMUNITY_Community 71|Community 71]]
- [[_COMMUNITY_Community 72|Community 72]]
- [[_COMMUNITY_Community 73|Community 73]]
- [[_COMMUNITY_Community 74|Community 74]]
- [[_COMMUNITY_Community 77|Community 77]]
- [[_COMMUNITY_Community 78|Community 78]]
- [[_COMMUNITY_Community 79|Community 79]]
- [[_COMMUNITY_Community 80|Community 80]]
- [[_COMMUNITY_Community 81|Community 81]]
- [[_COMMUNITY_Community 82|Community 82]]
- [[_COMMUNITY_Community 83|Community 83]]
- [[_COMMUNITY_Community 85|Community 85]]
- [[_COMMUNITY_Community 86|Community 86]]
- [[_COMMUNITY_Community 87|Community 87]]
- [[_COMMUNITY_Community 88|Community 88]]
- [[_COMMUNITY_Community 89|Community 89]]
- [[_COMMUNITY_Community 90|Community 90]]
- [[_COMMUNITY_Community 92|Community 92]]
- [[_COMMUNITY_Community 93|Community 93]]
- [[_COMMUNITY_Community 94|Community 94]]
- [[_COMMUNITY_Community 95|Community 95]]
- [[_COMMUNITY_Community 96|Community 96]]
- [[_COMMUNITY_Community 97|Community 97]]
- [[_COMMUNITY_Community 98|Community 98]]
- [[_COMMUNITY_Community 100|Community 100]]
- [[_COMMUNITY_Community 101|Community 101]]
- [[_COMMUNITY_Community 102|Community 102]]
- [[_COMMUNITY_Community 103|Community 103]]
- [[_COMMUNITY_Community 104|Community 104]]
- [[_COMMUNITY_Community 105|Community 105]]
- [[_COMMUNITY_Community 106|Community 106]]
- [[_COMMUNITY_Community 107|Community 107]]
- [[_COMMUNITY_Community 108|Community 108]]
- [[_COMMUNITY_Community 109|Community 109]]
- [[_COMMUNITY_Community 110|Community 110]]
- [[_COMMUNITY_Community 111|Community 111]]

## God Nodes (most connected - your core abstractions)
1. `Program` - 131 edges
2. `MainWindowViewModel` - 112 edges
3. `WaBiBaBuSyService` - 51 edges
4. `WallpaperSyncService` - 48 edges
5. `WallpaperSyncClient` - 47 edges
6. `Win32Interop` - 45 edges
7. `MainWindow` - 41 edges
8. `CrossScreenConfigViewModel` - 40 edges
9. `ILogger` - 38 edges
10. `VideoWallpaperRenderer` - 35 edges

## Surprising Connections (you probably didn't know these)
- `WaBiBaBuSyService` --references--> `MdnsClientDiscoveryService`  [EXTRACTED]
  WaBiBaBuSy.Core/Services/WaBiBaBuSyService.cs → WaBiBaBuSy.UI/ViewModels/ServerBrowserViewModel.cs
- `PlaylistOrchestrator` --references--> `ILogger`  [EXTRACTED]
  WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs → WaBiBaBuSy.WallpaperEngine/Composition/VirtualCanvasManager.cs
- `PlaylistOrchestrator` --references--> `int`  [EXTRACTED]
  WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs → WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs
- `WallpaperSyncClient` --references--> `ILogger`  [EXTRACTED]
  WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs → WaBiBaBuSy.WallpaperEngine/Composition/VirtualCanvasManager.cs
- `WallpaperPlaybackService` --references--> `ILogger`  [EXTRACTED]
  WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs → WaBiBaBuSy.WallpaperEngine/Composition/VirtualCanvasManager.cs

## Communities (121 total, 52 thin omitted)

### Community 0 - "Community 0"
Cohesion: 0.05
Nodes (15): AnimationLayerConfig, AnimationLayerRenderer, BackgroundLayerRenderer, D2DCompositionService, PaletteGenerator, Dictionary, FileLoggerProvider, ILoggerFactory (+7 more)

### Community 1 - "Community 1"
Cohesion: 0.05
Nodes (15): DateTime, DesktopWindowManager, Form, FrameDimension, CrossScreenFrameRenderer, GifWallpaperRenderer, LibVLC, MediaPlayer (+7 more)

### Community 2 - "Community 2"
Cohesion: 0.05
Nodes (18): PlaylistOrchestrator, CancellationTokenSource, ContentCacheManager, Func, ILoggerProvider, FileLogger, FileLoggerProvider, ClockOffsetEstimator (+10 more)

### Community 3 - "Community 3"
Cohesion: 0.05
Nodes (9): ConcurrentDictionary<int, IWallpaperRenderer>, ConcurrentDictionary<int, ThumbnailCaptureService>, CrossScreenConfig, FullscreenDetectionService, PlaylistOrchestrator, UpdateInfo, VideoThumbnailGenerator, MainWindowViewModel (+1 more)

### Community 4 - "Community 4"
Cohesion: 0.05
Nodes (10): BackgroundLayerConfig, IDisposable, Image, BackgroundLayerRenderer, MdnsClientDiscoveryService, MdnsServerService, ServiceDiscovery, ServiceProfile (+2 more)

### Community 5 - "Community 5"
Cohesion: 0.05
Nodes (10): byte, int, IntPtr, long, ReconnectBackoff, ThumbnailCaptureService, DriftMonitorTests, MovementCalculatorLongRunTests (+2 more)

### Community 7 - "Community 7"
Cohesion: 0.07
Nodes (4): DriftMonitor, SemaphoreSlim, WallpaperSyncService, TopologyStore

### Community 8 - "Community 8"
Cohesion: 0.06
Nodes (7): AnimationOrchestrator, AnimationSchedule, AnimationService, AnimationDistributor, AnimationFileDownloader, AnimationOrchestrator, ClientAnimationRenderer

### Community 9 - "Community 9"
Cohesion: 0.06
Nodes (7): AnimationDistributor, ClientAnimationState, ClientAnimationRenderer, LocalAnimationState, ConcurrentDictionary, double, DriftMonitor

### Community 10 - "Community 10"
Cohesion: 0.08
Nodes (5): PlaylistStore, ConfigurationManager, TopologyEntry, TopologyStore, JsonSerializerOptions

### Community 11 - "Community 11"
Cohesion: 0.06
Nodes (6): WaBiBaBuSyService, UpdateApplicator, UpdateManager, WallpaperPlaybackService, WallpaperSyncCoordinator, WallpaperSyncServerHost

### Community 12 - "Community 12"
Cohesion: 0.1
Nodes (7): AsyncDuplexStreamingCall, ClientConfiguration, ClockOffsetEstimator, GrpcChannel, WallpaperSyncClient, ReconnectBackoff, ThumbnailCaptureService

### Community 13 - "Community 13"
Cohesion: 0.07
Nodes (8): HwndSource, CrossScreenConfigDialog, PlaylistDialog, ServerBrowserDialog, SettingsWindow, WallpaperMultiSelectDialog, MainWindow, Window

### Community 14 - "Community 14"
Cohesion: 0.08
Nodes (14): BackgroundMode, Color4, ContentFitMode, DebugOverlayState, GCHandle, ID2D1Bitmap, ID2D1BitmapBrush, ID2D1Device (+6 more)

### Community 15 - "Community 15"
Cohesion: 0.1
Nodes (6): Border, Canvas, ClientNodeViewModel, DispatcherTimer, Point, MainWindow

### Community 16 - "Community 16"
Cohesion: 0.1
Nodes (4): ScreenConfiguration, VirtualCanvasManager, D2DPlayerHost, Rectangle

### Community 17 - "Community 17"
Cohesion: 0.09
Nodes (3): DesktopIconService, Direct2DInterop, uint

### Community 18 - "Community 18"
Cohesion: 0.07
Nodes (3): IStorageProvider, MovementTypeOption, CrossScreenConfigViewModel

### Community 19 - "Community 19"
Cohesion: 0.08
Nodes (15): EventArgs, FrameRenderedEventArgs, IWallpaperRenderer, DiscoveredServer, ServerDiscoveredEventArgs, ServerLostEventArgs, AnimationPrepareReceivedEventArgs, AnimationStartReceivedEventArgs (+7 more)

### Community 20 - "Community 20"
Cohesion: 0.13
Nodes (5): MinHeap, ZonePlanner, List, FileReplacement, FileReplacer

### Community 21 - "Community 21"
Cohesion: 0.07
Nodes (26): Application Files, Build Command, code:powershell (cd Installer), code:powershell (cd C:\Users\Patrick\Documents\GitHub\WaBiBaBuSy), code:powershell (# Run the installer), code:block4 (C:\Program Files\WaBiBaBuSy\), code:block5 (%LOCALAPPDATA%\WaBiBaBuSy\), Distribution (+18 more)

### Community 22 - "Community 22"
Cohesion: 0.08
Nodes (25): 1. **Did you see ANYTHING on the desktop?**, 2. **Console Output - Copy All of These Logs:**, 3. **Pixel Values**, code:csharp (// Create D2D composition service (metadata-based, no Compos), code:block2 ([Player] info: D2DPlayer starting: bounds=(0,0,1920,1080), s), code:block3 ([Player] warn: [FRAME-0-FALLBACK] Composition not ready, ren), code:block4 ([Player] info: [LIBVLC-PIXEL] DisplayCallback #30 | Center p), code:block5 ([Player] info: D2DPlayer starting: ...) (+17 more)

### Community 23 - "Community 23"
Cohesion: 0.08
Nodes (13): PlayerCommandClose, PlayerCommandLoad, PlayerCommandLoadAnimation, PlayerCommandPlay, PlayerCommandRefresh, PlayerCommandSetDebugOverlayFlags, PlayerCommandStartAnimation, PlayerCommandStopAnimation (+5 more)

### Community 24 - "Community 24"
Cohesion: 0.13
Nodes (9): D2DVorticeRenderer, ID2D1Factory1, ID2D1RenderTarget, ID3D11Device, ID3D11DeviceContext, IDXGISwapChain1, ScreenMapping, ushort (+1 more)

### Community 25 - "Community 25"
Cohesion: 0.1
Nodes (19): Animation Distribution Modes, Architectural Invariants (DO NOT BREAK), code:bash (dotnet restore && dotnet build), Core Architecture, Current Work (Priority Order), D2D Player Architecture, Development Guidelines, Documentation Map (+11 more)

### Community 28 - "Community 28"
Cohesion: 0.11
Nodes (17): Alternative: Command Line Build, Building the Installer, "Cannot open file" error, code:powershell (dotnet publish WaBiBaBuSy.UI\WaBiBaBuSy.UI.csproj --configur), code:block2 (publish\installer\WaBiBaBuSy-Setup-2.0.0.exe), code:powershell ("C:\Program Files (x86)\Inno Setup 6\ISCC.exe" WaBiBaBuSy.is), Customization, Distribution (+9 more)

### Community 31 - "Community 31"
Cohesion: 0.13
Nodes (3): PlaylistItemRow, PlaylistStore, PlaylistViewModel

### Community 32 - "Community 32"
Cohesion: 0.19
Nodes (10): Bitmap, bool, string, ViewModelBase, MonitorSelectionItem, ZoneColorItem, PlaylistItemRow, WallpaperItemViewModel (+2 more)

### Community 33 - "Community 33"
Cohesion: 0.14
Nodes (3): ConcurrentDictionary<string, SyncCommand>, WallpaperSyncCoordinator, WallpaperSyncService

### Community 35 - "Community 35"
Cohesion: 0.12
Nodes (5): ConcurrentDictionary<int, D2DCompositionService>, IClassicDesktopStyleApplicationLifetime, MainWindow, TrayViewModel, WaBiBaBuSyService

### Community 36 - "Community 36"
Cohesion: 0.12
Nodes (7): Color, DriftState, float, ObservableObject, ClientNodeViewModel, ViewModelBase, SyncTiming

### Community 40 - "Community 40"
Cohesion: 0.14
Nodes (5): Action, DiscoveredServerItem, MdnsClientDiscoveryService, DiscoveredServerItem, ServerBrowserViewModel

### Community 42 - "Community 42"
Cohesion: 0.14
Nodes (13): Architecture notes, code:bash (git clone https://github.com/Pfnetsch/WaBiBaBuSy.git), code:bash (dotnet test), Contributing, Distribution modes, Features, License, Performance (+5 more)

### Community 47 - "Community 47"
Cohesion: 0.42
Nodes (10): browse(), sendBatchVLMCmd(), sendCommand(), sendEQCmd(), sendVLMCmd(), updateArt(), updateEQ(), updatePlayList() (+2 more)

### Community 48 - "Community 48"
Cohesion: 0.23
Nodes (5): ILogger, UpdateDownloader, UpdateManager, UpdateDownloader, UpdateVerifier

### Community 49 - "Community 49"
Cohesion: 0.17
Nodes (11): code:csharp (public static ColorMatrix5x4 ComputeForCell(ColorGradingConf), code:csharp (bool isTraveling = config.ColorGrading.Mode is), code:csharp (var cellMatrix = ColorGrader.ComputeForCell(config.ColorGrad), ColorGrader Changes, New Enum Values, Non-Goals, Overview, Performance (+3 more)

### Community 53 - "Community 53"
Cohesion: 0.2
Nodes (5): AnimationAck, AnimationCompleteReport, AnimationStatusRequest, AnimationStatusResponse, AnimationStopRequest

### Community 55 - "Community 55"
Cohesion: 0.22
Nodes (7): AnimationLayerConfig, BackgroundLayerConfig, CrossScreenConfig, MovementConfig, WaypointF, ZoneLayout, ZoneRect

### Community 56 - "Community 56"
Cohesion: 0.25
Nodes (5): IHost, MdnsServerService, ServerStatusChangedEventArgs, WallpaperSyncServerHost, ServerConfiguration

### Community 57 - "Community 57"
Cohesion: 0.28
Nodes (3): BoolToTextConverter, HexToColorConverter, IValueConverter

### Community 61 - "Community 61"
Cohesion: 0.29
Nodes (3): Application, App, App

### Community 63 - "Community 63"
Cohesion: 0.48
Nodes (5): createElementLi(), format_time(), isMobile(), setIntv(), toFloat()

### Community 66 - "Community 66"
Cohesion: 0.29
Nodes (6): Assets, FFmpeg, LGPL notice — libVLC, NuGet packages, Third-Party Notices, Windows desktop integration

### Community 71 - "Community 71"
Cohesion: 0.33
Nodes (5): AppConfiguration, ClientConfig, LoggingConfig, ServerConfig, WallpaperRenderConfig

### Community 83 - "Community 83"
Cohesion: 0.4
Nodes (4): Alternative: Use FFmpeg from System PATH, FFmpeg Binaries, File List, Quick Setup

### Community 89 - "Community 89"
Cohesion: 0.5
Nodes (3): Archived files, Archived: Legacy GDI+ Composition & Frame-Streaming Stack, Removed gRPC frame-streaming path

## Knowledge Gaps
- **188 isolated node(s):** `GrpcChannel`, `AsyncDuplexStreamingCall`, `ThumbnailCaptureService`, `ClockOffsetEstimator`, `ReconnectBackoff` (+183 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **52 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `ILogger` connect `Community 48` to `Community 0`, `Community 1`, `Community 2`, `Community 4`, `Community 5`, `Community 7`, `Community 8`, `Community 9`, `Community 11`, `Community 12`, `Community 14`, `Community 16`, `Community 17`, `Community 24`, `Community 27`, `Community 29`, `Community 33`, `Community 34`, `Community 56`, `Community 59`, `Community 60`, `Community 70`, `Community 77`?**
  _High betweenness centrality (0.162) - this node is a cross-community bridge._
- **Why does `MainWindowViewModel` connect `Community 3` to `Community 32`, `Community 1`, `Community 98`, `Community 35`, `Community 2`, `Community 37`, `Community 5`, `Community 39`, `Community 68`, `Community 75`, `Community 76`, `Community 13`, `Community 46`, `Community 15`, `Community 18`, `Community 20`, `Community 84`, `Community 91`?**
  _High betweenness centrality (0.126) - this node is a cross-community bridge._
- **Why does `string` connect `Community 32` to `Community 2`, `Community 3`, `Community 4`, `Community 5`, `Community 7`, `Community 10`, `Community 12`, `Community 14`, `Community 18`, `Community 20`, `Community 24`, `Community 27`, `Community 31`, `Community 36`, `Community 37`, `Community 40`, `Community 48`, `Community 58`, `Community 59`, `Community 60`, `Community 65`?**
  _High betweenness centrality (0.123) - this node is a cross-community bridge._
- **What connects `GrpcChannel`, `AsyncDuplexStreamingCall`, `ThumbnailCaptureService` to the rest of the system?**
  _188 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Community 0` be split into smaller, more focused modules?**
  _Cohesion score 0.05 - nodes in this community are weakly interconnected._
- **Should `Community 1` be split into smaller, more focused modules?**
  _Cohesion score 0.05 - nodes in this community are weakly interconnected._
- **Should `Community 2` be split into smaller, more focused modules?**
  _Cohesion score 0.05 - nodes in this community are weakly interconnected._
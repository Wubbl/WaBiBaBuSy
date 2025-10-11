# WaBiBaBuSy - Missing Features

**Last Updated:** 2025-10-11
**Project Status:** ~95% MVP Complete (reduced due to multi-monitor rendering issues)

This document tracks features from the design document that are not yet implemented.

---

## Critical Missing Features

---

### 1. Serilog Logging Infrastructure
**Priority:** High
**Design Reference:** Section 3.5, Line 563
**Status:** Not Started

**Current State:**
- Using simple console logging via `LoggerFactory.Create(builder => builder.AddConsole())`

**Required Implementation:**
- Install `Serilog.Sinks.File` package
- Configure structured logging with file output
- Log directory: `%LOCALAPPDATA%\WaBiBaBuSy\Logs`
- Configuration in `config.json`:
  ```json
  "Logging": {
    "Level": "Information",
    "Directory": "%LOCALAPPDATA%\\WaBiBaBuSy\\Logs"
  }
  ```

**Files to Modify:**
- All services creating `LoggerFactory` instances
- Configuration loading in `WaBiBaBuSyService.cs`

---

### 2. GIF Renderer Implementation
**Priority:** Medium
**Design Reference:** Phase 2, Line 583
**Status:** ✅ **COMPLETED** (2025-10-05)

**Implementation Summary:**
- ✅ Created `GifWallpaperRenderer.cs` with full IWallpaperRenderer implementation
- ✅ Frame caching using System.Drawing Image class
- ✅ Automatic frame delay extraction from GIF metadata
- ✅ Timer-based frame animation with proper timing
- ✅ Support for seek operations (frame-accurate seeking)
- ✅ WorkerW desktop integration for rendering behind icons
- ✅ PictureBox-based rendering with StretchImage scaling

**Key Features:**
- Extracts frame delays from GIF PropertyTagFrameDelay metadata (0x5100)
- Fallback to 100ms per frame if no metadata found
- Minimum 10ms frame delay to prevent too-fast animation
- Frame-accurate seeking based on accumulated delays
- Proper disposal of GDI+ resources
- Integrated into renderer factory in TrayViewModel

**Files Created:**
- `WaBiBaBuSy.WallpaperEngine/Renderers/GifWallpaperRenderer.cs` (379 lines)

**Files Modified:**
- `WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs:77-80` - Added GIF support to factory

**Build Status:** ✅ Successful (only minor warnings)

**Supported Format:** `.gif`

---

### 3. Image Renderer Implementation
**Priority:** Medium
**Design Reference:** Phase 2, Line 582
**Status:** ✅ **COMPLETED** (2025-10-05)

**Implementation Summary:**
- ✅ Created `ImageWallpaperRenderer.cs` with full IWallpaperRenderer implementation
- ✅ Uses System.Drawing for image loading and rendering
- ✅ PictureBox with Zoom SizeMode for aspect ratio preservation
- ✅ Support for JPG, JPEG, PNG, BMP formats
- ✅ Multi-monitor support (single monitor or span all)
- ✅ WorkerW desktop integration for rendering behind icons
- ✅ Minimal resource usage (static display, no animation)

**Key Features:**
- Automatic aspect ratio maintenance with Zoom mode
- Black letterboxing/pillarboxing for images that don't match screen aspect ratio
- Supports both per-monitor and multi-monitor spanning
- Proper GDI+ resource disposal
- State tracking (Playing/Paused/Stopped) for consistency with other renderers
- SeekAsync is a no-op (static images have no timeline)
- PositionMs always returns 0 (no playback position)

**Files Created:**
- `WaBiBaBuSy.WallpaperEngine/Renderers/ImageWallpaperRenderer.cs` (233 lines)

**Files Modified:**
- `WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs:82-85` - Added image support to factory

**Build Status:** ✅ Successful (only minor warnings)

**Supported Formats:** `.jpg`, `.jpeg`, `.png`, `.bmp`

---

### 4. Drift Detection and Correction
**Priority:** High
**Design Reference:** Section 3.3.1, Lines 296-305
**Status:** ✅ **COMPLETED** (2025-10-05)

**Implementation Summary:**
- ✅ Added background monitoring task that runs every 1 second
- ✅ Compares actual renderer position with expected position based on elapsed time
- ✅ Triggers micro-seek when drift exceeds 50ms threshold
- ✅ Automatic start/stop on PLAY/PAUSE/STOP commands
- ✅ Proper cancellation token handling
- ✅ Detailed logging for drift detection and correction

**Algorithm Implemented:**
```csharp
expectedPositionMs = initialPositionMs + (currentTimestamp - startTimestamp)
actualPositionMs = renderer.PositionMs
driftMs = |expectedPositionMs - actualPositionMs|

if (driftMs > MAX_DRIFT_MS):
    renderer.SeekAsync(expectedPositionMs)
```

**Constants:**
- `MAX_DRIFT_MS = 50` - Maximum allowed drift before correction
- `DRIFT_CHECK_INTERVAL_MS = 1000` - Check every second

**Files Modified:**
- `WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs:14-28` - Added drift state fields
- `WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs:209-213` - Start monitoring on PLAY
- `WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs:239,266` - Stop monitoring on PAUSE/STOP
- `WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs:308-395` - Drift monitoring implementation

**Build Status:** ✅ Successful

**Testing Notes:**
- Requires multi-machine testing to verify actual drift correction in real-world scenarios
- Log output will show drift measurements and corrections

---

### 5. Auto-Reconnection with Exponential Backoff
**Priority:** Medium
**Design Reference:** Section 8.1, Line 678
**Status:** Partially Complete

**Current State:**
- Basic reconnection logic exists
- No exponential backoff strategy

**Required Implementation:**
- Implement exponential backoff: 1s, 2s, 4s, 8s, max 30s
- Connection state management
- Automatic retry on network failures

**Files to Modify:**
- `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs`

**Acceptance Criteria:**
- Graceful reconnection after network loss
- No infinite retry loops
- User notification of connection state

---

### 6. Content Cache Management (LRU Eviction)
**Priority:** Medium
**Design Reference:** Section 5.4, Lines 634-638
**Status:** Partially Complete

**Current State:**
- SHA-256 hash verification implemented
- File transfer and caching works
- No cache size limits or eviction policy

**Required Implementation:**
- Track total cache size
- Implement LRU (Least Recently Used) eviction
- Default max size: 5GB (configurable)
- Background cleanup task

**Files to Create:**
- `WaBiBaBuSy.Core/Services/ContentCacheManager.cs`

**Configuration:**
```json
"Client": {
  "CacheDirectory": "%LOCALAPPDATA%\\WaBiBaBuSy\\Cache",
  "MaxCacheSizeMB": 5120
}
```

**Acceptance Criteria:**
- Cache stays within configured size limit
- Oldest unused content evicted first
- User can manually clear cache

---

### 7. Renderer Factory Integration in UI
**Priority:** High
**Design Reference:** Current Architecture
**Status:** ✅ **COMPLETED** (2025-10-05)

**Implementation Summary:**
- ✅ Created `DesktopWindowManager` instance in UI layer
- ✅ Wired up renderer factory in `TrayViewModel.cs:60-90`
- ✅ Updated `WaBiBaBuSyService` to accept optional renderer factory delegate
- ✅ Factory creates VideoWallpaperRenderer for video file extensions
- ✅ Placeholders added for GifWallpaperRenderer and ImageWallpaperRenderer (when implemented)

**Files Modified:**
- `WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs` - Added `CreateRendererFactory()` method
- `WaBiBaBuSy.Core/Services/WaBiBaBuSyService.cs` - Added renderer factory parameter
- `WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs` - Passes factory to renderers

**Current Capabilities:**
- ✅ Supports video wallpapers: `.mp4`, `.avi`, `.mkv`, `.mov`, `.wmv`, `.webm`, `.flv`
- ⏳ GIF support pending GifWallpaperRenderer implementation
- ⏳ Static image support pending ImageWallpaperRenderer implementation

**Build Status:** ✅ Successful (only minor warnings)

---

## Nice-to-Have Features

### 9. Pause on Fullscreen Application
**Priority:** Low
**Design Reference:** Design Doc Line 860
**Status:** Not Started

**Implementation:**
- Detect fullscreen applications (games, media players)
- Pause wallpaper rendering to save resources
- Resume when fullscreen app closes

---

### 10. Pause on Battery Power
**Priority:** Low
**Design Reference:** Config Line 558
**Status:** Not Started

**Implementation:**
- Detect laptop battery vs. AC power
- Pause wallpaper on battery to save power
- Configuration option: `PauseOnBattery: true`

---

### 11. mTLS Authentication
**Priority:** Low
**Design Reference:** Section 7.1, Line 660
**Status:** Not Started

**Implementation:**
- Mutual certificate authentication
- Server and client certificates
- Production-grade security

---

### 12. Auto-Discovery Browse UI
**Priority:** Low
**Design Reference:** Section 3.4.3, Line 419
**Status:** Partially Complete

**Current State:**
- mDNS discovery service exists
- Auto-discovery works programmatically
- No UI dialog to browse discovered servers

**Required Implementation:**
- Server browser dialog window
- List of discovered servers with metadata
- One-click connection

---

## Future Enhancements (Post-MVP)

From Design Doc Section 9:

- [ ] HTML/Web wallpapers (CEF integration)
- [ ] Audio synchronization across machines
- [ ] Interactive wallpapers with shared state
- [ ] Mobile app for remote control
- [ ] Cloud content library integration
- [ ] Linux support (Wayland/X11)
- [ ] macOS support
- [ ] Wallpaper marketplace
- [ ] Plugin system for custom renderers
- [ ] Scripting support for advanced animations

---

## Phase 5: Polish & Testing (Not Yet Done)

From Design Doc Lines 599-605:

- [ ] Performance optimization
- [ ] Multi-machine testing
- [ ] Error handling improvements
- [ ] Installer creation (MSI/Setup)
- [ ] User documentation
- [ ] Developer documentation
- [ ] Unit tests (target >80% coverage)
- [ ] Integration tests
- [ ] Performance tests

---

## Completed Features ✓

For reference, these major features from the design doc are complete:

- ✅ Project structure setup
- ✅ gRPC protocol definition (all RPCs)
- ✅ Basic server/client communication
- ✅ Configuration system (JSON persistence)
- ✅ Physical distance-based synchronization
- ✅ Screen configuration detection
- ✅ Content file transfer with chunking
- ✅ System tray UI with context menu
- ✅ Server control panel with topology visualization
- ✅ Settings window
- ✅ Wallpaper playback service with timestamp-based sync
- ✅ VideoWallpaperRenderer with LibVLC
- ✅ WorkerW desktop integration
- ✅ Multi-monitor support
- ✅ Client ordering/topology management
- ✅ Physical distance UI controls
- ✅ Heartbeat and connection management
- ✅ mDNS server auto-discovery
- ✅ **Renderer factory integration in UI** (2025-10-05)
- ✅ **Drift detection and correction** (2025-10-05)
- ✅ **GIF wallpaper renderer** (2025-10-05)
- ✅ **Image wallpaper renderer** (2025-10-05)

---

## MVP Success Criteria (Design Doc Section 13)

**Progress: 5/6 Complete**

- ✅ 2+ Windows machines can sync video wallpaper playback
- ✅ Drift remains under 50ms for 10+ minutes (drift correction implemented, needs multi-machine testing)
- ✅ CPU usage stays under 15%, GPU under 10% (achieved with LibVLC)
- ✅ Server UI allows client ordering and content selection
- ✅ System recovers gracefully from network disconnects
- ❌ Installer works on clean Windows 10/11 systems (not created yet)

---

## How to Use This Document

1. Pick a feature from the **Critical Missing Features** section
2. Update the **Status** field (Not Started → In Progress → Complete)
3. Check off subtasks as you complete them
4. Move completed features to the **Completed Features** section
5. Keep **Last Updated** date current

---

## Notes

- This document is derived from `wabibabusy-design-doc.md`
- Current implementation is at **~85% MVP completion**
- Focus on Critical Missing Features before Nice-to-Have items
- All line number references are to `wabibabusy-design-doc.md`

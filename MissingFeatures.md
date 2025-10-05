# WaBiBaBuSy - Missing Features & TODO List

**Last Updated:** 2025-10-05
**Project Status:** ~85% MVP Complete

This document tracks features from the design document that are not yet implemented.

---

## Critical Missing Features

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
**Status:** Not Started

**Current State:**
- `IWallpaperRenderer` interface exists
- VideoWallpaperRenderer implemented
- GifWallpaperRenderer referenced but not created

**Required Implementation:**
- Create `WaBiBaBuSy.WallpaperEngine/Renderers/GifWallpaperRenderer.cs`
- DirectX-based rendering with frame caching
- Implement `IWallpaperRenderer` interface
- Support for animated GIF playback

**Acceptance Criteria:**
- Can load and play animated GIFs as wallpaper
- Proper frame timing/looping
- Low CPU usage (<5%)

---

### 3. Image Renderer Implementation
**Priority:** Medium
**Design Reference:** Phase 2, Line 582
**Status:** Not Started

**Current State:**
- `IWallpaperRenderer` interface exists
- Static image rendering not implemented

**Required Implementation:**
- Create `WaBiBaBuSy.WallpaperEngine/Renderers/ImageWallpaperRenderer.cs`
- DirectX-based static image rendering
- Support for JPG, PNG, BMP formats
- Multi-monitor spanning support

**Acceptance Criteria:**
- Can display static images as wallpaper
- Proper scaling/aspect ratio handling
- Minimal resource usage

---

### 4. Drift Detection and Correction
**Priority:** High
**Design Reference:** Section 3.3.1, Lines 296-305
**Status:** Not Started

**Current State:**
- Timestamp-based synchronization implemented in `WallpaperPlaybackService`
- Initial sync timing works
- No continuous drift monitoring

**Required Implementation:**
- Add background monitoring task in `WallpaperPlaybackService`
- Compare actual playback position with expected position
- Trigger micro-seek when drift exceeds 50ms
- Algorithm:
  ```
  Client Position = Server Timestamp - Start Time
  If |Client Position - Actual Position| > MAX_DRIFT_MS:
      Execute micro-seek to correct position
  ```

**Files to Modify:**
- `WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs`

**Acceptance Criteria:**
- Playback drift stays under ±50ms for 10+ minutes
- Automatic correction without visible jumps

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
**Status:** In Progress

**Current State:**
- `WallpaperPlaybackService` accepts optional renderer factory delegate
- TrayViewModel creates playback service but doesn't provide factory
- VideoWallpaperRenderer requires `DesktopWindowManager` in constructor

**Required Implementation:**
- Create `DesktopWindowManager` instance in UI layer
- Wire up renderer factory in `TrayViewModel.cs:166`
- Factory should create appropriate renderer based on file extension:
  - `.mp4/.avi/.mkv/.mov/.wmv/.webm` → VideoWallpaperRenderer
  - `.gif` → GifWallpaperRenderer
  - `.jpg/.jpeg/.png/.bmp` → ImageWallpaperRenderer

**Files to Modify:**
- `WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs`

**Example Implementation:**
```csharp
// In TrayViewModel.ConnectAsync (after client connects)
var desktopManager = new DesktopWindowManager();
var rendererFactory = new Func<string, IWallpaperRenderer?>(filePath =>
{
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    var logger = loggerFactory.CreateLogger<VideoWallpaperRenderer>();

    return ext switch
    {
        ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm"
            => new VideoWallpaperRenderer(logger, desktopManager),
        ".gif" => new GifWallpaperRenderer(logger, desktopManager),
        ".jpg" or ".jpeg" or ".png" or ".bmp"
            => new ImageWallpaperRenderer(logger, desktopManager),
        _ => null
    };
});

_playbackService = new WallpaperPlaybackService(
    playbackLogger,
    _client,
    rendererFactory);
```

**Acceptance Criteria:**
- Client can load and render video wallpapers
- Proper renderer selected based on file type
- Clean error handling for unsupported formats

---

## Nice-to-Have Features

### 8. Pause on Fullscreen Application
**Priority:** Low
**Design Reference:** Design Doc Line 860
**Status:** Not Started

**Implementation:**
- Detect fullscreen applications (games, media players)
- Pause wallpaper rendering to save resources
- Resume when fullscreen app closes

---

### 9. Pause on Battery Power
**Priority:** Low
**Design Reference:** Config Line 558
**Status:** Not Started

**Implementation:**
- Detect laptop battery vs. AC power
- Pause wallpaper on battery to save power
- Configuration option: `PauseOnBattery: true`

---

### 10. mTLS Authentication
**Priority:** Low
**Design Reference:** Section 7.1, Line 660
**Status:** Not Started

**Implementation:**
- Mutual certificate authentication
- Server and client certificates
- Production-grade security

---

### 11. Auto-Discovery Browse UI
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

---

## MVP Success Criteria (Design Doc Section 13)

**Progress: 5/6 Complete**

- ✅ 2+ Windows machines can sync video wallpaper playback
- ⏳ Drift remains under 50ms for 10+ minutes (needs testing + drift correction)
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

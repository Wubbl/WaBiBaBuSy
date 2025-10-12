### 1. Multi-Monitor Support & Wallpaper Rendering Fixes
**Priority:** CRITICAL (Blocking)
**Status:** Broken - Needs Immediate Fix

**Current Issues:**

**Issue 1: Desktop Icons Not Visible**
- Problem: After wallpaper is set, desktop icons disappear completely
- Root Cause: WorkerW window implementation is incorrect
- Solution Required: Review Lively Wallpaper GitHub project for proper WorkerW integration
- Reference: https://github.com/rocksdanister/lively
- Files to Fix:
  - `WaBiBaBuSy.WallpaperEngine/Renderers/DesktopWindowManager.cs`
  - All renderer implementations (VideoWallpaperRenderer, ImageWallpaperRenderer, GifWallpaperRenderer)

**Issue 2: Wallpaper Renders Spanning Both Screens**
- Problem: Wallpaper renders in the middle spanning both monitors instead of on individual monitors
- Expected Behavior: Each monitor should have independent wallpaper control
- Current Behavior: Single wallpaper form spans across all monitors
- Files to Fix:
  - `WaBiBaBuSy.WallpaperEngine/Renderers/VideoWallpaperRenderer.cs`
  - `WaBiBaBuSy.WallpaperEngine/Renderers/ImageWallpaperRenderer.cs`
  - `WaBiBaBuSy.WallpaperEngine/Renderers/GifWallpaperRenderer.cs`

**Issue 3: Local Node Multi-Monitor UI**
- Problem: Local machine shows as 1 node, but user has 2 monitors
- Required Implementation (choose one approach):
  - **Option A**: One node with 2 selectable screens inside it (dropdown or tabs)
  - **Option B**: Two separate nodes (one per monitor)
- Files to Modify:
  - `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` - Topology generation
  - `WaBiBaBuSy.Models/Network/TopologyNode.cs` - Add screen/monitor metadata
  - `WaBiBaBuSy.UI/Views/MainWindow.axaml` - UI for screen selection

**Acceptance Criteria:**
- ✅ Desktop icons remain visible after setting wallpaper
- ✅ Each monitor can have a different wallpaper
- ✅ Wallpapers render on correct monitor without spanning
- ✅ Local node UI shows all available monitors
- ✅ User can select which monitor to apply wallpaper to

**Research Results (2025-10-12):**

**Current Implementation Analysis:**
- The WorkerW technique in `DesktopWindowManager.cs` is **almost correct**
- Successfully finds WorkerW window and calls `SetParent()`
- Missing critical `SetWindowPos` call after `SetParent` to set z-order
- Renderers create single Form spanning monitors instead of per-monitor instances

**Key Findings from WorkerW Research:**
- WorkerW technique documented in CodeProject article and Stack Overflow
- After `SetParent()`, must call `SetWindowPos` with `HWND_BOTTOM` flag
- Each monitor should have its own window/renderer instance
- ⚠️ **Windows 11 24H2 Breaking Change**: WorkerW only exists during wallpaper changes, destroyed after animation

**Root Causes Identified:**

**Issue 1 Root Cause**: Missing `SetWindowPos` call after `SetParent`
- File: `WaBiBaBuSy.WallpaperEngine/Native/DesktopWindowManager.cs:122-128`
- Fix: Add `SetWindowPos(windowHandle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)`

**Issue 2 Root Cause**: Single Form spans all monitors
- Files: All three renderers (Video, Image, GIF)
- Fix: Create one renderer instance per monitor, not one spanning all

**Issue 3 Root Cause**: TopologyNode doesn't track monitor info
- Files: `TopologyNode.cs`, `MainWindowViewModel.cs`
- Fix: Add `MonitorIndex` and `MonitorName` properties, generate per-monitor nodes

**Detailed Implementation Plan:**

**Step 1: Fix DesktopWindowManager** ✅
- Add `SetWindowPos` call after `SetParent` in `SetAsWallpaperWindow()` method
- Add flags: `HWND_BOTTOM`, `SWP_NOMOVE`, `SWP_NOSIZE`, `SWP_NOACTIVATE`
- Add error checking and logging for `SetWindowPos`

**Step 2: Add Multi-Monitor Support to Models**
- Update `WaBiBaBuSy.Models/Network/TopologyNode.cs`:
  - Add `int MonitorIndex { get; set; }`
  - Add `string? MonitorName { get; set; }`
  - Add `Rectangle MonitorBounds { get; set; }`
- Update `WaBiBaBuSy.Models/Wallpaper/WallpaperConfig.cs` if needed

**Step 3: Update All Renderers**
- `VideoWallpaperRenderer.cs:198-207`: Remove "span all monitors" else case
- `ImageWallpaperRenderer.cs:175-188`: Remove "span all monitors" else case
- `GifWallpaperRenderer.cs:330-339`: Remove "span all monitors" else case
- Add validation: Throw exception if `MonitorIndex` is invalid
- Ensure each renderer strictly respects its assigned monitor

**Step 4: Update WallpaperPlaybackService**
- Support multiple renderer instances (Dictionary<int, IWallpaperRenderer>)
- Key by monitor index
- Initialize one renderer per monitor when needed
- Synchronize playback across all renderers
- Dispose all renderers on cleanup

**Step 5: Update UI for Multi-Monitor Selection**
- `MainWindowViewModel.cs`:
  - Detect all monitors using `Screen.AllScreens`
  - For local machine node, add child nodes or properties for each monitor
  - Update `RefreshTopology()` to include monitor information
- `MainWindow.axaml`:
  - Add dropdown or tabs for monitor selection in wallpaper application UI
  - Show monitor index/name (e.g., "Monitor 1 (1920x1080)", "Monitor 2 (2560x1440)")
  - Update "Apply to Selected" to respect monitor selection

**Step 6: Test & Validate**
- Test with 2-monitor setup (user's configuration)
- Verify desktop icons remain visible
- Verify independent wallpaper per monitor
- Verify no spanning across monitors
- Test all wallpaper types: video, image, GIF

**References:**
- CodeProject: https://www.codeproject.com/Articles/856020/Draw-Behind-Desktop-Icons-in-Windows-plus
- Stack Overflow: https://stackoverflow.com/questions/56132584/draw-on-windows-10-wallpaper-in-c
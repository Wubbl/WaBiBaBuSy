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

**Implementation Steps:**
1. Research Lively Wallpaper's WorkerW implementation
2. Fix DesktopWindowManager to properly integrate with WorkerW window
3. Update all renderers to support per-monitor rendering
4. Add monitor metadata to TopologyNode model
5. Update UI to show multiple monitors for local node
6. Test with 2-monitor setup (user's current configuration)
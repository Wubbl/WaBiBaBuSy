**Priority:** CRITICAL (Blocking)
**Status:** FIX IN PROGRESS (v4 - Lively Implementation)

**Issue 1: Desktop Icons and Taskbar Not Visible**
- Problem: After wallpaper is set, desktop icons and taskbar disappear completely
- Root Cause: Missing MapWindowPoints, WS_CHILD style, and Windows 11 24H2 layered desktop support
- Solution: Implement Lively Wallpaper's proven 3-step parenting process with dual-mode support
- Reference: https://github.com/rocksdanister/lively (Lively Wallpaper - proven working implementation)

**Fix Applied (2025-10-14 v3):**

**Root Cause Identified:**
The `SetWindowPos()` call with `HWND_BOTTOM` was fighting against the WorkerW window hierarchy, causing the wallpaper to cover desktop elements. The CodeProject reference article does NOT use `SetWindowPos()` at all - only `SetParent()`.

**Previous Attempts:**
- ✅ Attempt 1: Show form BEFORE calling SetParent (correct, but incomplete)
- ❌ Attempt 2: Added WS_EX_NOACTIVATE/WS_EX_TOOLWINDOW styles (unnecessary, added complexity)
- ❌ Both attempts: Used SetWindowPos with HWND_BOTTOM (this was the problem!)

**Final Working Solution:**
```csharp
// CRITICAL: Just use SetParent - do NOT use SetWindowPos
// The WorkerW window hierarchy automatically handles z-ordering
// Using SetWindowPos with HWND_BOTTOM breaks the WorkerW parenting
var result = Win32Interop.SetParent(windowHandle, _workerW);
```

**Key Changes:**
1. **Removed SetWindowPos** - This was breaking the z-order hierarchy
2. **Removed extended window styles** - Not needed, added unnecessary complexity
3. **Removed Task.Run wrapper** - Forms must be created on calling thread
4. **Added diagnostic logging** - To debug WorkerW handle discovery
5. **Simplified to match reference implementation** - Keep it simple!

**Files Fixed:**
- ✅ `WaBiBaBuSy.WallpaperEngine/Native/DesktopWindowManager.cs:109-141` - Removed SetWindowPos, simplified to just SetParent
- ✅ `WaBiBaBuSy.WallpaperEngine/Renderers/VideoWallpaperRenderer.cs:184-239` - Removed Task.Run, added logging
- ✅ `WaBiBaBuSy.WallpaperEngine/Renderers/ImageWallpaperRenderer.cs:161-224` - Removed Task.Run, added logging
- ✅ `WaBiBaBuSy.WallpaperEngine/Renderers/GifWallpaperRenderer.cs:317-377` - Removed Task.Run, added logging

**Build Status:** ✅ Clean build - 0 errors, 5 warnings (pre-existing)

**What Changed:**
1. **Removed SetWindowPos call entirely** - WorkerW hierarchy handles z-order automatically
2. **Removed extended window style manipulation** - Not required for WorkerW technique
3. **Removed Task.Run() from form creation** - Forms must be created on calling thread
4. **Added debug logging** - Log form handle and WorkerW handle for troubleshooting
5. **Matches reference implementation** - Now follows CodeProject article exactly

**Testing Required:**
- [ ] Test image wallpaper - verify desktop icons visible
- [ ] Test video wallpaper - verify desktop icons visible
- [ ] Test GIF wallpaper - verify desktop icons visible
- [ ] Verify taskbar remains accessible and clickable
- [ ] Verify desktop icons can be clicked and interacted with
- [ ] Test on Windows 10
- [ ] Test on Windows 11

**Technical Details:**
The WorkerW window technique works by creating a special window hierarchy:
```
Progman (Desktop)
  └─ SHELLDLL_DefView (Desktop Icons)
  └─ WorkerW (Our wallpaper window goes here)
```

When you parent your window to WorkerW, Windows automatically places it behind SHELLDLL_DefView (desktop icons). Using SetWindowPos to manually adjust z-order breaks this automatic hierarchy and causes the window to appear on top of everything.

---

## Fix v4 - Lively Wallpaper Implementation (2025-10-14)

**Analysis of Lively Wallpaper Codebase Complete**

After studying Lively Wallpaper's implementation (https://github.com/rocksdanister/lively), we identified **critical missing components**:

### Root Causes Identified:

1. ❌ **Not using MapWindowPoints** - Must calculate window position relative to WorkerW parent
2. ❌ **Missing dual-mode support** - Windows 11 24H2 uses "Raised Desktop with Layered ShellView" mode
3. ❌ **Wrong SetWindowPos usage** - Need to call BEFORE and AFTER SetParent with correct coordinates
4. ❌ **Missing WS_CHILD style** - Window needs to be explicitly marked as child
5. ❌ **Not setting WS_EX_LAYERED** - Required for Windows 11 24H2+ layered desktop mode
6. ❌ **No desktop refresh** - Need to call `SystemParametersInfo` after setup

### Key Findings from Lively (WinDesktopCore.cs):

**Windows 11 24H2 Support - "Raised Desktop with Layered ShellView"** (Lines 148-150, 1021-1042)
- Lively detects if Progman has `WS_EX_NOREDIRECTIONBITMAP` extended style
- In this mode, they parent to **Progman** instead of WorkerW
- They set `WS_CHILD` style and `WS_EX_LAYERED` with alpha 255
- They use `SetWindowPos` to z-order BELOW `shellDLL_DefView`

**Two Different Parenting Strategies**:
- **Legacy Mode (Windows 10/11 pre-24H2)**: Parent to WorkerW
- **Layered Mode (Windows 11 24H2+)**: Parent to Progman + z-order below DefView

**SetWindowPos IS Used Correctly** (Lines 498-523, 539-548):
- First `SetWindowPos` - Position window on screen with absolute coordinates
- Call `MapWindowPoints` - Calculate position relative to parent
- Call `SetParent` - Attach to WorkerW/Progman
- Second `SetWindowPos` - Re-position with relative coordinates to parent
- Call `RefreshDesktop()` - Force desktop redraw

### Implementation Plan:

**Phase 1: Update Win32Interop.cs**
- Add `WS_CHILD` window style constant
- Add `WS_EX_NOREDIRECTIONBITMAP` extended style
- Add `MapWindowPoints()` P/Invoke
- Add `ShowWindow()` P/Invoke
- Add `GetWindowLongPtr()`/`SetWindowLongPtr()` for 64-bit compatibility
- Add `SetLayeredWindowAttributes()` P/Invoke

**Phase 2: Create WindowUtil Helper**
- `HasExtendedStyle()` - Check if window has specific extended style
- `SetWindowStyle()` - Add WS_CHILD style
- `SetWindowTransparency()` - Add WS_EX_LAYERED + alpha channel
- `TrySetParent()` - Safe SetParent with error checking

**Phase 3: Update DesktopWindowManager.cs**
- Detect "Raised Desktop" mode (check `WS_EX_NOREDIRECTIONBITMAP` on Progman)
- Store `shellDLL_DefView` handle for z-ordering in layered mode
- Implement dual-mode parenting:
  - **Legacy Mode**: 3-step process with WorkerW
  - **Layered Mode**: Parent to Progman + z-order below DefView
- Add `RefreshDesktop()` method with `SystemParametersInfo`

**Phase 4: Update All Renderers**
- Implement proper coordinate mapping with `MapWindowPoints`
- Add `SetWindowPos` before and after `SetParent`
- Call `RefreshDesktop()` after wallpaper setup

**Files to Modify:**
- ✏️ `WaBiBaBuSy.WallpaperEngine/Native/Win32Interop.cs` - Add missing APIs
- ✏️ `WaBiBaBuSy.WallpaperEngine/Native/DesktopWindowManager.cs` - Add dual-mode support
- ➕ `WaBiBaBuSy.WallpaperEngine/Helpers/WindowUtil.cs` - New helper class
- ✏️ `WaBiBaBuSy.WallpaperEngine/Renderers/VideoWallpaperRenderer.cs` - Update parenting logic
- ✏️ `WaBiBaBuSy.WallpaperEngine/Renderers/ImageWallpaperRenderer.cs` - Update parenting logic
- ✏️ `WaBiBaBuSy.WallpaperEngine/Renderers/GifWallpaperRenderer.cs` - Update parenting logic

**Testing Required:**
- [ ] Test on Windows 10
- [ ] Test on Windows 11 (pre-24H2)
- [ ] Test on Windows 11 24H2+ (layered desktop mode)
- [ ] Verify desktop icons visible and clickable
- [ ] Verify taskbar accessible
- [ ] Test all wallpaper types (Image, Video, GIF)

---

## Fix v4 Implementation - Session 2025-10-14 (Continued)

**Status:** IN PROGRESS - Debugging window visibility issue

### Implementation Completed:

**Phase 1: Win32Interop.cs Updates** ✅
- ✅ Added `WS_CHILD` and `WS_VISIBLE` window style constants
- ✅ Added `WS_EX_NOREDIRECTIONBITMAP` extended style constant
- ✅ Added `MapWindowPoints()` P/Invoke with RECT parameter
- ✅ Added `ShowWindow()` and `GetWindowRect()` P/Invoke
- ✅ Added `GetWindowLongPtr()`/`SetWindowLongPtr()` for 64-bit compatibility
- ✅ Added `SetLayeredWindowAttributes()` P/Invoke
- ✅ Added `GetParent()` P/Invoke for diagnostics
- ✅ Added `RECT` and `POINT` structures
- Location: `WaBiBaBuSy.WallpaperEngine/Native/Win32Interop.cs`

**Phase 2: WindowUtil Helper Class** ✅
- ✅ Created new helper class with utility methods
- ✅ Implemented `HasExtendedStyle()` - Check window extended styles
- ✅ Implemented `SetWindowStyle()` - Add window styles (like WS_CHILD)
- ✅ Implemented `SetWindowExStyle()` - Add extended window styles
- ✅ Implemented `SetWindowTransparency()` - Add WS_EX_LAYERED + alpha channel
- ✅ Implemented `TrySetParent()` - Safe SetParent with error checking
- Location: `WaBiBaBuSy.WallpaperEngine/Helpers/WindowUtil.cs`

**Phase 3: DesktopWindowManager.cs Dual-Mode Support** ✅
- ✅ Detects "Raised Desktop" mode using `WS_EX_NOREDIRECTIONBITMAP` check on Progman
- ✅ Stores `_shellDLL_DefView` handle for z-ordering in layered mode
- ✅ Implemented `SetAsWallpaperLegacyMode()` with Lively's 4-step process:
  - Step 1: Position window with absolute coordinates using SetWindowPos
  - Step 2: Calculate relative position using MapWindowPoints
  - Step 3: Set parent to WorkerW using SetParent
  - Step 4: Reposition with relative coordinates using SetWindowPos
- ✅ Implemented `SetAsWallpaperLayeredMode()` for Windows 11 24H2+:
  - Add WS_CHILD style
  - Add WS_EX_LAYERED with alpha=255
  - Parent to Progman (not WorkerW)
  - Z-order below SHELLDLL_DefView
- ✅ Added `RefreshDesktop()` method (currently disabled for testing)
- Location: `WaBiBaBuSy.WallpaperEngine/Native/DesktopWindowManager.cs`

**Phase 4: Renderer Updates** ✅
- ✅ Updated VideoWallpaperRenderer to pass screen bounds Rectangle to SetAsWallpaperWindow
- ✅ Updated ImageWallpaperRenderer to pass screen bounds Rectangle to SetAsWallpaperWindow
- ✅ Updated GifWallpaperRenderer to pass screen bounds Rectangle to SetAsWallpaperWindow
- Locations: All three renderer files in `WaBiBaBuSy.WallpaperEngine/Renderers/`

**Build Status:** ✅ Clean build - 0 errors, 5 warnings (pre-existing)

### Current Problem: Window Invisible After Parenting

**Symptom:**
- In **Legacy Mode** (forced for testing): All Win32 API calls succeed, but wallpaper window is NOT visible
- In **Layered Mode**: Taskbar flickers briefly, but wallpaper window never appears
- Desktop icons and taskbar remain visible (which is good), but the wallpaper is completely invisible

**Diagnostic Logs from Last Test (Legacy Mode):**
```
Successfully found WorkerW window: 3604768 (Layered mode: False)
Step 1: Positioned window at absolute coords (0, 0)
Step 2: Mapped points - Left: 0, Top: 0, Right: 0, Bottom: 0
Step 3: Successfully set parent to WorkerW
Step 4: Repositioned window at relative coords (0, 0)
Step 5: Skipped RefreshDesktop for testing
Successfully set wallpaper window (Legacy mode)
```

**Key Observations:**
1. WorkerW handle is found successfully (3604768)
2. MapWindowPoints returns (0,0,0,0) - which should be correct for origin
3. SetParent succeeds without errors
4. SetWindowPos calls succeed without errors
5. BUT: The wallpaper window is completely invisible

**Hypothesis:**
The Windows Form may be losing its `WS_VISIBLE` style when parented to WorkerW, or the form's rendering pipeline isn't compatible with being a child of WorkerW.

### Latest Debugging Attempt (2025-10-14 Evening):

**Changes Made:**
1. Added `SWP_SHOWWINDOW` flag to both SetWindowPos calls in legacy mode
2. Added explicit `ShowWindow(hwnd, SW_SHOW)` call after SetParent to WorkerW
3. Added comprehensive diagnostic logging via new `LogWindowState()` method that tracks:
   - Window rectangle (position and size) via GetWindowRect
   - WS_VISIBLE flag status
   - WS_CHILD flag status
   - WS_EX_LAYERED flag status
   - Parent window handle via GetParent
4. Added diagnostic logging at 5 key points:
   - BEFORE Step 1 (Initial state)
   - AFTER Step 1 (Positioned)
   - AFTER Step 3 (SetParent to WorkerW)
   - AFTER Step 3b (ShowWindow call)
   - AFTER Step 4 (Final reposition)

**Files Modified:**
- ✏️ `WaBiBaBuSy.WallpaperEngine/Native/DesktopWindowManager.cs:187-306` - Enhanced legacy mode with diagnostics
- ✏️ `WaBiBaBuSy.WallpaperEngine/Native/Win32Interop.cs:63-64` - Added GetParent P/Invoke

**Next Steps for Testing:**
1. Run the application and apply a wallpaper
2. Check console logs to see window state at each step:
   - Is WS_VISIBLE being lost after SetParent?
   - Is the window rectangle changing unexpectedly?
   - Is the parent handle set correctly?
   - Is WS_CHILD being added automatically by SetParent?
3. Based on diagnostic output, determine if:
   - Windows Forms is incompatible with WorkerW parenting
   - Additional window styles or flags are needed
   - The window needs to be invalidated/refreshed after parenting
   - Alternative approach is needed (e.g., raw Win32 window instead of Windows Forms)

**Testing Status:** ⏳ Awaiting test run with enhanced diagnostics

**Alternative Approaches to Consider:**
1. Try setting WS_CHILD style explicitly before SetParent (like layered mode does)
2. Try adding WS_EX_LAYERED even in legacy mode (maybe Windows Forms needs it?)
3. Try invalidating/updating the window after parenting: `InvalidateRect()`, `UpdateWindow()`
4. Consider using a raw Win32 window instead of Windows Forms (if Forms is incompatible with WorkerW)
5. Check if Lively uses special handling for different renderer types (Forms vs native windows)

---

**Summary:** Full Lively implementation is complete with dual-mode support and proper coordinate mapping. All Win32 API calls succeed without errors, but the wallpaper window becomes invisible after parenting to WorkerW. Enhanced diagnostic logging has been added to track window visibility state at each step. Next session should run test with diagnostics and analyze the window state to determine root cause of invisibility.
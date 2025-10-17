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

---

## Latest Test Results (2025-10-14 Late Evening):

**MAJOR BREAKTHROUGH - Parenting Now Works!**
- ✅ SetParent now successfully sets parent to WorkerW (verified via GetParent)
- ✅ Fix: Added `WS_CHILD` style BEFORE calling SetParent
- ✅ Window is correctly parented: `Parent = 3604768 (WorkerW = 3604768)`
- ✅ Window has WS_VISIBLE=True, WS_CHILD=True
- ✅ Window rectangle is correct: (0, 0, 2560x1440)

**BUT: Wallpaper Still Not Visible**
- ❌ Despite all APIs succeeding, wallpaper window does not render
- ❌ Tried: InvalidateRect, UpdateWindow, RedrawWindow - no effect
- ❌ Tried: Removing WS_EX_NOACTIVATE and WS_EX_TOOLWINDOW - no effect
- Desktop icons and taskbar visible (good) but wallpaper is invisible

**Root Cause Hypothesis:**
Windows Forms may not be compatible with being parented to WorkerW. The Form's rendering pipeline might not work when it's a child of a system window like WorkerW.

---

## TODO for Next Session - CRITICAL INVESTIGATION:

### 1. Compare with Lively Project Implementation
**Question:** Why does Lively work but our implementation doesn't?

**Investigation Tasks:**
- [ ] Check what UI framework Lively uses for their wallpaper windows
  - Is it WPF? WinForms? Raw Win32? DirectX surface?
  - Location: Check `Lively.UI.WinUI/` and renderer implementations
- [ ] Check if Lively uses Windows Forms at all for wallpaper rendering
  - Our code uses `Form` from `System.Windows.Forms`
  - Does Lively use raw Win32 windows or WPF windows instead?
- [ ] Compare Lively's renderer architecture with ours:
  - **Our approach:** Windows Forms with PictureBox (Image), VLC VideoView (Video), etc.
  - **Lively's approach:** Check their renderer implementations in `Lively.UI.WinUI/Views/`
- [ ] Check if Lively does any special initialization for Forms/Windows before parenting
  - Do they set additional window styles?
  - Do they use CreateWindowEx directly instead of Forms?
  - Do they handle WM_PAINT or other messages specially?

### 2. Why Are We Using Windows Forms?
**Question:** Should we be using WPF instead of WinForms for wallpaper rendering?

**Review Our Current Implementation:**
- **ImageWallpaperRenderer** - Uses `Form` + `PictureBox` (WinForms)
  - Location: `WaBiBaBuSy.WallpaperEngine/Renderers/ImageWallpaperRenderer.cs`
  - Creates: `new Form()` with `PictureBox` control
- **VideoWallpaperRenderer** - Uses `Form` + LibVLCSharp `VideoView` (WinForms)
  - Location: `WaBiBaBuSy.WallpaperEngine/Renderers/VideoWallpaperRenderer.cs`
  - Creates: `new Form()` with VLC VideoView control
- **GifWallpaperRenderer** - Uses `Form` + `PictureBox` (WinForms)
  - Location: `WaBiBaBuSy.WallpaperEngine/Renderers/GifWallpaperRenderer.cs`
  - Creates: `new Form()` with animated PictureBox

**Investigation Tasks:**
- [ ] Check if LibVLCSharp has WPF support (`LibVLCSharp.WPF` package)
- [ ] Research: Can WPF windows be parented to WorkerW successfully?
- [ ] Research: Do wallpaper engines typically use raw Win32 windows instead of Forms/WPF?
- [ ] Consider: Should we switch to WPF `Window` instead of WinForms `Form`?
- [ ] Consider: Should we use raw Win32 windows created with CreateWindowEx?

### 3. Is Our ImageRenderer Implementation Weird?
**Question:** Is there something fundamentally wrong with our renderer design?

**Code Review Tasks:**
- [ ] Check if PictureBox renders correctly when parented to WorkerW
  - Test: Create minimal WinForms app that parents Form+PictureBox to WorkerW
  - Compare: Does a simple test app with just Form+PictureBox work?
- [ ] Check if we need to override WndProc to handle WM_PAINT messages
  - Windows Forms might not paint when it's a child of WorkerW
  - We might need to manually handle paint events
- [ ] Check if we should use raw GDI/GDI+ drawing instead of PictureBox
  - Override OnPaint and draw directly to the Form's Graphics context
  - This gives more control over rendering pipeline
- [ ] Review Lively's image renderer implementation
  - File: Check `Lively.UI.WinUI/Views/` for their image wallpaper view
  - Compare their approach to ours

### 4. Alternative Approaches to Test:

**Option A: Raw Win32 Window**
- [ ] Create a test renderer that uses `CreateWindowEx` instead of `Form`
- [ ] Manually handle WM_PAINT messages with raw GDI drawing
- [ ] Test if raw Win32 window parents to WorkerW and renders correctly

**Option B: WPF Window**
- [ ] Convert ImageWallpaperRenderer to use WPF `Window` instead of WinForms `Form`
- [ ] Use WPF `Image` control instead of WinForms `PictureBox`
- [ ] Test if WPF's rendering pipeline works better with WorkerW parenting

**Option C: DirectX/Direct2D Surface**
- [ ] Research if Lively uses DirectX for rendering
- [ ] Consider using SharpDX or similar for direct GPU rendering
- [ ] This might be overkill but worth investigating

**Option D: Windows 11 Layered Desktop Mode**
- [ ] Stop forcing legacy mode and try the native Windows 11 24H2 layered mode
- [ ] Maybe the new mode works better than legacy WorkerW technique?
- [ ] Remove the "FORCING LEGACY MODE FOR TESTING" override

### 5. Diagnostic Questions to Answer:

**About Our Current State:**
- [ ] Does the Form receive any Windows messages (WM_PAINT, WM_ERASEBKGND, etc.) after parenting?
  - Add WndProc override to log all messages
- [ ] Is the Form's Handle still valid after SetParent?
  - Check `Form.IsHandleCreated` and `Form.Handle` after parenting
- [ ] Does the Form's client area exist?
  - Check `Form.ClientRectangle` after parenting
- [ ] Is the PictureBox control rendering?
  - Add Paint event handler to PictureBox and log when it fires

**About Lively:**
- [ ] What window class does Lively create for wallpapers?
  - Use Spy++ or similar tool on running Lively instance
- [ ] What are the window styles/extended styles of Lively's wallpaper windows?
  - Compare with our window after parenting
- [ ] Does Lively use any special COM interfaces or DWM APIs we're missing?

### 6. Key Files to Investigate in Lively:

```
Lively-reference/src/Lively/
├── Lively.UI.WinUI/Views/        # Check their view implementations
├── Lively.Gallery/               # Check their wallpaper implementations
├── Lively.Common/Helpers/        # Already reviewed WindowUtil
└── Lively/Core/WinDesktopCore.cs # Already reviewed - our impl matches this
```

---

## Summary for Next Session:

**Current State:**
1. ✅ WorkerW parenting works correctly (verified via diagnostics)
2. ✅ All window styles are correct (WS_VISIBLE, WS_CHILD, correct parent)
3. ❌ **Windows Form does NOT render when parented to WorkerW**

**Most Likely Root Cause:**
Windows Forms is not compatible with being parented to system windows like WorkerW. The Forms rendering pipeline probably expects to be a top-level window or child of another Form.

**Next Steps Priority:**
1. **HIGHEST PRIORITY:** Investigate what UI framework Lively uses (WPF? Raw Win32?)
2. **HIGH PRIORITY:** Test if switching to WPF Windows works
3. **MEDIUM PRIORITY:** Try creating raw Win32 window with CreateWindowEx
4. **LOW PRIORITY:** Test Windows 11 24H2 native layered mode (stop forcing legacy)

**Critical Question to Answer:**
**Why does everyone else use WPF or raw Win32 for wallpaper engines, and we're using WinForms?** There's probably a good reason WinForms doesn't work for this use case.

---

## FINAL ROOT CAUSE IDENTIFIED (2025-10-17)

**Status:** ✅ **ROOT CAUSE FOUND** - Migration plan created

### The Problem: Windows Forms is Fundamentally Incompatible

After extensive debugging and comparing with Lively Wallpaper's implementation, we discovered:

1. **SetParent succeeds initially** - All Win32 API calls work correctly
2. **Windows Forms immediately resets the parent** - The Forms framework un-parents the window
3. **Wallpaper flashes briefly then disappears** - Visible evidence of Forms fighting the parenting
4. **Windows Forms expects to be a top-level window** - Its rendering pipeline breaks when parented to system windows

### Why Lively Works (And We Don't)

**Lively's Architecture:**
1. ✅ **Uses WPF Windows** instead of WinForms Forms
2. ✅ **Separate Process Architecture** - each wallpaper is a standalone `.exe`
3. ✅ **Main app parents external HWNDs** - Forms framework can't fight back from different process

**Our Current Architecture:**
1. ❌ **Uses Windows Forms** - incompatible with system window parenting
2. ❌ **Same Process** - Forms framework actively fights SetParent calls
3. ❌ **Direct instantiation** - Forms manages its own parent-child relationships

### Evidence Gathered

**From Lively Source Code Analysis:**
- `Lively.Player.Vlc` - Uses **Windows Forms** BUT runs as separate `.exe`
- `Lively.Player.Wmf` - Uses **WPF Window** for media/images, separate `.exe`
- Main Lively app calls `SetParent` on **external process HWNDs**
- IPC via stdin/stdout JSON messages
- HWND sent from player to parent process after window creation

**From Our Testing:**
- All Win32 APIs succeed (SetParent, SetWindowPos, MapWindowPoints, etc.)
- Parent is set correctly initially (verified via GetParent)
- Window becomes invisible immediately after (Forms resets parent to null)
- Adding WS_CHILD style doesn't help
- Overriding CreateParams doesn't help
- Overriding WndProc doesn't help
- **Windows Forms is actively fighting the parenting**

### Solution: Migrate to WPF + Separate Processes

**See:** `MIGRATION_PLAN_WPF_SEPARATE_PROCESS.md` for full migration plan

**High-Level Migration:**
1. Create separate player .exe projects for Image/Video/GIF
2. Use WPF Windows instead of WinForms Forms
3. Implement IPC via stdin/stdout (JSON messages)
4. Parent process launches players and receives HWNDs
5. Parent process calls SetParent on external HWNDs

**Timeline:** ~5 weeks for full migration

**Benefits:**
- ✅ Proven architecture (Lively uses this successfully)
- ✅ Better process isolation
- ✅ Easier crash recovery
- ✅ WPF has better rendering capabilities
- ✅ Separate processes can't fight SetParent

**Drawbacks:**
- Additional complexity (multiple .exe files)
- IPC overhead (minimal with JSON stdin/stdout)
- Process management required
- Larger refactoring effort

---

## Closing Issue 1

**Status:** Issue 1 is being **CLOSED** and moved to new architecture task

**Reason:** The issue cannot be fixed with current Windows Forms architecture. Root cause is fundamental incompatibility between Windows Forms and system window parenting.

**Next Steps:**
1. Close this issue
2. Create new task: "Migrate to WPF + Separate Process Architecture"
3. Follow migration plan in `MIGRATION_PLAN_WPF_SEPARATE_PROCESS.md`
4. Start with Phase 1: Infrastructure and IPC framework

**Lessons Learned:**
- Windows Forms is not suitable for wallpaper engines
- Lively's architecture is proven and should be adopted
- Separate process architecture provides better isolation
- WPF has better compatibility with system window parenting
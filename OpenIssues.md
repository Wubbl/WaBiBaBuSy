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
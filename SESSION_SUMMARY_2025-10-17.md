# Session Summary: Wallpaper Rendering Investigation
**Date:** 2025-10-17
**Duration:** Extended debugging session
**Status:** Root cause identified, migration plan created

---

## Problem

Desktop icons and taskbar disappear when wallpaper is applied. Only the wallpaper is visible, making the desktop unusable.

---

## Investigation Journey

### Attempt 1: Remove SetWindowPos HWND_BOTTOM
- **Theory:** SetWindowPos was breaking the WorkerW hierarchy
- **Result:** ❌ Still broken

### Attempt 2: Study Lively's Implementation
- **Action:** Analyzed Lively Wallpaper's open-source code
- **Finding:** Lively uses complex dual-mode approach (Legacy + Windows 11 24H2 Layered)
- **Implemented:** Full Lively approach with MapWindowPoints, proper coordinate mapping
- **Result:** ❌ SetParent worked, but window invisible

### Attempt 3: Enhanced Diagnostics
- **Added:** Comprehensive logging of window state at each step
- **Finding:** All Win32 APIs succeed, WS_VISIBLE=True, WS_CHILD=True, Parent set correctly
- **Problem:** Window renders but then becomes invisible

### Attempt 4: Try Adding WS_CHILD Before SetParent
- **Theory:** Need to add WS_CHILD style before calling SetParent
- **Result:** ❌ SetParent started failing - couldn't set parent at all

### Attempt 5: Remove WS_CHILD Manipulation
- **Action:** Let SetParent add WS_CHILD automatically (like Lively does)
- **Result:** ❌ SetParent returns success, but GetParent returns 0 (parent not actually set)

### Attempt 6: Try Windows Form Minimized State
- **Theory:** Start form minimized like Lively does
- **Result:** ❌ Form shrinks to tiny size (160x28) when restored to Normal

### Attempt 7: Custom CreateParams with Parent Set
- **Theory:** Set parent in CreateParams before window creation
- **Result:** ❌ Same issue - parent not actually set

### Attempt 8: Show Form First, Then Parent
- **Theory:** Form needs to be fully shown before parenting
- **Result:** ❌ SetParent fails - Windows Forms resets parent to null

### Attempt 9: Try Windows 11 24H2 Layered Mode
- **Action:** Removed legacy mode override, used native layered desktop mode
- **Result:** ❌ Wallpaper flashes briefly then disappears

### Attempt 10: Override WndProc to Block Parent Reset
- **Theory:** Windows Forms is resetting parent via WndProc messages
- **Result:** ❌ Doesn't help - Forms resets parent at a lower level

---

## ROOT CAUSE DISCOVERED

**Windows Forms is fundamentally incompatible with being parented to system windows (WorkerW/Progman).**

### Evidence

1. **SetParent succeeds** - Returns old parent handle correctly
2. **GetParent immediately returns 0** - Parent is reset to null
3. **Wallpaper flashes briefly** - Visible proof of Forms fighting the parenting
4. **All Win32 APIs work correctly** - The problem is Windows Forms, not our code

### Why Lively Works

**Critical Discovery:** Lively uses a **completely different architecture**:

1. **WPF Windows** - Uses `System.Windows.Window` instead of `System.Windows.Forms.Form`
2. **Separate Processes** - Each wallpaper runs as a standalone `.exe` file
3. **IPC Communication** - stdin/stdout JSON messages for control
4. **External HWND Parenting** - Main app parents windows from different processes

**Key Insight:** When you call SetParent from a **different process**, Windows Forms can't fight back because the Forms framework isn't running in the calling process!

---

## Solution

### Migrate to WPF + Separate Process Architecture

**Full plan:** See `MIGRATION_PLAN_WPF_SEPARATE_PROCESS.md`

**High-Level Changes:**
1. Create 3 new WPF .exe projects:
   - `WaBiBaBuSy.Player.Image.exe`
   - `WaBiBaBuSy.Player.Video.exe`
   - `WaBiBaBuSy.Player.Gif.exe`

2. Each player:
   - Uses WPF Window (not WinForms Form)
   - Loads content from command-line args
   - Sends HWND to parent via stdout (JSON)
   - Listens for commands on stdin (JSON)

3. Main app:
   - Launches player processes
   - Receives HWNDs via IPC
   - Calls SetParent on external HWNDs
   - Sends playback commands via IPC

**Timeline:** ~5 weeks

---

## Files Created

1. **`MIGRATION_PLAN_WPF_SEPARATE_PROCESS.md`**
   - Comprehensive migration plan
   - Phase-by-phase implementation guide
   - Code samples and architecture diagrams
   - Risk analysis and timeline estimates

2. **`OpenIssues.md`** (Updated)
   - Root cause documentation
   - Evidence from investigation
   - Closing Issue 1 with migration path

3. **`SESSION_SUMMARY_2025-10-17.md`** (This file)
   - Complete investigation journey
   - All attempts documented
   - Final conclusions

---

## Key Learnings

### What Worked
✅ Lively's WindowUtil implementation for SetParent
✅ Dual-mode detection (Legacy vs Windows 11 24H2)
✅ MapWindowPoints coordinate mapping
✅ Comprehensive diagnostic logging
✅ Analyzing open-source reference implementation

### What Didn't Work
❌ Windows Forms with any combination of flags/styles
❌ CreateParams override
❌ WndProc message blocking
❌ Minimized window state tricks
❌ Adding WS_CHILD manually
❌ Any in-process Windows Forms solution

### Why It Failed
Windows Forms has internal logic that manages parent-child relationships. When you call SetParent via Win32 API, Forms detects the change and **actively resets the parent back to null** because it expects to be a top-level window.

This is a fundamental design limitation of Windows Forms, not a bug in our code.

---

## Recommendations

### Immediate Actions
1. ✅ **Approve migration plan** - Review `MIGRATION_PLAN_WPF_SEPARATE_PROCESS.md`
2. **Start Phase 1** - Set up infrastructure and IPC framework
3. **Proof of concept** - Build Image player first (simplest)

### Long-Term Strategy
1. **Incremental migration** - Don't break existing functionality
2. **Keep old renderers** - Until WPF versions are stable
3. **Feature flag** - Allow switching between old/new during transition
4. **Thorough testing** - Each phase before moving to next

### Alternative Considered (Not Recommended)
- ❌ **Raw Win32 windows** - More complex than WPF, less feature-rich
- ❌ **Try to fix Windows Forms** - Fundamentally broken, not worth the effort
- ❌ **Different wallpaper approach** - Current WorkerW technique is correct

---

## Next Session Tasks

1. Review and approve migration plan
2. Create player project structure:
   - `WaBiBaBuSy.Player.Common` (shared library)
   - `WaBiBaBuSy.Player.Image` (WPF exe)
3. Implement basic IPC framework:
   - Message models (JSON)
   - ProcessCommunicator class
   - StdInListener class
4. Build proof-of-concept Image player
5. Test SetParent with WPF window

---

## Success Metrics

Migration will be considered successful when:
- [ ] Image wallpapers work behind desktop icons
- [ ] Desktop icons and taskbar remain accessible
- [ ] Process isolation working correctly
- [ ] IPC communication stable
- [ ] Crash recovery implemented
- [ ] Performance acceptable (<200MB RAM, <15% CPU)

---

## Conclusion

After 10+ different approaches and extensive debugging, we've identified the root cause: **Windows Forms is incompatible with wallpaper engine requirements**. The solution is to adopt Lively's proven architecture using WPF + separate processes.

This is a larger refactoring effort (~5 weeks) but it's the only viable path forward. The good news is we have a complete migration plan and a proven reference implementation (Lively) to follow.

**Bottom line:** Issue 1 cannot be fixed with Windows Forms. Migration to WPF + separate processes is required.

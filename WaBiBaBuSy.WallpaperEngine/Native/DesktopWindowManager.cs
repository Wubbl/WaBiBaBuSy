using Microsoft.Extensions.Logging;
using WaBiBaBuSy.WallpaperEngine.Helpers;

namespace WaBiBaBuSy.WallpaperEngine.Native;

/// <summary>
/// Manages desktop window integration using the WorkerW window technique.
/// This allows rendering wallpapers behind desktop icons.
/// Supports both legacy WorkerW mode and Windows 11 24H2+ layered desktop mode.
/// </summary>
public class DesktopWindowManager
{
    private readonly ILogger<DesktopWindowManager> _logger;
    private IntPtr _workerW = IntPtr.Zero;
    private IntPtr _progman = IntPtr.Zero;
    private IntPtr _shellDLL_DefView = IntPtr.Zero;
    private bool _isRaisedDesktopWithLayeredShellView = false;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, bool> _wallpaperWindows = new();

    public DesktopWindowManager(ILogger<DesktopWindowManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the WorkerW window handle for wallpaper rendering.
    /// Returns IntPtr.Zero if not found.
    /// </summary>
    public IntPtr WorkerWHandle => _workerW;

    /// <summary>
    /// Gets whether the system is using the new layered desktop mode (Windows 11 24H2+).
    /// </summary>
    public bool IsLayeredDesktopMode => _isRaisedDesktopWithLayeredShellView;

    /// <summary>
    /// Gets the Progman window handle.
    /// </summary>
    public IntPtr ProgmanHandle => _progman;

    /// <summary>
    /// Gets the SHELLDLL_DefView window handle (contains desktop icons).
    /// </summary>
    public IntPtr ShellDllDefViewHandle => _shellDLL_DefView;

    /// <summary>
    /// Gets all tracked wallpaper window handles (set by SetAsWallpaperWindow).
    /// Used for thumbnail capture.
    /// </summary>
    public IEnumerable<IntPtr> WallpaperWindows => _wallpaperWindows.Keys;

    /// <summary>
    /// Untrack a wallpaper window handle (call when disposing a renderer).
    /// </summary>
    public void UntrackWallpaperWindow(IntPtr windowHandle)
    {
        _wallpaperWindows.TryRemove(windowHandle, out _);
    }

    /// <summary>
    /// Finds and returns the WorkerW window handle.
    /// This window sits between the desktop and desktop icons.
    /// Detects Windows 11 24H2+ layered desktop mode.
    /// </summary>
    public IntPtr FindDesktopWorkerWindow()
    {
        try
        {
            _logger.LogInformation("Finding desktop WorkerW window");

            // Fetch the Progman window
            _progman = Win32Interop.FindWindow("Progman", null);
            if (_progman == IntPtr.Zero)
            {
                _logger.LogError("Failed to find Progman window");
                return IntPtr.Zero;
            }

            _logger.LogDebug("Found Progman window: {Progman}", _progman);

            // Check if Windows is using the new layered desktop mode (Windows 11 24H2+)
            bool isActuallyLayeredMode = WindowUtil.HasExtendedStyle(_progman, Win32Interop.WS_EX_NOREDIRECTIONBITMAP);
            _isRaisedDesktopWithLayeredShellView = isActuallyLayeredMode;

            if (isActuallyLayeredMode)
            {
                _logger.LogInformation("Detected Windows 11 24H2+ layered desktop mode - will use layered approach");
            }
            else
            {
                _logger.LogInformation("Detected legacy desktop mode - will use WorkerW approach");
            }

            // Send 0x052C to Progman. This message directs Progman to spawn a
            // WorkerW behind the desktop icons. If it is already there, nothing happens.
            // Parameters: wParam=0xD, lParam=0x1 (undocumented, but the values the shell expects)
            Win32Interop.SendMessageTimeout(
                _progman,
                Win32Interop.WM_SPAWN_WORKER,
                new IntPtr(0xD),
                new IntPtr(0x1),
                Win32Interop.SendMessageTimeoutFlags.SMTO_NORMAL,
                1000,
                out _);

            _logger.LogDebug("Sent spawn worker message to Progman");

            // We enumerate all Windows, until we find one that has the SHELLDLL_DefView
            // as a child. If we found that window, we take its next sibling and assign it to workerw.
            IntPtr workerw = IntPtr.Zero;
            IntPtr shellDefView = IntPtr.Zero;

            Win32Interop.EnumWindows(new Win32Interop.EnumWindowsProc((tophandle, topparamhandle) =>
            {
                IntPtr p = Win32Interop.FindWindowEx(
                    tophandle,
                    IntPtr.Zero,
                    "SHELLDLL_DefView",
                    IntPtr.Zero);

                if (p != IntPtr.Zero)
                {
                    // Gets the WorkerW Window after the current one.
                    workerw = Win32Interop.FindWindowEx(
                        IntPtr.Zero,
                        tophandle,
                        "WorkerW",
                        IntPtr.Zero);

                    shellDefView = p;
                    _logger.LogDebug("Found SHELLDLL_DefView: {DefView}, WorkerW: {WorkerW}", shellDefView, workerw);
                }

                return true;
            }), IntPtr.Zero);

            // In layered desktop mode, WorkerW and DefView are children of Progman
            if (isActuallyLayeredMode)
            {
                // Find SHELLDLL_DefView under Progman
                shellDefView = Win32Interop.FindWindowEx(_progman, IntPtr.Zero, "SHELLDLL_DefView", IntPtr.Zero);
                // Find WorkerW under Progman
                workerw = Win32Interop.FindWindowEx(_progman, IntPtr.Zero, "WorkerW", IntPtr.Zero);
                _logger.LogDebug("Layered mode: Found SHELLDLL_DefView: {DefView}, WorkerW: {WorkerW} under Progman",
                    shellDefView, workerw);
            }

            // Use detected mode - don't force legacy mode anymore
            // Windows 11 24H2+ layered mode might work better with Windows Forms

            _workerW = workerw;
            _shellDLL_DefView = shellDefView;

            if (_workerW != IntPtr.Zero)
            {
                _logger.LogInformation("Successfully found WorkerW window: {WorkerW} (Layered mode: {IsLayered})",
                    _workerW, _isRaisedDesktopWithLayeredShellView);
            }
            else
            {
                _logger.LogWarning("Failed to find WorkerW window");
            }

            return _workerW;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding desktop worker window");
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Sets the specified window as a child of the WorkerW/Progman window,
    /// positioning it behind desktop icons.
    /// Two parenting strategies are needed: pre-24H2 shells expose WorkerW as a top-level window
    /// to parent into, while 24H2+ raises the desktop into a layered shell view where the wallpaper
    /// must become a Progman child ordered below SHELLDLL_DefView instead.
    /// </summary>
    /// <param name="windowHandle">Window handle to set as wallpaper</param>
    /// <param name="screenBounds">Screen bounds (X, Y, Width, Height) for positioning</param>
    /// <param name="forceLegacyMode">Force legacy WorkerW mode even on Windows 11 24H2+ (required for DXGI swap chain windows)</param>
    /// <returns>True if successful</returns>
    public bool SetAsWallpaperWindow(IntPtr windowHandle, System.Drawing.Rectangle screenBounds, bool forceLegacyMode = false)
    {
        try
        {
            if (_workerW == IntPtr.Zero)
            {
                _logger.LogError("WorkerW window not found, cannot set wallpaper window");
                return false;
            }

            // DXGI swap chain windows MUST use legacy mode - layered mode causes explorer crash
            bool useLegacyMode = forceLegacyMode || !_isRaisedDesktopWithLayeredShellView;

            _logger.LogInformation("Setting window {Window} as wallpaper (DetectedMode: {DetectedMode}, ForceLegacy: {ForceLegacy}, UsingMode: {UsingMode}, WorkerW: {WorkerW})",
                windowHandle,
                _isRaisedDesktopWithLayeredShellView ? "Layered" : "Legacy",
                forceLegacyMode,
                useLegacyMode ? "Legacy" : "Layered",
                _workerW);

            bool result;
            if (!useLegacyMode)
            {
                // Windows 11 24H2+ Layered Desktop Mode - parent to Progman with WS_EX_LAYERED
                _logger.LogInformation("Using LAYERED mode (Windows 11 24H2+)");
                result = SetAsWallpaperLayeredMode(windowHandle, screenBounds);
            }
            else
            {
                // Legacy Mode (Windows 10 / Windows 11 pre-24H2) - parent to WorkerW
                _logger.LogInformation("Using LEGACY mode (forced or pre-24H2)");
                result = SetAsWallpaperLegacyMode(windowHandle, screenBounds);
            }

            if (result)
            {
                _wallpaperWindows[windowHandle] = true;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting wallpaper window");
            return false;
        }
    }

    /// <summary>
    /// Legacy mode (Windows 10 / pre-24H2): parent the window into WorkerW, which the shell keeps
    /// between the desktop background and the icon layer.
    /// </summary>
    private bool SetAsWallpaperLegacyMode(IntPtr windowHandle, System.Drawing.Rectangle screenBounds)
    {
        _logger.LogInformation("Using legacy WorkerW parenting mode");
        _logger.LogInformation("Screen bounds: ({X}, {Y}, {W}x{H})",
            screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height);

        // Step 1: position in screen coordinates while the window is still top-level, so step 2 can
        // translate a known rectangle into WorkerW's client space.
        if (!Win32Interop.SetWindowPos(
            windowHandle,
            1,  // HWND_TOP
            screenBounds.X,
            screenBounds.Y,
            screenBounds.Width,
            screenBounds.Height,
            (uint)Win32Interop.SWP_NOACTIVATE))
        {
            _logger.LogWarning("SetWindowPos (before SetParent) failed");
        }
        else
        {
            _logger.LogInformation("Step 1: Positioned window at screen coords ({X},{Y})", screenBounds.X, screenBounds.Y);
        }

        // Step 2: SetParent does not re-map coordinates, so convert the screen rect into
        // WorkerW-relative coordinates before re-parenting.
        var prct = new Win32Interop.RECT();
        Win32Interop.MapWindowPoints(windowHandle, _workerW, ref prct, 2);
        _logger.LogInformation("Step 2: Mapped points relative to WorkerW - ({Left}, {Top})", prct.Left, prct.Top);

        // Styles must be applied while the window is still top-level. Changing WS_CAPTION or
        // WS_EX_TOOLWINDOW after re-parenting leaves the shell with stale frame metrics and the
        // window renders with a border or refuses to paint.
        _logger.LogInformation("Applying window style modifications before SetParent...");
        StripWindowChrome(windowHandle);
        HideFromTaskbar(windowHandle);

        // SetParent sets WS_CHILD itself, but doing it up front means the window is already a
        // well-formed child when the shell first sees it, which avoids a one-frame flash.
        _logger.LogInformation("Manually setting WS_CHILD style BEFORE SetParent...");
        var currentStyle = Win32Interop.GetWindowLong(windowHandle, Win32Interop.GWL_STYLE);
        var newStyle = currentStyle | Win32Interop.WS_CHILD;
        Win32Interop.SetWindowLong(windowHandle, Win32Interop.GWL_STYLE, newStyle);
        _logger.LogInformation("WS_CHILD style set manually (was: 0x{OldStyle:X}, now: 0x{NewStyle:X})",
            currentStyle, newStyle);

        _logger.LogInformation("Window styles applied, ready for SetParent");

        // Step 3: re-parent into WorkerW
        _logger.LogInformation("BEFORE SetParent - checking window state...");
        LogWindowState(windowHandle, "BEFORE SetParent");

        var oldParent = Win32Interop.SetParent(windowHandle, _workerW);
        _logger.LogInformation("Step 3: SetParent returned old parent: {OldParent}, new parent should be WorkerW: {WorkerW}",
            oldParent, _workerW);

        // IMMEDIATELY check if it worked
        var actualParent = Win32Interop.GetParent(windowHandle);
        _logger.LogInformation("IMMEDIATELY after SetParent: GetParent returned: {ActualParent}", actualParent);

        if (actualParent != _workerW)
        {
            _logger.LogError("SetParent FAILED or was RESET! Expected: {Expected}, Actual: {Actual}",
                _workerW, actualParent);

            // Try one more time with a delay
            _logger.LogWarning("Trying SetParent again after 100ms delay...");
            System.Threading.Thread.Sleep(100);
            Win32Interop.SetParent(windowHandle, _workerW);
            actualParent = Win32Interop.GetParent(windowHandle);
            _logger.LogInformation("After retry: GetParent returned: {ActualParent}", actualParent);

            if (actualParent != _workerW)
            {
                LogWindowState(windowHandle, "AFTER SetParent FAILED");
                return false;
            }
        }

        _logger.LogInformation("Successfully parented to WorkerW");
        LogWindowState(windowHandle, "AFTER SetParent SUCCESS");

        // Step 4: re-apply the geometry, now in the parent-relative coordinates from step 2
        if (!Win32Interop.SetWindowPos(
            windowHandle,
            1,  // HWND_TOP
            prct.Left,
            prct.Top,
            screenBounds.Width,
            screenBounds.Height,
            (uint)(Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_NOZORDER)))
        {
            _logger.LogError("SetWindowPos (after SetParent) FAILED");
            return false;
        }

        _logger.LogInformation("Step 4: Repositioned at relative coords ({Left},{Top})", prct.Left, prct.Top);

        // Step 5: force the shell to repaint so the newly parented window becomes visible
        RefreshDesktop();
        _logger.LogInformation("Step 5: Called RefreshDesktop");

        // DIAGNOSTIC: Check window state after all operations
        System.Threading.Thread.Sleep(100);
        LogWindowState(windowHandle, "FINAL STATE (100ms after RefreshDesktop)");

        // Verify window is still visible
        if (Win32Interop.IsWindowVisible(windowHandle))
        {
            _logger.LogInformation("SUCCESS: Window is VISIBLE after all operations");
        }
        else
        {
            _logger.LogError("PROBLEM: Window is NOT VISIBLE after all operations!");
        }

        // Force window to redraw its content after parenting
        _logger.LogInformation("Forcing window to show and redraw...");
        Win32Interop.ShowWindow(windowHandle, Win32Interop.SW_SHOW);
        Win32Interop.InvalidateRect(windowHandle, IntPtr.Zero, true);
        Win32Interop.UpdateWindow(windowHandle);

        // Try setting it to bottom of Z-order explicitly
        _logger.LogInformation("Setting window to bottom of Z-order...");
        Win32Interop.SetWindowPos(windowHandle, Win32Interop.HWND_BOTTOM, 0, 0, 0, 0,
            (uint)(Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE));

        _logger.LogInformation("Successfully set wallpaper window (Legacy mode)");
        return true;
    }

    /// <summary>
    /// Logs the current window state for debugging.
    /// </summary>
    private void LogWindowState(IntPtr hwnd, string context)
    {
        try
        {
            // Get window rectangle
            if (Win32Interop.GetWindowRect(hwnd, out var rect))
            {
                _logger.LogInformation("{Context}: Window rect = ({Left}, {Top}, {Right}, {Bottom}), Size = ({Width}x{Height})",
                    context, rect.Left, rect.Top, rect.Right, rect.Bottom,
                    rect.Right - rect.Left, rect.Bottom - rect.Top);
            }
            else
            {
                _logger.LogWarning("{Context}: GetWindowRect FAILED", context);
            }

            // Get window style
            var style = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE).ToInt64();
            bool hasVisible = (style & Win32Interop.WS_VISIBLE) != 0;
            bool hasChild = (style & Win32Interop.WS_CHILD) != 0;
            _logger.LogInformation("{Context}: WS_VISIBLE={Visible}, WS_CHILD={Child}",
                context, hasVisible, hasChild);

            // Get extended window style
            var exStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE).ToInt64();
            bool hasLayered = (exStyle & Win32Interop.WS_EX_LAYERED) != 0;
            _logger.LogInformation("{Context}: WS_EX_LAYERED={Layered}",
                context, hasLayered);

            // Get parent
            var parent = Win32Interop.GetParent(hwnd);
            _logger.LogInformation("{Context}: Parent = {Parent} (WorkerW = {WorkerW})",
                context, parent, _workerW);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log window state for context: {Context}", context);
        }
    }

    /// <summary>
    /// Layered mode: Parent to Progman and z-order below ShellDLL_DefView (Windows 11 24H2+).
    /// Ordering matters: styles, then geometry, then parenting, then Z-order. Followed by a WPF
    /// composition refresh, because re-parenting invalidates the HwndSource render target.
    /// </summary>
    private bool SetAsWallpaperLayeredMode(IntPtr windowHandle, System.Drawing.Rectangle screenBounds)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("LAYERED MODE - style/geometry/parent/z-order sequence + WPF refresh");
        _logger.LogInformation("========================================");
        _logger.LogInformation("Handles - Window: {Window}, Progman: {Progman}, DefView: {DefView}, WorkerW: {WorkerW}",
            windowHandle, _progman, _shellDLL_DefView, _workerW);

        // Step 1: mark as a child window before parenting, so the shell never sees it top-level
        WindowUtil.AddWindowStyle(windowHandle, Win32Interop.WS_CHILD);
        _logger.LogInformation("Added WS_CHILD style");

        // Step 2: layered composition at full opacity. In 24H2+ the desktop itself is layered, and
        // a non-layered child of Progman is simply not composited. WS_EX_TRANSPARENT must NOT be
        // used here - it crashes explorer.exe on these builds.
        WindowUtil.SetLayeredOpacity(windowHandle, 255);
        _logger.LogInformation("Added WS_EX_LAYERED with alpha=255 (full opacity)");

        // Step 3: position while still top-level (screen coordinates)
        _logger.LogInformation("Positioning window at ({X}, {Y}, {W}x{H})",
            screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height);

        Win32Interop.SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            screenBounds.X,
            screenBounds.Y,
            screenBounds.Width,
            screenBounds.Height,
            (uint)(Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE));

        // Step 4: in layered mode the wallpaper is a Progman child, not a WorkerW child
        if (!WindowUtil.TrySetParent(windowHandle, _progman))
        {
            _logger.LogError("Failed to set parent to Progman");
            return false;
        }
        _logger.LogInformation("Successfully set parent to Progman: {Progman}", _progman);

        // Step 5: insert below SHELLDLL_DefView so desktop icons stay on top of the wallpaper
        var windowFlags = (uint)(Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);

        if (_shellDLL_DefView != IntPtr.Zero)
        {
            _logger.LogInformation("Setting Z-order below SHELLDLL_DefView: {DefView}", _shellDLL_DefView);

            Win32Interop.SetWindowPos(
                windowHandle,
                _shellDLL_DefView,
                0, 0, 0, 0,
                windowFlags);

            _logger.LogInformation("SetWindowPos SUCCESS - Z-order set below DefView");
        }
        else
        {
            _logger.LogError("SHELLDLL_DefView handle is NULL! Cannot set Z-order correctly");
        }

        // Step 6: re-assert WorkerW's position at the bottom of Progman's Z-order
        EnsureWorkerWZOrder();

        _logger.LogInformation("Successfully set wallpaper window (Layered mode)");
        return true;
    }

    /// <summary>
    /// In layered desktop mode, keeps WorkerW pinned to the bottom of Progman's Z-order.
    /// </summary>
    /// <remarks>
    /// Wallpaper windows are inserted directly above WorkerW. If the shell re-orders WorkerW above
    /// one of its siblings, those siblings occlude the wallpaper, so the invariant is re-asserted
    /// after every parenting operation. No-op outside layered mode, where WorkerW is the parent
    /// rather than a sibling.
    /// </remarks>
    private void EnsureWorkerWZOrder()
    {
        if (!_isRaisedDesktopWithLayeredShellView)
            return;

        var bottomMost = WindowUtil.FindBottomMostChild(_progman);
        if (bottomMost == _workerW)
        {
            _logger.LogInformation("WorkerW Z-order is correct (already at bottom)");
            return;
        }

        _logger.LogWarning("WorkerW is not the bottom-most child of Progman (bottom: {BottomMost}, WorkerW: {WorkerW}) - correcting",
            bottomMost, _workerW);

        Win32Interop.SetWindowPos(
            _workerW,
            Win32Interop.HWND_BOTTOM,
            0, 0, 0, 0,
            (uint)(Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE));

        _logger.LogInformation("Moved WorkerW to bottom of Z-order");
    }

    /// <summary>
    /// Strips the frame, caption and system menu so the window can sit flush behind the desktop
    /// icons with no visible chrome.
    /// </summary>
    /// <remarks>
    /// Style bits are cleared with <c>&amp; ~mask</c> so unrelated flags survive. See
    /// https://learn.microsoft.com/windows/win32/winmsg/window-styles for the meaning of each flag.
    /// <c>WS_EX_LAYERED</c> is cleared here as well: the legacy WorkerW path relies on plain,
    /// non-redirected rendering, and a stale layered flag suppresses painting entirely.
    /// </remarks>
    /// <param name="handle">Window handle</param>
    private void StripWindowChrome(IntPtr handle)
    {
        const int chromeStyles =
            Win32Interop.WS_CAPTION |
            Win32Interop.WS_THICKFRAME |
            Win32Interop.WS_SYSMENU |
            Win32Interop.WS_MAXIMIZEBOX |
            Win32Interop.WS_MINIMIZEBOX;

        const int chromeExStyles =
            Win32Interop.WS_EX_DLGMODALFRAME |
            Win32Interop.WS_EX_COMPOSITED |
            Win32Interop.WS_EX_WINDOWEDGE |
            Win32Interop.WS_EX_CLIENTEDGE |
            Win32Interop.WS_EX_LAYERED |
            Win32Interop.WS_EX_STATICEDGE |
            Win32Interop.WS_EX_TOOLWINDOW |
            Win32Interop.WS_EX_APPWINDOW;

        var style = Win32Interop.GetWindowLong(handle, Win32Interop.GWL_STYLE);
        var exStyle = Win32Interop.GetWindowLong(handle, Win32Interop.GWL_EXSTYLE);

        Win32Interop.SetWindowLong(handle, Win32Interop.GWL_STYLE, style & ~chromeStyles);
        Win32Interop.SetWindowLong(handle, Win32Interop.GWL_EXSTYLE, exStyle & ~chromeExStyles);

        _logger.LogInformation(
            "Stripped window chrome - style 0x{OldStyle:X} -> 0x{NewStyle:X}, exStyle 0x{OldExStyle:X} -> 0x{NewExStyle:X}",
            style, style & ~chromeStyles, exStyle, exStyle & ~chromeExStyles);
    }

    /// <summary>
    /// Hides the window from the taskbar and Alt-Tab, and stops it taking focus.
    /// </summary>
    /// <remarks>
    /// <c>SetWindowLong</c> alone is not enough: the shell caches taskbar membership when the window
    /// is first shown, so the window is hidden and re-shown to force the shell to re-evaluate it.
    /// </remarks>
    /// <param name="handle">Window handle</param>
    private void HideFromTaskbar(IntPtr handle)
    {
        const int hiddenExStyles = Win32Interop.WS_EX_NOACTIVATE | Win32Interop.WS_EX_TOOLWINDOW;

        var exStyle = Win32Interop.GetWindowLong(handle, Win32Interop.GWL_EXSTYLE);

        Win32Interop.ShowWindow(handle, Win32Interop.SW_HIDE);
        Win32Interop.SetWindowLong(handle, Win32Interop.GWL_EXSTYLE, exStyle | hiddenExStyles);
        Win32Interop.ShowWindow(handle, Win32Interop.SW_SHOW);

        _logger.LogInformation("Hid window from taskbar - exStyle 0x{OldExStyle:X} -> 0x{NewExStyle:X}",
            exStyle, exStyle | hiddenExStyles);
    }

    /// <summary>
    /// Refreshes the desktop to clear any wallpaper artifacts.
    /// </summary>
    private void RefreshDesktop()
    {
        // Don't refresh in layered mode as it will destroy the WorkerW
        if (_isRaisedDesktopWithLayeredShellView)
            return;

        try
        {
            Win32Interop.SystemParametersInfo(Win32Interop.SPI_SETDESKWALLPAPER, 0, null, Win32Interop.SPIF_UPDATEINIFILE);
            _logger.LogDebug("Desktop refreshed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh desktop");
        }
    }

    /// <summary>
    /// Restores the Windows desktop to its original state by closing the WorkerW window.
    /// This should be called when the application exits to reset the wallpaper.
    /// </summary>
    public void RestoreDesktop()
    {
        try
        {
            if (_workerW != IntPtr.Zero)
            {
                _logger.LogInformation("Restoring desktop, closing WorkerW window: {WorkerW}", _workerW);

                // Send close message to WorkerW window
                Win32Interop.SendMessage(_workerW, Win32Interop.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

                _workerW = IntPtr.Zero;

                _logger.LogInformation("Desktop restored successfully");
            }
            else
            {
                _logger.LogDebug("No WorkerW window to restore");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring desktop");
        }
    }
}

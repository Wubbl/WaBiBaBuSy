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
            // Parameters: wParam=0xD, lParam=0x1 (Lively uses these values)
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
    /// Implements Lively Wallpaper's dual-mode approach for Windows 10/11 compatibility.
    /// </summary>
    /// <param name="windowHandle">Window handle to set as wallpaper</param>
    /// <param name="screenBounds">Screen bounds (X, Y, Width, Height) for positioning</param>
    /// <returns>True if successful</returns>
    public bool SetAsWallpaperWindow(IntPtr windowHandle, System.Drawing.Rectangle screenBounds)
    {
        try
        {
            if (_workerW == IntPtr.Zero)
            {
                _logger.LogError("WorkerW window not found, cannot set wallpaper window");
                return false;
            }

            _logger.LogInformation("Setting window {Window} as wallpaper (Mode: {Mode}, WorkerW: {WorkerW})",
                windowHandle,
                _isRaisedDesktopWithLayeredShellView ? "Layered" : "Legacy",
                _workerW);

            // TEMPORARY: Force legacy mode for WPF windows
            // Layered mode seems to make WPF windows invisible
            _logger.LogWarning("FORCING LEGACY MODE for testing - WPF windows don't work with layered mode");
            return SetAsWallpaperLegacyMode(windowHandle, screenBounds);

            /*
            if (_isRaisedDesktopWithLayeredShellView)
            {
                // Windows 11 24H2+ Layered Desktop Mode
                return SetAsWallpaperLayeredMode(windowHandle, screenBounds);
            }
            else
            {
                // Legacy Mode (Windows 10 / Windows 11 pre-24H2)
                return SetAsWallpaperLegacyMode(windowHandle, screenBounds);
            }
            */
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting wallpaper window");
            return false;
        }
    }

    /// <summary>
    /// Legacy mode: Parent to WorkerW window - EXACT copy of Lively's TrySetWallpaperPerScreen flow.
    /// </summary>
    private bool SetAsWallpaperLegacyMode(IntPtr windowHandle, System.Drawing.Rectangle screenBounds)
    {
        _logger.LogInformation("Using legacy WorkerW parenting mode (EXACT Lively flow)");
        _logger.LogInformation("Screen bounds: ({X}, {Y}, {W}x{H})",
            screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height);

        // Step 1: SetWindowPos BEFORE SetParent - position window at screen location (Lively line 498)
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

        // Step 2: MapWindowPoints to calculate position relative to WorkerW (Lively line 510)
        var prct = new Win32Interop.RECT();
        Win32Interop.MapWindowPoints(windowHandle, _workerW, ref prct, 2);
        _logger.LogInformation("Step 2: Mapped points relative to WorkerW - ({Left}, {Top})", prct.Left, prct.Top);

        // LIVELY CRITICAL SEQUENCE: Apply window styles BEFORE SetParent (Lively WinDesktopCore.cs lines 168-169)
        _logger.LogInformation("Applying Lively's window style modifications BEFORE SetParent...");
        BorderlessWinStyle(windowHandle);
        RemoveWindowFromTaskbar(windowHandle);

        // CRITICAL: Manually set WS_CHILD style BEFORE SetParent (Lively WinDesktopCore.cs line 1023)
        // This is what Lively does in layered mode - let's try it in legacy mode too
        _logger.LogInformation("Manually setting WS_CHILD style BEFORE SetParent...");
        var currentStyle = Win32Interop.GetWindowLong(windowHandle, Win32Interop.GWL_STYLE);
        var newStyle = currentStyle | Win32Interop.WS_CHILD;
        Win32Interop.SetWindowLong(windowHandle, Win32Interop.GWL_STYLE, newStyle);
        _logger.LogInformation("WS_CHILD style set manually (was: 0x{OldStyle:X}, now: 0x{NewStyle:X})",
            currentStyle, newStyle);

        _logger.LogInformation("Window styles applied, ready for SetParent");

        // Step 3: SetParent to WorkerW (Lively's TryAttachToDesktop, line 511)
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

        // Step 4: SetWindowPos AFTER SetParent with relative coordinates (Lively line 514)
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

        // Step 5: Refresh desktop (Lively line 524)
        RefreshDesktop();
        _logger.LogInformation("Step 5: Called RefreshDesktop");

        _logger.LogInformation("Successfully set wallpaper window (Legacy mode - EXACT Lively flow)");
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
    /// </summary>
    private bool SetAsWallpaperLayeredMode(IntPtr windowHandle, System.Drawing.Rectangle screenBounds)
    {
        _logger.LogInformation("Using layered desktop parenting mode (Windows 11 24H2+)");
        _logger.LogInformation("Handles - Window: {Window}, Progman: {Progman}, DefView: {DefView}, WorkerW: {WorkerW}",
            windowHandle, _progman, _shellDLL_DefView, _workerW);

        // Step 1: Add WS_CHILD style
        WindowUtil.SetWindowStyle(windowHandle, Win32Interop.WS_CHILD);
        _logger.LogInformation("Added WS_CHILD style");

        // Step 2: Add WS_EX_LAYERED style with full opacity (alpha = 255)
        // NOTE: WPF windows manage their own composition and break if we add WS_EX_LAYERED manually
        // Skip this step for WPF windows (they already handle layering internally)
        // TODO: Detect WPF window class and skip SetWindowTransparency
        // For now, try without it since WPF windows have AllowsTransparency property
        // WindowUtil.SetWindowTransparency(windowHandle, 255);
        _logger.LogInformation("Skipping WS_EX_LAYERED for WPF window (WPF manages its own composition)");

        // Step 3: Set parent to Progman (not WorkerW!)
        if (!WindowUtil.TrySetParent(windowHandle, _progman))
        {
            _logger.LogError("Failed to set parent to Progman");
            return false;
        }

        _logger.LogInformation("Successfully set parent to Progman: {Progman}", _progman);

        // Step 4: Position window and z-order below SHELLDLL_DefView
        // Using SWP_NOMOVE | SWP_NOSIZE would keep current position, so we DON'T use those flags
        var windowFlags = (uint)(Win32Interop.SWP_NOACTIVATE);

        if (_shellDLL_DefView != IntPtr.Zero)
        {
            _logger.LogInformation("Positioning window at ({X}, {Y}, {W}x{H}) below SHELLDLL_DefView: {DefView}",
                screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height, _shellDLL_DefView);

            // Position the window with its actual size and z-order below DefView
            if (!Win32Interop.SetWindowPos(
                windowHandle,
                (int)_shellDLL_DefView, // Insert below DefView
                screenBounds.X,
                screenBounds.Y,
                screenBounds.Width,
                screenBounds.Height,
                windowFlags))
            {
                _logger.LogError("SetWindowPos FAILED");
            }
            else
            {
                _logger.LogInformation("SetWindowPos SUCCESS - Window positioned at ({X},{Y}) size ({W}x{H})",
                    screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height);
            }

            // CRITICAL: Show the window to make it visible
            Win32Interop.ShowWindow(windowHandle, Win32Interop.SW_SHOW);
            _logger.LogInformation("Called ShowWindow to make window visible");
        }
        else
        {
            _logger.LogError("SHELLDLL_DefView handle is NULL!");

            // Still try to position the window even without DefView
            Win32Interop.SetWindowPos(
                windowHandle,
                1,
                screenBounds.X,
                screenBounds.Y,
                screenBounds.Width,
                screenBounds.Height,
                windowFlags);

            Win32Interop.ShowWindow(windowHandle, Win32Interop.SW_SHOW);
        }

        _logger.LogInformation("Completed layered mode setup");
        return true;
    }

    /// <summary>
    /// Removes window border and some menu items. Based on Lively Wallpaper implementation.
    /// Ref: https://github.com/Codeusa/Borderless-Gaming
    /// </summary>
    /// <param name="handle">Window handle</param>
    private void BorderlessWinStyle(IntPtr handle)
    {
        _logger.LogInformation("Applying borderless window style (Lively method)");

        // Get current window styles
        var styleCurrentWindowStandard = Win32Interop.GetWindowLong(handle, Win32Interop.GWL_STYLE);
        var styleCurrentWindowExtended = Win32Interop.GetWindowLong(handle, Win32Interop.GWL_EXSTYLE);

        _logger.LogInformation("Current styles - Standard: 0x{Standard:X}, Extended: 0x{Extended:X}",
            styleCurrentWindowStandard, styleCurrentWindowExtended);

        // Compute new standard style - remove caption, thick frame, system menu, min/max boxes
        var styleNewWindowStandard = styleCurrentWindowStandard
            & ~(Win32Interop.WS_CAPTION
              | Win32Interop.WS_THICKFRAME
              | Win32Interop.WS_SYSMENU
              | Win32Interop.WS_MAXIMIZEBOX
              | Win32Interop.WS_MINIMIZEBOX);

        // Compute new extended style - remove various window edge styles, layered, toolwindow, appwindow
        var styleNewWindowExtended = styleCurrentWindowExtended
            & ~(Win32Interop.WS_EX_DLGMODALFRAME
              | Win32Interop.WS_EX_COMPOSITED
              | Win32Interop.WS_EX_WINDOWEDGE
              | Win32Interop.WS_EX_CLIENTEDGE
              | Win32Interop.WS_EX_LAYERED
              | Win32Interop.WS_EX_STATICEDGE
              | Win32Interop.WS_EX_TOOLWINDOW
              | Win32Interop.WS_EX_APPWINDOW);

        // Update window styles
        Win32Interop.SetWindowLong(handle, Win32Interop.GWL_STYLE, styleNewWindowStandard);
        Win32Interop.SetWindowLong(handle, Win32Interop.GWL_EXSTYLE, styleNewWindowExtended);

        _logger.LogInformation("New styles applied - Standard: 0x{Standard:X}, Extended: 0x{Extended:X}",
            styleNewWindowStandard, styleNewWindowExtended);
    }

    /// <summary>
    /// Makes window toolwindow and force remove from taskbar. Based on Lively Wallpaper implementation.
    /// </summary>
    /// <param name="handle">Window handle</param>
    private void RemoveWindowFromTaskbar(IntPtr handle)
    {
        _logger.LogInformation("Removing window from taskbar (Lively method)");

        var styleCurrentWindowExtended = Win32Interop.GetWindowLong(handle, Win32Interop.GWL_EXSTYLE);

        var styleNewWindowExtended = styleCurrentWindowExtended
            | Win32Interop.WS_EX_NOACTIVATE
            | Win32Interop.WS_EX_TOOLWINDOW;

        // Update window styles - hide then show to apply changes
        Win32Interop.ShowWindow(handle, Win32Interop.SW_HIDE);
        Win32Interop.SetWindowLong(handle, Win32Interop.GWL_EXSTYLE, styleNewWindowExtended);
        Win32Interop.ShowWindow(handle, Win32Interop.SW_SHOW);

        _logger.LogInformation("Window removed from taskbar");
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

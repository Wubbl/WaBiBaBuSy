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
            _isRaisedDesktopWithLayeredShellView = WindowUtil.HasExtendedStyle(_progman, Win32Interop.WS_EX_NOREDIRECTIONBITMAP);
            if (_isRaisedDesktopWithLayeredShellView)
            {
                _logger.LogInformation("Detected raised desktop with layered ShellView (Windows 11 24H2+)");
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

            // In layered desktop mode, WorkerW is a child of Progman
            if (_isRaisedDesktopWithLayeredShellView)
            {
                workerw = Win32Interop.FindWindowEx(_progman, IntPtr.Zero, "WorkerW", IntPtr.Zero);
                _logger.LogDebug("Layered mode: Found WorkerW as child of Progman: {WorkerW}", workerw);
            }

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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting wallpaper window");
            return false;
        }
    }

    /// <summary>
    /// Legacy mode: Parent to WorkerW window (Windows 10 / Windows 11 pre-24H2).
    /// Uses Lively's 3-step process: Position -> MapPoints -> SetParent -> Reposition.
    /// </summary>
    private bool SetAsWallpaperLegacyMode(IntPtr windowHandle, System.Drawing.Rectangle screenBounds)
    {
        _logger.LogDebug("Using legacy WorkerW parenting mode");

        // Step 1: Position window on screen with absolute coordinates
        if (!Win32Interop.SetWindowPos(
            windowHandle,
            1,
            screenBounds.X,
            screenBounds.Y,
            screenBounds.Width,
            screenBounds.Height,
            (int)Win32Interop.SWP_NOACTIVATE))
        {
            _logger.LogWarning("Failed to set initial window position (Step 1)");
        }

        // Step 2: Calculate position relative to WorkerW using MapWindowPoints
        var prct = new Win32Interop.RECT
        {
            Left = 0,
            Top = 0,
            Right = 0,
            Bottom = 0
        };
        Win32Interop.MapWindowPoints(windowHandle, _workerW, ref prct, 2);
        _logger.LogDebug("Mapped points relative to WorkerW: ({Left}, {Top})", prct.Left, prct.Top);

        // Step 3: Set parent to WorkerW
        if (!WindowUtil.TrySetParent(windowHandle, _workerW))
        {
            _logger.LogError("Failed to set parent to WorkerW");
            return false;
        }

        _logger.LogDebug("Successfully set parent to WorkerW");

        // Step 4: Reposition with relative coordinates
        if (!Win32Interop.SetWindowPos(
            windowHandle,
            1,
            prct.Left,
            prct.Top,
            screenBounds.Width,
            screenBounds.Height,
            (int)(Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_NOZORDER)))
        {
            _logger.LogWarning("Failed to set final window position (Step 4)");
        }

        // Step 5: Refresh desktop to clear any artifacts
        RefreshDesktop();

        _logger.LogInformation("Successfully set wallpaper window (Legacy mode)");
        return true;
    }

    /// <summary>
    /// Layered mode: Parent to Progman and z-order below ShellDLL_DefView (Windows 11 24H2+).
    /// </summary>
    private bool SetAsWallpaperLayeredMode(IntPtr windowHandle, System.Drawing.Rectangle screenBounds)
    {
        _logger.LogDebug("Using layered desktop parenting mode (Windows 11 24H2+)");

        // Step 1: Add WS_CHILD style
        WindowUtil.SetWindowStyle(windowHandle, Win32Interop.WS_CHILD);
        _logger.LogDebug("Added WS_CHILD style");

        // Step 2: Add WS_EX_LAYERED style with full opacity (alpha = 255)
        WindowUtil.SetWindowTransparency(windowHandle, 255);
        _logger.LogDebug("Added WS_EX_LAYERED style with alpha=255");

        // Step 3: Set parent to Progman (not WorkerW!)
        if (!WindowUtil.TrySetParent(windowHandle, _progman))
        {
            _logger.LogError("Failed to set parent to Progman");
            return false;
        }

        _logger.LogDebug("Successfully set parent to Progman");

        // Step 4: Position window and z-order below SHELLDLL_DefView
        var windowFlags = (uint)(Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);

        if (_shellDLL_DefView != IntPtr.Zero)
        {
            Win32Interop.SetWindowPos(
                windowHandle,
                (int)_shellDLL_DefView, // Insert below DefView
                0,
                0,
                0,
                0,
                windowFlags);

            _logger.LogDebug("Z-ordered window below SHELLDLL_DefView");
        }
        else
        {
            _logger.LogWarning("SHELLDLL_DefView not found, z-ordering may be incorrect");
        }

        _logger.LogInformation("Successfully set wallpaper window (Layered mode)");
        return true;
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

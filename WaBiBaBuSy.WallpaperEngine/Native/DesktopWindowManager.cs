using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.WallpaperEngine.Native;

/// <summary>
/// Manages desktop window integration using the WorkerW window technique.
/// This allows rendering wallpapers behind desktop icons.
/// </summary>
public class DesktopWindowManager
{
    private readonly ILogger<DesktopWindowManager> _logger;
    private IntPtr _workerW = IntPtr.Zero;

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
    /// Finds and returns the WorkerW window handle.
    /// This window sits between the desktop and desktop icons.
    /// </summary>
    public IntPtr FindDesktopWorkerWindow()
    {
        try
        {
            _logger.LogDebug("Finding desktop WorkerW window");

            // Fetch the Progman window
            IntPtr progman = Win32Interop.FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
            {
                _logger.LogError("Failed to find Progman window");
                return IntPtr.Zero;
            }

            _logger.LogDebug("Found Progman window: {Progman}", progman);

            // Send 0x052C to Progman. This message directs Progman to spawn a
            // WorkerW behind the desktop icons. If it is already there, nothing happens.
            Win32Interop.SendMessageTimeout(
                progman,
                Win32Interop.WM_SPAWN_WORKER,
                IntPtr.Zero,
                IntPtr.Zero,
                Win32Interop.SendMessageTimeoutFlags.SMTO_NORMAL,
                1000,
                out _);

            _logger.LogDebug("Sent spawn worker message to Progman");

            // We enumerate all Windows, until we find one that has the SHELLDLL_DefView
            // as a child. If we found that window, we take its next sibling and assign it to workerw.
            IntPtr workerw = IntPtr.Zero;

            Win32Interop.EnumWindows(new Win32Interop.EnumWindowsProc((tophandle, topparamhandle) =>
            {
                IntPtr shellDefView = Win32Interop.FindWindowEx(
                    tophandle,
                    IntPtr.Zero,
                    "SHELLDLL_DefView",
                    IntPtr.Zero);

                if (shellDefView != IntPtr.Zero)
                {
                    // Gets the WorkerW Window after the current one.
                    workerw = Win32Interop.FindWindowEx(
                        IntPtr.Zero,
                        tophandle,
                        "WorkerW",
                        IntPtr.Zero);

                    _logger.LogDebug("Found SHELLDLL_DefView, WorkerW: {WorkerW}", workerw);
                }

                return true;
            }), IntPtr.Zero);

            _workerW = workerw;

            if (_workerW != IntPtr.Zero)
            {
                _logger.LogInformation("Successfully found WorkerW window: {WorkerW}", _workerW);
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
    /// Sets the specified window as a child of the WorkerW window,
    /// positioning it behind desktop icons.
    /// </summary>
    public bool SetAsWallpaperWindow(IntPtr windowHandle)
    {
        try
        {
            if (_workerW == IntPtr.Zero)
            {
                _logger.LogError("WorkerW window not found, cannot set wallpaper window");
                return false;
            }

            _logger.LogInformation("Setting window {Window} as wallpaper", windowHandle);

            // Set the wallpaper window as a child of WorkerW
            var result = Win32Interop.SetParent(windowHandle, _workerW);

            if (result == IntPtr.Zero)
            {
                _logger.LogError("Failed to set parent window");
                return false;
            }

            _logger.LogInformation("Successfully set wallpaper window parent");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting wallpaper window");
            return false;
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

using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Helpers;

/// <summary>
/// Utility methods for Windows window manipulation.
/// Based on Lively Wallpaper implementation.
/// </summary>
public static class WindowUtil
{
    /// <summary>
    /// Checks if a window has a specific extended style.
    /// </summary>
    public static bool HasExtendedStyle(IntPtr hwnd, uint style)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        IntPtr exStylePtr = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE);
        if (exStylePtr == IntPtr.Zero)
            return false;

        return (exStylePtr.ToInt64() & style) != 0;
    }

    /// <summary>
    /// Sets a window style by adding it to existing styles.
    /// </summary>
    public static void SetWindowStyle(IntPtr hwnd, long styleToAdd)
    {
        long currentStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE).ToInt64();
        long newStyle = currentStyle | styleToAdd;

        Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE, (IntPtr)newStyle);
    }

    /// <summary>
    /// Sets an extended window style by adding it to existing extended styles.
    /// </summary>
    public static void SetWindowExStyle(IntPtr hwnd, long exStyleToAdd)
    {
        long currentExStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE).ToInt64();
        long newExStyle = currentExStyle | exStyleToAdd;

        Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, (IntPtr)newExStyle);
    }

    /// <summary>
    /// Makes window transparent using layered window attributes.
    /// Adds WS_EX_LAYERED style if not present and sets transparency.
    /// </summary>
    /// <param name="hwnd">Window handle</param>
    /// <param name="transparency">Alpha value (0-255, 255 = opaque)</param>
    public static void SetWindowTransparency(IntPtr hwnd, byte transparency = 255)
    {
        var exStyle = GetExtendedWindowStyle(hwnd);
        if ((exStyle & Win32Interop.WS_EX_LAYERED) == 0)
        {
            var styleNewWindowExtended = exStyle | Win32Interop.WS_EX_LAYERED;
            Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, (IntPtr)styleNewWindowExtended);
        }
        Win32Interop.SetLayeredWindowAttributes(hwnd, 0, transparency, (uint)Win32Interop.LWA_ALPHA);
    }

    /// <summary>
    /// Safely sets parent window with error checking.
    /// </summary>
    public static bool TrySetParent(IntPtr child, IntPtr parent)
    {
        return Win32Interop.SetParent(child, parent) != IntPtr.Zero;
    }

    /// <summary>
    /// Gets the last child window in Z-order (bottom-most child).
    /// From Lively WindowUtil.cs lines 199-210
    /// </summary>
    public static IntPtr GetLastChildWindow(IntPtr parent)
    {
        IntPtr lastChild = IntPtr.Zero;

        Win32Interop.EnumChildWindows(parent, (hWnd, lParam) =>
        {
            lastChild = hWnd;
            return true; // Continue enumeration
        }, IntPtr.Zero);

        return lastChild;
    }

    /// <summary>
    /// Gets the extended window style flags.
    /// </summary>
    private static long GetExtendedWindowStyle(IntPtr hWnd)
    {
        IntPtr exStylePtr = Win32Interop.GetWindowLongPtr(hWnd, Win32Interop.GWL_EXSTYLE);
        return exStylePtr.ToInt64();
    }
}

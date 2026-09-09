using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Helpers;

/// <summary>
/// Thin, allocation-free helpers around the Win32 window style and hierarchy APIs.
/// </summary>
/// <remarks>
/// Every method here is a direct application of the documented behaviour of
/// <c>GetWindowLongPtr</c> / <c>SetWindowLongPtr</c> / <c>SetLayeredWindowAttributes</c>:
/// <list type="bullet">
/// <item>https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindowlongptrw</item>
/// <item>https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowlongptrw</item>
/// <item>https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setlayeredwindowattributes</item>
/// </list>
/// Style words are read and written as 64-bit values because <c>GWL_EXSTYLE</c> flags such as
/// <c>WS_EX_NOREDIRECTIONBITMAP</c> live above the low 32 bits on some builds.
/// </remarks>
public static class WindowUtil
{
    /// <summary>Reads a style word (<c>GWL_STYLE</c> or <c>GWL_EXSTYLE</c>) as a 64-bit mask.</summary>
    private static long ReadStyleWord(IntPtr hwnd, int index) =>
        Win32Interop.GetWindowLongPtr(hwnd, index).ToInt64();

    /// <summary>Writes a style word back to the window.</summary>
    private static void WriteStyleWord(IntPtr hwnd, int index, long value) =>
        Win32Interop.SetWindowLongPtr(hwnd, index, (IntPtr)value);

    /// <summary>ORs <paramref name="bits"/> into the given style word and writes it back.</summary>
    private static long TurnOnBits(IntPtr hwnd, int index, long bits)
    {
        long updated = ReadStyleWord(hwnd, index) | bits;
        WriteStyleWord(hwnd, index, updated);
        return updated;
    }

    /// <summary>
    /// Tests whether every bit in <paramref name="flags"/> is present in the window's extended style.
    /// </summary>
    /// <returns><c>false</c> for a null handle or when the style word cannot be read.</returns>
    public static bool HasExtendedStyle(IntPtr hwnd, uint flags)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        long exStyle = ReadStyleWord(hwnd, Win32Interop.GWL_EXSTYLE);
        return exStyle != 0 && (exStyle & flags) == flags;
    }

    /// <summary>Adds one or more <c>WS_*</c> flags to the window's standard style, preserving the rest.</summary>
    public static long AddWindowStyle(IntPtr hwnd, long flags) =>
        TurnOnBits(hwnd, Win32Interop.GWL_STYLE, flags);

    /// <summary>Adds one or more <c>WS_EX_*</c> flags to the window's extended style, preserving the rest.</summary>
    public static long AddExtendedStyle(IntPtr hwnd, long flags) =>
        TurnOnBits(hwnd, Win32Interop.GWL_EXSTYLE, flags);

    /// <summary>
    /// Applies a uniform alpha to the whole window. <c>WS_EX_LAYERED</c> is a prerequisite for
    /// <c>SetLayeredWindowAttributes</c>, so it is added first when missing.
    /// </summary>
    /// <param name="hwnd">Target window.</param>
    /// <param name="alpha">0 = fully transparent, 255 = fully opaque.</param>
    public static void SetLayeredOpacity(IntPtr hwnd, byte alpha = 255)
    {
        if (!HasExtendedStyle(hwnd, Win32Interop.WS_EX_LAYERED))
            AddExtendedStyle(hwnd, Win32Interop.WS_EX_LAYERED);

        Win32Interop.SetLayeredWindowAttributes(hwnd, 0, alpha, (uint)Win32Interop.LWA_ALPHA);
    }

    /// <summary>Re-parents <paramref name="child"/>, reporting failure instead of throwing.</summary>
    /// <returns><c>true</c> when the window had a previous parent and the call succeeded.</returns>
    public static bool TrySetParent(IntPtr child, IntPtr parent) =>
        Win32Interop.SetParent(child, parent) != IntPtr.Zero;

    /// <summary>
    /// Returns the bottom-most child of <paramref name="parent"/> in Z-order.
    /// </summary>
    /// <remarks>
    /// <c>EnumChildWindows</c> walks children in Z-order from top to bottom, so the handle seen by
    /// the final callback invocation is the bottom-most one.
    /// </remarks>
    public static IntPtr FindBottomMostChild(IntPtr parent)
    {
        IntPtr bottomMost = IntPtr.Zero;

        Win32Interop.EnumChildWindows(parent, (hwnd, _) =>
        {
            bottomMost = hwnd;
            return true;
        }, IntPtr.Zero);

        return bottomMost;
    }
}

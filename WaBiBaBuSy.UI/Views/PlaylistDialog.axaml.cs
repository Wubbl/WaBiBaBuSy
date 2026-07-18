using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;

namespace WaBiBaBuSy.UI.Views;

public partial class PlaylistDialog : Window
{
    public PlaylistDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsWindows())
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                const int GWL_STYLE = -16;
                const int WS_MINIMIZEBOX = 0x20000;
                const int WS_MAXIMIZEBOX = 0x10000;
                var style = GetWindowLong(handle, GWL_STYLE);
                SetWindowLong(handle, GWL_STYLE, style & ~(WS_MINIMIZEBOX | WS_MAXIMIZEBOX));
            }
        }
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// Guard against PlatformImpl being null when Avalonia dispatches input
    /// before the native window is fully created or after disposal.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (PlatformImpl == null) return;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (PlatformImpl == null) return;
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (PlatformImpl == null) return;
        base.OnPointerReleased(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (PlatformImpl == null) return;
        base.OnKeyDown(e);
    }
}

using System.Runtime.InteropServices;

namespace WaBiBaBuSy.Core.Services.Networking;

/// <summary>
/// Queries physical pixels-per-cm per monitor for client registration.
/// Mirrors the DPI logic in WallpaperEngine's NativeMonitorInfo (raw DPI with
/// effective-DPI fallback) so server-side gap math is consistent whether a
/// monitor is local to the server or reported by a remote client.
/// Core cannot reference WallpaperEngine, hence this small local copy.
/// </summary>
internal static class MonitorDpiHelper
{
    /// <summary>
    /// Pixels-per-cm keyed by display device name (e.g. @"\\.\DISPLAY1").
    /// Value 0 means DPI could not be queried for that monitor.
    /// </summary>
    public static Dictionary<string, float> GetPixelsPerCmByDevice()
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
                {
                    var info = new MONITORINFOEX();
                    info.cbSize = Marshal.SizeOf<MONITORINFOEX>();
                    if (GetMonitorInfo(hMonitor, ref info))
                    {
                        // Raw physical DPI (MDT_RAW_DPI=2) first; effective DPI (MDT_EFFECTIVE_DPI=0) fallback.
                        float pixelsPerCm = 0f;
                        if (GetDpiForMonitor(hMonitor, 2, out uint rawDpiX, out _) == 0 && rawDpiX > 0)
                            pixelsPerCm = rawDpiX / 2.54f;
                        else if (GetDpiForMonitor(hMonitor, 0, out uint effDpiX, out _) == 0 && effDpiX > 0)
                            pixelsPerCm = effDpiX / 2.54f;

                        result[info.szDevice] = pixelsPerCm;
                    }
                    return true;
                }, IntPtr.Zero);
        }
        catch
        {
            // DPI APIs unavailable (e.g. pre-8.1 shcore) — callers treat missing entries as unknown.
        }
        return result;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}

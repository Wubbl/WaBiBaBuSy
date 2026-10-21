using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.WallpaperEngine.Native;

/// <summary>
/// Which of this machine's monitors are busy (BiBaBo skips them, design §8.1): visible, non-minimised, non-cloaked
/// top-level windows of other processes — not the desktop, the taskbar or tool windows — covering ≥ 80 % of a monitor
/// (<see cref="BusyRule"/>). Monitor indexes are those of <see cref="NativeMonitorInfo.GetAllMonitors"/>. Any thread.
/// </summary>
public static class MonitorBusyProbe
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_TRANSPARENT = 0x00000020;   // click-through overlays (recorders, GPU overlays) cover everything
    private const int DWMWA_CLOAKED = 14;
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    public static IReadOnlyList<int> BusyMonitors()
    {
        try
        {
            var windows = Windows();
            var monitors = NativeMonitorInfo.GetAllMonitors();
            var busy = new List<int>();
            for (int i = 0; i < monitors.Length; i++)
            {
                var b = monitors[i].Bounds;
                if (BusyRule.IsBusy(new Rectangle(b.X, b.Y, b.Width, b.Height), windows)) busy.Add(i);
            }
            return busy;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Busy] Probe failed: {ex.Message}");
            return Array.Empty<int>();   // unknown = free: never keeps a seat out of the game by mistake
        }
    }

    private static List<Rectangle> Windows()
    {
        uint ownPid = (uint)Environment.ProcessId;
        var result = new List<Rectangle>();
        var className = new StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid) return true;
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & (WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT)) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (ShellClasses.Contains(className.ToString())) return true;
            if (GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top)
                result.Add(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}

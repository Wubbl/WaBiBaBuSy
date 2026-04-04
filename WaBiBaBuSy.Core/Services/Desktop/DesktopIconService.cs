using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.Core.Services.Desktop;

/// <summary>
/// Reads desktop icon positions from Windows' SysListView32 via cross-process memory access.
/// </summary>
public class DesktopIconService
{
    private readonly ILogger<DesktopIconService>? _logger;

    // Win32 constants
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ      = 0x0010;
    private const uint MEM_COMMIT           = 0x1000;
    private const uint MEM_RELEASE          = 0x8000;
    private const uint PAGE_READWRITE       = 0x04;
    private const uint LVM_GETITEMCOUNT     = 0x1004;
    private const uint LVM_GETITEMPOSITION  = 0x1010;
    private const uint SPI_ICONHORIZONTALSPACING = 0x000D;
    private const uint SPI_ICONVERTICALSPACING   = 0x0018;

    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string? cls, string? name);
    [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, IntPtr title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, out uint result, uint winIni);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAllocEx(IntPtr hProc, IntPtr addr, uint size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(IntPtr hProc, IntPtr addr, uint size, uint type);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr hProc, IntPtr baseAddr, [Out] byte[] buf, uint size, out uint read);

    public DesktopIconService(ILogger<DesktopIconService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns the pixel-level grid cell for each desktop icon.
    /// Pixel positions come from LVM_GETITEMPOSITION; caller divides by cell size.
    /// </summary>
    public List<IconGridCell> GetIconPositions()
    {
        var result = new List<IconGridCell>();

        try
        {
            var listView = FindDesktopListView();
            if (listView == IntPtr.Zero)
            {
                _logger?.LogWarning("[IconDetect] SysListView32 not found");
                return result;
            }

            GetWindowThreadProcessId(listView, out uint pid);
            IntPtr hProcess = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                _logger?.LogWarning("[IconDetect] OpenProcess failed (pid={Pid})", pid);
                return result;
            }

            try
            {
                // Allocate a POINT struct (8 bytes) inside explorer.exe
                uint pointSize = 8;
                IntPtr remotePoint = VirtualAllocEx(hProcess, IntPtr.Zero, pointSize, MEM_COMMIT, PAGE_READWRITE);
                if (remotePoint == IntPtr.Zero)
                {
                    _logger?.LogWarning("[IconDetect] VirtualAllocEx failed");
                    return result;
                }

                try
                {
                    int count = (int)SendMessage(listView, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
                    _logger?.LogInformation("[IconDetect] Found {Count} icons", count);

                    var pointBuf = new byte[pointSize];
                    for (int i = 0; i < count; i++)
                    {
                        SendMessage(listView, LVM_GETITEMPOSITION, new IntPtr(i), remotePoint);
                        if (ReadProcessMemory(hProcess, remotePoint, pointBuf, pointSize, out _))
                        {
                            int px = BitConverter.ToInt32(pointBuf, 0);
                            int py = BitConverter.ToInt32(pointBuf, 4);
                            result.Add(new IconGridCell(px, py));
                        }
                    }
                }
                finally
                {
                    VirtualFreeEx(hProcess, remotePoint, 0, MEM_RELEASE);
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IconDetect] Exception reading icon positions");
        }

        return result;
    }

    /// <summary>
    /// Returns the icon grid cell size in pixels via SPI_ICONHORIZONTALSPACING / VERTICALSPACING.
    /// Typical value: 75–80px at 100% DPI; larger at higher DPI.
    /// </summary>
    public (int CellW, int CellH) GetGridCellSize()
    {
        uint w = 75, h = 75;
        try
        {
            SystemParametersInfo(SPI_ICONHORIZONTALSPACING, 0, out w, 0);
            SystemParametersInfo(SPI_ICONVERTICALSPACING,   0, out h, 0);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[IconDetect] Could not read icon spacing, using defaults");
        }
        return ((int)w, (int)h);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static IntPtr FindDesktopListView()
    {
        // Progman → SHELLDLL_DefView → SysListView32
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", IntPtr.Zero);
            if (defView != IntPtr.Zero)
            {
                IntPtr lv = FindWindowEx(defView, IntPtr.Zero, "SysListView32", IntPtr.Zero);
                if (lv != IntPtr.Zero) return lv;
            }
        }

        // Windows 11: WorkerW → SHELLDLL_DefView → SysListView32
        IntPtr workerW = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            IntPtr defView = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", IntPtr.Zero);
            if (defView != IntPtr.Zero)
            {
                IntPtr lv = FindWindowEx(defView, IntPtr.Zero, "SysListView32", IntPtr.Zero);
                if (lv != IntPtr.Zero) { workerW = lv; return false; }
            }
            return true;
        }, IntPtr.Zero);

        return workerW;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
}

/// <summary>
/// A single desktop icon at a pixel position (top-left of icon cell in screen coordinates).
/// </summary>
public record IconGridCell(int PixelX, int PixelY);

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WaBiBaBuSy.WallpaperEngine.Native;

/// <summary>
/// Left clicks on the desktop and key presses while the desktop has the focus, observed — never blocked (BiBaBo on the
/// bomb seat, design §8). Raw Input with <c>RIDEV_INPUTSINK</c> on a hidden message-only window on its own thread
/// (Lively's technique, no hook). A click counts when the window under the cursor belongs to the desktop (Progman /
/// WorkerW), a key when the foreground window does: input into a game, a chat or any other window is ignored. Events
/// run on the sink's thread.
/// </summary>
public sealed class RawInputSink : IDisposable
{
    private const uint WM_INPUT = 0x00FF, WM_QUIT = 0x0012, PM_NOREMOVE = 0x0000;
    private const uint RIDEV_INPUTSINK = 0x00000100, RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
    private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001, RI_KEY_BREAK = 0x0001, RI_KEY_E0 = 0x0002;
    private const uint GA_ROOT = 2;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private Thread? _thread;
    private uint _threadId;
    private WndProc? _wndProc;   // kept alive while the window exists
    private readonly HashSet<ushort> _held = new();   // keys down: auto-repeat is not a new press

    /// <summary>A left click landed on the desktop at (x, y) screen pixels. Raised on the sink's thread.</summary>
    public event Action<int, int>? LeftButtonDown;

    /// <summary>A key went down (not a repeat) while the desktop had the focus: virtual key, E0 prefix. Sink's thread.</summary>
    public event Action<int, bool>? KeyDown;

    public void Start()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "BiBaBoRawInput" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void Loop(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        _wndProc = Proc;
        var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = _wndProc, hInstance = GetModuleHandle(null), lpszClassName = "WaBiBaBuSyRawInput" };
        RegisterClassEx(ref wc);
        var hwnd = CreateWindowEx(0, wc.lpszClassName, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },   // mouse
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },   // keyboard
        };
        if (hwnd == IntPtr.Zero || !RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Debug.WriteLine($"[RawInput] Registration failed (error {Marshal.GetLastWin32Error()})");
        ready.Set();
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            UnregisterClass(wc.lpszClassName, wc.hInstance);
        }
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { OnRawInput(lParam); }
            catch (Exception ex) { Debug.WriteLine($"[RawInput] {ex.Message}"); }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        uint header = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, header) != size) return;
            var h = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (h.dwType == RIM_TYPEKEYBOARD)
            {
                // RAWKEYBOARD: MakeCode (2), Flags (2), Reserved (2), VKey (2), …
                ushort flags = (ushort)Marshal.ReadInt16(buffer, (int)header + 2), vkey = (ushort)Marshal.ReadInt16(buffer, (int)header + 6);
                if ((flags & RI_KEY_BREAK) != 0) { _held.Remove(vkey); return; }
                if (!_held.Add(vkey) || !IsDesktop(GetForegroundWindow())) return;
                KeyDown?.Invoke(vkey, (flags & RI_KEY_E0) != 0);
                return;
            }
            if (h.dwType != RIM_TYPEMOUSE) return;
            // RAWMOUSE: usFlags (2) + padding (2), then usButtonFlags.
            ushort buttonFlags = (ushort)Marshal.ReadInt16(buffer, (int)header + 4);
            if ((buttonFlags & RI_MOUSE_LEFT_BUTTON_DOWN) == 0) return;
            if (!GetCursorPos(out var pt) || !IsDesktop(WindowFromPoint(pt))) return;
            LeftButtonDown?.Invoke(pt.X, pt.Y);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The window belongs to the desktop (its root is Progman or a WorkerW).</summary>
    private static bool IsDesktop(IntPtr hwnd)
    {
        var root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero) return false;
        var name = new System.Text.StringBuilder(32);
        GetClassName(root, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW";
    }

    public void Stop()
    {
        var thread = _thread;
        _thread = null;
        if (thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose() => Stop();

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}

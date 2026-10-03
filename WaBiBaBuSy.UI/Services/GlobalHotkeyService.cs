using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.UI.Services;

/// <summary>
/// System-wide Ctrl+Alt+1..5 for show events. Runs a dedicated thread with its own message queue:
/// <c>RegisterHotKey(NULL, …)</c> posts WM_HOTKEY to the registering thread, so no window is needed and the keys
/// work while the main window is closed. Started only while the server runs (clients never grab the keys).
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012, PM_NOREMOVE = 0x0000;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;

    private readonly object _gate = new();
    private Thread? _thread;
    private uint _threadId;

    /// <summary>A hotkey slot (1..5) was pressed. Raised on the hotkey thread — marshal to the UI thread.</summary>
    public event Action<int>? Pressed;

    /// <summary>Slots another application already holds (not registered).</summary>
    public IReadOnlyList<int> FailedSlots { get; private set; } = Array.Empty<int>();

    /// <summary>True while the hotkey thread runs and the keys are registered.</summary>
    public bool IsRunning { get { lock (_gate) return _thread != null; } }

    /// <summary>Register the hotkeys on a dedicated thread. No-op when already running.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_thread != null) return;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "GlobalHotkeys" };
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(2));
        }
    }

    private void Loop(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);   // create this thread's queue before Stop can post to it
        var failed = new List<int>();
        foreach (int slot in ShowEventHotkeys.Slots)
            if (!RegisterHotKey(IntPtr.Zero, slot, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)('0' + slot)))
                failed.Add(slot);
        FailedSlots = failed;
        ready.Set();
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message != WM_HOTKEY) continue;
                try { Pressed?.Invoke((int)msg.wParam); }
                catch (Exception ex) { Console.Error.WriteLine($"[Hotkeys] Handler failed: {ex.Message}"); }
            }
        }
        finally
        {
            foreach (int slot in ShowEventHotkeys.Slots) UnregisterHotKey(IntPtr.Zero, slot);
        }
    }

    /// <summary>Unregister the hotkeys and end the thread. No-op when not running.</summary>
    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
        }
        if (thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
        FailedSlots = Array.Empty<int>();
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

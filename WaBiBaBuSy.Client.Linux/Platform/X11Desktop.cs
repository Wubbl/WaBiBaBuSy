using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using static WaBiBaBuSy.Client.Linux.Platform.X11Native;

namespace WaBiBaBuSy.Client.Linux.Platform;

/// <summary>A physical monitor in X11 root-window pixel coordinates.</summary>
public sealed record MonitorGeometry(string Name, int X, int Y, int Width, int Height, bool IsPrimary, int WidthMm, int RefreshHz);

/// <summary>How the wallpaper window is placed in the stacking order.</summary>
public enum WindowPlacement
{
    /// <summary>_NET_WM_WINDOW_TYPE_DESKTOP: the desktop layer, below every app (default).</summary>
    Desktop,
    /// <summary>Normal window kept below others (_NET_WM_STATE_BELOW). Fallback if Desktop is hidden behind the shell's own desktop.</summary>
    Below,
    /// <summary>Ordinary window, for debugging.</summary>
    Normal
}

/// <summary>
/// The Linux replacement for the Windows WorkerW trick. Uses EWMH hints that GNOME (Mutter,
/// including XWayland clients under a Wayland session), KDE (KWin) and most X11 window managers
/// honour, plus an empty XShape input region so clicks fall through to whatever is below.
/// </summary>
public sealed class X11Desktop : IDisposable
{
    /// <summary>WM_CLASS of every wallpaper window — what the KWin rule / GNOME extension match on.</summary>
    public const string WmClass = "wabibabusy-wallpaper";

    private readonly ILogger _logger;
    private readonly IntPtr _display;
    private readonly object _lock = new(); // Xlib connection is not thread-safe without XInitThreads
    private readonly HashSet<IntPtr> _ownWindows = new();

    public X11Desktop(ILogger logger)
    {
        _logger = logger;
        _display = XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
            throw new InvalidOperationException("Cannot open X display — is DISPLAY set? (Wayland sessions need XWayland)");
    }

    /// <summary>Enumerate monitors via XRandR 1.5; falls back to the whole root window.</summary>
    public IReadOnlyList<MonitorGeometry> GetMonitors()
    {
        lock (_lock) return GetMonitorsLocked();
    }

    private IReadOnlyList<MonitorGeometry> GetMonitorsLocked()
    {
        var root = XDefaultRootWindow(_display);
        int refreshHz = QueryRefreshRate(root);
        var result = new List<MonitorGeometry>();
        try
        {
            var ptr = XRRGetMonitors(_display, root, true, out int count);
            if (ptr != IntPtr.Zero)
            {
                try
                {
                    int size = Marshal.SizeOf<XRRMonitorInfo>();
                    for (int i = 0; i < count; i++)
                    {
                        var m = Marshal.PtrToStructure<XRRMonitorInfo>(ptr + i * size);
                        result.Add(new MonitorGeometry(AtomName(m.name) ?? $"monitor-{i}",
                            m.x, m.y, m.width, m.height, m.primary != 0, m.mwidth, refreshHz));
                    }
                }
                finally { XRRFreeMonitors(ptr); }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning("XRandR unavailable ({Message}); treating the whole screen as one monitor", ex.Message);
        }

        if (result.Count == 0)
        {
            int screen = XDefaultScreen(_display);
            result.Add(new MonitorGeometry("screen", 0, 0, XDisplayWidth(_display, screen), XDisplayHeight(_display, screen),
                true, XDisplayWidthMM(_display, screen), refreshHz));
        }

        // Same ordering as the Windows client reports: left to right
        return result.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
    }

    private int QueryRefreshRate(IntPtr root)
    {
        try
        {
            var cfg = XRRGetScreenInfo(_display, root);
            if (cfg == IntPtr.Zero) return 60;
            try
            {
                int rate = XRRConfigCurrentRate(cfg);
                return rate > 0 ? rate : 60;
            }
            finally { XRRFreeScreenConfigInfo(cfg); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 60;
        }
    }

    private string? AtomName(IntPtr atom)
    {
        if (atom == IntPtr.Zero) return null;
        var p = XGetAtomName(_display, atom);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringAnsi(p); }
        finally { XFree(p); }
    }

    private IntPtr Atom(string name) => XInternAtom(_display, name, false);

    /// <summary>
    /// Set the properties a WM reads at map time. Call before the window is shown
    /// (and again afterwards — harmless, and covers toolkits that reset them on map).
    /// </summary>
    public void ApplyPreMapHints(IntPtr window, WindowPlacement placement)
    {
        lock (_lock) ApplyPreMapHintsLocked(window, placement);
    }

    private void ApplyPreMapHintsLocked(IntPtr window, WindowPlacement placement)
    {
        _ownWindows.Add(window);
        // WM_CLASS: "instance\0class\0"
        var wmClass = Encoding.ASCII.GetBytes($"{WmClass}\0{WmClass}\0");
        XChangeProperty(_display, window, Atom("WM_CLASS"), Atom("STRING"), 8, PropModeReplace, wmClass, wmClass.Length);

        if (placement != WindowPlacement.Normal)
        {
            var type = placement == WindowPlacement.Desktop ? "_NET_WM_WINDOW_TYPE_DESKTOP" : "_NET_WM_WINDOW_TYPE_NORMAL";
            XChangeProperty(_display, window, Atom("_NET_WM_WINDOW_TYPE"), (IntPtr)XA_ATOM, 32, PropModeReplace,
                new[] { Atom(type) }, 1);

            // Initial state (read by the WM when the window is mapped)
            var states = StateAtoms(placement).Select(Atom).ToArray();
            XChangeProperty(_display, window, Atom("_NET_WM_STATE"), (IntPtr)XA_ATOM, 32, PropModeReplace, states, states.Length);

            // On all workspaces
            XChangeProperty(_display, window, Atom("_NET_WM_DESKTOP"), (IntPtr)XA_CARDINAL, 32, PropModeReplace,
                new[] { new IntPtr(0xFFFFFFFF) }, 1);
        }

        MakeClickThrough(window);
        XFlush(_display);
    }

    /// <summary>
    /// Desktop: no BELOW state — the desktop layer is already below every app, and BELOW/lowering
    /// would put us under the shell's own desktop window (plasmashell) on KDE.
    /// </summary>
    private static string[] StateAtoms(WindowPlacement placement) => placement == WindowPlacement.Below
        ? new[] { "_NET_WM_STATE_BELOW", "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER", "_NET_WM_STATE_STICKY" }
        : new[] { "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER", "_NET_WM_STATE_STICKY" };

    /// <summary>
    /// After mapping: re-request the states via client messages (EWMH: the WM owns _NET_WM_STATE of
    /// mapped windows), then order the window — Desktop: top of the desktop layer (above the
    /// shell's desktop, still below every app); Below: bottom of the stack.
    /// </summary>
    public void ApplyPostMapHints(IntPtr window, WindowPlacement placement)
    {
        lock (_lock) ApplyPostMapHintsLocked(window, placement);
    }

    private void ApplyPostMapHintsLocked(IntPtr window, WindowPlacement placement)
    {
        _ownWindows.Add(window);
        MakeClickThrough(window);
        if (placement == WindowPlacement.Normal)
        {
            XFlush(_display);
            return;
        }

        var root = XDefaultRootWindow(_display);
        var netWmState = Atom("_NET_WM_STATE");
        const int NetWmStateAdd = 1;
        var states = StateAtoms(placement);
        for (int i = 0; i < states.Length; i += 2)
        {
            var ev = new XClientMessageEvent
            {
                type = ClientMessage,
                send_event = 1,
                display = _display,
                window = window,
                message_type = netWmState,
                format = 32,
                l0 = (IntPtr)NetWmStateAdd,
                l1 = Atom(states[i]),
                l2 = i + 1 < states.Length ? Atom(states[i + 1]) : IntPtr.Zero,
                l3 = (IntPtr)1 // source indication: normal application
            };
            XSendEvent(_display, root, false, (IntPtr)(SubstructureRedirectMask | SubstructureNotifyMask), ref ev);
        }

        if (placement == WindowPlacement.Desktop) XRaiseWindow(_display, window);
        else XLowerWindow(_display, window);
        XFlush(_display);
    }

    /// <summary>
    /// Desktop placement: if another desktop-type window (e.g. plasmashell after a click on the
    /// desktop) is stacked above ours, raise ours again. Returns true when a fix was needed.
    /// </summary>
    public bool KeepAboveOtherDesktopWindows()
    {
        lock (_lock)
        {
            var root = XDefaultRootWindow(_display);
            var stacking = ReadProperty(root, Atom("_NET_CLIENT_LIST_STACKING"), (IntPtr)XA_WINDOW, 1024); // bottom → top
            var desktopType = Atom("_NET_WM_WINDOW_TYPE_DESKTOP");
            var windowType = Atom("_NET_WM_WINDOW_TYPE");

            int lowestOwn = -1, highestForeignDesktop = -1;
            for (int i = 0; i < stacking.Length; i++)
            {
                if (_ownWindows.Contains(stacking[i]))
                {
                    if (lowestOwn < 0) lowestOwn = i;
                }
                else if (ReadProperty(stacking[i], windowType, (IntPtr)XA_ATOM, 8).Contains(desktopType))
                {
                    highestForeignDesktop = i;
                }
            }
            if (lowestOwn < 0 || highestForeignDesktop < lowestOwn) return false;

            foreach (var w in stacking.Where(_ownWindows.Contains)) XRaiseWindow(_display, w);
            XFlush(_display);
            return true;
        }
    }

    /// <summary>
    /// True when the focused window is fullscreen (a game, a video) — the Linux counterpart of the
    /// Windows FullscreenDetectionService. Only X11/XWayland windows are visible here; native
    /// Wayland apps never report as fullscreen. BadWindow races are absorbed by Avalonia's
    /// process-wide Xlib error handler.
    /// </summary>
    public bool IsActiveWindowFullscreen()
    {
        lock (_lock)
        {
            var root = XDefaultRootWindow(_display);
            var active = ReadProperty(root, Atom("_NET_ACTIVE_WINDOW"), (IntPtr)XA_WINDOW, 1);
            if (active.Length == 0 || active[0] == IntPtr.Zero || _ownWindows.Contains(active[0])) return false;

            var fullscreen = Atom("_NET_WM_STATE_FULLSCREEN");
            return ReadProperty(active[0], Atom("_NET_WM_STATE"), (IntPtr)XA_ATOM, 32).Contains(fullscreen);
        }
    }

    /// <summary>Read a format-32 property (returned as C longs).</summary>
    private IntPtr[] ReadProperty(IntPtr window, IntPtr property, IntPtr type, int maxItems)
    {
        int status = XGetWindowProperty(_display, window, property, IntPtr.Zero, maxItems, false, type,
            out _, out int format, out IntPtr nItems, out _, out IntPtr data);
        if (status != 0 || data == IntPtr.Zero) return Array.Empty<IntPtr>();
        try
        {
            if (format != 32) return Array.Empty<IntPtr>();
            var result = new IntPtr[(int)nItems];
            Marshal.Copy(data, result, 0, result.Length);
            return result;
        }
        finally { XFree(data); }
    }

    /// <summary>Empty input shape → pointer events pass through to the desktop / windows below.</summary>
    private void MakeClickThrough(IntPtr window)
    {
        try
        {
            XShapeCombineRectangles(_display, window, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, Unsorted);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug("XShape unavailable ({Message}); wallpaper window will receive clicks", ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_lock)
            if (_display != IntPtr.Zero) XCloseDisplay(_display);
    }
}

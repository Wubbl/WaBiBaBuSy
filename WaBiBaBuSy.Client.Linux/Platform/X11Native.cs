using System.Runtime.InteropServices;

namespace WaBiBaBuSy.Client.Linux.Platform;

/// <summary>Minimal Xlib / XRandR / XShape P/Invoke surface (libX11, libXrandr, libXext).</summary>
internal static class X11Native
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXrandr = "libXrandr.so.2";
    private const string LibXext = "libXext.so.6";

    public const int PropModeReplace = 0;
    public const int ClientMessage = 33;
    public const long SubstructureRedirectMask = 1L << 20;
    public const long SubstructureNotifyMask = 1L << 19;
    public const int ShapeInput = 2;
    public const int ShapeSet = 0;
    public const int Unsorted = 0;
    public const int XA_ATOM = 4;
    public const int XA_CARDINAL = 6;

    [DllImport(LibX11)] public static extern IntPtr XOpenDisplay(IntPtr displayName);
    [DllImport(LibX11)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] public static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] public static extern int XDefaultScreen(IntPtr display);
    [DllImport(LibX11)] public static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport(LibX11)] public static extern int XDisplayHeight(IntPtr display, int screen);
    [DllImport(LibX11)] public static extern int XDisplayWidthMM(IntPtr display, int screen);
    [DllImport(LibX11)] public static extern IntPtr XInternAtom(IntPtr display, string atomName, bool onlyIfExists);
    [DllImport(LibX11)] public static extern IntPtr XGetAtomName(IntPtr display, IntPtr atom);
    [DllImport(LibX11)] public static extern int XFree(IntPtr data);
    [DllImport(LibX11)] public static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] public static extern int XSync(IntPtr display, bool discard);

    [DllImport(LibX11)]
    public static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type,
        int format, int mode, IntPtr[] data, int nelements);

    [DllImport(LibX11)]
    public static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type,
        int format, int mode, byte[] data, int nelements);

    [DllImport(LibX11)]
    public static extern int XSendEvent(IntPtr display, IntPtr window, bool propagate, IntPtr eventMask, ref XClientMessageEvent ev);

    [DllImport(LibX11)] public static extern int XLowerWindow(IntPtr display, IntPtr window);
    [DllImport(LibX11)] public static extern int XRaiseWindow(IntPtr display, IntPtr window);

    public const int XA_WINDOW = 33;

    [DllImport(LibX11)]
    public static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr longOffset,
        IntPtr longLength, bool delete, IntPtr reqType, out IntPtr actualType, out int actualFormat,
        out IntPtr nItems, out IntPtr bytesAfter, out IntPtr prop);

    [StructLayout(LayoutKind.Sequential, Size = 192)] // sizeof(XEvent) on LP64
    public struct XClientMessageEvent
    {
        public int type;
        public IntPtr serial;
        public int send_event;
        public IntPtr display;
        public IntPtr window;
        public IntPtr message_type;
        public int format;
        public IntPtr l0, l1, l2, l3, l4;
    }

    // ── XRandR 1.5 monitors ──
    [StructLayout(LayoutKind.Sequential)]
    public struct XRRMonitorInfo
    {
        public IntPtr name;      // Atom
        public int primary;
        public int automatic;
        public int noutput;
        public int x, y, width, height;
        public int mwidth, mheight;
        public IntPtr outputs;
    }

    [DllImport(LibXrandr)] public static extern IntPtr XRRGetMonitors(IntPtr display, IntPtr window, bool getActive, out int nmonitors);
    [DllImport(LibXrandr)] public static extern void XRRFreeMonitors(IntPtr monitors);
    [DllImport(LibXrandr)] public static extern IntPtr XRRGetScreenInfo(IntPtr display, IntPtr window);
    [DllImport(LibXrandr)] public static extern short XRRConfigCurrentRate(IntPtr config);
    [DllImport(LibXrandr)] public static extern void XRRFreeScreenConfigInfo(IntPtr config);

    // ── XShape (input pass-through) ──
    [DllImport(LibXext)]
    public static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int destKind, int xOff, int yOff,
        IntPtr rectangles, int nRects, int op, int ordering);

    /// <summary>With <c>src</c> = None (0) and ShapeSet: back to the default (whole window) region.</summary>
    [DllImport(LibXext)]
    public static extern void XShapeCombineMask(IntPtr display, IntPtr window, int destKind, int xOff, int yOff, IntPtr src, int op);

    // ── Geometry (busy / fullscreen monitors) ──
    [DllImport(LibX11)]
    public static extern int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y,
        out uint width, out uint height, out uint borderWidth, out uint depth);

    [DllImport(LibX11)]
    public static extern bool XTranslateCoordinates(IntPtr display, IntPtr srcWindow, IntPtr destWindow, int srcX, int srcY,
        out int destX, out int destY, out IntPtr child);
}

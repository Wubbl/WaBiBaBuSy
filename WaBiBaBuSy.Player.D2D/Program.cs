using System.Runtime.InteropServices;
using System.Drawing;
using System.Numerics;
using System.Drawing.Imaging;
using ImageMagick;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using DriverType = Vortice.Direct3D.DriverType;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Newtonsoft.Json;
using LibVLCSharp.Shared;
using WaBiBaBuSy.Player.Common.Messages;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Player.D2D;

/// <summary>
/// Separate process for DXGI/Direct2D rendering with local composition.
/// This process receives animation metadata (not frames) and handles all composition locally,
/// eliminating JPEG encoding/decoding and reducing main process CPU to ~0%.
///
/// IPC Protocol (stdin/stdout - JSON messages):
/// - On startup: outputs "HWND:<handle>" when window is ready
/// - Commands (stdin):
///   - JSON: PlayerCommandLoadAnimation - Load animation + composition config
///   - JSON: PlayerCommandStartAnimation - Start playback with timing info
///   - JSON: PlayerCommandStopAnimation - Stop playback
///   - "PARENT:<hwnd>,<z-order>" - Parent window to desktop
///   - "COLOR:RRGGBB" - Fill with solid color (legacy, for testing)
///   - "EXIT" - Clean shutdown
/// - Responses (stdout):
///   - "READY" - Command completed
///   - "ERROR:<message>" - Error occurred
/// </summary>
class Program
{
    // Win32 constants
    private const int CS_HREDRAW = 0x0002;
    private const int CS_VREDRAW = 0x0001;
    private const int CS_OWNDC = 0x0020;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_HOTKEY = 0x0312;
    private const int IDC_ARROW = 32512;

    // Debug overlay hotkey (F11 — F12 is taken by Avalonia Dev Tools in the main UI)
    private const int HOTKEY_ID_DEBUG_OVERLAY = 0xB1B1;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_F11 = 0x7A;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Win32 imports
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(IntPtr hWnd, IntPtr lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    private const uint GW_HWNDPREV = 3;
    private const uint GW_CHILD    = 5;
    private const uint GW_HWNDLAST = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXICON = 11;
    private const int SM_CYICON = 12;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int   dmFields;
        public int   dmPositionX;
        public int   dmPositionY;
        public int   dmDisplayOrientation;
        public int   dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int   dmBitsPerPel;
        public int   dmPelsWidth;
        public int   dmPelsHeight;
        public int   dmDisplayFlags;
        public int   dmDisplayFrequency;
        public int   dmICMMethod;
        public int   dmICMIntent;
        public int   dmMediaType;
        public int   dmDitherType;
        public int   dmReserved1;
        public int   dmReserved2;
        public int   dmPanningWidth;
        public int   dmPanningHeight;
    }

    private static int QueryDisplayRefreshHz(string deviceName)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : -1;
    }

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint WS_CHILD = 0x40000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const uint LWA_ALPHA = 0x2;
    private const uint SWP_NOACTIVATE = 0x0010;

    // ── Desktop icon detection (IconZone mode) ───────────────────────────────
    private const uint DICO_PROCESS_VM_OPERATION = 0x0008;
    private const uint DICO_PROCESS_VM_READ      = 0x0010;
    private const uint DICO_MEM_COMMIT           = 0x1000;
    private const uint DICO_MEM_RELEASE          = 0x8000;
    private const uint DICO_PAGE_READWRITE        = 0x04;
    private const uint DICO_LVM_GETITEMCOUNT      = 0x1004;
    private const uint DICO_LVM_GETITEMPOSITION   = 0x1010;
    private const uint DICO_SPI_ICONHSPACING      = 0x000D;
    private const uint DICO_SPI_ICONVSPACING      = 0x0018;

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool   CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern uint   WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, uint size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool   VirtualFreeEx(IntPtr proc, IntPtr addr, uint size, uint type);
    [DllImport("kernel32.dll")] private static extern bool   ReadProcessMemory(IntPtr proc, IntPtr baseAddr, [Out] byte[] buf, uint size, out uint read);
    [DllImport("user32.dll")]   private static extern uint   GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfoUint(uint action, uint param, out uint result, uint winIni);
    [DllImport("user32.dll")]   private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private static List<(int X, int Y)> DetectDesktopIconPositions()
    {
        var result = new List<(int, int)>();
        try
        {
            var lv = FindDesktopListView();
            if (lv == IntPtr.Zero) return result;

            GetWindowThreadProcessId(lv, out uint pid);
            IntPtr proc = OpenProcess(DICO_PROCESS_VM_OPERATION | DICO_PROCESS_VM_READ, false, pid);
            if (proc == IntPtr.Zero) return result;

            try
            {
                IntPtr remote = VirtualAllocEx(proc, IntPtr.Zero, 8, DICO_MEM_COMMIT, DICO_PAGE_READWRITE);
                if (remote == IntPtr.Zero) return result;
                try
                {
                    int count = (int)SendMessage(lv, DICO_LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
                    var buf = new byte[8];
                    for (int i = 0; i < count; i++)
                    {
                        SendMessage(lv, DICO_LVM_GETITEMPOSITION, new IntPtr(i), remote);
                        if (ReadProcessMemory(proc, remote, buf, 8, out _))
                            result.Add((BitConverter.ToInt32(buf, 0), BitConverter.ToInt32(buf, 4)));
                    }
                }
                finally { VirtualFreeEx(proc, remote, 0, DICO_MEM_RELEASE); }
            }
            finally { CloseHandle(proc); }
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "[IconZone] Icon detection failed"); }
        return result;
    }

    private static (int W, int H) GetIconCellSize()
    {
        try
        {
            if (SystemParametersInfoUint(DICO_SPI_ICONHSPACING, 0, out uint w, 0) &&
                SystemParametersInfoUint(DICO_SPI_ICONVSPACING, 0, out uint h, 0))
                return ((int)w, (int)h);
        }
        catch { }
        return (75, 75);
    }

    /// <summary>
    /// Returns per-monitor icon positions in local (monitor-relative) coordinates.
    /// Cheap: only reads ListView positions, no D2D work.
    /// </summary>
    private static List<(int X, int Y)> GetFilteredIconPositions()
    {
        var (cellW, cellH) = GetIconCellSize();
        var allIcons = DetectDesktopIconPositions();
        var result = new List<(int X, int Y)>(allIcons.Count);
        // Center-based ownership: each icon belongs to exactly one monitor — the one whose
        // rectangle contains the icon cell's center. Cell-rect overlap (the previous test)
        // caused icons near a monitor boundary to be claimed by BOTH neighbors, so phantom
        // zones appeared on the second monitor at coordinates derived from a negative
        // ix - _monitorOffsetX (clamped to x = 0 by the planner).
        foreach (var (ix, iy) in allIcons)
        {
            int cx = ix + cellW / 2;
            int cy = iy + cellH / 2;
            if (cx < _monitorOffsetX || cx >= _monitorOffsetX + _width)  continue;
            if (cy < _monitorOffsetY || cy >= _monitorOffsetY + _height) continue;
            result.Add((ix - _monitorOffsetX, iy - _monitorOffsetY));
        }
        return result;
    }

    /// <summary>
    /// Returns true when <paramref name="fresh"/> differs from <see cref="_detectedIcons"/>.
    /// Comparison is order-independent (sorts both lists).
    /// </summary>
    private static bool IconPositionsChanged(List<(int X, int Y)> fresh)
    {
        if (fresh.Count != _detectedIcons.Count) return true;
        var a = fresh.OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
        var b = _detectedIcons.OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return true;
        return false;
    }

    private static IntPtr FindDesktopListView()
    {
        IntPtr prog = FindWindow("Progman", null);
        if (prog != IntPtr.Zero)
        {
            IntPtr def = FindWindowEx(prog, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (def != IntPtr.Zero)
            {
                IntPtr lv = FindWindowEx(def, IntPtr.Zero, "SysListView32", null);
                if (lv != IntPtr.Zero) return lv;
            }
        }
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            IntPtr def = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (def != IntPtr.Zero)
            {
                IntPtr lv = FindWindowEx(def, IntPtr.Zero, "SysListView32", null);
                if (lv != IntPtr.Zero) { found = lv; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    private const uint PM_REMOVE = 0x0001;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Instance fields
    private static IntPtr _hwnd = IntPtr.Zero;
    private static WndProcDelegate? _wndProcDelegate;
    private static string _windowClassName = "";
    private static ushort _classAtom;
    private static GCHandle _wndProcHandle;

    // Direct3D/Direct2D - Stage 1: Modern DeviceContext pattern
    private static ID3D11Device? _d3dDevice;
    private static ID3D11DeviceContext? _immediateContext;
    private static IDXGISwapChain1? _swapChain;
    // Waitable object owned by the swap chain (FrameLatencyWaitableObject flag). Signaled
    // when DWM is ready to consume our next frame. Closed automatically when _swapChain
    // is disposed — do NOT CloseHandle it ourselves.
    private static IntPtr _frameLatencyWaitable = IntPtr.Zero;
    private static ID2D1Factory1? _d2dFactory;
    private static ID2D1Device? _d2dDevice;
    private static ID2D1DeviceContext? _d2dContext;

    // State
    private static int _width;
    private static int _height;
    private static volatile bool _running = true;
    private static readonly object _colorLock = new();
    private static Color4 _currentColor = new(0, 0, 0, 1); // Default black
    private static volatile bool _windowShown = false;
    private static IntPtr _zOrderReference = IntPtr.Zero;
    // True when parented under Progman with DefView as z-order ref (Win11 24H2+ raised desktop).
    // False in legacy WorkerW mode where _zOrderReference is HWND_BOTTOM (= IntPtr(1), not a real window).
    private static volatile bool _isLayeredMode = false;

    // IconZone refresh debouncing — consumed by render loop when the desktop icons move.
    private static volatile bool _needsIconRefresh = false;
    private static long _iconRefreshScheduledAt = 0;   // tick when refresh was last requested
    private const  long IconRefreshDebounceMs   = 400; // wait for desktop to settle before re-detecting
    private static long _lastIconPositionCheckTick = 0;

    // Desktop-state poll: catastrophic-orphaning + z-order correctness.
    //   - Catastrophic (parent or DefView destroyed) → signal host to re-issue PARENT.
    //   - Z-order disturbance (we got bumped above DefView, e.g. by Snipping Tool) →
    //     re-issue SetWindowPos locally (idempotent — no-op when already correct).
    private static long _lastDesktopPollTick    = 0;
    private static long _lastOrphanSignalTick   = 0;
    private const  long DesktopPollIntervalMs   = 5_000;
    private const  long OrphanSignalCooldownMs  = 30_000;

    // Pending PARENT command to be processed on main thread
    private static volatile string? _pendingParentCommand = null;
    private static readonly object _parentLock = new();

    // Debug overlay state (togglable via F12 hotkey or cmd_toggle_debug_overlay stdin message)
    private struct DebugOverlayState
    {
        public bool Enabled;
        public bool ShowPath;
        public bool ShowIconRects;
        public bool ShowZoneBandOutlines;
        public bool ShowInfoPanel;
    }
    private static DebugOverlayState _debugOverlay = new()
    {
        Enabled = false,
        ShowPath = true,
        ShowIconRects = true,
        ShowZoneBandOutlines = false,
        ShowInfoPanel = true
    };

    // Composition system (video fallback path)
    private static CompositionRenderer? _compositionRenderer;
    private static VirtualCanvasManager? _canvasManager;
    private static AnimationLayerConfig? _animationConfig;
    private static BackgroundLayerConfig? _backgroundConfig;
    private static readonly object _compositionLock = new();
    private static volatile bool _compositionInitialized = false;

    // Stage 2: Native D2D GIF frame cache
    private static ID2D1Bitmap[]? _d2dGifFrames;      // GPU-cached frames (primary source — for back-compat single-source paths)
    private static List<int>? _d2dGifDelays;            // Per-frame delay (ms)
    private static long _d2dGifTotalDurationMs;
    private static int _contentNativeWidth, _contentNativeHeight;
    private static double _gifSpeedMultiplier = 1.0;
    private static volatile bool _useNativeD2DComposition = false;

    // Multi-source frame caches (one entry per AnimationPaths[i]). Always populated; for the
    // single-source case the list has exactly one entry. The animation render loop uses these
    // when Pattern is active OR multi-image mode is on.
    private static readonly List<ID2D1Bitmap[]> _d2dFramesPerSource = new();
    private static readonly List<List<int>>     _d2dDelaysPerSource = new();
    private static readonly List<long>          _d2dTotalDurationPerSource = new();
    private static readonly List<(int W, int H)> _sourceDimensions = new();

    // F2: Color grading effect — lazy-created on first use, reused every frame.
    private static ID2D1Effect? _colorMatrixEffect;
    private static readonly byte[] _colorMatrixBytes = new byte[20 * sizeof(float)];

    // F3a: Zone clip mask — when true, after drawing the pattern we cover icon-zone rects
    // with corridor color so user desktop icons remain visible. Background is rendered plain
    // (no palette) when UseZonePalette is false.
    private static volatile bool _maskZones = false;
    private static volatile bool _useZonePalette = false;
    private static ID2D1SolidColorBrush? _zoneCoverBrush;

    // Stage 6: Native D2D video playback (LibVLC → raw buffer → CopyFromMemory → ID2D1Bitmap)
    private static LibVLC? _libVLC;
    private static LibVLCSharp.Shared.MediaPlayer? _vlcPlayer;
    private static IntPtr _videoBufferA;
    private static IntPtr _videoBufferB;
    private static volatile IntPtr _videoWriteBuffer;
    private static volatile IntPtr _videoReadBuffer;
    private static volatile bool _videoFrameReady;
    private static ID2D1Bitmap? _currentVideoD2DBitmap;
    private static int _videoNativeWidth, _videoNativeHeight;
    private static volatile bool _useNativeD2DVideo;

    // Stage 3: Native D2D background
    private static ID2D1Bitmap? _backgroundImageBitmap;  // For image backgrounds
    private static Color4 _backgroundColor;               // For solid color
    private static BackgroundMode _backgroundMode;

    // ThreeZone background brushes (pre-created, reused each frame)
    private static ID2D1SolidColorBrush? _topZoneBrush;
    private static ID2D1SolidColorBrush? _bottomZoneBrush;
    private static ID2D1SolidColorBrush? _corridorBrush;

    // Corridor constraint (derived from ThreeZone background)
    private static int _corridorTopPx;
    private static int _corridorHeightPx;
    private static bool _hasCorridorConstraint;

    // IconZone mode — N-brush zone drawing + precomputed path
    private static readonly List<(float Y, float Height, float X, float Width, bool IsFree, ID2D1SolidColorBrush? Brush)> _iconZoneBands = new();
    private static Color4 _iconCorridorBgColor = new Color4(0.118f, 0.118f, 0.118f, 1f); // #1E1E1E default
    private static readonly List<(float X, float Y)> _animPath = new();
    private static float _animPathTotalLength;
    // Raw per-monitor icon positions (local coords, set in InitializeBackground IconZone)
    private static readonly List<(int X, int Y)> _detectedIcons = new();
    private static int _detectedCellW = 75, _detectedCellH = 75;
    // Number of full A* path traversals completed; used to trigger per-traverse path variation
    private static int _traverseCount = 0;

    // Pending path update from host (cmd_update_path); consumed at the top of UpdateAnimationPosition.
    // Written by the stdin command thread, read by the render thread.
    private static volatile List<WaypointF>? _pendingNewPath = null;

    // Actual movement trail — ring buffer of recent animation center positions in local space.
    // Shows the true trajectory including SineWave offsets, so it can be compared to the A* path.
    private const int MOVEMENT_TRAIL_CAPACITY = 180;
    private static readonly (float X, float Y)[] _movementTrail = new (float X, float Y)[MOVEMENT_TRAIL_CAPACITY];
    private static int _movementTrailCount;
    private static int _movementTrailHead;

    // Debug overlay brushes — lazy-created the first time the overlay turns on.
    private static ID2D1SolidColorBrush? _debugPathBrush;
    private static ID2D1SolidColorBrush? _debugWaypointBrush;
    private static ID2D1SolidColorBrush? _debugAnimRectBrush;
    private static ID2D1SolidColorBrush? _debugInfoBrush;
    private static ID2D1SolidColorBrush? _debugIconRectBrush;
    private static ID2D1SolidColorBrush? _debugTrailBrush;
    private static ID2D1SolidColorBrush? _debugZoneBandBrush;

    // Stage 4: Animation positioning (ported from AnimationLayerRenderer)
    private static int _animWidth, _animHeight;    // Scaled by FitMode
    private static float _animX, _animY;            // Current position
    private static ContentFitMode _fitMode;
    private static bool _centerInitialPosition;

    // Movement system
    private static MovementConfig? _movementConfig;
    private static int _virtualCanvasWidth = 1920;
    private static int _monitorOffsetX = 0;
    private static int _monitorOffsetY = 0;
    private static bool _rotateWithPath;
    private static float _animRotationRad;

    // Animation state
    private static volatile bool _isPlaying = false;
    private static long _startTimestampMs = 0;
    private static int _pixelsPerSecond = 0;
    private static DateTime _renderLoopStart = DateTime.MinValue;
    private static long _frameCount = 0;
    private static DateTime _lastLoopLogTime = DateTime.MinValue;

    // Test mode: simple color toggle to verify swap chain works
    private static volatile bool _testModeEnabled = false;
    private static DateTime _lastColorToggle = DateTime.MinValue;

    // Logging
    private static ILogger? _logger;

    static int Main(string[] args)
    {
        try
        {
            // Setup logging - CRITICAL: All logs must go to stderr, not stdout!
            // stdout is reserved for IPC protocol (HWND:, READY, ERROR:)
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole(options =>
                {
                    options.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace; // ALL logs to stderr
                    options.FormatterName = "simple";
                });
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;       // No multi-line wrapping
                    options.IncludeScopes = false;
                });
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            });
            _logger = loggerFactory.CreateLogger<Program>();

            // Parse command line: --bounds x,y,width,height [--test]
            int x = 0, y = 0;
            _width = 800;
            _height = 600;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--bounds" && i + 1 < args.Length)
                {
                    var parts = args[i + 1].Split(',');
                    if (parts.Length == 4)
                    {
                        x = int.Parse(parts[0]);
                        y = int.Parse(parts[1]);
                        _width = int.Parse(parts[2]);
                        _height = int.Parse(parts[3]);
                        _monitorOffsetY = y;
                    }
                }
                else if (args[i] == "--test")
                {
                    // Start in test mode: toggle red/blue every 2 seconds
                    _testModeEnabled = true;
                    _lastColorToggle = DateTime.UtcNow;
                }
            }

            _logger?.LogInformation("D2DPlayer starting: bounds=({X},{Y},{Width},{Height})", x, y, _width, _height);

            // Create window
            CreateNativeWindow(x, y, _width, _height);

            // Create D3D/D2D resources (Stage 1: DeviceContext pattern)
            CreateD3DDevice();
            CreateSwapChain();
            CreateD2DDeviceContext();

            // Output HWND for parent process
            Console.WriteLine($"HWND:{_hwnd.ToInt64()}");
            Console.Out.Flush();

            // Start command processing in background
            var commandThread = new Thread(ProcessCommands) { IsBackground = true };
            commandThread.Start();

            // Run render loop with message pump on MAIN thread
            RenderLoop();

            // Cleanup
            Cleanup();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR:{ex.Message}");
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
        uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    private static void CreateNativeWindow(int x, int y, int width, int height)
    {
        _windowClassName = $"WaBiBaBuSyPlayer_{Guid.NewGuid():N}";
        var hInstance = GetModuleHandle(null);

        // Create and pin the delegate to prevent garbage collection
        _wndProcDelegate = WindowProc;
        _wndProcHandle = GCHandle.Alloc(_wndProcDelegate);

        // Allocate unmanaged string for class name
        var classNamePtr = Marshal.StringToHGlobalUni(_windowClassName);

        try
        {
            var wndClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = CS_HREDRAW | CS_VREDRAW | CS_OWNDC,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = hInstance,
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                hbrBackground = IntPtr.Zero,
                lpszMenuName = IntPtr.Zero,
                lpszClassName = classNamePtr,
                hIcon = IntPtr.Zero,
                hIconSm = IntPtr.Zero,
                cbClsExtra = 0,
                cbWndExtra = 0
            };

            _classAtom = RegisterClassExW(ref wndClass);
            if (_classAtom == 0)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Exception($"Failed to register window class. Error: {error}");
            }

            // Create window HIDDEN initially - will be shown on first frame render
            _hwnd = CreateWindowExW(
                WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                _windowClassName,
                "WaBiBaBuSy Player",
                WS_POPUP, // No WS_VISIBLE - window starts hidden
                x, y, width, height,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Exception($"Failed to create window. Error: {error}");
            }

            UpdateWindow(_hwnd);

            // F11 global hotkey — toggles debug overlay from anywhere, even when a game is focused.
            // Registration failure is non-fatal; the stdin command still works.
            if (!RegisterHotKey(_hwnd, HOTKEY_ID_DEBUG_OVERLAY, MOD_NOREPEAT, VK_F11))
            {
                var err = Marshal.GetLastWin32Error();
                _logger?.LogWarning("[Debug] RegisterHotKey F11 failed (error={Err}). Overlay toggle is only available via stdin command.", err);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePtr);
        }
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                ValidateRect(_hwnd, IntPtr.Zero);
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return new IntPtr(1);
            case WM_HOTKEY:
                if (wParam.ToInt32() == HOTKEY_ID_DEBUG_OVERLAY)
                {
                    _debugOverlay.Enabled = !_debugOverlay.Enabled;
                    _logger?.LogInformation("[Debug] Overlay {State} via F11", _debugOverlay.Enabled ? "ON" : "OFF");
                }
                return IntPtr.Zero;
            case WM_DESTROY:
                _running = false;
                return IntPtr.Zero;
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private static void CreateD3DDevice()
    {
        var creationFlags = DeviceCreationFlags.BgraSupport;

        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            creationFlags,
            featureLevels,
            out _d3dDevice,
            out _,
            out _immediateContext);

        if (result.Failure)
        {
            throw new Exception($"Failed to create D3D11 device: {result}");
        }
    }

    private static void CreateSwapChain()
    {
        if (_d3dDevice == null || _hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("D3D device or window not initialized");
        }

        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        using var dxgiAdapter = dxgiDevice.GetAdapter();
        using var dxgiFactory = dxgiAdapter.GetParent<IDXGIFactory2>();

        var swapChainDesc = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            // Scaling.None + FlipDiscard are the two preconditions DWM checks before granting
            // "independent flip" to a flip-model swap chain. Stretch forces a composited path
            // that paces to the primary monitor's refresh rate, which causes tearing on
            // secondary panels at a different rate (e.g. 165 Hz primary + 75 Hz secondary).
            // Buffer dims (_width/_height) come straight from --bounds and match the window
            // client area 1:1, so Scaling.None is safe here.
            Scaling = Scaling.None,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            // FrameLatencyWaitableObject lets us pace by waiting for DWM to be ready for a
            // new frame before we start rendering, instead of letting Present(1) buffer up
            // to ~3 frames in DWM's queue. Combined with SetMaximumFrameLatency(1) below
            // this tightens the timing relationship with DWM's compositor — important on
            // mixed-refresh-rate setups where DWM is forced to composite (layered/WorkerW
            // wallpaper windows can never use independent flip on Win11 24H2+).
            Flags = SwapChainFlags.FrameLatencyWaitableObject
        };

        _swapChain = dxgiFactory.CreateSwapChainForHwnd(_d3dDevice, _hwnd, swapChainDesc);

        // Reduce DWM-side queue depth to 1 frame and grab the waitable handle.
        using (var sc2 = _swapChain.QueryInterface<IDXGISwapChain2>())
        {
            sc2.MaximumFrameLatency = 1;
            _frameLatencyWaitable = sc2.FrameLatencyWaitableObject;
        }

        // Diagnostic (mixed-refresh-rate tearing investigation): which monitor did DXGI
        // associate this swap chain with, and what is its current refresh rate? Present(1)
        // syncs to *this* output's vblank — if it's the wrong one, we'd see tearing on the
        // intended monitor.
        try
        {
            using var output = _swapChain.GetContainingOutput();
            var od = output.Description;
            var r = od.DesktopCoordinates;
            int refreshHz = QueryDisplayRefreshHz(od.DeviceName);
            _logger?.LogInformation(
                "[SwapChain] containingOutput={Name} rect=({L},{T})-({R},{B}) panel={W}x{H}@{Hz}Hz | buffer={BufW}x{BufH} scaling={Scaling} swapEffect={SwapEffect}",
                od.DeviceName, r.Left, r.Top, r.Right, r.Bottom,
                r.Right - r.Left, r.Bottom - r.Top, refreshHz,
                _width, _height, swapChainDesc.Scaling, swapChainDesc.SwapEffect);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SwapChain] Failed to query containing output for diagnostics");
        }
    }

    /// <summary>
    /// Stage 1: Create persistent ID2D1DeviceContext instead of legacy ID2D1RenderTarget.
    /// The DeviceContext persists across frames - only the render target bitmap is swapped per-frame.
    /// This allows cached ID2D1Bitmaps (GIF frames, background images) to survive across presents.
    /// </summary>
    private static void CreateD2DDeviceContext()
    {
        if (_swapChain == null || _d3dDevice == null)
        {
            throw new InvalidOperationException("Swap chain or D3D device not initialized");
        }

        _d2dFactory = Vortice.Direct2D1.D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);

        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        _logger?.LogInformation("D2D DeviceContext created (persistent, no per-frame recreation needed)");
    }

    private static void ProcessParentCommand(string line)
    {
        var parts = line.Substring(7).Split(',');
        if (parts.Length >= 1)
        {
            try
            {
                var parentHwnd = new IntPtr(long.Parse(parts[0]));
                var zOrderHwnd = parts.Length >= 2 ? new IntPtr(long.Parse(parts[1])) : IntPtr.Zero;

                // FIX: Use WS_EX_LAYERED instead of WS_EX_TRANSPARENT to prevent Explorer crashes.
                // WS_EX_TRANSPARENT crashes explorer.exe on Windows 11 24H2+ when used on desktop-parented windows.
                // WS_EX_LAYERED + SetLayeredWindowAttributes(0xFF) allows DirectX presents without performance issues.
                var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
                SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA); // Full opacity

                // SetParent to make us a child/sibling
                SetParent(_hwnd, parentHwnd);

                // Position behind DefView or bottom
                if (zOrderHwnd != IntPtr.Zero)
                {
                    _zOrderReference = zOrderHwnd;
                    _isLayeredMode = true;
                    SetWindowPos(_hwnd, zOrderHwnd, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
                }
                else
                {
                    _zOrderReference = new IntPtr(1); // HWND_BOTTOM
                    _isLayeredMode = false;
                    var HWND_BOTTOM = new IntPtr(1);
                    SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
                }

                _logger?.LogInformation("Window parented to desktop: parent={Parent}, zOrder={ZOrder}, style=WS_EX_LAYERED", parentHwnd, zOrderHwnd);
                Console.WriteLine("READY");
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "PARENT command failed");
                Console.WriteLine($"ERROR:PARENT failed: {ex.Message}");
                Console.Out.Flush();
            }
        }
        else
        {
            Console.WriteLine("ERROR:Invalid PARENT format");
            Console.Out.Flush();
        }
    }

    private static void RenderLoop()
    {
        _logger?.LogInformation("Render loop started");
        _renderLoopStart = DateTime.UtcNow;

        while (_running)
        {
            // Pace the loop by DWM's FrameLatencyWaitableObject (signaled when DWM is ready
            // for our next frame). 100 ms cap keeps us responsive during pauses; we'll only
            // ever wait this long if DWM is hung. Replaces any "guess at refresh rate"
            // pacing — DWM tells us exactly when to render.
            if (_frameLatencyWaitable != IntPtr.Zero)
            {
                WaitForSingleObject(_frameLatencyWaitable, 100);
            }

            // Log every second to verify loop is running
            var now = DateTime.UtcNow;
            if ((now - _lastLoopLogTime).TotalSeconds >= 1.0)
            {
                _lastLoopLogTime = now;
                bool composing = false;
                lock (_compositionLock)
                {
                    composing = (_compositionInitialized || _useNativeD2DComposition) && _isPlaying;
                }
                _logger?.LogInformation("[D2D-LOOP] Frame #{Count} | NativeD2D: {Native} | Composing: {Composing} | WindowShown: {Shown}",
                    _frameCount, _useNativeD2DComposition, composing, _windowShown);
            }

            try
            {
                // Process Windows messages (CRITICAL for window stability!)
                int msgCount = 0;
                while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE) && msgCount < 100)
                {
                    msgCount++;
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                // Check for pending PARENT command
                string? parentCmd = null;
                lock (_parentLock)
                {
                    parentCmd = _pendingParentCommand;
                    _pendingParentCommand = null;
                }
                if (parentCmd != null)
                {
                    ProcessParentCommand(parentCmd);
                }

                // Periodic IconZone icon-position check — runs every 5 s.
                // Compare live icon positions against the cached set; rebuild zones ONLY
                // if icons actually moved. Never touches window parenting or Z-order.
                if (_backgroundMode == BackgroundMode.IconZone && !_needsIconRefresh)
                {
                    long nowTick = Environment.TickCount64;
                    if (nowTick - _lastIconPositionCheckTick > 5000)
                    {
                        _lastIconPositionCheckTick = nowTick;
                        var fresh = GetFilteredIconPositions();
                        if (IconPositionsChanged(fresh))
                        {
                            _logger?.LogInformation("[IconRefresh] Icon positions changed — scheduling zone rebuild");
                            _needsIconRefresh = true;
                            _iconRefreshScheduledAt = Environment.TickCount64;
                        }
                    }
                }

                // Desktop-state poll — runs every DesktopPollIntervalMs (5 s).
                //
                // Two distinct failure modes are checked here:
                //
                //   1. CATASTROPHIC orphaning (parent or DefView destroyed).
                //      Cause: explorer restart, display reconfiguration, raised-desktop
                //      teardown. Recovery requires the host to re-run desktop discovery
                //      and re-issue the PARENT command, so we signal the host. A 30-s
                //      cooldown prevents spam while the host re-parents.
                //
                //   2. Z-ORDER DISTURBANCE (parent + DefView intact, but our window has
                //      been bumped above DefView).
                //      Cause: Snipping Tool, fullscreen apps, other windows that mess
                //      with desktop child z-order. Symptom: desktop icons appear to
                //      vanish because our wallpaper now paints on top of DefView.
                //      Recovery is local — re-issue SetWindowPos with the same z-order
                //      reference. The call is idempotent (no-op if already correct),
                //      which is why we can poll without causing flicker on unrelated
                //      events (this was the regression from commit d775f9e).
                if (_hwnd != IntPtr.Zero && _zOrderReference != IntPtr.Zero)
                {
                    long nowTick = Environment.TickCount64;
                    if (nowTick - _lastDesktopPollTick > DesktopPollIntervalMs)
                    {
                        _lastDesktopPollTick = nowTick;

                        bool noAncestor = GetAncestor(_hwnd, GA_ROOT) == _hwnd;
                        // zOrderRefDead is only meaningful in layered mode where
                        // _zOrderReference is a real DefView HWND. In legacy mode the
                        // reference is HWND_BOTTOM (IntPtr(1), not a real window), so
                        // IsWindow always returns false there — exclude that case.
                        bool zOrderRefDead = _isLayeredMode && !IsWindow(_zOrderReference);
                        bool catastrophic  = noAncestor || zOrderRefDead;

                        if (catastrophic)
                        {
                            if (nowTick - _lastOrphanSignalTick > OrphanSignalCooldownMs)
                            {
                                _lastOrphanSignalTick = nowTick;
                                _logger?.LogWarning("[Reparent] Catastrophic state (noAncestor={NoAnc}, zOrderDead={ZDead}) — signaling host",
                                    noAncestor, zOrderRefDead);
                                Console.Error.WriteLine("SIGNAL:NEEDS_REPARENT");
                                Console.Error.Flush();
                            }
                        }
                        else if (_isLayeredMode)
                        {
                            // Layered mode: we expect DefView to be SOMEWHERE above us
                            // in z-order — not necessarily immediately above. In multi-
                            // monitor setups there is one wallpaper window per screen,
                            // all parented as siblings under Progman/WorkerW; only one
                            // can have DefView as its immediate GW_HWNDPREV. Requiring
                            // immediacy caused a ping-pong where each window kept
                            // restoring itself above DefView and displacing the others.
                            //
                            // Walk GW_HWNDPREV upward: if we hit DefView before running
                            // out of siblings, we are correctly below the icon layer.
                            // If we walk off the top without finding DefView, something
                            // (Snipping Tool, fullscreen app) actually bumped us above
                            // it — restore.
                            bool defViewAbove = false;
                            var cur = GetWindow(_hwnd, GW_HWNDPREV);
                            while (cur != IntPtr.Zero)
                            {
                                if (cur == _zOrderReference)
                                {
                                    defViewAbove = true;
                                    break;
                                }
                                cur = GetWindow(cur, GW_HWNDPREV);
                            }

                            if (!defViewAbove)
                            {
                                _logger?.LogInformation("[ZOrder] Layered z-order disturbed — restoring behind DefView");
                                SetWindowPos(_hwnd, _zOrderReference, 0, 0, 0, 0,
                                    SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
                            }
                        }
                        else
                        {
                            // Legacy WorkerW mode: we expect to be the last child of
                            // our parent (HWND_BOTTOM). If not, push back to bottom.
                            var parent = GetAncestor(_hwnd, GA_ROOT);
                            if (parent != IntPtr.Zero && parent != _hwnd
                                && GetWindow(parent, GW_CHILD) != IntPtr.Zero)
                            {
                                var lastChild = GetWindow(GetWindow(parent, GW_CHILD), GW_HWNDLAST);
                                if (lastChild != _hwnd)
                                {
                                    _logger?.LogInformation("[ZOrder] Legacy z-order disturbed — restoring HWND_BOTTOM");
                                    SetWindowPos(_hwnd, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0,
                                        SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
                                }
                            }
                        }
                    }
                }

                // Re-detect desktop icons and rebuild zones after a desktop refresh.
                // Debounce: wait for the desktop to finish settling before reading icon positions.
                if (_needsIconRefresh && _backgroundConfig != null && _d2dContext != null
                    && Environment.TickCount64 - _iconRefreshScheduledAt >= IconRefreshDebounceMs)
                {
                    _needsIconRefresh = false;
                    _logger?.LogInformation("[IconRefresh] Re-detecting icons after desktop refresh");
                    InitializeBackground(_backgroundConfig);
                }

                if (_d2dContext != null && _swapChain != null)
                {
                    // Stage 1: Per-frame pattern - bind DeviceContext to current back buffer
                    using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
                    var targetProps = new BitmapProperties1
                    {
                        PixelFormat = new Vortice.DCommon.PixelFormat(
                            Format.B8G8R8A8_UNorm,
                            Vortice.DCommon.AlphaMode.Ignore),
                        DpiX = 96.0f,
                        DpiY = 96.0f,
                        BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw
                    };

                    using var targetBitmap = _d2dContext.CreateBitmapFromDxgiSurface(backBuffer, targetProps);
                    _d2dContext.Target = targetBitmap;
                    _d2dContext.BeginDraw();

                    // TEST MODE: Simple color toggle to verify swap chain works
                    if (_testModeEnabled)
                    {
                        var timeSinceToggle = (DateTime.UtcNow - _lastColorToggle).TotalSeconds;
                        bool isRed = ((int)(timeSinceToggle / 2.0)) % 2 == 0;
                        var testColor = isRed ? new Color4(1, 0, 0, 1) : new Color4(0, 0, 1, 1);
                        _d2dContext.Clear(testColor);

                        if (_frameCount % 60 == 0)
                        {
                            _logger?.LogInformation("[TEST MODE] Frame #{Frame} | Color: {Color} | TimeSinceToggle: {Time:F1}s",
                                _frameCount, isRed ? "RED" : "BLUE", timeSinceToggle);
                        }

                        // Show window if not shown
                        if (!_windowShown)
                        {
                            _windowShown = true;
                            _d2dContext.EndDraw();
                            _d2dContext.Target = null;
                            _swapChain.Present(1, PresentFlags.None);

                            if (_zOrderReference != IntPtr.Zero)
                                SetWindowPos(_hwnd, _zOrderReference, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                            else
                                ShowWindow(_hwnd, 5);
                            UpdateWindow(_hwnd);
                            _logger?.LogInformation("[TEST MODE] Window shown");
                            _frameCount++;
                            continue;
                        }

                        _d2dContext.EndDraw();
                        _d2dContext.Target = null;
                        _swapChain.Present(1, PresentFlags.None); // VSync paces the loop

                        _frameCount++;
                        continue; // Skip normal rendering
                    }

                    // Check if composition is initialized and playing
                    bool shouldComposeNative = false;
                    bool shouldComposeFallback = false;
                    bool compInit = false;
                    bool isPlay = false;
                    lock (_compositionLock)
                    {
                        compInit = _compositionInitialized;
                        isPlay = _isPlaying;
                        shouldComposeNative = (_useNativeD2DComposition || _useNativeD2DVideo) && isPlay;
                        shouldComposeFallback = compInit && isPlay && !_useNativeD2DComposition && !_useNativeD2DVideo;
                    }

                    if (_frameCount % 60 == 0)
                    {
                        _logger?.LogInformation("[RENDER-LOOP] Frame #{Frame} | NativeD2D: {Native} | FallbackComp: {Fallback} | Playing: {Play}",
                            _frameCount, shouldComposeNative, shouldComposeFallback, isPlay);
                    }

                    // Stage 4: Pure D2D render path for GIF animations
                    if (shouldComposeNative && _d2dGifFrames != null)
                    {
                        try
                        {
                            var elapsedMs = (long)(DateTime.UtcNow - _renderLoopStart).TotalMilliseconds;

                            // Draw background (Stage 3)
                            DrawBackground();

                            // Update animation position (Stage 4)
                            UpdateAnimationPosition(elapsedMs);

                            // Pattern/multi-image/grading-aware draw (falls back to single DrawBitmap when none active)
                            DrawAnimationLayer(elapsedMs);

                            if (_frameCount % 60 == 0)
                            {
                                _logger?.LogInformation("[D2D-NATIVE] Frame #{Frame} | Sources: {Src} | Pattern: {Pat} | Grading: {Grad} | Pos: ({X:F0},{Y:F0})",
                                    _frameCount, _d2dFramesPerSource.Count,
                                    _animationConfig?.Pattern != null,
                                    _animationConfig?.ColorGrading?.Mode != ColorGradingMode.None,
                                    _animX, _animY);
                            }

                            // Show window on first frame
                            if (!_windowShown)
                            {
                                _windowShown = true;
                                _d2dContext.EndDraw();
                                _d2dContext.Target = null;
                                _swapChain.Present(1, PresentFlags.None);

                                if (_zOrderReference != IntPtr.Zero)
                                    SetWindowPos(_hwnd, _zOrderReference, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                                else
                                    ShowWindow(_hwnd, 5);
                                UpdateWindow(_hwnd);
                                _logger?.LogInformation("Window shown after first native D2D frame");
                                _frameCount++;
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Native D2D composition error");
                            // Fall through to color fill below
                        }
                    }
                    // Stage 6: Pure D2D render path for video (LibVLC → CopyFromMemory → DrawBitmap)
                    else if (shouldComposeNative && _useNativeD2DVideo && _currentVideoD2DBitmap != null)
                    {
                        try
                        {
                            var elapsedMs = (long)(DateTime.UtcNow - _renderLoopStart).TotalMilliseconds;

                            DrawBackground();
                            UpdateAnimationPosition(elapsedMs);

                            // Upload new frame from LibVLC buffer → GPU (~0.5ms for 1080p)
                            if (_videoFrameReady)
                            {
                                _videoFrameReady = false;
                                var readBuf = _videoReadBuffer;
                                if (readBuf != IntPtr.Zero)
                                {
                                    _currentVideoD2DBitmap.CopyFromMemory(readBuf, (uint)(_videoNativeWidth * 4));
                                }
                            }

                            // Video uses single-cell path with optional grading; pattern is unsupported for video.
                            DrawAnimationLayer(elapsedMs, videoBitmapOverride: _currentVideoD2DBitmap);

                            if (_frameCount % 60 == 0)
                            {
                                _logger?.LogInformation("[D2D-VIDEO] Frame #{Frame} | Pos: ({X:F0},{Y:F0}) | Size: {W}x{H} | Grading: {Grad}",
                                    _frameCount, _animX, _animY, _animWidth, _animHeight,
                                    _animationConfig?.ColorGrading?.Mode != ColorGradingMode.None);
                            }

                            if (!_windowShown)
                            {
                                _windowShown = true;
                                _d2dContext.EndDraw();
                                _d2dContext.Target = null;
                                _swapChain.Present(1, PresentFlags.None);

                                if (_zOrderReference != IntPtr.Zero)
                                    SetWindowPos(_hwnd, _zOrderReference, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                                else
                                    ShowWindow(_hwnd, 5);
                                UpdateWindow(_hwnd);
                                _logger?.LogInformation("Window shown after first native D2D video frame");
                                _frameCount++;
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Native D2D video render error");
                        }
                    }
                    // Video fallback path: uses CompositionRenderer + GDI+ ConvertBitmapToD2D
                    else if (shouldComposeFallback && _compositionRenderer != null && _canvasManager != null)
                    {
                        try
                        {
                            var elapsedMs = (long)(DateTime.UtcNow - _renderLoopStart).TotalMilliseconds;
                            var currentTimestampMs = _startTimestampMs + elapsedMs;

                            if (_frameCount % 60 == 0)
                            {
                                _logger?.LogInformation("[TIMESTAMP] Frame #{Frame} | Elapsed: {Elapsed}ms | CurrentTimestamp: {Timestamp}ms | PPS: {PPS}",
                                    _frameCount, elapsedMs, currentTimestampMs, _pixelsPerSecond);
                            }

                            _compositionRenderer.UpdateAnimationPosition(currentTimestampMs, _pixelsPerSecond);

                            var screen = _canvasManager.ScreenMappings[0];
                            using var composedFrame = _compositionRenderer.ComposeForScreen(screen);

                            using var d2dBitmap = ConvertBitmapToD2D(composedFrame);

                            if (d2dBitmap != null)
                            {
                                var destRect = new System.Drawing.RectangleF(0, 0, _width, _height);
                                _d2dContext.DrawBitmap(
                                    d2dBitmap,
                                    destRect,
                                    1.0f,
                                    BitmapInterpolationMode.Linear,
                                    null);

                                if (!_windowShown)
                                {
                                    _windowShown = true;
                                    _d2dContext.EndDraw();
                                    _d2dContext.Target = null;
                                    _swapChain.Present(1, PresentFlags.None);

                                    if (_zOrderReference != IntPtr.Zero)
                                        SetWindowPos(_hwnd, _zOrderReference, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                                    else
                                        ShowWindow(_hwnd, 5);
                                    UpdateWindow(_hwnd);
                                    _logger?.LogInformation("Window shown after first fallback frame");
                                    _frameCount++;
                                    continue;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Composition fallback error");
                        }
                    }
                    else
                    {
                        // Fallback to solid color
                        Color4 color;
                        lock (_colorLock)
                        {
                            color = _currentColor;
                        }
                        _d2dContext.Clear(color);
                    }

                    RecordMovementTrailSample();
                    DrawDebugOverlay();

                    _d2dContext.EndDraw();
                    _d2dContext.Target = null;
                    _swapChain.Present(1, PresentFlags.None);

                    if (_frameCount % 60 == 0 && _frameCount > 0)
                    {
                        _logger?.LogInformation("[D2D-PRESENT] Frame #{Frame} presented | NativeD2D: {Native}",
                            _frameCount, shouldComposeNative);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Render loop error");
                // After a render error we won't have hit Present(1) — sleep briefly so a
                // persistent failure does not spin the CPU at 100%.
                Thread.Sleep(16);
            }

            // Pacing: when rendering succeeds, the loop is paced by Present(1) (VSync).
            // No explicit sleep here — locks to the swap chain's monitor refresh rate
            // (e.g. 60 Hz on a normal monitor, 165 Hz on a high-refresh monitor).
            _frameCount++;
        }

        _logger?.LogInformation("Render loop stopped");
    }

    // ================================
    // Stage 2: GIF Frame Extraction
    // ================================

    /// <summary>
    /// Extract all GIF frames using Magick.NET and upload to GPU as ID2D1Bitmap[].
    /// One-time cost during load - frames persist in GPU memory for zero-copy rendering.
    /// </summary>
    private static void ExtractGifFramesToD2D(string filePath)
    {
        if (_d2dContext == null)
            throw new InvalidOperationException("D2D context not initialized");

        var startTime = DateTime.UtcNow;

        using var collection = new MagickImageCollection(filePath);

        _logger?.LogInformation("[GIF-D2D] Extracting {Count} frames from {File} using Magick.NET",
            collection.Count, Path.GetFileName(filePath));

        // Coalesce applies GIF disposal methods so each frame becomes a full image
        collection.Coalesce();

        int frameCount = collection.Count;
        var frames = new ID2D1Bitmap[frameCount];
        var delays = new List<int>(frameCount);
        int zeroDelayCount = 0;

        for (int i = 0; i < frameCount; i++)
        {
            var frame = collection[i];

            // Extract delay (AnimationDelay is in 1/100th of a second)
            int delayMs = (int)(frame.AnimationDelay * 10);
            if (delayMs <= 0)
            {
                delayMs = 10; // Browser standard: 0-delay = 10ms
                zeroDelayCount++;
            }
            if (delayMs > 1000)
            {
                _logger?.LogWarning("[GIF-D2D] Frame delay {Original}ms capped to 1000ms", delayMs);
                delayMs = 1000;
            }
            delays.Add(delayMs);

            // Convert Magick frame to GDI+ Bitmap, then upload to D2D
            using var gdiBitmap = frame.ToBitmap();
            frames[i] = UploadBitmapToD2D(gdiBitmap);
        }

        // Store dimensions from first frame
        _contentNativeWidth = (int)collection[0].Width;
        _contentNativeHeight = (int)collection[0].Height;

        _d2dGifFrames = frames;
        _d2dGifDelays = delays;
        _d2dGifTotalDurationMs = delays.Sum();

        if (zeroDelayCount > 0)
        {
            _logger?.LogInformation("[GIF-D2D] {Count}/{Total} frames had 0-delay, set to 10ms",
                zeroDelayCount, frameCount);
        }

        var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
        long estimatedGpuMB = (long)frameCount * _contentNativeWidth * _contentNativeHeight * 4 / (1024 * 1024);

        _logger?.LogInformation("[GIF-D2D] Extraction complete in {ElapsedMs}ms: {Frames} frames, {Duration}ms total ({FPS:F1} FPS avg), ~{GpuMB} MB GPU",
            (int)elapsedMs, frameCount, _d2dGifTotalDurationMs,
            _d2dGifTotalDurationMs > 0 ? frameCount * 1000.0 / _d2dGifTotalDurationMs : 0,
            estimatedGpuMB);
        _logger?.LogInformation("[GIF-D2D] Frame delays: min={Min}ms, max={Max}ms, avg={Avg:F1}ms | Speed: {Speed}x",
            delays.Min(), delays.Max(), delays.Average(), _gifSpeedMultiplier);
    }

    /// <summary>
    /// Upload a GDI+ Bitmap to GPU as an ID2D1Bitmap (one-time, used during init).
    /// </summary>
    private static ID2D1Bitmap UploadBitmapToD2D(Bitmap gdiBitmap)
    {
        if (_d2dContext == null)
            throw new InvalidOperationException("D2D context not initialized");

        var bitmapData = gdiBitmap.LockBits(
            new Rectangle(0, 0, gdiBitmap.Width, gdiBitmap.Height),
            ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

        try
        {
            var bitmapProps = new BitmapProperties
            {
                PixelFormat = new Vortice.DCommon.PixelFormat(
                    Format.B8G8R8A8_UNorm,
                    Vortice.DCommon.AlphaMode.Premultiplied),
                DpiX = 96.0f,
                DpiY = 96.0f
            };

            return _d2dContext.CreateBitmap(
                new Vortice.Mathematics.SizeI(gdiBitmap.Width, gdiBitmap.Height),
                bitmapData.Scan0,
                (uint)bitmapData.Stride,
                bitmapProps);
        }
        finally
        {
            gdiBitmap.UnlockBits(bitmapData);
        }
    }

    /// <summary>
    /// Get current GIF frame index based on elapsed time with SpeedMultiplier applied.
    /// Fixes ISSUE-004 (GIF too slow) by scaling elapsed time.
    /// </summary>
    private static int GetCurrentGifFrameIndex(long elapsedMs)
    {
        if (_d2dGifDelays == null || _d2dGifTotalDurationMs <= 0)
            return 0;

        // Apply SpeedMultiplier to elapsed time (fixes ISSUE-004)
        long effectiveMs = (long)(elapsedMs * _gifSpeedMultiplier);
        long loopedMs = effectiveMs % _d2dGifTotalDurationMs;

        int frameIndex = _d2dGifDelays.Count - 1; // Default to last frame
        long accumulated = 0;
        for (int i = 0; i < _d2dGifDelays.Count; i++)
        {
            accumulated += _d2dGifDelays[i];
            if (accumulated > loopedMs)
            {
                frameIndex = i;
                break;
            }
        }

        return frameIndex;
    }

    // ================================
    // Stage 6: Native D2D Video (LibVLC)
    // ================================

    /// <summary>
    /// Initialize LibVLC for direct video decoding into raw RGBA buffers.
    /// Sets up double-buffering and creates a persistent ID2D1Bitmap for CopyFromMemory updates.
    /// </summary>
    private static void InitializeNativeVideo(string filePath)
    {
        _logger?.LogInformation("[VIDEO-INIT] Initializing LibVLC for native D2D video: {Path}", filePath);

        // Initialize LibVLC
        LibVLCSharp.Shared.Core.Initialize();
        _libVLC = new LibVLC(enableDebugLogs: false,
            "--no-video-title-show",
            "--no-audio",
            "--file-caching=300",
            "--network-caching=300",
            "--avcodec-hw=any");

        _vlcPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

        // Parse media to detect video dimensions
        using var media = new Media(_libVLC, filePath, FromType.FromPath);
        media.Parse(MediaParseOptions.ParseLocal).Wait();

        var videoTracks = media.Tracks.Where(t => t.TrackType == TrackType.Video).ToArray();
        if (videoTracks.Length > 0)
        {
            _videoNativeWidth = (int)videoTracks[0].Data.Video.Width;
            _videoNativeHeight = (int)videoTracks[0].Data.Video.Height;
            _logger?.LogInformation("[VIDEO-INIT] Detected video dimensions: {W}x{H}", _videoNativeWidth, _videoNativeHeight);
        }
        else
        {
            _videoNativeWidth = 1920;
            _videoNativeHeight = 1080;
            _logger?.LogWarning("[VIDEO-INIT] Could not detect video dimensions, using default 1920x1080");
        }

        // Allocate double buffers (lock-free swap)
        int bufferSize = _videoNativeWidth * _videoNativeHeight * 4;
        _videoBufferA = Marshal.AllocHGlobal(bufferSize);
        _videoBufferB = Marshal.AllocHGlobal(bufferSize);
        _videoWriteBuffer = _videoBufferA;
        _videoReadBuffer = _videoBufferB;
        _videoFrameReady = false;

        // Clear buffers to black
        unsafe
        {
            new Span<byte>((void*)_videoBufferA, bufferSize).Clear();
            new Span<byte>((void*)_videoBufferB, bufferSize).Clear();
        }

        // Set up LibVLC memory callbacks
        uint pitch = (uint)(_videoNativeWidth * 4);
        _vlcPlayer.SetVideoFormat("RV32", (uint)_videoNativeWidth, (uint)_videoNativeHeight, pitch);
        _vlcPlayer.SetVideoCallbacks(VideoLockCb, null, VideoDisplayCb);

        // Create persistent ID2D1Bitmap for CopyFromMemory updates (zero per-frame allocation)
        var bitmapProps = new BitmapProperties(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96f, 96f);
        _currentVideoD2DBitmap = _d2dContext!.CreateBitmap(
            new SizeI(_videoNativeWidth, _videoNativeHeight),
            IntPtr.Zero, 0,
            bitmapProps);

        // Wire looping via EndReached (proven pattern from VideoWallpaperRenderer)
        _vlcPlayer.EndReached += (s, e) =>
        {
            if (_animationConfig?.Loop == true)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Thread.Sleep(50);
                        _vlcPlayer?.Stop();
                        Thread.Sleep(50);
                        using var loopMedia = new Media(_libVLC!, filePath, FromType.FromPath);
                        if (_vlcPlayer != null)
                        {
                            _vlcPlayer.Media = loopMedia;
                            _vlcPlayer.Play();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "[VIDEO-LOOP] Error restarting video");
                    }
                });
            }
        };

        // Start playback immediately (LibVLC decodes frames in the background)
        var playMedia = new Media(_libVLC, filePath, FromType.FromPath);
        _vlcPlayer.Media = playMedia;
        _vlcPlayer.Play();

        _logger?.LogInformation("[VIDEO-INIT] LibVLC native video initialized, playback started");
    }

    /// <summary>
    /// LibVLC lock callback - provides buffer pointer for frame decode.
    /// </summary>
    private static IntPtr VideoLockCb(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _videoWriteBuffer);
        return IntPtr.Zero;
    }

    /// <summary>
    /// LibVLC display callback - swaps double buffers (lock-free).
    /// </summary>
    private static void VideoDisplayCb(IntPtr opaque, IntPtr picture)
    {
        // Swap: completed write buffer becomes read buffer
        var oldRead = Interlocked.Exchange(ref _videoReadBuffer, _videoWriteBuffer);
        _videoWriteBuffer = oldRead;
        _videoFrameReady = true;
    }

    // ================================
    // Stage 3: D2D Background Rendering
    // ================================

    /// <summary>
    /// Initialize background layer from config. Loads image backgrounds as GPU bitmaps.
    /// </summary>
    private static void InitializeBackground(BackgroundLayerConfig config)
    {
        _backgroundMode = config.Mode;

        // Reset corridor constraint and dispose old ThreeZone brushes
        _hasCorridorConstraint = false;
        _corridorTopPx = 0;
        _corridorHeightPx = 0;
        _topZoneBrush?.Dispose();    _topZoneBrush    = null;
        _bottomZoneBrush?.Dispose(); _bottomZoneBrush = null;
        _corridorBrush?.Dispose();   _corridorBrush   = null;

        // Reset IconZone brushes and path
        foreach (var (_, _, _, _, _, brush) in _iconZoneBands) brush?.Dispose();
        _iconZoneBands.Clear();
        _animPath.Clear();
        _animPathTotalLength = 0f;
        _traverseCount = 0;
        _pendingNewPath = null;

        // Dispose previous background image
        _backgroundImageBitmap?.Dispose();
        _backgroundImageBitmap = null;

        switch (config.Mode)
        {
            case BackgroundMode.SolidColor:
                _backgroundColor = ParseHexColor(config.ColorHex);
                _logger?.LogInformation("[BG-D2D] Solid color: {Color}", config.ColorHex);
                break;

            case BackgroundMode.StretchedImage:
            case BackgroundMode.TiledImage:
                if (!string.IsNullOrEmpty(config.ImagePath) && File.Exists(config.ImagePath))
                {
                    using var gdiBitmap = (Bitmap)Image.FromFile(config.ImagePath);
                    _backgroundImageBitmap = UploadBitmapToD2D(gdiBitmap);
                    _logger?.LogInformation("[BG-D2D] Image loaded: {Path} ({W}x{H})", config.ImagePath, gdiBitmap.Width, gdiBitmap.Height);
                }
                else
                {
                    _logger?.LogWarning("[BG-D2D] Image not found, falling back to solid color: {Path}", config.ImagePath);
                    _backgroundMode = BackgroundMode.SolidColor;
                    _backgroundColor = ParseHexColor(config.ColorHex);
                }
                break;

            case BackgroundMode.ThreeZone:
                _topZoneBrush    = _d2dContext!.CreateSolidColorBrush(ParseHexColor(config.TopZoneColorHex));
                _bottomZoneBrush = _d2dContext.CreateSolidColorBrush(ParseHexColor(config.BottomZoneColorHex));
                _corridorBrush   = _d2dContext.CreateSolidColorBrush(ParseHexColor(config.CorridorColorHex));
                _corridorTopPx    = config.CorridorTopPx;
                _corridorHeightPx = config.CorridorHeightPx;
                _hasCorridorConstraint = true;
                _logger?.LogInformation("[BG-D2D] ThreeZone: corridorTop={T}px height={H}px",
                    _corridorTopPx, _corridorHeightPx);
                break;

            case BackgroundMode.IconZone:
            {
                _iconCorridorBgColor = ParseHexColor(config.IconCorridorColorHex);

                var (cellW, cellH) = GetIconCellSize();
                int iconImageW = Math.Max(0, GetSystemMetrics(SM_CXICON));
                int iconImageH = Math.Max(0, GetSystemMetrics(SM_CYICON));
                // LVM_GETITEMPOSITION returns every desktop icon in virtual-screen coords.
                // Each player keeps only icons whose cell rectangle intersects its own monitor rect
                // [_monitorOffsetX, _monitorOffsetX + _width) × [_monitorOffsetY, _monitorOffsetY + _height),
                // then shifts to local (monitor-relative) space.
                var iconPositions = GetFilteredIconPositions();
                _logger?.LogInformation("[IconZone] Monitor offset={Offset}px kept {Kept} icons, cell={W}x{H}px",
                    _monitorOffsetX, iconPositions.Count, cellW, cellH);

                _detectedIcons.Clear();
                _detectedIcons.AddRange(iconPositions);
                _detectedCellW = cellW;
                _detectedCellH = cellH;

                // Padding around each icon zone rect. For pattern mode (_maskZones) the zones
                // cover many visible cells, so double the padding for a noticeably larger
                // exclusion area. For normal single-animation IconZone, half the anim height
                // keeps the animation bitmap clear of the icon.
                int pathPaddingPx = _maskZones ? _animHeight * 3 / 2 : _animHeight / 2;
                if (_movementConfig?.Type == MovementType.SineWave)
                    pathPaddingPx += (int)Math.Ceiling(_movementConfig.WaveAmplitudePixels);
                if (_movementConfig?.Type == MovementType.RandomWalk && _movementConfig.WaveAmplitudePixels > 0)
                    pathPaddingPx += (int)Math.Ceiling(_movementConfig.WaveAmplitudePixels);
                // Always compute local zones for background rendering
                var layout = WaBiBaBuSy.WallpaperEngine.Desktop.ZonePlanner.Compute(
                    iconPositions, cellW, cellH, _width, _height,
                    config.IconZonePaletteHexes, config.IconCorridorColorHex,
                    paddingPx: pathPaddingPx,
                    visualPaddingPx: Math.Max(20, cellW / 3),
                    iconImageW: iconImageW,
                    iconImageH: iconImageH);

                foreach (var band in layout.Bands)
                {
                    var brush = _d2dContext!.CreateSolidColorBrush(ParseHexColor(band.ColorHex));
                    _iconZoneBands.Add((band.Y, band.Height, band.X, band.Width, band.IsFree, brush));
                }

                // Path: use centrally-precomputed path (sequential mode) if provided,
                // otherwise use locally-computed path and shift to virtual-canvas space.
                var precomputed = _animationConfig?.PrecomputedPath;
                if (precomputed?.Count > 0)
                {
                    _logger?.LogInformation("[IconZone] Using precomputed global path ({Pts} waypoints)", precomputed.Count);
                    foreach (var wp in precomputed)
                        _animPath.Add((wp.X, wp.Y));
                }
                else
                {
                    foreach (var wp in layout.Path)
                        _animPath.Add((wp.X, wp.Y));

                    // Shift local path to virtual-canvas space for sequential mode
                    if (_monitorOffsetX != 0)
                    {
                        _logger?.LogInformation("[IconZone] Shifting path by MonitorOffsetX={Offset}px for sequential mode", _monitorOffsetX);
                        for (int i = 0; i < _animPath.Count; i++)
                            _animPath[i] = (_animPath[i].X + _monitorOffsetX, _animPath[i].Y);
                    }
                }

                _animPathTotalLength = ComputePathLength(_animPath);
                _logger?.LogInformation("[BG-D2D] IconZone: {Bands} bands, {Pts} path points, totalLen={Len:F0}px",
                    _iconZoneBands.Count, _animPath.Count, _animPathTotalLength);
                break;
            }
        }
    }

    /// <summary>
    /// Draw background using pure D2D calls (no GDI+).
    /// </summary>
    private static void DrawBackground()
    {
        if (_d2dContext == null) return;

        switch (_backgroundMode)
        {
            case BackgroundMode.SolidColor:
                _d2dContext.Clear(_backgroundColor);
                break;

            case BackgroundMode.StretchedImage:
                if (_backgroundImageBitmap != null)
                {
                    _d2dContext.Clear(new Color4(0, 0, 0, 1)); // Black behind image
                    var fullScreen = new System.Drawing.RectangleF(0, 0, _width, _height);
                    _d2dContext.DrawBitmap(_backgroundImageBitmap, fullScreen, 1.0f, BitmapInterpolationMode.Linear, null);
                }
                else
                {
                    _d2dContext.Clear(_backgroundColor);
                }
                break;

            case BackgroundMode.TiledImage:
                if (_backgroundImageBitmap != null)
                {
                    _d2dContext.Clear(new Color4(0, 0, 0, 1));
                    var imgSize = _backgroundImageBitmap.Size;
                    int tileW = (int)imgSize.Width;
                    int tileH = (int)imgSize.Height;
                    if (tileW > 0 && tileH > 0)
                    {
                        for (int ty = 0; ty < _height; ty += tileH)
                        {
                            for (int tx = 0; tx < _width; tx += tileW)
                            {
                                var tileRect = new System.Drawing.RectangleF(tx, ty, tileW, tileH);
                                _d2dContext.DrawBitmap(_backgroundImageBitmap, tileRect, 1.0f, BitmapInterpolationMode.Linear, null);
                            }
                        }
                    }
                }
                else
                {
                    _d2dContext.Clear(_backgroundColor);
                }
                break;

            case BackgroundMode.ThreeZone:
                _d2dContext.Clear(new Color4(0, 0, 0, 1));
                // Top zone
                if (_topZoneBrush != null && _corridorTopPx > 0)
                    _d2dContext.FillRectangle(new System.Drawing.RectangleF(0, 0, _width, _corridorTopPx), _topZoneBrush);
                // Corridor
                if (_corridorBrush != null)
                    _d2dContext.FillRectangle(new System.Drawing.RectangleF(0, _corridorTopPx, _width, _corridorHeightPx), _corridorBrush);
                // Bottom zone
                int bottomY = _corridorTopPx + _corridorHeightPx;
                if (_bottomZoneBrush != null && bottomY < _height)
                    _d2dContext.FillRectangle(new System.Drawing.RectangleF(0, bottomY, _width, _height - bottomY), _bottomZoneBrush);
                break;

            case BackgroundMode.IconZone:
                _d2dContext.Clear(_iconCorridorBgColor);
                // Skip the colored zone palette when zones are being used as a mask (pattern mode)
                // OR when the user has not opted in to the palette (debug visualization).
                if (!_maskZones && _useZonePalette)
                {
                    foreach (var (zY, zH, zX, zW, isFree, zBrush) in _iconZoneBands)
                        if (!isFree && zBrush != null)
                        {
                            float drawW = zW < 0 ? _width : zW;
                            _d2dContext.FillRoundedRectangle(
                                new Vortice.Direct2D1.RoundedRectangle
                                {
                                    Rect = new System.Drawing.RectangleF(zX, zY, drawW, zH),
                                    RadiusX = 8f, RadiusY = 8f
                                }, zBrush);
                        }
                }
                break;
        }
    }

    private static Color4 ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return new Color4(r / 255f, g / 255f, b / 255f, 1f);
        }
        return new Color4(0, 0, 0, 1); // Default black
    }

    /// <summary>
    /// Record the animation's current center point into the trail ring buffer for later rendering.
    /// Called every frame regardless of overlay visibility, so the trail is ready the instant it
    /// is toggled on.
    /// </summary>
    private static void RecordMovementTrailSample()
    {
        if (_animWidth <= 0 || _animHeight <= 0) return;
        float cx = _animX + _animWidth * 0.5f;
        float cy = _animY + _animHeight * 0.5f;
        _movementTrail[_movementTrailHead] = (cx, cy);
        _movementTrailHead = (_movementTrailHead + 1) % MOVEMENT_TRAIL_CAPACITY;
        if (_movementTrailCount < MOVEMENT_TRAIL_CAPACITY) _movementTrailCount++;
    }

    /// <summary>
    /// Draw the debug overlay on top of the current frame: A* path polyline and waypoints,
    /// actual-movement trail, icon zone rects, animation outline, and status LED.
    /// Called once per frame before EndDraw; fully inert when disabled.
    /// Toggle via F11 hotkey or <see cref="PlayerCommandToggleDebugOverlay"/>.
    /// </summary>
    private static void DrawDebugOverlay()
    {
        if (!_debugOverlay.Enabled) return;
        if (_d2dContext == null) return;

        // Lazy-create brushes the first time the overlay is used.
        _debugPathBrush     ??= _d2dContext.CreateSolidColorBrush(new Color4(1f, 0.1f, 0.9f, 1f)); // magenta
        _debugWaypointBrush ??= _d2dContext.CreateSolidColorBrush(new Color4(1f, 1f, 0.1f, 1f));   // yellow
        _debugAnimRectBrush ??= _d2dContext.CreateSolidColorBrush(new Color4(0f, 1f, 1f, 1f));     // cyan
        _debugInfoBrush     ??= _d2dContext.CreateSolidColorBrush(new Color4(0.1f, 1f, 0.2f, 1f)); // green
        _debugIconRectBrush ??= _d2dContext.CreateSolidColorBrush(new Color4(1f, 0.2f, 0.2f, 1f)); // red
        _debugTrailBrush    ??= _d2dContext.CreateSolidColorBrush(new Color4(1f, 0.6f, 0f, 1f));   // orange
        _debugZoneBandBrush ??= _d2dContext.CreateSolidColorBrush(new Color4(0.2f, 1f, 0.6f, 1f)); // teal-green

        // 1. Icon zone rects — overlays the planner's "occupied" rectangles so we can see what
        //    the A* path is actually avoiding. Also draws a dot at each raw icon position.
        if (_debugOverlay.ShowIconRects && _debugIconRectBrush != null)
        {
            foreach (var (zY, zH, zX, zW, isFree, _) in _iconZoneBands)
            {
                if (isFree) continue;
                float drawW = zW < 0 ? _width : zW;
                _d2dContext.DrawRoundedRectangle(
                    new Vortice.Direct2D1.RoundedRectangle
                    {
                        Rect = new System.Drawing.RectangleF(zX, zY, drawW, zH),
                        RadiusX = 8f, RadiusY = 8f
                    }, _debugIconRectBrush, strokeWidth: 1.5f);
            }
            foreach (var (ix, iy) in _detectedIcons)
            {
                // Snap to cell center — same grid-cell logic as ZonePlanner zone rects.
                int cellCx = (ix / _detectedCellW) * _detectedCellW + _detectedCellW / 2;
                int cellCy = (iy / _detectedCellH) * _detectedCellH + _detectedCellH / 2;
                var ellipse = new Vortice.Direct2D1.Ellipse(new Vector2(cellCx, cellCy), 3f, 3f);
                _d2dContext.FillEllipse(ellipse, _debugIconRectBrush);
            }
        }

        // 2. Free zone band outlines — highlights the corridor spaces A* can navigate through.
        if (_debugOverlay.ShowZoneBandOutlines && _debugZoneBandBrush != null)
        {
            foreach (var (zY, zH, zX, zW, isFree, _) in _iconZoneBands)
            {
                if (!isFree) continue;
                float drawW = zW < 0 ? _width : zW;
                _d2dContext.DrawRectangle(
                    new System.Drawing.RectangleF(zX, zY, drawW, zH),
                    _debugZoneBandBrush, strokeWidth: 1.5f);
            }
        }

        // 3. A* path polyline + waypoint markers.
        if (_debugOverlay.ShowPath && _debugPathBrush != null && _debugWaypointBrush != null && _animPath.Count >= 2)
        {
            for (int i = 0; i < _animPath.Count - 1; i++)
            {
                var a = _animPath[i];
                var b = _animPath[i + 1];
                var pa = new Vector2(a.X - _monitorOffsetX, a.Y);
                var pb = new Vector2(b.X - _monitorOffsetX, b.Y);
                _d2dContext.DrawLine(pa, pb, _debugPathBrush, strokeWidth: 2f);
            }
            foreach (var (wx, wy) in _animPath)
            {
                var ellipse = new Vortice.Direct2D1.Ellipse(
                    new Vector2(wx - _monitorOffsetX, wy), 4f, 4f);
                _d2dContext.FillEllipse(ellipse, _debugWaypointBrush);
            }
        }

        // 4. Actual movement trail — shows what the animation's center is doing, including
        //    SineWave offsets. If the trail wanders into red icon rects, movement is wrong.
        if (_debugOverlay.ShowPath && _debugTrailBrush != null && _movementTrailCount >= 2)
        {
            int first = _movementTrailCount < MOVEMENT_TRAIL_CAPACITY
                ? 0
                : _movementTrailHead;
            for (int i = 1; i < _movementTrailCount; i++)
            {
                int prev = (first + i - 1) % MOVEMENT_TRAIL_CAPACITY;
                int cur  = (first + i)     % MOVEMENT_TRAIL_CAPACITY;
                var a = _movementTrail[prev];
                var b = _movementTrail[cur];
                _d2dContext.DrawLine(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y),
                    _debugTrailBrush, strokeWidth: 1.5f);
            }
        }

        // 5. Current animation bitmap outline — shows actual rendered size vs. expected position.
        if (_debugAnimRectBrush != null && _animWidth > 0 && _animHeight > 0)
        {
            var rect = new System.Drawing.RectangleF(_animX, _animY, _animWidth, _animHeight);
            _d2dContext.DrawRectangle(rect, _debugAnimRectBrush, strokeWidth: 2f);
        }

        // 6. Status LED (top-left) — proves each player's debug overlay is running even on
        //    monitors that otherwise look empty. Deliberately simple; text panel is a future add.
        if (_debugOverlay.ShowInfoPanel && _debugInfoBrush != null)
        {
            var led = new System.Drawing.RectangleF(8, 8, 16, 16);
            _d2dContext.FillRectangle(led, _debugInfoBrush);
        }
    }

    // ================================
    // Stage 4: Animation Layout & Position
    // ================================

    /// <summary>
    /// Calculate animation dimensions based on FitMode (ported from AnimationLayerRenderer).
    /// </summary>
    private static void CalculateAnimationLayout(AnimationLayerConfig config)
    {
        int nativeWidth = _contentNativeWidth;
        int nativeHeight = _contentNativeHeight;

        if (nativeWidth <= 0 || nativeHeight <= 0)
        {
            _logger?.LogWarning("[LAYOUT] Invalid native dimensions ({W}x{H}), using screen size", nativeWidth, nativeHeight);
            _animWidth = _width;
            _animHeight = _height;
        }
        else
        {
            switch (config.FitMode)
            {
                case ContentFitMode.Center:
                    _animWidth = nativeWidth;
                    _animHeight = nativeHeight;
                    break;

                case ContentFitMode.Fit:
                    double fitScale = Math.Min(
                        (double)_width / nativeWidth,
                        (double)_height / nativeHeight);
                    _animWidth = (int)(nativeWidth * fitScale);
                    _animHeight = (int)(nativeHeight * fitScale);
                    break;

                case ContentFitMode.Fill:
                    double fillScale = Math.Max(
                        (double)_width / nativeWidth,
                        (double)_height / nativeHeight);
                    _animWidth = (int)(nativeWidth * fillScale);
                    _animHeight = (int)(nativeHeight * fillScale);
                    break;

                case ContentFitMode.Stretch:
                default:
                    _animWidth = _width;
                    _animHeight = _height;
                    break;
            }
        }

        _fitMode = config.FitMode;
        _centerInitialPosition = config.CenterInitialPosition;
        _rotateWithPath = config.RotateWithPath;

        // Calculate initial position
        if (_centerInitialPosition || _pixelsPerSecond == 0)
        {
            // Centered on screen
            _animX = (_width - _animWidth) / 2f;
        }
        else
        {
            // Off-screen left for scrolling animations
            _animX = -_animWidth;
        }

        // Vertical alignment
        if (_hasCorridorConstraint && _corridorHeightPx > 0)
        {
            // Center within the corridor
            _animY = _corridorTopPx + (_corridorHeightPx - _animHeight) / 2f;
            _animY = MathF.Max(_corridorTopPx, _animY);
        }
        else
        {
            switch (config.VerticalAlign)
            {
                case VerticalAlignment.Top:
                    _animY = 0;
                    break;
                case VerticalAlignment.Bottom:
                    _animY = _height - _animHeight;
                    break;
                case VerticalAlignment.Center:
                default:
                    _animY = (_height - _animHeight) / 2f;
                    break;
            }
        }

        _logger?.LogInformation("[LAYOUT] Animation: {W}x{H} at ({X:F0},{Y:F0}) | FitMode: {Fit} | Native: {NW}x{NH} | Screen: {SW}x{SH}",
            _animWidth, _animHeight, _animX, _animY, config.FitMode, nativeWidth, nativeHeight, _width, _height);
    }

    /// <summary>
    /// Update animation position based on elapsed time.
    /// Uses MovementCalculator when available, falls back to legacy linear scroll.
    /// </summary>
    private static void UpdateAnimationPosition(long elapsedMs)
    {
        // Apply any pending path update from host before any position logic.
        var pendingPath = Interlocked.Exchange(ref _pendingNewPath, null);
        if (pendingPath != null && _backgroundMode == BackgroundMode.IconZone)
        {
            _animPath.Clear();
            foreach (var wp in pendingPath) _animPath.Add((wp.X, wp.Y));
            _animPathTotalLength = ComputePathLength(_animPath);
            _logger?.LogInformation("[IconZone] Path refreshed from host: {Pts} waypoints", _animPath.Count);
        }

        // F3a: when zones are used as a clip mask (Pattern + IconZone), skip path-following entirely
        // — the pattern fills the desktop and zones are a composition mask only.
        if (_maskZones)
        {
            // Fall through to standard MovementCalculator branch below (the pattern translates as a unit).
        }
        // IconZone: path-following. Path is in virtual-canvas space; subtract MonitorOffsetX to get local coords.
        else if (_backgroundMode == BackgroundMode.IconZone && _animPath.Count >= 2)
        {
            float speed = _movementConfig?.SpeedPixelsPerSecond ?? 300f;

            // Traverse detection: check if we've completed a full lap and need a new path
            // Must run BEFORE dist computation so RebuildPathOnly updates _animPathTotalLength
            if (_animPathTotalLength > 0f)
            {
                float fullDist = elapsedMs * speed / 1000f;
                float cycleDist = (_movementConfig?.Type == MovementType.Bounce)
                    ? _animPathTotalLength * 2f
                    : _animPathTotalLength;
                int newTraverseCount = (int)(fullDist / cycleDist);
                if (newTraverseCount > _traverseCount)
                {
                    _traverseCount = newTraverseCount;
                    RebuildPathOnly(_traverseCount); // variationSeed = iteration number → different route each time
                    // Signal only in sequential/precomputed mode. RebuildPathOnly() already returned early
                    // here (it no-ops when PrecomputedPath is set); host needs the signal to recompute a
                    // new A* path and broadcast it before the next lap starts.
                    bool isPrecomputedMode = _animationConfig?.PrecomputedPath?.Count > 0;
                    if (isPrecomputedMode)
                    {
                        Console.Error.WriteLine($"SIGNAL:LAP_COMPLETE:{_traverseCount}");
                        Console.Error.Flush();
                    }
                }
            }

            float dist;
            if (_movementConfig?.Type == MovementType.Bounce)
            {
                float cycle = _animPathTotalLength * 2f;
                float t = (elapsedMs * speed / 1000f) % cycle;
                dist = t < _animPathTotalLength ? t : cycle - t;
            }
            else
            {
                dist = (_animPathTotalLength > 0f)
                    ? (elapsedMs * speed / 1000f) % _animPathTotalLength
                    : 0f;
            }

            var (px, py) = SamplePath(_animPath, dist);

            // Compute path tangent when either rotation or SineWave perpendicular offset
            // is active — both consume it.
            bool needsTangent = _rotateWithPath
                || _movementConfig?.Type == MovementType.SineWave
                || (_movementConfig?.Type == MovementType.RandomWalk && _movementConfig.WaveAmplitudePixels > 0);
            float tangentAngle = 0f;
            if (needsTangent && _animPathTotalLength > 0f)
            {
                float lookAhead = MathF.Min(10f, _animPathTotalLength * 0.01f);
                bool nearEnd = dist > _animPathTotalLength - lookAhead;
                var (px2, py2) = SamplePath(_animPath, nearEnd ? dist - lookAhead : (dist + lookAhead) % _animPathTotalLength);
                float dx = nearEnd ? px - px2 : px2 - px;
                float dy = nearEnd ? py - py2 : py2 - py;
                tangentAngle = MathF.Atan2(dy, dx);
            }
            _animRotationRad = _rotateWithPath ? tangentAngle : 0f;

            if (_movementConfig?.Type == MovementType.SineWave)
            {
                float amp  = _movementConfig.WaveAmplitudePixels;
                float freq = _movementConfig.WaveFrequencyHz;
                float sinOffset = MathF.Sin(elapsedMs / 1000f * freq * MathF.Tau) * amp;
                // Oscillate perpendicular to path direction so the wave stays inside the padded
                // corridor. Perpendicular of (cos θ, sin θ) is (−sin θ, cos θ).
                float perpX = -MathF.Sin(tangentAngle);
                float perpY =  MathF.Cos(tangentAngle);
                px += perpX * sinOffset;
                py += perpY * sinOffset;
                // Clamp the center coordinate so the bitmap stays on-screen.
                py = Math.Clamp(py, _animHeight / 2f, Math.Max(_animHeight / 2f, _height - _animHeight / 2f));
            }

            if (_movementConfig?.Type == MovementType.RandomWalk && _movementConfig.WaveAmplitudePixels > 0)
            {
                float stepIntervalMs = MathF.Max(100f, _movementConfig.RandomStepIntervalMs);
                int stepIndex  = (int)(elapsedMs / stepIntervalMs);
                float withinStep = (elapsedMs % stepIntervalMs) / stepIntervalMs;  // 0..1
                int lapSeed = _movementConfig.RandomSeed ^ (int)((uint)_traverseCount * 2246822519u);
                float t0 = GeneratePerpOffset(lapSeed, stepIndex);
                float t1 = GeneratePerpOffset(lapSeed, stepIndex + 1);
                float perpOffset = (t0 + (t1 - t0) * withinStep) * _movementConfig.WaveAmplitudePixels;
                float perpX = -MathF.Sin(tangentAngle);
                float perpY =  MathF.Cos(tangentAngle);
                px += perpX * perpOffset;
                py += perpY * perpOffset;
                py = Math.Clamp(py, _animHeight / 2f, Math.Max(_animHeight / 2f, _height - _animHeight / 2f));
            }

            // (px, py) is the animation CENTER in virtual-canvas space.
            // _animX/_animY are the bitmap top-left, so offset by half the bitmap size.
            _animX = px - _animWidth / 2f - _monitorOffsetX;
            _animY = py - _animHeight / 2f;
            return;
        }

        // Warn if IconZone mode has no path to follow (should not happen since ZonePlanner has fallback)
        if (_backgroundMode == BackgroundMode.IconZone && _animPath.Count < 2)
            _logger?.LogWarning("[IconZone] Path-following skipped: only {Count} waypoints available. Check icon detection and ZonePlanner output.", _animPath.Count);

        // Standard movement via MovementCalculator (Static returns a centered, time-invariant position).
        if (_movementConfig != null)
        {
            var (vx, vy) = MovementCalculator.Calculate(
                _movementConfig, elapsedMs,
                _animWidth, _animHeight,
                _virtualCanvasWidth, _height);
            _animX = vx - _monitorOffsetX;
            _animY = vy;

            // Clamp Y to corridor when ThreeZone background is active
            if (_hasCorridorConstraint && _corridorHeightPx > 0)
            {
                float minY = _corridorTopPx;
                float maxY = _corridorTopPx + _corridorHeightPx - _animHeight;
                if (maxY < minY) maxY = minY;
                _animY = Math.Clamp(_animY, minY, maxY);
            }
        }
        else if (_pixelsPerSecond > 0)
        {
            // Legacy backward compat: simple left-to-right scroll for configs without MovementConfig.
            var elapsedSeconds = elapsedMs / 1000.0;
            _animX = (float)(-_animWidth + (elapsedSeconds * _pixelsPerSecond));
        }
    }

    /// <summary>
    /// Re-computes only the A* animation path (no background zone rebuild).
    /// Called on each full path traverse to pick a different route.
    ///
    /// SEQUENTIAL MODE: the initial path is a *global* path computed by the host over
    /// the full virtual canvas (all monitors' icons), delivered via AnimationConfig.
    /// PrecomputedPath. Each player has only its own monitor's icons / bounds, so it
    /// cannot regenerate a global path locally — if it tried, every monitor would
    /// produce its own local path and each monitor would show its own animation
    /// instead of the single spanning animation. So in sequential mode we keep the
    /// original global path for the life of the session (no per-lap variation).
    /// </summary>
    private static void RebuildPathOnly(int variationSeed)
    {
        if (_backgroundMode != BackgroundMode.IconZone) return;
        if (_detectedIcons.Count == 0) return;
        if (_backgroundConfig == null) return;

        // Sequential mode: keep the precomputed global path untouched. See method summary.
        if (_animationConfig?.PrecomputedPath is { Count: > 0 }) return;

        int pathPaddingPx = _animHeight / 2;
        if (_movementConfig?.Type == MovementType.SineWave)
            pathPaddingPx += (int)Math.Ceiling(_movementConfig.WaveAmplitudePixels);
        if (_movementConfig?.Type == MovementType.RandomWalk && _movementConfig.WaveAmplitudePixels > 0)
            pathPaddingPx += (int)Math.Ceiling(_movementConfig.WaveAmplitudePixels);

        int iconImageW = Math.Max(0, GetSystemMetrics(SM_CXICON));
        int iconImageH = Math.Max(0, GetSystemMetrics(SM_CYICON));

        var layout = WaBiBaBuSy.WallpaperEngine.Desktop.ZonePlanner.Compute(
            _detectedIcons, _detectedCellW, _detectedCellH, _width, _height,
            _backgroundConfig.IconZonePaletteHexes, _backgroundConfig.IconCorridorColorHex,
            paddingPx: pathPaddingPx,
            visualPaddingPx: Math.Max(4, _detectedCellW / 10),
            iconImageW: iconImageW,
            iconImageH: iconImageH,
            pathVariationSeed: variationSeed);

        _animPath.Clear();
        foreach (var wp in layout.Path)
            _animPath.Add((wp.X, wp.Y));

        _animPathTotalLength = ComputePathLength(_animPath);
        _logger?.LogInformation("[IconZone] Path rebuilt (traverse #{Seed}): {Pts} waypoints, totalLen={Len:F0}px",
            variationSeed, _animPath.Count, _animPathTotalLength);
    }

    private static float ComputePathLength(List<(float X, float Y)> path)
    {
        if (path.Count < 2) return 0f;
        float total = 0f;
        for (int i = 0; i < path.Count - 1; i++)
        {
            float dx = path[i + 1].X - path[i].X;
            float dy = path[i + 1].Y - path[i].Y;
            total += MathF.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    /// <summary>
    /// Returns a deterministic value in [-1, 1] for the given seed and step index.
    /// </summary>
    private static float GeneratePerpOffset(int seed, int stepIndex)
    {
        var rng = new Random(seed ^ (int)((uint)stepIndex * 2654435761u));
        return (float)(rng.NextDouble() * 2.0 - 1.0);
    }

    private static (float X, float Y) SamplePath(List<(float X, float Y)> path, float dist)
    {
        if (path.Count == 0) return (0f, 0f);
        if (path.Count == 1) return (path[0].X, path[0].Y);

        float remaining = dist;
        for (int i = 0; i < path.Count - 1; i++)
        {
            float dx = path[i + 1].X - path[i].X;
            float dy = path[i + 1].Y - path[i].Y;
            float segLen = MathF.Sqrt(dx * dx + dy * dy);
            if (remaining <= segLen || i == path.Count - 2)
            {
                float t = segLen > 0f ? Math.Clamp(remaining / segLen, 0f, 1f) : 0f;
                return (path[i].X + dx * t, path[i].Y + dy * t);
            }
            remaining -= segLen;
        }
        return (path[^1].X, path[^1].Y);
    }

    private static float ClampToNearestFreeBand(float y)
    {
        // With 2D per-icon zones the free space is not described as bands;
        // just clamp the sine offset to screen bounds so it doesn't go off-screen.
        return Math.Clamp(y, 0f, Math.Max(0f, _height - _animHeight));
    }

    // ================================
    // GDI+ to D2D conversion (video fallback)
    // ================================

    private static ID2D1Bitmap? ConvertBitmapToD2D(Bitmap gdiBitmap)
    {
        if (_d2dContext == null)
            return null;

        try
        {
            var bitmapData = gdiBitmap.LockBits(
                new Rectangle(0, 0, gdiBitmap.Width, gdiBitmap.Height),
                ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

            try
            {
                var bitmapProps = new BitmapProperties
                {
                    PixelFormat = new Vortice.DCommon.PixelFormat(
                        Format.B8G8R8A8_UNorm,
                        Vortice.DCommon.AlphaMode.Premultiplied),
                    DpiX = 96.0f,
                    DpiY = 96.0f
                };

                var d2dBitmap = _d2dContext.CreateBitmap(
                    new Vortice.Mathematics.SizeI(gdiBitmap.Width, gdiBitmap.Height),
                    bitmapData.Scan0,
                    (uint)bitmapData.Stride,
                    bitmapProps);

                return d2dBitmap;
            }
            finally
            {
                gdiBitmap.UnlockBits(bitmapData);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to convert bitmap to D2D");
            return null;
        }
    }

    // ================================
    // Command Processing
    // ================================

    private static void ProcessCommands()
    {
        _logger?.LogInformation("Command processor started");

        while (_running)
        {
            try
            {
                var line = Console.ReadLine();
                if (line == null)
                {
                    _logger?.LogInformation("Stdin closed, exiting");
                    _running = false;
                    break;
                }

                line = line.Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                _logger?.LogDebug("Received command: {Command}", line.Substring(0, Math.Min(50, line.Length)));

                // Handle string-based commands (legacy/special)
                if (line.StartsWith("PARENT:"))
                {
                    lock (_parentLock)
                    {
                        _pendingParentCommand = line;
                    }
                }
                else if (line.StartsWith("COLOR:"))
                {
                    HandleColorCommand(line);
                }
                else if (line == "EXIT")
                {
                    _running = false;
                    Console.WriteLine("READY");
                    Console.Out.Flush();
                }
                else if (line == "TEST")
                {
                    _testModeEnabled = true;
                    _lastColorToggle = DateTime.UtcNow;
                    _logger?.LogInformation("[TEST MODE] Enabled! Will toggle red/blue every 2 seconds");
                    Console.WriteLine("READY");
                    Console.Out.Flush();
                }
                else if (line == "TESTOFF")
                {
                    _testModeEnabled = false;
                    _logger?.LogInformation("[TEST MODE] Disabled");
                    Console.WriteLine("READY");
                    Console.Out.Flush();
                }
                else if (line.StartsWith("{"))
                {
                    HandleJsonCommand(line);
                }
                else
                {
                    Console.WriteLine($"ERROR:Unknown command: {line}");
                    Console.Out.Flush();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Command processing error");
                Console.WriteLine($"ERROR:{ex.Message}");
                Console.Out.Flush();
            }
        }

        _logger?.LogInformation("Command processor stopped");
    }

    private static void HandleColorCommand(string line)
    {
        try
        {
            var hex = line.Substring(6);
            if (hex.Length == 6)
            {
                int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                var newColor = new Color4(r / 255f, g / 255f, b / 255f, 1f);

                lock (_colorLock)
                {
                    _currentColor = newColor;
                }

                _logger?.LogInformation("Color set to RGB({R},{G},{B})", r, g, b);
                Console.WriteLine("READY");
                Console.Out.Flush();
            }
            else
            {
                Console.WriteLine("ERROR:Invalid color format");
                Console.Out.Flush();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Color command error");
            Console.WriteLine($"ERROR:{ex.Message}");
            Console.Out.Flush();
        }
    }

    private static void HandleJsonCommand(string json)
    {
        try
        {
            var wrapper = JsonConvert.DeserializeObject<MessageTypeWrapper>(json);
            if (wrapper == null || string.IsNullOrEmpty(wrapper.MessageType))
            {
                Console.WriteLine("ERROR:Failed to parse JSON message or missing MessageType");
                Console.Out.Flush();
                return;
            }

            _logger?.LogDebug("JSON message type: {MessageType}", wrapper.MessageType);

            switch (wrapper.MessageType)
            {
                case "cmd_load_animation":
                    var loadCmd = JsonConvert.DeserializeObject<PlayerCommandLoadAnimation>(json);
                    if (loadCmd != null)
                        HandleLoadAnimationCommand(loadCmd);
                    else
                        Console.WriteLine("ERROR:Failed to deserialize PlayerCommandLoadAnimation");
                    break;

                case "cmd_start_animation":
                    var startCmd = JsonConvert.DeserializeObject<PlayerCommandStartAnimation>(json);
                    if (startCmd != null)
                        HandleStartAnimationCommand(startCmd);
                    else
                        Console.WriteLine("ERROR:Failed to deserialize PlayerCommandStartAnimation");
                    break;

                case "cmd_stop_animation":
                    HandleStopAnimationCommand();
                    break;

                case "cmd_toggle_debug_overlay":
                    var overlayCmd = JsonConvert.DeserializeObject<PlayerCommandToggleDebugOverlay>(json);
                    if (overlayCmd != null)
                    {
                        _debugOverlay.Enabled = overlayCmd.Toggle ? !_debugOverlay.Enabled : overlayCmd.Enabled;
                        _logger?.LogInformation("[Debug] Overlay {State} via stdin", _debugOverlay.Enabled ? "ON" : "OFF");
                    }
                    else
                        Console.WriteLine("ERROR:Failed to deserialize PlayerCommandToggleDebugOverlay");
                    break;

                case "cmd_set_debug_overlay_flags":
                    var flagsCmd = JsonConvert.DeserializeObject<PlayerCommandSetDebugOverlayFlags>(json);
                    if (flagsCmd != null)
                    {
                        _debugOverlay.Enabled = flagsCmd.Enabled;
                        _debugOverlay.ShowPath = flagsCmd.ShowPath;
                        _debugOverlay.ShowIconRects = flagsCmd.ShowIconRects;
                        _debugOverlay.ShowZoneBandOutlines = flagsCmd.ShowZoneBandOutlines;
                        _debugOverlay.ShowInfoPanel = flagsCmd.ShowInfoPanel;
                        _logger?.LogInformation("[Debug] Overlay flags updated: Enabled={E} Path={P} Rects={R} Zones={Z} Info={I}",
                            flagsCmd.Enabled, flagsCmd.ShowPath, flagsCmd.ShowIconRects, flagsCmd.ShowZoneBandOutlines, flagsCmd.ShowInfoPanel);
                    }
                    else
                        Console.WriteLine("ERROR:Failed to deserialize PlayerCommandSetDebugOverlayFlags");
                    break;

                case "cmd_update_path":
                    var pathCmd = JsonConvert.DeserializeObject<PlayerCommandUpdatePath>(json);
                    if (pathCmd?.Path?.Count > 0)
                        _pendingNewPath = pathCmd.Path;
                    break;

                default:
                    Console.WriteLine($"ERROR:Unknown JSON message type: {wrapper.MessageType}");
                    Console.Out.Flush();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "JSON command error");
            Console.WriteLine($"ERROR:JSON parsing failed: {ex.Message}");
            Console.Out.Flush();
        }
    }

    /// <summary>
    /// Drop all per-source frame/dimension caches. Disposes the GPU bitmaps too.
    /// </summary>
    private static void ClearPerSourceCaches()
    {
        // Don't dispose _d2dGifFrames here — DisposeNativeD2DResources() handles that path.
        // We only need to clear the references in the multi-source lists; the underlying
        // bitmap objects either come from _d2dGifFrames (already disposed) or were uploaded
        // directly into the per-source lists (disposed below).
        for (int i = 1; i < _d2dFramesPerSource.Count; i++)
        {
            var frames = _d2dFramesPerSource[i];
            for (int f = 0; f < frames.Length; f++)
            {
                frames[f]?.Dispose();
            }
        }
        _d2dFramesPerSource.Clear();
        _d2dDelaysPerSource.Clear();
        _d2dTotalDurationPerSource.Clear();
        _sourceDimensions.Clear();
    }

    /// <summary>
    /// Load any AdditionalAnimationPaths into the per-source caches. The primary source
    /// has already been loaded into _d2dGifFrames and pushed onto the per-source lists.
    /// </summary>
    private static void LoadAdditionalAnimationSources(AnimationLayerConfig animConfig)
    {
        if (animConfig.AdditionalAnimationPaths == null || animConfig.AdditionalAnimationPaths.Count == 0)
            return;

        foreach (var path in animConfig.AdditionalAnimationPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp")
                {
                    using var image = new MagickImage(path);
                    using var gdi = image.ToBitmap();
                    var bmp = UploadBitmapToD2D(gdi);
                    _d2dFramesPerSource.Add(new[] { bmp });
                    _d2dDelaysPerSource.Add(new List<int> { 1000 });
                    _d2dTotalDurationPerSource.Add(1000);
                    _sourceDimensions.Add(((int)image.Width, (int)image.Height));
                    _logger?.LogInformation("[LOAD] Additional source loaded: {Path} ({W}x{H})", path, image.Width, image.Height);
                }
                else
                {
                    // For now, only static-image additional sources are supported. GIF/video
                    // multi-source can be added later; the pattern picker will fall back to the
                    // primary source for non-image additional paths.
                    _logger?.LogWarning("[LOAD] Skipping non-image additional source: {Path} (extension {Ext})", path, ext);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[LOAD] Failed to load additional source: {Path}", path);
            }
        }
    }

    /// <summary>
    /// Lazily create the ColorMatrix D2D effect. Reused across frames; only the matrix data
    /// is updated each frame (cheap — the effect object itself is heavy to construct).
    /// </summary>
    private static unsafe void EnsureColorMatrixEffect()
    {
        if (_colorMatrixEffect != null || _d2dContext == null) return;
        try
        {
            // Vortice 3.6.2 returns a raw nint from CreateEffect; wrap it in the typed COM interface.
            nint ptr = _d2dContext.CreateEffect(EffectGuids.ColorMatrix);
            if (ptr == IntPtr.Zero)
            {
                _logger?.LogError("[ColorGrading] CreateEffect returned null pointer");
                return;
            }
            _colorMatrixEffect = new ID2D1Effect(ptr);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[ColorGrading] Failed to create D2D ColorMatrix effect");
        }
    }

    /// <summary>
    /// Apply a 5x4 color matrix to the effect's parameter slot (D2D1_COLORMATRIX_PROP_COLOR_MATRIX = 0).
    /// </summary>
    private static unsafe void SetColorMatrixOnEffect(ColorMatrix5x4 m)
    {
        if (_colorMatrixEffect == null) return;
        // Pack matrix into the persistent byte buffer (row-major, 20 floats).
        var floats = MemoryMarshal.Cast<byte, float>(_colorMatrixBytes);
        floats[0] = m.M11; floats[1] = m.M12; floats[2] = m.M13; floats[3] = m.M14;
        floats[4] = m.M21; floats[5] = m.M22; floats[6] = m.M23; floats[7] = m.M24;
        floats[8] = m.M31; floats[9] = m.M32; floats[10] = m.M33; floats[11] = m.M34;
        floats[12] = m.M41; floats[13] = m.M42; floats[14] = m.M43; floats[15] = m.M44;
        floats[16] = m.M51; floats[17] = m.M52; floats[18] = m.M53; floats[19] = m.M54;
        try
        {
            _colorMatrixEffect.SetValueByName("ColorMatrix", PropertyType.Matrix5x4, _colorMatrixBytes, (uint)_colorMatrixBytes.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[ColorGrading] Failed to set color matrix");
        }
    }

    /// <summary>
    /// Get the current GIF frame index for a specific source, given total elapsed time.
    /// </summary>
    private static int GetCurrentFrameIndexForSource(int sourceIdx, long elapsedMs)
    {
        if (sourceIdx < 0 || sourceIdx >= _d2dFramesPerSource.Count) return 0;
        var frames = _d2dFramesPerSource[sourceIdx];
        if (frames.Length <= 1) return 0;

        var delays = _d2dDelaysPerSource[sourceIdx];
        long total = _d2dTotalDurationPerSource[sourceIdx];
        if (total <= 0) return 0;

        // Honor the same speed multiplier the primary source uses
        long t = (long)(elapsedMs * _gifSpeedMultiplier) % total;
        long acc = 0;
        for (int i = 0; i < delays.Count; i++)
        {
            acc += delays[i];
            if (t < acc) return Math.Min(i, frames.Length - 1);
        }
        return frames.Length - 1;
    }

    /// <summary>
    /// Cover icon-zone rectangles with the corridor (plain) color so the pattern only shows
    /// in free space and the user's desktop icons remain visible. No-op when MaskZones is off.
    /// When gradientColor is provided the cover takes that color so the fade blends into the
    /// current gradient hue rather than the static corridor background.
    /// </summary>
    private static void DrawZoneCoverIfMasked(Color4? gradientColor = null)
    {
        if (!_maskZones || _d2dContext == null) return;
        if (_iconZoneBands.Count == 0) return;

        if (_zoneCoverBrush == null)
            _zoneCoverBrush = _d2dContext.CreateSolidColorBrush(_iconCorridorBgColor);

        _zoneCoverBrush.Color = gradientColor ?? _iconCorridorBgColor;

        foreach (var (zY, zH, zX, zW, isFree, _) in _iconZoneBands)
        {
            if (isFree) continue;  // free bands are corridor — pattern stays
            float drawW = zW < 0 ? _width : zW;
            _d2dContext.FillRectangle(new System.Drawing.RectangleF(zX, zY, drawW, zH), _zoneCoverBrush);
        }
    }

    /// <summary>
    /// Draw the animation layer for the current frame. Handles single-source/no-pattern
    /// (single DrawBitmap), multi-source/no-pattern (each image at a seeded offset), and
    /// pattern-fill (infinite world-space grid via PatternLayout).
    /// </summary>
    private static void DrawAnimationLayer(long elapsedMs, ID2D1Bitmap? videoBitmapOverride = null)
    {
        if (_d2dContext == null || _animationConfig == null) return;

        // Determine source bitmaps to use. For video, the override supersedes per-source frames.
        bool useVideo = videoBitmapOverride != null;
        int sourceCount = useVideo ? 1 : Math.Max(1, _d2dFramesPerSource.Count);
        if (!useVideo && _d2dFramesPerSource.Count == 0) return;

        // Color grading: compute one matrix per frame for time-based modes; per-cell for Traveling modes.
        bool gradingActive = _animationConfig.ColorGrading != null
                             && _animationConfig.ColorGrading.Mode != ColorGradingMode.None;
        bool isTraveling = _animationConfig.ColorGrading != null &&
                           (_animationConfig.ColorGrading.Mode == ColorGradingMode.TravelingRainbow ||
                            _animationConfig.ColorGrading.Mode == ColorGradingMode.TravelingList ||
                            _animationConfig.ColorGrading.Mode == ColorGradingMode.TravelingRandom);
        if (gradingActive)
        {
            EnsureColorMatrixEffect();
            if (!isTraveling)
            {
                var grading = ColorGrader.Compute(_animationConfig.ColorGrading, elapsedMs);
                SetColorMatrixOnEffect(grading);
            }
        }

        // Decide draw mode.
        var pattern = _animationConfig.Pattern;
        bool patternActive = pattern != null && !useVideo;  // video skipped for now (single-cell)

        if (patternActive)
        {
            // Cell slot = FitMode-scaled animation dimensions (_animHeight), NOT TargetHeight.
            // ScaleToTargetHeight was incorrect here: it scaled small logos up to 720+ px,
            // collapsing many rows down to 1-2. Using _animHeight respects FitMode:
            //   Center → native size (typical for patterns), Fill → screen height, etc.
            int cellW = _animWidth, cellH = _animHeight;
            if (_sourceDimensions.Count > 0 && _animHeight > 0)
            {
                int maxNativeH = 0;
                foreach (var (_, h) in _sourceDimensions)
                    if (h > maxNativeH) maxNativeH = h;
                if (maxNativeH > 0)
                {
                    float pScale = (float)_animHeight / maxNativeH;
                    int maxScaledW = 0;
                    foreach (var (w, _) in _sourceDimensions)
                    {
                        int sw = (int)(w * pScale);
                        if (sw > maxScaledW) maxScaledW = sw;
                    }
                    if (maxScaledW > 0) cellW = maxScaledW;
                }
            }

            var cells = PatternLayout.Compute(
                pattern!,
                anchorX: _animX, anchorY: _animY,
                cellW: cellW, cellH: cellH,
                virtualCanvasWidth: _virtualCanvasWidth, virtualCanvasHeight: _height,
                monitorOffsetX: _monitorOffsetX,
                monitorWidth: _width, monitorHeight: _height,
                sourceImageCount: sourceCount);

            bool hasFade = _maskZones && _iconZoneBands.Count > 0;
            float fadeRadius = cellH > 0 ? cellH : 80f;

            foreach (var cell in cells)
            {
                int srcIdx = cell.SourceIndex % sourceCount;
                int frameIdx = GetCurrentFrameIndexForSource(srcIdx, elapsedMs);
                var frames = _d2dFramesPerSource[srcIdx];
                if (frames.Length == 0) continue;
                var bmp = frames[Math.Min(frameIdx, frames.Length - 1)];

                var (drawW, drawH) = ComputePatternDrawSize(srcIdx);

                float alpha = hasFade
                    ? ComputePatternCellAlpha(cell.ScreenX + drawW / 2f, cell.ScreenY + drawH / 2f, fadeRadius)
                    : 1f;
                if (alpha <= 0.01f) continue;

                if (isTraveling && gradingActive)
                {
                    var cellMatrix = ColorGrader.ComputeForCell(_animationConfig.ColorGrading, cell.LogicalI, cell.LogicalJ);
                    SetColorMatrixOnEffect(cellMatrix);
                }
                DrawCellWithOptionalGrading(bmp, cell.ScreenX, cell.ScreenY, drawW, drawH, cell.RotationDeg, gradingActive, alpha);
            }
        }
        else if (sourceCount > 1 && !useVideo)
        {
            // Multi-image without pattern: draw each source at a seeded offset around the anchor.
            int seed = _animationConfig.Pattern?.Seed ?? 0;
            float spread = Math.Max(0f, _animationConfig.MultiImageSpread);
            float jitterMs = Math.Max(0f, _animationConfig.MultiImagePhaseJitterMs);

            for (int srcIdx = 0; srcIdx < sourceCount; srcIdx++)
            {
                int hash = Hash2(seed, srcIdx);
                float dx = ((((hash & 0xFFFF) / 65535f) * 2f) - 1f) * spread;
                float dy = (((((hash >> 16) & 0xFFFF) / 65535f) * 2f) - 1f) * spread;
                long phase = (long)(((srcIdx * 0.6180339887) % 1.0) * jitterMs);

                int frameIdx = GetCurrentFrameIndexForSource(srcIdx, elapsedMs + phase);
                var frames = _d2dFramesPerSource[srcIdx];
                if (frames.Length == 0) continue;
                var bmp = frames[Math.Min(frameIdx, frames.Length - 1)];

                var (drawW, drawH) = ComputeDrawSizeForSource(srcIdx);
                DrawCellWithOptionalGrading(bmp, _animX + dx, _animY + dy, drawW, drawH, 0f, gradingActive);
            }
        }
        else
        {
            // Original single-source path. Honor RotateWithPath when active.
            ID2D1Bitmap? bmp = useVideo ? videoBitmapOverride : (_d2dGifFrames != null && _d2dGifFrames.Length > 0
                ? _d2dGifFrames[GetCurrentGifFrameIndex(elapsedMs)]
                : null);
            if (bmp == null) return;

            float rotDeg = 0f;
            bool hasPathRotation = _rotateWithPath && _animRotationRad != 0f && _animPath.Count >= 2;
            if (hasPathRotation) rotDeg = _animRotationRad * 180f / MathF.PI;

            DrawCellWithOptionalGrading(bmp, _animX, _animY, _animWidth, _animHeight, rotDeg, gradingActive);
        }

        // F3a: cover icon-zone rectangles so user icons remain visible.
        // When color grading is active, fill with the current gradient color so the fade
        // blends into the live hue rather than the static corridor background.
        Color4? gradientCover = null;
        if (gradingActive && _animationConfig?.ColorGrading != null)
        {
            var (cr, cg, cb) = ColorGrader.ComputeCurrentColor(_animationConfig.ColorGrading, elapsedMs);
            gradientCover = new Color4(cr, cg, cb, 1f);
        }
        DrawZoneCoverIfMasked(gradientCover);
    }

    private static (int W, int H) ScaleToTargetHeight(int srcW, int srcH)
    {
        int targetH = _animationConfig?.TargetHeight ?? 720;
        if (srcH <= 0) return (_animWidth, _animHeight);
        float scale = (float)targetH / srcH;
        return ((int)(srcW * scale), targetH);
    }

    private static (int W, int H) ComputeDrawSizeForSource(int srcIdx)
    {
        if (srcIdx < 0 || srcIdx >= _sourceDimensions.Count) return (_animWidth, _animHeight);
        var (sw, sh) = _sourceDimensions[srcIdx];
        return ScaleToTargetHeight(sw, sh);
    }

    // Pattern-specific draw size: scale each source proportionally to _animHeight (not TargetHeight).
    private static (int W, int H) ComputePatternDrawSize(int srcIdx)
    {
        if (_animHeight <= 0 || srcIdx < 0 || srcIdx >= _sourceDimensions.Count)
            return (_animWidth, _animHeight);
        var (sw, sh) = _sourceDimensions[srcIdx];
        if (sh <= 0) return (_animWidth, _animHeight);
        float scale = (float)_animHeight / sh;
        return ((int)(sw * scale), _animHeight);
    }

    // Returns [0..1] opacity for a pattern cell centered at (cx,cy).
    // Dead zone: pattern stays invisible for 70% of fadeRadius beyond each zone edge.
    // Blend zone: fades in over the next 50% using a quadratic ease for a snappier transition.
    private static float ComputePatternCellAlpha(float cx, float cy, float fadeRadius)
    {
        float minDist = float.MaxValue;
        foreach (var (zY, zH, zX, zW, isFree, _) in _iconZoneBands)
        {
            if (isFree) continue;
            float rW = zW < 0 ? _width : zW;
            float nearX = Math.Clamp(cx, zX, zX + rW);
            float nearY = Math.Clamp(cy, zY, zY + zH);
            float dx = cx - nearX, dy = cy - nearY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist < minDist) minDist = dist;
        }
        float deadRadius = fadeRadius * 0.7f;
        float blendRadius = fadeRadius * 0.5f;
        float t = Math.Clamp((minDist - deadRadius) / blendRadius, 0f, 1f);
        return t * t;
    }

    /// <summary>
    /// Draw one cell with optional rotation and color grading.
    /// </summary>
    private static void DrawCellWithOptionalGrading(
        ID2D1Bitmap bmp, float x, float y, int w, int h,
        float rotationDeg, bool gradingActive, float alpha = 1f)
    {
        if (_d2dContext == null) return;

        bool needsRotation = MathF.Abs(rotationDeg) > 0.01f;
        Matrix3x2 prior = Matrix3x2.Identity;
        if (needsRotation)
        {
            float cx = x + w / 2f;
            float cy = y + h / 2f;
            prior = _d2dContext.Transform;
            _d2dContext.Transform = Matrix3x2.CreateRotation(rotationDeg * MathF.PI / 180f, new Vector2(cx, cy));
        }

        // Color grading via DrawImage doesn't support per-draw opacity; fall back to
        // DrawBitmap (with alpha) when the cell is being faded near an icon zone.
        bool useGrading = gradingActive && _colorMatrixEffect != null && alpha >= 0.999f;
        if (useGrading)
        {
            try
            {
                _colorMatrixEffect!.SetInput(0, bmp, true);
                _d2dContext.DrawImage(_colorMatrixEffect.Output, new Vector2(x, y), null, InterpolationMode.Linear, CompositeMode.SourceOver);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[ColorGrading] DrawImage failed; falling back to DrawBitmap");
                _d2dContext.DrawBitmap(bmp, new System.Drawing.RectangleF(x, y, w, h), alpha, BitmapInterpolationMode.Linear, null);
            }
        }
        else
        {
            _d2dContext.DrawBitmap(bmp, new System.Drawing.RectangleF(x, y, w, h), alpha, BitmapInterpolationMode.Linear, null);
        }

        if (needsRotation) _d2dContext.Transform = prior;
    }

    private static int Hash2(int seed, int k)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)k * 2654435761u;
            h ^= h >> 13;
            h *= 2246822519u;
            h ^= h >> 16;
            return (int)h;
        }
    }

    private static void HandleLoadAnimationCommand(PlayerCommandLoadAnimation cmd)
    {
        try
        {
            _logger?.LogInformation("Loading animation: {Path}", cmd.AnimationConfig.AnimationPath);

            lock (_compositionLock)
            {
                // Stage 5: Dispose existing native D2D resources
                DisposeNativeD2DResources();

                // Dispose existing composition fallback
                _compositionRenderer?.Dispose();
                _compositionRenderer = null;
                _canvasManager = null;
                _compositionInitialized = false;
                _useNativeD2DComposition = false;

                // Store configuration
                _animationConfig = cmd.AnimationConfig;
                _backgroundConfig = cmd.BackgroundConfig;
                _movementConfig = cmd.MovementConfig;
                _virtualCanvasWidth = cmd.VirtualCanvasWidth;
                _monitorOffsetX = cmd.MonitorOffsetX;
                _maskZones = cmd.MaskZones;
                _useZonePalette = cmd.UseZonePalette;

                // Reset per-source caches before loading
                ClearPerSourceCaches();

                var filePath = cmd.AnimationConfig.AnimationPath;
                var extension = Path.GetExtension(filePath).ToLowerInvariant();

                if (extension == ".gif")
                {
                    // Stage 2: Native D2D path for GIFs
                    _gifSpeedMultiplier = cmd.AnimationConfig.SpeedMultiplier;
                    _logger?.LogInformation("[LOAD] GIF detected, using native D2D composition (SpeedMultiplier: {Speed}x)", _gifSpeedMultiplier);

                    // Extract GIF frames to GPU
                    ExtractGifFramesToD2D(filePath);

                    // Calculate animation layout first so _animHeight is set
                    // (InitializeBackground IconZone uses _animHeight / 2 as path padding)
                    CalculateAnimationLayout(cmd.AnimationConfig);

                    // Initialize background (Stage 3)
                    InitializeBackground(cmd.BackgroundConfig);

                    _useNativeD2DComposition = true;
                    _logger?.LogInformation("[LOAD] Native D2D composition ready: {Frames} frames, {W}x{H}", _d2dGifFrames?.Length, _animWidth, _animHeight);
                }
                else if (extension is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".flv")
                {
                    // Stage 6: Native D2D path for videos (LibVLC → raw buffer → CopyFromMemory)
                    _logger?.LogInformation("[LOAD] Video detected ({Ext}), using native D2D video", extension);

                    InitializeNativeVideo(filePath);

                    _contentNativeWidth = _videoNativeWidth;
                    _contentNativeHeight = _videoNativeHeight;
                    CalculateAnimationLayout(cmd.AnimationConfig);

                    InitializeBackground(cmd.BackgroundConfig);

                    _useNativeD2DVideo = true;
                    _logger?.LogInformation("[LOAD] Native D2D video ready: {W}x{H}", _videoNativeWidth, _videoNativeHeight);
                }
                else if (extension is ".jpg" or ".jpeg" or ".png" or ".bmp")
                {
                    // Static image: load as single-frame via Magick.NET, reuse GIF pipeline
                    _logger?.LogInformation("[LOAD] Static image detected ({Ext}), loading as single D2D frame", extension);

                    using var image = new MagickImage(filePath);
                    _contentNativeWidth = (int)image.Width;
                    _contentNativeHeight = (int)image.Height;

                    using var gdiBitmap = image.ToBitmap();
                    _d2dGifFrames = [UploadBitmapToD2D(gdiBitmap)];
                    _d2dGifDelays = [1000]; // Single frame, delay irrelevant
                    _d2dGifTotalDurationMs = 1000;

                    CalculateAnimationLayout(cmd.AnimationConfig);
                    InitializeBackground(cmd.BackgroundConfig);

                    _useNativeD2DComposition = true;
                    _logger?.LogInformation("[LOAD] Static image ready: {W}x{H}", _contentNativeWidth, _contentNativeHeight);
                }
                else
                {
                    _logger?.LogWarning("[LOAD] Unsupported format ({Ext}), no rendering available", extension);
                }

                // Populate per-source caches: primary source first, then any AdditionalAnimationPaths.
                if (_d2dGifFrames != null)
                {
                    _d2dFramesPerSource.Add(_d2dGifFrames);
                    _d2dDelaysPerSource.Add(_d2dGifDelays ?? new List<int> { 1000 });
                    _d2dTotalDurationPerSource.Add(_d2dGifTotalDurationMs > 0 ? _d2dGifTotalDurationMs : 1000);
                    _sourceDimensions.Add((_contentNativeWidth, _contentNativeHeight));
                }
                LoadAdditionalAnimationSources(cmd.AnimationConfig);
            }

            Console.WriteLine("READY");
            Console.Out.Flush();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load animation");
            Console.WriteLine($"ERROR:Load animation failed: {ex.Message}");
            Console.Out.Flush();
        }
    }

    private static void HandleStartAnimationCommand(PlayerCommandStartAnimation cmd)
    {
        try
        {
            _logger?.LogInformation("[START-CMD] StartTimestamp: {Timestamp}ms, PixelsPerSecond: {PPS}px/s",
                cmd.StartTimestampMs, cmd.PixelsPerSecond);

            lock (_compositionLock)
            {
                if (!_compositionInitialized && !_useNativeD2DComposition && !_useNativeD2DVideo)
                {
                    _logger?.LogError("[START-CMD] FAILED: No rendering path initialized!");
                    throw new InvalidOperationException("No composition initialized. Call LOAD_ANIMATION first.");
                }

                _startTimestampMs = cmd.StartTimestampMs;
                _pixelsPerSecond = cmd.PixelsPerSecond;

                // Use shared UTC timestamp if provided (> 0) for multi-monitor sync,
                // otherwise fall back to local time for single-monitor mode
                if (cmd.StartTimestampMs > 0)
                {
                    // Convert UTC ms timestamp to DateTime for consistent elapsed calculation
                    _renderLoopStart = DateTime.UtcNow.AddMilliseconds(
                        -(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cmd.StartTimestampMs));
                    _logger?.LogInformation("[START-CMD] Using shared timestamp: {Ts}ms, offset from now: {Offset}ms",
                        cmd.StartTimestampMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cmd.StartTimestampMs);
                }
                else
                {
                    _renderLoopStart = DateTime.UtcNow;
                }
                _isPlaying = true;

                // Resume video if it was paused
                if (_useNativeD2DVideo && _vlcPlayer != null && !_vlcPlayer.IsPlaying)
                    _vlcPlayer.Play();

                // Recalculate initial position with updated pixelsPerSecond
                if ((_useNativeD2DComposition || _useNativeD2DVideo) && _animationConfig != null)
                {
                    if (_centerInitialPosition || _pixelsPerSecond == 0)
                        _animX = (_width - _animWidth) / 2f;
                    else
                        _animX = -_animWidth;
                }

                _logger?.LogInformation("[START-CMD] SUCCESS: _isPlaying = TRUE, PPS = {PPS}, NativeD2D = {Native}", _pixelsPerSecond, _useNativeD2DComposition);
            }

            Console.WriteLine("READY");
            Console.Out.Flush();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[START-CMD] EXCEPTION during start animation");
            Console.WriteLine($"ERROR:Start animation failed: {ex.Message}");
            Console.Out.Flush();
        }
    }

    private static void HandleStopAnimationCommand()
    {
        try
        {
            _logger?.LogInformation("Stopping animation");

            lock (_compositionLock)
            {
                _isPlaying = false;

                if (_useNativeD2DVideo && _vlcPlayer != null && _vlcPlayer.IsPlaying)
                    _vlcPlayer.Pause();
            }

            Console.WriteLine("READY");
            Console.Out.Flush();
            _logger?.LogInformation("Animation stopped");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to stop animation");
            Console.WriteLine($"ERROR:Stop animation failed: {ex.Message}");
            Console.Out.Flush();
        }
    }

    // ================================
    // Stage 5: Cleanup & Disposal
    // ================================

    /// <summary>
    /// Dispose native D2D GIF frames and background bitmap.
    /// Called when loading new animation or during final cleanup.
    /// </summary>
    private static void DisposeNativeD2DResources()
    {
        if (_d2dGifFrames != null)
        {
            for (int i = 0; i < _d2dGifFrames.Length; i++)
            {
                _d2dGifFrames[i]?.Dispose();
            }
            _d2dGifFrames = null;
            _logger?.LogInformation("[DISPOSE] D2D GIF frames released");
        }
        _d2dGifDelays = null;
        _d2dGifTotalDurationMs = 0;

        // Multi-source caches: dispose any additional bitmaps that were uploaded directly into
        // _d2dFramesPerSource[1..]. Index 0 shares storage with _d2dGifFrames (already disposed above).
        for (int i = 1; i < _d2dFramesPerSource.Count; i++)
        {
            var frames = _d2dFramesPerSource[i];
            for (int f = 0; f < frames.Length; f++) frames[f]?.Dispose();
        }
        _d2dFramesPerSource.Clear();
        _d2dDelaysPerSource.Clear();
        _d2dTotalDurationPerSource.Clear();
        _sourceDimensions.Clear();

        _colorMatrixEffect?.Dispose();
        _colorMatrixEffect = null;
        _zoneCoverBrush?.Dispose();
        _zoneCoverBrush = null;

        _backgroundImageBitmap?.Dispose();
        _backgroundImageBitmap = null;

        _topZoneBrush?.Dispose();    _topZoneBrush    = null;
        _bottomZoneBrush?.Dispose(); _bottomZoneBrush = null;
        _corridorBrush?.Dispose();   _corridorBrush   = null;
        _hasCorridorConstraint = false;

        foreach (var (_, _, _, _, _, brush) in _iconZoneBands) brush?.Dispose();
        _iconZoneBands.Clear();
        _animPath.Clear();

        // Dispose native video resources
        try { _vlcPlayer?.Stop(); } catch { }
        _vlcPlayer?.Dispose();
        _vlcPlayer = null;
        _libVLC?.Dispose();
        _libVLC = null;
        _currentVideoD2DBitmap?.Dispose();
        _currentVideoD2DBitmap = null;
        if (_videoBufferA != IntPtr.Zero) { Marshal.FreeHGlobal(_videoBufferA); _videoBufferA = IntPtr.Zero; }
        if (_videoBufferB != IntPtr.Zero) { Marshal.FreeHGlobal(_videoBufferB); _videoBufferB = IntPtr.Zero; }
        _videoReadBuffer = IntPtr.Zero;
        _videoWriteBuffer = IntPtr.Zero;
        _useNativeD2DVideo = false;
    }

    private static void Cleanup()
    {
        _logger?.LogInformation("Cleanup starting");
        _running = false;

        // Dispose native D2D resources
        DisposeNativeD2DResources();

        // Dispose composition fallback
        lock (_compositionLock)
        {
            _compositionRenderer?.Dispose();
            _compositionRenderer = null;
            _canvasManager = null;
            _compositionInitialized = false;
        }

        // Debug overlay brushes (lazy-created, may be null)
        _debugPathBrush?.Dispose();     _debugPathBrush = null;
        _debugWaypointBrush?.Dispose(); _debugWaypointBrush = null;
        _debugAnimRectBrush?.Dispose(); _debugAnimRectBrush = null;
        _debugInfoBrush?.Dispose();     _debugInfoBrush = null;
        _debugIconRectBrush?.Dispose(); _debugIconRectBrush = null;
        _debugTrailBrush?.Dispose();    _debugTrailBrush = null;
        _debugZoneBandBrush?.Dispose(); _debugZoneBandBrush = null;

        // Dispose D2D/D3D resources (Stage 1 order)
        _d2dContext?.Dispose();
        _d2dDevice?.Dispose();
        _swapChain?.Dispose();
        _immediateContext?.Dispose();
        _d3dDevice?.Dispose();
        _d2dFactory?.Dispose();

        // Destroy window
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID_DEBUG_OVERLAY);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_classAtom != 0)
        {
            var hInstance = GetModuleHandle(null);
            UnregisterClass(_windowClassName, hInstance);
            _classAtom = 0;
        }

        // Free the pinned delegate
        if (_wndProcHandle.IsAllocated)
        {
            _wndProcHandle.Free();
        }

        _logger?.LogInformation("Cleanup complete");
    }
}

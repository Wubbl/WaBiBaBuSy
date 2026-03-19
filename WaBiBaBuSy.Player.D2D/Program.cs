using System.Runtime.InteropServices;
using System.Drawing;
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
    private const int IDC_ARROW = 32512;

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

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint WS_CHILD = 0x40000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const uint LWA_ALPHA = 0x2;
    private const uint SWP_NOACTIVATE = 0x0010;
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

    // Pending PARENT command to be processed on main thread
    private static volatile string? _pendingParentCommand = null;
    private static readonly object _parentLock = new();

    // Composition system (video fallback path)
    private static CompositionRenderer? _compositionRenderer;
    private static VirtualCanvasManager? _canvasManager;
    private static AnimationLayerConfig? _animationConfig;
    private static BackgroundLayerConfig? _backgroundConfig;
    private static readonly object _compositionLock = new();
    private static volatile bool _compositionInitialized = false;

    // Stage 2: Native D2D GIF frame cache
    private static ID2D1Bitmap[]? _d2dGifFrames;      // GPU-cached frames
    private static List<int>? _d2dGifDelays;            // Per-frame delay (ms)
    private static long _d2dGifTotalDurationMs;
    private static int _gifNativeWidth, _gifNativeHeight;
    private static double _gifSpeedMultiplier = 1.0;
    private static volatile bool _useNativeD2DComposition = false;

    // Stage 3: Native D2D background
    private static ID2D1Bitmap? _backgroundImageBitmap;  // For image backgrounds
    private static Color4 _backgroundColor;               // For solid color
    private static BackgroundMode _backgroundMode;

    // Stage 4: Animation positioning (ported from AnimationLayerRenderer)
    private static int _animWidth, _animHeight;    // Scaled by FitMode
    private static float _animX, _animY;            // Current position
    private static ContentFitMode _fitMode;
    private static bool _centerInitialPosition;

    // Movement system
    private static MovementConfig? _movementConfig;
    private static int _virtualCanvasWidth = 1920;
    private static int _monitorOffsetX = 0;

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
                    options.LogToStandardErrorThreshold = LogLevel.Trace; // ALL logs to stderr
                    options.FormatterName = "simple";
                });
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;       // No multi-line wrapping
                    options.IncludeScopes = false;
                });
                builder.SetMinimumLevel(LogLevel.Information);
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
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return new IntPtr(1);
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
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = SwapChainFlags.None
        };

        _swapChain = dxgiFactory.CreateSwapChainForHwnd(_d3dDevice, _hwnd, swapChainDesc);
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
                    SetWindowPos(_hwnd, zOrderHwnd, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
                }
                else
                {
                    _zOrderReference = new IntPtr(1); // HWND_BOTTOM
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
                            _swapChain.Present(0, PresentFlags.None);

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
                        _swapChain.Present(0, PresentFlags.None);

                        Thread.Sleep(16);
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
                        shouldComposeNative = _useNativeD2DComposition && isPlay;
                        shouldComposeFallback = compInit && isPlay && !_useNativeD2DComposition;
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

                            // Get current GIF frame (Stage 2) - zero allocation, GPU blit
                            int frameIdx = GetCurrentGifFrameIndex(elapsedMs);
                            var destRect = new System.Drawing.RectangleF(_animX, _animY, _animWidth, _animHeight);

                            _d2dContext.DrawBitmap(
                                _d2dGifFrames[frameIdx],
                                destRect,
                                1.0f,
                                BitmapInterpolationMode.Linear,
                                null);

                            if (_frameCount % 60 == 0)
                            {
                                _logger?.LogInformation("[D2D-NATIVE] Frame #{Frame} | GifFrame: {GifIdx}/{GifTotal} | Pos: ({X:F0},{Y:F0}) | Size: {W}x{H}",
                                    _frameCount, frameIdx, _d2dGifFrames.Length, _animX, _animY, _animWidth, _animHeight);
                            }

                            // Show window on first frame
                            if (!_windowShown)
                            {
                                _windowShown = true;
                                _d2dContext.EndDraw();
                                _d2dContext.Target = null;
                                _swapChain.Present(0, PresentFlags.None);

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
                                    _swapChain.Present(0, PresentFlags.None);

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

                    _d2dContext.EndDraw();
                    _d2dContext.Target = null;
                    _swapChain.Present(0, PresentFlags.None);

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
            }

            Thread.Sleep(16);
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
        _gifNativeWidth = (int)collection[0].Width;
        _gifNativeHeight = (int)collection[0].Height;

        _d2dGifFrames = frames;
        _d2dGifDelays = delays;
        _d2dGifTotalDurationMs = delays.Sum();

        if (zeroDelayCount > 0)
        {
            _logger?.LogInformation("[GIF-D2D] {Count}/{Total} frames had 0-delay, set to 10ms",
                zeroDelayCount, frameCount);
        }

        var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
        long estimatedGpuMB = (long)frameCount * _gifNativeWidth * _gifNativeHeight * 4 / (1024 * 1024);

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
    // Stage 3: D2D Background Rendering
    // ================================

    /// <summary>
    /// Initialize background layer from config. Loads image backgrounds as GPU bitmaps.
    /// </summary>
    private static void InitializeBackground(BackgroundLayerConfig config)
    {
        _backgroundMode = config.Mode;

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

    // ================================
    // Stage 4: Animation Layout & Position
    // ================================

    /// <summary>
    /// Calculate animation dimensions based on FitMode (ported from AnimationLayerRenderer).
    /// </summary>
    private static void CalculateAnimationLayout(AnimationLayerConfig config)
    {
        int nativeWidth = _gifNativeWidth;
        int nativeHeight = _gifNativeHeight;

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

        _logger?.LogInformation("[LAYOUT] Animation: {W}x{H} at ({X:F0},{Y:F0}) | FitMode: {Fit} | Native: {NW}x{NH} | Screen: {SW}x{SH}",
            _animWidth, _animHeight, _animX, _animY, config.FitMode, nativeWidth, nativeHeight, _width, _height);
    }

    /// <summary>
    /// Update animation position based on elapsed time.
    /// Uses MovementCalculator when available, falls back to legacy linear scroll.
    /// </summary>
    private static void UpdateAnimationPosition(long elapsedMs)
    {
        if (_movementConfig != null && _movementConfig.Type != MovementType.Static)
        {
            var (vx, vy) = MovementCalculator.Calculate(
                _movementConfig, elapsedMs,
                _animWidth, _animHeight,
                _virtualCanvasWidth, _height);
            _animX = vx - _monitorOffsetX;
            _animY = vy;
        }
        else if (_pixelsPerSecond > 0)
        {
            // Legacy backward compat: simple left-to-right scroll
            var elapsedSeconds = elapsedMs / 1000.0;
            _animX = (float)(-_animWidth + (elapsedSeconds * _pixelsPerSecond));
        }
        // For static animations (pixelsPerSecond=0 and no movement config), position stays at initial centered value
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

                var filePath = cmd.AnimationConfig.AnimationPath;
                var extension = Path.GetExtension(filePath).ToLowerInvariant();

                if (extension == ".gif")
                {
                    // Stage 2: Native D2D path for GIFs
                    _gifSpeedMultiplier = cmd.AnimationConfig.SpeedMultiplier;
                    _logger?.LogInformation("[LOAD] GIF detected, using native D2D composition (SpeedMultiplier: {Speed}x)", _gifSpeedMultiplier);

                    // Extract GIF frames to GPU
                    ExtractGifFramesToD2D(filePath);

                    // Initialize background (Stage 3)
                    InitializeBackground(cmd.BackgroundConfig);

                    // Calculate animation layout (Stage 4)
                    CalculateAnimationLayout(cmd.AnimationConfig);

                    _useNativeD2DComposition = true;
                    _logger?.LogInformation("[LOAD] Native D2D composition ready: {Frames} frames, {W}x{H}", _d2dGifFrames?.Length, _animWidth, _animHeight);
                }
                else
                {
                    // Video files: use existing CompositionRenderer fallback
                    _logger?.LogInformation("[LOAD] Video detected ({Ext}), using CompositionRenderer fallback", extension);

                    var screenConfig = new ScreenConfiguration
                    {
                        ClientId = "D2DPlayer",
                        Order = 0,
                        Width = _width,
                        Height = _height,
                        PhysicalDistanceCm = 0,
                        Hostname = "localhost",
                        MonitorIndex = cmd.MonitorIndex
                    };

                    _canvasManager = new VirtualCanvasManager(
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<VirtualCanvasManager>.Instance);
                    _canvasManager.CalculateLayout(new[] { screenConfig });

                    var loggerFactory = LoggerFactory.Create(builder =>
                    {
                        builder.AddConsole(options =>
                        {
                            options.LogToStandardErrorThreshold = LogLevel.Trace;
                            options.FormatterName = "simple";
                        });
                        builder.AddSimpleConsole(options =>
                        {
                            options.SingleLine = true;
                            options.IncludeScopes = false;
                        });
                        builder.SetMinimumLevel(LogLevel.Information);
                    });

                    _compositionRenderer = new CompositionRenderer(
                        loggerFactory.CreateLogger<CompositionRenderer>(),
                        loggerFactory);

                    var initTask = _compositionRenderer.InitializeAsync(
                        _canvasManager,
                        cmd.BackgroundConfig,
                        cmd.AnimationConfig,
                        cmd.MonitorIndex);
                    initTask.Wait();

                    _compositionInitialized = true;
                    _logger?.LogInformation("CompositionRenderer fallback initialized");
                }
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
                if (!_compositionInitialized && !_useNativeD2DComposition)
                {
                    _logger?.LogError("[START-CMD] FAILED: Neither native D2D nor composition initialized!");
                    throw new InvalidOperationException("No composition initialized. Call LOAD_ANIMATION first.");
                }

                _startTimestampMs = cmd.StartTimestampMs;
                _pixelsPerSecond = cmd.PixelsPerSecond;
                _renderLoopStart = DateTime.UtcNow;
                _isPlaying = true;

                // Recalculate initial position with updated pixelsPerSecond
                if (_useNativeD2DComposition && _animationConfig != null)
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

        _backgroundImageBitmap?.Dispose();
        _backgroundImageBitmap = null;
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

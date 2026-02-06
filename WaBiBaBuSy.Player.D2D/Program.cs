using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
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

    // Direct3D/Direct2D
    private static ID3D11Device? _d3dDevice;
    private static ID3D11DeviceContext? _immediateContext;
    private static IDXGISwapChain1? _swapChain;
    private static ID2D1Factory1? _d2dFactory;
    private static ID2D1RenderTarget? _d2dRenderTarget;
    private static RenderTargetProperties _renderTargetProps; // Stored for recreating render target after Present()

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

    // Composition system
    private static CompositionRenderer? _compositionRenderer;
    private static VirtualCanvasManager? _canvasManager;
    private static AnimationLayerConfig? _animationConfig;
    private static BackgroundLayerConfig? _backgroundConfig;
    private static readonly object _compositionLock = new();
    private static volatile bool _compositionInitialized = false;

    // Animation state
    private static volatile bool _isPlaying = false;
    private static long _startTimestampMs = 0;
    private static int _pixelsPerSecond = 0;
    private static DateTime _renderLoopStart = DateTime.MinValue;
    private static long _frameCount = 0; // TASK-008 VERIFICATION: Track render loop iterations
    private static DateTime _lastLoopLogTime = DateTime.MinValue; // TASK-008: Track when we last logged loop status

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

            // Create D3D/D2D resources
            CreateD3DDevice();
            CreateSwapChain();
            CreateD2DRenderTarget();

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
            SwapEffect = SwapEffect.FlipSequential, // Changed from FlipDiscard - Test for Win11 24H2 compatibility
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = SwapChainFlags.None
        };

        _swapChain = dxgiFactory.CreateSwapChainForHwnd(_d3dDevice, _hwnd, swapChainDesc);
    }

    private static void CreateD2DRenderTarget()
    {
        if (_swapChain == null)
        {
            throw new InvalidOperationException("Swap chain not initialized");
        }

        _d2dFactory = Vortice.Direct2D1.D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);

        // Store render target properties for recreation after each Present() (flip model buffer rotation)
        _renderTargetProps = new RenderTargetProperties
        {
            Type = RenderTargetType.Hardware,
            PixelFormat = new Vortice.DCommon.PixelFormat(
                Format.B8G8R8A8_UNorm,
                Vortice.DCommon.AlphaMode.Ignore),
            DpiX = 96.0f,
            DpiY = 96.0f
        };

        using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
        _d2dRenderTarget = _d2dFactory.CreateDxgiSurfaceRenderTarget(backBuffer, _renderTargetProps);
    }

    /// <summary>
    /// Recreates the D2D render target after Present() to handle DXGI flip model buffer rotation.
    /// With FlipSequential swap effect, buffers rotate after each Present() call.
    /// The render target must be recreated to bind to the new back buffer.
    /// </summary>
    private static void RecreateD2DRenderTarget()
    {
        if (_swapChain == null || _d2dFactory == null)
            return;

        try
        {
            // Dispose old render target
            _d2dRenderTarget?.Dispose();
            _d2dRenderTarget = null;

            // Create new render target bound to current back buffer
            // Note: The surface can be disposed after CreateDxgiSurfaceRenderTarget - D2D keeps its own reference
            using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
            _d2dRenderTarget = _d2dFactory.CreateDxgiSurfaceRenderTarget(backBuffer, _renderTargetProps);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[CRITICAL] RecreateD2DRenderTarget failed!");
            throw; // Re-throw to see the actual crash
        }
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
                // This is the official Microsoft guidance for "raised desktop" compatibility.
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
            // TASK-008 DEBUG: Log every second to verify loop is running
            var now = DateTime.UtcNow;
            if ((now - _lastLoopLogTime).TotalSeconds >= 1.0)
            {
                _lastLoopLogTime = now;
                bool composing = false;
                lock (_compositionLock)
                {
                    composing = _compositionInitialized && _isPlaying;
                }
                _logger?.LogInformation("[D2D-LOOP] Render loop alive! Frame #{Count} | Composing: {Composing} | WindowShown: {Shown}",
                    _frameCount, composing, _windowShown);
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

                if (_d2dRenderTarget != null && _swapChain != null)
                {
                    _d2dRenderTarget.BeginDraw();

                    // TEST MODE: Simple color toggle to verify swap chain works
                    if (_testModeEnabled)
                    {
                        var timeSinceToggle = (DateTime.UtcNow - _lastColorToggle).TotalSeconds;
                        bool isRed = ((int)(timeSinceToggle / 2.0)) % 2 == 0;
                        var testColor = isRed ? new Color4(1, 0, 0, 1) : new Color4(0, 0, 1, 1); // Red or Blue
                        _d2dRenderTarget.Clear(testColor);

                        if (_frameCount % 60 == 0) // Log every 60 frames (~1 second)
                        {
                            _logger?.LogInformation("[TEST MODE] Frame #{Frame} | Color: {Color} | TimeSinceToggle: {Time:F1}s",
                                _frameCount, isRed ? "RED" : "BLUE", timeSinceToggle);
                        }

                        // Show window if not shown
                        if (!_windowShown)
                        {
                            _windowShown = true;
                            _d2dRenderTarget.EndDraw(out _, out _);
                            _swapChain.Present(0, PresentFlags.None);
                            RecreateD2DRenderTarget();

                            if (_zOrderReference != IntPtr.Zero)
                                SetWindowPos(_hwnd, _zOrderReference, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                            else
                                ShowWindow(_hwnd, 5);
                            UpdateWindow(_hwnd);
                            _logger?.LogInformation("[TEST MODE] Window shown");
                            continue;
                        }

                        _d2dRenderTarget.EndDraw(out _, out _);
                        _swapChain.Present(0, PresentFlags.None);
                        RecreateD2DRenderTarget();

                        Thread.Sleep(16);
                        _frameCount++;
                        continue; // Skip normal rendering
                    }

                    // Check if composition is initialized and playing
                    bool shouldCompose = false;
                    bool compInit = false;
                    bool isPlay = false;
                    lock (_compositionLock)
                    {
                        compInit = _compositionInitialized;
                        isPlay = _isPlaying;
                        shouldCompose = compInit && isPlay;
                    }

                    // DIAGNOSTIC: Log composition state every 10 frames (more frequent for debugging)
                    if (_frameCount % 60 == 0) // Log every 60 frames (~1 second) to reduce noise
                    {
                        _logger?.LogInformation("[RENDER-LOOP] Frame #{Frame} | Initialized: {Init} | Playing: {Play} | ShouldCompose: {ShouldCompose}",
                            _frameCount, compInit, isPlay, shouldCompose);
                    }

                    if (shouldCompose && _compositionRenderer != null && _canvasManager != null)
                    {
                        try
                        {
                            // Calculate current timestamp
                            var elapsedMs = (long)(DateTime.UtcNow - _renderLoopStart).TotalMilliseconds;
                            var currentTimestampMs = _startTimestampMs + elapsedMs;

                            // DIAGNOSTIC: Log timestamp every 60 frames (~1 second) to reduce noise
                            if (_frameCount % 60 == 0)
                            {
                                _logger?.LogInformation("[TIMESTAMP] Frame #{Frame} | Elapsed: {Elapsed}ms | CurrentTimestamp: {Timestamp}ms | PPS: {PPS}",
                                    _frameCount, elapsedMs, currentTimestampMs, _pixelsPerSecond);
                            }

                            // CRITICAL: Update animation position BEFORE composing
                            _compositionRenderer.UpdateAnimationPosition(currentTimestampMs, _pixelsPerSecond);

                            // Compose frame for this screen
                            var screen = _canvasManager.ScreenMappings[0]; // Single screen for this player
                            using var composedFrame = _compositionRenderer.ComposeForScreen(screen);

                            // DIAGNOSTIC: Log composed frame info every 30 frames
                            if (_frameCount % 30 == 0)
                            {
                                // Sample center pixel of composed frame to verify it's changing
                                int centerX = composedFrame.Width / 2;
                                int centerY = composedFrame.Height / 2;
                                var pixel = composedFrame.GetPixel(centerX, centerY);
                                _logger?.LogInformation("[COMPOSE] Frame #{Frame} | Size: {W}x{H} | CenterPixel: R={R} G={G} B={B}",
                                    _frameCount, composedFrame.Width, composedFrame.Height, pixel.R, pixel.G, pixel.B);
                            }

                            // Convert System.Drawing.Bitmap to D2D bitmap
                            using var d2dBitmap = ConvertBitmapToD2D(composedFrame);

                            // Draw the bitmap
                            if (d2dBitmap != null)
                            {
                                var destRect = new System.Drawing.RectangleF(0, 0, _width, _height);
                                _d2dRenderTarget.DrawBitmap(
                                    d2dBitmap,
                                    destRect,      // destination rectangle
                                    1.0f,          // opacity
                                    BitmapInterpolationMode.Linear,
                                    null);         // source rectangle (null = entire bitmap)

                                // Show window on first frame
                                if (!_windowShown)
                                {
                                    _windowShown = true;
                                    _d2dRenderTarget.EndDraw(out _, out _);
                                    // TASK-008 FIX: Use Present(0, ...) - no vsync wait
                                    _swapChain.Present(0, PresentFlags.None);

                                    // CRITICAL FIX: Recreate D2D render target after Present() for DXGI flip model
                                    RecreateD2DRenderTarget();

                                    if (_zOrderReference != IntPtr.Zero)
                                    {
                                        SetWindowPos(_hwnd, _zOrderReference, 0, 0, 0, 0,
                                            SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                                    }
                                    else
                                    {
                                        ShowWindow(_hwnd, 5); // SW_SHOW
                                    }

                                    UpdateWindow(_hwnd);
                                    _logger?.LogInformation("Window shown after first frame");
                                    continue;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Composition error");
                            // Fall through to color fill
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
                        _d2dRenderTarget.Clear(color);
                    }

                    _d2dRenderTarget.EndDraw(out _, out _);

                    // TASK-008 FIX: Use Present(0, ...) for immediate present without vsync wait
                    // Windows 11 24H2 has issues with vsync on desktop-parented windows
                    _swapChain.Present(0, PresentFlags.None);

                    // CRITICAL FIX: Recreate D2D render target after Present() for DXGI flip model.
                    // With FlipSequential swap effect, buffers rotate after each Present() call.
                    // The render target was bound to the old back buffer (now front buffer).
                    // We must recreate it to bind to the new back buffer.
                    // Without this, every frame after the first draws to the front buffer (already displayed).
                    RecreateD2DRenderTarget();

                    // TASK-008 VERIFICATION: Log every 60 frames to confirm Present() is being called
                    if (_frameCount % 60 == 0 && _frameCount > 0)
                    {
                        _logger?.LogInformation("[D2D-PRESENT] Frame #{Frame} presented to swap chain | Composing: {Composing}",
                            _frameCount, shouldCompose);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Render loop error");
            }

            // Adaptive sleep based on content type
            // For 60 FPS content: sleep 16ms
            // For 10 FPS GIFs: sleep can be longer (e.g., 50-100ms)
            // Using 16ms ensures we check for new frames frequently while not wasting CPU
            Thread.Sleep(16);

            // TASK-008 VERIFICATION: Increment frame counter
            _frameCount++;
        }

        _logger?.LogInformation("Render loop stopped");
    }

    private static ID2D1Bitmap? ConvertBitmapToD2D(Bitmap gdiBitmap)
    {
        if (_d2dRenderTarget == null)
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

                var d2dBitmap = _d2dRenderTarget.CreateBitmap(
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
                    // Enable test mode: toggle between red and blue every 2 seconds
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
                    // JSON message - parse and handle
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
            // Use MessageTypeWrapper to extract MessageType without deserializing the whole object
            var wrapper = JsonConvert.DeserializeObject<MessageTypeWrapper>(json);
            if (wrapper == null || string.IsNullOrEmpty(wrapper.MessageType))
            {
                Console.WriteLine("ERROR:Failed to parse JSON message or missing MessageType");
                Console.Out.Flush();
                return;
            }

            _logger?.LogDebug("JSON message type: {MessageType}", wrapper.MessageType);

            // Deserialize to the correct concrete type based on MessageType
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
                // Dispose existing composition
                _compositionRenderer?.Dispose();
                _compositionRenderer = null;
                _canvasManager = null;
                _compositionInitialized = false;

                // Store configuration
                _animationConfig = cmd.AnimationConfig;
                _backgroundConfig = cmd.BackgroundConfig;

                // Create virtual canvas for this screen
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

                // Create composition renderer
                var loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.AddConsole(options =>
                    {
                        options.LogToStandardErrorThreshold = LogLevel.Trace; // ALL logs to stderr
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

                // Initialize composition renderer
                var initTask = _compositionRenderer.InitializeAsync(
                    _canvasManager,
                    cmd.BackgroundConfig,
                    cmd.AnimationConfig,
                    cmd.MonitorIndex);

                initTask.Wait(); // Synchronous wait on background thread

                _compositionInitialized = true;
                _logger?.LogInformation("Composition initialized successfully");
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
            _logger?.LogInformation("[START-CMD] RECEIVED START ANIMATION COMMAND");
            _logger?.LogInformation("[START-CMD] StartTimestamp: {Timestamp}ms, PixelsPerSecond: {PPS}px/s",
                cmd.StartTimestampMs, cmd.PixelsPerSecond);

            lock (_compositionLock)
            {
                if (!_compositionInitialized || _compositionRenderer == null)
                {
                    _logger?.LogError("[START-CMD] FAILED: Composition not initialized!");
                    throw new InvalidOperationException("Composition not initialized. Call LOAD_ANIMATION first.");
                }

                _startTimestampMs = cmd.StartTimestampMs;
                _pixelsPerSecond = cmd.PixelsPerSecond;
                _renderLoopStart = DateTime.UtcNow; // Reset render loop timer
                _isPlaying = true;

                _logger?.LogInformation("[START-CMD] SUCCESS: _isPlaying = TRUE, _pixelsPerSecond = {PPS}", _pixelsPerSecond);
            }

            Console.WriteLine("READY");
            Console.Out.Flush();
            _logger?.LogInformation("[START-CMD] Animation started - render loop should now compose frames!");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "🎬 [START-CMD] EXCEPTION during start animation");
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

    private static void Cleanup()
    {
        _logger?.LogInformation("Cleanup starting");
        _running = false;

        // Dispose composition
        lock (_compositionLock)
        {
            _compositionRenderer?.Dispose();
            _compositionRenderer = null;
            _canvasManager = null;
            _compositionInitialized = false;
        }

        // Dispose D2D/D3D resources
        _d2dRenderTarget?.Dispose();
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

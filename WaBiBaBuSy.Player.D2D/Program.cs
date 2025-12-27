using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using DriverType = Vortice.Direct3D.DriverType;

namespace WaBiBaBuSy.Player.D2D;

/// <summary>
/// Separate process for DXGI/Direct2D rendering.
/// This process creates a window that can be safely parented to the desktop
/// by the main WaBiBaBuSy application without crashing explorer.exe.
///
/// IPC Protocol (stdin/stdout):
/// - On startup: outputs "HWND:&lt;handle&gt;" when window is ready
/// - Commands (stdin):
///   - "COLOR:RRGGBB" - Fill with solid color
///   - "FILE:&lt;path&gt;" - Load and display image file
///   - "EXIT" - Clean shutdown
/// - Responses (stdout):
///   - "READY" - Command completed
///   - "ERROR:&lt;message&gt;" - Error occurred
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

    // State
    private static int _width;
    private static int _height;
    private static volatile bool _running = true;
    private static readonly object _colorLock = new();
    private static Color4 _currentColor = new(1, 0, 0, 1); // Default red

    // Pending PARENT command to be processed on main thread
    private static volatile string? _pendingParentCommand = null;
    private static readonly object _parentLock = new();

    static int Main(string[] args)
    {
        try
        {
            // Parse command line: --bounds x,y,width,height
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
            }

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
            // PARENT commands will be queued and processed on main thread in render loop
            var commandThread = new Thread(ProcessCommands) { IsBackground = true };
            commandThread.Start();

            // Run render loop with message pump on MAIN thread
            // This also processes pending PARENT commands to ensure thread affinity
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

            Console.Error.WriteLine($"DEBUG: Window class registered with atom: {_classAtom}");

            _hwnd = CreateWindowExW(
                WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                _windowClassName,
                "WaBiBaBuSy Player",
                WS_POPUP | WS_VISIBLE,
                x, y, width, height,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Exception($"Failed to create window. Error: {error}");
            }

            Console.Error.WriteLine($"DEBUG: Window created: {_hwnd}");

            ShowWindow(_hwnd, 5); // SW_SHOW
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
            SwapEffect = SwapEffect.FlipDiscard,
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

        using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);

        var renderTargetProps = new RenderTargetProperties
        {
            Type = RenderTargetType.Hardware,
            PixelFormat = new Vortice.DCommon.PixelFormat(
                Format.B8G8R8A8_UNorm,
                Vortice.DCommon.AlphaMode.Ignore),
            DpiX = 96.0f,
            DpiY = 96.0f
        };

        _d2dRenderTarget = _d2dFactory.CreateDxgiSurfaceRenderTarget(backBuffer, renderTargetProps);
    }

    /// <summary>
    /// Processes the PARENT command on the calling thread.
    /// </summary>
    private static void ProcessParentCommand(string line)
    {
        var parts = line.Substring(7).Split(',');
        if (parts.Length >= 1)
        {
            try
            {
                var parentHwnd = new IntPtr(long.Parse(parts[0]));
                var zOrderHwnd = parts.Length >= 2 ? new IntPtr(long.Parse(parts[1])) : IntPtr.Zero;

                Console.Error.WriteLine($"DEBUG: Parenting to {parentHwnd}, z-order below {zOrderHwnd}");

                // Add WS_EX_TRANSPARENT for mouse pass-through
                var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT);
                Console.Error.WriteLine($"DEBUG: Added WS_EX_TRANSPARENT");

                // FIRST: SetParent to make us a sibling of DefView
                var prevParent = SetParent(_hwnd, parentHwnd);
                Console.Error.WriteLine($"DEBUG: SetParent result: prev={prevParent}, error={Marshal.GetLastWin32Error()}");

                // THEN: Position behind DefView (now that we're siblings)
                // When hWndInsertAfter is a window handle, we're placed AFTER it in z-order (behind it visually)
                if (zOrderHwnd != IntPtr.Zero)
                {
                    SetWindowPos(_hwnd, zOrderHwnd, 0, 0, _width, _height, SWP_NOACTIVATE);
                    Console.Error.WriteLine($"DEBUG: Positioned behind {zOrderHwnd} (DefView) - should be BEHIND icons");
                }
                else
                {
                    var HWND_BOTTOM = new IntPtr(1);
                    SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, _width, _height, SWP_NOACTIVATE);
                    Console.Error.WriteLine($"DEBUG: Positioned at HWND_BOTTOM");
                }

                // Show window
                ShowWindow(_hwnd, 5); // SW_SHOW
                UpdateWindow(_hwnd);
                Console.Error.WriteLine($"DEBUG: Called ShowWindow");

                Console.WriteLine("READY");
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
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
        Console.Error.WriteLine("DEBUG: RenderLoop started");
        Console.Error.Flush();

        while (_running)
        {
            try
            {
                // Process Windows messages (CRITICAL for window stability!)
                // Limit to 100 messages per frame to avoid infinite loops
                int msgCount = 0;
                while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE) && msgCount < 100)
                {
                    msgCount++;
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                // Check for pending PARENT command - must be processed on main thread
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
                    Color4 color;
                    lock (_colorLock)
                    {
                        color = _currentColor;
                    }

                    _d2dRenderTarget.BeginDraw();
                    _d2dRenderTarget.Clear(color);
                    _d2dRenderTarget.EndDraw(out _, out _);
                    _swapChain.Present(1, PresentFlags.None);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Render error: {ex.Message}");
                Console.Error.WriteLine($"Render error stack: {ex.StackTrace}");
            }

            Thread.Sleep(16); // ~60 FPS
        }

        Console.Error.WriteLine("DEBUG: RenderLoop exited");
    }

    private static void ProcessCommands()
    {
        while (_running)
        {
            try
            {
                var line = Console.ReadLine();
                if (line == null)
                {
                    // Stdin closed - parent process likely exited
                    _running = false;
                    break;
                }

                line = line.Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                if (line.StartsWith("PARENT:"))
                {
                    // Queue PARENT command to be processed on main thread (render loop)
                    // This ensures window operations happen on the owning thread
                    Console.Error.WriteLine("DEBUG: Queuing PARENT command for main thread");
                    lock (_parentLock)
                    {
                        _pendingParentCommand = line;
                    }
                    // Don't send READY yet - will be sent after main thread processes it
                }
                else if (line.StartsWith("COLOR:"))
                {
                    // Parse hex color: COLOR:FF0000
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

                        Console.Error.WriteLine($"DEBUG: Color changed to R={r} G={g} B={b}");
                        Console.WriteLine("READY");
                        Console.Out.Flush();
                    }
                    else
                    {
                        Console.WriteLine("ERROR:Invalid color format");
                        Console.Out.Flush();
                    }
                }
                else if (line == "EXIT")
                {
                    _running = false;
                    Console.WriteLine("READY");
                    Console.Out.Flush();
                }
                else
                {
                    Console.WriteLine($"ERROR:Unknown command: {line}");
                    Console.Out.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR:{ex.Message}");
                Console.Out.Flush();
            }
        }
    }

    private static void Cleanup()
    {
        _running = false;

        _d2dRenderTarget?.Dispose();
        _swapChain?.Dispose();
        _immediateContext?.Dispose();
        _d3dDevice?.Dispose();
        _d2dFactory?.Dispose();

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
    }
}

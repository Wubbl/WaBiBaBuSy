using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.DCommon;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Native;
using DXGIAlphaMode = Vortice.DXGI.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace WaBiBaBuSy.WallpaperEngine.Direct2D;

/// <summary>
/// Hardware-accelerated Direct2D renderer using Vortice.Windows with DXGI swap chain.
/// Renders composed wallpaper frames to the desktop via GPU acceleration.
/// Uses native Win32 window (no Windows Forms dependency).
/// </summary>
public class D2DVorticeRenderer : IDisposable
{
    private readonly ILogger<D2DVorticeRenderer> _logger;
    private readonly DesktopWindowManager _desktopWindowManager;
    private readonly ScreenMapping _screen;

    // Native window
    private IntPtr _hwnd = IntPtr.Zero;
    private string _windowClassName = "";
    private Win32Interop.WndProc? _wndProcDelegate;
    private ushort _classAtom;

    // Direct3D11 & DXGI
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _immediateContext;
    private IDXGISwapChain1? _swapChain;

    // Direct2D
    private ID2D1Factory1? _d2dFactory;
    private ID2D1RenderTarget? _d2dRenderTarget;

    // State
    private bool _isInitialized;
    private bool _disposed;
    private int _width;
    private int _height;

    public D2DVorticeRenderer(
        ScreenMapping screen,
        ILogger<D2DVorticeRenderer> logger,
        DesktopWindowManager desktopWindowManager)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _desktopWindowManager = desktopWindowManager ?? throw new ArgumentNullException(nameof(desktopWindowManager));

        _width = screen.ScreenBounds.Width;
        _height = screen.ScreenBounds.Height;
    }

    /// <summary>
    /// Initializes the Direct3D11 device, DXGI swap chain, and Direct2D render target.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized)
        {
            _logger.LogWarning("D2DVorticeRenderer already initialized");
            return;
        }

        try
        {
            _logger.LogInformation("Initializing D2DVorticeRenderer for screen {Order} ({Width}x{Height})",
                _screen.Order, _width, _height);

            // Step 1: Create native Win32 window
            CreateNativeWindow();

            // Step 2: Create Direct3D11 device with BGRA support
            CreateD3DDevice();

            // Step 3: Create DXGI swap chain
            CreateSwapChain();

            // Step 4: Create Direct2D factory and render target
            CreateD2DRenderTarget();

            // Step 5: Parent window to WorkerW desktop
            ParentToDesktop();

            _isInitialized = true;
            _logger.LogInformation("D2DVorticeRenderer initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize D2DVorticeRenderer");
            Dispose();
            throw;
        }
    }

    private void CreateNativeWindow()
    {
        _logger.LogInformation("Creating native Win32 window...");

        // Generate unique class name
        _windowClassName = $"WaBiBaBuSyD2DRenderer_{Guid.NewGuid():N}";

        // Get module handle
        var hInstance = Win32Interop.GetModuleHandle(null);

        // Create window procedure delegate (must keep reference to prevent GC)
        _wndProcDelegate = WindowProc;

        // Register window class
        var wndClass = new Win32Interop.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32Interop.WNDCLASSEX>(),
            style = Win32Interop.CS_HREDRAW | Win32Interop.CS_VREDRAW | Win32Interop.CS_OWNDC,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = hInstance,
            hIcon = IntPtr.Zero,
            hCursor = Win32Interop.LoadCursor(IntPtr.Zero, Win32Interop.IDC_ARROW),
            hbrBackground = IntPtr.Zero,  // No background brush (we'll render everything)
            lpszMenuName = null,
            lpszClassName = _windowClassName,
            hIconSm = IntPtr.Zero
        };

        _classAtom = Win32Interop.RegisterClassEx(ref wndClass);
        if (_classAtom == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Exception($"Failed to register window class. Error: {error}");
        }

        _logger.LogDebug("Window class registered: {ClassName} (atom: {Atom})", _windowClassName, _classAtom);

        // Create window - initially without WS_EX_TRANSPARENT
        // (Will be added later ONLY for legacy mode, not layered mode)
        _hwnd = Win32Interop.CreateWindowEx(
            Win32Interop.WS_EX_NOACTIVATE,  // No activation, but NO transparent flag yet
            _windowClassName,
            "WaBiBaBuSy Wallpaper",
            Win32Interop.WS_POPUP | Win32Interop.WS_VISIBLE | Win32Interop.WS_CLIPCHILDREN | Win32Interop.WS_CLIPSIBLINGS,
            _screen.ScreenBounds.X,
            _screen.ScreenBounds.Y,
            _screen.ScreenBounds.Width,
            _screen.ScreenBounds.Height,
            IntPtr.Zero,  // No parent yet
            IntPtr.Zero,  // No menu
            hInstance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Exception($"Failed to create window. Error: {error}");
        }

        _logger.LogInformation("Native window created: HWND={Handle}, Bounds={Bounds}",
            _hwnd, _screen.ScreenBounds);
    }

    /// <summary>
    /// Minimal window procedure - handles only essential messages.
    /// No message pumping required - DefWindowProc handles everything.
    /// NOTE: In Windows 11 24H2+ layered mode, we rely on Z-ordering,
    /// not WM_NCHITTEST, to ensure mouse input reaches desktop icons.
    /// </summary>
    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Interop.WM_PAINT:
                // Let Direct2D handle all rendering
                return IntPtr.Zero;

            case Win32Interop.WM_ERASEBKGND:
                // Prevent flicker - we render everything ourselves
                return new IntPtr(1);

            case Win32Interop.WM_DESTROY:
                _logger.LogDebug("Window {Handle} received WM_DESTROY", hWnd);
                return IntPtr.Zero;

            default:
                // Let Windows handle all other messages (including WM_NCHITTEST)
                // In layered mode, proper Z-ordering ensures input routes correctly
                return Win32Interop.DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private void CreateD3DDevice()
    {
        var creationFlags = DeviceCreationFlags.BgraSupport;

#if DEBUG
        creationFlags |= DeviceCreationFlags.Debug;
#endif

        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        var result = D3D11.D3D11CreateDevice(
            null,  // Use default adapter
            DriverType.Hardware,
            creationFlags,
            featureLevels,
            out _d3dDevice,
            out var featureLevel,
            out _immediateContext);

        if (result.Failure)
        {
            throw new Exception($"Failed to create D3D11 device: {result}");
        }

        _logger.LogDebug("D3D11 device created with feature level: {FeatureLevel}", featureLevel);
    }

    private void CreateSwapChain()
    {
        if (_d3dDevice == null || _hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("D3D device or window handle not initialized");
        }

        // Get DXGI device and factory
        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        using var dxgiAdapter = dxgiDevice.GetAdapter();
        using var dxgiFactory = dxgiAdapter.GetParent<IDXGIFactory2>();

        // Create swap chain description
        var swapChainDesc = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,  // BGRA for Direct2D compatibility
            BufferCount = 2,  // Double buffering
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),  // No MSAA
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,  // Modern flip model
            AlphaMode = DXGIAlphaMode.Ignore,
            Flags = SwapChainFlags.None
        };

        _swapChain = dxgiFactory.CreateSwapChainForHwnd(
            _d3dDevice,
            _hwnd,
            swapChainDesc);

        _logger.LogDebug("DXGI swap chain created: {Width}x{Height}, Format: {Format}",
            _width, _height, swapChainDesc.Format);
    }

    private void CreateD2DRenderTarget()
    {
        if (_swapChain == null)
        {
            throw new InvalidOperationException("Swap chain not initialized");
        }

        // Create Direct2D factory
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);

        // Get back buffer surface from swap chain
        using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);

        // Create Direct2D render target from DXGI surface
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

        _logger.LogDebug("Direct2D render target created from DXGI surface");
    }

    private void ParentToDesktop()
    {
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Window handle not initialized");
        }

        var workerW = _desktopWindowManager.FindDesktopWorkerWindow();
        if (workerW != IntPtr.Zero)
        {
            _desktopWindowManager.SetAsWallpaperWindow(_hwnd, _screen.ScreenBounds);

            // Check which mode was used
            bool isLayeredMode = _desktopWindowManager.IsLayeredDesktopMode;
            _logger.LogInformation("Window {Handle} parented using {Mode} mode",
                _hwnd, isLayeredMode ? "LAYERED" : "LEGACY");

            if (!isLayeredMode)
            {
                // LEGACY MODE: Add WS_EX_TRANSPARENT to pass mouse input through
                _logger.LogInformation("LEGACY mode: Adding WS_EX_TRANSPARENT for input pass-through");
                var currentExStyle = Win32Interop.GetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE);
                var newExStyle = currentExStyle | Win32Interop.WS_EX_TRANSPARENT | Win32Interop.WS_EX_NOACTIVATE;
                Win32Interop.SetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE, newExStyle);
                _logger.LogInformation("Applied WS_EX_TRANSPARENT (0x{Old:X} -> 0x{New:X})",
                    currentExStyle, newExStyle);
            }
            else
            {
                // LAYERED MODE (Windows 11 24H2+): DO NOT use WS_EX_TRANSPARENT
                // Z-ordering below SHELLDLL_DefView handles input routing
                _logger.LogInformation("LAYERED mode: Relying on Z-order for input routing (no WS_EX_TRANSPARENT)");
                var currentExStyle = Win32Interop.GetWindowLong(_hwnd, Win32Interop.GWL_EXSTYLE);
                _logger.LogInformation("Current extended style in layered mode: 0x{ExStyle:X}", currentExStyle);
            }
        }
        else
        {
            _logger.LogWarning("Failed to find WorkerW window, wallpaper may not appear correctly");
        }
    }

    /// <summary>
    /// Displays a composed frame to the desktop wallpaper.
    /// </summary>
    /// <param name="frame">The composed bitmap frame to display</param>
    public void DisplayFrame(Bitmap frame)
    {
        if (!_isInitialized || _disposed)
        {
            _logger.LogWarning("Cannot display frame: renderer not initialized or disposed");
            return;
        }

        if (_d2dRenderTarget == null || _swapChain == null)
        {
            _logger.LogError("D2D render target or swap chain is null");
            return;
        }

        try
        {
            // Convert System.Drawing.Bitmap to Direct2D bitmap
            using var d2dBitmap = ConvertToD2DBitmap(frame);

            // Render to DXGI surface
            _d2dRenderTarget.BeginDraw();
            _d2dRenderTarget.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 1));  // Black background

            // Draw bitmap scaled to screen
            var destRect = new System.Drawing.RectangleF(0, 0, _width, _height);
            _d2dRenderTarget.DrawBitmap(
                d2dBitmap,
                destRect,
                1.0f,  // Opacity
                BitmapInterpolationMode.Linear,
                null);

            _d2dRenderTarget.EndDraw(out _, out _);

            // Present to screen (1 = VSync, 0 = immediate)
            _swapChain.Present(1, PresentFlags.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error displaying frame");
        }
    }

    private ID2D1Bitmap ConvertToD2DBitmap(Bitmap source)
    {
        if (_d2dRenderTarget == null)
        {
            throw new InvalidOperationException("D2D render target not initialized");
        }

        // Lock bitmap to access pixel data
        var bitmapData = source.LockBits(
            new Rectangle(0, 0, source.Width, source.Height),
            ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            var bitmapProps = new BitmapProperties
            {
                PixelFormat = new Vortice.DCommon.PixelFormat(
                    Format.B8G8R8A8_UNorm,
                    Vortice.DCommon.AlphaMode.Premultiplied)
            };

            // Create Direct2D bitmap from pixel data
            var d2dBitmap = _d2dRenderTarget.CreateBitmap(
                new Vortice.Mathematics.SizeI(source.Width, source.Height),
                bitmapData.Scan0,
                (uint)bitmapData.Stride,
                bitmapProps);

            return d2dBitmap;
        }
        finally
        {
            source.UnlockBits(bitmapData);
        }
    }

    /// <summary>
    /// Handles window resize events (if needed for dynamic resolution changes).
    /// </summary>
    public void OnResize(int newWidth, int newHeight)
    {
        if (_disposed || _swapChain == null || _d2dFactory == null)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Resizing swap chain to {Width}x{Height}", newWidth, newHeight);

            // Dispose old render target
            _d2dRenderTarget?.Dispose();
            _d2dRenderTarget = null;

            // Resize swap chain buffers
            _swapChain.ResizeBuffers(
                2,  // Buffer count
                (uint)newWidth,
                (uint)newHeight,
                Format.B8G8R8A8_UNorm,
                SwapChainFlags.None);

            // Recreate Direct2D render target
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

            _width = newWidth;
            _height = newHeight;

            _logger.LogDebug("Swap chain resized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resizing swap chain");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _logger.LogInformation("Disposing D2DVorticeRenderer");

        _d2dRenderTarget?.Dispose();
        _d2dRenderTarget = null;

        _swapChain?.Dispose();
        _swapChain = null;

        _immediateContext?.Dispose();
        _immediateContext = null;

        _d3dDevice?.Dispose();
        _d3dDevice = null;

        _d2dFactory?.Dispose();
        _d2dFactory = null;

        // Destroy native window
        if (_hwnd != IntPtr.Zero)
        {
            Win32Interop.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            _logger.LogDebug("Native window destroyed");
        }

        // Unregister window class
        if (_classAtom != 0)
        {
            var hInstance = Win32Interop.GetModuleHandle(null);
            Win32Interop.UnregisterClass(_windowClassName, hInstance);
            _classAtom = 0;
            _logger.LogDebug("Window class unregistered: {ClassName}", _windowClassName);
        }

        _disposed = true;
        _isInitialized = false;

        _logger.LogDebug("D2DVorticeRenderer disposed");
    }
}

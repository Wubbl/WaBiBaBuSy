using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Direct2D;

/// <summary>
/// GDI+ renderer that displays pre-composed bitmap frames using native Win32 windows.
/// Creates a dedicated rendering window parented to Progman (like standalone renderers).
/// This approach is required for Windows 11 24H2+ layered desktop mode.
/// Note: Despite the name, this uses GDI+ (not Direct2D) for backwards compatibility.
/// </summary>
public class Direct2DRenderer : IDisposable
{
    private readonly ILogger<Direct2DRenderer> _logger;
    private readonly DesktopWindowManager _desktopWindowManager;
    private bool _disposed;

    // Native window
    private IntPtr _hwnd = IntPtr.Zero;
    private string _windowClassName = "";
    private Win32Interop.WndProc? _wndProcDelegate;
    private ushort _classAtom;
    private bool _windowInitialized;

    // Rendering state
    private ScreenMapping? _currentScreen;
    private Bitmap? _currentFrame;

    // Frame buffer cache
    private readonly Dictionary<string, Bitmap> _previousFrames = new();

    public Direct2DRenderer(
        ILogger<Direct2DRenderer> logger,
        DesktopWindowManager desktopWindowManager)
    {
        _logger = logger;
        _desktopWindowManager = desktopWindowManager;
    }

    /// <summary>
    /// Initialize the rendering window for a specific screen.
    /// Must be called before DisplayFrame.
    /// </summary>
    public void Initialize(ScreenMapping screen)
    {
        if (_windowInitialized)
        {
            _logger.LogWarning("[Direct2D] Renderer already initialized");
            return;
        }

        _logger.LogInformation("[Direct2D] Initializing render window for screen {Order}", screen.Order);

        _currentScreen = screen;

        // Create native Win32 window
        CreateNativeWindow(screen.ScreenBounds);

        // Parent to desktop
        ParentToDesktop(screen.ScreenBounds);

        _windowInitialized = true;
        _logger.LogInformation("[Direct2D] Render window initialized successfully");
    }

    private void CreateNativeWindow(Rectangle bounds)
    {
        _logger.LogInformation("[Direct2D] Creating native Win32 window...");

        // Generate unique class name
        _windowClassName = $"WaBiBaBuSyDirect2DRenderer_{Guid.NewGuid():N}";

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
            hbrBackground = IntPtr.Zero,  // No background brush (we paint everything)
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

        _logger.LogDebug("[Direct2D] Window class registered: {ClassName} (atom: {Atom})", _windowClassName, _classAtom);

        // Create window with WS_EX_TRANSPARENT to let mouse clicks pass through to desktop icons
        _hwnd = Win32Interop.CreateWindowEx(
            Win32Interop.WS_EX_NOACTIVATE | Win32Interop.WS_EX_TRANSPARENT,  // CRITICAL: Allow input to pass through
            _windowClassName,
            "WaBiBaBuSy Wallpaper",
            Win32Interop.WS_POPUP | Win32Interop.WS_VISIBLE | Win32Interop.WS_CLIPCHILDREN | Win32Interop.WS_CLIPSIBLINGS,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            IntPtr.Zero,  // No parent yet
            IntPtr.Zero,  // No menu
            hInstance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Exception($"Failed to create window. Error: {error}");
        }

        _logger.LogInformation("[Direct2D] Native window created: HWND={Handle}, Bounds={Bounds}",
            _hwnd, bounds);
    }

    /// <summary>
    /// Minimal window procedure - handles paint and essential messages.
    /// </summary>
    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Interop.WM_PAINT:
                // Handle paint message - render current frame
                PaintFrame(hWnd);
                return IntPtr.Zero;

            case Win32Interop.WM_ERASEBKGND:
                // Prevent flicker - we render everything ourselves
                return new IntPtr(1);

            case Win32Interop.WM_DESTROY:
                _logger.LogDebug("[Direct2D] Window {Handle} received WM_DESTROY", hWnd);
                return IntPtr.Zero;

            default:
                // Let Windows handle all other messages
                return Win32Interop.DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private void PaintFrame(IntPtr hwnd)
    {
        if (_currentFrame == null || _currentScreen == null)
            return;

        // Get device context
        var hdc = GetDC(hwnd);
        if (hdc == IntPtr.Zero)
            return;

        try
        {
            // Create compatible DC for double buffering
            var memDC = CreateCompatibleDC(hdc);
            if (memDC == IntPtr.Zero)
                return;

            try
            {
                // Create compatible bitmap
                var hBitmap = _currentFrame.GetHbitmap();
                var oldBitmap = SelectObject(memDC, hBitmap);

                // Blit to screen
                StretchBlt(
                    hdc, 0, 0, _currentScreen.ScreenBounds.Width, _currentScreen.ScreenBounds.Height,
                    memDC, 0, 0, _currentFrame.Width, _currentFrame.Height,
                    TernaryRasterOperations.SRCCOPY);

                // Cleanup
                SelectObject(memDC, oldBitmap);
                DeleteObject(hBitmap);
            }
            finally
            {
                DeleteDC(memDC);
            }
        }
        finally
        {
            ReleaseDC(hwnd, hdc);
        }
    }

    private void ParentToDesktop(Rectangle screenBounds)
    {
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Window handle not initialized");
        }

        var workerW = _desktopWindowManager.FindDesktopWorkerWindow();
        if (workerW != IntPtr.Zero)
        {
            _logger.LogInformation("[Direct2D] Found WorkerW: {WorkerW}, parenting render window", workerW);

            _desktopWindowManager.SetAsWallpaperWindow(_hwnd, screenBounds);
            _logger.LogInformation("[Direct2D] Render window parented to desktop successfully");
        }
        else
        {
            _logger.LogWarning("[Direct2D] Could not find WorkerW window, wallpaper may not render behind icons");
        }
    }

    /// <summary>
    /// Display a pre-composed frame on screen.
    /// Called by the composer after frame composition is complete.
    /// </summary>
    public void DisplayFrame(string clientId, Bitmap frame, ScreenMapping screen)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Direct2DRenderer));

        if (frame == null)
            throw new ArgumentNullException(nameof(frame));

        // Initialize window on first frame if not already done
        if (!_windowInitialized)
        {
            Initialize(screen);
        }

        try
        {
            _logger.LogTrace("[Direct2D] Displaying frame for screen {Order} (client {ClientId})", screen.Order, clientId);

            // Store previous frame for disposal
            var previousFrame = _previousFrames.TryGetValue(clientId, out var cached) ? cached : null;

            // Update current frame
            _currentFrame = frame;
            _currentScreen = screen;

            // Cache current frame
            _previousFrames[clientId] = frame;

            // Trigger repaint
            if (_hwnd != IntPtr.Zero)
            {
                Win32Interop.InvalidateRect(_hwnd, IntPtr.Zero, false);
                Win32Interop.UpdateWindow(_hwnd);
            }

            // Dispose previous frame to avoid memory leak
            previousFrame?.Dispose();

            _logger.LogInformation("[Direct2D] Frame displayed on screen {Order} ({Width}x{Height})",
                screen.Order, frame.Width, frame.Height);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Direct2D] Error displaying frame for screen {Order}", screen.Order);
            throw;
        }
    }

    /// <summary>
    /// Display all frames from a composed set.
    /// </summary>
    public void DisplayFrames(Dictionary<string, Bitmap> frames, Dictionary<string, ScreenMapping> screenMappings)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Direct2DRenderer));

        if (frames == null || frames.Count == 0)
        {
            _logger.LogWarning("[Direct2D] No frames to display");
            return;
        }

        foreach (var (clientId, frame) in frames)
        {
            if (screenMappings.TryGetValue(clientId, out var screen))
            {
                DisplayFrame(clientId, frame, screen);
            }
            else
            {
                _logger.LogWarning("[Direct2D] Screen mapping not found for client {ClientId}", clientId);
                frame.Dispose();
            }
        }
    }

    /// <summary>
    /// Clear all cached frame buffers.
    /// </summary>
    public void ClearCache()
    {
        foreach (var frame in _previousFrames.Values)
        {
            frame?.Dispose();
        }
        _previousFrames.Clear();

        _currentFrame = null;

        _logger.LogDebug("[Direct2D] Frame buffer cache cleared");
    }

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("[Direct2D] Disposing renderer");

        ClearCache();

        // Destroy native window
        if (_hwnd != IntPtr.Zero)
        {
            Win32Interop.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            _logger.LogDebug("[Direct2D] Native window destroyed");
        }

        // Unregister window class
        if (_classAtom != 0)
        {
            var hInstance = Win32Interop.GetModuleHandle(null);
            Win32Interop.UnregisterClass(_windowClassName, hInstance);
            _classAtom = 0;
            _logger.LogDebug("[Direct2D] Window class unregistered: {ClassName}", _windowClassName);
        }

        _windowInitialized = false;
        _disposed = true;
        GC.SuppressFinalize(this);

        _logger.LogInformation("[Direct2D] Renderer disposed");
    }

    // GDI Interop for rendering
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(
        IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc,
        TernaryRasterOperations rop);

    private enum TernaryRasterOperations : uint
    {
        SRCCOPY = 0x00CC0020
    }
}

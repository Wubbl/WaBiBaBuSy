# Native Win32 Window Architecture

**Date:** 2025-12-24
**Status:** ✅ Implemented and Tested
**Component:** D2DVorticeRenderer, Win32Interop

## Overview

WaBiBaBuSy uses **native Win32 window creation** for Direct2D rendering to avoid message loop conflicts with the Avalonia UI framework. This architecture eliminates the Windows Forms dependency that previously caused application freezing.

## Problem Statement

### Original Issue: Windows Forms + Avalonia Conflict

The initial implementation used Windows Forms (`Form`, `PictureBox`) to create rendering windows:

```csharp
// ❌ OLD APPROACH - Caused freezing
_renderForm = new Form { ... };
_renderForm.Show();  // No message loop!

// This sends Win32 messages that never get processed
Win32Interop.SetParent(windowHandle, _workerW);  // Blocks indefinitely
```

**Root Cause:**
- Avalonia runs its own message loop
- Windows Forms requires `Application.Run()` or `Application.DoEvents()` to process messages
- Win32 APIs (`SetParent`, `SetWindowPos`) send window messages that queue up
- Without a message pump, these messages never get processed → freeze

## Solution: Native Win32 Windows

Instead of using Windows Forms, we create windows directly via Win32 API:

```csharp
// ✅ NEW APPROACH - No freezing
RegisterClassEx(ref wndClass);  // Register window class
_hwnd = CreateWindowEx(...);    // Create native window
// DefWindowProc handles all messages automatically - no manual pumping needed
```

**Benefits:**
- ✅ No Windows Forms dependency
- ✅ No message loop conflicts with Avalonia
- ✅ Full control over window procedure
- ✅ Clean disposal with proper window class unregistration
- ✅ Matches Lively Wallpaper's approach (proven on Windows 11 24H2+)

---

## Architecture Components

### 1. Win32Interop Enhancements

**File:** `WaBiBaBuSy.WallpaperEngine/Native/Win32Interop.cs`

**New APIs Added:**

```csharp
// Window creation
[DllImport("user32.dll")]
public static extern IntPtr CreateWindowEx(
    int dwExStyle, string lpClassName, string lpWindowName,
    int dwStyle, int x, int y, int nWidth, int nHeight,
    IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

[DllImport("user32.dll")]
public static extern bool DestroyWindow(IntPtr hWnd);

// Window class registration
[DllImport("user32.dll")]
public static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

[DllImport("user32.dll")]
public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

// Window procedure
[DllImport("user32.dll")]
public static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

// Module handle
[DllImport("kernel32.dll")]
public static extern IntPtr GetModuleHandle(string? lpModuleName);
```

**Key Structures:**

```csharp
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WNDCLASSEX
{
    public uint cbSize;
    public uint style;              // CS_HREDRAW | CS_VREDRAW | CS_OWNDC
    public IntPtr lpfnWndProc;      // Window procedure function pointer
    public int cbClsExtra;
    public int cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    public string? lpszMenuName;
    public string lpszClassName;
    public IntPtr hIconSm;
}

public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
```

### 2. D2DVorticeRenderer Implementation

**File:** `WaBiBaBuSy.WallpaperEngine/Direct2D/D2DVorticeRenderer.cs`

**Initialization Flow:**

```
Initialize()
  ├─ CreateNativeWindow()
  │    ├─ Generate unique window class name
  │    ├─ RegisterClassEx() with window procedure
  │    └─ CreateWindowEx() with WS_POPUP | WS_VISIBLE
  │
  ├─ CreateD3DDevice()
  │    └─ D3D11CreateDevice() with BGRA support
  │
  ├─ CreateSwapChain()
  │    └─ CreateSwapChainForHwnd() bound to native HWND
  │
  ├─ CreateD2DRenderTarget()
  │    └─ CreateDxgiSurfaceRenderTarget() from swap chain
  │
  └─ ParentToDesktop()
       └─ SetAsWallpaperWindow() to attach to WorkerW
```

**Window Procedure (Minimal):**

```csharp
private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
{
    switch (msg)
    {
        case WM_PAINT:
            // Let Direct2D handle all rendering
            return IntPtr.Zero;

        case WM_ERASEBKGND:
            // Prevent flicker - we render everything ourselves
            return new IntPtr(1);

        case WM_DESTROY:
            _logger.LogDebug("Window received WM_DESTROY");
            return IntPtr.Zero;

        default:
            // DefWindowProc handles all other messages automatically
            return Win32Interop.DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
```

**Key Points:**
- Window procedure is **stateless** and **minimal**
- No manual message pumping (`GetMessage`, `DispatchMessage`) required
- `DefWindowProc` handles standard Windows messages automatically
- Delegate is kept as instance variable to prevent garbage collection

**Disposal Flow:**

```
Dispose()
  ├─ Dispose D2D render target
  ├─ Dispose swap chain
  ├─ Dispose D3D device & context
  ├─ Dispose D2D factory
  ├─ DestroyWindow(hwnd)
  └─ UnregisterClass(windowClassName, hInstance)
```

---

## Rendering Pipeline

The rendering pipeline remains unchanged from the Windows Forms version:

```
DisplayFrame(Bitmap frame)
  ├─ ConvertToD2DBitmap(frame)
  │    └─ Lock bitmap pixels → CreateBitmap from raw data
  │
  ├─ BeginDraw()
  ├─ Clear(black)
  ├─ DrawBitmap(d2dBitmap, scaled to screen)
  ├─ EndDraw()
  └─ Present(vsync=1)  // VSync for smooth rendering
```

**Performance:**
- GPU-accelerated Direct2D rendering
- DXGI flip model swap chain (modern flip presentation)
- VSync for tear-free display
- Hardware-accelerated bitmap scaling

---

## Integration with DesktopWindowManager

**File:** `WaBiBaBuSy.WallpaperEngine/Native/DesktopWindowManager.cs`

The native window integrates with `DesktopWindowManager` using the same `SetAsWallpaperWindow()` method:

```csharp
private void ParentToDesktop()
{
    var workerW = _desktopWindowManager.FindDesktopWorkerWindow();
    if (workerW != IntPtr.Zero)
    {
        // SetAsWallpaperWindow handles:
        // - Legacy mode (Windows 10): Parent to WorkerW
        // - Layered mode (Windows 11 24H2+): Parent to Progman with WS_EX_LAYERED
        _desktopWindowManager.SetAsWallpaperWindow(_hwnd, _screen.ScreenBounds);
    }
}
```

**Desktop Manager Compatibility:**
- ✅ Windows 10: Legacy WorkerW parenting
- ✅ Windows 11 pre-24H2: Legacy WorkerW parenting
- ✅ Windows 11 24H2+: Layered desktop mode with Progman parenting
- ✅ Automatic detection via `WS_EX_NOREDIRECTIONBITMAP` check

---

## Testing

### Unit Test: WaBiBaBuSy.D2DTest

**File:** `WaBiBaBuSy.D2DTest/Program.cs`

**Test Procedure:**
```
1. Create DesktopWindowManager
2. Get primary screen bounds via GetSystemMetrics() (no Windows Forms)
3. Create D2DVorticeRenderer
4. Initialize renderer
5. Render red bitmap at 60 FPS for 10 seconds
6. Dispose and exit
```

**Expected Results:**
- ✅ No application freezing
- ✅ Red wallpaper visible behind desktop icons
- ✅ ~600 frames rendered in 10 seconds (~60 FPS)
- ✅ Clean disposal without errors

**Test Output Example:**
```
WaBiBaBuSy Direct2D Vortice Renderer Test
==========================================
Primary screen: 1920x1080 at (0, 0)
Creating D2DVorticeRenderer (native Win32 window)...
Initializing renderer...
SUCCESS: Renderer initialized!

===================================
RED WINDOW SHOULD NOW BE VISIBLE!
===================================

Rendering... 0 seconds remaining (Frames: 600)
Rendered 600 frames in 10.02 seconds (59.9 FPS)
Test completed successfully!
```

---

## Comparison: Windows Forms vs Native Win32

| Aspect | Windows Forms (Old) | Native Win32 (New) |
|--------|---------------------|-------------------|
| **Window Creation** | `new Form()` | `CreateWindowEx()` |
| **Message Loop** | Requires `Application.Run()` | `DefWindowProc` handles automatically |
| **Avalonia Compatibility** | ❌ Conflict | ✅ No conflict |
| **Freezing Issue** | ❌ Yes | ✅ No |
| **Control** | Limited | ✅ Full control |
| **Memory Footprint** | Higher (Forms framework) | Lower (direct API) |
| **Initialization Time** | Slower | Faster |
| **Dependencies** | System.Windows.Forms.dll | None (Win32 only) |

---

## Best Practices

### 1. Window Procedure Delegate Lifetime

**Critical:** The `WndProc` delegate **must** be kept as an instance variable:

```csharp
private Win32Interop.WndProc? _wndProcDelegate;  // MUST be instance field!

private void CreateNativeWindow()
{
    _wndProcDelegate = WindowProc;  // Create and store delegate

    var wndClass = new Win32Interop.WNDCLASSEX
    {
        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate)
    };
}
```

**Why:** If the delegate is a local variable, the garbage collector may collect it, causing crashes when Windows tries to call the window procedure.

### 2. Window Class Name Uniqueness

Generate unique class names to avoid conflicts:

```csharp
_windowClassName = $"WaBiBaBuSyD2DRenderer_{Guid.NewGuid():N}";
```

**Why:** Multiple renderer instances may coexist (multi-monitor). Unique class names prevent registration conflicts.

### 3. Proper Disposal Order

Always dispose in reverse order of creation:

```csharp
public void Dispose()
{
    _d2dRenderTarget?.Dispose();      // 5. D2D render target
    _swapChain?.Dispose();             // 4. Swap chain
    _immediateContext?.Dispose();      // 3. D3D context
    _d3dDevice?.Dispose();             // 2. D3D device
    _d2dFactory?.Dispose();            // 1. D2D factory

    if (_hwnd != IntPtr.Zero)
        DestroyWindow(_hwnd);          // 6. Window

    if (_classAtom != 0)
        UnregisterClass(_windowClassName, hInstance);  // 7. Window class
}
```

### 4. Error Handling

Always check return values and use `Marshal.GetLastWin32Error()`:

```csharp
_classAtom = Win32Interop.RegisterClassEx(ref wndClass);
if (_classAtom == 0)
{
    var error = Marshal.GetLastWin32Error();
    throw new Exception($"Failed to register window class. Error: {error}");
}
```

---

## Future Enhancements

### 1. Multi-Monitor Optimization

Current approach creates one window per screen. Could optimize by:
- Single window spanning all monitors
- Dynamic window positioning on monitor changes

### 2. Window Styles Optimization

Experiment with additional window styles for performance:
- `WS_EX_NOREDIRECTIONBITMAP` - Disable DWM redirection
- `WS_EX_COMPOSITED` - Double-buffering hint

### 3. Message Handling Optimization

Add message filtering in window procedure:
- Handle `WM_DISPLAYCHANGE` for monitor config changes
- Handle `WM_POWERBROADCAST` for power state changes
- Optimize redraw messages

---

## References

### External Documentation
- [CreateWindowEx Documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowexa)
- [RegisterClassEx Documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerclassexa)
- [Window Procedures](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-procedures)
- [Lively Wallpaper WinDesktopCore](https://github.com/rocksdanister/lively/blob/main/src/Lively/Lively.Common/Services/Desktop/WinDesktopCore.cs)

### Internal Documentation
- `.docs/DIRECT2D_RENDERING_ARCHITECTURE.md` - Composition pipeline architecture
- `.docs/DISTRIBUTED_ANIMATION_SYSTEM.md` - Animation distribution system
- `CLAUDE.md` - Project overview and architecture

---

## Summary

The native Win32 window architecture:
- ✅ **Eliminates freezing** by removing Windows Forms dependency
- ✅ **Works seamlessly** with Avalonia UI framework
- ✅ **Provides full control** over window lifecycle
- ✅ **Matches proven approach** used by Lively Wallpaper
- ✅ **Reduces memory footprint** by eliminating Forms framework
- ✅ **Simplifies architecture** with direct Win32 API usage

This architecture is production-ready and forms the foundation for all Direct2D rendering in WaBiBaBuSy.

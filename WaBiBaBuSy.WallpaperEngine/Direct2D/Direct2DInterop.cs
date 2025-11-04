using System;
using System.Runtime.InteropServices;

namespace WaBiBaBuSy.WallpaperEngine.Direct2D;

/// <summary>
/// P/Invoke declarations for Direct2D 1.1 and Direct3D 11 interop.
/// Enables GPU-accelerated bitmap rendering to screen.
/// </summary>
internal static class Direct2DInterop
{
    // Direct3D 11 Device Creation
    [DllImport("d3d11.dll", SetLastError = true)]
    internal static extern int D3D11CreateDevice(
        IntPtr adapter,
        D3D_DRIVER_TYPE driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out IntPtr featureLevel,
        out IntPtr deviceContext);

    // DXGI Device Creation
    [DllImport("d3d11.dll", SetLastError = true)]
    internal static extern int D3D11CreateDeviceAndSwapChain(
        IntPtr adapter,
        D3D_DRIVER_TYPE driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        ref DXGI_SWAP_CHAIN_DESC swapChainDesc,
        out IntPtr swapChain,
        out IntPtr device,
        out IntPtr featureLevel,
        out IntPtr deviceContext);

    // Direct2D Factory Creation
    [DllImport("d2d1.dll", SetLastError = true)]
    internal static extern int D2D1CreateFactory(
        D2D1_FACTORY_TYPE factoryType,
        ref Guid riid,
        IntPtr pFactoryOptions,
        out IntPtr ppIFactory);

    // GDI to Direct2D Conversion
    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    internal static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbmp,
        uint uStartScan,
        uint cScanLines,
        IntPtr lpvBits,
        ref BITMAPINFO lpbmi,
        uint uUsage);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    // Constants
    internal const uint D3D11_SDK_VERSION = 7;
    internal const uint D3D_FEATURE_LEVEL_11_0 = 0xb000;
    internal const uint D3D_FEATURE_LEVEL_10_1 = 0xa100;
    internal const uint D3D_FEATURE_LEVEL_10_0 = 0xa000;
    internal const uint D3D_FEATURE_LEVEL_9_3 = 0x9300;

    internal const uint DIB_RGB_COLORS = 0;
    internal const uint SRCCOPY = 0x00CC0020;

    // COM Object Release
    [DllImport("ole32.dll")]
    internal static extern uint CoTaskMemFree(IntPtr pv);
}

internal enum D3D_DRIVER_TYPE : uint
{
    UNKNOWN = 0,
    HARDWARE = 1,
    REFERENCE = 2,
    NULL = 3,
    SOFTWARE = 4,
    WARP = 5
}

internal enum D2D1_FACTORY_TYPE : uint
{
    SINGLE_THREADED = 0,
    MULTI_THREADED = 1
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
    public uint[] bmiColors;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_SWAP_CHAIN_DESC
{
    public DXGI_MODE_DESC BufferDesc;
    public DXGI_SAMPLE_DESC SampleDesc;
    public uint BufferUsage;
    public uint BufferCount;
    public IntPtr OutputWindow;
    [MarshalAs(UnmanagedType.Bool)]
    public bool Windowed;
    public DXGI_SWAP_EFFECT SwapEffect;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_MODE_DESC
{
    public uint Width;
    public uint Height;
    public DXGI_RATIONAL RefreshRate;
    public uint Format; // DXGI_FORMAT
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_SAMPLE_DESC
{
    public uint Count;
    public uint Quality;
}

internal enum DXGI_SWAP_EFFECT : uint
{
    DISCARD = 0,
    SEQUENTIAL = 1,
    FLIP_SEQUENTIAL = 3,
    FLIP_DISCARD = 4
}

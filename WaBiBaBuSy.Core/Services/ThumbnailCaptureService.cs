using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.Core.Services;

/// <summary>
/// Captures thumbnails of the current wallpaper window for live preview in the server topology.
/// Uses Win32 PrintWindow to capture the wallpaper HWND and encodes as JPEG.
/// </summary>
public class ThumbnailCaptureService
{
    private readonly ILogger<ThumbnailCaptureService> _logger;
    private const int ThumbnailWidth = 160;
    private const int ThumbnailHeight = 90;
    private const long JpegQuality = 60L;

    private IntPtr _wallpaperHwnd = IntPtr.Zero;
    private string? _currentWallpaperName;
    private byte[]? _cachedThumbnail;
    private string? _cachedWallpaperName;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    // PW_RENDERFULLCONTENT captures DWM-composed content (works with D2D/DXGI windows)
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    public ThumbnailCaptureService(ILogger<ThumbnailCaptureService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Set the wallpaper window handle to capture from.
    /// </summary>
    public void SetWallpaperHwnd(IntPtr hwnd, string? wallpaperName = null)
    {
        if (_wallpaperHwnd != hwnd)
        {
            _wallpaperHwnd = hwnd;
            _cachedThumbnail = null; // Invalidate cache on HWND change
        }

        if (_currentWallpaperName != wallpaperName)
        {
            _currentWallpaperName = wallpaperName;
            _cachedThumbnail = null; // Invalidate cache on wallpaper change
        }
    }

    /// <summary>
    /// Clear the wallpaper window handle (no wallpaper active).
    /// </summary>
    public void ClearWallpaperHwnd()
    {
        _wallpaperHwnd = IntPtr.Zero;
        _currentWallpaperName = null;
        _cachedThumbnail = null;
        _cachedWallpaperName = null;
    }

    /// <summary>
    /// Gets the current wallpaper name.
    /// </summary>
    public string? CurrentWallpaperName => _currentWallpaperName;

    /// <summary>
    /// Capture the current wallpaper as a JPEG thumbnail.
    /// Returns null if no wallpaper is active or capture fails.
    /// </summary>
    public byte[]? CaptureCurrentThumbnail()
    {
        if (_wallpaperHwnd == IntPtr.Zero || !IsWindow(_wallpaperHwnd))
        {
            return null;
        }

        try
        {
            if (!GetWindowRect(_wallpaperHwnd, out var rect))
            {
                _logger.LogDebug("Failed to get window rect for HWND {Hwnd}", _wallpaperHwnd);
                return null;
            }

            var windowWidth = rect.Width;
            var windowHeight = rect.Height;

            if (windowWidth <= 0 || windowHeight <= 0)
            {
                return null;
            }

            // Capture the window content
            using var windowBitmap = new Bitmap(windowWidth, windowHeight, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(windowBitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    // PW_RENDERFULLCONTENT works with DWM-composed windows (D2D, DXGI)
                    if (!PrintWindow(_wallpaperHwnd, hdc, PW_RENDERFULLCONTENT))
                    {
                        _logger.LogDebug("PrintWindow failed for HWND {Hwnd}", _wallpaperHwnd);
                        return null;
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }

            // Resize to thumbnail
            using var thumbnail = new Bitmap(ThumbnailWidth, ThumbnailHeight, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(thumbnail))
            {
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.SmoothingMode = SmoothingMode.HighSpeed;
                graphics.DrawImage(windowBitmap, 0, 0, ThumbnailWidth, ThumbnailHeight);
            }

            // Encode as JPEG
            using var ms = new MemoryStream();
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
            var jpegCodec = ImageCodecInfo.GetImageDecoders()
                .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            thumbnail.Save(ms, jpegCodec, encoderParams);

            _cachedThumbnail = ms.ToArray();
            _cachedWallpaperName = _currentWallpaperName;

            return _cachedThumbnail;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error capturing wallpaper thumbnail");
            return null;
        }
    }
}

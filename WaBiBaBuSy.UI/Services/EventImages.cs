using System;
using System.IO;
using Avalonia.Media.Imaging;

namespace WaBiBaBuSy.UI.Services;

/// <summary>Image facts the server needs for show events.</summary>
public static class EventImages
{
    /// <summary>Width ÷ height of an image file; 1 when it cannot be read.</summary>
    public static float Aspect(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 1f;
            using var bmp = new Bitmap(path);
            return bmp.PixelSize.Height > 0 ? bmp.PixelSize.Width / (float)bmp.PixelSize.Height : 1f;
        }
        catch (Exception)
        {
            return 1f;
        }
    }
}

using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using WaBiBaBuSy.Models.Configuration;

namespace WaBiBaBuSy.UI.Services;

/// <summary>The black picture behind events fired while nothing plays (a file, so remote seats receive the stand-in scene).</summary>
internal static class IdleImage
{
    /// <summary>Path of the image, written once under %APPDATA%\WaBiBaBuSy.</summary>
    public static string EnsurePath()
    {
        var path = Path.Combine(AppDataPaths.Root, "event-standin-black.png");
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(AppDataPaths.Root);
        using var bmp = new Bitmap(64, 36, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Black);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }
}

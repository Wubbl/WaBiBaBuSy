using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>PNG ↔ BGRA8 buffers for the image checks (System.Drawing, Windows only).</summary>
public static class PngPixels
{
    public sealed record Image(byte[] Pixels, int Width, int Height, int Stride);

    public static Image Decode(string path)
    {
        using var bmp = new Bitmap(path);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = bmp.Width * 4;
            var pixels = new byte[stride * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
            return new Image(pixels, bmp.Width, bmp.Height, stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public static void Encode(byte[] bgra, int width, int height, int stride, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var bmp = new Bitmap(width, height, stride, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            bmp.Save(path, ImageFormat.Png);
        }
        finally
        {
            handle.Free();
        }
    }
}

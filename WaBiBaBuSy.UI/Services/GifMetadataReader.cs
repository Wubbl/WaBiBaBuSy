using System;
using System.Drawing;
using System.Drawing.Imaging;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.Services;

/// <summary>
/// Reads GIF resolution, frame count and loop length (delays normalized the way the D2D player plays them).
/// </summary>
public static class GifMetadataReader
{
    private const int FrameDelayPropertyId = 0x5100; // PropertyTagFrameDelay, 1/100 s per frame

    /// <summary>Returns (resolution "WxH", frame count, loop length ms), or null when the file cannot be read.</summary>
    public static (string Resolution, int FrameCount, long DurationMs)? Read(string path)
    {
        try
        {
            using var image = Image.FromFile(path);
            var resolution = $"{image.Width}x{image.Height}";
            int frames = image.GetFrameCount(FrameDimension.Time);

            long durationMs = 0;
            if (frames > 1 && Array.IndexOf(image.PropertyIdList, FrameDelayPropertyId) >= 0)
            {
                var bytes = image.GetPropertyItem(FrameDelayPropertyId)?.Value;
                if (bytes != null)
                {
                    for (int i = 0; i < frames && (i + 1) * 4 <= bytes.Length; i++)
                        durationMs += MediaDetails.NormalizeGifDelayMs(BitConverter.ToInt32(bytes, i * 4) * 10);
                }
            }

            return (resolution, frames, durationMs);
        }
        catch
        {
            return null;
        }
    }
}

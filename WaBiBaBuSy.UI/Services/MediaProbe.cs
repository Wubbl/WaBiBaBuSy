using System;
using System.IO;
using ImageMagick;
using WaBiBaBuSy.Models.Wallpaper.Suggest;

namespace WaBiBaBuSy.UI.Services;

/// <summary>Decodes a file's first frame to a 64 × 64 BGRA thumbnail for <see cref="MediaAnalyzer"/>.</summary>
public static class MediaProbe
{
    private const uint ThumbSize = 64;

    /// <summary>
    /// The file's original size and its pixel facts. Any decode failure gives (0, 0, <see cref="PixelFacts.Unknown"/>):
    /// suggestions still work, just without colour facts.
    /// </summary>
    public static (int Width, int Height, PixelFacts Pixels) Read(string imagePath)
    {
        try
        {
            using var image = new MagickImage(imagePath, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
            int width = (int)image.Width, height = (int)image.Height;
            if (!image.HasAlpha) image.Alpha(AlphaOption.Set);
            image.Resize(ThumbSize, ThumbSize);
            using var pixels = image.GetPixels();
            var bgra = pixels.ToByteArray(PixelMapping.BGRA);
            return bgra == null
                ? (width, height, PixelFacts.Unknown)
                : (width, height, MediaAnalyzer.Analyze(bgra, (int)image.Width, (int)image.Height));
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, 0, PixelFacts.Unknown);
        }
    }
}

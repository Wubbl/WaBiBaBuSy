using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ImageMagick;

namespace WaBiBaBuSy.WallpaperEngine.Services;

/// <summary>
/// Detects the dominant edge color of an image/GIF/video frame for use as background color.
/// Samples edge pixels and finds the most frequent color bucket.
/// </summary>
public static class BackgroundColorDetector
{
    /// <summary>
    /// Detect the dominant color along the edges of the given file.
    /// For GIF: uses frame 0 via Magick.NET.
    /// For images: loads directly via Magick.NET.
    /// For video: attempts to use VideoThumbnailGenerator cache, falls back to #000000.
    /// Returns hex color string like "#1A2B3C".
    /// </summary>
    public static async Task<string> DetectDominantEdgeColorAsync(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ext is ".gif")
                return DetectFromGif(filePath);

            if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp")
                return DetectFromImage(filePath);

            if (ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".flv")
                return await DetectFromVideoThumbnailAsync(filePath);

            return "#000000";
        }
        catch
        {
            return "#000000";
        }
    }

    private static string DetectFromGif(string filePath)
    {
        using var collection = new MagickImageCollection(filePath);
        if (collection.Count == 0) return "#000000";

        collection.Coalesce();
        using var frame = collection[0];
        return SampleEdgePixels(frame);
    }

    private static string DetectFromImage(string filePath)
    {
        using var image = new MagickImage(filePath);
        return SampleEdgePixels(image);
    }

    private static async Task<string> DetectFromVideoThumbnailAsync(string filePath)
    {
        // Try to find a cached thumbnail for this video
        var fileInfo = new FileInfo(filePath);
        var cacheKey = $"{filePath}|{fileInfo.LastWriteTimeUtc.Ticks}|320";

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(cacheKey));
        var hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant()[..16];

        var thumbnailCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WaBiBaBuSy", "Thumbnails");
        var cachedPath = Path.Combine(thumbnailCacheDir, $"{hash}.jpg");

        if (File.Exists(cachedPath))
        {
            return DetectFromImage(cachedPath);
        }

        // No cached thumbnail available
        return "#000000";
    }

    /// <summary>
    /// Sample edge pixels from a MagickImage and return the dominant color as hex.
    /// Samples every 10th pixel along all 4 edges, quantizes to 16 levels per channel.
    /// </summary>
    private static string SampleEdgePixels(IMagickImage<byte> image)
    {
        var width = (int)image.Width;
        var height = (int)image.Height;

        if (width < 2 || height < 2)
            return "#000000";

        var colorCounts = new Dictionary<int, int>();
        var pixels = image.GetPixels();
        var step = Math.Max(1, Math.Min(10, Math.Min(width, height) / 4));

        // Sample top edge
        for (int x = 0; x < width; x += step)
            AddPixel(pixels, x, 0, colorCounts);

        // Sample bottom edge
        for (int x = 0; x < width; x += step)
            AddPixel(pixels, x, height - 1, colorCounts);

        // Sample left edge
        for (int y = 0; y < height; y += step)
            AddPixel(pixels, 0, y, colorCounts);

        // Sample right edge
        for (int y = 0; y < height; y += step)
            AddPixel(pixels, width - 1, y, colorCounts);

        if (colorCounts.Count == 0)
            return "#000000";

        // Find most frequent bucket
        var maxCount = 0;
        var dominantKey = 0;
        foreach (var kvp in colorCounts)
        {
            if (kvp.Value > maxCount)
            {
                maxCount = kvp.Value;
                dominantKey = kvp.Key;
            }
        }

        // Decode quantized color back to RGB (center of bucket)
        var r = ((dominantKey >> 8) & 0xF) * 17; // 0xF * 17 = 255
        var g = ((dominantKey >> 4) & 0xF) * 17;
        var b = (dominantKey & 0xF) * 17;

        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static void AddPixel(IPixelCollection<byte> pixels, int x, int y, Dictionary<int, int> colorCounts)
    {
        var pixel = pixels.GetPixel(x, y);
        var channels = pixel.ToArray();

        if (channels.Length < 3) return;

        // Skip transparent pixels (if alpha channel exists and alpha < 128)
        if (channels.Length >= 4 && channels[3] < 128) return;

        // Quantize to 16 levels per channel (4-bit each)
        var rQ = channels[0] >> 4;
        var gQ = channels[1] >> 4;
        var bQ = channels[2] >> 4;

        var key = (rQ << 8) | (gQ << 4) | bQ;

        colorCounts.TryGetValue(key, out var count);
        colorCounts[key] = count + 1;
    }
}

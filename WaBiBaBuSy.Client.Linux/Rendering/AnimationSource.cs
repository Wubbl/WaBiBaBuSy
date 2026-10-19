using ImageMagick;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace WaBiBaBuSy.Client.Linux.Rendering;

/// <summary>
/// Decoded animation content: every frame as a raster SKImage plus per-frame delays.
/// Mirrors the Windows player's GIF pipeline (Magick.NET coalesce, 0-delay → 10 ms,
/// delays capped at 1000 ms) so frame selection is identical across platforms.
/// </summary>
public sealed class AnimationSource : IDisposable
{
    public SKImage[] Frames { get; }
    public List<int> Delays { get; }
    public long TotalDurationMs { get; }
    public int Width { get; }
    public int Height { get; }

    private AnimationSource(SKImage[] frames, List<int> delays, int width, int height)
    {
        Frames = frames;
        Delays = delays;
        TotalDurationMs = Math.Max(1, delays.Sum());
        Width = width;
        Height = height;
    }

    public static bool IsImage(string ext) => ext is ".jpg" or ".jpeg" or ".png" or ".bmp";
    public static bool IsVideo(string ext) => ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".flv";

    /// <summary>Load a GIF (all frames) or a static image (single frame).</summary>
    public static AnimationSource Load(string filePath, ILogger? logger = null)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".gif")
            return LoadGif(filePath, logger);
        if (IsImage(ext))
        {
            using var image = new MagickImage(filePath);
            var frame = ToSkImage(image);
            return new AnimationSource(new[] { frame }, new List<int> { 1000 }, (int)image.Width, (int)image.Height);
        }
        throw new NotSupportedException($"Unsupported animation format '{ext}'");
    }

    private static AnimationSource LoadGif(string filePath, ILogger? logger)
    {
        var startTime = DateTime.UtcNow;
        using var collection = new MagickImageCollection(filePath);

        // Coalesce applies GIF disposal methods so each frame becomes a full image
        collection.Coalesce();

        int frameCount = collection.Count;
        var frames = new SKImage[frameCount];
        var delays = new List<int>(frameCount);

        for (int i = 0; i < frameCount; i++)
        {
            var frame = collection[i];

            // AnimationDelay is in 1/100th of a second
            int delayMs = (int)(frame.AnimationDelay * 10);
            if (delayMs <= 0) delayMs = 10; // Browser standard: 0-delay = 10ms
            if (delayMs > 1000) delayMs = 1000;
            delays.Add(delayMs);

            frames[i] = ToSkImage(frame);
        }

        int width = (int)collection[0].Width;
        int height = (int)collection[0].Height;

        logger?.LogInformation("[GIF] Decoded {Frames} frames ({W}x{H}, {Duration}ms) from {File} in {Elapsed:F0}ms",
            frameCount, width, height, delays.Sum(), Path.GetFileName(filePath), (DateTime.UtcNow - startTime).TotalMilliseconds);

        return new AnimationSource(frames, delays, width, height);
    }

    /// <summary>Convert a Magick image into an immutable raster SKImage (unpremultiplied RGBA).</summary>
    public static SKImage ToSkImage(IMagickImage<byte> image)
    {
        int w = (int)image.Width, h = (int)image.Height;
        using var pixels = image.GetPixelsUnsafe();
        var rgba = pixels.ToByteArray(PixelMapping.RGBA)
                   ?? throw new InvalidOperationException("Failed to read image pixels");
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var data = SKData.CreateCopy(rgba);
        return SKImage.FromPixels(info, data, w * 4)
               ?? throw new InvalidOperationException("Failed to create SKImage");
    }

    /// <summary>Frame index at the given (already speed-scaled) time, looping.</summary>
    public int FrameIndexAt(long effectiveMs)
    {
        if (Frames.Length <= 1) return 0;
        long t = effectiveMs % TotalDurationMs;
        if (t < 0) t += TotalDurationMs;
        long acc = 0;
        for (int i = 0; i < Delays.Count; i++)
        {
            acc += Delays[i];
            if (t < acc) return Math.Min(i, Frames.Length - 1);
        }
        return Frames.Length - 1;
    }

    public void Dispose()
    {
        foreach (var f in Frames) f.Dispose();
    }
}

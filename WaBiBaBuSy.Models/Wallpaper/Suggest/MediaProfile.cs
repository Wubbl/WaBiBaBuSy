namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>What kind of file a media item is (decides which recipes can use it).</summary>
public enum MediaKind { Image, Gif, Video }

/// <summary>Size of a media item relative to the server's primary monitor.</summary>
public enum SizeClass { Small, Medium, FullScreen }

/// <summary>Shape of a media item: Wide (&gt; 2.5 : 1), Tall (&lt; 0.6 : 1) or Square in between.</summary>
public enum AspectClass { Wide, Square, Tall }

/// <summary>Facts read from a file's (downscaled) first frame.</summary>
/// <param name="HasAlpha">More than 5 % of the pixels are see-through (alpha &lt; 128).</param>
/// <param name="IsMonochrome">At least 90 % of the visible pixels are achromatic or share the dominant hue.</param>
/// <param name="DominantColors">Up to 3 <c>#RRGGBB</c> colours, most frequent first.</param>
/// <param name="MeanLuminance">Mean Rec. 709 luma of the visible pixels, 0..1.</param>
public sealed record PixelFacts(bool HasAlpha, bool IsMonochrome, IReadOnlyList<string> DominantColors, double MeanLuminance)
{
    /// <summary>Used when a file could not be decoded: no colour facts, mid luminance.</summary>
    public static PixelFacts Unknown { get; } = new(false, false, Array.Empty<string>(), 0.5);
}

/// <summary>Everything the suggestion recipes know about one picked file.</summary>
public sealed record MediaProfile(
    string Path, MediaKind Kind, int PixelWidth, int PixelHeight,
    SizeClass Size, AspectClass Aspect, PixelFacts Pixels, long? LoopMs, long? DurationMs)
{
    /// <summary>A room-spanning background picture (<see cref="MediaAnalyzer.IsPanorama"/>).</summary>
    public bool IsPanorama { get; init; }

    /// <summary>
    /// Build a profile. <paramref name="durationMs"/> is the gallery's duration: the loop length for a GIF,
    /// the clip length for a video, ignored for stills; 0 or less means unknown.
    /// </summary>
    public static MediaProfile Create(string path, int width, int height, int screenWidth, int screenHeight,
        PixelFacts pixels, long durationMs)
    {
        var kind = MediaAnalyzer.KindOf(path);
        return new MediaProfile(path, kind, width, height,
            MediaAnalyzer.ClassifySize(width, height, screenWidth, screenHeight),
            MediaAnalyzer.ClassifyAspect(width, height), pixels,
            kind == MediaKind.Gif && durationMs > 0 ? durationMs : null,
            kind == MediaKind.Video && durationMs > 0 ? durationMs : null)
        {
            IsPanorama = MediaAnalyzer.IsPanorama(kind, width, height, screenWidth, pixels.HasAlpha),
        };
    }
}

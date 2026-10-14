namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>Pure pixel and size analysis for scene suggestions (spec 2026-10-14 §1.1). No imaging dependency.</summary>
public static class MediaAnalyzer
{
    private const byte VisibleAlpha = 128;
    private const double AlphaShare = 0.05;
    private const double LowSaturation = 0.15;
    private const int DarkMax = 40;          // max channel below this = achromatic (noise near black)
    private const double MonoShare = 0.90;
    private const double ColorShare = 0.05;
    private const int MaxColors = 3;
    private const int HueBins = 36;          // 10° each
    private const int HueReachBins = 2;      // dominant bin ± 2

    /// <summary>Analyse a BGRA buffer (4 bytes per pixel, rows packed). Too-short or empty input → <see cref="PixelFacts.Unknown"/>.</summary>
    public static PixelFacts Analyze(ReadOnlySpan<byte> bgra, int width, int height)
    {
        int n = width * height;
        if (width <= 0 || height <= 0 || bgra.Length < n * 4) return PixelFacts.Unknown;

        int transparent = 0, visible = 0, achromatic = 0;
        double lumaSum = 0;
        var hueBins = new int[HueBins];
        var buckets = new Dictionary<int, int>();

        for (int i = 0; i < n; i++)
        {
            byte b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2], a = bgra[i * 4 + 3];
            if (a < VisibleAlpha) { transparent++; continue; }
            visible++;
            lumaSum += (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;

            int key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
            buckets[key] = buckets.GetValueOrDefault(key) + 1;

            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max < DarkMax || (max - min) / (double)max < LowSaturation) { achromatic++; continue; }
            hueBins[(int)(Hue(r, g, b, max, min) / 10.0) % HueBins]++;
        }

        bool hasAlpha = transparent > AlphaShare * n;
        if (visible == 0) return new PixelFacts(hasAlpha, false, Array.Empty<string>(), PixelFacts.Unknown.MeanLuminance);

        int dominant = 0;
        for (int i = 1; i < HueBins; i++) if (hueBins[i] > hueBins[dominant]) dominant = i;
        int nearDominant = 0;
        for (int d = -HueReachBins; d <= HueReachBins; d++) nearDominant += hueBins[(dominant + d + HueBins) % HueBins];
        bool mono = achromatic + nearDominant >= MonoShare * visible;

        var colors = buckets
            .Where(kv => kv.Value >= ColorShare * visible)
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Take(MaxColors)
            .Select(kv => BucketHex(kv.Key))
            .ToList();

        return new PixelFacts(hasAlpha, mono, colors, lumaSum / visible);
    }

    /// <summary>Small: height &lt; 25 % of the screen; FullScreen: ≥ 70 % of both sides; unknown sizes are Medium.</summary>
    public static SizeClass ClassifySize(int width, int height, int screenWidth, int screenHeight)
    {
        if (width <= 0 || height <= 0 || screenWidth <= 0 || screenHeight <= 0) return SizeClass.Medium;
        if (width >= 0.7 * screenWidth && height >= 0.7 * screenHeight) return SizeClass.FullScreen;
        return height < 0.25 * screenHeight ? SizeClass.Small : SizeClass.Medium;
    }

    /// <summary>Wide above 2.5 : 1, Tall below 0.6 : 1, otherwise (and for unknown sizes) Square.</summary>
    public static AspectClass ClassifyAspect(int width, int height)
    {
        if (width <= 0 || height <= 0) return AspectClass.Square;
        double aspect = width / (double)height;
        return aspect > 2.5 ? AspectClass.Wide : aspect < 0.6 ? AspectClass.Tall : AspectClass.Square;
    }

    /// <summary>
    /// A still, opaque picture at least 3 : 1 and 1.5 screens wide: made to span the room as a
    /// <see cref="BackgroundMode.Panorama"/> background, not to move as a sprite (a transparent wide wordmark stays a sprite).
    /// </summary>
    public static bool IsPanorama(MediaKind kind, int width, int height, int screenWidth, bool hasAlpha) =>
        kind == MediaKind.Image && !hasAlpha && height > 0 && screenWidth > 0
        && width >= 3.0 * height && width >= 1.5 * screenWidth;

    /// <summary>Gif for <c>.gif</c>, Video for anything <see cref="SceneChecks.IsVideo"/> accepts, else Image.</summary>
    public static MediaKind KindOf(string path) =>
        string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase) ? MediaKind.Gif
        : SceneChecks.IsVideo(path) ? MediaKind.Video
        : MediaKind.Image;

    private static double Hue(int r, int g, int b, int max, int min)
    {
        double d = max - min;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    private static string BucketHex(int key)
    {
        int r = (key >> 8 & 0xF) << 4 | 8, g = (key >> 4 & 0xF) << 4 | 8, b = (key & 0xF) << 4 | 8;
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}

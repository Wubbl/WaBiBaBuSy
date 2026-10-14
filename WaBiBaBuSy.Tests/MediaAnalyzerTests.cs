using WaBiBaBuSy.Models.Wallpaper.Suggest;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class MediaAnalyzerTests
{
    /// <summary>A w × h BGRA buffer filled by <paramref name="pixel"/>(x, y) → (r, g, b, a).</summary>
    private static byte[] Image(int w, int h, Func<int, int, (byte r, byte g, byte b, byte a)> pixel)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                int i = (y * w + x) * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a;
            }
        return px;
    }

    [Fact]
    public void OpaqueRedIsMonochromeWithoutAlpha()
    {
        var f = MediaAnalyzer.Analyze(Image(8, 8, (_, _) => (255, 0, 0, 255)), 8, 8);
        Assert.False(f.HasAlpha);
        Assert.True(f.IsMonochrome);
        Assert.Equal(new[] { "#F80808" }, f.DominantColors);
        Assert.InRange(f.MeanLuminance, 0.20, 0.23);
    }

    [Fact]
    public void BlackLogoOnTransparencyIsMonochromeWithAlpha()
    {
        var f = MediaAnalyzer.Analyze(Image(8, 8, (x, _) => x < 4 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)), 8, 8);
        Assert.True(f.HasAlpha);
        Assert.True(f.IsMonochrome);
        Assert.Equal(new[] { "#080808" }, f.DominantColors);
        Assert.InRange(f.MeanLuminance, 0.0, 0.01);
    }

    [Fact]
    public void FourColorQuadrantsAreColorfulWithThreeDominantColors()
    {
        var f = MediaAnalyzer.Analyze(Image(8, 8, (x, y) => (x < 4, y < 4) switch
        {
            (true, true) => ((byte)255, (byte)0, (byte)0, (byte)255),
            (false, true) => ((byte)0, (byte)255, (byte)0, (byte)255),
            (true, false) => ((byte)0, (byte)0, (byte)255, (byte)255),
            _ => ((byte)255, (byte)255, (byte)0, (byte)255),
        }), 8, 8);
        Assert.False(f.IsMonochrome);
        Assert.Equal(3, f.DominantColors.Count);
    }

    [Fact]
    public void GreyscaleGradientIsMonochrome()
    {
        var f = MediaAnalyzer.Analyze(Image(16, 1, (x, _) => ((byte)(x * 16), (byte)(x * 16), (byte)(x * 16), (byte)255)), 16, 1);
        Assert.True(f.IsMonochrome);
    }

    [Fact]
    public void NeighbouringHuesCountAsMonochrome()
    {
        // pure red (0°) and red-orange (~14°) — one hue family
        var f = MediaAnalyzer.Analyze(Image(8, 8, (x, _) => x < 4 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)60, (byte)0, (byte)255)), 8, 8);
        Assert.True(f.IsMonochrome);
    }

    [Fact]
    public void NoisyNearBlackPixelsCountAsAchromatic()
    {
        // JPEG-ish noise around black must not make a black logo "colourful"
        var f = MediaAnalyzer.Analyze(Image(8, 8, (x, y) => (x + y) % 2 == 0 ? ((byte)12, (byte)4, (byte)2, (byte)255) : ((byte)2, (byte)10, (byte)14, (byte)255)), 8, 8);
        Assert.True(f.IsMonochrome);
    }

    [Fact]
    public void FullyTransparentImageHasNoColors()
    {
        var f = MediaAnalyzer.Analyze(Image(4, 4, (_, _) => (0, 0, 0, 0)), 4, 4);
        Assert.True(f.HasAlpha);
        Assert.False(f.IsMonochrome);
        Assert.Empty(f.DominantColors);
    }

    [Fact]
    public void EmptyInputIsUnknown()
    {
        Assert.Same(PixelFacts.Unknown, MediaAnalyzer.Analyze(ReadOnlySpan<byte>.Empty, 0, 0));
        Assert.Same(PixelFacts.Unknown, MediaAnalyzer.Analyze(new byte[8], 4, 4)); // too short for 4 × 4
    }

    [Fact]
    public void ColorsUnderFivePercentAreDropped()
    {
        // 1 green pixel of 64 = 1.6 %
        var f = MediaAnalyzer.Analyze(Image(8, 8, (x, y) => x == 0 && y == 0 ? ((byte)0, (byte)255, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255)), 8, 8);
        Assert.Equal(new[] { "#F8F8F8" }, f.DominantColors);
    }

    [Theory]
    [InlineData(200, 200, 1920, 1080, SizeClass.Small)]
    [InlineData(800, 600, 1920, 1080, SizeClass.Medium)]
    [InlineData(1920, 1080, 1920, 1080, SizeClass.FullScreen)]
    [InlineData(1400, 800, 1920, 1080, SizeClass.FullScreen)]
    [InlineData(0, 0, 1920, 1080, SizeClass.Medium)]
    [InlineData(200, 200, 0, 0, SizeClass.Medium)]
    public void SizeClasses(int w, int h, int sw, int sh, SizeClass expected) =>
        Assert.Equal(expected, MediaAnalyzer.ClassifySize(w, h, sw, sh));

    [Theory]
    [InlineData(1000, 200, AspectClass.Wide)]
    [InlineData(300, 300, AspectClass.Square)]
    [InlineData(200, 500, AspectClass.Tall)]
    [InlineData(0, 0, AspectClass.Square)]
    public void AspectClasses(int w, int h, AspectClass expected) =>
        Assert.Equal(expected, MediaAnalyzer.ClassifyAspect(w, h));

    [Theory]
    [InlineData(@"C:\a\cat.gif", MediaKind.Gif)]
    [InlineData(@"C:\a\logo.PNG", MediaKind.Image)]
    [InlineData(@"C:\a\clip.mp4", MediaKind.Video)]
    public void KindFromExtension(string path, MediaKind expected) =>
        Assert.Equal(expected, MediaAnalyzer.KindOf(path));

    [Fact]
    public void CreateKeepsLoopForGifsAndDurationForVideos()
    {
        var gif = MediaProfile.Create(@"C:\a\cat.gif", 200, 200, 1920, 1080, PixelFacts.Unknown, 1200);
        Assert.Equal(1200, gif.LoopMs);
        Assert.Null(gif.DurationMs);
        var video = MediaProfile.Create(@"C:\a\clip.mp4", 1920, 1080, 1920, 1080, PixelFacts.Unknown, 60000);
        Assert.Null(video.LoopMs);
        Assert.Equal(60000, video.DurationMs);
        var still = MediaProfile.Create(@"C:\a\logo.png", 200, 200, 1920, 1080, PixelFacts.Unknown, 0);
        Assert.Null(still.LoopMs);
        Assert.Equal(SizeClass.Small, still.Size);
    }
}

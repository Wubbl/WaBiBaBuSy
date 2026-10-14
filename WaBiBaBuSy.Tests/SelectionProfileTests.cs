using WaBiBaBuSy.Models.Wallpaper.Suggest;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SelectionProfileTests
{
    internal static MediaProfile File(string name, int w, int h, bool mono = true, bool alpha = true,
        string[]? colors = null, long durationMs = 0, double luma = 0.5) =>
        MediaProfile.Create(@"C:\m\" + name, w, h, 1920, 1080,
            new PixelFacts(alpha, mono, colors ?? new[] { "#F80808" }, luma), durationMs);

    [Fact]
    public void OneFileIsSingle() =>
        Assert.Equal(SelectionMix.Single, new SelectionProfile(new[] { File("a.png", 200, 200) }, 1920, 1080).Mix);

    [Fact]
    public void SimilarHeightsAreUniform() =>
        Assert.Equal(SelectionMix.Uniform, new SelectionProfile(new[] { File("a.png", 200, 200), File("b.png", 300, 380) }, 1920, 1080).Mix);

    [Fact]
    public void OneFullScreenPlusSmallIsBigPlusSmall()
    {
        var s = new SelectionProfile(new[] { File("small.gif", 200, 200), File("big.gif", 1920, 1080) }, 1920, 1080);
        Assert.Equal(SelectionMix.BigPlusSmall, s.Mix);
        Assert.Equal("big.gif", Path.GetFileName(s.Big!.Path));
        Assert.Equal(new[] { "small.gif" }, s.Sprites.Select(f => Path.GetFileName(f.Path)));
    }

    [Fact]
    public void VeryDifferentHeightsAreMixed() =>
        Assert.Equal(SelectionMix.Mixed, new SelectionProfile(new[] { File("a.png", 100, 100), File("b.png", 500, 500) }, 1920, 1080).Mix);

    [Fact]
    public void SingleFullScreenIsTheBigOneAndTheOnlySprite()
    {
        var s = new SelectionProfile(new[] { File("big.gif", 1920, 1080) }, 1920, 1080);
        Assert.NotNull(s.Big);
        Assert.Single(s.Sprites);
    }

    [Fact]
    public void PaletteKeepsHeroColorsFirstWithoutDuplicates()
    {
        var s = new SelectionProfile(new[]
        {
            File("a.png", 200, 200, colors: new[] { "#F80808", "#080808" }),
            File("b.png", 200, 200, colors: new[] { "#080808", "#08F808", "#0808F8", "#F8F808", "#888888" }),
        }, 1920, 1080);
        Assert.Equal(new[] { "#F80808", "#080808", "#08F808", "#0808F8", "#F8F808" }, s.SharedPalette);
    }

    [Fact]
    public void ShortestLoopIgnoresStills()
    {
        var s = new SelectionProfile(new[] { File("a.gif", 200, 200, durationMs: 1500), File("b.gif", 200, 200, durationMs: 800), File("c.png", 200, 200) }, 1920, 1080);
        Assert.Equal(800, s.ShortestLoopMs);
        Assert.Null(new SelectionProfile(new[] { File("c.png", 200, 200) }, 1920, 1080).ShortestLoopMs);
    }

    [Fact]
    public void AllMonochromeNeedsEveryFile() =>
        Assert.False(new SelectionProfile(new[] { File("a.png", 200, 200), File("b.png", 200, 200, mono: false) }, 1920, 1080).AllMonochrome);

    [Fact]
    public void DescribeNamesTheHeroAndCount() =>
        Assert.Equal("a.png + 2 more", new SelectionProfile(new[] { File("a.png", 200, 200), File("b.png", 200, 200), File("c.png", 200, 200) }, 1920, 1080).Describe());

    [Fact]
    public void DuplicatePathsCountOnce()
    {
        // a Fireflies draft holds five copies of one path; re-suggesting must see one file
        var s = new SelectionProfile(new[] { File("a.png", 200, 200), File("a.png", 200, 200), File("A.PNG", 200, 200) }, 1920, 1080);
        Assert.Equal(1, s.Count);
        Assert.Equal(SelectionMix.Single, s.Mix);
    }

    [Fact]
    public void EmptySelectionThrows() =>
        Assert.Throws<ArgumentException>(() => new SelectionProfile(Array.Empty<MediaProfile>(), 1920, 1080));
}

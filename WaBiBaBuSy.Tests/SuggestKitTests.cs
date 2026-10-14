using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Models.Wallpaper.Suggest;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SuggestKitTests
{
    private static SelectionProfile One(double luma = 0.5, string[]? colors = null, long loopMs = 0, string name = "a.gif") =>
        new(new[] { SelectionProfileTests.File(name, 200, 200, colors: colors, durationMs: loopMs, luma: luma) }, 1920, 1080);

    [Fact]
    public void UnitIsInRangeAndDeterministic()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            double u = SuggestKit.Unit(seed, 3);
            Assert.InRange(u, 0.0, 0.9999999);
            Assert.Equal(u, SuggestKit.Unit(seed, 3));
        }
        Assert.NotEqual(SuggestKit.Unit(1, 3), SuggestKit.Unit(2, 3));
    }

    [Fact]
    public void RangeStaysInsideItsBounds()
    {
        for (int seed = 0; seed < 200; seed++)
            Assert.InRange(SuggestKit.Range(seed, 1, 300f, 500f), 300f, 500f);
    }

    [Fact]
    public void LightMediaGetsADarkBackgroundInItsOwnHue()
    {
        string hex = SuggestKit.BackgroundHex(One(luma: 0.8, colors: new[] { "#08F808" }));
        Assert.True(Luma(hex) < 0.12, hex);
        Assert.True(Channel(hex, 1) > Channel(hex, 0), hex); // still green
    }

    [Fact]
    public void DarkMediaGetsALightBackground() =>
        Assert.True(Luma(SuggestKit.BackgroundHex(One(luma: 0.1, colors: new[] { "#080808" }))) > 0.8);

    [Fact]
    public void ForceDarkWinsOverDarkMedia() =>
        Assert.True(Luma(SuggestKit.BackgroundHex(One(luma: 0.1, colors: new[] { "#080808" }), forceDark: true)) < 0.12);

    [Fact]
    public void NoPaletteFallsBackToNearBlack() =>
        Assert.True(Luma(SuggestKit.BackgroundHex(One(luma: 0.5, colors: Array.Empty<string>()))) < 0.12);

    [Fact]
    public void CyclesSnapToWholeLoops()
    {
        // loop 1.2 s, wanted 0.25 Hz (4 s) → 3 loops = 3.6 s
        Assert.Equal(1000.0 / 3600.0, SuggestKit.SnapCyclesPerSecond(One(loopMs: 1200), 0.25), 6);
        // faster than one loop → one loop
        Assert.Equal(1000.0 / 1200.0, SuggestKit.SnapCyclesPerSecond(One(loopMs: 1200), 5.0), 6);
        // no GIF → unchanged
        Assert.Equal(0.25, SuggestKit.SnapCyclesPerSecond(One(name: "a.png"), 0.25));
    }

    [Fact]
    public void NewSceneKeepsTargetsAndSetsTheSprites()
    {
        var baseScene = new CrossScreenConfig
        {
            SelectedMonitorIds = new() { "m1", "m2" },
            DistributionMode = AnimationDistributionMode.Simultaneous,
        };
        baseScene.Movement.Type = MovementType.Circular;
        var s = new SelectionProfile(new[] { SelectionProfileTests.File("a.png", 200, 200), SelectionProfileTests.File("b.png", 200, 200) }, 1920, 1080);

        var scene = SuggestKit.NewScene(baseScene, s, s.Sprites, 0.2);

        Assert.Equal(new[] { "m1", "m2" }, scene.SelectedMonitorIds);
        Assert.NotSame(baseScene.SelectedMonitorIds, scene.SelectedMonitorIds);
        Assert.Equal(AnimationDistributionMode.Simultaneous, scene.DistributionMode);
        Assert.Equal(@"C:\m\a.png", scene.Animation.AnimationPath);
        Assert.Equal(new[] { @"C:\m\b.png" }, scene.Animation.AdditionalAnimationPaths);
        Assert.Equal(ContentFitMode.TargetHeight, scene.Animation.FitMode);
        Assert.Equal(216, scene.Animation.TargetHeight);
        Assert.Equal(MovementType.Linear, scene.Movement.Type); // fresh, not the base's Circular
        Assert.Equal(BackgroundMode.SolidColor, scene.Background.Mode);
    }

    [Fact]
    public void PartyColorsAreFiveValidColorsRotatedBySeed()
    {
        var a = SuggestKit.PartyColors(0);
        Assert.Equal(5, a.Count);
        Assert.All(a, c => Assert.True(ColorGrader.IsValidColor(c), c));
        Assert.Equal(a.ToHashSet(), SuggestKit.PartyColors(3).ToHashSet());
    }

    private static int Channel(string hex, int i) => Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16);
    private static double Luma(string hex) => (0.2126 * Channel(hex, 0) + 0.7152 * Channel(hex, 1) + 0.0722 * Channel(hex, 2)) / 255.0;
}

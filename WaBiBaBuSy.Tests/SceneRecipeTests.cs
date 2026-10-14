using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Models.Wallpaper.Suggest;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SceneRecipeTests
{
    public static IEnumerable<object[]> Recipes() => SceneRecipes.All.Select(r => new object[] { r.Name });

    private static ISceneRecipe R(string name) => SceneRecipes.All.Single(r => r.Name == name);
    private static MediaProfile F(string name, int w, int h, bool mono = true, bool alpha = true, long ms = 0) =>
        SelectionProfileTests.File(name, w, h, mono, alpha, durationMs: ms);
    private static SelectionProfile Sel(params MediaProfile[] files) => new(files, 1920, 1080);

    /// <summary>Selections every recipe is built against (scored or not, Build must never throw).</summary>
    private static IEnumerable<SelectionProfile> Selections() => new[]
    {
        Sel(F("logo.png", 200, 200)),
        Sel(F("logo.png", 200, 200, mono: false)),
        Sel(F("cat.gif", 300, 300, ms: 1200)),
        Sel(F("word.png", 900, 200)),
        Sel(F("a.png", 150, 150), F("b.png", 160, 160), F("c.png", 140, 140)),
        Sel(F("small.gif", 200, 200, ms: 800), F("big.gif", 1920, 1080, ms: 3000)),
        Sel(F("photo.jpg", 1920, 1080, mono: false, alpha: false)),
        Sel(MediaProfile.Create(@"C:\m\broken.png", 0, 0, 1920, 1080, PixelFacts.Unknown, 0)),
    };

    private static readonly CrossScreenConfig Base = new()
    {
        SelectedMonitorIds = new() { "m1" },
        DistributionMode = AnimationDistributionMode.Simultaneous,
    };

    [Fact]
    public void ThereAreNineUniquelyNamedRecipes() =>
        Assert.Equal(9, SceneRecipes.All.Select(r => r.Name).Distinct().Count());

    [Theory, MemberData(nameof(Recipes))]
    public void BuildIsDeterministicPerSeed(string name)
    {
        foreach (var s in Selections())
            Assert.Equal(SceneFile.ToJson(R(name).Build(s, Base, 7).Scene), SceneFile.ToJson(R(name).Build(s, Base, 7).Scene));
    }

    [Theory, MemberData(nameof(Recipes))]
    public void ScoresStayBetweenZeroAndOne(string name)
    {
        foreach (var s in Selections()) Assert.InRange(R(name).Score(s), 0.0, 1.0);
    }

    [Theory, MemberData(nameof(Recipes))]
    public void OfferedScenesHaveNoWarningsAndKeepTheTargets(string name)
    {
        foreach (var s in Selections().Where(s => R(name).Score(s) > 0))
        {
            var sug = R(name).Build(s, Base, 11);
            Assert.Empty(SceneChecks.Validate(sug.Scene, null));
            Assert.Equal(new[] { "m1" }, sug.Scene.SelectedMonitorIds);
            Assert.Equal(AnimationDistributionMode.Simultaneous, sug.Scene.DistributionMode);
            Assert.Equal(name, sug.RecipeName);
            Assert.False(string.IsNullOrWhiteSpace(sug.Why));
        }
    }

    [Theory, MemberData(nameof(Recipes))]
    public void EveryRecipeBuildsAValidSceneFromUnknownPixels(string name)
    {
        var s = Sel(MediaProfile.Create(@"C:\m\broken.png", 0, 0, 1920, 1080, PixelFacts.Unknown, 0));
        var scene = R(name).Build(s, Base, 3).Scene;
        Assert.Equal(@"C:\m\broken.png", scene.Animation.AnimationPath);
    }

    [Fact]
    public void DifferentSeedsVaryTheScene() =>
        Assert.NotEqual(SceneFile.ToJson(R("DVD Bounce").Build(Selections().First(), Base, 1).Scene),
                        SceneFile.ToJson(R("DVD Bounce").Build(Selections().First(), Base, 2).Scene));

    [Fact]
    public void MonoLogoIsTintedColorfulLogoIsNot()
    {
        Assert.NotEqual(ColorGradingMode.None, R("DVD Bounce").Build(Sel(F("logo.png", 200, 200)), Base, 1).Scene.Animation.ColorGrading.Mode);
        Assert.Equal(ColorGradingMode.None, R("DVD Bounce").Build(Sel(F("logo.png", 200, 200, mono: false)), Base, 1).Scene.Animation.ColorGrading.Mode);
    }

    [Fact]
    public void OrbitSpinsOneTurnPerWholeLoops()
    {
        var spin = R("Orbit").Build(Sel(F("cat.gif", 300, 300, ms: 1200)), Base, 1).Scene.Animation.Effects!.SpinDegPerSecond;
        double secondsPerTurn = 360.0 / Math.Abs(spin);
        Assert.Equal(0, Math.Round(secondsPerTurn * 1000) % 1200);
    }

    [Fact]
    public void BigPlusSmallSpriteRecipesUseOnlyTheSmallFiles()
    {
        var s = Sel(F("small.gif", 200, 200, ms: 800), F("big.gif", 1920, 1080, ms: 3000));
        var scene = R("DVD Bounce").Build(s, Base, 1).Scene;
        Assert.Equal(@"C:\m\small.gif", scene.Animation.AnimationPath);
        Assert.Empty(scene.Animation.AdditionalAnimationPaths);
        Assert.Equal(@"C:\m\big.gif", R("Showcase").Build(s, Base, 1).Scene.Animation.AnimationPath);
    }

    [Fact]
    public void VideoSpritesOnlyGetShowcase()
    {
        var s = Sel(MediaProfile.Create(@"C:\m\clip.mp4", 800, 600, 1920, 1080, PixelFacts.Unknown, 60000));
        Assert.All(SceneRecipes.All.Where(r => r.Name != "Showcase"), r => Assert.Equal(0, r.Score(s)));
        Assert.True(R("Showcase").Score(s) > 0);
    }

    [Fact]
    public void OpaqueMediaIsNotSwarmed()
    {
        var s = Sel(F("a.jpg", 150, 150, alpha: false), F("b.jpg", 150, 150, alpha: false));
        Assert.True(R("Pulse Grid").Score(s) <= 0.3);
        Assert.True(R("Fireflies").Score(s) <= 0.3);
    }
}

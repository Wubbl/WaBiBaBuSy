using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Models.Wallpaper.Suggest;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SceneSuggesterTests
{
    private static MediaProfile F(string name, int w, int h, bool mono = true, long ms = 0) =>
        SelectionProfileTests.File(name, w, h, mono, durationMs: ms);
    private static SelectionProfile Sel(params MediaProfile[] files) => new(files, 1920, 1080);
    private static IEnumerable<string> Names(SuggestionSet set) => set.Cards.Select(c => c.RecipeName);
    private static readonly CrossScreenConfig Base = new();

    [Fact]
    public void ASmallMonoLogoGetsDvdBounce() =>
        Assert.Contains("DVD Bounce", Names(SceneSuggester.Suggest(Sel(F("logo.png", 200, 200)), Base, 1)));

    [Fact]
    public void ThreeLogosGetPulseGrid() =>
        Assert.Contains("Pulse Grid", Names(SceneSuggester.Suggest(Sel(F("a.png", 150, 150), F("b.png", 160, 160), F("c.png", 140, 140)), Base, 1)));

    [Fact]
    public void AFullScreenGifGetsShowcaseFirst() =>
        Assert.Equal("Showcase", SceneSuggester.Suggest(Sel(F("big.gif", 1920, 1080, mono: false, ms: 3000)), Base, 1).Cards[0].RecipeName);

    [Fact]
    public void ASingleMediumVideoGetsShowcase()
    {
        var set = SceneSuggester.Suggest(Sel(MediaProfile.Create(@"C:\m\clip.mp4", 800, 600, 1920, 1080, PixelFacts.Unknown, 60000)), Base, 1);
        Assert.Equal(new[] { "Showcase" }, Names(set));
    }

    [Fact]
    public void SuggestGivesFourDistinctCards()
    {
        var set = SceneSuggester.Suggest(Sel(F("logo.png", 200, 200)), Base, 1);
        Assert.Equal(SceneSuggester.CardCount, set.Cards.Count);
        Assert.Equal(set.Cards.Count, Names(set).Distinct().Count());
    }

    [Fact]
    public void SameSeedSameCards()
    {
        var s = Sel(F("logo.png", 200, 200));
        Assert.Equal(SceneSuggester.Shuffle(s, Base, 5, new[] { "DVD Bounce" }).Cards.Select(c => SceneFile.ToJson(c.Scene)),
                     SceneSuggester.Shuffle(s, Base, 5, new[] { "DVD Bounce" }).Cards.Select(c => SceneFile.ToJson(c.Scene)));
    }

    [Fact]
    public void ShuffleAvoidsShownRecipesWhenItCan()
    {
        var s = Sel(F("logo.png", 200, 200));
        var first = SceneSuggester.Suggest(s, Base, 1);
        int offered = SceneRecipes.All.Count(r => r.Score(s) > 0);
        var next = SceneSuggester.Shuffle(s, Base, 2, Names(first).ToList());
        int fresh = Names(next).Count(n => !Names(first).Contains(n));
        Assert.Equal(Math.Min(SceneSuggester.CardCount, offered - first.Cards.Count), fresh);
        Assert.Equal(Math.Min(SceneSuggester.CardCount, offered), next.Cards.Count);
    }

    [Fact]
    public void AVideoMixedWithImagesStillGetsAShowcaseOfTheVideo()
    {
        var video = MediaProfile.Create(@"C:\m\clip.mp4", 800, 600, 1920, 1080, PixelFacts.Unknown, 60000);
        var set = SceneSuggester.Suggest(Sel(F("logo.png", 200, 200), video), Base, 1);
        Assert.Equal(new[] { "Showcase" }, Names(set));
        Assert.Equal(@"C:\m\clip.mp4", set.Cards[0].Scene.Layers[0].Animation.AnimationPath);
        Assert.Equal(SceneSuggester.VideoNote, set.Note);
    }

    [Fact]
    public void CardsCarryCentimetreValuesForPhysicalRooms()
    {
        var s = new SelectionProfile(new[] { F("logo.png", 200, 200) }, 1920, 1080, pixelsPerCm: 40f);
        foreach (var card in SceneSuggester.Suggest(s, Base, 1).Cards)
        {
            Assert.Equal(card.Scene.Layers[0].Animation.TargetHeight / 40f, card.Scene.Layers[0].Animation.TargetHeightCm, 3);
            Assert.Equal(card.Scene.Layers[0].Movement.SpeedPixelsPerSecond / 40f, card.Scene.Layers[0].Movement.SpeedCmPerSecond, 3);
        }
    }

    [Fact]
    public void BigPlusSmallCarriesTheLayersNote()
    {
        Assert.Equal(SceneSuggester.LayersNote, SceneSuggester.Suggest(Sel(F("s.gif", 200, 200), F("big.gif", 1920, 1080)), Base, 1).Note);
        Assert.Null(SceneSuggester.Suggest(Sel(F("logo.png", 200, 200)), Base, 1).Note);
    }

    [Fact]
    public void RankedListsOfferedRecipesBestFirst()
    {
        var s = Sel(F("logo.png", 200, 200));
        var ranked = SceneSuggester.Ranked(s);
        Assert.Equal("DVD Bounce", ranked[0].Name);
        Assert.Equal(SceneRecipes.All.Count(r => r.Score(s) > 0), ranked.Count);
        for (int i = 1; i < ranked.Count; i++) Assert.True(ranked[i - 1].Score(s) >= ranked[i].Score(s));
    }

    [Fact]
    public void BuildCardAddsCentimetres()
    {
        var s = new SelectionProfile(new[] { F("logo.png", 200, 200) }, 1920, 1080, pixelsPerCm: 40f);
        var card = SceneSuggester.BuildCard(SceneSuggester.Ranked(s)[0], s, Base, 3);
        Assert.Equal(card.Scene.Layers[0].Animation.TargetHeight / 40f, card.Scene.Layers[0].Animation.TargetHeightCm, 3);
    }

    [Fact]
    public void SuggestIsTheTopOfRanked() =>
        Assert.Equal(SceneSuggester.Ranked(Sel(F("logo.png", 200, 200))).Take(SceneSuggester.CardCount).Select(r => r.Name),
                     Names(SceneSuggester.Suggest(Sel(F("logo.png", 200, 200)), Base, 1)));
}

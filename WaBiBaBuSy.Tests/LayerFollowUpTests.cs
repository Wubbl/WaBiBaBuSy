using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Models.Wallpaper.Suggest;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Scene-layer follow-ups (roadmap 2.3): spawn windows and Suggest "add as layer".</summary>
public class LayerFollowUpTests
{
    [Fact]
    public void SpawnWindow_ShowsForItsDurationEveryCycle_WithFades()
    {
        // Every 60 s for 10 s.
        Assert.Equal(1f, LayerSchedule.Visibility(0, 10_000, 123_456));        // no window: always shown
        Assert.Equal(0f, LayerSchedule.Visibility(60_000, 10_000, 0));         // fading in from 0
        Assert.Equal(0.5f, LayerSchedule.Visibility(60_000, 10_000, 125), 3);
        Assert.Equal(1f, LayerSchedule.Visibility(60_000, 10_000, 5_000));
        Assert.Equal(0.5f, LayerSchedule.Visibility(60_000, 10_000, 9_875), 3); // fading out
        Assert.Equal(0f, LayerSchedule.Visibility(60_000, 10_000, 30_000));     // hidden
        Assert.Equal(1f, LayerSchedule.Visibility(60_000, 10_000, 125_000));    // next cycles too
        Assert.Equal(1f, LayerSchedule.Visibility(60_000, 10_000, -55_000));    // before the start: same cycle math (never drawn then anyway)
        Assert.Equal(1f, LayerSchedule.Visibility(5_000, 9_000, 3_000));        // longer than the cycle: always shown
    }

    [Fact]
    public void SpawnWindow_SurvivesASceneFileRoundTrip()
    {
        var scene = new CrossScreenConfig { Layers = { new SceneLayer { ShowEveryMs = 60_000, ShowForMs = 8_000 } } };
        var back = SceneFile.Clone(scene);
        Assert.Equal(60_000, back.Layers[0].ShowEveryMs);
        Assert.Equal(8_000, back.Layers[0].ShowForMs);
    }

    private static SceneLayer Sprite(string file) => new() { Name = file, Animation = new AnimationLayerConfig { AnimationPath = file } };

    [Fact]
    public void AddAsLayers_KeepsTheBaseScene_AndPutsTheCardsSpritesOnTop()
    {
        var baseScene = new CrossScreenConfig
        {
            Background = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = "#112233" },
            Layers = { Sprite("rain.gif") },
        };
        var card = new CrossScreenConfig
        {
            Background = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = "#FFFFFF" },
            Layers = { Sprite("cat.gif") },
        };
        var merged = SceneSuggester.AddAsLayers(baseScene, card);
        Assert.Equal("#112233", merged.Background.ColorHex);
        Assert.Equal(new[] { "rain.gif", "cat.gif" }, merged.Layers.Select(l => l.Animation.AnimationPath));
        Assert.Single(baseScene.Layers);   // inputs untouched
    }

    [Fact]
    public void AddAsLayers_DropsAFreshDraftsEmptyLayer_AndStopsAtTheLayerLimit()
    {
        var fresh = new CrossScreenConfig { Layers = { new SceneLayer() } };
        Assert.Equal(new[] { "cat.gif" },
            SceneSuggester.AddAsLayers(fresh, new CrossScreenConfig { Layers = { Sprite("cat.gif") } }).Layers.Select(l => l.Animation.AnimationPath));

        var full = new CrossScreenConfig();
        for (int i = 0; i < SceneChecks.MaxLayers; i++) full.Layers.Add(Sprite($"l{i}.png"));
        Assert.Equal(SceneChecks.MaxLayers, SceneSuggester.AddAsLayers(full, new CrossScreenConfig { Layers = { Sprite("cat.gif") } }).Layers.Count);
    }
}

using System.Text.Json;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Pre-layers scene JSON (one Animation + Movement + DistributionMode) loads as layer 0, forever.</summary>
public class LegacySceneMigrationTests
{
    private const string Legacy = """
    {
      "Background": { "Mode": "SolidColor", "ColorHex": "#202020" },
      "Animation": { "AnimationPath": "C:\\art\\fish.gif", "TargetHeight": 64, "FitMode": "TargetHeight", "FaceTravelDirection": true,
                     "AdditionalAnimationPaths": [ "C:\\art\\b.png" ], "Effects": { "SpinDegPerSecond": 90 } },
      "Movement": { "Type": "Bounce", "SpeedPixelsPerSecond": 600, "DirectionAngleDegrees": 33, "NodePhaseDelayMs": 250 },
      "DistributionMode": "Simultaneous",
      "AnimationSpeedPxPerSecond": 500,
      "SelectedMonitorIds": [ "a" ]
    }
    """;

    private static CrossScreenConfig Load(string json) => JsonSerializer.Deserialize<CrossScreenConfig>(json, SceneFile.JsonOptions)!;

    [Fact]
    public void Legacy_BecomesOneLayer_WithEveryField()
    {
        var scene = Load(Legacy);

        var layer = Assert.Single(scene.Layers);
        Assert.Equal("fish", layer.Name);
        Assert.True(layer.Enabled);
        Assert.Equal(1f, layer.Opacity);
        Assert.Equal(AnimationDistributionMode.Simultaneous, layer.Distribution);
        Assert.Equal(@"C:\art\fish.gif", layer.Animation.AnimationPath);
        Assert.Equal(64, layer.Animation.TargetHeight);
        Assert.Equal(ContentFitMode.TargetHeight, layer.Animation.FitMode);
        Assert.True(layer.Animation.FaceTravelDirection);
        Assert.Equal(new[] { @"C:\art\b.png" }, layer.Animation.AdditionalAnimationPaths);
        Assert.Equal(90f, layer.Animation.Effects!.SpinDegPerSecond);
        Assert.Equal(MovementType.Bounce, layer.Movement.Type);
        Assert.Equal(600f, layer.Movement.SpeedPixelsPerSecond);
        Assert.Equal(33f, layer.Movement.DirectionAngleDegrees);
        Assert.Equal(250, layer.Movement.NodePhaseDelayMs);
        Assert.Equal("#202020", scene.Background.ColorHex);
        Assert.Equal(new[] { "a" }, scene.SelectedMonitorIds);
        Assert.Null(scene.Legacy);
    }

    [Fact]
    public void Legacy_EnumsAsNumbers_DefaultOptions()
    {
        // Playlists were written with the default serializer: enums as numbers, PascalCase, no converters.
        var json = """{ "Animation": { "AnimationPath": "x.png" }, "Movement": { "Type": 2 }, "DistributionMode": 1 }""";
        var scene = JsonSerializer.Deserialize<CrossScreenConfig>(json)!;

        var layer = Assert.Single(scene.Layers);
        Assert.Equal(MovementType.Bounce, layer.Movement.Type);
        Assert.Equal(AnimationDistributionMode.Simultaneous, layer.Distribution);
    }

    [Fact]
    public void Legacy_LowerCaseKeys_Migrate()
    {
        var json = """{ "animation": { "animationPath": "x.png" }, "movement": { "type": "Linear" }, "distributionMode": "Simultaneous" }""";
        var layer = Assert.Single(Load(json).Layers);
        Assert.Equal("x.png", layer.Animation.AnimationPath);
        Assert.Equal(MovementType.Linear, layer.Movement.Type);
        Assert.Equal(AnimationDistributionMode.Simultaneous, layer.Distribution);
    }

    [Fact]
    public void LayersPresent_LegacyKeysIgnored()
    {
        var json = """
        { "Layers": [ { "Name": "Top", "Animation": { "AnimationPath": "new.png" } } ],
          "Animation": { "AnimationPath": "old.png" } }
        """;
        var layer = Assert.Single(Load(json).Layers);
        Assert.Equal("new.png", layer.Animation.AnimationPath);
    }

    [Fact]
    public void NoSpriteKeys_NoLayers()
    {
        var scene = Load("""{ "Background": { "Mode": "SolidColor" }, "DistributionMode": "Simultaneous" }""");
        Assert.Empty(scene.Layers);
        Assert.Null(scene.FirstLayer);
    }

    [Fact]
    public void NullLayers_LoadsAsEmpty()
    {
        Assert.Empty(Load("""{ "Layers": null }""").Layers);
    }

    [Fact]
    public void Saved_HasNoLegacyKeys_AndRoundTrips()
    {
        var json = SceneFile.ToJson(Load(Legacy));

        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Animation", keys);
        Assert.DoesNotContain("Movement", keys);
        Assert.DoesNotContain("DistributionMode", keys);
        Assert.DoesNotContain("AnimationSpeedPxPerSecond", keys);
        Assert.Contains("Layers", keys);

        Assert.Equal(json, SceneFile.ToJson(Load(json)));
    }

    [Fact]
    public void Clone_KeepsEveryLayer()
    {
        var scene = Load(Legacy);
        scene.Layers.Add(new SceneLayer { Name = "Top", Opacity = 0.5f, PhaseOffsetMs = 7000 });

        var clone = SceneFile.Clone(scene);

        Assert.Equal(2, clone.Layers.Count);
        Assert.Equal(0.5f, clone.Layers[1].Opacity);
        Assert.Equal(7000, clone.Layers[1].PhaseOffsetMs);
        Assert.NotSame(scene.Layers[0], clone.Layers[0]);
    }

    public static IEnumerable<object[]> RepoSceneFiles()
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "TestScenarios"))) root = Path.GetDirectoryName(root);
        foreach (var dir in new[] { "scenes", Path.Combine("showcase", "scenes") })
            foreach (var file in Directory.GetFiles(Path.Combine(root!, "TestScenarios", dir), "*.json"))
                yield return new object[] { file };
    }

    [Theory]
    [MemberData(nameof(RepoSceneFiles))]
    public void RepoSceneFiles_LoadWithAnExistingBottomFile(string file)
    {
        var scene = SceneFile.Load(file);   // throws on a missing asset

        Assert.NotEmpty(scene.Layers);
        Assert.True(File.Exists(scene.Layers[0].Animation.AnimationPath), scene.Layers[0].Animation.AnimationPath);
        Assert.Null(scene.Legacy);
    }
}

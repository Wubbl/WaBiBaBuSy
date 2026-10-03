using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SceneFileWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-scenefile-{Guid.NewGuid():N}");

    public SceneFileWriteTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private string Asset(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
        return p;
    }

    [Fact]
    public void ToJson_PascalCaseEnumNames_LoadsBack()
    {
        var logo = Asset("logo.png");
        var config = new CrossScreenConfig
        {
            Animation = new AnimationLayerConfig
            {
                AnimationPath = logo,
                Effects = new SpriteEffectsConfig { Entry = BezelEffect.Ripple },
            },
            Movement = new MovementConfig { Type = MovementType.Bounce, SpeedPixelsPerSecond = 420 },
            DistributionMode = AnimationDistributionMode.Simultaneous,
        };

        var json = SceneFile.ToJson(config);
        Assert.Contains("\"AnimationPath\"", json);
        Assert.Contains("\"Bounce\"", json);
        Assert.Contains("\"Simultaneous\"", json);
        Assert.Contains("\"Ripple\"", json);
        Assert.Contains("\n", json);   // indented, hand-editable

        var path = Path.Combine(_dir, "scene.json");
        File.WriteAllText(path, json);
        var back = SceneFile.Load(path);
        Assert.Equal(logo, back.Animation.AnimationPath);
        Assert.Equal(MovementType.Bounce, back.Movement.Type);
        Assert.Equal(420, back.Movement.SpeedPixelsPerSecond);
        Assert.Equal(BezelEffect.Ripple, back.Animation.Effects!.Entry);
    }

    [Fact]
    public void ToJson_NonAsciiPathNotEscaped()
    {
        var json = SceneFile.ToJson(new CrossScreenConfig { Animation = new AnimationLayerConfig { AnimationPath = @"C:\Bilder\Größe.png" } });
        Assert.Contains("Größe.png", json);
    }

    // A hand-edited preset with a nested null used to load fine and crash the editor (NullReferenceException in
    // LoadFromConfig) — it must be reported like a top-level null.
    [Theory]
    [InlineData("""{ "Animation": { "AnimationPath": "logo.png", "ColorGrading": null } }""", "Animation.ColorGrading")]
    [InlineData("""{ "Animation": { "AnimationPath": "logo.png", "ColorGrading": { "ColorList": null } } }""", "Animation.ColorGrading.ColorList")]
    [InlineData("""{ "Animation": { "AnimationPath": "logo.png" }, "Background": { "IconZonePaletteHexes": null } }""", "Background.IconZonePaletteHexes")]
    [InlineData("""{ "Animation": { "AnimationPath": "logo.png" }, "SelectedMonitorIds": null }""", "SelectedMonitorIds")]
    public void Load_NestedNull_ThrowsScenarioException(string json, string member)
    {
        Asset("logo.png");
        var path = Path.Combine(_dir, "scene.json");
        File.WriteAllText(path, json);

        var ex = Assert.Throws<ScenarioException>(() => SceneFile.Load(path));
        Assert.Contains(member + " is null", ex.Message);
    }

    [Fact]
    public void Clone_IsDeepAndEqual()
    {
        var config = new CrossScreenConfig
        {
            Animation = new AnimationLayerConfig { AnimationPath = "a.png", AdditionalAnimationPaths = new List<string> { "b.png" } },
            SelectedMonitorIds = new List<string> { "N1" },
        };
        var copy = SceneFile.Clone(config);

        Assert.Equal(SceneFile.ToJson(config), SceneFile.ToJson(copy));
        copy.Animation.AdditionalAnimationPaths.Add("c.png");
        copy.SelectedMonitorIds.Clear();
        Assert.Single(config.Animation.AdditionalAnimationPaths);
        Assert.Single(config.SelectedMonitorIds);
    }
}

using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class PresetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wbbs-presets-{Guid.NewGuid():N}");
    private readonly PresetStore _store;

    public PresetStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new PresetStore(Path.Combine(_root, "presets"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private string Asset(string relative, byte fill = 1)
    {
        var p = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, Enumerable.Repeat(fill, 32).ToArray());
        return p;
    }

    /// <summary>A showcase-style scene: comments, enum names, ../assets path, targets.</summary>
    private string ShowcaseScene(string name = "comet")
    {
        Asset("show/assets/logo.png");
        var path = Path.Combine(_root, "show", "scenes", name + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
        {
          // a comment, like the showcase files
          "Animation": { "AnimationPath": "../assets/logo.png" },
          "Movement": { "Type": "Linear", "SpeedPixelsPerSecond": 900 },
          "DistributionMode": "Sequential",
          "SelectedMonitorIds": ["SOMEONE"],
        }
        """);
        return path;
    }

    private static CrossScreenConfig Scene(string path) => new()
    {
        Layers = { new SceneLayer { Animation = new AnimationLayerConfig { AnimationPath = path }, Movement = new MovementConfig { Type = MovementType.Linear } } },

        SelectedMonitorIds = new List<string> { "N1", "N2" },
    };

    [Fact]
    public async Task Save_Load_Roundtrip_StripsTargets_KeepsGalleryPath()
    {
        var logo = Asset("gallery/logo.png");
        var saved = await _store.SaveAsync("Comet", Scene(logo));

        Assert.Equal("Comet", saved);
        var back = await _store.LoadAsync("Comet");
        Assert.Equal(logo, back.Layers[0].Animation.AnimationPath);
        Assert.Empty(back.SelectedMonitorIds);
        Assert.False(Directory.Exists(Path.Combine(_store.Directory, "assets")));   // gallery files are referenced, not copied
        var json = File.ReadAllText(_store.PathFor("Comet"));
        Assert.Contains("\"Linear\"", json);
        Assert.Contains("\"AnimationPath\"", json);
    }

    [Fact]
    public async Task Save_DoesNotMutateCallerConfig()
    {
        var config = Scene(Asset("gallery/logo.png"));
        await _store.SaveAsync("x", config);
        Assert.Equal(2, config.SelectedMonitorIds.Count);
    }

    [Fact]
    public async Task Save_PathUnderStore_WrittenRelative()
    {
        var name = await _store.ImportAsync(ShowcaseScene());
        var loaded = await _store.LoadAsync(name);   // absolute path inside presets\assets
        await _store.SaveAsync("copy", loaded);

        Assert.Contains("\"assets/logo.png\"", File.ReadAllText(_store.PathFor("copy")));
    }

    [Fact]
    public async Task Import_NonMediaAsset_IsRejected_AndNothingIsCopied()
    {
        // Review 2026-10-13 B4: a handed-over preset pointing at ..\secret.txt used to bundle it and send it to every client.
        Asset("show/secret.txt");
        var path = Path.Combine(_root, "show", "scenes", "evil.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Animation": { "AnimationPath": "../secret.txt" } }""");

        var ex = await Assert.ThrowsAsync<ScenarioException>(() => _store.ImportAsync(path));
        Assert.Contains("secret.txt", ex.Message);
        Assert.Empty(_store.List());
        Assert.False(Directory.Exists(Path.Combine(_store.Directory, "assets")));
    }

    [Fact]
    public async Task List_AlphabeticalNamesOnly()
    {
        var logo = Asset("gallery/logo.png");
        await _store.SaveAsync("b", Scene(logo));
        await _store.SaveAsync("A", Scene(logo));
        await _store.ImportAsync(ShowcaseScene("c"));   // creates presets\assets\

        Assert.Equal(new[] { "A", "b", "c" }, _store.List());
    }

    [Fact]
    public void List_MissingFolder_Empty() => Assert.Empty(_store.List());

    [Theory]
    [InlineData("a/b:c", "a_b_c")]
    [InlineData("CON", "CON_")]
    [InlineData("  ", "preset")]
    [InlineData("Comet.", "Comet")]
    [InlineData(" Comet ", "Comet")]
    public void Sanitize_MakesASafeFileStem(string name, string expected) => Assert.Equal(expected, PresetStore.Sanitize(name));

    [Theory]
    [InlineData("../evil")]
    [InlineData("..")]
    [InlineData(@"..\..\evil")]
    public void PathFor_StaysInsideDirectory(string name) =>
        Assert.Equal(Path.GetFullPath(_store.Directory), Path.GetDirectoryName(Path.GetFullPath(_store.PathFor(name))));

    [Fact]
    public async Task UniqueName_AppendsCounter()
    {
        var logo = Asset("gallery/logo.png");
        Assert.Equal("Comet", _store.UniqueName("Comet"));
        await _store.SaveAsync("Comet", Scene(logo));
        Assert.Equal("Comet (2)", _store.UniqueName("Comet"));
        await _store.SaveAsync("Comet (2)", Scene(logo));
        Assert.Equal("Comet (3)", _store.UniqueName("Comet"));
    }

    [Fact]
    public async Task Import_ShowcaseScene_CopiesAssetsAndStripsTargets()
    {
        var name = await _store.ImportAsync(ShowcaseScene());

        Assert.Equal("comet", name);
        Assert.True(File.Exists(Path.Combine(_store.Directory, "assets", "logo.png")));
        Assert.Contains("\"assets/logo.png\"", File.ReadAllText(_store.PathFor(name)));
        var back = await _store.LoadAsync(name);
        Assert.Equal(Path.Combine(_store.Directory, "assets", "logo.png"), back.Layers[0].Animation.AnimationPath);
        Assert.Empty(back.SelectedMonitorIds);
        Assert.Equal(900, back.Layers[0].Movement.SpeedPixelsPerSecond);
    }

    [Fact]
    public async Task Import_Twice_UniqueNameAndAssetsReused()
    {
        var source = ShowcaseScene();
        Assert.Equal("comet", await _store.ImportAsync(source));
        Assert.Equal("comet (2)", await _store.ImportAsync(source));
        Assert.Single(Directory.GetFiles(Path.Combine(_store.Directory, "assets")));
    }

    [Fact]
    public async Task Import_ExplicitName_Overwrites()
    {
        await _store.ImportAsync(ShowcaseScene(), "Comet");
        await _store.ImportAsync(ShowcaseScene(), "Comet");
        Assert.Equal(new[] { "Comet" }, _store.List());
    }

    [Fact]
    public async Task Import_MissingAsset_ThrowsAndWritesNothing()
    {
        var path = Path.Combine(_root, "bad.json");
        File.WriteAllText(path, """{ "Animation": { "AnimationPath": "nope.png" } }""");

        await Assert.ThrowsAsync<ScenarioException>(() => _store.ImportAsync(path));
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Export_WritesJsonAndAssetsFolder_ThatImportsBack()
    {
        var target = Path.Combine(_root, "usb", "Comet.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var config = Scene(Asset("gallery/logo.png"));

        await PresetStore.ExportAsync(config, target);

        Assert.True(File.Exists(Path.Combine(_root, "usb", "assets", "logo.png")));
        var json = File.ReadAllText(target);
        Assert.Contains("\"assets/logo.png\"", json);
        Assert.DoesNotContain("N1", json);
        Assert.Equal("Comet", await _store.ImportAsync(target));
    }

    [Fact]
    public async Task Load_Corrupt_ThrowsScenarioException()
    {
        Directory.CreateDirectory(_store.Directory);
        File.WriteAllText(_store.PathFor("broken"), "{ not json");
        Assert.Contains("broken", _store.List());
        await Assert.ThrowsAsync<ScenarioException>(() => _store.LoadAsync("broken"));
    }

    [Fact]
    public async Task Changed_RaisedOnSaveImportDelete()
    {
        int changes = 0;
        _store.Changed += (_, _) => changes++;
        await _store.SaveAsync("a", Scene(Asset("gallery/logo.png")));
        await _store.ImportAsync(ShowcaseScene());
        _store.Delete("a");
        Assert.Equal(3, changes);
        Assert.Equal(new[] { "comet" }, _store.List());
    }
}

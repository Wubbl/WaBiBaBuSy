using WaBiBaBuSy.Core.Services.Animation;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class PresetSeederTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wbbs-seed-{Guid.NewGuid():N}");
    private readonly PresetStore _store;
    private string Starter => Path.Combine(_root, "starter");

    public PresetSeederTests()
    {
        Directory.CreateDirectory(_root);
        _store = new PresetStore(Path.Combine(_root, "presets"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private void WriteStarter(string manifest)
    {
        Directory.CreateDirectory(Path.Combine(Starter, "scenes"));
        Directory.CreateDirectory(Path.Combine(Starter, "assets"));
        File.WriteAllBytes(Path.Combine(Starter, "assets", "logo.png"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(Starter, "scenes", "a.json"), """{ "Animation": { "AnimationPath": "../assets/logo.png" } }""");
        File.WriteAllText(Path.Combine(Starter, "scenes", "b.json"), """{ "Animation": { "AnimationPath": "../assets/logo.png" } }""");
        File.WriteAllText(Path.Combine(Starter, PresetSeeder.ManifestFileName), manifest);
    }

    private const string TwoEntries = """
    {
      // comment allowed
      "presets": [
        { "name": "Alpha", "scene": "scenes/a.json" },
        { "name": "Beta", "scene": "scenes/b.json" },
      ]
    }
    """;

    [Fact]
    public async Task SeedIfEmpty_ImportsEveryEntry()
    {
        WriteStarter(TwoEntries);
        Assert.Equal(2, await PresetSeeder.SeedIfEmptyAsync(_store, Starter));
        Assert.Equal(new[] { "Alpha", "Beta" }, _store.List());
        Assert.Single(Directory.GetFiles(Path.Combine(_store.Directory, "assets")));
    }

    [Fact]
    public async Task SeedIfEmpty_NotEmpty_DoesNothing()
    {
        WriteStarter(TwoEntries);
        await _store.ImportAsync(Path.Combine(Starter, "scenes", "a.json"), "Mine");
        Assert.Equal(0, await PresetSeeder.SeedIfEmptyAsync(_store, Starter));
        Assert.Equal(new[] { "Mine" }, _store.List());
    }

    [Fact]
    public async Task SeedIfEmpty_BadEntrySkipped()
    {
        WriteStarter("""{ "presets": [ { "name": "Gone", "scene": "scenes/missing.json" }, { "name": "Alpha", "scene": "scenes/a.json" } ] }""");
        Assert.Equal(1, await PresetSeeder.SeedIfEmptyAsync(_store, Starter));
        Assert.Equal(new[] { "Alpha" }, _store.List());
    }

    [Fact]
    public async Task SeedIfEmpty_MissingStarterDir_ReturnsZero()
    {
        Assert.Equal(0, await PresetSeeder.SeedIfEmptyAsync(_store, Path.Combine(_root, "nowhere")));
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task SeedIfEmpty_CorruptManifest_ReturnsZero()
    {
        WriteStarter("{ nope");
        Assert.Equal(0, await PresetSeeder.SeedIfEmptyAsync(_store, Starter));
    }

    [Fact]
    public async Task ShippedShowcaseManifest_SeedsSelfContainedPresets()
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "TestScenarios"))) root = Path.GetDirectoryName(root);
        var showcase = Path.Combine(root!, "TestScenarios", "showcase");

        int seeded = await PresetSeeder.SeedIfEmptyAsync(_store, showcase);

        Assert.Equal(14, seeded);
        Assert.Contains("Comet", _store.List());
        Assert.Contains("Party Colors", _store.List());
        Assert.Contains("Ring Cat", _store.List());
        Assert.DoesNotContain(_store.List(), n => n.Contains("Lunar", StringComparison.OrdinalIgnoreCase));
        var storeDir = Path.GetFullPath(_store.Directory) + Path.DirectorySeparatorChar;
        foreach (var name in _store.List())
        {
            var scene = await _store.LoadAsync(name);
            Assert.StartsWith(storeDir, scene.Animation.AnimationPath, StringComparison.OrdinalIgnoreCase);
            Assert.All(scene.Animation.AdditionalAnimationPaths, p => Assert.StartsWith(storeDir, p, StringComparison.OrdinalIgnoreCase));
        }
    }
}

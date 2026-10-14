using System.Text.RegularExpressions;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class PresetBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wbbs-bundle-{Guid.NewGuid():N}");
    private string Src => Path.Combine(_root, "src");
    private string Out => Path.Combine(_root, "out");

    public PresetBundleTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Out);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static string WriteFile(string dir, string name, byte fill)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, Enumerable.Repeat(fill, 64).ToArray());
        return p;
    }

    private static CrossScreenConfig Scene(string primary, string? extra = null, string? background = null) => new()
    {
        Layers = { new SceneLayer { Animation = new AnimationLayerConfig
        {
            AnimationPath = primary,
            AdditionalAnimationPaths = extra == null ? new List<string>() : new List<string> { primary, extra },
        } } },

        Background = background == null
            ? new BackgroundLayerConfig()
            : new BackgroundLayerConfig { Mode = BackgroundMode.StretchedImage, ImagePath = background },
    };

    [Fact]
    public async Task Bundle_CopiesEveryAssetAndRewritesRelative()
    {
        var logo = WriteFile(Src, "logo.png", 1);
        var b = WriteFile(Src, "b.png", 2);
        var bg = WriteFile(Src, "bg.jpg", 3);
        var config = Scene(logo, b, bg);

        await PresetBundle.BundleAssetsAsync(config, Out);

        Assert.Equal("assets/logo.png", config.Layers[0].Animation.AnimationPath);
        Assert.Equal(new[] { "assets/logo.png", "assets/b.png" }, config.Layers[0].Animation.AdditionalAnimationPaths);
        Assert.Equal("assets/bg.jpg", config.Background.ImagePath);
        Assert.True(File.Exists(Path.Combine(Out, "assets", "logo.png")));
        Assert.True(File.Exists(Path.Combine(Out, "assets", "b.png")));
        Assert.True(File.Exists(Path.Combine(Out, "assets", "bg.jpg")));
        Assert.True(File.Exists(logo));   // copied, not moved
    }

    [Fact]
    public async Task Bundle_SameNameSameContent_ReusesFile()
    {
        var logo = WriteFile(Src, "logo.png", 1);
        WriteFile(Path.Combine(Out, "assets"), "logo.png", 1);
        var config = Scene(logo);

        await PresetBundle.BundleAssetsAsync(config, Out);

        Assert.Equal("assets/logo.png", config.Layers[0].Animation.AnimationPath);
        Assert.Single(Directory.GetFiles(Path.Combine(Out, "assets")));
    }

    [Fact]
    public async Task Bundle_SameNameDifferentContent_GetsHashSuffix()
    {
        var logo = WriteFile(Src, "logo.png", 1);
        var existing = WriteFile(Path.Combine(Out, "assets"), "logo.png", 9);
        var config = Scene(logo);

        await PresetBundle.BundleAssetsAsync(config, Out);

        Assert.Matches(new Regex("^assets/logo-[0-9a-f]{8}\\.png$"), config.Layers[0].Animation.AnimationPath);
        Assert.Equal(9, File.ReadAllBytes(existing)[0]);   // the other preset's file is untouched
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Out, "assets")).Length);

        // Bundling the same file again reuses the suffixed copy.
        var again = Scene(logo);
        await PresetBundle.BundleAssetsAsync(again, Out);
        Assert.Equal(config.Layers[0].Animation.AnimationPath, again.Layers[0].Animation.AnimationPath);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Out, "assets")).Length);
    }

    [Fact]
    public async Task Bundle_MissingAsset_ThrowsNamingTheFile()
    {
        var config = Scene(Path.Combine(Src, "gone.png"));
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => PresetBundle.BundleAssetsAsync(config, Out));
        Assert.Contains("gone.png", ex.Message);
    }

    [Fact]
    public async Task Bundle_RelativeAsset_Throws()
    {
        var config = Scene("logo.png");
        await Assert.ThrowsAsync<FileNotFoundException>(() => PresetBundle.BundleAssetsAsync(config, Out));
    }

    [Fact]
    public void RelativizeUnder_OnlyPathsInsideTheFolder()
    {
        var inside = Path.Combine(Out, "assets", "logo.png");
        var outside = Path.Combine(Src, "b.png");
        var config = Scene(inside, outside);

        PresetBundle.RelativizeUnder(config, Out);

        Assert.Equal("assets/logo.png", config.Layers[0].Animation.AnimationPath);
        Assert.Equal(new[] { "assets/logo.png", outside }, config.Layers[0].Animation.AdditionalAnimationPaths);
    }
}

using System.Security.Cryptography;
using System.Text;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ContentIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-content-{Guid.NewGuid():N}");
    public ContentIdentityTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Format_IsNamePlus16HexLower()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("x"));
        var id = ContentIdentity.Format("logo.gif", hash);
        Assert.StartsWith("logo.gif-", id);
        Assert.Equal("logo.gif-".Length + 16, id.Length);
        Assert.True(ContentIdentity.HasHash(id));
        Assert.False(ContentIdentity.HasHash("logo.gif"));
        Assert.False(ContentIdentity.HasHash("logo-notahash-value"));
    }

    [Fact]
    public void ComputeId_ChangesWithBytes_StableAcrossCalls_UsesFileName()
    {
        var a = Path.Combine(_dir, "logo.gif");
        File.WriteAllText(a, "AAAA");
        var id1 = ContentIdentity.ComputeId(a);
        var id2 = ContentIdentity.ComputeId(a);
        Assert.Equal(id1, id2);
        Assert.StartsWith("logo.gif-", id1);

        // same name, different bytes (ensure mtime differs so the cache is bypassed)
        File.WriteAllText(a, "BBBB");
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddSeconds(5));
        var id3 = ContentIdentity.ComputeId(a);
        Assert.NotEqual(id1, id3);

        // same bytes under another name → different id (name is part of it) but same hash suffix
        var b = Path.Combine(_dir, "other.gif");
        File.WriteAllText(b, "BBBB");
        Assert.Equal(id3[^16..], ContentIdentity.ComputeId(b)[^16..]);
    }

    [Fact]
    public void ComputeId_MissingFile_FallsBackToName()
    {
        Assert.Equal("nope.gif", ContentIdentity.ComputeId(Path.Combine(_dir, "nope.gif")));
    }
}

public class ContentCacheIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-cache-{Guid.NewGuid():N}");
    public ContentCacheIndexTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Roundtrip_DropsEntriesWhoseFileIsGone()
    {
        var present = Path.Combine(_dir, "a.gif"); File.WriteAllText(present, "a");
        var gone = Path.Combine(_dir, "b.gif");
        ContentCacheIndex.Save(_dir, new Dictionary<string, string> { ["a-1"] = present, ["b-2"] = gone });
        var loaded = ContentCacheIndex.Load(_dir);
        Assert.Single(loaded);
        Assert.Equal(present, loaded["a-1"]);
    }

    [Fact]
    public void Load_Missing_Or_Corrupt_IsEmpty()
    {
        Assert.Empty(ContentCacheIndex.Load(_dir));
        File.WriteAllText(Path.Combine(_dir, ContentCacheIndex.FileName), "{{{");
        Assert.Empty(ContentCacheIndex.Load(_dir));
    }
}

public class SceneAssetsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-assets-{Guid.NewGuid():N}");
    public SceneAssetsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void CollectPaths_PrimaryAdditionalBackground_Deduplicated()
    {
        var cfg = new CrossScreenConfig
        {
            Animation = new AnimationLayerConfig { AnimationPath = @"C:\a\fish.gif", AdditionalAnimationPaths = { @"C:\a\b.png", @"C:\a\fish.gif", "" } },
            Background = new BackgroundLayerConfig { Mode = BackgroundMode.TiledImage, ImagePath = @"C:\a\bg.jpg" }
        };
        var paths = SceneAssets.CollectPaths(cfg);
        Assert.Equal(3, paths.Count);
        Assert.Equal((@"C:\a\fish.gif", AssetRole.Primary), paths[0]);
        Assert.Equal((@"C:\a\b.png", AssetRole.Additional), paths[1]);
        Assert.Equal((@"C:\a\bg.jpg", AssetRole.Background), paths[2]);
    }

    [Fact]
    public void CollectPaths_SolidBackground_HasNoBackgroundAsset()
    {
        var cfg = new CrossScreenConfig
        {
            Animation = new AnimationLayerConfig { AnimationPath = @"C:\a\fish.gif" },
            Background = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ImagePath = @"C:\a\ignored.jpg" }
        };
        Assert.Single(SceneAssets.CollectPaths(cfg));
    }

    [Fact]
    public void RewriteToLocal_MapsKnown_KeepsExistingLocal_DropsMissing_DegradesBackground()
    {
        var localB = Path.Combine(_dir, "b.png"); File.WriteAllText(localB, "b");
        var localExisting = Path.Combine(_dir, "here.png"); File.WriteAllText(localExisting, "h");
        var anim = new AnimationLayerConfig
        {
            AnimationPath = @"X:\server\fish.gif",
            AdditionalAnimationPaths = { @"X:\server\b.png", localExisting, @"X:\server\missing.png" },
            TargetHeight = 333
        };
        var bg = new BackgroundLayerConfig { Mode = BackgroundMode.StretchedImage, ImagePath = @"X:\server\bg.jpg", ColorHex = "#123456" };
        var map = new Dictionary<string, string> { [@"X:\server\b.png"] = localB };

        var (a2, bg2) = SceneAssets.RewriteToLocal(anim, bg, map, primaryLocalPath: Path.Combine(_dir, "fish.gif"));

        Assert.Equal(Path.Combine(_dir, "fish.gif"), a2.AnimationPath);
        Assert.Equal(new[] { localB, localExisting }, a2.AdditionalAnimationPaths);
        Assert.Equal(333, a2.TargetHeight);
        Assert.Equal(BackgroundMode.SolidColor, bg2.Mode);      // bg.jpg unknown + missing → solid
        Assert.Equal("#123456", bg2.ColorHex);
        // inputs untouched
        Assert.Equal(3, anim.AdditionalAnimationPaths.Count);
        Assert.Equal(BackgroundMode.StretchedImage, bg.Mode);
    }

    [Fact]
    public void RewriteToLocal_BackgroundMapped_StaysImage()
    {
        var localBg = Path.Combine(_dir, "bg.jpg"); File.WriteAllText(localBg, "x");
        var bg = new BackgroundLayerConfig { Mode = BackgroundMode.TiledImage, ImagePath = @"X:\bg.jpg" };
        var (_, bg2) = SceneAssets.RewriteToLocal(new AnimationLayerConfig(), bg, new Dictionary<string, string> { [@"X:\bg.jpg"] = localBg }, null);
        Assert.Equal(BackgroundMode.TiledImage, bg2.Mode);
        Assert.Equal(localBg, bg2.ImagePath);
    }
}

using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Models.Wallpaper.Suggest;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class PlaylistBuilderTests
{
    private static MediaProfile Small(string name, bool mono = true, long loopMs = 0) =>
        SelectionProfileTests.File(name, 200, 200, mono, alpha: true, durationMs: loopMs);
    private static MediaProfile Opaque(string name) => SelectionProfileTests.File(name, 600, 600, mono: false, alpha: false);
    private static MediaProfile Big(string name) => SelectionProfileTests.File(name, 1920, 1080, mono: false);
    private static MediaProfile Video(string name, long ms) =>
        MediaProfile.Create(@"C:\m\" + name, 1280, 720, 1920, 1080, PixelFacts.Unknown, ms);
    private static Playlist Build(params MediaProfile[] files) =>
        PlaylistBuilder.Build("Cats", files, 1920, 1080, 0f, new CrossScreenConfig(), 30_000);
    private static IEnumerable<string> Recipes(Playlist p) => p.Items.Select(i => i.Name.Split(':')[0]);
    private static int FileCount(PlaylistItem i) => 1 + i.Config.Layers[0].Animation.AdditionalAnimationPaths.Distinct().Count();

    [Theory]
    [InlineData(3, new[] { 3 })]
    [InlineData(4, new[] { 4 })]
    [InlineData(5, new[] { 5 })]
    [InlineData(6, new[] { 3, 3 })]
    [InlineData(7, new[] { 4, 3 })]
    [InlineData(9, new[] { 4, 5 })]
    [InlineData(10, new[] { 4, 3, 3 })]
    [InlineData(12, new[] { 4, 4, 4 })]
    public void GroupSizesCoverEveryFile(int count, int[] expected) =>
        Assert.Equal(expected, PlaylistBuilder.GroupSizes(count));

    [Fact]
    public void BucketsBecomeShowcasesGroupsAndSolos()
    {
        var files = Enumerable.Range(1, 9).Select(i => Small($"cat{i:00}.gif")).ToList();
        files.Add(Big("bg1.gif")); files.Add(Big("bg2.png")); files.Add(Video("intro.mp4", 40_000));
        files.Add(Opaque("photo1.jpg")); files.Add(Opaque("photo2.jpg"));
        files.Add(SelectionProfileTests.File("wordmark.png", 900, 200));
        var p = Build(files.ToArray());

        Assert.Equal(3, p.Items.Count(i => i.Name.StartsWith("Showcase:")));
        Assert.Equal(new[] { 4, 5 }, p.Items.Where(i => i.Name.Contains(" + ")).Select(FileCount).OrderBy(n => n));
        Assert.Equal(3 + 2 + 3, p.Items.Count);
        Assert.Contains(p.Items, i => i.Config.Layers[0].Animation.AnimationPath.EndsWith("wordmark.png") && FileCount(i) == 1);
    }

    [Fact]
    public void FewerThanThreeSwarmableGoSolo()
    {
        var p = Build(Small("a.gif"), Small("b.gif"));
        Assert.Equal(2, p.Items.Count);
        Assert.All(p.Items, i => Assert.Equal(1, FileCount(i)));
    }

    [Fact]
    public void ItemsInterleaveGroupSoloGroupShowcase()
    {
        var files = Enumerable.Range(1, 8).Select(i => Small($"c{i}.gif")).Concat(new[] { Opaque("o1.jpg"), Opaque("o2.jpg"), Big("big.png") }).ToArray();
        var p = Build(files);
        bool IsGroup(PlaylistItem i) => FileCount(i) > 1;
        Assert.True(IsGroup(p.Items[0]));
        Assert.False(IsGroup(p.Items[1]));
        Assert.True(IsGroup(p.Items[2]));
        Assert.StartsWith("Showcase:", p.Items[3].Name);
        Assert.False(IsGroup(p.Items[4]));
    }

    [Fact]
    public void NoRecipeTwiceInARowWhenThereIsAnAlternative()
    {
        var p = Build(Enumerable.Range(1, 6).Select(i => Opaque($"o{i}.jpg")).ToArray());
        var names = Recipes(p).ToList();
        for (int i = 1; i < names.Count; i++) Assert.NotEqual(names[i - 1], names[i]);
    }

    [Fact]
    public void OnlyVideosGiveShowcasesWithoutLooping()
    {
        var p = Build(Video("a.mp4", 10_000), Video("b.mp4", 20_000), Video("c.mp4", 0));
        Assert.Equal(3, p.Items.Count);
        Assert.All(p.Items, i => Assert.StartsWith("Showcase:", i.Name));
    }

    [Fact]
    public void UnknownFilesBecomeSoloItems()
    {
        var unknown = MediaProfile.Create(@"C:\m\broken.png", 0, 0, 1920, 1080, PixelFacts.Unknown, 0);
        var p = Build(unknown);
        Assert.Single(p.Items);
        Assert.Equal(@"C:\m\broken.png", p.Items[0].Config.Layers[0].Animation.AnimationPath);
    }

    [Fact]
    public void GifDwellIsWholeLoops()
    {
        var p = Build(Small("a.gif", loopMs: 1300), Small("b.gif", loopMs: 1300), Small("c.gif", loopMs: 1300));
        Assert.Equal(29_900, p.Items.Single().DurationMs); // 23 × 1.3 s, nearest to 30 s
    }

    [Fact]
    public void LoopsThatDivideTheDefaultStoreNoDuration() =>
        Assert.Null(Build(Small("a.gif", loopMs: 1200)).Items.Single().DurationMs); // 25 × 1.2 s = 30 s = default

    [Fact]
    public void VideoDwellIsItsLengthCapped()
    {
        var p = Build(Video("short.mp4", 45_000), Video("long.mp4", 600_000), Video("unknown.mp4", 0));
        var byFile = p.Items.ToDictionary(i => Path.GetFileName(i.Config.Layers[0].Animation.AnimationPath), i => i.DurationMs);
        Assert.Equal(45_000, byFile["short.mp4"]);
        Assert.Equal(PlaylistBuilder.MaxVideoDwellMs, byFile["long.mp4"]);
        Assert.Null(byFile["unknown.mp4"]);
    }

    [Fact]
    public void SameFolderSamePlaylist()
    {
        var files = Enumerable.Range(1, 7).Select(i => Small($"c{i}.gif", mono: i % 2 == 0)).Append(Opaque("o.jpg")).ToArray();
        string Json(Playlist p) => string.Join("\n", p.Items.Select(i => i.Name + i.DurationMs + SceneFile.ToJson(i.Config)));
        Assert.Equal(Json(Build(files)), Json(Build(files)));
    }

    [Fact]
    public void EveryItemIsAValidScene()
    {
        var files = Enumerable.Range(1, 9).Select(i => Small($"c{i}.gif", mono: i % 3 == 0, loopMs: 900))
            .Concat(new[] { Opaque("o.jpg"), Big("b.png"), Video("v.mp4", 30_000), SelectionProfileTests.File("w.png", 900, 200) }).ToArray();
        foreach (var item in Build(files).Items) Assert.Empty(SceneChecks.Validate(item.Config, null));
    }

    [Fact]
    public void NamesAndDefaults()
    {
        var p = Build(Small("cat01.gif"), Small("cat02.gif"), Small("cat03.gif"), Small("cat04.gif"));
        Assert.Equal("Cats", p.Name);
        Assert.Equal(30_000, p.DefaultItemDurationMs);
        Assert.EndsWith(": cat01.gif + 3", p.Items.Single().Name);
    }

    [Fact]
    public void EmptyFolderGivesNoItems() => Assert.Empty(Build().Items);
}

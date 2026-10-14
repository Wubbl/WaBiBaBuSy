# Folder → Party Playlist and "Get content" Links Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** "Auto from folder…" in the Playlist tab turns a folder of media into a varied party playlist (smart groups, rotating recipes, loop-aligned durations), and a "Get content ▾" menu by the gallery links to free content sites, AI generators and clean-up tools.

**Architecture:** A pure `PlaylistBuilder` in `WaBiBaBuSy.Models/Wallpaper/Suggest/` buckets and groups `MediaProfile`s and asks the existing recipes (through a new `SceneSuggester.Ranked` / `BuildCard`) for each item's scene. A pure `ContentLinks` list in `WaBiBaBuSy.Models/Content/`. The UI picks the folder, adds the files to the gallery, profiles them with the existing `MediaProbe` (one shared helper with Suggest), and appends the items to the open playlist.

**Tech Stack:** .NET 9, C# 12, Avalonia 12 + CommunityToolkit.Mvvm, Magick.NET 14.17.1 (UI), xUnit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-10-14-folder-playlist-and-content-links-design.md`

## Global Constraints

- Pure, deterministic Models code: no `System.Random`, no clock, no `string.GetHashCode` (randomised per process). Seeds from file names via a fixed FNV-1a hash + `PatternLayout.Hash3`.
- Recipes in `SceneRecipes.cs` are not changed.
- `Playlist` / `PlaylistItem` format and playback are unchanged.
- Every built item passes `SceneChecks.Validate(scene, null)` with no warnings.
- Folder read: top level only, `SceneChecks.IsMediaFile`, sorted by file name (ordinal, case-insensitive), max **200** files.
- Video dwell cap **120 000 ms**; `DurationMs` stored only when it differs from the playlist default.
- Links: `https://` root-domain URLs only; opened with `Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })`; the app makes no network calls.
- File-scoped namespaces, nullable, XML docs on public members (repo standard).

## Review Focus

1. **A folder of only videos / only full-screen files** → every item is a Showcase; the "no recipe twice in a row" rule must not loop or throw when Showcase is the only recipe. Test: `PlaylistBuilderTests.OnlyVideosGiveShowcasesWithoutLooping` (Task 2).
2. **Files that fail to decode** (Unknown pixels, 0 × 0) → they land in Solo and still build. Test: `PlaylistBuilderTests.UnknownFilesBecomeSoloItems` (Task 2).
3. **Freshly added gallery files have no duration yet** (the gallery's media-details pass is async) → GIF loops and video lengths must be read directly, or dwell snapping silently does nothing. Owned by Task 4 (`ProfileFilesAsync` reads `GifMetadataReader` / `ProbeVideo` when the gallery has 0); manual check.
4. **Pressing "Auto from folder…" twice quickly** → the second press must not run concurrently (`AsyncRelayCommand` default) and items are appended once per press. Task 4, manual check.
5. **A folder with 1000 files** → only the first 200 by name, status says so. Task 4, manual check.

---

### Task 1: `SceneSuggester.Ranked` and `BuildCard`

**Files:**
- Modify: `WaBiBaBuSy.Models/Wallpaper/Suggest/SceneSuggester.cs`
- Test: `WaBiBaBuSy.Tests/SceneSuggesterTests.cs`

**Interfaces:**
- Produces: `public static IReadOnlyList<ISceneRecipe> Ranked(SelectionProfile s)` — recipes with score > 0, best first, ties in `SceneRecipes.All` order. `public static Suggestion BuildCard(ISceneRecipe recipe, SelectionProfile s, CrossScreenConfig baseScene, int seed)` — `recipe.Build` plus the cm twins (`WithCentimetres`). `Suggest` / `Shuffle` use both (behaviour unchanged).

- [ ] **Step 1: Write the failing tests** — append to `SceneSuggesterTests`:

```csharp
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
        Assert.Equal(card.Scene.Animation.TargetHeight / 40f, card.Scene.Animation.TargetHeightCm, 3);
    }

    [Fact]
    public void SuggestIsTheTopOfRanked() =>
        Assert.Equal(SceneSuggester.Ranked(Sel(F("logo.png", 200, 200))).Take(SceneSuggester.CardCount).Select(r => r.Name),
                     Names(SceneSuggester.Suggest(Sel(F("logo.png", 200, 200)), Base, 1)));
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SceneSuggesterTests"`
Expected: build FAILS — `'SceneSuggester' does not contain a definition for 'Ranked'`.

- [ ] **Step 3: Implement** — in `SceneSuggester.cs` add:

```csharp
    /// <summary>The recipes offered for <paramref name="s"/> (score &gt; 0), best first; ties in <see cref="SceneRecipes.All"/> order.</summary>
    public static IReadOnlyList<ISceneRecipe> Ranked(SelectionProfile s) =>
        Offered(s).OrderByDescending(x => x.score).ThenBy(x => x.index).Select(x => x.recipe).ToList();

    /// <summary>Build one recipe's scene for <paramref name="s"/>, with the cm twins filled for physical-unit rooms.</summary>
    public static Suggestion BuildCard(ISceneRecipe recipe, SelectionProfile s, CrossScreenConfig baseScene, int seed) =>
        WithCentimetres(recipe.Build(s, baseScene, seed), s.PixelsPerCm);
```

and change `Suggest` to `var picked = Ranked(s).Take(CardCount);` and the private `Build` to
`new(recipes.Select((r, i) => BuildCard(r, s, baseScene, PatternLayout.Hash3(seed, i, 7))).ToList(), …)`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SceneSuggesterTests|FullyQualifiedName~SceneRecipeTests"`
Expected: PASS (existing suggester tests unchanged).

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/Suggest/SceneSuggester.cs WaBiBaBuSy.Tests/SceneSuggesterTests.cs
git commit -m "feat(suggest): expose ranked recipes and single-card build for the playlist builder"
```

---

### Task 2: `PlaylistBuilder`

**Files:**
- Create: `WaBiBaBuSy.Models/Wallpaper/Suggest/PlaylistBuilder.cs`
- Test: `WaBiBaBuSy.Tests/PlaylistBuilderTests.cs`

**Interfaces:**
- Consumes: `MediaProfile`, `SelectionProfile(files, w, h, pixelsPerCm)`, `SceneSuggester.Ranked` / `BuildCard` (Task 1), `PatternLayout.Hash3`, `Playlist`, `PlaylistItem`.
- Produces: `public static class PlaylistBuilder` with `const int MaxVideoDwellMs = 120_000`, `static IReadOnlyList<int> GroupSizes(int count)`, `static Playlist Build(string name, IReadOnlyList<MediaProfile> files, int screenWidth, int screenHeight, float pixelsPerCm, CrossScreenConfig baseScene, int defaultDurationMs)`.

- [ ] **Step 1: Write the failing tests** — `WaBiBaBuSy.Tests/PlaylistBuilderTests.cs`:

```csharp
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
    private static int FileCount(PlaylistItem i) => 1 + i.Config.Animation.AdditionalAnimationPaths.Distinct().Count();

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
        Assert.Contains(p.Items, i => i.Config.Animation.AnimationPath.EndsWith("wordmark.png") && FileCount(i) == 1);
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
        Assert.Equal(@"C:\m\broken.png", p.Items[0].Config.Animation.AnimationPath);
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
        var byFile = p.Items.ToDictionary(i => Path.GetFileName(i.Config.Animation.AnimationPath), i => i.DurationMs);
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
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~PlaylistBuilderTests"`
Expected: build FAILS — `PlaylistBuilder` not found.

- [ ] **Step 3: Write `PlaylistBuilder.cs`**

```csharp
namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>
/// Turns a folder's media profiles into a varied party playlist (spec 2026-10-14 folder-playlist §1): full-screen files
/// and videos as Showcases, look-alike transparent sprites in groups of 3–5, the rest as solo items; interleaved, no
/// recipe twice in a row, GIF dwell on whole loops. Pure and deterministic in its inputs.
/// </summary>
public static class PlaylistBuilder
{
    /// <summary>A Showcase video plays at most this long.</summary>
    public const int MaxVideoDwellMs = 120_000;

    private const int GroupSize = 4;
    private enum Bucket { Group, Solo, Showcase }
    private static readonly Bucket[] Rhythm = { Bucket.Group, Bucket.Solo, Bucket.Group, Bucket.Showcase };

    /// <summary>Group sizes for <paramref name="count"/> swarmable files (≥ 3): fours, the remainder folded in (5 → 5, 6 → 3+3, 9 → 4+5).</summary>
    public static IReadOnlyList<int> GroupSizes(int count)
    {
        if (count < 3) return Array.Empty<int>();
        var sizes = Enumerable.Repeat(GroupSize, count / GroupSize).ToList();
        switch (count % GroupSize)
        {
            case 1: sizes[^1] = 5; break;
            case 2: sizes[^1] = 3; sizes.Add(3); break;
            case 3: sizes.Add(3); break;
        }
        return sizes;
    }

    /// <summary>
    /// Build the playlist. <paramref name="baseScene"/> supplies monitor targets and distribution mode (as for Suggest);
    /// <paramref name="defaultDurationMs"/> becomes the playlist default and the dwell items round to.
    /// </summary>
    public static Playlist Build(string name, IReadOnlyList<MediaProfile> files, int screenWidth, int screenHeight,
        float pixelsPerCm, CrossScreenConfig baseScene, int defaultDurationMs)
    {
        var playlist = new Playlist { Name = name, DefaultItemDurationMs = defaultDurationMs };
        var ordered = files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase).ToList();

        var showcase = ordered.Where(IsShowcase).ToList();
        var swarm = ordered.Where(f => !IsShowcase(f) && f.Pixels.HasAlpha && f.Aspect != AspectClass.Wide).ToList();
        var solo = ordered.Where(f => !IsShowcase(f) && !swarm.Contains(f)).ToList();
        if (swarm.Count < 3) { solo.AddRange(swarm); solo = solo.OrderBy(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase).ToList(); swarm.Clear(); }

        var looks = swarm.OrderBy(f => f.Pixels.IsMonochrome ? 0 : 1).ThenBy(f => f.PixelHeight)
            .ThenBy(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase).ToList();
        var groups = new Queue<IReadOnlyList<MediaProfile>>();
        int at = 0;
        foreach (int size in GroupSizes(looks.Count)) { groups.Enqueue(looks.GetRange(at, size)); at += size; }

        var queues = new Dictionary<Bucket, Queue<IReadOnlyList<MediaProfile>>>
        {
            [Bucket.Group] = groups,
            [Bucket.Solo] = new(solo.Select(f => (IReadOnlyList<MediaProfile>)new[] { f })),
            [Bucket.Showcase] = new(showcase.Select(f => (IReadOnlyList<MediaProfile>)new[] { f })),
        };

        string? previous = null;
        for (int beat = 0; queues.Values.Any(q => q.Count > 0); beat++)
        {
            var queue = queues[Rhythm[beat % Rhythm.Length]];
            if (queue.Count == 0) continue;
            var item = BuildItem(queue.Dequeue(), screenWidth, screenHeight, pixelsPerCm, baseScene, defaultDurationMs, previous);
            previous = item.Name.Split(':')[0];
            playlist.Items.Add(item);
        }
        return playlist;
    }

    private static bool IsShowcase(MediaProfile f) => f.Size == SizeClass.FullScreen || f.Kind == MediaKind.Video;

    private static PlaylistItem BuildItem(IReadOnlyList<MediaProfile> files, int screenWidth, int screenHeight,
        float pixelsPerCm, CrossScreenConfig baseScene, int defaultDurationMs, string? previousRecipe)
    {
        var selection = new SelectionProfile(files, screenWidth, screenHeight, pixelsPerCm);
        var ranked = SceneSuggester.Ranked(selection);
        var recipe = ranked.FirstOrDefault(r => r.Name != previousRecipe) ?? ranked[0];
        int seed = PatternLayout.Hash3(StableHash(files), 0, 11);
        var card = SceneSuggester.BuildCard(recipe, selection, baseScene, seed);

        string first = Path.GetFileName(files[0].Path);
        return new PlaylistItem
        {
            Name = $"{recipe.Name}: {first}" + (files.Count > 1 ? $" + {files.Count - 1}" : string.Empty),
            Config = card.Scene,
            DurationMs = Dwell(selection, defaultDurationMs),
        };
    }

    /// <summary>Video: its length up to the cap; GIFs: the nearest whole number of shortest loops; else the default (null).</summary>
    private static int? Dwell(SelectionProfile s, int defaultDurationMs)
    {
        long dwell = s.Files.FirstOrDefault(f => f.Kind == MediaKind.Video)?.DurationMs is long videoMs
            ? Math.Min(videoMs, MaxVideoDwellMs)
            : s.ShortestLoopMs is long loop && loop > 0
                ? Math.Max(1, (long)Math.Round(defaultDurationMs / (double)loop)) * loop
                : defaultDurationMs;
        return dwell == defaultDurationMs ? null : (int)Math.Min(dwell, int.MaxValue);
    }

    /// <summary>FNV-1a over the lower-case file names: stable across runs and machines (unlike string.GetHashCode).</summary>
    private static int StableHash(IReadOnlyList<MediaProfile> files)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var f in files)
                foreach (char c in Path.GetFileName(f.Path).ToLowerInvariant()) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~PlaylistBuilderTests"`
Expected: PASS. If `ItemsInterleaveGroupSoloGroupShowcase` fails, print the item names in the assertion message and check the rhythm loop, not the test: 8 small alpha files → groups 4+4, 2 solos, 1 showcase → order G S G Sh S.

- [ ] **Step 5: Full suite**

Run: `dotnet test WaBiBaBuSy.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/Suggest/PlaylistBuilder.cs WaBiBaBuSy.Tests/PlaylistBuilderTests.cs
git commit -m "feat(playlist): build a party playlist from media profiles - showcases, look-alike groups, solos, loop-aligned dwell"
```

---

### Task 3: `ContentLinks`

**Files:**
- Create: `WaBiBaBuSy.Models/Content/ContentLinks.cs`
- Test: `WaBiBaBuSy.Tests/ContentLinksTests.cs`

**Interfaces:**
- Produces: `sealed record ContentLink(string Name, string Url, string Tip)`, `sealed record ContentLinkGroup(string Title, IReadOnlyList<ContentLink> Links)`, `static class ContentLinks { IReadOnlyList<ContentLinkGroup> Groups; const string Tip; }`.

- [ ] **Step 1: Write the failing tests**

```csharp
using WaBiBaBuSy.Models.Content;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ContentLinksTests
{
    [Fact]
    public void ThreeNonEmptyGroups()
    {
        Assert.Equal(new[] { "Free GIFs & sprites", "Make with AI", "Clean up" }, ContentLinks.Groups.Select(g => g.Title));
        Assert.All(ContentLinks.Groups, g => Assert.NotEmpty(g.Links));
    }

    [Fact]
    public void EveryLinkIsHttpsWithNameAndTip() =>
        Assert.All(ContentLinks.Groups.SelectMany(g => g.Links), l =>
        {
            Assert.True(Uri.TryCreate(l.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps, l.Url);
            Assert.False(string.IsNullOrWhiteSpace(l.Name));
            Assert.False(string.IsNullOrWhiteSpace(l.Tip));
        });

    [Fact]
    public void LinksAreUnique() =>
        Assert.Equal(ContentLinks.Groups.SelectMany(g => g.Links).Count(),
                     ContentLinks.Groups.SelectMany(g => g.Links).Select(l => l.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count());
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ContentLinksTests"`
Expected: build FAILS — `ContentLinks` not found.

- [ ] **Step 3: Write `ContentLinks.cs`**

```csharp
namespace WaBiBaBuSy.Models.Content;

/// <summary>One place to get or make content, opened in the browser.</summary>
public sealed record ContentLink(string Name, string Url, string Tip);

/// <summary>A titled group of <see cref="ContentLink"/>s.</summary>
public sealed record ContentLinkGroup(string Title, IReadOnlyList<ContentLink> Links);

/// <summary>The gallery's "Get content ▾" menu (spec 2026-10-14 folder-playlist §3). Root-domain URLs only.</summary>
public static class ContentLinks
{
    private const string Licence = " Check the licence before a public show.";

    /// <summary>Shown as the last, non-clickable menu line.</summary>
    public const string Tip = "Best results: small transparent PNG / GIF, plain background → remove.bg, seamless loop";

    /// <summary>The menu, in display order.</summary>
    public static IReadOnlyList<ContentLinkGroup> Groups { get; } = new[]
    {
        new ContentLinkGroup("Free GIFs & sprites", new[]
        {
            new ContentLink("GIPHY", "https://giphy.com", "Huge GIF library, many with transparent backgrounds (stickers)." + Licence),
            new ContentLink("Tenor", "https://tenor.com", "Reaction GIFs, good for short loops." + Licence),
            new ContentLink("itch.io game assets", "https://itch.io/game-assets/free", "Free pixel-art sprite sheets — tiny files that scale up crisply." + Licence),
            new ContentLink("OpenGameArt", "https://opengameart.org", "Free sprites and animations, many CC0." + Licence),
            new ContentLink("LottieFiles", "https://lottiefiles.com", "Free vector animations; export as GIF for the wall." + Licence),
            new ContentLink("Pixabay", "https://pixabay.com", "Free photos, illustrations and short videos." + Licence),
            new ContentLink("Pexels", "https://www.pexels.com", "Free photos and full-screen video clips." + Licence),
        }),
        new ContentLinkGroup("Make with AI", new[]
        {
            new ContentLink("ChatGPT images", "https://chatgpt.com", "Describe it and ask for a transparent background."),
            new ContentLink("Ideogram", "https://ideogram.ai", "Strong at logos and text."),
            new ContentLink("Adobe Firefly", "https://firefly.adobe.com", "Images and short clips, commercially safe training data."),
            new ContentLink("Kling", "https://klingai.com", "Animate a still image into a short clip; ask for a seamless loop."),
            new ContentLink("Luma Dream Machine", "https://lumalabs.ai", "Image or text to short video loops."),
            new ContentLink("Runway", "https://runwayml.com", "Video generation and editing."),
            new ContentLink("ComfyUI", "https://www.comfy.org", "Free and local on your own GPU — best for making lots of content."),
        }),
        new ContentLinkGroup("Clean up", new[]
        {
            new ContentLink("remove.bg", "https://www.remove.bg", "Make the background transparent so the sprite can float."),
            new ContentLink("ezgif", "https://ezgif.com", "Crop, resize, speed up and convert video to GIF."),
            new ContentLink("Upscayl", "https://upscayl.org", "Free local upscaling for small images."),
        }),
    };
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ContentLinksTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Content/ContentLinks.cs WaBiBaBuSy.Tests/ContentLinksTests.cs
git commit -m "feat(content): Get content link list - free sites, AI generators, clean-up tools"
```

---

### Task 4: UI — shared profiling, Auto from folder, Get content menu

**Files:**
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.SceneEditor.cs` (extract `PrimaryScreen` + `ProfileFilesAsync` from `BuildSelectionProfileAsync`)
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.Playlist.cs` (folder flow, wiring)
- Modify: `WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs` (hook, command, status, `AppendItems`)
- Modify: `WaBiBaBuSy.UI/Views/PlaylistPanel.axaml` (menu entry, status line)
- Create: `WaBiBaBuSy.UI/ViewModels/ContentMenuEntry.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (`ContentMenuEntries`, `OpenLink`) and `WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml` (Get content button next to + Add)

**Interfaces:**
- Consumes: `PlaylistBuilder.Build` (Task 2), `ContentLinks` (Task 3), `MediaProbe.Read`, `GifMetadataReader.Read(path) → (string Resolution, int FrameCount, long DurationMs)?`, `_thumbnailGenerator.ProbeVideo(path) → Task<VideoProbeResult?>` (`Resolution`, `DurationMs`), `SceneChecks.IsMediaFile`, `AddWallpaperFromPath`, `_storageProvider.OpenFolderPickerAsync`.
- Produces: `PlaylistViewModel.BuildFromFolder` (`Func<Task>?`), `AddFromFolderCommand`, `FolderStatus` / `HasFolderStatus`, `AppendItems(string playlistName, IEnumerable<PlaylistItem>)`; `MainWindowViewModel.ContentMenuEntries`.

- [ ] **Step 1: Extract the shared profiling helper** in `MainWindowViewModel.SceneEditor.cs` — replace the body of `BuildSelectionProfileAsync` after its `paths` / empty check with:

```csharp
        var screen = PrimaryScreen();
        var files = await ProfileFilesAsync(paths, screen.Width, screen.Height);
        return new SelectionProfile(files, screen.Width, screen.Height, screen.PixelsPerCm);
    }

    /// <summary>Primary monitor size and reference DPI; 1920 × 1080 and 0 (unknown DPI) when they cannot be read.</summary>
    private static (int Width, int Height, float PixelsPerCm) PrimaryScreen()
    {
        try
        {
            var primary = LocalMonitors.Snapshot().Monitors.FirstOrDefault(m => m.IsPrimary);
            return (primary?.Width ?? 1920, primary?.Height ?? 1080, LocalMonitors.ReferencePixelsPerCm);
        }
        catch (Exception ex)
        {
            AppLogger.CreateLogger<MainWindowViewModel>().LogWarning(ex, "Could not read the primary monitor, assuming 1920x1080");
            return (1920, 1080, 0f);
        }
    }

    /// <summary>
    /// Profile media files for Suggest and Auto from folder. Gallery facts are snapshotted on the UI thread; the decode,
    /// and — for files the gallery has not measured yet — the GIF loop / video probe, run off it. A file that fails
    /// any step is profiled with what is known (never throws). <paramref name="progress"/> gets the count done.
    /// </summary>
    private async Task<List<MediaProfile>> ProfileFilesAsync(IReadOnlyList<string> paths, int screenW, int screenH,
        IProgress<int>? progress = null)
    {
        var facts = paths.Select(p =>
        {
            var item = Wallpapers.FirstOrDefault(w => string.Equals(w.FilePath, p, StringComparison.OrdinalIgnoreCase));
            return (path: p, video: SceneChecks.IsVideo(p), thumb: item?.ThumbnailPath, resolution: item?.Resolution,
                durationMs: item?.DurationMs ?? 0);
        }).ToList();

        return await Task.Run(async () =>
        {
            var result = new List<MediaProfile>(facts.Count);
            foreach (var x in facts)
            {
                var (w, h, pixels) = (0, 0, PixelFacts.Unknown);
                long durationMs = x.durationMs;
                string? resolution = x.resolution;
                try
                {
                    if (x.video && (durationMs <= 0 || string.IsNullOrEmpty(resolution))
                        && await _thumbnailGenerator.ProbeVideo(x.path) is { } probe)
                        (durationMs, resolution) = (probe.DurationMs, probe.Resolution);
                    else if (durationMs <= 0 && MediaAnalyzer.KindOf(x.path) == MediaKind.Gif
                             && GifMetadataReader.Read(x.path) is { } gif)
                        durationMs = gif.DurationMs;
                    bool hasThumb = x.video && !string.IsNullOrEmpty(x.thumb) && File.Exists(x.thumb);
                    if (!x.video || hasThumb) (w, h, pixels) = MediaProbe.Read(hasThumb ? x.thumb! : x.path);
                }
                catch (Exception ex)
                {
                    // ponytail: any probe / decode failure degrades to "unknown pixels"; the file still gets an item
                    AppLogger.CreateLogger<MainWindowViewModel>().LogWarning(ex, "Could not analyse {Path}", x.path);
                }
                if (x.video && TryParseResolution(resolution, out int vw, out int vh)) (w, h) = (vw, vh);
                result.Add(MediaProfile.Create(x.path, w, h, screenW, screenH, pixels, durationMs));
                progress?.Report(result.Count);
            }
            return result;
        });
    }
```

Delete the old inline `facts` / `screenW` / `Task.Run` block it replaces. Add `using WaBiBaBuSy.UI.Services;` if missing (it is there from auto-suggest).

- [ ] **Step 2: Build**

Run: `dotnet build WaBiBaBuSy.UI`
Expected: 0 errors.

- [ ] **Step 3: `PlaylistViewModel` additions** (next to `AddCurrentScene`):

```csharp
    /// <summary>"＋ Add ▾ → Auto from folder…": set by the host (folder pick, profiling, building).</summary>
    public Func<Task>? BuildFromFolder { get; set; }

    /// <summary>Progress / result line of the last Auto from folder run (empty = hidden).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFolderStatus))] private string _folderStatus = string.Empty;

    /// <summary>True when <see cref="FolderStatus"/> has text.</summary>
    public bool HasFolderStatus => !string.IsNullOrEmpty(FolderStatus);

    [RelayCommand]
    private async Task AddFromFolderAsync()
    {
        if (BuildFromFolder != null) await BuildFromFolder();
    }

    /// <summary>Append built items; an empty playlist also takes <paramref name="playlistName"/>. Returns the count added.</summary>
    public int AppendItems(string playlistName, IEnumerable<PlaylistItem> items)
    {
        bool wasEmpty = Items.Count == 0;
        int added = 0;
        foreach (var item in items) { Items.Add(new PlaylistItemRow(item)); added++; }
        if (wasEmpty && added > 0) PlaylistName = playlistName;
        return added;
    }
```

- [ ] **Step 4: The folder flow** in `MainWindowViewModel.Playlist.cs` — in `InitPlaylist()` add `PlaylistEditor.BuildFromFolder = AddPlaylistFromFolderAsync;`, and add:

```csharp
    private const int MaxFolderFiles = 200;

    /// <summary>
    /// Auto from folder (spec 2026-10-14 folder-playlist §2): pick a folder, add its media to the gallery, profile it,
    /// build a party playlist and append its items to the open playlist. Never throws; the status line says what happened.
    /// </summary>
    private async Task AddPlaylistFromFolderAsync()
    {
        try
        {
            if (_storageProvider == null) return;
            var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Folder with pictures, GIFs or videos",
                AllowMultiple = false,
            });
            var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(folder)) return;
            string folderName = Path.GetFileName(folder.TrimEnd('\\', '/'));

            var all = Directory.EnumerateFiles(folder).Where(SceneChecks.IsMediaFile)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
            if (all.Count == 0)
            {
                PlaylistEditor.FolderStatus = $"No pictures or videos in {folderName}";
                return;
            }
            var files = all.Take(MaxFolderFiles).ToList();
            foreach (var f in files) AddWallpaperFromPath(f);

            var screen = PrimaryScreen();
            var progress = new Progress<int>(n => PlaylistEditor.FolderStatus = $"Analysing {n} / {files.Count}…");
            PlaylistEditor.FolderStatus = $"Analysing 0 / {files.Count}…";
            var profiles = await ProfileFilesAsync(files, screen.Width, screen.Height, progress);

            var built = PlaylistBuilder.Build(folderName, profiles, screen.Width, screen.Height, screen.PixelsPerCm,
                SceneEditor.BuildConfig(), PlaylistEditor.DefaultDurationMs);
            int added = PlaylistEditor.AppendItems(built.Name, built.Items);
            double minutes = built.Items.Sum(i => i.GetEffectiveDurationMs(PlaylistEditor.DefaultDurationMs)) / 60_000.0;
            PlaylistEditor.FolderStatus = $"Added {added} items (≈ {Math.Round(minutes)} min) from {folderName}"
                + (all.Count > files.Count ? $" — first {files.Count} of {all.Count} files" : string.Empty);
        }
        catch (Exception ex)
        {
            AppLogger.CreateLogger<MainWindowViewModel>().LogWarning(ex, "Auto from folder failed");
            PlaylistEditor.FolderStatus = "Could not build from that folder: " + ex.Message;
        }
    }
```

Add the usings the file needs: `System.Collections.Generic`, `System.IO`, `System.Threading.Tasks`, `Avalonia.Platform.Storage`, `Microsoft.Extensions.Logging`, `WaBiBaBuSy.Core.Services.Logging`, `WaBiBaBuSy.Models.Wallpaper`, `WaBiBaBuSy.Models.Wallpaper.Suggest` (check which are already there; `Playlist` is referenced fully-qualified in this file because the class has a `Playlist`-named member — keep `built` typed with `var`). Check that `PlaylistEditor.DefaultDurationMs` is public (`grep -n "DefaultDurationMs" WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs`); if it is a private field-backed property, use the public `DefaultDurationSeconds * 1000`.

- [ ] **Step 5: Playlist panel markup** — in `PlaylistPanel.axaml`, add to the `＋ Add ▾` `MenuFlyout` after "From preset":

```xml
                    <MenuItem Header="Auto from folder…" Command="{Binding AddFromFolderCommand}"
                              ToolTip.Tip="Build varied items from every picture, GIF and video in a folder (appended to this playlist)"/>
```

and replace the lone `<Button Grid.Row="4" …>` row with a `StackPanel Grid.Row="4"` holding that same button followed by:

```xml
            <TextBlock Text="{Binding FolderStatus}" IsVisible="{Binding HasFolderStatus}"
                       FontSize="11" Foreground="#9FC5FF" TextWrapping="Wrap" Margin="0,4,0,0"/>
```

(keep the button's attributes; move only `Grid.Row="4"` to the `StackPanel`).

- [ ] **Step 6: Get content menu** — create `WaBiBaBuSy.UI/ViewModels/ContentMenuEntry.cs`:

```csharp
using System.Windows.Input;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>A row of the gallery's "Get content ▾" menu: a link (with a command) or a non-clickable title / tip.</summary>
public sealed record ContentMenuEntry(string Header, string? Tip, ICommand? Command)
{
    /// <summary>Links are clickable; titles and the tip line are not.</summary>
    public bool IsLink => Command != null;
}
```

In `MainWindowViewModel.cs`, next to `OpenLogFolder`:

```csharp
    /// <summary>The gallery's "Get content ▾" menu, built from <see cref="ContentLinks"/>.</summary>
    public IReadOnlyList<ContentMenuEntry> ContentMenuEntries => _contentMenuEntries ??= ContentLinks.Groups
        .SelectMany(g => new[] { new ContentMenuEntry($"— {g.Title} —", null, null) }
            .Concat(g.Links.Select(l => new ContentMenuEntry(l.Name, l.Tip, OpenLinkCommand))))
        .Append(new ContentMenuEntry("💡 " + ContentLinks.Tip, null, null))
        .ToList();
    private IReadOnlyList<ContentMenuEntry>? _contentMenuEntries;

    /// <summary>Open a content link in the default browser.</summary>
    [RelayCommand]
    private void OpenLink(string? header)
    {
        var url = ContentLinks.Groups.SelectMany(g => g.Links).FirstOrDefault(l => l.Name == header)?.Url;
        if (url == null) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.CreateLogger<MainWindowViewModel>().LogWarning(ex, "Could not open {Url}", url); }
    }
```

(add `using WaBiBaBuSy.Models.Content;`). In `SceneEditorPanel.axaml`, replace the gallery's `+ Add` `Button` with:

```xml
            <StackPanel Orientation="Horizontal" Spacing="6" HorizontalAlignment="Right" VerticalAlignment="Bottom" Margin="0,0,14,4">
                <Button Content="Get content ▾" ToolTip.Tip="Where to find or make small animated content (opens your browser)">
                    <Button.Flyout>
                        <MenuFlyout ItemsSource="{Binding ContentMenuEntries}">
                            <MenuFlyout.ItemContainerTheme>
                                <ControlTheme TargetType="MenuItem" BasedOn="{StaticResource {x:Type MenuItem}}" x:DataType="vm:ContentMenuEntry">
                                    <Setter Property="Header" Value="{Binding Header}"/>
                                    <Setter Property="Command" Value="{Binding Command}"/>
                                    <Setter Property="CommandParameter" Value="{Binding Header}"/>
                                    <Setter Property="IsEnabled" Value="{Binding IsLink}"/>
                                    <Setter Property="ToolTip.Tip" Value="{Binding Tip}"/>
                                </ControlTheme>
                            </MenuFlyout.ItemContainerTheme>
                        </MenuFlyout>
                    </Button.Flyout>
                </Button>
                <Button Content="+ Add" Command="{Binding AddWallpaperCommand}"
                        ToolTip.Tip="Add files to the gallery (or drop files onto this panel)"/>
            </StackPanel>
```

- [ ] **Step 7: Build and test**

Run: `dotnet build && dotnet test WaBiBaBuSy.Tests`
Expected: 0 errors; all tests PASS. If `MenuFlyout.ItemContainerTheme` is not available in this Avalonia version, use the `PlaylistPanel.axaml` "From preset" pattern: a single `MenuItem Header="Get content"` with `ItemsSource` + `MenuItem.ItemContainerTheme` inside a plain `MenuFlyout`.

- [ ] **Step 8: Real-data check without the GUI** — a throwaway console in the scratchpad (as in the auto-suggest plan) that runs `MediaProbe.Read` + `PlaylistBuilder.Build` on `TestScenarios/showcase/assets` and prints item names and durations. Expected: one item per file (fewer than 3 swarmable), no recipe twice in a row.

- [ ] **Step 9: Manual GUI check (owed to Patrick if it cannot be driven from here)**
1. Playlist tab → ＋ Add ▾ → Auto from folder… → `TestScenarios/showcase/assets` → status "Analysing… / Added N items"; items appear with thumbnails; files appear in the gallery.
2. Again with a folder of ~40 GIFs → groups "… + 3", interleaved, durations on whole loops.
3. A folder with no media → "No pictures or videos in …".
4. Start the show → items play.
5. Get content ▾ → titles greyed, links open the browser, tooltips show.

- [ ] **Step 10: Commit**

```bash
git add WaBiBaBuSy.UI
git commit -m "feat(playlist): Auto from folder in the Playlist tab, Get content menu by the gallery; shared media profiling"
```

---

### Task 5: Docs

**Files:** `docs/superpowers/specs/2026-10-14-folder-playlist-and-content-links-design.md` (status → Implemented + any rulings), `claude.md` (Key Capabilities: one bullet after Auto-suggest), `.docs/RECENT_UPDATES.md` (entry on top), `.docs/UI_ARCHITECTURE.md` (Playlist tab: Auto from folder; gallery: Get content).

- [ ] **Step 1:** Edit each file in its existing style. Capability bullet:

```markdown
- Auto from folder: the Playlist tab builds a party playlist from a folder (full-screen files / videos as Showcases, look-alike transparent sprites in groups of 3–5, the rest solo; interleaved, no recipe twice in a row, GIF dwell on whole loops — `PlaylistBuilder`); the gallery's "Get content ▾" links free content sites, AI generators and clean-up tools (`Models/Content/ContentLinks.cs`)
```

- [ ] **Step 2:** `./tools/graphify-update.ps1`, then commit:

```bash
git add docs/superpowers/specs/2026-10-14-folder-playlist-and-content-links-design.md claude.md .docs/RECENT_UPDATES.md .docs/UI_ARCHITECTURE.md graphify-out/GRAPH_REPORT.md
git commit -m "docs: auto from folder and Get content - capabilities, changelog, UI architecture"
```

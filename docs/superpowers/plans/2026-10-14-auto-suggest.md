# Auto-Suggest Scenes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A **✨ Suggest** button in the Scene editor that analyses the picked media and offers four candidate scenes (cards); clicking one makes it the draft, Shuffle offers more, Keep / Revert close the strip.

**Architecture:** Pure analysis + recipes + picker in `WaBiBaBuSy.Models/Wallpaper/Suggest/` (no imaging dependency, unit-tested). The UI decodes each file's first frame to 64 × 64 BGRA with Magick.NET (`Services/MediaProbe.cs`) and hosts a `SuggestionsViewModel` whose cards load through `SceneEditor.LoadFromConfig` — the preset-load path — so the room view previews them live.

**Tech Stack:** .NET 9, C# 12, Avalonia 12 + CommunityToolkit.Mvvm, Magick.NET-Q8 14.17.1 (UI only), xUnit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-10-14-auto-suggest-design.md`

## Global Constraints

- Determinism: recipes are pure functions of `(SelectionProfile, base scene, seed)`. No `System.Random`, no clock, no per-machine state inside `WaBiBaBuSy.Models`. Seeded values come from `PatternLayout.Hash3`.
- A suggestion copies `SelectedMonitorIds` and `DistributionMode` from the base scene; everything else is the recipe's.
- Nothing is sent to machines by Suggest / card click / Keep / Revert; only Play does that (unchanged).
- `WaBiBaBuSy.Models` gets no new package references.
- File-scoped namespaces, nullable enabled, XML doc on public types/members (repo standard).
- Every built scene passes `SceneChecks.Validate(scene, null)` with no warnings.
- Card count: 4 (`SceneSuggester.CardCount`).
- Thresholds (spec §1.1): Small height < 0.25·screen height; FullScreen width ≥ 0.7·W and height ≥ 0.7·H; Wide aspect > 2.5, Tall < 0.6; HasAlpha > 5 % of pixels with alpha < 128; mono ≥ 90 % of visible pixels achromatic or near the dominant hue; dominant colours = 4-bit buckets ≥ 5 %, max 3.

## Spec adjustments found while planning (fold into the spec in Task 6)

- Effects are not drawn with a pattern or with extra images (`SceneChecks.Validate`) → Pulse Grid times its **colour cycle** to the loop (no Pulse effect); Orbit spins only a single file.
- Endless Linear needs a Traveling colour mode → Logo Conveyor is Endless only for mono media (TravelingRainbow); colourful conveyor loops.
- Trails take the grading colour (`TrailConfig.UseGradingColor`), not a hex → Comet's trail is tinted only for mono media.
- `ISceneRecipe.Score` sees only the selection, so the "Sequential bonus" for Logo Conveyor is dropped.
- Any video in the sprites → only Showcase is offered (a pattern with a video is a warning; videos are backdrops).
- `SharedPalette` keeps the hero's colours first (file order), not global frequency.
- Background: light media (mean luma > 0.6) → dark background; dark media (< 0.25) → light; in between → dark. Fireflies always dark.
- The profile is built when Suggest is pressed (decoding ≤ a handful of 64 px thumbnails), not cached in `ScheduleMediaDetails` — no gallery persistence change.
- "Near the dominant hue" = within the dominant 10° hue bin ± 2 bins.
- Pulse Grid's mono tint is `Rainbow` (a time-based colour wave, as preset 10), not `TravelingRainbow` (static per cell); Comet copies preset 13's pulse and bezel Ripple / Glow, bezel colour = first palette colour.
- Parade is a one-row `Pattern` (preset 05: `SpacingY` = half the screen), not `NodePhaseDelayMs` — the phase delay stays on Orbit (preset 06).

## Review Focus

1. **A file that cannot be decoded** (deleted from disk, corrupt, a video whose thumbnail is missing) → Suggest still opens, with cards built from `PixelFacts.Unknown`. Test: `SceneRecipeTests.EveryRecipeBuildsAValidSceneFromUnknownPixels` (Task 4) and the UI `try/catch` per file (Task 5).
2. **Clicking a card, then clicking a gallery thumbnail** → the strip closes and the new pick is kept (the saved draft is not restored over it). Owned by Task 5 (`PickGalleryItem` calls `Suggestions.Discard()`), manual check step.
3. **A selection where no recipe applies** (a single medium-sized video) → Showcase still offers it; an empty card list never shows an empty strip. Test: `SceneSuggesterTests.ASingleMediumVideoGetsShowcase` (Task 4).
4. **Same media, same seed on two machines** → identical scenes. Test: `SceneRecipeTests.BuildIsDeterministicPerSeed` (Task 4, compares `SceneFile.ToJson`).
5. **Fully transparent or zero-size image** → no division by zero, profile is Unknown-like. Test: `MediaAnalyzerTests.FullyTransparentImageHasNoColors` / `EmptyInputIsUnknown` (Task 1).

---

### Task 1: Media analysis — `PixelFacts`, `MediaProfile`, `MediaAnalyzer`

**Files:**
- Create: `WaBiBaBuSy.Models/Wallpaper/Suggest/MediaProfile.cs`
- Create: `WaBiBaBuSy.Models/Wallpaper/Suggest/MediaAnalyzer.cs`
- Test: `WaBiBaBuSy.Tests/MediaAnalyzerTests.cs`

**Interfaces:**
- Produces:
  - `enum MediaKind { Image, Gif, Video }`, `enum SizeClass { Small, Medium, FullScreen }`, `enum AspectClass { Wide, Square, Tall }`
  - `sealed record PixelFacts(bool HasAlpha, bool IsMonochrome, IReadOnlyList<string> DominantColors, double MeanLuminance)` with `static PixelFacts Unknown`
  - `sealed record MediaProfile(string Path, MediaKind Kind, int PixelWidth, int PixelHeight, SizeClass Size, AspectClass Aspect, PixelFacts Pixels, long? LoopMs, long? DurationMs)` with `static MediaProfile Create(string path, int width, int height, int screenWidth, int screenHeight, PixelFacts pixels, long durationMs)`
  - `static class MediaAnalyzer { PixelFacts Analyze(ReadOnlySpan<byte> bgra, int width, int height); SizeClass ClassifySize(int w, int h, int screenW, int screenH); AspectClass ClassifyAspect(int w, int h); MediaKind KindOf(string path); }`

- [ ] **Step 1: Write the failing tests**

`WaBiBaBuSy.Tests/MediaAnalyzerTests.cs`:

```csharp
using WaBiBaBuSy.Models.Wallpaper.Suggest;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class MediaAnalyzerTests
{
    /// <summary>A w × h BGRA buffer filled by <paramref name="pixel"/>(x, y) → (r, g, b, a).</summary>
    private static byte[] Image(int w, int h, Func<int, int, (byte r, byte g, byte b, byte a)> pixel)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                int i = (y * w + x) * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a;
            }
        return px;
    }

    [Fact]
    public void OpaqueRedIsMonochromeWithoutAlpha()
    {
        var f = MediaAnalyzer.Analyze(Image(8, 8, (_, _) => (255, 0, 0, 255)), 8, 8);
        Assert.False(f.HasAlpha);
        Assert.True(f.IsMonochrome);
        Assert.Equal(new[] { "#F80808" }, f.DominantColors);
        Assert.InRange(f.MeanLuminance, 0.20, 0.23);
    }

    [Fact]
    public void BlackLogoOnTransparencyIsMonochromeWithAlpha()
    {
        var f = MediaAnalyzer.Analyze(Image(8, 8, (x, _) => x < 4 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)), 8, 8);
        Assert.True(f.HasAlpha);
        Assert.True(f.IsMonochrome);
        Assert.Equal(new[] { "#080808" }, f.DominantColors);
        Assert.InRange(f.MeanLuminance, 0.0, 0.01);
    }

    [Fact]
    public void FourColorQuadrantsAreColorfulWithThreeDominantColors()
    {
        var f = MediaAnalyzer.Analyze(Image(8, 8, (x, y) => (x < 4, y < 4) switch
        {
            (true, true) => (255, 0, 0, 255),
            (false, true) => (0, 255, 0, 255),
            (true, false) => (0, 0, 255, 255),
            _ => (255, 255, 0, 255),
        }), 8, 8);
        Assert.False(f.IsMonochrome);
        Assert.Equal(3, f.DominantColors.Count);
    }

    [Fact]
    public void GreyscaleGradientIsMonochrome()
    {
        var f = MediaAnalyzer.Analyze(Image(16, 1, (x, _) => ((byte)(x * 16), (byte)(x * 16), (byte)(x * 16), (byte)255)), 16, 1);
        Assert.True(f.IsMonochrome);
    }

    [Fact]
    public void NeighbouringHuesCountAsMonochrome()
    {
        // pure red (0°) and red-orange (~14°) — one hue family
        var f = MediaAnalyzer.Analyze(Image(8, 8, (x, _) => x < 4 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)60, (byte)0, (byte)255)), 8, 8);
        Assert.True(f.IsMonochrome);
    }

    [Fact]
    public void NoisyNearBlackPixelsCountAsAchromatic()
    {
        // JPEG-ish noise around black must not make a black logo "colourful"
        var f = MediaAnalyzer.Analyze(Image(8, 8, (x, y) => ((x + y) % 2 == 0 ? ((byte)12, (byte)4, (byte)2, (byte)255) : ((byte)2, (byte)10, (byte)14, (byte)255))), 8, 8);
        Assert.True(f.IsMonochrome);
    }

    [Fact]
    public void FullyTransparentImageHasNoColors()
    {
        var f = MediaAnalyzer.Analyze(Image(4, 4, (_, _) => (0, 0, 0, 0)), 4, 4);
        Assert.True(f.HasAlpha);
        Assert.False(f.IsMonochrome);
        Assert.Empty(f.DominantColors);
    }

    [Fact]
    public void EmptyInputIsUnknown()
    {
        Assert.Same(PixelFacts.Unknown, MediaAnalyzer.Analyze(ReadOnlySpan<byte>.Empty, 0, 0));
        Assert.Same(PixelFacts.Unknown, MediaAnalyzer.Analyze(new byte[8], 4, 4)); // too short for 4 × 4
    }

    [Fact]
    public void ColorsUnderFivePercentAreDropped()
    {
        // 1 green pixel of 64 = 1.6 %
        var f = MediaAnalyzer.Analyze(Image(8, 8, (x, y) => x == 0 && y == 0 ? ((byte)0, (byte)255, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255)), 8, 8);
        Assert.Equal(new[] { "#F8F8F8" }, f.DominantColors);
    }

    [Theory]
    [InlineData(200, 200, 1920, 1080, SizeClass.Small)]
    [InlineData(800, 600, 1920, 1080, SizeClass.Medium)]
    [InlineData(1920, 1080, 1920, 1080, SizeClass.FullScreen)]
    [InlineData(1400, 800, 1920, 1080, SizeClass.FullScreen)]
    [InlineData(0, 0, 1920, 1080, SizeClass.Medium)]
    [InlineData(200, 200, 0, 0, SizeClass.Medium)]
    public void SizeClasses(int w, int h, int sw, int sh, SizeClass expected) =>
        Assert.Equal(expected, MediaAnalyzer.ClassifySize(w, h, sw, sh));

    [Theory]
    [InlineData(1000, 200, AspectClass.Wide)]
    [InlineData(300, 300, AspectClass.Square)]
    [InlineData(200, 500, AspectClass.Tall)]
    [InlineData(0, 0, AspectClass.Square)]
    public void AspectClasses(int w, int h, AspectClass expected) =>
        Assert.Equal(expected, MediaAnalyzer.ClassifyAspect(w, h));

    [Theory]
    [InlineData(@"C:\a\cat.gif", MediaKind.Gif)]
    [InlineData(@"C:\a\logo.PNG", MediaKind.Image)]
    [InlineData(@"C:\a\clip.mp4", MediaKind.Video)]
    public void KindFromExtension(string path, MediaKind expected) =>
        Assert.Equal(expected, MediaAnalyzer.KindOf(path));

    [Fact]
    public void CreateKeepsLoopForGifsAndDurationForVideos()
    {
        var gif = MediaProfile.Create(@"C:\a\cat.gif", 200, 200, 1920, 1080, PixelFacts.Unknown, 1200);
        Assert.Equal(1200, gif.LoopMs);
        Assert.Null(gif.DurationMs);
        var video = MediaProfile.Create(@"C:\a\clip.mp4", 1920, 1080, 1920, 1080, PixelFacts.Unknown, 60000);
        Assert.Null(video.LoopMs);
        Assert.Equal(60000, video.DurationMs);
        var still = MediaProfile.Create(@"C:\a\logo.png", 200, 200, 1920, 1080, PixelFacts.Unknown, 0);
        Assert.Null(still.LoopMs);
        Assert.Equal(SizeClass.Small, still.Size);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~MediaAnalyzerTests"`
Expected: build FAILS — `The type or namespace name 'Suggest' does not exist in the namespace 'WaBiBaBuSy.Models.Wallpaper'`.

- [ ] **Step 3: Write `MediaProfile.cs`**

```csharp
namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>What kind of file a media item is (decides which recipes can use it).</summary>
public enum MediaKind { Image, Gif, Video }

/// <summary>Size of a media item relative to the server's primary monitor.</summary>
public enum SizeClass { Small, Medium, FullScreen }

/// <summary>Shape of a media item: Wide (&gt; 2.5 : 1), Tall (&lt; 0.6 : 1) or Square in between.</summary>
public enum AspectClass { Wide, Square, Tall }

/// <summary>Facts read from a file's (downscaled) first frame.</summary>
/// <param name="HasAlpha">More than 5 % of the pixels are see-through (alpha &lt; 128).</param>
/// <param name="IsMonochrome">At least 90 % of the visible pixels are achromatic or share the dominant hue.</param>
/// <param name="DominantColors">Up to 3 <c>#RRGGBB</c> colours, most frequent first.</param>
/// <param name="MeanLuminance">Mean Rec. 709 luma of the visible pixels, 0..1.</param>
public sealed record PixelFacts(bool HasAlpha, bool IsMonochrome, IReadOnlyList<string> DominantColors, double MeanLuminance)
{
    /// <summary>Used when a file could not be decoded: no colour facts, mid luminance.</summary>
    public static PixelFacts Unknown { get; } = new(false, false, Array.Empty<string>(), 0.5);
}

/// <summary>Everything the suggestion recipes know about one picked file.</summary>
public sealed record MediaProfile(
    string Path, MediaKind Kind, int PixelWidth, int PixelHeight,
    SizeClass Size, AspectClass Aspect, PixelFacts Pixels, long? LoopMs, long? DurationMs)
{
    /// <summary>
    /// Build a profile. <paramref name="durationMs"/> is the gallery's duration: the loop length for a GIF,
    /// the clip length for a video, ignored for stills; 0 or less means unknown.
    /// </summary>
    public static MediaProfile Create(string path, int width, int height, int screenWidth, int screenHeight,
        PixelFacts pixels, long durationMs)
    {
        var kind = MediaAnalyzer.KindOf(path);
        return new MediaProfile(path, kind, width, height,
            MediaAnalyzer.ClassifySize(width, height, screenWidth, screenHeight),
            MediaAnalyzer.ClassifyAspect(width, height), pixels,
            kind == MediaKind.Gif && durationMs > 0 ? durationMs : null,
            kind == MediaKind.Video && durationMs > 0 ? durationMs : null);
    }
}
```

- [ ] **Step 4: Write `MediaAnalyzer.cs`**

```csharp
namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>Pure pixel and size analysis for scene suggestions (spec 2026-10-14 §1.1). No imaging dependency.</summary>
public static class MediaAnalyzer
{
    private const byte VisibleAlpha = 128;
    private const double AlphaShare = 0.05;
    private const double LowSaturation = 0.15;
    private const int DarkMax = 40;          // max channel below this = achromatic (noise near black)
    private const double MonoShare = 0.90;
    private const double ColorShare = 0.05;
    private const int MaxColors = 3;
    private const int HueBins = 36;          // 10° each
    private const int HueReachBins = 2;      // dominant bin ± 2

    /// <summary>Analyse a BGRA buffer (4 bytes per pixel, rows packed). Too-short or empty input → <see cref="PixelFacts.Unknown"/>.</summary>
    public static PixelFacts Analyze(ReadOnlySpan<byte> bgra, int width, int height)
    {
        int n = width * height;
        if (width <= 0 || height <= 0 || bgra.Length < n * 4) return PixelFacts.Unknown;

        int transparent = 0, visible = 0, achromatic = 0;
        double lumaSum = 0;
        var hueBins = new int[HueBins];
        var buckets = new Dictionary<int, int>();

        for (int i = 0; i < n; i++)
        {
            byte b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2], a = bgra[i * 4 + 3];
            if (a < VisibleAlpha) { transparent++; continue; }
            visible++;
            lumaSum += (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;

            int key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
            buckets[key] = buckets.GetValueOrDefault(key) + 1;

            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max < DarkMax || (max - min) / (double)max < LowSaturation) { achromatic++; continue; }
            hueBins[(int)(Hue(r, g, b, max, min) / 10.0) % HueBins]++;
        }

        bool hasAlpha = transparent > AlphaShare * n;
        if (visible == 0) return new PixelFacts(hasAlpha, false, Array.Empty<string>(), PixelFacts.Unknown.MeanLuminance);

        int dominant = 0;
        for (int i = 1; i < HueBins; i++) if (hueBins[i] > hueBins[dominant]) dominant = i;
        int nearDominant = 0;
        for (int d = -HueReachBins; d <= HueReachBins; d++) nearDominant += hueBins[(dominant + d + HueBins) % HueBins];
        bool mono = achromatic + nearDominant >= MonoShare * visible;

        var colors = buckets
            .Where(kv => kv.Value >= ColorShare * visible)
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Take(MaxColors)
            .Select(kv => BucketHex(kv.Key))
            .ToList();

        return new PixelFacts(hasAlpha, mono, colors, lumaSum / visible);
    }

    /// <summary>Small: height &lt; 25 % of the screen; FullScreen: ≥ 70 % of both sides; unknown sizes are Medium.</summary>
    public static SizeClass ClassifySize(int width, int height, int screenWidth, int screenHeight)
    {
        if (width <= 0 || height <= 0 || screenWidth <= 0 || screenHeight <= 0) return SizeClass.Medium;
        if (width >= 0.7 * screenWidth && height >= 0.7 * screenHeight) return SizeClass.FullScreen;
        return height < 0.25 * screenHeight ? SizeClass.Small : SizeClass.Medium;
    }

    /// <summary>Wide above 2.5 : 1, Tall below 0.6 : 1, otherwise (and for unknown sizes) Square.</summary>
    public static AspectClass ClassifyAspect(int width, int height)
    {
        if (width <= 0 || height <= 0) return AspectClass.Square;
        double aspect = width / (double)height;
        return aspect > 2.5 ? AspectClass.Wide : aspect < 0.6 ? AspectClass.Tall : AspectClass.Square;
    }

    /// <summary>Gif for <c>.gif</c>, Video for anything <see cref="SceneChecks.IsVideo"/> accepts, else Image.</summary>
    public static MediaKind KindOf(string path) =>
        string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase) ? MediaKind.Gif
        : SceneChecks.IsVideo(path) ? MediaKind.Video
        : MediaKind.Image;

    private static double Hue(int r, int g, int b, int max, int min)
    {
        double d = max - min;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    private static string BucketHex(int key)
    {
        int r = (key >> 8 & 0xF) << 4 | 8, g = (key >> 4 & 0xF) << 4 | 8, b = (key & 0xF) << 4 | 8;
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~MediaAnalyzerTests"`
Expected: PASS (all). If `SceneChecks.IsVideo` does not accept `.mp4`, read `SceneChecks.cs:24` and use an extension it lists in the `KindFromExtension` test.

- [ ] **Step 6: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/Suggest/MediaProfile.cs WaBiBaBuSy.Models/Wallpaper/Suggest/MediaAnalyzer.cs WaBiBaBuSy.Tests/MediaAnalyzerTests.cs
git commit -m "feat(suggest): media analysis - pixel facts, size / aspect classes, media profile"
```

---

### Task 2: Selection profile

**Files:**
- Create: `WaBiBaBuSy.Models/Wallpaper/Suggest/SelectionProfile.cs`
- Test: `WaBiBaBuSy.Tests/SelectionProfileTests.cs`

**Interfaces:**
- Consumes: `MediaProfile`, `PixelFacts`, `SizeClass` (Task 1).
- Produces: `enum SelectionMix { Single, Uniform, BigPlusSmall, Mixed }`; `sealed class SelectionProfile(IReadOnlyList<MediaProfile> files, int screenWidth, int screenHeight)` with `Files`, `Count`, `ScreenWidth`, `ScreenHeight`, `Mix`, `AllMonochrome`, `SharedPalette` (≤ 5), `ShortestLoopMs` (`long?`), `Big` (`MediaProfile?`), `Sprites` (`IReadOnlyList<MediaProfile>`), `HasVideoSprite`, `AllSpritesHaveAlpha`, `AllSpritesSmall`, `MeanLuminance`, `Describe()` (`string`, e.g. `"cat.gif + 2 more"`).

- [ ] **Step 1: Write the failing tests**

```csharp
using WaBiBaBuSy.Models.Wallpaper.Suggest;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SelectionProfileTests
{
    internal static MediaProfile File(string name, int w, int h, bool mono = true, bool alpha = true,
        string[]? colors = null, long durationMs = 0, double luma = 0.5) =>
        MediaProfile.Create(@"C:\m\" + name, w, h, 1920, 1080,
            new PixelFacts(alpha, mono, colors ?? new[] { "#F80808" }, luma), durationMs);

    [Fact]
    public void OneFileIsSingle() =>
        Assert.Equal(SelectionMix.Single, new SelectionProfile(new[] { File("a.png", 200, 200) }, 1920, 1080).Mix);

    [Fact]
    public void SimilarHeightsAreUniform() =>
        Assert.Equal(SelectionMix.Uniform, new SelectionProfile(new[] { File("a.png", 200, 200), File("b.png", 300, 380) }, 1920, 1080).Mix);

    [Fact]
    public void OneFullScreenPlusSmallIsBigPlusSmall()
    {
        var s = new SelectionProfile(new[] { File("small.gif", 200, 200), File("big.gif", 1920, 1080) }, 1920, 1080);
        Assert.Equal(SelectionMix.BigPlusSmall, s.Mix);
        Assert.Equal("big.gif", Path.GetFileName(s.Big!.Path));
        Assert.Equal(new[] { "small.gif" }, s.Sprites.Select(f => Path.GetFileName(f.Path)));
    }

    [Fact]
    public void VeryDifferentHeightsAreMixed() =>
        Assert.Equal(SelectionMix.Mixed, new SelectionProfile(new[] { File("a.png", 100, 100), File("b.png", 500, 500) }, 1920, 1080).Mix);

    [Fact]
    public void SingleFullScreenIsTheBigOneAndTheOnlySprite()
    {
        var s = new SelectionProfile(new[] { File("big.gif", 1920, 1080) }, 1920, 1080);
        Assert.NotNull(s.Big);
        Assert.Single(s.Sprites);
    }

    [Fact]
    public void PaletteKeepsHeroColorsFirstWithoutDuplicates()
    {
        var s = new SelectionProfile(new[]
        {
            File("a.png", 200, 200, colors: new[] { "#F80808", "#080808" }),
            File("b.png", 200, 200, colors: new[] { "#080808", "#08F808", "#0808F8", "#F8F808", "#888888" }),
        }, 1920, 1080);
        Assert.Equal(new[] { "#F80808", "#080808", "#08F808", "#0808F8", "#F8F808" }, s.SharedPalette);
    }

    [Fact]
    public void ShortestLoopIgnoresStills()
    {
        var s = new SelectionProfile(new[] { File("a.gif", 200, 200, durationMs: 1500), File("b.gif", 200, 200, durationMs: 800), File("c.png", 200, 200) }, 1920, 1080);
        Assert.Equal(800, s.ShortestLoopMs);
        Assert.Null(new SelectionProfile(new[] { File("c.png", 200, 200) }, 1920, 1080).ShortestLoopMs);
    }

    [Fact]
    public void AllMonochromeNeedsEveryFile() =>
        Assert.False(new SelectionProfile(new[] { File("a.png", 200, 200), File("b.png", 200, 200, mono: false) }, 1920, 1080).AllMonochrome);

    [Fact]
    public void DescribeNamesTheHeroAndCount() =>
        Assert.Equal("a.png + 2 more", new SelectionProfile(new[] { File("a.png", 200, 200), File("b.png", 200, 200), File("c.png", 200, 200) }, 1920, 1080).Describe());

    [Fact]
    public void EmptySelectionThrows() =>
        Assert.Throws<ArgumentException>(() => new SelectionProfile(Array.Empty<MediaProfile>(), 1920, 1080));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SelectionProfileTests"`
Expected: build FAILS — `SelectionProfile` / `SelectionMix` not found.

- [ ] **Step 3: Write `SelectionProfile.cs`**

```csharp
namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>How the picked files relate to each other (spec 2026-10-14 §1.2).</summary>
public enum SelectionMix { Single, Uniform, BigPlusSmall, Mixed }

/// <summary>The whole selection the recipes score and build from: files (hero first) plus the screen they target.</summary>
public sealed class SelectionProfile
{
    private const int MaxPalette = 5;

    /// <summary>Profile the selection. <paramref name="files"/> must hold at least one file, hero first.</summary>
    public SelectionProfile(IReadOnlyList<MediaProfile> files, int screenWidth, int screenHeight)
    {
        if (files.Count == 0) throw new ArgumentException("A selection needs at least one file", nameof(files));
        Files = files;
        ScreenWidth = screenWidth > 0 ? screenWidth : 1920;
        ScreenHeight = screenHeight > 0 ? screenHeight : 1080;
        Mix = ClassifyMix(files);
        Big = Mix == SelectionMix.BigPlusSmall ? files.First(f => f.Size == SizeClass.FullScreen)
            : files.Count == 1 && files[0].Size == SizeClass.FullScreen ? files[0]
            : null;
        Sprites = Mix == SelectionMix.BigPlusSmall ? files.Where(f => f.Size != SizeClass.FullScreen).ToList() : files;
        AllMonochrome = Sprites.All(f => f.Pixels.IsMonochrome);
        var paletteSource = Big != null && Mix == SelectionMix.BigPlusSmall ? files.OrderBy(f => f == Big ? 0 : 1) : files.AsEnumerable();
        SharedPalette = paletteSource.SelectMany(f => f.Pixels.DominantColors)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPalette).ToList();
        var loops = files.Where(f => f.LoopMs > 0).Select(f => f.LoopMs!.Value).ToList();
        ShortestLoopMs = loops.Count > 0 ? loops.Min() : null;
    }

    /// <summary>The picked files, hero first.</summary>
    public IReadOnlyList<MediaProfile> Files { get; }
    /// <summary>Number of picked files.</summary>
    public int Count => Files.Count;
    /// <summary>Primary monitor width in pixels (1920 when unknown).</summary>
    public int ScreenWidth { get; }
    /// <summary>Primary monitor height in pixels (1080 when unknown).</summary>
    public int ScreenHeight { get; }
    /// <summary>How the files relate to each other.</summary>
    public SelectionMix Mix { get; }
    /// <summary>The full-screen file (BigPlusSmall, or a single full-screen file); otherwise null.</summary>
    public MediaProfile? Big { get; }
    /// <summary>The files sprite recipes move: all of them, except the big one in BigPlusSmall.</summary>
    public IReadOnlyList<MediaProfile> Sprites { get; }
    /// <summary>Every sprite is monochrome (and so can be tinted).</summary>
    public bool AllMonochrome { get; }
    /// <summary>Up to 5 dominant colours (the big file's first in BigPlusSmall, else the hero's), no duplicates.</summary>
    public IReadOnlyList<string> SharedPalette { get; }
    /// <summary>The shortest GIF loop in milliseconds, or null without GIFs.</summary>
    public long? ShortestLoopMs { get; }

    /// <summary>A sprite is a video (only Showcase can use videos).</summary>
    public bool HasVideoSprite => Sprites.Any(f => f.Kind == MediaKind.Video);
    /// <summary>Every sprite has transparency (can float, can swarm).</summary>
    public bool AllSpritesHaveAlpha => Sprites.All(f => f.Pixels.HasAlpha);
    /// <summary>Every sprite is Small.</summary>
    public bool AllSpritesSmall => Sprites.All(f => f.Size == SizeClass.Small);
    /// <summary>Mean luminance over the sprites.</summary>
    public double MeanLuminance => Sprites.Average(f => f.Pixels.MeanLuminance);

    /// <summary>"cat.gif" or "cat.gif + 2 more" for the strip title.</summary>
    public string Describe() =>
        Path.GetFileName(Files[0].Path) + (Count > 1 ? $" + {Count - 1} more" : string.Empty);

    private static SelectionMix ClassifyMix(IReadOnlyList<MediaProfile> files)
    {
        if (files.Count == 1) return SelectionMix.Single;
        if (files.Count(f => f.Size == SizeClass.FullScreen) == 1) return SelectionMix.BigPlusSmall;
        if (files.Any(f => f.PixelHeight <= 0)) return SelectionMix.Mixed;
        int min = files.Min(f => f.PixelHeight), max = files.Max(f => f.PixelHeight);
        return max <= 2 * min ? SelectionMix.Uniform : SelectionMix.Mixed;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SelectionProfileTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/Suggest/SelectionProfile.cs WaBiBaBuSy.Tests/SelectionProfileTests.cs
git commit -m "feat(suggest): selection profile - mix, palette, loops, sprites vs big file"
```

---

### Task 3: Shared recipe rules — `SuggestKit`

**Files:**
- Create: `WaBiBaBuSy.Models/Wallpaper/Suggest/SuggestKit.cs`
- Test: `WaBiBaBuSy.Tests/SuggestKitTests.cs`

**Interfaces:**
- Consumes: `SelectionProfile`, `MediaProfile` (Tasks 1–2); `PatternLayout.Hash3(int seed, int i, int j)`; `CrossScreenConfig`, `AnimationLayerConfig`, `ColorGradingConfig`, `ColorGradingMode`, `ContentFitMode` (existing).
- Produces (all `public static` on `SuggestKit`):
  - `double Unit(int seed, int salt)` → [0, 1)
  - `float Range(int seed, int salt, float min, float max)`
  - `IReadOnlyList<string> PartyColors(int seed)` (5 colours, rotated by seed)
  - `string BackgroundHex(SelectionProfile s, bool forceDark = false)`
  - `double SnapCyclesPerSecond(SelectionProfile s, double hz)`
  - `CrossScreenConfig NewScene(CrossScreenConfig baseScene, SelectionProfile s, IReadOnlyList<MediaProfile> sprites, double heightFraction)` — fresh scene: base's `SelectedMonitorIds` / `DistributionMode`, solid background from `BackgroundHex(s)`, `AnimationPath` = first sprite, `AdditionalAnimationPaths` = the rest, `FitMode = TargetHeight`, `TargetHeight = round(heightFraction · ScreenHeight)`, `SizeUnit = Pixels`, `SpeedMultiplier = 1.0`.
  - `string Mix(string hex, double towardWhite)` is private.

- [ ] **Step 1: Write the failing tests**

```csharp
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Models.Wallpaper.Suggest;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SuggestKitTests
{
    private static SelectionProfile One(double luma = 0.5, string[]? colors = null, long loopMs = 0, string name = "a.gif") =>
        new(new[] { SelectionProfileTests.File(name, 200, 200, colors: colors, durationMs: loopMs, luma: luma) }, 1920, 1080);

    [Fact]
    public void UnitIsInRangeAndDeterministic()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            double u = SuggestKit.Unit(seed, 3);
            Assert.InRange(u, 0.0, 0.9999999);
            Assert.Equal(u, SuggestKit.Unit(seed, 3));
        }
        Assert.NotEqual(SuggestKit.Unit(1, 3), SuggestKit.Unit(2, 3));
    }

    [Fact]
    public void RangeStaysInsideItsBounds()
    {
        for (int seed = 0; seed < 200; seed++)
            Assert.InRange(SuggestKit.Range(seed, 1, 300f, 500f), 300f, 500f);
    }

    [Fact]
    public void LightMediaGetsADarkBackgroundInItsOwnHue()
    {
        string hex = SuggestKit.BackgroundHex(One(luma: 0.8, colors: new[] { "#08F808" }));
        Assert.True(Luma(hex) < 0.12, hex);
        Assert.True(Channel(hex, 1) > Channel(hex, 0), hex); // still green
    }

    [Fact]
    public void DarkMediaGetsALightBackground() =>
        Assert.True(Luma(SuggestKit.BackgroundHex(One(luma: 0.1, colors: new[] { "#080808" }))) > 0.8);

    [Fact]
    public void ForceDarkWinsOverDarkMedia() =>
        Assert.True(Luma(SuggestKit.BackgroundHex(One(luma: 0.1, colors: new[] { "#080808" }), forceDark: true)) < 0.12);

    [Fact]
    public void NoPaletteFallsBackToNearBlack() =>
        Assert.True(Luma(SuggestKit.BackgroundHex(One(luma: 0.5, colors: Array.Empty<string>()))) < 0.12);

    [Fact]
    public void CyclesSnapToWholeLoops()
    {
        // loop 1.2 s, wanted 0.25 Hz (4 s) → 3 loops = 3.6 s
        Assert.Equal(1000.0 / 3600.0, SuggestKit.SnapCyclesPerSecond(One(loopMs: 1200), 0.25), 6);
        // faster than one loop → one loop
        Assert.Equal(1000.0 / 1200.0, SuggestKit.SnapCyclesPerSecond(One(loopMs: 1200), 5.0), 6);
        // no GIF → unchanged
        Assert.Equal(0.25, SuggestKit.SnapCyclesPerSecond(One(name: "a.png"), 0.25));
    }

    [Fact]
    public void NewSceneKeepsTargetsAndSetsTheSprites()
    {
        var baseScene = new CrossScreenConfig
        {
            SelectedMonitorIds = new() { "m1", "m2" },
            DistributionMode = AnimationDistributionMode.Simultaneous,
        };
        baseScene.Movement.Type = MovementType.Circular;
        var s = new SelectionProfile(new[] { SelectionProfileTests.File("a.png", 200, 200), SelectionProfileTests.File("b.png", 200, 200) }, 1920, 1080);

        var scene = SuggestKit.NewScene(baseScene, s, s.Sprites, 0.2);

        Assert.Equal(new[] { "m1", "m2" }, scene.SelectedMonitorIds);
        Assert.NotSame(baseScene.SelectedMonitorIds, scene.SelectedMonitorIds);
        Assert.Equal(AnimationDistributionMode.Simultaneous, scene.DistributionMode);
        Assert.Equal(@"C:\m\a.png", scene.Animation.AnimationPath);
        Assert.Equal(new[] { @"C:\m\b.png" }, scene.Animation.AdditionalAnimationPaths);
        Assert.Equal(ContentFitMode.TargetHeight, scene.Animation.FitMode);
        Assert.Equal(216, scene.Animation.TargetHeight);
        Assert.Equal(MovementType.Linear, scene.Movement.Type); // fresh, not the base's Circular
        Assert.Equal(BackgroundMode.SolidColor, scene.Background.Mode);
    }

    [Fact]
    public void PartyColorsAreFiveValidColorsRotatedBySeed()
    {
        var a = SuggestKit.PartyColors(0);
        Assert.Equal(5, a.Count);
        Assert.All(a, c => Assert.True(ColorGrader.IsValidColor(c), c));
        Assert.Equal(a.ToHashSet(), SuggestKit.PartyColors(3).ToHashSet());
    }

    private static int Channel(string hex, int i) => Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16);
    private static double Luma(string hex) => (0.2126 * Channel(hex, 0) + 0.7152 * Channel(hex, 1) + 0.0722 * Channel(hex, 2)) / 255.0;
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SuggestKitTests"`
Expected: build FAILS — `SuggestKit` not found. (If `ColorGrader.IsValidColor` is not public, use `Assert.Matches("^#[0-9A-F]{6}$", c)` instead.)

- [ ] **Step 3: Write `SuggestKit.cs`**

```csharp
namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>Rules every recipe shares (spec 2026-10-14 §2.1): seeded numbers, background, loop-snapped timing, the fresh scene.</summary>
public static class SuggestKit
{
    private static readonly string[] Party = { "#FF3B30", "#FFCC00", "#34C759", "#0A84FF", "#BF5AF2" };
    private const double DarkLuma = 0.08, LightLuma = 0.85;
    private const double LightMedia = 0.6, DarkMedia = 0.25;

    /// <summary>A deterministic number in [0, 1) for (seed, salt) — the only randomness recipes use.</summary>
    public static double Unit(int seed, int salt) => (uint)PatternLayout.Hash3(seed, salt, 0x5EED) / 4294967296.0;

    /// <summary>A deterministic value in [min, max].</summary>
    public static float Range(int seed, int salt, float min, float max) => (float)(min + (max - min) * Unit(seed, salt));

    /// <summary>Five vivid colours (the DVD Bounce preset's list), rotated by the seed.</summary>
    public static IReadOnlyList<string> PartyColors(int seed)
    {
        int start = (int)(Unit(seed, 77) * Party.Length);
        return Enumerable.Range(0, Party.Length).Select(i => Party[(start + i) % Party.Length]).ToList();
    }

    /// <summary>
    /// The first palette colour pushed dark (light or mid media, or <paramref name="forceDark"/>) or light (dark media).
    /// No palette → a near-black or near-white grey.
    /// </summary>
    public static string BackgroundHex(SelectionProfile s, bool forceDark = false)
    {
        bool light = !forceDark && s.MeanLuminance < DarkMedia;
        string seed = s.SharedPalette.Count > 0 ? s.SharedPalette[0] : "#808080";
        var (r, g, b) = Parse(seed);
        double luma = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        if (light)
        {
            double t = luma >= LightLuma ? 0 : (LightLuma - luma) / (1 - luma);
            return Hex(r + (255 - r) * t, g + (255 - g) * t, b + (255 - b) * t);
        }
        double k = luma <= 0 ? 0 : Math.Min(1, DarkLuma / luma);
        return luma <= 0 ? Hex(20, 20, 20) : Hex(r * k, g * k, b * k);
    }

    /// <summary>
    /// With a GIF in the selection, round a cycle frequency so one cycle lasts a whole number of loops (at least one);
    /// without one, return it unchanged.
    /// </summary>
    public static double SnapCyclesPerSecond(SelectionProfile s, double hz)
    {
        if (s.ShortestLoopMs is not long loop || loop <= 0 || hz <= 0) return hz;
        double periodMs = 1000.0 / hz;
        long loops = Math.Max(1, (long)Math.Round(periodMs / loop));
        return 1000.0 / (loops * loop);
    }

    /// <summary>
    /// A fresh scene for a recipe: the base scene's monitor selection and distribution mode, a solid background
    /// from <see cref="BackgroundHex"/>, <paramref name="sprites"/> as the animation files, scaled to
    /// <paramref name="heightFraction"/> of the screen height. Movement is a default <see cref="MovementConfig"/>.
    /// </summary>
    public static CrossScreenConfig NewScene(CrossScreenConfig baseScene, SelectionProfile s,
        IReadOnlyList<MediaProfile> sprites, double heightFraction) => new()
    {
        SelectedMonitorIds = new List<string>(baseScene.SelectedMonitorIds),
        DistributionMode = baseScene.DistributionMode,
        Background = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = BackgroundHex(s) },
        Animation = new AnimationLayerConfig
        {
            AnimationPath = sprites[0].Path,
            AdditionalAnimationPaths = sprites.Skip(1).Select(f => f.Path).ToList(),
            FitMode = ContentFitMode.TargetHeight,
            SizeUnit = SizeUnit.Pixels,
            TargetHeight = (int)Math.Round(heightFraction * s.ScreenHeight),
            SpeedMultiplier = 1.0,
        },
        Movement = new MovementConfig(),
    };

    private static (int r, int g, int b) Parse(string hex)
    {
        try
        {
            return (Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));
        }
        catch (Exception e) when (e is FormatException or ArgumentException) { return (128, 128, 128); }
    }

    private static string Hex(double r, double g, double b) =>
        $"#{Clamp(r):X2}{Clamp(g):X2}{Clamp(b):X2}";

    private static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, 0, 255));
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SuggestKitTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/Suggest/SuggestKit.cs WaBiBaBuSy.Tests/SuggestKitTests.cs
git commit -m "feat(suggest): shared recipe rules - seeded values, background, loop-snapped cycles, fresh scene"
```

---

### Task 4: The nine recipes and the picker

**Files:**
- Create: `WaBiBaBuSy.Models/Wallpaper/Suggest/SceneRecipes.cs` (interface, `Suggestion`, nine recipes, `All`)
- Create: `WaBiBaBuSy.Models/Wallpaper/Suggest/SceneSuggester.cs`
- Test: `WaBiBaBuSy.Tests/SceneRecipeTests.cs`, `WaBiBaBuSy.Tests/SceneSuggesterTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3; `SceneFile.ToJson`, `SceneChecks.Validate(scene, null)`, `PatternConfig`, `SpriteEffectsConfig`, `TrailConfig`, `PulseConfig`, `BezelEffect`, `MovementType`.
- Produces:
  - `interface ISceneRecipe { string Name { get; } double Score(SelectionProfile s); Suggestion Build(SelectionProfile s, CrossScreenConfig baseScene, int seed); }`
  - `sealed record Suggestion(string RecipeName, string Why, CrossScreenConfig Scene)`
  - `static class SceneRecipes { IReadOnlyList<ISceneRecipe> All { get; } }` — names exactly: `"DVD Bounce"`, `"Logo Conveyor"`, `"Marquee"`, `"Orbit"`, `"Pulse Grid"`, `"Fireflies"`, `"Comet"`, `"Parade"`, `"Showcase"`.
  - `sealed record SuggestionSet(IReadOnlyList<Suggestion> Cards, string? Note)`
  - `static class SceneSuggester { const int CardCount = 4; const string LayersNote = "big + small together needs scene layers"; SuggestionSet Suggest(SelectionProfile s, CrossScreenConfig baseScene, int seed); SuggestionSet Shuffle(SelectionProfile s, CrossScreenConfig baseScene, int seed, IReadOnlyCollection<string> shownRecipeNames); }`

- [ ] **Step 1: Write the failing recipe tests**

`WaBiBaBuSy.Tests/SceneRecipeTests.cs`:

```csharp
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Models.Wallpaper.Suggest;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SceneRecipeTests
{
    public static IEnumerable<object[]> Recipes() => SceneRecipes.All.Select(r => new object[] { r.Name });

    private static ISceneRecipe R(string name) => SceneRecipes.All.Single(r => r.Name == name);
    private static MediaProfile F(string name, int w, int h, bool mono = true, bool alpha = true, long ms = 0) =>
        SelectionProfileTests.File(name, w, h, mono, alpha, durationMs: ms);
    private static SelectionProfile Sel(params MediaProfile[] files) => new(files, 1920, 1080);

    /// <summary>Selections every recipe is built against (scored or not, Build must never throw).</summary>
    private static IEnumerable<SelectionProfile> Selections() => new[]
    {
        Sel(F("logo.png", 200, 200)),
        Sel(F("logo.png", 200, 200, mono: false)),
        Sel(F("cat.gif", 300, 300, ms: 1200)),
        Sel(F("word.png", 900, 200)),
        Sel(F("a.png", 150, 150), F("b.png", 160, 160), F("c.png", 140, 140)),
        Sel(F("small.gif", 200, 200, ms: 800), F("big.gif", 1920, 1080, ms: 3000)),
        Sel(F("photo.jpg", 1920, 1080, mono: false, alpha: false)),
        Sel(MediaProfile.Create(@"C:\m\broken.png", 0, 0, 1920, 1080, PixelFacts.Unknown, 0)),
    };

    private static readonly CrossScreenConfig Base = new()
    {
        SelectedMonitorIds = new() { "m1" },
        DistributionMode = AnimationDistributionMode.Simultaneous,
    };

    [Fact]
    public void ThereAreNineUniquelyNamedRecipes() =>
        Assert.Equal(9, SceneRecipes.All.Select(r => r.Name).Distinct().Count());

    [Theory, MemberData(nameof(Recipes))]
    public void BuildIsDeterministicPerSeed(string name)
    {
        foreach (var s in Selections())
            Assert.Equal(SceneFile.ToJson(R(name).Build(s, Base, 7).Scene), SceneFile.ToJson(R(name).Build(s, Base, 7).Scene));
    }

    [Theory, MemberData(nameof(Recipes))]
    public void ScoresStayBetweenZeroAndOne(string name)
    {
        foreach (var s in Selections()) Assert.InRange(R(name).Score(s), 0.0, 1.0);
    }

    [Theory, MemberData(nameof(Recipes))]
    public void OfferedScenesHaveNoWarningsAndKeepTheTargets(string name)
    {
        foreach (var s in Selections().Where(s => R(name).Score(s) > 0))
        {
            var sug = R(name).Build(s, Base, 11);
            Assert.Empty(SceneChecks.Validate(sug.Scene, null));
            Assert.Equal(new[] { "m1" }, sug.Scene.SelectedMonitorIds);
            Assert.Equal(AnimationDistributionMode.Simultaneous, sug.Scene.DistributionMode);
            Assert.Equal(name, sug.RecipeName);
            Assert.False(string.IsNullOrWhiteSpace(sug.Why));
        }
    }

    [Theory, MemberData(nameof(Recipes))]
    public void EveryRecipeBuildsAValidSceneFromUnknownPixels(string name)
    {
        var s = Sel(MediaProfile.Create(@"C:\m\broken.png", 0, 0, 1920, 1080, PixelFacts.Unknown, 0));
        var scene = R(name).Build(s, Base, 3).Scene;
        Assert.Equal(@"C:\m\broken.png", scene.Animation.AnimationPath);
    }

    [Fact]
    public void DifferentSeedsVaryTheScene() =>
        Assert.NotEqual(SceneFile.ToJson(R("DVD Bounce").Build(Selections().First(), Base, 1).Scene),
                        SceneFile.ToJson(R("DVD Bounce").Build(Selections().First(), Base, 2).Scene));

    [Fact]
    public void MonoLogoIsTintedColorfulLogoIsNot()
    {
        Assert.NotEqual(ColorGradingMode.None, R("DVD Bounce").Build(Sel(F("logo.png", 200, 200)), Base, 1).Scene.Animation.ColorGrading.Mode);
        Assert.Equal(ColorGradingMode.None, R("DVD Bounce").Build(Sel(F("logo.png", 200, 200, mono: false)), Base, 1).Scene.Animation.ColorGrading.Mode);
    }

    [Fact]
    public void OrbitSpinsOneTurnPerWholeLoops()
    {
        var spin = R("Orbit").Build(Sel(F("cat.gif", 300, 300, ms: 1200)), Base, 1).Scene.Animation.Effects!.SpinDegPerSecond;
        double secondsPerTurn = 360.0 / Math.Abs(spin);
        Assert.Equal(0, Math.Round(secondsPerTurn * 1000) % 1200);
    }

    [Fact]
    public void BigPlusSmallSpriteRecipesUseOnlyTheSmallFiles()
    {
        var s = Sel(F("small.gif", 200, 200, ms: 800), F("big.gif", 1920, 1080, ms: 3000));
        var scene = R("DVD Bounce").Build(s, Base, 1).Scene;
        Assert.Equal(@"C:\m\small.gif", scene.Animation.AnimationPath);
        Assert.Empty(scene.Animation.AdditionalAnimationPaths);
        Assert.Equal(@"C:\m\big.gif", R("Showcase").Build(s, Base, 1).Scene.Animation.AnimationPath);
    }

    [Fact]
    public void VideoSpritesOnlyGetShowcase()
    {
        var s = Sel(MediaProfile.Create(@"C:\m\clip.mp4", 800, 600, 1920, 1080, PixelFacts.Unknown, 60000));
        Assert.All(SceneRecipes.All.Where(r => r.Name != "Showcase"), r => Assert.Equal(0, r.Score(s)));
        Assert.True(R("Showcase").Score(s) > 0);
    }

    [Fact]
    public void OpaqueMediaIsNotSwarmed()
    {
        var s = Sel(F("a.jpg", 150, 150, alpha: false), F("b.jpg", 150, 150, alpha: false));
        Assert.True(R("Pulse Grid").Score(s) <= 0.3);
        Assert.True(R("Fireflies").Score(s) <= 0.3);
    }
}
```

`WaBiBaBuSy.Tests/SceneSuggesterTests.cs`:

```csharp
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
    public void BigPlusSmallCarriesTheLayersNote()
    {
        Assert.Equal(SceneSuggester.LayersNote, SceneSuggester.Suggest(Sel(F("s.gif", 200, 200), F("big.gif", 1920, 1080)), Base, 1).Note);
        Assert.Null(SceneSuggester.Suggest(Sel(F("logo.png", 200, 200)), Base, 1).Note);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SceneRecipeTests|FullyQualifiedName~SceneSuggesterTests"`
Expected: build FAILS — `SceneRecipes` / `ISceneRecipe` / `SceneSuggester` not found.

- [ ] **Step 3: Write `SceneRecipes.cs`**

Values are copied from the matching starter presets in `TestScenarios/showcase/scenes/` (01, 02, 03, 05, 06, 07, 10, 13).

```csharp
namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>One kind of suggested scene: how well it fits a selection, and how to build it.</summary>
public interface ISceneRecipe
{
    /// <summary>Card title, unique.</summary>
    string Name { get; }
    /// <summary>0 = not offered for this selection, else 0..1 (higher = better fit).</summary>
    double Score(SelectionProfile s);
    /// <summary>Build the scene; pure in (selection, base scene, seed).</summary>
    Suggestion Build(SelectionProfile s, CrossScreenConfig baseScene, int seed);
}

/// <summary>A built card: recipe name, the one-line reason, the scene.</summary>
public sealed record Suggestion(string RecipeName, string Why, CrossScreenConfig Scene);

/// <summary>The recipe set (spec 2026-10-14 §2.2), modelled on the starter presets.</summary>
public static class SceneRecipes
{
    /// <summary>All recipes in a fixed order (ties in score are broken by this order).</summary>
    public static IReadOnlyList<ISceneRecipe> All { get; } = new ISceneRecipe[]
    {
        new DvdBounce(), new LogoConveyor(), new Marquee(), new Orbit(), new PulseGrid(),
        new Fireflies(), new Comet(), new Parade(), new Showcase(),
    };

    // Shared helpers -------------------------------------------------------------------------

    /// <summary>Sprite recipes never take videos or a lone full-screen file.</summary>
    internal static bool SpritesUsable(SelectionProfile s) =>
        !s.HasVideoSprite && !(s.Mix == SelectionMix.Single && s.Big != null);

    internal static string MonoWhy(SelectionProfile s, string tinted) => s.AllMonochrome ? tinted : "keeps its own colours";

    internal static string LoopWhy(SelectionProfile s) =>
        s.ShortestLoopMs is long ms ? $", timed to the {ms / 1000.0:0.#} s loop" : string.Empty;

    internal static ColorGradingConfig Tint(SelectionProfile s, ColorGradingMode monoMode, double cyclesPerSecond, int seed)
    {
        if (!s.AllMonochrome) return new ColorGradingConfig { Mode = ColorGradingMode.None };
        var party = SuggestKit.PartyColors(seed);
        return new ColorGradingConfig
        {
            Mode = monoMode,
            CyclesPerSecond = SuggestKit.SnapCyclesPerSecond(s, cyclesPerSecond),
            ColorList = party.ToList(),
            GradientA = party[0],
            GradientB = party[2],
            Seed = (int)(SuggestKit.Unit(seed, 31) * 1000) + 1,
        };
    }

    // Recipes ---------------------------------------------------------------------------------

    private sealed class DvdBounce : ISceneRecipe
    {
        public string Name => "DVD Bounce";
        public double Score(SelectionProfile s) =>
            !SpritesUsable(s) || s.Sprites.Count != 1 ? 0
            : 0.6 + (s.AllSpritesSmall ? 0.2 : 0) + (s.AllSpritesHaveAlpha ? 0.2 : 0);
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites.Take(1).ToList(), 0.22);
            scene.Movement.Type = MovementType.Bounce;
            scene.Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 320, 480);
            scene.Movement.DirectionAngleDegrees = SuggestKit.Range(seed, 2, 25, 40);
            scene.Animation.ColorGrading = Tint(s, ColorGradingMode.CycleColorList, 0.25, seed);
            return new(Name, "Bounces off the edges, " + MonoWhy(s, "cycles party colours") + LoopWhy(s), scene);
        }
    }

    private sealed class LogoConveyor : ISceneRecipe
    {
        public string Name => "Logo Conveyor";
        public double Score(SelectionProfile s) => !SpritesUsable(s) ? 0 : s.AllMonochrome ? 0.7 : 0.5;
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites, 0.13);
            scene.Animation.Pattern = new PatternConfig { Sizing = PatternConfig.SizingMode.Fill, SpacingX = 80, SpacingY = 80, Seed = seed };
            scene.Movement.Type = MovementType.Linear;
            scene.Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 150, 220);
            scene.Movement.DirectionAngleDegrees = 0;
            scene.Movement.Endless = s.AllMonochrome; // Endless needs a Traveling colour mode
            scene.Animation.ColorGrading = Tint(s, ColorGradingMode.TravelingRainbow, 0.1, seed);
            return new(Name, "A belt of logos across all screens, " + MonoWhy(s, "every one its own colour"), scene);
        }
    }

    private sealed class Marquee : ISceneRecipe
    {
        public string Name => "Marquee";
        public double Score(SelectionProfile s) =>
            SpritesUsable(s) && s.Sprites.All(f => f.Aspect == AspectClass.Wide) ? 0.9 : 0;
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites, 0.15);
            scene.Movement.Type = MovementType.SineWave;
            scene.Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 200, 300);
            scene.Movement.WaveAmplitudePixels = (float)(0.08 * s.ScreenHeight);
            scene.Movement.WaveFrequencyHz = (float)SuggestKit.SnapCyclesPerSecond(s, 0.2);
            scene.Movement.DirectionAngleDegrees = 0;
            scene.Animation.ColorGrading = Tint(s, ColorGradingMode.Gradient, 0.15, seed);
            return new(Name, "Wide wordmark scrolls on a gentle wave, " + MonoWhy(s, "with a colour gradient"), scene);
        }
    }

    private sealed class Orbit : ISceneRecipe
    {
        public string Name => "Orbit";
        public double Score(SelectionProfile s) =>
            !SpritesUsable(s) ? 0
            : s.Sprites.Count == 1 ? (s.Sprites[0].Aspect == AspectClass.Square ? 0.7 : 0.4) + (s.AllSpritesHaveAlpha ? 0.1 : 0)
            : 0.4;
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites, 0.2);
            scene.Movement.Type = MovementType.Circular;
            scene.Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 250, 350);
            scene.Movement.OrbitRadiusPixels = (float)(0.3 * s.ScreenHeight);
            scene.Movement.NodePhaseDelayMs = 500;
            scene.Animation.FaceTravelDirection = true;
            if (s.Sprites.Count == 1) // effects need a single image
            {
                double turnsPerSecond = SuggestKit.SnapCyclesPerSecond(s, 0.25);
                float sign = SuggestKit.Unit(seed, 5) < 0.5 ? -1 : 1;
                scene.Animation.Effects = new SpriteEffectsConfig { SpinDegPerSecond = sign * (float)(360 * turnsPerSecond) };
            }
            scene.Animation.ColorGrading = Tint(s, ColorGradingMode.Rainbow, 0.1, seed);
            return new(Name, "Circles the screen" + (s.Sprites.Count == 1 ? " and spins" : string.Empty) + LoopWhy(s), scene);
        }
    }

    private sealed class PulseGrid : ISceneRecipe
    {
        public string Name => "Pulse Grid";
        public double Score(SelectionProfile s)
        {
            if (!SpritesUsable(s) || s.Sprites.Any(f => f.Size == SizeClass.FullScreen)) return 0;
            double score = 0.4 + (s.Sprites.Count >= 2 ? 0.4 : 0) + (s.AllSpritesSmall ? 0.1 : 0);
            return s.AllSpritesHaveAlpha ? score : Math.Min(score, 0.3);
        }
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites, 0.1);
            scene.Animation.Pattern = new PatternConfig { Sizing = PatternConfig.SizingMode.Fill, SpacingX = 60, SpacingY = 60, Seed = seed };
            scene.Movement.Type = MovementType.Static;
            scene.Animation.ColorGrading = Tint(s, ColorGradingMode.Rainbow, 0.08, seed);
            string spread = s.Sprites.Count > 1 ? $"{s.Sprites.Count} files spread over a grid" : "A grid of copies";
            return new(Name, spread + ", " + MonoWhy(s, "rainbow colour wave") + LoopWhy(s), scene);
        }
    }

    private sealed class Fireflies : ISceneRecipe
    {
        public string Name => "Fireflies";
        public double Score(SelectionProfile s)
        {
            if (!SpritesUsable(s) || s.Sprites.Any(f => f.Size == SizeClass.FullScreen)) return 0;
            double score = (s.AllSpritesSmall ? 0.6 : 0.3) + (s.Sprites.Count >= 2 ? 0.2 : 0);
            return s.AllSpritesHaveAlpha ? score : Math.Min(score, 0.3);
        }
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var copies = Enumerable.Range(0, Math.Max(5, s.Sprites.Count)).Select(i => s.Sprites[i % s.Sprites.Count]).ToList();
            var scene = SuggestKit.NewScene(b, s, copies, 0.08);
            scene.Background.ColorHex = SuggestKit.BackgroundHex(s, forceDark: true);
            scene.Animation.MultiImageSpread = (float)(0.8 * s.ScreenWidth);
            scene.Animation.MultiImagePhaseJitterMs = 2500;
            scene.Movement.Type = MovementType.RandomWalk;
            scene.Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 150, 250);
            scene.Movement.RandomSeed = (int)(SuggestKit.Unit(seed, 9) * 100000);
            scene.Movement.RandomStepIntervalMs = 1800;
            scene.Movement.IterationStepCount = 0;
            scene.Animation.ColorGrading = s.AllMonochrome
                ? new ColorGradingConfig
                {
                    Mode = ColorGradingMode.RandomColors,
                    CyclesPerSecond = SuggestKit.SnapCyclesPerSecond(s, 0.3),
                    ColorList = new() { "#FFE066", "#FFB703", "#FB8500", "#C6FF6B", "#FFF3B0" },
                    Seed = (int)(SuggestKit.Unit(seed, 31) * 1000) + 1,
                }
                : new ColorGradingConfig { Mode = ColorGradingMode.None };
            return new(Name, "Drifting swarm on a dark background, " + MonoWhy(s, "glowing warm colours"), scene);
        }
    }

    private sealed class Comet : ISceneRecipe
    {
        public string Name => "Comet";
        public double Score(SelectionProfile s) =>
            !SpritesUsable(s) || s.Sprites.Count != 1 ? 0
            : !s.AllSpritesHaveAlpha ? 0.2
            : s.AllSpritesSmall ? 0.9 : 0.8;
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites.Take(1).ToList(), 0.12);
            scene.Background.ColorHex = SuggestKit.BackgroundHex(s, forceDark: true);
            scene.Movement.Type = MovementType.Linear;
            scene.Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 600, 900);
            scene.Movement.DirectionAngleDegrees = SuggestKit.Range(seed, 2, 10, 25);
            scene.Animation.ColorGrading = Tint(s, ColorGradingMode.Rainbow, 0.5, seed);
            scene.Animation.Effects = new SpriteEffectsConfig
            {
                Trail = new TrailConfig { Count = 8, SpacingMs = 70, StartOpacity = 0.55f, ScaleFalloff = 0.9f, UseGradingColor = s.AllMonochrome },
                Pulse = new PulseConfig { Amplitude = 0.08f, FrequencyHz = (float)SuggestKit.SnapCyclesPerSecond(s, 1.2) },
                Entry = BezelEffect.Ripple,
                Exit = BezelEffect.Glow,
                BezelColorHex = s.SharedPalette.Count > 0 ? s.SharedPalette[0] : "#7FDBFF",
                BezelDurationMs = 600,
            };
            return new(Name, "Shoots across with a trail" + (s.AllMonochrome ? " in rainbow colours" : string.Empty), scene);
        }
    }

    private sealed class Parade : ISceneRecipe
    {
        public string Name => "Parade";
        public double Score(SelectionProfile s) =>
            !SpritesUsable(s) ? 0
            : s.Mix switch
            {
                SelectionMix.Uniform => 0.8,
                SelectionMix.BigPlusSmall => s.Sprites.Count >= 2 ? 0.5 : 0.3,
                SelectionMix.Mixed => 0.4,
                _ => 0.3,
            };
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites, 0.18);
            scene.Animation.Pattern = new PatternConfig
            {
                Sizing = PatternConfig.SizingMode.Fill, SpacingX = 140, SpacingY = (float)(0.5 * s.ScreenHeight),
                RandomOffsetMaxPx = 30, Seed = seed,
            };
            scene.Movement.Type = MovementType.Linear;
            scene.Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 200, 260);
            scene.Movement.DirectionAngleDegrees = 0;
            scene.Movement.Reversed = SuggestKit.Unit(seed, 4) < 0.5;
            scene.Animation.ColorGrading = new ColorGradingConfig { Mode = ColorGradingMode.None };
            return new(Name, s.Sprites.Count > 1 ? $"{s.Sprites.Count} files march in a row" : "A row of copies marches by", scene);
        }
    }

    private sealed class Showcase : ISceneRecipe
    {
        public string Name => "Showcase";
        public double Score(SelectionProfile s) =>
            s.Big != null ? (s.Mix == SelectionMix.BigPlusSmall ? 0.9 : 1.0)
            : s.Count == 1 && s.Files[0].Kind == MediaKind.Video ? 0.8
            : 0;
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var file = s.Big ?? s.Files[0];
            var scene = SuggestKit.NewScene(b, s, new[] { file }, 1.0);
            scene.Background.ColorHex = "#000000";
            scene.Animation.FitMode = ContentFitMode.Fill;
            scene.Movement.Type = MovementType.Static;
            scene.Animation.ColorGrading = new ColorGradingConfig { Mode = ColorGradingMode.None };
            return new(Name, $"Shows {Path.GetFileName(file.Path)} full screen, as it is", scene);
        }
    }
}
```

- [ ] **Step 4: Write `SceneSuggester.cs`**

```csharp
namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>The cards for one Suggest / Shuffle press, plus an optional info line.</summary>
public sealed record SuggestionSet(IReadOnlyList<Suggestion> Cards, string? Note);

/// <summary>Picks which recipes become cards (spec 2026-10-14 §2.4). Pure in its inputs.</summary>
public static class SceneSuggester
{
    /// <summary>Cards per press.</summary>
    public const int CardCount = 4;

    /// <summary>Shown for a full-screen file picked together with smaller ones.</summary>
    public const string LayersNote = "big + small together needs scene layers";

    /// <summary>The best-scoring recipes (ties in <see cref="SceneRecipes.All"/> order).</summary>
    public static SuggestionSet Suggest(SelectionProfile s, CrossScreenConfig baseScene, int seed)
    {
        var picked = Offered(s).OrderByDescending(x => x.score).ThenBy(x => x.index).Take(CardCount).Select(x => x.recipe);
        return Build(s, baseScene, seed, picked);
    }

    /// <summary>
    /// Recipes drawn by score, preferring ones not in <paramref name="shownRecipeNames"/>; shown ones fill up when
    /// fewer than <see cref="CardCount"/> fresh ones exist. A new seed varies a repeated recipe.
    /// </summary>
    public static SuggestionSet Shuffle(SelectionProfile s, CrossScreenConfig baseScene, int seed, IReadOnlyCollection<string> shownRecipeNames)
    {
        var offered = Offered(s);
        var fresh = offered.Where(x => !shownRecipeNames.Contains(x.recipe.Name)).ToList();
        var picked = new List<ISceneRecipe>();
        for (int draw = 0; picked.Count < CardCount && fresh.Count > 0; draw++)
        {
            double total = fresh.Sum(x => x.score), target = SuggestKit.Unit(seed, 1000 + draw) * total;
            int at = 0;
            for (double run = fresh[0].score; run <= target && at < fresh.Count - 1; run += fresh[++at].score) { }
            picked.Add(fresh[at].recipe);
            fresh.RemoveAt(at);
        }
        picked.AddRange(offered.Where(x => !picked.Contains(x.recipe))
            .OrderByDescending(x => x.score).ThenBy(x => x.index)
            .Take(CardCount - picked.Count).Select(x => x.recipe));
        return Build(s, baseScene, seed, picked);
    }

    private static List<(ISceneRecipe recipe, double score, int index)> Offered(SelectionProfile s) =>
        SceneRecipes.All.Select((r, i) => (recipe: r, score: r.Score(s), index: i)).Where(x => x.score > 0).ToList();

    private static SuggestionSet Build(SelectionProfile s, CrossScreenConfig baseScene, int seed, IEnumerable<ISceneRecipe> recipes) =>
        new(recipes.Select((r, i) => r.Build(s, baseScene, PatternLayout.Hash3(seed, i, 7))).ToList(),
            s.Mix == SelectionMix.BigPlusSmall ? LayersNote : null);
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SceneRecipeTests|FullyQualifiedName~SceneSuggesterTests"`
Expected: PASS. If `OfferedScenesHaveNoWarningsAndKeepTheTargets` fails on a warning, the message names it — fix the recipe (do not loosen the test): e.g. "Endless needs a Traveling color mode" means `Endless` was set without a Traveling mode.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test WaBiBaBuSy.Tests`
Expected: PASS, no regressions.

- [ ] **Step 7: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/Suggest/SceneRecipes.cs WaBiBaBuSy.Models/Wallpaper/Suggest/SceneSuggester.cs WaBiBaBuSy.Tests/SceneRecipeTests.cs WaBiBaBuSy.Tests/SceneSuggesterTests.cs
git commit -m "feat(suggest): nine scene recipes modelled on the starter presets, suggest / shuffle picker"
```

---

### Task 5: UI — decode, Suggestions view model, Suggest button and card strip

**Files:**
- Create: `WaBiBaBuSy.UI/Services/MediaProbe.cs`
- Create: `WaBiBaBuSy.UI/ViewModels/SuggestionsViewModel.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.SceneEditor.cs` (property, init, profile builder, `PickGalleryItem` at ~line 219)
- Modify: `WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml` (styles at ~line 21, strip in the row-0 `StackPanel` at ~line 108, Suggest button in the Content tab after the File row at ~line 145)

**Interfaces:**
- Consumes: `SelectionProfile`, `MediaProfile`, `PixelFacts`, `MediaAnalyzer`, `SceneSuggester`, `SuggestionSet`, `Suggestion` (Tasks 1–4); `SceneFile.Clone`; `LocalMonitors.Snapshot()`; `SceneEditor.BuildConfig()` / `LoadFromConfig(CrossScreenConfig)`; `Wallpapers` (`ObservableCollection<WallpaperItemViewModel>`, fields `FilePath`, `Type`, `ThumbnailPath`, `Resolution`, `DurationMs`).
- Produces: `MainWindowViewModel.Suggestions` (`SuggestionsViewModel`) with commands `OpenCommand`, `ShuffleCommand`, `ApplyCommand(SuggestionCardViewModel)`, `KeepCommand`, `RevertCommand`, method `Discard()`; properties `IsOpen`, `Title`, `Note`, `HasNote`, `CanKeep`, `Cards`.

- [ ] **Step 1: Write `Services/MediaProbe.cs`**

```csharp
using ImageMagick;
using WaBiBaBuSy.Models.Wallpaper.Suggest;

namespace WaBiBaBuSy.UI.Services;

/// <summary>Decodes a file's first frame to a 64 × 64 BGRA thumbnail for <see cref="MediaAnalyzer"/>.</summary>
public static class MediaProbe
{
    private const uint ThumbSize = 64;

    /// <summary>
    /// The file's original size and its pixel facts. Any decode failure gives (0, 0, <see cref="PixelFacts.Unknown"/>):
    /// suggestions still work, just without colour facts.
    /// </summary>
    public static (int Width, int Height, PixelFacts Pixels) Read(string imagePath)
    {
        try
        {
            using var image = new MagickImage(imagePath, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
            int width = (int)image.Width, height = (int)image.Height;
            if (!image.HasAlpha) image.Alpha(AlphaOption.Set);
            image.Resize(ThumbSize, ThumbSize);
            var bgra = image.GetPixels().ToByteArray(PixelMapping.BGRA);
            return bgra == null
                ? (width, height, PixelFacts.Unknown)
                : (width, height, MediaAnalyzer.Analyze(bgra, (int)image.Width, (int)image.Height));
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, 0, PixelFacts.Unknown);
        }
    }
}
```

- [ ] **Step 2: Write `ViewModels/SuggestionsViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Models.Wallpaper.Suggest;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>One suggestion card in the strip.</summary>
public partial class SuggestionCardViewModel : ObservableObject
{
    public SuggestionCardViewModel(Suggestion suggestion) => Suggestion = suggestion;

    /// <summary>The built suggestion.</summary>
    public Suggestion Suggestion { get; }
    /// <summary>Card title.</summary>
    public string Name => Suggestion.RecipeName;
    /// <summary>Why it was suggested.</summary>
    public string Why => Suggestion.Why;

    /// <summary>True for the card that is the draft right now.</summary>
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// The Suggest strip (spec 2026-10-14 §3): saves the draft when it opens, loads a clicked card as the draft,
/// Shuffle replaces the cards, Keep closes, Revert restores the saved draft. Nothing here sends to machines.
/// </summary>
public partial class SuggestionsViewModel : ObservableObject
{
    private readonly Func<CrossScreenConfig> _captureDraft;
    private readonly Action<CrossScreenConfig> _loadDraft;
    private readonly Func<Task<SelectionProfile?>> _profileSelection;
    private CrossScreenConfig? _savedDraft;
    private SelectionProfile? _selection;
    private int _seed;

    public SuggestionsViewModel(Func<CrossScreenConfig> captureDraft, Action<CrossScreenConfig> loadDraft,
        Func<Task<SelectionProfile?>> profileSelection)
    {
        _captureDraft = captureDraft;
        _loadDraft = loadDraft;
        _profileSelection = profileSelection;
    }

    /// <summary>The cards on show.</summary>
    public ObservableCollection<SuggestionCardViewModel> Cards { get; } = new();

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNote))] private string? _note;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(KeepCommand))] private bool _canKeep;

    /// <summary>True when the info line has text.</summary>
    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>Profile the picked media and show the best four cards.</summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        var selection = await _profileSelection();
        if (selection == null) return;
        _selection = selection;
        _savedDraft = _captureDraft();
        _seed = Environment.TickCount; // UI-only: the built scenes carry their own seeds, so they stay deterministic
        Title = "✨ Suggestions for: " + selection.Describe();
        Show(SceneSuggester.Suggest(selection, _savedDraft, _seed));
        IsOpen = true;
    }

    /// <summary>Four more cards, preferring recipes not on show; the draft is untouched.</summary>
    [RelayCommand]
    private void Shuffle()
    {
        if (_selection == null || _savedDraft == null) return;
        _seed = PatternLayout.Hash3(_seed, 1, 0);
        var shown = Cards.Select(c => c.Name).ToList();
        Show(SceneSuggester.Shuffle(_selection, _savedDraft, _seed, shown));
    }

    /// <summary>Make the card the draft (the room view previews it).</summary>
    [RelayCommand]
    private void Apply(SuggestionCardViewModel? card)
    {
        if (card == null) return;
        foreach (var c in Cards) c.IsSelected = c == card;
        _loadDraft(SceneFile.Clone(card.Suggestion.Scene));
        CanKeep = true;
    }

    /// <summary>Close the strip, keeping the clicked card as the draft.</summary>
    [RelayCommand(CanExecute = nameof(CanKeep))]
    private void Keep() => Close();

    /// <summary>Close the strip and restore the draft from before it opened.</summary>
    [RelayCommand]
    private void Revert()
    {
        if (_savedDraft != null) _loadDraft(_savedDraft);
        Close();
    }

    /// <summary>Close without touching the draft (the user picked other media).</summary>
    public void Discard()
    {
        if (IsOpen) Close();
    }

    private void Show(SuggestionSet set)
    {
        Cards.Clear();
        foreach (var s in set.Cards) Cards.Add(new SuggestionCardViewModel(s));
        Note = set.Note;
    }

    private void Close()
    {
        IsOpen = false;
        CanKeep = false;
        Cards.Clear();
        _savedDraft = null;
        _selection = null;
    }
}
```

- [ ] **Step 3: Wire it into `MainWindowViewModel.SceneEditor.cs`**

Add the property next to `SceneEditor` (line 31):

```csharp
    /// <summary>The ✨ Suggest strip over the Scene editor (spec 2026-10-14).</summary>
    public SuggestionsViewModel Suggestions { get; private set; } = null!;
```

At the start of `InitSceneEditor()` (line 127):

```csharp
        Suggestions = new SuggestionsViewModel(() => SceneEditor.BuildConfig(), c => SceneEditor.LoadFromConfig(c),
            BuildSelectionProfileAsync);
```

In `PickGalleryItem` (line ~219), as its first line:

```csharp
        Suggestions.Discard();
```

Add the profile builder to the same partial class:

```csharp
    /// <summary>
    /// Profile the draft's animation files for the Suggest strip: gallery facts (type, duration, video resolution)
    /// on the UI thread, then the first-frame decode off it. Null when no file is picked.
    /// </summary>
    private async Task<SelectionProfile?> BuildSelectionProfileAsync()
    {
        var paths = new[] { SceneEditor.AnimationPath }.Concat(SceneEditor.AdditionalAnimationPaths)
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0) return null;
        var items = paths.Select(p => (path: p, item: Wallpapers.FirstOrDefault(w =>
            string.Equals(w.FilePath, p, StringComparison.OrdinalIgnoreCase)))).ToList();

        var primary = LocalMonitors.Snapshot().Monitors.FirstOrDefault(m => m.IsPrimary);
        int screenW = primary?.Width ?? 1920, screenH = primary?.Height ?? 1080;

        var files = await Task.Run(() => items.Select(x =>
        {
            bool video = x.item?.Type == WallpaperType.Video;
            var decodePath = video && !string.IsNullOrEmpty(x.item!.ThumbnailPath) ? x.item.ThumbnailPath : x.path;
            var (w, h, pixels) = MediaProbe.Read(decodePath);
            if (video && TryParseResolution(x.item!.Resolution, out int vw, out int vh)) (w, h) = (vw, vh);
            return MediaProfile.Create(x.path, w, h, screenW, screenH, pixels, x.item?.DurationMs ?? 0);
        }).ToList());

        return new SelectionProfile(files, screenW, screenH);
    }

    private static bool TryParseResolution(string? resolution, out int width, out int height)
    {
        width = height = 0;
        var parts = (resolution ?? string.Empty).Split('x', 'X', '×');
        return parts.Length == 2 && int.TryParse(parts[0].Trim(), out width) && int.TryParse(parts[1].Trim(), out height);
    }
```

Add `using WaBiBaBuSy.Models.Wallpaper.Suggest;` and `using WaBiBaBuSy.UI.Services;` at the top if missing.

- [ ] **Step 4: Build to check the wiring**

Run: `dotnet build WaBiBaBuSy.UI`
Expected: 0 errors. If `MagickReadSettings.FrameIndex` / `FrameCount` or `PixelMapping` differ in Magick.NET 14.17, check `WaBiBaBuSy.WallpaperEngine/Services/BackgroundColorDetector.cs:47` (it reads frame 0 via `MagickImageCollection`) and copy that pattern: `using var frames = new MagickImageCollection(path); var image = (MagickImage)frames[0].Clone();`.

- [ ] **Step 5: Add the markup to `SceneEditorPanel.axaml`**

Styles, inside `<UserControl.Styles>` after the `Border.strip.selected` style:

```xml
        <Style Selector="Button.card">
            <Setter Property="Width" Value="170"/>
            <Setter Property="MinHeight" Value="64"/>
            <Setter Property="Padding" Value="8,6"/>
            <Setter Property="Margin" Value="0,0,6,0"/>
            <Setter Property="VerticalContentAlignment" Value="Top"/>
            <Setter Property="HorizontalContentAlignment" Value="Left"/>
        </Style>
        <Style Selector="Button.card.selected">
            <Setter Property="BorderBrush" Value="#0A84FF"/>
            <Setter Property="BorderThickness" Value="2"/>
        </Style>
```

The strip, as the first child of the row-0 `StackPanel` ("Picking hint + validation chips"):

```xml
            <Border Background="#252528" BorderBrush="#3E3E42" BorderThickness="1" CornerRadius="4" Padding="8,6"
                    IsVisible="{Binding Suggestions.IsOpen}">
                <StackPanel Spacing="6">
                    <Grid ColumnDefinitions="*,Auto">
                        <TextBlock Text="{Binding Suggestions.Title}" FontWeight="SemiBold" VerticalAlignment="Center"
                                   TextTrimming="CharacterEllipsis"/>
                        <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="4">
                            <Button Content="🎲 Shuffle" Command="{Binding Suggestions.ShuffleCommand}"
                                    ToolTip.Tip="Four other suggestions (the draft stays until you click one)"/>
                            <Button Content="Keep" Command="{Binding Suggestions.KeepCommand}"
                                    ToolTip.Tip="Close and keep the clicked suggestion as the draft"/>
                            <Button Content="✕" Command="{Binding Suggestions.RevertCommand}"
                                    ToolTip.Tip="Close and go back to the scene from before Suggest"/>
                        </StackPanel>
                    </Grid>
                    <ItemsControl ItemsSource="{Binding Suggestions.Cards}">
                        <ItemsControl.ItemsPanel>
                            <ItemsPanelTemplate>
                                <WrapPanel/>
                            </ItemsPanelTemplate>
                        </ItemsControl.ItemsPanel>
                        <ItemsControl.ItemTemplate>
                            <DataTemplate x:DataType="vm:SuggestionCardViewModel">
                                <Button Classes="card" Classes.selected="{Binding IsSelected}"
                                        Command="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).Suggestions.ApplyCommand}"
                                        CommandParameter="{Binding}">
                                    <StackPanel Spacing="2">
                                        <TextBlock Text="{Binding Name}" FontWeight="SemiBold"/>
                                        <TextBlock Text="{Binding Why}" FontSize="11" Foreground="#BBBBBB" TextWrapping="Wrap"/>
                                    </StackPanel>
                                </Button>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                    <TextBlock Text="{Binding Suggestions.Note, StringFormat='ⓘ {0}'}" FontSize="11" Foreground="#9FC5FF"
                               IsVisible="{Binding Suggestions.HasNote}"/>
                </StackPanel>
            </Border>
```

The Suggest button, in the Content tab directly after the `File ⓘ` `Grid` (the tab's DataContext is the editor, so it reaches the window view model through the `UserControl`):

```xml
                            <Grid ColumnDefinitions="120,*">
                                <Button Grid.Column="1" Content="✨ Suggest" HorizontalAlignment="Left"
                                        IsEnabled="{Binding HasAnimationFile}"
                                        Command="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).Suggestions.OpenCommand}"
                                        ToolTip.Tip="Four scenes built for the picked media — click one to try it, Shuffle for more"/>
                            </Grid>
```

`HasAnimationFile` does not exist yet: add it to `CrossScreenConfigViewModel` next to `AnimationFileName`, and raise it when `AnimationPath` changes:

```csharp
    /// <summary>True when an animation file is picked (enables ✨ Suggest).</summary>
    public bool HasAnimationFile => !string.IsNullOrWhiteSpace(AnimationPath);
```

with `[NotifyPropertyChangedFor(nameof(HasAnimationFile))]` added to the `_animationPath` field's attributes (find it with `grep -n "_animationPath" WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs`). If `_animationPath` already has an `OnAnimationPathChanged` partial, raise it there instead with `OnPropertyChanged(nameof(HasAnimationFile));`.

- [ ] **Step 6: Build and run the tests**

Run: `dotnet build && dotnet test WaBiBaBuSy.Tests`
Expected: 0 errors, all tests PASS.

- [ ] **Step 7: Manual check (report what you saw, step by step)**

Run: `dotnet run --project WaBiBaBuSy.UI`, open the Server Control Panel.
1. Pick `TestScenarios/showcase/assets/bibabu-logo.png` (add it to the gallery if missing) → **✨ Suggest** → 4 cards, DVD Bounce among them; the title names the file.
2. Click each card → the room view tiles change to it; the Motion / Look tabs show its values; the clicked card is outlined.
3. **🎲 Shuffle** → different cards, the draft does not change until a card is clicked.
4. **✕** → the scene from before Suggest is back.
5. Suggest again, click a card, **Keep** → strip closes, the card's scene stays.
6. Suggest, click a card, then click another gallery thumbnail → strip closes, the new file is the draft's file (Review Focus 2).
7. Ctrl+click two more images → Suggest → Pulse Grid among the cards.
8. A large GIF/video plus a small image → Showcase card and the "needs scene layers" line.
9. Play with a card applied → the machines show it (unchanged Play path).

- [ ] **Step 8: Commit**

```bash
git add WaBiBaBuSy.UI/Services/MediaProbe.cs WaBiBaBuSy.UI/ViewModels/SuggestionsViewModel.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.SceneEditor.cs WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml
git commit -m "feat(suggest): Suggest button and card strip in the Scene editor - preview by click, shuffle, keep / revert"
```

---

### Task 6: Docs

**Files:**
- Modify: `docs/superpowers/specs/2026-10-14-auto-suggest-design.md` (fold in "Spec adjustments found while planning", status → Implemented)
- Modify: `CLAUDE.md` (Key Capabilities: one bullet), `.docs/RECENT_UPDATES.md` (one entry), `.docs/UI_ARCHITECTURE.md` (Scene editor: the Suggest strip, one paragraph)

- [ ] **Step 1: Update the spec** — replace each affected sentence with the adjusted rule from this plan's "Spec adjustments" list; set `**Status:** Implemented 2026-10-14 (UI manual check: <result>)`.

- [ ] **Step 2: Add to `CLAUDE.md` Key Capabilities**, after the Scene presets bullet:

```markdown
- Auto-suggest: ✨ Suggest in the Scene editor profiles the picked media (size vs monitor, aspect, transparency, mono vs colourful, dominant colours, GIF loop length) and offers four recipe-built scenes as cards (`WaBiBaBuSy.Models/Wallpaper/Suggest/`); click previews in the room view, Shuffle, Keep / ✕ revert
```

- [ ] **Step 3: Add a `RECENT_UPDATES.md` entry and the `UI_ARCHITECTURE.md` paragraph** in the files' existing style (read the top entry first and match it).

- [ ] **Step 4: Refresh the graph and commit**

```bash
./tools/graphify-update.ps1
git add docs/superpowers/specs/2026-10-14-auto-suggest-design.md CLAUDE.md .docs/RECENT_UPDATES.md .docs/UI_ARCHITECTURE.md graphify-out/GRAPH_REPORT.md
git commit -m "docs: auto-suggest - spec adjustments, capabilities, changelog, UI architecture"
```

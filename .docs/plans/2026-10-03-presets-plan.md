# Scene Presets, Import/Export, Tray Quick Start, Starter Presets — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Save / load / import / export scene presets, add presets to playlists, start a preset from the tray on all seats with the window closed, and seed curated starter presets on first run (roadmap 2.2, rollout step 4).

**Architecture:** A preset is a scene file (`SceneFile` format). `PresetBundle` (Models) copies assets next to a preset and rewrites paths relative; `PresetStore` + `PresetSeeder` (Core) own the folder and first-run seeding. A pure `RoomNodeSources` (Core) builds the room from local monitors + server clients so `ServerPlaybackHost` can play without a window; the window uses the same builder. UI: editor footer, playlist "＋ Add ▾", tray "Quick start ▸".

**Tech Stack:** .NET 9 (net9.0-windows), C# 12, System.Text.Json, Avalonia 12.1.2, CommunityToolkit.Mvvm 8.4, xUnit 2.9.

**Spec:** [`2026-10-03-presets-design.md`](2026-10-03-presets-design.md) (read it first). One deviation, recorded in Task 4: the UI csproj **already** copies `TestScenarios\**` next to the exe (`<None Include="..\TestScenarios\**\*" LinkBase="TestScenarios" …>`), so the starter source is `<AppBase>\TestScenarios\showcase\` — no new `Presets\` link items.

## Global Constraints

- Trunk-based: commit on `main`; every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Stage only the task's files (never `graphify-out/GRAPH_REPORT.md` unless the docs task says so).
- Before each commit: `dotnet build WaBiBaBuSy.sln` → 0 errors and `dotnet test WaBiBaBuSy.Tests` → all green.
- **Never launch the app** (it plays on the user's monitors). UI tasks are build-verified; behaviour goes into the manual checklist.
- C# 12, file-scoped namespaces, nullable on, XML docs on public APIs, async/await for file I/O, match surrounding comment density.
- Tests reference Core + Models only; everything testable lives there. xUnit `[Fact]`, temp dirs under `Path.GetTempPath()` cleaned in `Dispose`.
- Preset folder `%APPDATA%\WaBiBaBuSy\presets\`; assets in `presets\assets\`; preset paths written as `assets/<file>` (forward slashes).
- `SelectedMonitorIds` is empty in every stored / exported preset.
- Do not touch the deterministic rendering paths (Player.D2D, MovementCalculator, ColorGrader, PatternLayout).

## Review Focus

1. **Hostile preset names** ("CON", "a/b:c", "  ", "../x") must stay a file inside the presets folder and not throw — pinned in Task 3 (`Sanitize_*`, `PathFor_StaysInsideDirectory`).
2. **Importing the same file twice** must give "x (2)" and reuse the asset, not duplicate it — Task 3 (`Import_Twice_UniqueNameAndAssetsReused`).
3. **Load → edit → Save of an imported preset** must keep `assets/…` relative (not freeze absolute AppData paths) — Task 3 (`Save_PathUnderStore_WrittenRelative`).
4. **A corrupt / hand-edited preset** in the folder must surface as `ScenarioException` (UI shows it), never crash Quick start or the editor — Task 3 (`Load_Corrupt_ThrowsScenarioException`), UI catch in Tasks 7–8.
5. **Starter source missing** (dev build without TestScenarios, trimmed install) must seed nothing and not throw — Task 4 (`SeedIfEmpty_MissingStarterDir_ReturnsZero`).

---

## File Structure

| File | Responsibility |
|---|---|
| `WaBiBaBuSy.Models/Testing/SceneFile.cs` (modify) | + `ToJson`, `Clone` (scene write / deep copy) |
| `WaBiBaBuSy.Models/Content/PresetBundle.cs` (new) | copy a scene's assets into `<dir>\assets\`, rewrite paths; relativize paths under a folder |
| `WaBiBaBuSy.Core/Services/Animation/PresetStore.cs` (new) | presets folder: list, load, save, import, export, delete, unique names, `Changed` |
| `WaBiBaBuSy.Core/Services/Animation/PresetSeeder.cs` (new) | first-run import of the starter manifest |
| `TestScenarios/showcase/starter-presets.json` (new) | starter names → scene files |
| `TestScenarios/showcase/scenes/14-ring-cat.json`, `15-party-colors.json` (new) | the two missing design presets |
| `WaBiBaBuSy.Models/Topology/LocalMonitor.cs` (new) | `LocalMonitor`, `LocalScreens` records |
| `WaBiBaBuSy.Core/Services/RoomNodeSources.cs` (new) | pure room build: server / standalone node lists, `MonitorFor`, `ToRoomNode` |
| `WaBiBaBuSy.UI/Services/LocalMonitors.cs` (new) | WinForms + native enumeration → `LocalScreens`; reference DPI |
| `WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs` (modify) | session gaps, room owner, `EnsureRoom`, `QuickStartAsync` |
| `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (modify) | use builder + `LocalMonitors`; gaps via host; room owner; preset store ctor param |
| `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.Presets.cs` (new) | editor preset commands + menu entries |
| `WaBiBaBuSy.UI/ViewModels/PresetMenuEntry.cs` (new) | `(Name, Command)` row for menus |
| `WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs` (modify) | preset entries, `AddItem(config, name)` |
| `WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs`, `WaBiBaBuSy.UI/App.axaml.cs` (modify) | store, seeding, Quick start menu |
| `WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml`, `PlaylistPanel.axaml` (modify) | buttons / flyouts |
| Tests: `SceneFileWriteTests.cs`, `PresetBundleTests.cs`, `PresetStoreTests.cs`, `PresetSeederTests.cs`, `RoomNodeSourcesTests.cs` (new) | |

---

### Task 1: `SceneFile.ToJson` + `SceneFile.Clone`

**Files:**
- Modify: `WaBiBaBuSy.Models/Testing/SceneFile.cs`
- Test: `WaBiBaBuSy.Tests/SceneFileWriteTests.cs`

**Interfaces:**
- Produces: `public static string SceneFile.ToJson(CrossScreenConfig config)` (indented, PascalCase, enum names, non-ASCII unescaped); `public static CrossScreenConfig SceneFile.Clone(CrossScreenConfig config)` (deep copy via JSON).

- [ ] **Step 1: Write the failing tests**

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~SceneFileWriteTests`
Expected: build error — `SceneFile` has no `ToJson` / `Clone`.

- [ ] **Step 3: Implement** (add to `SceneFile`, plus `using System.Text.Encodings.Web;`)

```csharp
    private static readonly JsonSerializerOptions WriteOptions = new(JsonOptions)
    {
        WriteIndented = true,
        // Paths with umlauts stay readable in hand-edited preset files.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Scene JSON as <see cref="Load"/> reads it (PascalCase, enum names, indented).</summary>
    public static string ToJson(CrossScreenConfig config) => JsonSerializer.Serialize(config, WriteOptions);

    /// <summary>Deep copy through the scene JSON.</summary>
    public static CrossScreenConfig Clone(CrossScreenConfig config) =>
        JsonSerializer.Deserialize<CrossScreenConfig>(JsonSerializer.Serialize(config, JsonOptions), JsonOptions)!;
```

- [ ] **Step 4: Run to verify it passes** — same command, expected PASS (3 tests). If `Clone_IsDeepAndEqual` fails because a property does not round-trip, report it (do not paper over it): it would also break presets.

- [ ] **Step 5: Full build + test, commit**

```bash
dotnet build WaBiBaBuSy.sln && dotnet test WaBiBaBuSy.Tests
git add WaBiBaBuSy.Models/Testing/SceneFile.cs WaBiBaBuSy.Tests/SceneFileWriteTests.cs
git commit -m "feat(presets): write and clone scene files

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `PresetBundle` — copy assets next to a scene

**Files:**
- Create: `WaBiBaBuSy.Models/Content/PresetBundle.cs`
- Test: `WaBiBaBuSy.Tests/PresetBundleTests.cs`

**Interfaces:**
- Consumes: `SceneAssets.CollectPaths(CrossScreenConfig)` (Models/Content/ContentAssetRef.cs), `ContentIdentity.ComputeId(string)` / `HasHash(string)` / `HashPrefixLength`.
- Produces:
  - `public const string PresetBundle.AssetsFolder = "assets";`
  - `public static Task PresetBundle.BundleAssetsAsync(CrossScreenConfig config, string targetDir)` — mutates `config`; throws `FileNotFoundException` (message names the path) for a missing or non-absolute asset.
  - `public static void PresetBundle.RelativizeUnder(CrossScreenConfig config, string baseDir)` — mutates `config`.

- [ ] **Step 1: Write the failing tests**

```csharp
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

    private string File_(string dir, string name, byte fill)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, Enumerable.Repeat(fill, 64).ToArray());
        return p;
    }

    private CrossScreenConfig Scene(string primary, string? extra = null, string? background = null) => new()
    {
        Animation = new AnimationLayerConfig
        {
            AnimationPath = primary,
            AdditionalAnimationPaths = extra == null ? new List<string>() : new List<string> { primary, extra },
        },
        Background = background == null
            ? new BackgroundLayerConfig()
            : new BackgroundLayerConfig { Mode = BackgroundMode.StretchedImage, ImagePath = background },
    };

    [Fact]
    public async Task Bundle_CopiesEveryAssetAndRewritesRelative()
    {
        var logo = File_(Src, "logo.png", 1);
        var b = File_(Src, "b.png", 2);
        var bg = File_(Src, "bg.jpg", 3);
        var config = Scene(logo, b, bg);

        await PresetBundle.BundleAssetsAsync(config, Out);

        Assert.Equal("assets/logo.png", config.Animation.AnimationPath);
        Assert.Equal(new[] { "assets/logo.png", "assets/b.png" }, config.Animation.AdditionalAnimationPaths);
        Assert.Equal("assets/bg.jpg", config.Background.ImagePath);
        Assert.True(File.Exists(Path.Combine(Out, "assets", "logo.png")));
        Assert.True(File.Exists(Path.Combine(Out, "assets", "b.png")));
        Assert.True(File.Exists(Path.Combine(Out, "assets", "bg.jpg")));
        Assert.True(File.Exists(logo));   // copied, not moved
    }

    [Fact]
    public async Task Bundle_SameNameSameContent_ReusesFile()
    {
        var logo = File_(Src, "logo.png", 1);
        File_(Path.Combine(Out, "assets"), "logo.png", 1);
        var config = Scene(logo);

        await PresetBundle.BundleAssetsAsync(config, Out);

        Assert.Equal("assets/logo.png", config.Animation.AnimationPath);
        Assert.Single(Directory.GetFiles(Path.Combine(Out, "assets")));
    }

    [Fact]
    public async Task Bundle_SameNameDifferentContent_GetsHashSuffix()
    {
        var logo = File_(Src, "logo.png", 1);
        var existing = File_(Path.Combine(Out, "assets"), "logo.png", 9);
        var config = Scene(logo);

        await PresetBundle.BundleAssetsAsync(config, Out);

        Assert.Matches(new Regex("^assets/logo-[0-9a-f]{8}\\.png$"), config.Animation.AnimationPath);
        Assert.Equal(9, File.ReadAllBytes(existing)[0]);   // the other preset's file is untouched
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Out, "assets")).Length);

        // Bundling the same file again reuses the suffixed copy.
        var again = Scene(logo);
        await PresetBundle.BundleAssetsAsync(again, Out);
        Assert.Equal(config.Animation.AnimationPath, again.Animation.AnimationPath);
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

        Assert.Equal("assets/logo.png", config.Animation.AnimationPath);
        Assert.Equal(new[] { "assets/logo.png", outside }, config.Animation.AdditionalAnimationPaths);
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~PresetBundleTests` → build error (`PresetBundle` missing).

- [ ] **Step 3: Implement** `WaBiBaBuSy.Models/Content/PresetBundle.cs`

```csharp
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Content;

/// <summary>
/// Makes a scene self-contained in a folder: every asset is copied into <c>&lt;folder&gt;\assets\</c> and the
/// scene references it as <c>assets/&lt;file&gt;</c>, which <see cref="Testing.SceneFile.Load"/> resolves against
/// the scene file's folder. Used for preset import, starter seeding and export.
/// </summary>
public static class PresetBundle
{
    /// <summary>Asset sub-folder next to the scene files.</summary>
    public const string AssetsFolder = "assets";

    /// <summary>
    /// Copy every asset of <paramref name="config"/> (absolute paths) into <c>targetDir\assets\</c> and rewrite the
    /// paths to <c>assets/&lt;file&gt;</c>. A file with the same name and content is reused; a different file with
    /// the same name is stored as <c>&lt;stem&gt;-&lt;hash8&gt;&lt;ext&gt;</c>. Throws <see cref="FileNotFoundException"/>
    /// for a missing or relative asset path (nothing is rewritten then).
    /// </summary>
    public static async Task BundleAssetsAsync(CrossScreenConfig config, string targetDir)
    {
        var assets = SceneAssets.CollectPaths(config);
        foreach (var (path, _) in assets)
            if (!Path.IsPathRooted(path) || !File.Exists(path))
                throw new FileNotFoundException($"asset not found: {path}", path);

        var assetsDir = Path.Combine(targetDir, AssetsFolder);
        Directory.CreateDirectory(assetsDir);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _) in assets)
            map[path] = $"{AssetsFolder}/{await PlaceAsync(path, assetsDir)}";

        Rewrite(config, p => map.TryGetValue(p, out var relative) ? relative : p);
    }

    /// <summary>Rewrite absolute paths inside <paramref name="baseDir"/> as forward-slash paths relative to it.</summary>
    public static void RelativizeUnder(CrossScreenConfig config, string baseDir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir)) + Path.DirectorySeparatorChar;
        Rewrite(config, p =>
            Path.IsPathRooted(p) && Path.GetFullPath(p).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(root, p).Replace('\\', '/')
                : p);
    }

    /// <summary>File name the asset gets in <paramref name="assetsDir"/>; copies it unless an identical file is there.</summary>
    private static async Task<string> PlaceAsync(string source, string assetsDir)
    {
        var name = Path.GetFileName(source);
        var target = Path.Combine(assetsDir, name);
        if (!File.Exists(target))
        {
            await CopyAsync(source, target);
            return name;
        }

        var hash = Hash(source);
        if (Hash(target) == hash) return name;

        var hashed = $"{Path.GetFileNameWithoutExtension(name)}-{hash[..8]}{Path.GetExtension(name)}";
        var hashedTarget = Path.Combine(assetsDir, hashed);
        if (!File.Exists(hashedTarget)) await CopyAsync(source, hashedTarget);
        return hashed;
    }

    private static string Hash(string path)
    {
        var id = ContentIdentity.ComputeId(path);
        if (!ContentIdentity.HasHash(id)) throw new IOException($"cannot read asset: {path}");
        return id[^ContentIdentity.HashPrefixLength..];
    }

    private static async Task CopyAsync(string source, string target)
    {
        try
        {
            await using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await from.CopyToAsync(to);
        }
        catch
        {
            // Never leave a half-written asset that a later bundle would take for a complete one.
            try { if (File.Exists(target)) File.Delete(target); } catch { /* best effort */ }
            throw;
        }
    }

    private static void Rewrite(CrossScreenConfig config, Func<string, string> map)
    {
        string One(string p) => string.IsNullOrWhiteSpace(p) ? p : map(p);
        config.Animation.AnimationPath = One(config.Animation.AnimationPath);
        config.Animation.AdditionalAnimationPaths = config.Animation.AdditionalAnimationPaths.Select(One).ToList();
        if (!string.IsNullOrEmpty(config.Background.ImagePath))
            config.Background.ImagePath = One(config.Background.ImagePath);
    }
}
```

- [ ] **Step 4: Run to verify it passes** — expected PASS (6 tests).
- [ ] **Step 5: Full build + test, commit** — `git add WaBiBaBuSy.Models/Content/PresetBundle.cs WaBiBaBuSy.Tests/PresetBundleTests.cs`, message `feat(presets): bundle a scene's assets next to it`.

---

### Task 3: `PresetStore`

**Files:**
- Create: `WaBiBaBuSy.Core/Services/Animation/PresetStore.cs`
- Test: `WaBiBaBuSy.Tests/PresetStoreTests.cs`

**Interfaces:**
- Consumes: `SceneFile.Load / ToJson / Clone` (Task 1), `PresetBundle` (Task 2), `ScenarioException` (Models/Testing).
- Produces (`namespace WaBiBaBuSy.Core.Services.Animation; public sealed class PresetStore`):
  - `PresetStore(string? directoryOverride = null)`, `string Directory { get; }`, `event EventHandler? Changed`
  - `static string Sanitize(string name)`, `string PathFor(string name)`, `bool Exists(string name)`, `string UniqueName(string name)`
  - `IReadOnlyList<string> List()` (names, alphabetical, case-insensitive)
  - `Task<CrossScreenConfig> LoadAsync(string name)` (throws `ScenarioException`)
  - `Task<string> SaveAsync(string name, CrossScreenConfig config)` → saved name
  - `Task<string> ImportAsync(string sourceFile, string? name = null)` → saved name (throws `ScenarioException`, `IOException`)
  - `static Task ExportAsync(CrossScreenConfig config, string targetFile)` (throws `IOException`)
  - `void Delete(string name)`

- [ ] **Step 1: Write the failing tests**

```csharp
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

    /// <summary>A showcase-style scene: comments, enum names, ../assets path.</summary>
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
        Animation = new AnimationLayerConfig { AnimationPath = path },
        Movement = new MovementConfig { Type = MovementType.Linear },
        SelectedMonitorIds = new List<string> { "N1", "N2" },
    };

    [Fact]
    public async Task Save_Load_Roundtrip_StripsTargets_KeepsGalleryPath()
    {
        var logo = Asset("gallery/logo.png");
        var saved = await _store.SaveAsync("Comet", Scene(logo));

        Assert.Equal("Comet", saved);
        var back = await _store.LoadAsync("Comet");
        Assert.Equal(logo, back.Animation.AnimationPath);
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
        Assert.Equal(Path.Combine(_store.Directory, "assets", "logo.png"), back.Animation.AnimationPath);
        Assert.Empty(back.SelectedMonitorIds);
        Assert.Equal(900, back.Movement.SpeedPixelsPerSecond);
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
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~PresetStoreTests` → build error.

- [ ] **Step 3: Implement** `WaBiBaBuSy.Core/Services/Animation/PresetStore.cs`

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>
/// Scene presets under %APPDATA%\WaBiBaBuSy\presets\, one <see cref="SceneFile"/> per preset: a showcase scene is
/// a valid preset and an exported preset a valid scene. Targets are never stored (they belong to the room).
/// Saved presets reference gallery files in place; imported ones carry their assets in <c>presets\assets\</c>,
/// referenced relative to the preset, so the folder can be copied to another server.
/// </summary>
public sealed class PresetStore
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Directory holding the preset files (created on demand).</summary>
    public string Directory { get; }

    /// <summary>A preset was saved, imported or deleted. Raised on the calling thread.</summary>
    public event EventHandler? Changed;

    public PresetStore(string? directoryOverride = null)
    {
        Directory = directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WaBiBaBuSy", "presets");
    }

    /// <summary>A file stem for <paramref name="name"/>: invalid characters → '_', no trailing dots, no device names.</summary>
    public static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.').Trim();
        if (name.Length == 0) return "preset";
        return ReservedNames.Contains(name) ? name + "_" : name;
    }

    /// <summary>Full path for a preset name (does not create the file).</summary>
    public string PathFor(string name) => Path.Combine(Directory, Sanitize(name) + ".json");

    public bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary><paramref name="name"/> if free, else "name (2)", "name (3)", …</summary>
    public string UniqueName(string name)
    {
        var stem = Sanitize(name);
        if (!Exists(stem)) return stem;
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem} ({i})";
            if (!Exists(candidate)) return candidate;
        }
    }

    /// <summary>Preset names, alphabetical. Empty when the folder does not exist.</summary>
    public IReadOnlyList<string> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<string>();
        return System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Load a preset; asset paths come back absolute. Throws <see cref="ScenarioException"/> for a bad file.</summary>
    public Task<CrossScreenConfig> LoadAsync(string name) => Task.Run(() => SceneFile.Load(PathFor(name)));

    /// <summary>Save the editor's scene: gallery assets stay referenced in place, targets are dropped.</summary>
    public Task<string> SaveAsync(string name, CrossScreenConfig config)
    {
        var copy = SceneFile.Clone(config);
        copy.SelectedMonitorIds = new List<string>();
        PresetBundle.RelativizeUnder(copy, Directory);
        return WriteAsync(name, copy);
    }

    /// <summary>
    /// Import a scene / exported preset file: its assets are copied into <c>presets\assets\</c>. The name defaults
    /// to a free variant of the file name; an explicit name overwrites. Throws <see cref="ScenarioException"/>
    /// (bad JSON, missing asset, UNC path) or <see cref="IOException"/>.
    /// </summary>
    public async Task<string> ImportAsync(string sourceFile, string? name = null)
    {
        var config = await Task.Run(() => SceneFile.Load(sourceFile));
        config.SelectedMonitorIds = new List<string>();
        await PresetBundle.BundleAssetsAsync(config, Directory);
        return await WriteAsync(name ?? UniqueName(Path.GetFileNameWithoutExtension(sourceFile)), config);
    }

    /// <summary>Export a scene to <paramref name="targetFile"/> with its assets in an <c>assets\</c> folder next to it.</summary>
    public static async Task ExportAsync(CrossScreenConfig config, string targetFile)
    {
        var copy = SceneFile.Clone(config);
        copy.SelectedMonitorIds = new List<string>();
        var full = Path.GetFullPath(targetFile);
        await PresetBundle.BundleAssetsAsync(copy, Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, SceneFile.ToJson(copy));
    }

    /// <summary>Delete a preset (its assets stay: other presets may share them). No-op if missing.</summary>
    public void Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return;
        File.Delete(path);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string> WriteAsync(string name, CrossScreenConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var stem = Sanitize(name);
        await File.WriteAllTextAsync(PathFor(stem), SceneFile.ToJson(config));
        Changed?.Invoke(this, EventArgs.Empty);
        return stem;
    }
}
```

- [ ] **Step 4: Run to verify it passes** — expected PASS (all `PresetStoreTests`). Note `Sanitize(" Comet ")` → `"Comet"` and `".."` → `"preset"` (trailing dots trimmed), which keeps `PathFor` inside the folder.
- [ ] **Step 5: Full build + test, commit** — `git add WaBiBaBuSy.Core/Services/Animation/PresetStore.cs WaBiBaBuSy.Tests/PresetStoreTests.cs`, message `feat(presets): preset store with import / export`.

---

### Task 4: Starter presets — manifest, two new scenes, `PresetSeeder`

**Files:**
- Create: `TestScenarios/showcase/starter-presets.json`, `TestScenarios/showcase/scenes/14-ring-cat.json`, `TestScenarios/showcase/scenes/15-party-colors.json`
- Create: `WaBiBaBuSy.Core/Services/Animation/PresetSeeder.cs`
- Modify: `.docs/plans/2026-10-03-presets-design.md` §4 (starter source = shipped `TestScenarios\showcase\`)
- Test: `WaBiBaBuSy.Tests/PresetSeederTests.cs`

**Interfaces:**
- Consumes: `PresetStore.List / ImportAsync` (Task 3), `SceneFile.JsonOptions`.
- Produces: `public static class PresetSeeder { public const string ManifestFileName = "starter-presets.json"; public static Task<int> SeedIfEmptyAsync(PresetStore store, string starterDir, ILogger? logger = null); }`

- [ ] **Step 1: Add the scene files**

`TestScenarios/showcase/scenes/14-ring-cat.json`:
```jsonc
{
  // "Ring Fish" from the authoring design: a cat runs the wall facing where it goes, a short trail behind it.
  // Set the room's Path to Ring for a closed loop around both tables.
  "Background": { "Mode": "SolidColor", "ColorHex": "#06233A" },
  "Animation": {
    "AnimationPath": "../assets/cat.gif", "FitMode": "TargetHeight", "TargetHeight": 200,
    "FaceTravelDirection": true,
    "Effects": { "Trail": { "Count": 6, "SpacingMs": 90, "StartOpacity": 0.5, "ScaleFalloff": 0.92, "UseGradingColor": false } }
  },
  "Movement": { "Type": "Linear", "SpeedPixelsPerSecond": 600 },
  "DistributionMode": "Sequential"
}
```

`TestScenarios/showcase/scenes/15-party-colors.json`:
```jsonc
{
  // A still logo on every monitor flashing through party colors, fast.
  "Background": { "Mode": "SolidColor", "ColorHex": "#000000" },
  "Animation": {
    "AnimationPath": "../assets/bibabu-logo.png", "FitMode": "TargetHeight", "TargetHeight": 420,
    "ColorGrading": { "Mode": "CycleColorList", "CyclesPerSecond": 1.0,
                      "ColorList": ["#FF3B30", "#FFCC00", "#34C759", "#0A84FF", "#BF5AF2"] }
  },
  "Movement": { "Type": "Static" },
  "DistributionMode": "Simultaneous"
}
```

`TestScenarios/showcase/starter-presets.json` (12-lunar-tears is left out: it points at a 70 MB video outside the repo):
```jsonc
{
  // Starter presets seeded into %APPDATA%\WaBiBaBuSy\presets\ when that folder is empty (PresetSeeder).
  // Names are what the editor, the playlist and the tray's Quick start menu show.
  "presets": [
    { "name": "Logo Conveyor", "scene": "scenes/01-rainbow-conveyor.json" },
    { "name": "DVD Bounce", "scene": "scenes/02-dvd-bounce.json" },
    { "name": "Sine Serpent", "scene": "scenes/03-sine-serpent.json" },
    { "name": "Mexican Wave", "scene": "scenes/04-la-ola.json" },
    { "name": "Cat Parade", "scene": "scenes/05-cat-parade.json" },
    { "name": "Orbit", "scene": "scenes/06-cat-orbit-wave.json" },
    { "name": "Fireflies", "scene": "scenes/07-fireflies.json" },
    { "name": "Confetti Drift", "scene": "scenes/08-confetti-drift.json" },
    { "name": "Neon Corridor", "scene": "scenes/09-neon-corridor.json" },
    { "name": "Pulse Grid", "scene": "scenes/10-pulse-grid.json" },
    { "name": "Endless Stripes", "scene": "scenes/11-stripes-endless.json" },
    { "name": "Comet", "scene": "scenes/13-comet.json" },
    { "name": "Ring Cat", "scene": "scenes/14-ring-cat.json" },
    { "name": "Party Colors", "scene": "scenes/15-party-colors.json" }
  ]
}
```

(Design name mapping: Logo Rain → "Logo Conveyor" (Endless scrolls horizontally only), Mexican Wave → 04, Orbit → 06, Ring Fish → "Ring Cat", Party Colors → 15.)

- [ ] **Step 2: Write the failing tests**

```csharp
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
```

- [ ] **Step 3: Run to verify it fails** — `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~PresetSeederTests` → build error.

- [ ] **Step 4: Implement** `WaBiBaBuSy.Core/Services/Animation/PresetSeeder.cs`

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>
/// First run: imports the starter presets listed in <c>starter-presets.json</c> (the shipped showcase scenes)
/// into an empty preset folder. Assets are copied, so the presets do not depend on the install folder.
/// </summary>
public static class PresetSeeder
{
    public const string ManifestFileName = "starter-presets.json";

    private sealed class Manifest
    {
        public List<Entry> Presets { get; set; } = new();
    }

    private sealed class Entry
    {
        public string Name { get; set; } = string.Empty;
        public string Scene { get; set; } = string.Empty;
    }

    /// <summary>
    /// When the store holds no preset, import every manifest entry of <paramref name="starterDir"/>. A bad entry is
    /// logged and skipped; a missing or unreadable manifest seeds nothing. Returns the number of presets seeded.
    /// </summary>
    public static async Task<int> SeedIfEmptyAsync(PresetStore store, string starterDir, ILogger? logger = null)
    {
        if (store.List().Count > 0) return 0;

        var manifestPath = Path.Combine(starterDir, ManifestFileName);
        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath), SceneFile.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning("Starter presets not seeded: {Manifest}: {Message}", manifestPath, ex.Message);
            return 0;
        }

        int seeded = 0;
        foreach (var entry in manifest?.Presets ?? new List<Entry>())
        {
            try
            {
                await store.ImportAsync(Path.Combine(starterDir, entry.Scene), entry.Name);
                seeded++;
            }
            catch (Exception ex) when (ex is ScenarioException or IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning("Starter preset '{Name}' skipped: {Message}", entry.Name, ex.Message);
            }
        }
        return seeded;
    }
}
```

(`DirectoryNotFoundException` / `FileNotFoundException` are `IOException`s, which covers a missing starter folder.)

- [ ] **Step 5: Run to verify it passes** — expected PASS (6 tests). If the shipped-manifest test fails on a scene, fix the scene file (not the test).

- [ ] **Step 6: Record the spec deviation** — in `.docs/plans/2026-10-03-presets-design.md` §4, replace the sentence starting "The UI csproj links them into the build and publish output under `Presets\`…" with:

```markdown
The UI csproj already ships `TestScenarios\**` next to the exe (test mode), so the seeder reads
`<AppBase>\TestScenarios\showcase\starter-presets.json` directly — no second copy; the installer ships `publish\*`
recursively.
```

- [ ] **Step 7: Full build + test, commit** — `git add TestScenarios/showcase/starter-presets.json TestScenarios/showcase/scenes/14-ring-cat.json TestScenarios/showcase/scenes/15-party-colors.json WaBiBaBuSy.Core/Services/Animation/PresetSeeder.cs WaBiBaBuSy.Tests/PresetSeederTests.cs .docs/plans/2026-10-03-presets-design.md`, message `feat(presets): starter presets from the showcase scenes`.

---

### Task 5: `RoomNodeSources` — the room without a window (pure)

**Files:**
- Create: `WaBiBaBuSy.Models/Topology/LocalMonitor.cs`
- Create: `WaBiBaBuSy.Core/Services/RoomNodeSources.cs`
- Test: `WaBiBaBuSy.Tests/RoomNodeSourcesTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record LocalMonitor(int Index, int Width, int Height, int X, int Y, bool IsPrimary, string DeviceName, int RefreshHz);`
  - `public sealed record LocalScreens(IReadOnlyList<LocalMonitor> Monitors, int VirtualWidth, int VirtualHeight, string MachineName);`
  - `RoomNodeSources.ServerNodes(LocalScreens local, IEnumerable<ConnectedClient> clients, Func<string, int?> persistedOrder, Func<string, int?> persistedGapCm, IReadOnlyDictionary<string, int> sessionGapCm) → List<ConnectedClient>`
  - `RoomNodeSources.StandaloneNodes(LocalScreens local, IReadOnlyDictionary<string, int> sessionGapCm) → List<ConnectedClient>`
  - `RoomNodeSources.MonitorFor(ConnectedClient client) → (int Index, MonitorInfo? Monitor)`
  - `RoomNodeSources.ToRoomNode(ConnectedClient client) → RoomNode`
  (`ConnectedClient`, `MonitorInfo`, `ScreenConfiguration`, `ClientStatusEnum` are `WaBiBaBuSy.Grpc` types.)

The logic is moved **unchanged** from `MainWindowViewModel.RefreshTopology` (server branch), `ShowLocalMachineNode` and the add branch of `UpdateClientList`. Keep the field copies exactly (remote copies do not carry `PrefetchReady/Total` today — preserve that; it is out of scope).

- [ ] **Step 1: Write the failing tests**

```csharp
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class RoomNodeSourcesTests
{
    private static readonly LocalScreens TwoMonitors = new(new[]
    {
        new LocalMonitor(0, 1920, 1080, 0, 0, true, @"\\.\DISPLAY1", 144),
        new LocalMonitor(1, 2560, 1440, 1920, 0, false, @"\\.\DISPLAY2", 60),
    }, 4480, 1440, "PC");

    private static readonly Dictionary<string, int> NoGaps = new();

    private static ConnectedClient Remote(string id, int order, int monitors, int gap = 0)
    {
        var screen = new ScreenConfiguration { MonitorCount = monitors };
        for (int m = 0; m < monitors; m++)
            screen.Monitors.Add(new MonitorInfo { Index = m, Width = 1280 + m, Height = 720, RefreshRate = 75, PixelsPerCm = 38f });
        return new ConnectedClient
        {
            ClientId = id, Hostname = id.ToLowerInvariant(), IpAddress = "10.0.0.5", Status = ClientStatusEnum.ClientPlaying,
            OrderPosition = order, PhysicalDistanceCm = gap, ScreenConfig = screen, RttMs = 4, ClockOffsetMs = -2,
        };
    }

    [Fact]
    public void StandaloneNodes_OnePerMonitor()
    {
        var nodes = RoomNodeSources.StandaloneNodes(TwoMonitors, new Dictionary<string, int> { ["LOCAL_MACHINE_MONITOR_1"] = 12 });

        Assert.Equal(new[] { "LOCAL_MACHINE_MONITOR_0", "LOCAL_MACHINE_MONITOR_1" }, nodes.Select(n => n.ClientId));
        Assert.Equal(new[] { 0, 1 }, nodes.Select(n => n.OrderPosition));
        Assert.Equal("PC - Monitor 1", nodes[0].Hostname);
        Assert.Equal("Primary Monitor", nodes[0].IpAddress);
        Assert.Equal("Monitor 2", nodes[1].IpAddress);
        Assert.Equal(new[] { 0, 12 }, nodes.Select(n => n.PhysicalDistanceCm));
        Assert.Equal(ClientStatusEnum.ClientConnected, nodes[0].Status);
        Assert.Equal(2, nodes[0].ScreenConfig.MonitorCount);
        Assert.Equal(4480, nodes[0].ScreenConfig.TotalWidth);
        Assert.Equal(144, nodes[0].ScreenConfig.Monitors[0].RefreshRate);
    }

    [Fact]
    public void ServerNodes_LocalsThenClients_OrderAndGaps()
    {
        var persistedOrder = new Dictionary<string, int> { ["SERVER_LOCALHOST_MONITOR_1"] = 5, ["MULTI_MONITOR_1"] = 9 };
        var persistedGap = new Dictionary<string, int> { ["SERVER_LOCALHOST_MONITOR_0"] = 3, ["MULTI_MONITOR_0"] = 7 };
        var session = new Dictionary<string, int> { ["SERVER_LOCALHOST_MONITOR_1"] = 11, ["SERVER_LOCALHOST_MONITOR_0"] = 99 };

        var nodes = RoomNodeSources.ServerNodes(TwoMonitors,
            new[] { Remote("SINGLE", 4, 1, gap: 6), Remote("MULTI", 1, 2, gap: 8) },
            id => persistedOrder.TryGetValue(id, out var o) ? o : null,
            id => persistedGap.TryGetValue(id, out var g) ? g : null,
            session);

        Assert.Equal(new[] { "SERVER_LOCALHOST_MONITOR_0", "SERVER_LOCALHOST_MONITOR_1", "SINGLE", "MULTI_MONITOR_0", "MULTI_MONITOR_1" },
            nodes.Select(n => n.ClientId));
        // Locals: persisted order, else index. Gap: persisted, else session.
        Assert.Equal(0, nodes[0].OrderPosition);
        Assert.Equal(5, nodes[1].OrderPosition);
        Assert.Equal(3, nodes[0].PhysicalDistanceCm);
        Assert.Equal(11, nodes[1].PhysicalDistanceCm);
        Assert.Equal("PC - Monitor 2 (Server)", nodes[1].Hostname);
        Assert.Equal("Primary Monitor (Server)", nodes[0].IpAddress);
        // Single-monitor client: the server's order and gap as reported.
        Assert.Equal(4, nodes[2].OrderPosition);
        Assert.Equal(6, nodes[2].PhysicalDistanceCm);
        // Expanded client: persisted order, else a counter that starts after the locals (2) and advances once per
        // expanded node only (single-monitor clients keep the server's order and do not advance it).
        Assert.Equal(2, nodes[3].OrderPosition);
        Assert.Equal(9, nodes[4].OrderPosition);
        Assert.Equal(7, nodes[3].PhysicalDistanceCm);   // persisted
        Assert.Equal(8, nodes[4].PhysicalDistanceCm);   // client's own gap
        Assert.Equal("multi - Monitor 2", nodes[4].Hostname);
        Assert.Equal(ClientStatusEnum.ClientPlaying, nodes[4].Status);
        Assert.Equal(4, nodes[4].RttMs);
    }

    [Fact]
    public void ToRoomNode_LocalMonitor()
    {
        var node = RoomNodeSources.ToRoomNode(RoomNodeSources.StandaloneNodes(TwoMonitors, NoGaps)[1]);

        Assert.Equal("LOCAL_MACHINE_MONITOR_1", node.Id);
        Assert.Equal(1, node.MonitorIndex);
        Assert.Equal(2560, node.WidthPx);
        Assert.Equal(1440, node.HeightPx);
        Assert.Equal(60, node.RefreshHz);
        Assert.True(node.IsConnected);
        Assert.True(node.IsLocal);
    }

    [Fact]
    public void ToRoomNode_ExpandedAndSingleClients()
    {
        var expanded = Remote("MULTI", 0, 2);
        expanded.ClientId = "MULTI_MONITOR_1";
        var e = RoomNodeSources.ToRoomNode(expanded);
        Assert.Equal(1, e.MonitorIndex);
        Assert.Equal(1281, e.WidthPx);
        Assert.Equal(38f, e.PixelsPerCm);
        Assert.True(e.IsConnected);   // Playing counts as connected
        Assert.Equal(-2, e.ClockOffsetMs);

        var single = RoomNodeSources.ToRoomNode(Remote("SINGLE", 3, 1, gap: 6));
        Assert.Equal(0, single.MonitorIndex);
        Assert.Equal(1280, single.WidthPx);
        Assert.Equal(3, single.Order);
        Assert.Equal(6, single.GapBeforeCm);
    }

    [Fact]
    public void MonitorFor_IndexOutOfRange_KeepsIndexWithoutInfo()
    {
        var c = Remote("X", 0, 1);
        c.ClientId = "X_MONITOR_4";
        var (index, monitor) = RoomNodeSources.MonitorFor(c);
        Assert.Equal(4, index);
        Assert.Null(monitor);
        Assert.Equal(0, RoomNodeSources.ToRoomNode(c).WidthPx);
    }

    [Fact]
    public void ToRoomNode_DisconnectedStatus()
    {
        var c = Remote("X", 0, 1);
        c.Status = ClientStatusEnum.ClientDisconnected;
        Assert.False(RoomNodeSources.ToRoomNode(c).IsConnected);
    }
}
```

> The order assertions document the existing behaviour of the moved code; if the original differs, the original wins.

- [ ] **Step 2: Run to verify it fails** — `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~RoomNodeSourcesTests` → build error.

- [ ] **Step 3: Implement** `WaBiBaBuSy.Models/Topology/LocalMonitor.cs`

```csharp
namespace WaBiBaBuSy.Models.Topology;

/// <summary>One of this machine's monitors as the room sees it (screen bounds + refresh rate).</summary>
public sealed record LocalMonitor(int Index, int Width, int Height, int X, int Y, bool IsPrimary, string DeviceName, int RefreshHz);

/// <summary>This machine's monitors, the virtual screen size and the machine name (node labels).</summary>
public sealed record LocalScreens(IReadOnlyList<LocalMonitor> Monitors, int VirtualWidth, int VirtualHeight, string MachineName);
```

`WaBiBaBuSy.Core/Services/RoomNodeSources.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.Core.Services;

/// <summary>
/// The room's node list from its sources — this machine's monitors and, in server mode, the connected clients
/// (multi-monitor clients expanded into one node per monitor). Shared by the control panel and the playback
/// host, which builds the room itself when no window is open (tray Quick start).
/// </summary>
public static class RoomNodeSources
{
    /// <summary>Server mode: one node per server monitor, then the clients (multi-monitor clients expanded).</summary>
    public static List<ConnectedClient> ServerNodes(LocalScreens local, IEnumerable<ConnectedClient> clients,
        Func<string, int?> persistedOrder, Func<string, int?> persistedGapCm, IReadOnlyDictionary<string, int> sessionGapCm)
    {
        var screen = ScreenConfig(local);
        var nodes = new List<ConnectedClient>();
        for (int i = 0; i < local.Monitors.Count; i++)
        {
            var nodeId = $"{NodeIds.ServerLocalPrefix}{i}";
            nodes.Add(new ConnectedClient
            {
                ClientId = nodeId,
                Hostname = $"{local.MachineName} - Monitor {i + 1} (Server)",
                IpAddress = local.Monitors[i].IsPrimary ? "Primary Monitor (Server)" : $"Monitor {i + 1} (Server)",
                Status = ClientStatusEnum.ClientConnected,
                OrderPosition = persistedOrder(nodeId) ?? i,
                PhysicalDistanceCm = persistedGapCm(nodeId) ?? sessionGapCm.GetValueOrDefault(nodeId),
                ScreenConfig = screen
            });
        }

        int nextOrder = local.Monitors.Count;
        foreach (var client in clients)
        {
            int clientMonitorCount = client.ScreenConfig?.MonitorCount ?? 1;
            if (clientMonitorCount > 1 && client.ScreenConfig?.Monitors.Count > 0)
            {
                for (int m = 0; m < client.ScreenConfig.Monitors.Count; m++)
                {
                    var expandedId = $"{client.ClientId}_MONITOR_{m}";
                    var expandedOrder = persistedOrder(expandedId) ?? nextOrder;
                    nextOrder++;
                    nodes.Add(new ConnectedClient
                    {
                        ClientId = expandedId,
                        Hostname = $"{client.Hostname} - Monitor {m + 1}",
                        IpAddress = client.IpAddress,
                        Status = client.Status,
                        OrderPosition = expandedOrder,
                        // Expanded nodes are not connected clients: their gap lives only in the topology store.
                        PhysicalDistanceCm = persistedGapCm(expandedId) ?? client.PhysicalDistanceCm,
                        ScreenConfig = client.ScreenConfig,
                        ClockOffsetMs = client.ClockOffsetMs,
                        RttMs = client.RttMs,
                        LastDriftReportUtc = client.LastDriftReportUtc
                    });
                }
            }
            else
            {
                // Single monitor client - use server's OrderPosition directly
                nodes.Add(new ConnectedClient
                {
                    ClientId = client.ClientId,
                    Hostname = client.Hostname,
                    IpAddress = client.IpAddress,
                    Status = client.Status,
                    OrderPosition = client.OrderPosition,
                    PhysicalDistanceCm = client.PhysicalDistanceCm,
                    ScreenConfig = client.ScreenConfig,
                    ClockOffsetMs = client.ClockOffsetMs,
                    RttMs = client.RttMs,
                    LastDriftReportUtc = client.LastDriftReportUtc
                });
            }
        }
        return nodes;
    }

    /// <summary>No server: one node per local monitor. No store, so gaps set in the room live for the session.</summary>
    public static List<ConnectedClient> StandaloneNodes(LocalScreens local, IReadOnlyDictionary<string, int> sessionGapCm)
    {
        var screen = ScreenConfig(local);
        return local.Monitors.Select((monitor, i) =>
        {
            var nodeId = $"{NodeIds.LocalMachinePrefix}{i}";
            return new ConnectedClient
            {
                ClientId = nodeId,
                Hostname = $"{local.MachineName} - Monitor {i + 1}",
                IpAddress = monitor.IsPrimary ? "Primary Monitor" : $"Monitor {i + 1}",
                Status = ClientStatusEnum.ClientConnected,
                OrderPosition = i,
                PhysicalDistanceCm = sessionGapCm.GetValueOrDefault(nodeId),
                ScreenConfig = screen   // shared by every local node
            };
        }).ToList();
    }

    /// <summary>
    /// The monitor a node shows: the index from a <c>…MONITOR_n</c> id (info when the client reported it), else the
    /// only monitor of a single-monitor client, else (-1, null).
    /// </summary>
    public static (int Index, MonitorInfo? Monitor) MonitorFor(ConnectedClient client)
    {
        var id = client.ClientId;
        string? suffix = null;
        if (id.StartsWith(NodeIds.LocalMachinePrefix, StringComparison.Ordinal)) suffix = id[NodeIds.LocalMachinePrefix.Length..];
        else if (id.StartsWith(NodeIds.ServerLocalPrefix, StringComparison.Ordinal)) suffix = id[NodeIds.ServerLocalPrefix.Length..];
        else if (id.Contains("_MONITOR_", StringComparison.Ordinal)) suffix = id[(id.LastIndexOf("_MONITOR_", StringComparison.Ordinal) + "_MONITOR_".Length)..];

        var monitors = client.ScreenConfig?.Monitors;
        if (suffix != null && int.TryParse(suffix, out int index))
            return (index, monitors != null && monitors.Count > index ? monitors[index] : null);
        if (monitors?.Count == 1) return (0, monitors[0]);
        return (-1, null);
    }

    /// <summary>A node as the playback host lays scenes out over it.</summary>
    public static RoomNode ToRoomNode(ConnectedClient client)
    {
        var (index, monitor) = MonitorFor(client);
        return new RoomNode
        {
            Id = client.ClientId,
            Hostname = client.Hostname,
            Order = client.OrderPosition,
            MonitorIndex = index,
            WidthPx = monitor?.Width ?? 0,
            HeightPx = monitor?.Height ?? 0,
            RefreshHz = monitor?.RefreshRate ?? 0,
            GapBeforeCm = client.PhysicalDistanceCm,
            PixelsPerCm = monitor?.PixelsPerCm ?? 0f,
            IsConnected = client.Status is ClientStatusEnum.ClientConnected or ClientStatusEnum.ClientPlaying,
            RttMs = client.RttMs,
            ClockOffsetMs = client.ClockOffsetMs
        };
    }

    private static ScreenConfiguration ScreenConfig(LocalScreens local)
    {
        var screen = new ScreenConfiguration
        {
            MonitorCount = local.Monitors.Count,
            TotalWidth = local.VirtualWidth,
            TotalHeight = local.VirtualHeight
        };
        foreach (var m in local.Monitors)
        {
            screen.Monitors.Add(new MonitorInfo
            {
                Index = m.Index,
                Width = m.Width,
                Height = m.Height,
                X = m.X,
                Y = m.Y,
                IsPrimary = m.IsPrimary,
                DeviceName = m.DeviceName,
                RefreshRate = m.RefreshHz
            });
        }
        return screen;
    }
}
```

> Before writing, diff the moved code against `MainWindowViewModel.cs` (server branch ~1866–1965, `ShowLocalMachineNode` ~2047–2100, `UpdateClientList` add branch ~2160–2200) and keep any difference you find (the plan's copy was taken from those lines on 2026-10-03).

- [ ] **Step 4: Run to verify it passes** — expected PASS (6 tests).
- [ ] **Step 5: Full build + test, commit** — `git add WaBiBaBuSy.Models/Topology/LocalMonitor.cs WaBiBaBuSy.Core/Services/RoomNodeSources.cs WaBiBaBuSy.Tests/RoomNodeSourcesTests.cs`, message `refactor(room): pure room node builder in Core`.

---

### Task 6: Window and host use the builder; host builds the room; `QuickStartAsync`

**Files:**
- Create: `WaBiBaBuSy.UI/Services/LocalMonitors.cs`
- Modify: `WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs`

**Interfaces:**
- Consumes: `RoomNodeSources`, `LocalScreens` (Task 5), `SeatMapStore.Load()`.
- Produces:
  - `LocalMonitors.Snapshot() → LocalScreens`, `LocalMonitors.ReferencePixelsPerCm → float`
  - `ServerPlaybackHost.SessionGapCm : ConcurrentDictionary<string, int>`
  - `ServerPlaybackHost.UpdateRoom(object owner, IReadOnlyList<RoomNode> nodes, SeatMap seatMap, float referencePixelsPerCm)` (owner added)
  - `ServerPlaybackHost.ReleaseRoom(object owner)`
  - `ServerPlaybackHost.EnsureRoom()`
  - `ServerPlaybackHost.QuickStartAsync(CrossScreenConfig config) → Task<bool>`

No unit tests (UI project); the pure logic is covered by Task 5. Verify by build + full test run.

- [ ] **Step 1: `LocalMonitors`** — `WaBiBaBuSy.UI/Services/LocalMonitors.cs`

```csharp
using System;
using System.Linq;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.UI.Services;

/// <summary>This machine's monitors (WinForms bounds + native refresh rate) and the physical canvas's reference DPI.</summary>
public static class LocalMonitors
{
    private static float? _referencePixelsPerCm;

    /// <summary>The monitors now, in WinForms order (the order local node ids use).</summary>
    public static LocalScreens Snapshot()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var refreshByDevice = NativeMonitorInfo.GetAllMonitors()
            .GroupBy(m => m.DeviceName)
            .ToDictionary(g => g.Key, g => g.First().RefreshRateHz);
        var monitors = screens.Select((s, i) => new LocalMonitor(i, s.Bounds.Width, s.Bounds.Height, s.Bounds.X, s.Bounds.Y,
            s.Primary, s.DeviceName, refreshByDevice.GetValueOrDefault(s.DeviceName))).ToList();
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        return new LocalScreens(monitors, virtualScreen.Width, virtualScreen.Height, Environment.MachineName);
    }

    /// <summary>
    /// Pixels per cm of the primary monitor — the reference DPI of a physical canvas. Cached (monitor DPI does not
    /// change at runtime). 0 when it cannot be queried.
    /// </summary>
    public static float ReferencePixelsPerCm
    {
        get
        {
            if (_referencePixelsPerCm.HasValue) return _referencePixelsPerCm.Value;
            try
            {
                var monitors = NativeMonitorInfo.GetAllMonitors();
                var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
                _referencePixelsPerCm = primary?.PixelsPerCm ?? 0f;
            }
            catch { _referencePixelsPerCm = 0f; }
            return _referencePixelsPerCm.Value;
        }
    }
}
```

- [ ] **Step 2: Host — session gaps, room owner, `EnsureRoom`, `QuickStartAsync`.** In `ServerPlaybackHost.cs` (add `using WaBiBaBuSy.Core.Services;` if missing — it is already imported; `System.Collections.Concurrent` is imported):

Replace `UpdateRoom`:

```csharp
    // The window that last pushed the room; null = no open window keeps it current (EnsureRoom rebuilds it).
    private object? _roomOwner;

    /// <summary>
    /// Bezel gaps set in the room that no store keeps (standalone monitors, expanded nodes in client mode).
    /// App-lifetime, so they survive closing the window.
    /// </summary>
    public ConcurrentDictionary<string, int> SessionGapCm { get; } = new();

    /// <summary>The room's nodes, seat map and reference DPI — what scenes and show items are laid out over.</summary>
    public void UpdateRoom(object owner, IReadOnlyList<RoomNode> nodes, SeatMap seatMap, float referencePixelsPerCm)
    {
        _roomOwner = owner;
        _nodes = nodes;
        _seatMap = seatMap;
        _referencePixelsPerCm = referencePixelsPerCm;
    }

    /// <summary>The window closed: its room is no longer kept current (a newer window's room stays).</summary>
    public void ReleaseRoom(object owner)
    {
        if (ReferenceEquals(_roomOwner, owner)) _roomOwner = null;
    }

    /// <summary>
    /// Make sure the room is current: an open window keeps it so; otherwise build it from the sources the window
    /// uses (server monitors + clients when the server runs, else this machine's monitors; stored seat map).
    /// </summary>
    public void EnsureRoom()
    {
        if (_roomOwner != null && _nodes.Count > 0) return;
        var local = LocalMonitors.Snapshot();
        var sources = _service.IsServerRunning
            ? RoomNodeSources.ServerNodes(local, _service.GetConnectedClients(),
                _service.GetServerLocalMonitorOrder, _service.GetServerLocalMonitorDistance, SessionGapCm)
            : RoomNodeSources.StandaloneNodes(local, SessionGapCm);
        _nodes = sources.Select(RoomNodeSources.ToRoomNode).OrderBy(n => n.Order).ToList();
        _seatMap = SeatMapStore.Load();
        _referencePixelsPerCm = LocalMonitors.ReferencePixelsPerCm;
    }
```

Add after `PlaySceneAsync` (Scenes section):

```csharp
    /// <summary>
    /// Tray Quick start: play <paramref name="config"/> on every node of the room, window open or not. Same teardown
    /// as ▶ Play (a running show or scene stops first). False when a start is in flight or the room has no node.
    /// </summary>
    public async Task<bool> QuickStartAsync(CrossScreenConfig config)
    {
        if (!TryBeginStart()) return false;
        try
        {
            EnsureRoom();
            if (_nodes.Count == 0) return false;
            config.SelectedMonitorIds = new List<string>();
            await PlaySceneAsync(config);
            return IsCrossScreenRunning;
        }
        finally
        {
            EndStart();
        }
    }
```

Check the field names `_nodes`, `_seatMap`, `_referencePixelsPerCm` against the host (lines ~61–66) and that `SeatMapStore` / `ConcurrentDictionary` namespaces are imported (`WaBiBaBuSy.Models.Topology` / `System.Collections.Concurrent`). Update the class summary sentence "and while the window is closed scenes and show items use the last room it pushed" → add "; tray Quick start rebuilds the room from its sources (<see cref="EnsureRoom"/>)".

- [ ] **Step 3: Window — use the builder.** In `MainWindowViewModel.cs`:
  1. Delete the field `_sessionGapCm` (line ~67). Replace every `_sessionGapCm` use with `_server.SessionGapCm` (`SetGapCmAsync` ~1000, client-mode branch ~2000).
  2. Replace the `_referencePixelsPerCm` field and the body of `ReferencePixelsPerCm` (~188–208) with `public float ReferencePixelsPerCm => LocalMonitors.ReferencePixelsPerCm;` keeping its XML doc.
  3. In `RefreshTopology`, server branch: replace everything from `// Create a list that includes the server's local monitors…` through the end of the `foreach (var client in connectedClients)` loop with

     ```csharp
                var allNodes = RoomNodeSources.ServerNodes(LocalMonitors.Snapshot(), connectedClients,
                    _service.GetServerLocalMonitorOrder, _service.GetServerLocalMonitorDistance, _server.SessionGapCm);
     ```
     keeping the following `Total nodes to display` debug line and `UpdateClientList(allNodes);`.
  4. Replace the body of `ShowLocalMachineNode()` with

     ```csharp
        var nodes = RoomNodeSources.StandaloneNodes(LocalMonitors.Snapshot(), _server.SessionGapCm);
        if (_enableNetworkTopologyDebugOutput)
            Debug.WriteLine($"[ShowLocalMachineNode] {nodes.Count} monitor node(s)");
        UpdateClientList(nodes);
     ```
  5. In `UpdateClientList`'s add branch replace the monitor-suffix block (from `// Extract monitor info for this specific node` through the `else if (grpcClient.ScreenConfig?.Monitors.Count == 1) { … }` block) with

     ```csharp
                    // Monitor info for this node (LOCAL_MACHINE_MONITOR_X, SERVER_LOCALHOST_MONITOR_X, ClientId_MONITOR_X, single-monitor client)
                    var (monitorIndex, monitorInfo) = RoomNodeSources.MonitorFor(grpcClient);
                    string? monitorName = monitorInfo?.DeviceName;
                    int monitorWidth = monitorInfo?.Width ?? 0;
                    int monitorHeight = monitorInfo?.Height ?? 0;
                    bool isPrimary = monitorInfo?.IsPrimary ?? false;
                    int monitorRefreshHz = monitorInfo?.RefreshRate ?? 0;
                    float monitorPixelsPerCm = monitorInfo?.PixelsPerCm ?? 0f;
     ```
  6. `SyncRoom`: `_server.UpdateRoom(this, RoomNodes(), _seatMap, ReferencePixelsPerCm);`
  7. `Detach()`: add `_server.ReleaseRoom(this);` after `_detached = true;`.
  8. Remove `using`s that became unused only if the compiler warns (e.g. `NativeMonitorInfo` may still be used elsewhere — leave it).

- [ ] **Step 4: Build + test** — `dotnet build WaBiBaBuSy.sln` → 0 errors (fix any remaining `_sessionGapCm` / `UpdateRoom` call sites the compiler lists); `dotnet test WaBiBaBuSy.Tests` → green.
- [ ] **Step 5: Commit** — `git add WaBiBaBuSy.UI/Services/LocalMonitors.cs WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs`, message:

```
feat(server): playback host builds the room without a window

RoomNodeSources (Core) builds the node list for the control panel and the
host; the host rebuilds the room from its sources when no window keeps it
(EnsureRoom) and gains QuickStartAsync. Session gaps move to the host.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
```

---

### Task 7: Tray — preset store, first-run seeding, "Quick start ▸"

**Files:**
- Modify: `WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs`
- Modify: `WaBiBaBuSy.UI/App.axaml.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (ctor parameter only; menus come in Task 8)

**Interfaces:**
- Consumes: `PresetStore`, `PresetSeeder` (Tasks 3–4), `ServerPlaybackHost.QuickStartAsync` (Task 6).
- Produces: `TrayViewModel.Presets : PresetStore`; `TrayViewModel.AttachTrayMenu(NativeMenu? menu)`; `MainWindowViewModel(WaBiBaBuSyService service, ServerPlaybackHost server, PresetStore? presets = null)` storing `_presets`.

- [ ] **Step 1: TrayViewModel.** Add usings `Avalonia.Controls` (already), `Microsoft.Extensions.Logging`, `WaBiBaBuSy.Core.Services.Animation`. Fields and ctor additions (after `_serverPlayback` is created):

```csharp
    // Scene presets (editor, playlist, Quick start); seeded with the starter presets on first run
    private readonly PresetStore _presets = new();
    private NativeMenuItem? _quickStartItem;

    /// <summary>The app's preset store, shared with every control panel.</summary>
    public PresetStore Presets => _presets;
```

In the constructor, after `_service.TestRunControl = _testControl;`:

```csharp
        _presets.Changed += (_, _) => Dispatcher.UIThread.Post(RebuildQuickStartMenu);
        _ = SeedPresetsAsync();
```

Methods:

```csharp
    /// <summary>First run: the starter presets (shipped showcase scenes) into an empty preset folder.</summary>
    private async Task SeedPresetsAsync()
    {
        var log = AppLogger.CreateLogger<TrayViewModel>();
        try
        {
            var starterDir = Path.Combine(AppContext.BaseDirectory, "TestScenarios", "showcase");
            int seeded = await PresetSeeder.SeedIfEmptyAsync(_presets, starterDir, log);
            if (seeded > 0) log.LogInformation("Seeded {Count} starter preset(s) into {Dir}", seeded, _presets.Directory);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Seeding the starter presets failed");
        }
    }

    /// <summary>Insert "Quick start ▸" below "Open Control Panel" in the tray menu.</summary>
    public void AttachTrayMenu(NativeMenu? menu)
    {
        if (menu == null) return;
        _quickStartItem = new NativeMenuItem("Quick start") { Menu = new NativeMenu() };
        menu.Items.Insert(Math.Min(1, menu.Items.Count), _quickStartItem);
        RebuildQuickStartMenu();
    }

    /// <summary>One entry per preset; hidden while connected to another server as a client.</summary>
    private void RebuildQuickStartMenu()
    {
        if (_quickStartItem?.Menu is not { } submenu) return;
        submenu.Items.Clear();
        var names = _presets.List();
        foreach (var name in names)
        {
            var item = new NativeMenuItem(name);
            item.Click += (_, _) => _ = QuickStartAsync(name);
            submenu.Items.Add(item);
        }
        _quickStartItem.IsEnabled = names.Count > 0;
        _quickStartItem.IsVisible = !IsClientConnected;
    }

    partial void OnIsClientConnectedChanged(bool value) => Dispatcher.UIThread.Post(RebuildQuickStartMenu);

    /// <summary>Play a preset on every seat through the app-lifetime host (no window needed).</summary>
    private async Task QuickStartAsync(string name)
    {
        var log = AppLogger.CreateLogger<TrayViewModel>();
        if (_service.IsClientConnected) return;
        try
        {
            var config = await _presets.LoadAsync(name);
            if (!await _serverPlayback.QuickStartAsync(config))
                log.LogWarning("Quick start '{Preset}' did not start (a start was in flight, or the room has no node)", name);
        }
        catch (Exception ex) when (ex is ScenarioException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Quick start '{Preset}' failed: {Message}", name, ex.Message);
        }
    }
```

(`ScenarioException` is in `WaBiBaBuSy.Models.Testing`, already imported. If a partial method `OnIsClientConnectedChanged` already exists, merge into it.)

In `ShowWindow`: `new MainWindowViewModel(_service, _serverPlayback, _presets) { TestCoordinator = _testControl }`.

- [ ] **Step 2: App.axaml.cs** — after `DataContext = tray;`:

```csharp
            // "Quick start ▸ <presets>" is built in code: NativeMenu has no ItemsSource.
            tray.AttachTrayMenu(Avalonia.Controls.TrayIcon.GetIcons(this)?.FirstOrDefault()?.Menu);
```

(add `using System.Linq;`).

- [ ] **Step 3: MainWindowViewModel ctor** — add parameter `PresetStore? presets = null` and field `private readonly PresetStore _presets;` assigned `_presets = presets ?? new PresetStore();` before `InitSceneEditor()`. (`using WaBiBaBuSy.Core.Services.Animation;` is probably already present for `PlaylistStore`.)

- [ ] **Step 4: Build + test** — 0 errors, green. Verify `NativeMenuItem.IsVisible` / `Click` exist in Avalonia 12.1.2 (compile tells).
- [ ] **Step 5: Commit** — `git add WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs WaBiBaBuSy.UI/App.axaml.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs`, message `feat(tray): Quick start a preset on all seats; seed starter presets`.

---

### Task 8: Scene editor + Playlist preset UI

**Files:**
- Create: `WaBiBaBuSy.UI/ViewModels/PresetMenuEntry.cs`
- Create: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.Presets.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (call `InitPresets()` in ctor; unsubscribe in `Detach`)
- Modify: `WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs`
- Modify: `WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml`, `WaBiBaBuSy.UI/Views/PlaylistPanel.axaml`

**Interfaces:**
- Consumes: `_presets` (Task 7), `_storageProvider`, `SceneEditor.LoadFromConfig / BuildConfig`, `PlaylistEditor`.
- Produces: `PresetMenuEntry(string Name, ICommand Command)`; MWVM: `ScenePresets`, `HasPresets`, `PresetNameDraft`, `PresetStatus`, `SavePresetButtonText`, `SaveAsPresetCommand`, `ExportSceneCommand`, `ImportPresetCommand`; PlaylistViewModel: `PresetEntries`, `HasPresets`, `SetPresetEntries(...)`, `AddItem(CrossScreenConfig config, string? name = null)`.

- [ ] **Step 1: `PresetMenuEntry.cs`**

```csharp
using System.Windows.Input;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>A preset in a menu: its name and what clicking it does (load, add to playlist, …).</summary>
public sealed record PresetMenuEntry(string Name, ICommand Command);
```

- [ ] **Step 2: `MainWindowViewModel.Presets.cs`**

```csharp
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// Scene presets in the docked editor (Presets ▾, Save as preset…, Export…, Import…) and the Playlist tab's
/// "＋ Add ▾ → From preset". The store is the tray's, so the tray's Quick start menu follows every change.
/// </summary>
public partial class MainWindowViewModel
{
    private static readonly FilePickerFileType SceneJson = new("Scene / preset (JSON)") { Patterns = new[] { "*.json" } };

    /// <summary>Editor "Presets ▾": one entry per preset, loads it into the editor.</summary>
    public ObservableCollection<PresetMenuEntry> ScenePresets { get; } = new();

    [ObservableProperty] private bool _hasPresets;

    /// <summary>Name typed in "Save as preset…" (pre-filled with the last loaded preset).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SavePresetButtonText))]
    private string _presetNameDraft = string.Empty;

    /// <summary>Result of the last preset action, shown under the footer buttons.</summary>
    [ObservableProperty] private string _presetStatus = string.Empty;

    /// <summary>"Replace preset" when the typed name exists — the overwrite warning.</summary>
    public string SavePresetButtonText =>
        PresetNameDraft.Trim().Length > 0 && _presets.Exists(PresetNameDraft.Trim()) ? "Replace preset" : "Save preset";

    /// <summary>Called once from the constructor.</summary>
    private void InitPresets()
    {
        _presets.Changed += OnPresetsChanged;
        RefreshPresetMenus();
    }

    private void OnPresetsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_detached) RefreshPresetMenus();
    });

    private void RefreshPresetMenus()
    {
        var names = _presets.List();
        ScenePresets.Clear();
        foreach (var name in names)
            ScenePresets.Add(new PresetMenuEntry(name, new AsyncRelayCommand(() => LoadPresetAsync(name))));
        HasPresets = names.Count > 0;
        PlaylistEditor.SetPresetEntries(names.Select(name =>
            new PresetMenuEntry(name, new AsyncRelayCommand(() => AddPresetToPlaylistAsync(name)))));
        OnPropertyChanged(nameof(SavePresetButtonText));
    }

    /// <summary>Load a preset as the draft. The room selection (targets) is left as it is.</summary>
    private async Task LoadPresetAsync(string name)
    {
        try
        {
            SceneEditor.LoadFromConfig(await _presets.LoadAsync(name));
            PresetNameDraft = name;
            PresetStatus = $"Loaded preset “{name}”";
        }
        catch (Exception ex) when (ex is ScenarioException or IOException or UnauthorizedAccessException)
        {
            PresetStatus = $"Could not load “{name}”: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAsPreset()
    {
        var name = PresetNameDraft.Trim();
        if (name.Length == 0)
        {
            PresetStatus = "Type a preset name first";
            return;
        }
        try
        {
            var saved = await _presets.SaveAsync(name, SceneEditor.BuildConfig());
            PresetNameDraft = saved;
            PresetStatus = $"Saved preset “{saved}”";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PresetStatus = $"Could not save “{name}”: {ex.Message}";
        }
    }

    /// <summary>Export the draft as a JSON file plus an assets\ folder next to it (share it with the club).</summary>
    [RelayCommand]
    private async Task ExportScene()
    {
        if (_storageProvider == null) return;
        var file = await _storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export scene",
            SuggestedFileName = (PresetNameDraft.Trim().Length > 0 ? PresetStore.Sanitize(PresetNameDraft) : "scene") + ".json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { SceneJson },
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            await PresetStore.ExportAsync(SceneEditor.BuildConfig(), path);
            PresetStatus = $"Exported {Path.GetFileName(path)} (+ assets\\ next to it)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PresetStatus = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>Import a scene / exported preset (assets copied into the preset folder) and load it.</summary>
    [RelayCommand]
    private async Task ImportPreset()
    {
        if (_storageProvider == null) return;
        var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import scene or preset",
            AllowMultiple = false,
            FileTypeFilter = new[] { SceneJson },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            var name = await _presets.ImportAsync(path);
            await LoadPresetAsync(name);
            PresetStatus = $"Imported preset “{name}”";
        }
        catch (Exception ex) when (ex is ScenarioException or IOException or UnauthorizedAccessException)
        {
            PresetStatus = $"Import failed: {ex.Message}";
        }
    }

    /// <summary>Playlist "From preset": a copy of the preset as a new item, playing on all seats.</summary>
    private async Task AddPresetToPlaylistAsync(string name)
    {
        try
        {
            var config = await _presets.LoadAsync(name);
            config.SelectedMonitorIds = new();
            PlaylistEditor.AddItem(config, name);
        }
        catch (Exception ex) when (ex is ScenarioException or IOException or UnauthorizedAccessException)
        {
            PresetStatus = $"Could not add “{name}”: {ex.Message}";
        }
    }
}
```

In `MainWindowViewModel.cs`: call `InitPresets();` right after `InitSceneEditor();` in the constructor (line ~444; `PlaylistEditor` must already be assigned there — if it is created later in the ctor, call `InitPresets()` after it); in `Detach()` add `_presets.Changed -= OnPresetsChanged;`. Check the `_storageProvider` usage pattern at ~line 1584 (`TryGetLocalPath`) and match it.

- [ ] **Step 3: PlaylistViewModel.**

```csharp
    /// <summary>"＋ Add ▾ → From preset" entries (filled by the host).</summary>
    public ObservableCollection<PresetMenuEntry> PresetEntries { get; } = new();

    [ObservableProperty] private bool _hasPresets;

    /// <summary>Replace the "From preset" entries.</summary>
    public void SetPresetEntries(IEnumerable<PresetMenuEntry> entries)
    {
        PresetEntries.Clear();
        foreach (var e in entries) PresetEntries.Add(e);
        HasPresets = PresetEntries.Count > 0;
    }
```

Change `AddItem` to `public PlaylistItemRow AddItem(CrossScreenConfig config, string? name = null)` with `name ??= …existing file-name logic…`. Update the empty-list hint `"No items yet — build a scene, then ＋ Add current scene."` → `"No items yet — ＋ Add ▾ the current scene or a preset."`. Add `using System.Collections.Generic;` if needed.

- [ ] **Step 4: SceneEditorPanel.axaml** — in the footer `StackPanel`, after the existing `WrapPanel`, add:

```xml
                <WrapPanel>
                    <Button Content="Presets ▾" Margin="0,0,6,6" IsEnabled="{Binding HasPresets}"
                            ToolTip.Tip="Load a preset into the editor; the targets stay as selected in the room">
                        <Button.Flyout>
                            <MenuFlyout ItemsSource="{Binding ScenePresets}">
                                <MenuFlyout.ItemContainerTheme>
                                    <ControlTheme TargetType="MenuItem" BasedOn="{StaticResource {x:Type MenuItem}}" x:DataType="vm:PresetMenuEntry">
                                        <Setter Property="Header" Value="{Binding Name}"/>
                                        <Setter Property="Command" Value="{Binding Command}"/>
                                    </ControlTheme>
                                </MenuFlyout.ItemContainerTheme>
                            </MenuFlyout>
                        </Button.Flyout>
                    </Button>
                    <Button Content="Save as preset…" Margin="0,0,6,6"
                            ToolTip.Tip="Save this scene as a preset (without targets); gallery files are referenced, not copied">
                        <Button.Flyout>
                            <Flyout Placement="Top">
                                <StackPanel Spacing="6" Width="240">
                                    <TextBlock Text="Preset name" Classes="label"/>
                                    <TextBox Text="{Binding PresetNameDraft}" Watermark="e.g. Comet"/>
                                    <Button Content="{Binding SavePresetButtonText}" Command="{Binding SaveAsPresetCommand}"
                                            Classes="accent" HorizontalAlignment="Right"/>
                                </StackPanel>
                            </Flyout>
                        </Button.Flyout>
                    </Button>
                    <Button Content="Export…" Command="{Binding ExportSceneCommand}" Margin="0,0,6,6"
                            ToolTip.Tip="Write the scene as JSON with an assets folder next to it, e.g. to share with the club"/>
                    <Button Content="Import…" Command="{Binding ImportPresetCommand}" Margin="0,0,6,6"
                            ToolTip.Tip="Import a scene / exported preset file; its assets are copied into the preset folder"/>
                </WrapPanel>
                <TextBlock Text="{Binding PresetStatus}" Classes="hint"
                           IsVisible="{Binding PresetStatus, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
```

(If `MenuFlyout.ItemContainerTheme` does not compile in Avalonia 12, use `<MenuFlyout.Styles><Style Selector="MenuItem" x:DataType="vm:PresetMenuEntry">…</Style></MenuFlyout.Styles>` with the same two setters — check `rg -n "ItemContainerTheme|MenuFlyout ItemsSource" WaBiBaBuSy.UI` for an existing pattern first.)

- [ ] **Step 5: PlaylistPanel.axaml** — replace the `＋ Add current scene` button:

```xml
        <Button Grid.Row="4" Content="＋ Add ▾" HorizontalAlignment="Stretch" HorizontalContentAlignment="Center"
                Margin="0,6,0,0" ToolTip.Tip="Append the Scene editor's draft or a preset as a new item">
            <Button.Flyout>
                <MenuFlyout>
                    <MenuItem Header="Current scene" Command="{Binding AddCurrentSceneCommand}"/>
                    <MenuItem Header="From preset" ItemsSource="{Binding PresetEntries}" IsEnabled="{Binding HasPresets}">
                        <MenuItem.ItemContainerTheme>
                            <ControlTheme TargetType="MenuItem" BasedOn="{StaticResource {x:Type MenuItem}}" x:DataType="vm:PresetMenuEntry">
                                <Setter Property="Header" Value="{Binding Name}"/>
                                <Setter Property="Command" Value="{Binding Command}"/>
                            </ControlTheme>
                        </MenuItem.ItemContainerTheme>
                    </MenuItem>
                </MenuFlyout>
            </Button.Flyout>
        </Button>
```

- [ ] **Step 6: Build + test** — 0 errors (compiled bindings catch typos), green.
- [ ] **Step 7: Commit** — the five files plus `MainWindowViewModel.cs`, message `feat(editor): presets menu, save as preset, export / import; playlist add from preset`.

---

### Task 9: Docs, graph, final verification

**Files:**
- Modify: `.docs/RECENT_UPDATES.md`, `.docs/plans/2026-09-17-lan-party-roadmap.md` (row 2.2), `CLAUDE.md` (Key Capabilities + Last Updated), `.docs/plans/2026-10-04-manual-test-checklist.md`, `.docs/plans/2026-09-17-authoring-ux-and-preview-design.md` (status line), `.docs/plans/2026-10-03-presets-design.md` (status), `.docs/2026.07_OPEN_ITEMS.md` (if it lists 2.2 as open)
- Regenerate: `graphify-out/GRAPH_REPORT.md` via `./tools/graphify-update.ps1`

- [ ] **Step 1: RECENT_UPDATES** — new top entry `## 2026-10-03 — Scene presets, import/export, tray Quick start (roadmap 2.2)` with bullets **What** (editor Presets ▾ / Save as preset… / Export… / Import…, playlist ＋ Add ▾ → From preset, tray Quick start ▸ on all seats with the window closed, 14 starter presets seeded on first run), **Format & assets** (a preset is a scene file; save references gallery files, import/seed/export copy into `assets\` with relative paths, hash suffix on name clash — and why), **Room without a window** (`RoomNodeSources` shared by window and host, `EnsureRoom`, session gaps in the host), **Starter mapping** (design list → scenes, Logo Rain = conveyor because Endless is horizontal, new 14-ring-cat / 15-party-colors, Lunar Tears excluded), **Limits** (Quick start menu lists presets as of the last store change — files copied into the folder by hand show after restart; deleting every preset re-seeds the starters on next start; remote-client prefetch counts are still not copied into the room nodes — pre-existing), **Verified** (build + N unit tests; UI not run — see checklist).
- [ ] **Step 2: Roadmap row 2.2** → `| 2.2 | ~~**Authoring UX**~~ **Implemented 2026-10-03** — tabbed editor, validation hints, preset library (`%APPDATA%\WaBiBaBuSy\presets\`), import/export, tray Quick start, starter presets | M | — | authoring doc |`
- [ ] **Step 3: CLAUDE.md** — Key Capabilities: add after the Playlist line `- Scene presets: save / load / import / export scenes as presets (`%APPDATA%\WaBiBaBuSy\presets\`, scene-file format, assets bundled relative), add them to playlists, tray "Quick start ▸" plays one on all seats with the window closed; 14 starter presets seeded on first run from the showcase scenes`; Last Updated → 2026-10-03; Next → drop "2.2" from the roadmap list; Current Work item 3 → "2.4 crossing effects + events (rest) → 2.3 scene layers".
- [ ] **Step 4: Checklist** — new section in `2026-10-04-manual-test-checklist.md` (number after the last GUI section) "Presets & Quick start", items:
  - First start with an empty `%APPDATA%\WaBiBaBuSy\presets\`: 14 presets + `assets\` appear; Lunar Tears absent.
  - Editor Presets ▾ → "Comet": draft = comet, room selection unchanged, preview shows the trail.
  - Save as preset… new name → appears in Presets ▾, Playlist From preset and tray Quick start without restart; same name → button says "Replace preset".
  - Export… to a USB folder → `Name.json` + `assets\`; copy to the second machine (or another folder), Import… → loads, plays.
  - Import a raw showcase scene from `TestScenarios\showcase\scenes\` → assets copied into `presets\assets\`.
  - Playlist ＋ Add ▾ → From preset → item named after the preset, plays on all seats.
  - Window **closed** after app restart (never opened): tray Quick start ▸ Mexican Wave → plays on all local monitors; with the server started earlier and a client connected: plays on the client too.
  - Quick start while a show runs → show stops, preset plays; open the window → toolbar shows the scene running, room marks the nodes.
  - Connected as a client: Quick start is hidden.
  - A hand-broken preset JSON: Presets ▾ shows it, loading prints "Could not load …" in the status line; Quick start logs a warning, nothing crashes.
  - Ring Cat with the room set to Ring: the cat faces travel direction and wraps seamlessly.
- [ ] **Step 5: Status lines** — authoring design header: "§4 presets implemented 2026-10-03"; presets design: "Status: implemented 2026-10-03 (UI manual checks pending)"; OPEN_ITEMS: move 2.2 to done if listed.
- [ ] **Step 6: Final verification** — `dotnet build WaBiBaBuSy.sln` (0 errors), `dotnet test WaBiBaBuSy.Tests` (record the count), then `./tools/graphify-update.ps1`.
- [ ] **Step 7: Commit** — docs + `graphify-out/GRAPH_REPORT.md`, message `docs: presets, import/export and tray quick start (roadmap 2.2 done)`.

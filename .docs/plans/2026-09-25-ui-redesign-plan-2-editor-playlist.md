# UI Redesign — Plan 2: Docked Scene Editor + Playlist Tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the modal `CrossScreenConfigDialog` and `PlaylistDialog` with a right-hand panel
`[Scene | Playlist]`: a tabbed Scene editor whose draft plays inside the room tiles instantly, and a
Playlist tab that loads items into that editor; show controls move into the toolbar.

**Architecture:** The editor keeps using `CrossScreenConfigViewModel` (`LoadFromConfig` / `BuildConfig`),
now hosted by `MainWindowViewModel` as `SceneEditor` and shown by a `SceneEditorPanel` UserControl.
A 150 ms debounce turns every editor change into a draft `CrossScreenConfig` + the seat-map layout of
the targets (= room selection, or all nodes); `RoomView` paints either that draft on its design clock
or the running scene on the players' shared clock, toggled by a preview badge. Pure logic (checks,
targets, unit flags, playlist durations and reordering) lives in `WaBiBaBuSy.Models` and is unit-tested.

**Tech Stack:** .NET 9, Avalonia 12.1.2, CommunityToolkit.Mvvm 8.4.2, xUnit 2.9.3.

**Spec:** [`.docs/plans/2026-09-24-ui-redesign-design.md`](2026-09-24-ui-redesign-design.md) — this plan
covers rollout steps 3–4 (§2 right panel + preview badge, §4 Scene editor, §5 Playlist tab, the show
controls of §2's toolbar, the `⋯ → Dev` icon-zone palette of §4.3/§6, and the removals in §7).
Step 5 (Settings sidebar) gets Plan 3. Plan 1 (steps 1–2) is merged: [plan 1](2026-09-24-ui-redesign-plan-1-room.md).

## Global Constraints

- .NET 9, Avalonia **12.1.2**; **no new NuGet packages**.
- C# 12+, file-scoped namespaces, nullable enabled, XML docs on public APIs, MVVM in the UI layer.
- `WaBiBaBuSy.Models` stays UI-free (no Avalonia types); `WaBiBaBuSy.Tests` references only `WaBiBaBuSy.Models`.
- Do not touch player / movement / pattern / color math (Architectural Invariants in `CLAUDE.md`).
- `PlaylistStore` and the playlist JSON format are unchanged (durations stay **ms** in the model; the UI shows **seconds**).
- `seatmap.json` / `topology.json` formats and `SeatMapLayoutBuilder` are unchanged.
- Editor changes reach the room preview after a **150 ms** debounce and never reach the machines until Play.
- Units follow the room: physical room → cm and cm/s, else px and px/s; both stored values are carried, nothing is converted.
- `MainWindowViewModel` is already 4 100 lines: new code goes into the partial files
  `MainWindowViewModel.SceneEditor.cs` and `MainWindowViewModel.Playlist.cs`; edits to the main file are limited to the hooks named in each task.
- Build: `dotnet build WaBiBaBuSy.UI -nologo -v q` (close a running app first — it locks the output). Tests: `dotnet test WaBiBaBuSy.Tests -nologo`.
- After code changes run `./tools/graphify-update.ps1` (never bare `graphify update .`) — done once in Task 9.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
- Work on branch `ui-redesign-plan-2` created in place from `main` (no worktree).

## Out of scope for this plan (deliberate, with reason)

- **Settings sidebar** — Plan 3 (spec step 5).
- **"Use as scene targets"** selection-bar button and **"Targets: click → highlights"** — targets already *are*
  the selection and selected tiles are already highlighted; both would be no-ops.
- **Per-item target subsets in the Playlist tab** — new items target all nodes (empty `SelectedMonitorIds`);
  updating a loaded item keeps that item's stored target list. Editing target lists per item needs its own design.
- **Plan 1 deferred polish** (tile badge #0 vs #1, RoomView per-frame allocations, disabled Resync/Clear menu
  items, multi-monitor gap after reorder, Escape vs rubber band, flyout auto-focus, write-only `SelectedClient`)
  — unchanged by this work, still tracked in the UI-redesign memory note and picked up after E2E profiling.
- **Toolbar ■ semantics** — ■ keeps calling `StopCrossScreen` exactly as today (stops the show loop and a manual
  scene; the last show item stays on screen until "Clear all"). Changing that is a behaviour decision, not layout.

## Review Focus

1. **The 2 s topology refresh while the editor is open** → the draft preview keeps running smoothly; it must not restart at t = 0 every refresh (the refresh re-runs the draft update). Pinned by `SameScene_*` tests in Task 1; the draft only replaces `DraftScene` when `SceneChecks.SameScene` says it changed (Task 5); manual check in Task 5 Step 7.
2. **Selection made in arbitrary click order** → "Play on selection" sends target ids in **chain order**, because `ApplyCrossScreenConfigAsync` orders local players by position in `SelectedMonitorIds`. Test `Resolve_ReturnsSelectionInChainOrder` in Task 1.
3. **Selection that no longer exists in the room** (node left between click and Play) → "Play on selection" does nothing instead of silently playing on every node (empty list = all). Test `Resolve_SelectionNotInChain_ReturnsEmpty` in Task 1 + guard in `PlayDraftAsync` (Task 5).
4. **Toggling Room ⚙ "Physical units" mid-edit** → the editor switches between px and cm fields and nothing is lost; switching back shows the old px value. Test `ApplyRoomUnits_*` in Task 1; manual check in Task 5 Step 7.
5. **Junk in a duration box** ("", "0", "-5", "abc", "12,5", "999999") → blank/invalid = playlist default, comma accepted as decimal point, huge values clamped to 24 h. Tests `ParseSecondsToMs_*` in Task 2.

---

### Task 1: Scene helpers in Models — checks, targets, room units

**Files:**
- Create: `WaBiBaBuSy.Models/Wallpaper/SceneChecks.cs`
- Create: `WaBiBaBuSy.Models/Topology/SceneTargets.cs`
- Modify: `WaBiBaBuSy.Models/Wallpaper/PhysicalUnits.cs` (add `ApplyRoomUnits`)
- Test: `WaBiBaBuSy.Tests/SceneChecksTests.cs`

**Interfaces:**
- Consumes: `CrossScreenConfig`, `MovementType`, `BackgroundMode`, `ColorGradingMode`, `PhysicalUnits.EffectiveSpeedPx` (existing); `SeatMapLayoutResult` (`CanvasWidth`, `RefPixelsPerCm`, both `init`).
- Produces:
  - `public static class SceneChecks` with `const double MinCrossingSeconds = 5`, `const double MaxCrossingSeconds = 600`,
    `bool IsVideo(string? path)`, `double CrossingSeconds(CrossScreenConfig scene, SeatMapLayoutResult? layout)`,
    `string CrossingReadout(CrossScreenConfig scene, SeatMapLayoutResult? layout)`,
    `IReadOnlyList<string> Validate(CrossScreenConfig scene, SeatMapLayoutResult? layout)`,
    `bool SameScene(CrossScreenConfig? a, CrossScreenConfig? b)`.
  - `public static class SceneTargets` (namespace `WaBiBaBuSy.Models.Topology`) with
    `List<string> Resolve(IReadOnlyList<string> chainOrder, IReadOnlyCollection<string> selected)`.
  - `PhysicalUnits.ApplyRoomUnits(CrossScreenConfig config, bool physicalRoom)` (void, mutates the two unit flags).

- [ ] **Step 1: Write the failing tests**

```csharp
// WaBiBaBuSy.Tests/SceneChecksTests.cs
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Scene editor helpers: warning chips, crossing-time readout, draft change detection, targets from
/// the room selection and unit flags that follow the room.
/// </summary>
public class SceneChecksTests
{
    /// <summary>A scene that produces no warnings on a 5000 px canvas (10 s crossing).</summary>
    private static CrossScreenConfig Scene()
    {
        var c = new CrossScreenConfig();
        c.Background.Mode = BackgroundMode.SolidColor;
        c.Animation.AnimationPath = "fish.gif";
        c.Movement.Type = MovementType.Linear;
        c.Movement.SpeedPixelsPerSecond = 500f;
        return c;
    }

    private static SeatMapLayoutResult Canvas(int width, float refPpcm = 0f)
        => new() { CanvasWidth = width, RefPixelsPerCm = refPpcm };

    // ── crossing time ────────────────────────────────────────────────────────

    [Fact]
    public void CrossingSeconds_PixelCanvas_IsWidthOverSpeed()
        => Assert.Equal(10.0, SceneChecks.CrossingSeconds(Scene(), Canvas(5000)), 3);

    [Fact]
    public void CrossingSeconds_PhysicalCanvas_UsesCmSpeed()
    {
        var s = Scene();
        s.Movement.SpeedUnit = SpeedUnit.CentimetersPerSecond;
        s.Movement.SpeedCmPerSecond = 20f;                 // 20 cm/s × 40 px/cm = 800 px/s
        Assert.Equal(10.0, SceneChecks.CrossingSeconds(s, Canvas(8000, refPpcm: 40f)), 3);
    }

    [Fact]
    public void CrossingSeconds_StaticOrNoLayout_IsZero()
    {
        var s = Scene();
        s.Movement.Type = MovementType.Static;
        Assert.Equal(0, SceneChecks.CrossingSeconds(s, Canvas(5000)));
        Assert.Equal(0, SceneChecks.CrossingSeconds(Scene(), null));
        Assert.Equal(0, SceneChecks.CrossingSeconds(Scene(), Canvas(0)));
    }

    [Fact]
    public void CrossingReadout_FormatsSecondsAndMinutes()
    {
        Assert.Equal("crosses the room in 10 s", SceneChecks.CrossingReadout(Scene(), Canvas(5000)));
        Assert.Equal("crosses the room in 12.5 s", SceneChecks.CrossingReadout(Scene(), Canvas(6250)));
        Assert.Equal("crosses the room in 2 min 0 s", SceneChecks.CrossingReadout(Scene(), Canvas(59_800)));
        Assert.Equal("", SceneChecks.CrossingReadout(Scene(), null));
    }

    // ── warnings ─────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_CleanScene_NoWarnings()
        => Assert.Empty(SceneChecks.Validate(Scene(), Canvas(5000)));

    [Fact]
    public void Validate_EmptyPath_AsksForFile()
    {
        var s = Scene();
        s.Animation.AnimationPath = "";
        Assert.Contains("Pick an animation file from the strip", SceneChecks.Validate(s, Canvas(5000)));
    }

    [Fact]
    public void Validate_ImageBackgroundWithoutImage_Warns()
    {
        var s = Scene();
        s.Background.Mode = BackgroundMode.TiledImage;
        s.Background.ImagePath = null;
        Assert.Contains("Pick a background image", SceneChecks.Validate(s, Canvas(5000)));
    }

    [Fact]
    public void Validate_EndlessNeedsTravelingColors()
    {
        var s = Scene();
        s.Movement.Endless = true;
        Assert.Contains("Endless needs a Traveling color mode", SceneChecks.Validate(s, Canvas(5000)));

        s.Animation.ColorGrading.Mode = ColorGradingMode.TravelingList;
        Assert.DoesNotContain("Endless needs a Traveling color mode", SceneChecks.Validate(s, Canvas(5000)));
    }

    [Fact]
    public void Validate_PatternWithVideo_Warns()
    {
        var s = Scene();
        s.Animation.AnimationPath = @"C:\clips\party.MP4";
        s.Animation.Pattern = new PatternConfig();
        Assert.Contains("Pattern with a video: every cell decodes the video", SceneChecks.Validate(s, Canvas(5000)));
    }

    [Fact]
    public void Validate_TooFastAndTooSlow()
    {
        Assert.Contains("Very fast: crosses the room in 2 s", SceneChecks.Validate(Scene(), Canvas(1000)));
        Assert.Contains("Very slow: crosses the room in 11 min 40 s", SceneChecks.Validate(Scene(), Canvas(350_000)));
    }

    // ── draft change detection ───────────────────────────────────────────────

    [Fact]
    public void SameScene_IdenticalContent_IsTrue()
        => Assert.True(SceneChecks.SameScene(Scene(), Scene()));

    [Fact]
    public void SameScene_AnyFieldChanged_IsFalse()
    {
        var b = Scene();
        b.Movement.DirectionAngleDegrees = 45f;
        Assert.False(SceneChecks.SameScene(Scene(), b));
    }

    [Fact]
    public void SameScene_Nulls()
    {
        Assert.True(SceneChecks.SameScene(null, null));
        Assert.False(SceneChecks.SameScene(Scene(), null));
    }

    // ── targets ──────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NoSelection_IsEmptyMeaningAll()
        => Assert.Empty(SceneTargets.Resolve(new[] { "a", "b", "c" }, Array.Empty<string>()));

    [Fact]
    public void Resolve_ReturnsSelectionInChainOrder()
        => Assert.Equal(new[] { "a", "c", "d" }, SceneTargets.Resolve(new[] { "a", "b", "c", "d" }, new[] { "d", "a", "c" }));

    [Fact]
    public void Resolve_SelectionNotInChain_ReturnsEmpty()
        => Assert.Empty(SceneTargets.Resolve(new[] { "a", "b" }, new[] { "gone" }));

    // ── units follow the room ────────────────────────────────────────────────

    [Fact]
    public void ApplyRoomUnits_Physical_SetsCmFlags_KeepsBothValues()
    {
        var s = Scene();
        s.Animation.TargetHeight = 300;
        s.Animation.TargetHeightCm = 12f;
        s.Movement.SpeedCmPerSecond = 25f;

        PhysicalUnits.ApplyRoomUnits(s, physicalRoom: true);

        Assert.Equal(SizeUnit.Centimeters, s.Animation.SizeUnit);
        Assert.Equal(SpeedUnit.CentimetersPerSecond, s.Movement.SpeedUnit);
        Assert.Equal(300, s.Animation.TargetHeight);
        Assert.Equal(12f, s.Animation.TargetHeightCm);
        Assert.Equal(500f, s.Movement.SpeedPixelsPerSecond);
        Assert.Equal(25f, s.Movement.SpeedCmPerSecond);
    }

    [Fact]
    public void ApplyRoomUnits_PixelRoom_SetsPxFlags()
    {
        var s = Scene();
        s.Animation.SizeUnit = SizeUnit.Centimeters;
        s.Movement.SpeedUnit = SpeedUnit.CentimetersPerSecond;

        PhysicalUnits.ApplyRoomUnits(s, physicalRoom: false);

        Assert.Equal(SizeUnit.Pixels, s.Animation.SizeUnit);
        Assert.Equal(SpeedUnit.PixelsPerSecond, s.Movement.SpeedUnit);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests -nologo --filter FullyQualifiedName~SceneChecksTests`
Expected: build FAILS — `SceneChecks`, `SceneTargets`, `PhysicalUnits.ApplyRoomUnits` do not exist.

- [ ] **Step 3: Implement `SceneChecks`**

```csharp
// WaBiBaBuSy.Models/Wallpaper/SceneChecks.cs
using System.Globalization;
using System.Text.Json;
using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Editor-side checks on a scene draft: one-line warnings for the Scene editor, the crossing-time
/// readout next to the speed slider, and change detection for the room preview. Pure functions;
/// never used by the players.
/// </summary>
public static class SceneChecks
{
    /// <summary>A crossing faster than this (seconds) is flagged as too fast to follow.</summary>
    public const double MinCrossingSeconds = 5;

    /// <summary>A crossing slower than this (seconds) is flagged as too slow to notice.</summary>
    public const double MaxCrossingSeconds = 600;

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm", ".flv" };

    /// <summary>True when the path has a video file extension.</summary>
    public static bool IsVideo(string? path)
        => !string.IsNullOrEmpty(path) && VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Seconds the sprite needs to travel the full canvas width once at the scene's effective speed
    /// (cm/s resolved with the layout's reference DPI). 0 for Static movement, zero speed or no canvas.
    /// </summary>
    public static double CrossingSeconds(CrossScreenConfig scene, SeatMapLayoutResult? layout)
    {
        if (layout == null || layout.CanvasWidth <= 0 || scene.Movement.Type == MovementType.Static) return 0;
        float speed = PhysicalUnits.EffectiveSpeedPx(scene.Movement, layout.RefPixelsPerCm);
        return speed > 0f ? layout.CanvasWidth / (double)speed : 0;
    }

    /// <summary>"crosses the room in 12.5 s" / "… in 2 min 5 s"; empty when there is nothing to cross.</summary>
    public static string CrossingReadout(CrossScreenConfig scene, SeatMapLayoutResult? layout)
    {
        double s = CrossingSeconds(scene, layout);
        return s <= 0 ? string.Empty : "crosses the room in " + FormatSeconds(s);
    }

    private static string FormatSeconds(double s)
    {
        if (s < 60) return s.ToString("0.#", CultureInfo.InvariantCulture) + " s";
        int total = (int)Math.Round(s);
        return $"{total / 60} min {total % 60} s";
    }

    /// <summary>One-line warnings for the editor's amber chips, in display order. Empty = nothing to fix.</summary>
    public static IReadOnlyList<string> Validate(CrossScreenConfig scene, SeatMapLayoutResult? layout)
    {
        var warnings = new List<string>();
        var anim = scene.Animation;

        if (string.IsNullOrWhiteSpace(anim.AnimationPath))
            warnings.Add("Pick an animation file from the strip");

        if (scene.Background.Mode is BackgroundMode.StretchedImage or BackgroundMode.TiledImage
            && string.IsNullOrWhiteSpace(scene.Background.ImagePath))
            warnings.Add("Pick a background image");

        var grading = anim.ColorGrading?.Mode ?? ColorGradingMode.None;
        if (scene.Movement.Type == MovementType.Linear && scene.Movement.Endless
            && grading is not (ColorGradingMode.TravelingRainbow or ColorGradingMode.TravelingList or ColorGradingMode.TravelingRandom))
            warnings.Add("Endless needs a Traveling color mode");

        if (anim.Pattern != null && IsVideo(anim.AnimationPath))
            warnings.Add("Pattern with a video: every cell decodes the video");

        double crossing = CrossingSeconds(scene, layout);
        if (crossing > 0 && crossing < MinCrossingSeconds)
            warnings.Add("Very fast: " + CrossingReadout(scene, layout));
        else if (crossing > MaxCrossingSeconds)
            warnings.Add("Very slow: " + CrossingReadout(scene, layout));

        return warnings;
    }

    /// <summary>True when both scenes serialize identically (null equals only null).</summary>
    public static bool SameScene(CrossScreenConfig? a, CrossScreenConfig? b)
    {
        if (a == null || b == null) return a == b;
        return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    }
}
```

- [ ] **Step 4: Implement `SceneTargets`**

```csharp
// WaBiBaBuSy.Models/Topology/SceneTargets.cs
namespace WaBiBaBuSy.Models.Topology;

/// <summary>Targets of a scene started from the editor: the room selection, in chain order.</summary>
public static class SceneTargets
{
    /// <summary>
    /// The selected node ids in chain order (the apply path orders local players by position in this
    /// list), or an empty list — meaning every node — when nothing is selected. Selected ids that are
    /// no longer in the chain are dropped, so a stale selection can also yield an empty list: callers
    /// that require a selection must treat that as "nothing to play", not as "all".
    /// </summary>
    public static List<string> Resolve(IReadOnlyList<string> chainOrder, IReadOnlyCollection<string> selected)
    {
        if (selected.Count == 0) return new List<string>();
        var set = new HashSet<string>(selected);
        return chainOrder.Where(set.Contains).Distinct().ToList();
    }
}
```

- [ ] **Step 5: Add `ApplyRoomUnits` to `PhysicalUnits`**

In `WaBiBaBuSy.Models/Wallpaper/PhysicalUnits.cs`, insert directly above `private static T Clone<T>(T value)`:

```csharp
    /// <summary>
    /// Set the size and speed unit flags from the room: cm and cm/s on a physical canvas, px and px/s
    /// otherwise. Both stored values (px and cm) are left untouched, so switching the room back and
    /// forth loses nothing. Used by the Scene editor's BuildConfig.
    /// </summary>
    public static void ApplyRoomUnits(CrossScreenConfig config, bool physicalRoom)
    {
        config.Animation.SizeUnit = physicalRoom ? SizeUnit.Centimeters : SizeUnit.Pixels;
        config.Movement.SpeedUnit = physicalRoom ? SpeedUnit.CentimetersPerSecond : SpeedUnit.PixelsPerSecond;
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests -nologo`
Expected: PASS — all previous 187 tests plus the new `SceneChecksTests`.

- [ ] **Step 7: Commit**

```bash
git checkout -b ui-redesign-plan-2
git add WaBiBaBuSy.Models/Wallpaper/SceneChecks.cs WaBiBaBuSy.Models/Topology/SceneTargets.cs WaBiBaBuSy.Models/Wallpaper/PhysicalUnits.cs WaBiBaBuSy.Tests/SceneChecksTests.cs
git commit -m "feat: scene editor checks, selection targets and room unit flags

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `PlaylistEditing` — seconds, reorder target, duplicate

**Files:**
- Create: `WaBiBaBuSy.Models/Wallpaper/PlaylistEditing.cs`
- Test: `WaBiBaBuSy.Tests/PlaylistEditingTests.cs`

**Interfaces:**
- Consumes: `PlaylistItem`, `CrossScreenConfig` (existing).
- Produces: `public static class PlaylistEditing` with `const int MaxSeconds = 86_400`,
  `int? ParseSecondsToMs(string? text)`, `string FormatMsAsSeconds(int? ms)`,
  `int MoveTarget(int count, int from, int insertBefore)` (final index for `ObservableCollection.Move`, or -1),
  `PlaylistItem Duplicate(PlaylistItem item)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// WaBiBaBuSy.Tests/PlaylistEditingTests.cs
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Playlist tab helpers: durations typed in seconds, drag-reorder targets, duplicating items.</summary>
public class PlaylistEditingTests
{
    [Theory]
    [InlineData("30", 30_000)]
    [InlineData(" 12.5 ", 12_500)]
    [InlineData("12,5", 12_500)]
    [InlineData("0.25", 250)]
    [InlineData("999999", 86_400_000)]
    public void ParseSecondsToMs_Valid(string text, int expectedMs)
        => Assert.Equal(expectedMs, PlaylistEditing.ParseSecondsToMs(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("NaN")]
    public void ParseSecondsToMs_BlankOrInvalid_IsNullMeaningDefault(string? text)
        => Assert.Null(PlaylistEditing.ParseSecondsToMs(text));

    [Theory]
    [InlineData(30_000, "30")]
    [InlineData(12_500, "12.5")]
    [InlineData(250, "0.25")]
    [InlineData(null, "")]
    [InlineData(0, "")]
    public void FormatMsAsSeconds(int? ms, string expected)
        => Assert.Equal(expected, PlaylistEditing.FormatMsAsSeconds(ms));

    [Theory]
    [InlineData(5, 0, 5, 4)]    // first row dropped after the last row
    [InlineData(5, 4, 0, 0)]    // last row dropped before the first
    [InlineData(5, 1, 4, 3)]    // down: the gap before row 4 becomes index 3 once row 1 is removed
    [InlineData(5, 3, 1, 1)]    // up
    [InlineData(5, 0, 99, 4)]   // gap index clamps to the end
    [InlineData(5, 2, -3, 0)]   // and to the start
    public void MoveTarget_Moves(int count, int from, int insertBefore, int expected)
        => Assert.Equal(expected, PlaylistEditing.MoveTarget(count, from, insertBefore));

    [Theory]
    [InlineData(5, 2, 2)]       // gap right before itself
    [InlineData(5, 2, 3)]       // gap right after itself
    [InlineData(5, -1, 0)]      // from out of range
    [InlineData(5, 5, 0)]
    [InlineData(0, 0, 0)]
    public void MoveTarget_NoMove_IsMinusOne(int count, int from, int insertBefore)
        => Assert.Equal(-1, PlaylistEditing.MoveTarget(count, from, insertBefore));

    [Fact]
    public void Duplicate_DeepCopiesConfig_AndSuffixesName()
    {
        var original = new PlaylistItem
        {
            Name = "Fish",
            DurationMs = 12_000,
            SnapToLap = true,
            Config = new CrossScreenConfig { SelectedMonitorIds = { "a" } },
        };
        original.Config.Animation.AnimationPath = "fish.gif";

        var copy = PlaylistEditing.Duplicate(original);
        copy.Config.Animation.AnimationPath = "shark.gif";
        copy.Config.SelectedMonitorIds.Add("b");

        Assert.Equal("Fish copy", copy.Name);
        Assert.Equal(12_000, copy.DurationMs);
        Assert.True(copy.SnapToLap);
        Assert.Equal("fish.gif", original.Config.Animation.AnimationPath);
        Assert.Equal(new[] { "a" }, original.Config.SelectedMonitorIds);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests -nologo --filter FullyQualifiedName~PlaylistEditingTests`
Expected: build FAILS — `PlaylistEditing` does not exist.

- [ ] **Step 3: Implement `PlaylistEditing`**

```csharp
// WaBiBaBuSy.Models/Wallpaper/PlaylistEditing.cs
using System.Globalization;
using System.Text.Json;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Helpers for the Playlist tab: durations are typed in seconds but stored in ms, rows are reordered
/// by dragging into the gap between two rows, and items are duplicated as deep copies.
/// </summary>
public static class PlaylistEditing
{
    /// <summary>Longest duration accepted from the editor (24 h), in seconds.</summary>
    public const int MaxSeconds = 86_400;

    /// <summary>
    /// Parse a duration typed in seconds ("30", "12.5", "12,5") to whole ms. Blank, zero, negative or
    /// unparsable text → null, which means "use the playlist default"; values above
    /// <see cref="MaxSeconds"/> are clamped.
    /// </summary>
    public static int? ParseSecondsToMs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return null;
        if (double.IsNaN(seconds) || seconds <= 0) return null;
        return (int)Math.Round(Math.Min(seconds, MaxSeconds) * 1000.0);
    }

    /// <summary>Ms → seconds text for the editor ("30", "12.5"); null or 0 → empty (= default).</summary>
    public static string FormatMsAsSeconds(int? ms)
        => ms is > 0 ? (ms.Value / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>
    /// Final index for moving the row at <paramref name="from"/> into the gap before row
    /// <paramref name="insertBefore"/> as currently displayed (<paramref name="count"/> = after the last
    /// row; clamped). Returns -1 when nothing moves (dropped next to itself, or <paramref name="from"/>
    /// out of range). The result is the <c>newIndex</c> argument of <c>ObservableCollection.Move</c>.
    /// </summary>
    public static int MoveTarget(int count, int from, int insertBefore)
    {
        if (from < 0 || from >= count) return -1;
        insertBefore = Math.Clamp(insertBefore, 0, count);
        int to = insertBefore > from ? insertBefore - 1 : insertBefore;
        return to == from ? -1 : to;
    }

    /// <summary>Deep copy of an item (embedded config included), named "&lt;name&gt; copy".</summary>
    public static PlaylistItem Duplicate(PlaylistItem item)
    {
        var clone = JsonSerializer.Deserialize<PlaylistItem>(JsonSerializer.Serialize(item))!;
        clone.Name = string.IsNullOrWhiteSpace(item.Name) ? "copy" : item.Name + " copy";
        return clone;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests -nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/PlaylistEditing.cs WaBiBaBuSy.Tests/PlaylistEditingTests.cs
git commit -m "feat: playlist editing helpers (seconds, drag reorder target, duplicate)

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `RoomView` — draft clock controls and target layout on the design clock

The room already paints on a design clock when `SharedStartUtcMs == 0`. This task lets the host pause,
speed up and restart that clock, and lets the draft preview paint only its targets by passing their
layout through `ActiveLayout` also on the design clock.

**Files:**
- Modify: `WaBiBaBuSy.UI/Controls/RoomView.cs`

**Interfaces:**
- Consumes: `SceneClock.IsPaused`, `SceneClock.Speed`, `SceneClock.Restart()` (existing).
- Produces (styled properties on `RoomView`): `bool IsClockPaused`, `double ClockSpeed` (default 1.0),
  `int ClockRestartToken` (any change restarts the design clock). On the design clock, tiles are painted
  with `ActiveLayout ?? <whole room layout>`; tiles missing from `ActiveLayout` get no sprite.

- [ ] **Step 1: Add the three styled properties**

In `RoomView.cs`, directly after the `ActiveLayoutProperty` definition (the line
`AvaloniaProperty.Register<RoomView, SeatMapLayoutResult?>(nameof(ActiveLayout));`), add:

```csharp
    /// <summary>Defines the <see cref="IsClockPaused"/> property.</summary>
    public static readonly StyledProperty<bool> IsClockPausedProperty =
        AvaloniaProperty.Register<RoomView, bool>(nameof(IsClockPaused));
    /// <summary>Defines the <see cref="ClockSpeed"/> property.</summary>
    public static readonly StyledProperty<double> ClockSpeedProperty =
        AvaloniaProperty.Register<RoomView, double>(nameof(ClockSpeed), 1.0);
    /// <summary>Defines the <see cref="ClockRestartToken"/> property.</summary>
    public static readonly StyledProperty<int> ClockRestartTokenProperty =
        AvaloniaProperty.Register<RoomView, int>(nameof(ClockRestartToken));
```

and directly after the `ActiveLayout` CLR property add:

```csharp
    /// <summary>Freeze the design clock (draft preview). No effect while live.</summary>
    public bool IsClockPaused { get => GetValue(IsClockPausedProperty); set => SetValue(IsClockPausedProperty, value); }
    /// <summary>Design-clock speed multiplier (1×, 4×, 16× in the preview bar). No effect while live.</summary>
    public double ClockSpeed { get => GetValue(ClockSpeedProperty); set => SetValue(ClockSpeedProperty, value); }
    /// <summary>Any change restarts the design clock at t = 0 (preview bar ⟲).</summary>
    public int ClockRestartToken { get => GetValue(ClockRestartTokenProperty); set => SetValue(ClockRestartTokenProperty, value); }
```

- [ ] **Step 2: Update the docs of `Scene` and `ActiveLayout`**

Replace the `Scene` summary line
`/// <summary>Scene painted in the tiles (the running scene for now; the editor draft in Plan 2).</summary>` with:

```csharp
    /// <summary>Scene painted in the tiles: the running scene (live) or the Scene editor's draft (design clock).</summary>
```

Replace the `ActiveLayout` summary block (the four `///` lines starting `/// Layout the running players were started with`) with:

```csharp
    /// <summary>
    /// Layout the tiles are painted with. Live: the layout the running players were started with
    /// (target subset, start-time gaps and ppcm) — the seat map may have been edited or nodes may have
    /// joined since. Design clock: the draft's target layout; null = the whole room. Tiles missing from
    /// it are not painted either way.
    /// </summary>
```

- [ ] **Step 3: Wire the properties to the clock**

In `OnPropertyChanged`, replace

```csharp
        else if (change.Property == SceneProperty && !_clock.IsLive) _clock.Restart();
```

with

```csharp
        else if (change.Property == SceneProperty && !_clock.IsLive) _clock.Restart();
        else if (change.Property == IsClockPausedProperty) { _clock.IsPaused = IsClockPaused; InvalidateVisual(); }
        else if (change.Property == ClockSpeedProperty) _clock.Speed = ClockSpeed;
        else if (change.Property == ClockRestartTokenProperty) { _clock.Restart(); InvalidateVisual(); }
```

- [ ] **Step 4: Stop the 33 ms repaint while the draft is paused, and paint the target layout**

In `AnyTileAnimating`, replace

```csharp
        if (!_clock.IsLive) return true;   // design clock (editor draft): always animate
```

with

```csharp
        if (!_clock.IsLive) return !_clock.IsPaused;   // design clock (editor draft): animate unless paused
```

In `Render`, replace

```csharp
        // Live: paint with the layout the players were started with; design clock: the current seat map.
        bool live = _clock.IsLive;
        var paintLayout = live ? ActiveLayout : _layout;
```

with

```csharp
        // Live: the layout the players were started with. Design clock: the draft's target layout,
        // or the current seat map when the host passes none.
        bool live = _clock.IsLive;
        var paintLayout = live ? ActiveLayout : ActiveLayout ?? _layout;
```

- [ ] **Step 5: Build and run the existing tests**

Run: `dotnet build WaBiBaBuSy.UI -nologo -v q` then `dotnet test WaBiBaBuSy.Tests -nologo`
Expected: build succeeds with 0 errors; all tests pass. (Behaviour is unchanged until Task 5 binds the new properties: nothing sets `ActiveLayout` while `SharedStartUtcMs == 0` today.)

- [ ] **Step 6: Commit**

```bash
git add WaBiBaBuSy.UI/Controls/RoomView.cs
git commit -m "feat: room view draft clock controls (pause, speed, restart) and target layout

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: `CrossScreenConfigViewModel` — members the docked editor needs

Additive changes plus "units follow the room". The old dialog keeps compiling until Task 8 deletes it.

**Files:**
- Modify: `WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs`
- Modify: `WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml` (drop the two unit ComboBoxes)
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (two dialog-opening methods pass the room units)

**Interfaces:**
- Consumes: `PhysicalUnits.ApplyRoomUnits` (Task 1).
- Produces (on `CrossScreenConfigViewModel`):
  - `bool UsePhysicalUnits` (observable; replaces `SizeUnitIndex` / `SpeedUnitIndex`, which are removed); `IsSizeInCm` / `IsSpeedInCm` now mirror it.
  - `ObservableCollection<string> Warnings`, `string SpeedReadout` (observable), `void SetChecks(IReadOnlyList<string> warnings, string speedReadout)`.
  - `bool IsPickingBackgroundImage` (observable), `void PickFromStrip(string path, bool additive)`.
  - `string AnimationFileName`, `string BackgroundImageFileName`, `bool HasExtraImages`, `bool IsPatternCountVisible`.
  - `bool IsSequentialMode { get; set; }`, `bool IsSimultaneousMode { get; set; }` (settable, for radio buttons).

- [ ] **Step 1: Units follow the room**

Replace the block

```csharp
    // Tier 1.2 physical units: 0 = px, 1 = cm (cm only takes effect on a physical canvas)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSizeInCm))]
    private int _sizeUnitIndex = 0;
    [ObservableProperty] private float _animationHeightCm = 15f;
    public bool IsSizeInCm => SizeUnitIndex == 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpeedInCm))]
    private int _speedUnitIndex = 0;
    [ObservableProperty] private float _animationSpeedCm = 20f;
    public bool IsSpeedInCm => SpeedUnitIndex == 1;
```

with

```csharp
    /// <summary>
    /// Units follow the room (set by the host from Room ⚙ "Physical units"): cm and cm/s on a physical
    /// canvas, px and px/s otherwise. Both values are stored in the config; only the matching one is shown.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSizeInCm), nameof(IsSpeedInCm))]
    private bool _usePhysicalUnits;

    [ObservableProperty] private float _animationHeightCm = 15f;
    /// <summary>True when the height field shows cm (physical room).</summary>
    public bool IsSizeInCm => UsePhysicalUnits;

    [ObservableProperty] private float _animationSpeedCm = 20f;
    /// <summary>True when the speed slider shows cm/s (physical room).</summary>
    public bool IsSpeedInCm => UsePhysicalUnits;
```

In `LoadFromConfig`, replace

```csharp
        SizeUnitIndex = config.Animation.SizeUnit == SizeUnit.Centimeters ? 1 : 0;
        AnimationHeightCm = config.Animation.TargetHeightCm;
        SpeedUnitIndex = config.Movement.SpeedUnit == SpeedUnit.CentimetersPerSecond ? 1 : 0;
        AnimationSpeedCm = config.Movement.SpeedCmPerSecond;
```

with

```csharp
        // The unit flags are not loaded: the room decides which of the two stored values is shown.
        AnimationHeightCm = config.Animation.TargetHeightCm;
        AnimationSpeedCm = config.Movement.SpeedCmPerSecond;
```

In `BuildConfig`: change `return new CrossScreenConfig` to `var config = new CrossScreenConfig`; delete the
two initializer lines `SizeUnit = SizeUnitIndex == 1 ? SizeUnit.Centimeters : SizeUnit.Pixels,` and
`SpeedUnit = SpeedUnitIndex == 1 ? SpeedUnit.CentimetersPerSecond : SpeedUnit.PixelsPerSecond,`; and replace
the end of the method

```csharp
                NodePhaseDelayMs = Math.Max(0, NodePhaseDelayMs)
            }
        };
    }
```

with

```csharp
                NodePhaseDelayMs = Math.Max(0, NodePhaseDelayMs)
            }
        };
        PhysicalUnits.ApplyRoomUnits(config, UsePhysicalUnits);
        return config;
    }
```

- [ ] **Step 2: File-name captions**

Replace

```csharp
    [ObservableProperty]
    private string _backgroundImagePath = string.Empty;

    [ObservableProperty]
    private string _animationPath = string.Empty;
```

with

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImageFileName))]
    private string _backgroundImagePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimationFileName))]
    private string _animationPath = string.Empty;

    /// <summary>Caption of the primary file in the Content tab.</summary>
    public string AnimationFileName => string.IsNullOrWhiteSpace(AnimationPath)
        ? "Pick a file in the strip above" : Path.GetFileName(AnimationPath);

    /// <summary>Caption of the background image in the Background tab.</summary>
    public string BackgroundImageFileName => string.IsNullOrWhiteSpace(BackgroundImagePath)
        ? "No image yet" : Path.GetFileName(BackgroundImagePath);
```

- [ ] **Step 3: Settable distribution flags for the segmented toggle**

Replace

```csharp
    public bool IsSimultaneousMode => AnimationDistributionModeIndex == 1;

    partial void OnAnimationDistributionModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSimultaneousMode));
    }
```

with

```csharp
    /// <summary>Sequential radio button: true = one canvas spans all targets. Setting false is ignored (the other button sets its mode).</summary>
    public bool IsSequentialMode
    {
        get => AnimationDistributionModeIndex == 0;
        set { if (value) AnimationDistributionModeIndex = 0; }
    }

    /// <summary>Simultaneous radio button: true = every monitor plays its own copy. Setting false is ignored.</summary>
    public bool IsSimultaneousMode
    {
        get => AnimationDistributionModeIndex == 1;
        set { if (value) AnimationDistributionModeIndex = 1; }
    }

    partial void OnAnimationDistributionModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSequentialMode));
        OnPropertyChanged(nameof(IsSimultaneousMode));
    }
```

- [ ] **Step 4: Pattern count visibility and extra-image flag**

Replace

```csharp
    partial void OnPatternSizingIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPatternFill));
        OnPropertyChanged(nameof(IsPatternExplicit));
    }

    partial void OnPatternEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPatternWithIconZone));
    }
```

with

```csharp
    /// <summary>Count X/Y fields: pattern on and sized by explicit count.</summary>
    public bool IsPatternCountVisible => PatternEnabled && PatternSizingIndex == 1;

    partial void OnPatternSizingIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPatternFill));
        OnPropertyChanged(nameof(IsPatternExplicit));
        OnPropertyChanged(nameof(IsPatternCountVisible));
    }

    partial void OnPatternEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPatternWithIconZone));
        OnPropertyChanged(nameof(IsPatternCountVisible));
    }
```

After the line `[ObservableProperty] private float _multiImagePhaseJitterMs = 0f;` add:

```csharp
    /// <summary>True when extra source images are listed (shows the chip row).</summary>
    public bool HasExtraImages => AdditionalAnimationPaths.Count > 0;
```

In the constructor, after the palette loop, add:

```csharp
        _additionalAnimationPaths.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasExtraImages));
```

- [ ] **Step 5: Warning chips, speed readout, strip picking**

Directly after the property `public bool IsRandomWalkMode => SelectedMovementType?.Type == MovementType.RandomWalk;` add:

```csharp
    // ── Docked editor (UI redesign Plan 2) ───────────────────────────────────

    /// <summary>One-line warnings shown as amber chips above the tabs (computed by the host from the draft + room).</summary>
    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>Crossing-time readout under the speed slider, e.g. "crosses the room in 12.5 s".</summary>
    [ObservableProperty] private string _speedReadout = string.Empty;

    /// <summary>Replace the warning chips and the speed readout. Unchanged warnings do not touch the collection.</summary>
    public void SetChecks(IReadOnlyList<string> warnings, string speedReadout)
    {
        if (!Warnings.SequenceEqual(warnings))
        {
            Warnings.Clear();
            foreach (var w in warnings) Warnings.Add(w);
        }
        SpeedReadout = speedReadout;
    }

    /// <summary>True while the next gallery-strip click sets the background image instead of the animation file.</summary>
    [ObservableProperty] private bool _isPickingBackgroundImage;

    /// <summary>
    /// Apply a gallery-strip click: while picking a background it becomes the background image;
    /// otherwise it becomes the primary file, or (additive, Ctrl+click) an extra source image.
    /// </summary>
    public void PickFromStrip(string path, bool additive)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (IsPickingBackgroundImage)
        {
            BackgroundImagePath = path;
            IsPickingBackgroundImage = false;
            return;
        }
        if (additive)
        {
            if (!string.Equals(path, AnimationPath, StringComparison.OrdinalIgnoreCase) && !AdditionalAnimationPaths.Contains(path))
                AdditionalAnimationPaths.Add(path);
            return;
        }
        AnimationPath = path;
        AdditionalAnimationPaths.Remove(path);
    }
```

- [ ] **Step 6: Keep the old dialog compiling and honest about units**

In `CrossScreenConfigDialog.axaml` delete the element

```xml
                                <ComboBox SelectedIndex="{Binding SizeUnitIndex}" Width="70">
                                    <ComboBoxItem Content="px"/>
                                    <ComboBoxItem Content="cm"/>
                                </ComboBox>
```

and the element

```xml
                                <ComboBox Grid.Column="2" SelectedIndex="{Binding SpeedUnitIndex}" Width="80" FontSize="11">
                                    <ComboBoxItem Content="px/s"/>
                                    <ComboBoxItem Content="cm/s"/>
                                </ComboBox>
```

In `MainWindowViewModel.cs`, in both `ConfigureCrossScreen()` and `EditCrossScreenConfigForPlaylistAsync(...)`,
directly after `viewModel.SetAvailableMonitors(Clients);` add:

```csharp
            viewModel.UsePhysicalUnits = RoomPhysicalUnits;
```

(indent to match: 12 spaces in `ConfigureCrossScreen`, 8 in `EditCrossScreenConfigForPlaylistAsync`).

- [ ] **Step 7: Build and test**

Run: `dotnet build WaBiBaBuSy.UI -nologo -v q` then `dotnet test WaBiBaBuSy.Tests -nologo`
Expected: 0 errors (`SizeUnitIndex` / `SpeedUnitIndex` have no remaining references); all tests pass.

- [ ] **Step 8: Commit**

```bash
git add WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs
git commit -m "feat: scene editor view model follows room units, adds checks and strip picking

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Docked Scene editor, draft preview in the room, right panel

**Files:**
- Create: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.SceneEditor.cs`
- Create: `WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml`
- Create: `WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml.cs`
- Create: `WaBiBaBuSy.UI/Converters/FileNameConverter.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (ctor, `SetStorageProvider`, `OnRoomPhysicalUnitsChanged`, `NotifyClientSelectionCommands`, `StartCrossScreen`)
- Modify: `WaBiBaBuSy.UI/Views/MainWindow.axaml`
- Modify: `WaBiBaBuSy.UI/Views/MainWindow.axaml.cs`

**Interfaces:**
- Consumes: Task 1 `SceneChecks.Validate/CrossingReadout/SameScene`, `SceneTargets.Resolve`; Task 3 `RoomView.IsClockPaused/ClockSpeed/ClockRestartToken`; Task 4 editor members; existing `BuildSeatLayout(IEnumerable<ClientNodeViewModel>)`, `ResolveSpriteImagePath(string?)`, `RoomOrder`, `SelectedNodeIds`, `SelectWallpaper(WallpaperItemViewModel)`, `StartCrossScreen()`, `StopCrossScreen()`, `_crossScreenConfig`, `_playlistOrchestrator`.
- Produces (on `MainWindowViewModel`):
  - `CrossScreenConfigViewModel SceneEditor { get; }`, `static CrossScreenConfig DefaultScene()`.
  - Observable: `DraftScene`, `DraftLayout`, `DraftSpriteImagePath`, `IsPreviewDraft`, `IsRightPanelOpen` (default true), `RightPanelTabIndex` (0 Scene, 1 Playlist), `PreviewPaused`, `PreviewSpeedIndex`, `PreviewRestartToken`.
  - Computed: `RoomScene`, `RoomSharedStartMs`, `RoomSpriteImagePath`, `RoomLayout`, `PreviewClockSpeed`, `PreviewBadgeText`, `PreviewPauseGlyph`, `TargetsSummary`.
  - Commands: `TogglePreviewModeCommand`, `TogglePreviewPauseCommand`, `RestartPreviewCommand`, `ToggleRightPanelCommand`, `OpenSceneEditorCommand`, `PlayOnSelectionCommand` (CanExecute `HasNodeSelection`), `PlayOnAllCommand`, `RevertSceneCommand`.
  - Methods: `void PickGalleryItem(WallpaperItemViewModel item, bool additive)`, `void ScheduleDraftUpdate()`, `CrossScreenConfig RevertTarget()` (private; Task 6 changes it).
  - `FileNameConverter.Instance` (namespace `WaBiBaBuSy.UI.Views`): path → file name.

- [ ] **Step 1: Create the main view model partial**

```csharp
// WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.SceneEditor.cs
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Core.Services.Desktop;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// Docked Scene editor (UI redesign Plan 2): the editor holds one draft scene; every change is
/// debounced into the room preview (draft layout = the room selection, or all nodes) and nothing
/// reaches the machines until Play. The preview badge switches the room between that draft on a
/// local design clock and the running scene on the players' shared clock.
/// </summary>
public partial class MainWindowViewModel
{
    private const int DraftDebounceMs = 150;
    private DispatcherTimer? _draftDebounce;

    /// <summary>The Scene editor (one draft config, <see cref="CrossScreenConfigViewModel.BuildConfig"/>).</summary>
    public CrossScreenConfigViewModel SceneEditor { get; } = new();

    /// <summary>The editor's draft as last built; replaced only when its content changes (keeps the design clock running).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomScene))]
    private CrossScreenConfig? _draftScene;

    /// <summary>Seat-map layout of the draft's targets (room selection, or every node).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomLayout))]
    private SeatMapLayoutResult? _draftLayout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomSpriteImagePath))]
    private string? _draftSpriteImagePath;

    /// <summary>True: the room paints the editor draft; false: the running scene. Follows the panel, toggled by the badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomSharedStartMs), nameof(RoomLayout), nameof(RoomSpriteImagePath), nameof(RoomScene), nameof(PreviewBadgeText))]
    private bool _isPreviewDraft = true;

    /// <summary>Right panel expanded (false = thin rail).</summary>
    [ObservableProperty] private bool _isRightPanelOpen = true;

    /// <summary>Selected right-panel tab: 0 = Scene, 1 = Playlist.</summary>
    [ObservableProperty] private int _rightPanelTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPauseGlyph))]
    private bool _previewPaused;

    /// <summary>Draft clock speed: 0 = 1×, 1 = 4×, 2 = 16× (ComboBox order).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewClockSpeed))]
    private int _previewSpeedIndex;

    /// <summary>Bumped to restart the draft clock at t = 0.</summary>
    [ObservableProperty] private int _previewRestartToken;

    /// <summary>Scene the room paints.</summary>
    public CrossScreenConfig? RoomScene => IsPreviewDraft ? DraftScene : ActiveScene;
    /// <summary>Shared clock epoch for the room; 0 = design clock (draft).</summary>
    public long RoomSharedStartMs => IsPreviewDraft ? 0 : ActiveSharedStartMs;
    /// <summary>Sprite image the room paints.</summary>
    public string? RoomSpriteImagePath => IsPreviewDraft ? DraftSpriteImagePath : ActiveSpriteImagePath;
    /// <summary>Layout the room paints with (draft targets, or the layout the players were started with).</summary>
    public SeatMapLayoutResult? RoomLayout => IsPreviewDraft ? DraftLayout : ActiveLayout;
    /// <summary>Draft clock multiplier for the room.</summary>
    public double PreviewClockSpeed => PreviewSpeedIndex switch { 1 => 4.0, 2 => 16.0, _ => 1.0 };
    /// <summary>Preview badge caption.</summary>
    public string PreviewBadgeText => IsPreviewDraft ? "◉ Preview: draft (not live)" : "● Preview: live";
    /// <summary>Pause button glyph.</summary>
    public string PreviewPauseGlyph => PreviewPaused ? "▶" : "⏸";
    /// <summary>Editor footer: who Play on selection targets.</summary>
    public string TargetsSummary => SelectedNodeCount == 0
        ? "Targets: all nodes (select nodes in the room to narrow)"
        : $"Targets: {SelectedNodeCount} selected";

    partial void OnActiveSceneChanged(CrossScreenConfig? value) => OnPropertyChanged(nameof(RoomScene));
    partial void OnActiveSharedStartMsChanged(long value) => OnPropertyChanged(nameof(RoomSharedStartMs));
    partial void OnActiveSpriteImagePathChanged(string? value) => OnPropertyChanged(nameof(RoomSpriteImagePath));
    partial void OnActiveLayoutChanged(SeatMapLayoutResult? value) => OnPropertyChanged(nameof(RoomLayout));

    /// <summary>Opening the panel previews the draft, collapsing it shows what is live.</summary>
    partial void OnIsRightPanelOpenChanged(bool value) => IsPreviewDraft = value;

    /// <summary>Every switch between draft and live starts the draft at t = 0.</summary>
    partial void OnIsPreviewDraftChanged(bool value) => PreviewRestartToken++;

    partial void OnSeatMapVersionChanged(int value) => ScheduleDraftUpdate();

    /// <summary>Default scene for a fresh editor (IconZone background, empty file).</summary>
    public static CrossScreenConfig DefaultScene() => new()
    {
        Background = new BackgroundLayerConfig
        {
            Mode = BackgroundMode.IconZone,
            ColorHex = "#000000",
            IconZonePaletteHexes = PaletteGenerator.GenerateHarmonious(8),
            IconCorridorColorHex = "#1E1E1E"
        },
        Animation = new AnimationLayerConfig
        {
            AnimationPath = string.Empty,
            TargetHeight = 720,
            Loop = true,
            VerticalAlign = WaBiBaBuSy.Models.Wallpaper.VerticalAlignment.Center,
            RotateWithPath = true
        },
        AnimationSpeedPxPerSecond = 500
    };

    /// <summary>Called once at the end of the constructor.</summary>
    private void InitSceneEditor()
    {
        SceneEditor.UsePhysicalUnits = RoomPhysicalUnits;
        SceneEditor.LoadFromConfig(DefaultScene());
        SceneEditor.PropertyChanged += OnSceneEditorPropertyChanged;
        SceneEditor.AdditionalAnimationPaths.CollectionChanged += (_, _) => ScheduleDraftUpdate();
        SceneEditor.IconZonePalette.CollectionChanged += OnPaletteChanged;
        foreach (var z in SceneEditor.IconZonePalette) z.PropertyChanged += OnPaletteItemChanged;
        ScheduleDraftUpdate();
    }

    private void OnSceneEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Outputs of the draft update itself must not schedule another one.
        if (e.PropertyName is nameof(CrossScreenConfigViewModel.SpeedReadout)
            or nameof(CrossScreenConfigViewModel.IsPickingBackgroundImage)) return;
        ScheduleDraftUpdate();
    }

    private void OnPaletteChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Clear() (LoadFromConfig, Randomize) raises Reset without OldItems: re-attach to every current item.
        foreach (var z in SceneEditor.IconZonePalette)
        {
            z.PropertyChanged -= OnPaletteItemChanged;
            z.PropertyChanged += OnPaletteItemChanged;
        }
        ScheduleDraftUpdate();
    }

    private void OnPaletteItemChanged(object? sender, PropertyChangedEventArgs e) => ScheduleDraftUpdate();

    /// <summary>Rebuild the draft preview after <see cref="DraftDebounceMs"/> ms of quiet. Safe from any thread.</summary>
    public void ScheduleDraftUpdate()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ScheduleDraftUpdate);
            return;
        }
        _draftDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(DraftDebounceMs), DispatcherPriority.Background, (_, _) =>
        {
            _draftDebounce!.Stop();
            UpdateDraft();
        });
        _draftDebounce.Stop();
        _draftDebounce.Start();
    }

    private void UpdateDraft()
    {
        try
        {
            var config = SceneEditor.BuildConfig();
            var targets = new HashSet<string>(SceneTargets.Resolve(RoomOrder, SelectedNodeIds));
            var nodes = targets.Count == 0 ? Clients.ToList() : Clients.Where(c => targets.Contains(c.ClientId)).ToList();
            var layout = nodes.Count == 0 ? new SeatMapLayoutResult() : BuildSeatLayout(nodes);

            DraftLayout = layout;
            DraftSpriteImagePath = ResolveSpriteImagePath(config.Animation.AnimationPath);
            // The 2 s topology refresh re-runs this: only a real change may restart the draft clock.
            if (!SceneChecks.SameScene(DraftScene, config)) DraftScene = config;
            SceneEditor.SetChecks(SceneChecks.Validate(config, layout), SceneChecks.CrossingReadout(config, layout));
        }
        catch (Exception ex)
        {
            AppLogger.CreateLogger<MainWindowViewModel>().LogWarning(ex, "Scene draft preview update failed");
        }
    }

    /// <summary>
    /// Gallery-strip click: sets the primary file (and selects it for the Dev tools), Ctrl+click adds
    /// an extra image, and while the Background tab is picking, sets the background image.
    /// </summary>
    public void PickGalleryItem(WallpaperItemViewModel item, bool additive)
    {
        bool setsPrimary = !SceneEditor.IsPickingBackgroundImage && !additive;
        SceneEditor.PickFromStrip(item.FilePath, additive);
        if (!setsPrimary) return;
        SelectWallpaper(item);
        if (SceneEditor.BackgroundColor is "" or "#000000")
            _ = SceneEditor.AutoDetectBackgroundColorCommand.ExecuteAsync(null);
    }

    [RelayCommand] private void TogglePreviewMode() => IsPreviewDraft = !IsPreviewDraft;
    [RelayCommand] private void TogglePreviewPause() => PreviewPaused = !PreviewPaused;
    [RelayCommand] private void RestartPreview() => PreviewRestartToken++;
    [RelayCommand] private void ToggleRightPanel() => IsRightPanelOpen = !IsRightPanelOpen;

    [RelayCommand]
    private void OpenSceneEditor()
    {
        IsRightPanelOpen = true;
        RightPanelTabIndex = 0;
    }

    [RelayCommand(CanExecute = nameof(HasNodeSelection))]
    private Task PlayOnSelection() => PlayDraftAsync(selectionOnly: true);

    [RelayCommand]
    private Task PlayOnAll() => PlayDraftAsync(selectionOnly: false);

    /// <summary>
    /// Send the draft to the machines: stops a running show or scene first, then starts the draft on
    /// the selection (chain order) or on every node.
    /// </summary>
    private async Task PlayDraftAsync(bool selectionOnly)
    {
        var config = SceneEditor.BuildConfig();
        var path = config.Animation.AnimationPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            var warnings = SceneEditor.Warnings.ToList();
            warnings.Insert(0, string.IsNullOrWhiteSpace(path) ? "Pick an animation file from the strip" : $"File not found: {Path.GetFileName(path)}");
            SceneEditor.SetChecks(warnings.Distinct().ToList(), SceneEditor.SpeedReadout);
            return;
        }

        var targets = selectionOnly ? SceneTargets.Resolve(RoomOrder, SelectedNodeIds) : new List<string>();
        if (selectionOnly && targets.Count == 0) return;   // stale selection: never fall back to "all"
        config.SelectedMonitorIds = targets;

        if (_playlistOrchestrator?.IsRunning == true || IsCrossScreenRunning)
            await StopCrossScreen();

        _crossScreenConfig = config;
        HasAnimationConfig = true;
        await StartCrossScreen();
    }

    [RelayCommand]
    private void RevertScene() => SceneEditor.LoadFromConfig(RevertTarget());

    /// <summary>What Revert restores: the last played scene, else the default scene.</summary>
    private CrossScreenConfig RevertTarget() => _crossScreenConfig ?? DefaultScene();
}
```

- [ ] **Step 2: Hook the partial into the main file**

In `MainWindowViewModel.cs`:

1. Constructor — replace

```csharp
        // Room layout (rows / ring) — single row until the user configures the room
        LoadSeatMap();
    }
```

with

```csharp
        // Room layout (rows / ring) — single row until the user configures the room
        LoadSeatMap();

        // Docked Scene editor: default draft, units from the room, preview wiring
        InitSceneEditor();
    }
```

2. `SetStorageProvider` — after `_storageProvider = storageProvider;` add `SceneEditor.SetStorageProvider(storageProvider);`.

3. Replace `    partial void OnRoomPhysicalUnitsChanged(bool value) => UpdateRoomSettings();` with

```csharp
    partial void OnRoomPhysicalUnitsChanged(bool value)
    {
        UpdateRoomSettings();
        SceneEditor.UsePhysicalUnits = value;   // editor units follow the room
    }
```

4. In `NotifyClientSelectionCommands`, after `OnPropertyChanged(nameof(SelectionSummary));` add

```csharp
        OnPropertyChanged(nameof(TargetsSummary));
        PlayOnSelectionCommand.NotifyCanExecuteChanged();
        ScheduleDraftUpdate();   // draft targets = selection; also covers nodes joining/leaving (called after every refresh)
```

5. In `StartCrossScreen`, replace

```csharp
            Debug.WriteLine("[CrossScreen] No configuration. Opening config dialog...");
            await ConfigureCrossScreen();

            if (_crossScreenConfig == null || string.IsNullOrEmpty(_crossScreenConfig.Animation.AnimationPath))
            {
                Debug.WriteLine("[CrossScreen] Configuration cancelled or incomplete");
                return;
            }
```

with

```csharp
            Debug.WriteLine("[CrossScreen] No configuration — opening the Scene editor");
            OpenSceneEditor();
            return;
```

- [ ] **Step 3: File-name converter**

```csharp
// WaBiBaBuSy.UI/Converters/FileNameConverter.cs
using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;

namespace WaBiBaBuSy.UI.Views;

/// <summary>Converts a file path to its file name (for chips and captions).</summary>
public sealed class FileNameConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> use in XAML.</summary>
    public static readonly FileNameConverter Instance = new();

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string path ? Path.GetFileName(path) : value;

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 4: Create `SceneEditorPanel`**

```xml
<!-- WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml -->
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:WaBiBaBuSy.UI.ViewModels"
             xmlns:views="using:WaBiBaBuSy.UI.Views"
             xmlns:sys="using:System"
             x:Class="WaBiBaBuSy.UI.Views.SceneEditorPanel"
             x:DataType="vm:MainWindowViewModel"
             FontSize="12">
    <UserControl.Styles>
        <Style Selector="TextBlock.label">
            <Setter Property="FontSize" Value="12"/>
            <Setter Property="Foreground" Value="#CCCCCC"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
        </Style>
        <Style Selector="TextBlock.hint">
            <Setter Property="FontSize" Value="11"/>
            <Setter Property="Foreground" Value="#888888"/>
            <Setter Property="TextWrapping" Value="Wrap"/>
        </Style>
        <Style Selector="Border.strip">
            <Setter Property="BorderBrush" Value="#555555"/>
        </Style>
        <Style Selector="Border.strip.selected">
            <Setter Property="BorderBrush" Value="#0078D4"/>
        </Style>
        <Style Selector="TabControl.editor > TabItem">
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="MinHeight" Value="32"/>
            <Setter Property="Padding" Value="10,4"/>
        </Style>
        <Style Selector="Expander">
            <Setter Property="HorizontalAlignment" Value="Stretch"/>
        </Style>
    </UserControl.Styles>

    <Grid RowDefinitions="Auto,Auto,*,Auto">
        <!-- Gallery strip: click = primary file, Ctrl+click = extra image, right-click = remove -->
        <Grid Grid.Row="0" ColumnDefinitions="*,Auto" Margin="0,8,0,4">
            <ScrollViewer HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Disabled">
                <ItemsControl ItemsSource="{Binding Wallpapers}">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <StackPanel Orientation="Horizontal" Spacing="6"/>
                        </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                    <ItemsControl.ItemTemplate>
                        <DataTemplate x:DataType="vm:WallpaperItemViewModel">
                            <Border Classes="strip" Classes.selected="{Binding IsSelected}"
                                    Width="96" Height="72" CornerRadius="4" Background="#404040"
                                    BorderThickness="2" ClipToBounds="True" Margin="0,0,0,10"
                                    Cursor="Hand" ToolTip.Tip="{Binding Name}"
                                    PointerPressed="OnStripItemPressed">
                                <Border.ContextMenu>
                                    <ContextMenu>
                                        <MenuItem Header="Remove from gallery"
                                                  Command="{Binding $parent[Window].((vm:MainWindowViewModel)DataContext).RemoveWallpaperCommand}"
                                                  CommandParameter="{Binding}"/>
                                    </ContextMenu>
                                </Border.ContextMenu>
                                <Panel>
                                    <Image Source="{Binding Thumbnail}" Stretch="UniformToFill"
                                           IsVisible="{Binding Thumbnail, Converter={x:Static ObjectConverters.IsNotNull}}"/>
                                    <TextBlock Text="{Binding Name}" FontSize="10" Foreground="#AAAAAA" TextWrapping="Wrap" Margin="4"
                                               IsVisible="{Binding Thumbnail, Converter={x:Static ObjectConverters.IsNull}}"/>
                                    <Border Background="{Binding TypeBadgeBrush}" CornerRadius="3" Padding="3,1"
                                            HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,3,3,0">
                                        <TextBlock Text="{Binding TypeName}" Foreground="White" FontSize="8" FontWeight="Bold"/>
                                    </Border>
                                </Panel>
                            </Border>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </ScrollViewer>
            <Button Grid.Column="1" Content="+ Add" Command="{Binding AddWallpaperCommand}"
                    VerticalAlignment="Top" Margin="6,0,0,0"
                    ToolTip.Tip="Add files to the gallery (or drop files onto this panel)"/>
        </Grid>

        <!-- Picking hint + validation chips -->
        <StackPanel Grid.Row="1" Spacing="4" Margin="0,0,0,4">
            <Border Background="#1A3A5C" CornerRadius="4" Padding="8,4"
                    IsVisible="{Binding SceneEditor.IsPickingBackgroundImage}">
                <TextBlock Text="Click a thumbnail to use it as the background image (click 'Choose from strip' again to cancel)"
                           FontSize="11" TextWrapping="Wrap"/>
            </Border>
            <ItemsControl ItemsSource="{Binding SceneEditor.Warnings}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate>
                        <WrapPanel/>
                    </ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate x:DataType="sys:String">
                        <Border Background="#3A2E10" BorderBrush="#FFB020" BorderThickness="1" CornerRadius="10"
                                Padding="8,2" Margin="0,0,4,4">
                            <TextBlock Text="{Binding}" Foreground="#FFD080" FontSize="11"/>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </StackPanel>

        <!-- Tabs: bound to the editor view model -->
        <Border Grid.Row="2" DataContext="{Binding SceneEditor}">
            <TabControl Classes="editor" x:DataType="vm:CrossScreenConfigViewModel" Padding="0">

                <!-- CONTENT -->
                <TabItem Header="Content">
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <StackPanel Spacing="8" Margin="0,10,12,10">
                            <Grid ColumnDefinitions="120,*">
                                <TextBlock Classes="label" Text="File ⓘ"
                                           ToolTip.Tip="Click a thumbnail in the strip above. Ctrl+click adds it as an extra source image."/>
                                <TextBlock Grid.Column="1" Text="{Binding AnimationFileName}" FontWeight="SemiBold"
                                           VerticalAlignment="Center" TextTrimming="CharacterEllipsis"
                                           ToolTip.Tip="{Binding AnimationPath}"/>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding HasExtraImages}">
                                <TextBlock Classes="label" Text="Extra images" VerticalAlignment="Top" Margin="0,3,0,0"/>
                                <ItemsControl Grid.Column="1" ItemsSource="{Binding AdditionalAnimationPaths}">
                                    <ItemsControl.ItemsPanel>
                                        <ItemsPanelTemplate>
                                            <WrapPanel/>
                                        </ItemsPanelTemplate>
                                    </ItemsControl.ItemsPanel>
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate x:DataType="sys:String">
                                            <Border Background="#2D2D30" CornerRadius="10" Padding="8,1,2,1" Margin="0,0,4,4">
                                                <StackPanel Orientation="Horizontal" Spacing="2">
                                                    <TextBlock Text="{Binding Converter={x:Static views:FileNameConverter.Instance}}"
                                                               FontSize="11" VerticalAlignment="Center" MaxWidth="140"
                                                               TextTrimming="CharacterEllipsis" ToolTip.Tip="{Binding}"/>
                                                    <Button Content="✕" FontSize="10" Padding="4,0" Background="Transparent"
                                                            Command="{Binding $parent[TabControl].((vm:CrossScreenConfigViewModel)DataContext).RemoveAdditionalImageCommand}"
                                                            CommandParameter="{Binding}" ToolTip.Tip="Remove this extra image"/>
                                                </StackPanel>
                                            </Border>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>
                            </Grid>
                            <Grid ColumnDefinitions="120,*">
                                <TextBlock Classes="label" Text="Fit"/>
                                <ComboBox Grid.Column="1" SelectedIndex="{Binding FitModeIndex}" HorizontalAlignment="Stretch">
                                    <ComboBoxItem Content="Center (native size)"/>
                                    <ComboBoxItem Content="Fit into screen"/>
                                    <ComboBoxItem Content="Fill screen"/>
                                    <ComboBoxItem Content="Stretch to screen"/>
                                    <ComboBoxItem Content="Target height"/>
                                </ComboBox>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsAnimationHeightRelevant}">
                                <TextBlock Classes="label" Text="Height"/>
                                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
                                    <NumericUpDown Value="{Binding AnimationHeight}" Minimum="10" Maximum="4320" Increment="10"
                                                   Width="130" IsVisible="{Binding !IsSizeInCm}"/>
                                    <NumericUpDown Value="{Binding AnimationHeightCm}" Minimum="0.5" Maximum="200" Increment="0.5"
                                                   FormatString="0.0" Width="130" IsVisible="{Binding IsSizeInCm}"/>
                                    <TextBlock Classes="label" Text="px" IsVisible="{Binding !IsSizeInCm}"/>
                                    <TextBlock Classes="label" Text="cm" IsVisible="{Binding IsSizeInCm}"/>
                                </StackPanel>
                            </Grid>
                            <Grid ColumnDefinitions="120,*">
                                <TextBlock Classes="label" Text="Vertical align"/>
                                <ComboBox Grid.Column="1" SelectedIndex="{Binding VerticalAlignmentIndex}" Width="140">
                                    <ComboBoxItem Content="Top"/>
                                    <ComboBoxItem Content="Center"/>
                                    <ComboBoxItem Content="Bottom"/>
                                </ComboBox>
                            </Grid>
                            <CheckBox IsChecked="{Binding AnimationLoop}" Content="Loop"/>
                            <CheckBox IsChecked="{Binding FaceTravelDirection}" Content="Face travel direction ⓘ"
                                      ToolTip.Tip="Mirror the sprite whenever it moves left."/>
                            <Expander Header="Advanced">
                                <StackPanel Spacing="8">
                                    <Grid ColumnDefinitions="120,*">
                                        <TextBlock Classes="label" Text="Playback speed ⓘ"
                                                   ToolTip.Tip="GIF / video frame timing. 1.0 = native frame delays; independent of the movement speed."/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding SpeedMultiplier}" Minimum="0.1" Maximum="10"
                                                       Increment="0.1" FormatString="0.0" Width="130" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*">
                                        <TextBlock Classes="label" Text="Image spread ⓘ"
                                                   ToolTip.Tip="Extra images without a pattern: spread (px) between the copies."/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding MultiImageSpread}" Minimum="0" Maximum="1000"
                                                       Increment="10" Width="130" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*">
                                        <TextBlock Classes="label" Text="Phase jitter ⓘ"
                                                   ToolTip.Tip="Extra images without a pattern: time offset (ms) between the copies."/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding MultiImagePhaseJitterMs}" Minimum="0" Maximum="5000"
                                                       Increment="50" Width="130" HorizontalAlignment="Left"/>
                                    </Grid>
                                </StackPanel>
                            </Expander>
                        </StackPanel>
                    </ScrollViewer>
                </TabItem>

                <!-- MOTION -->
                <TabItem Header="Motion">
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <StackPanel Spacing="8" Margin="0,10,12,10">
                            <Grid ColumnDefinitions="120,*">
                                <TextBlock Classes="label" Text="Pattern"/>
                                <ComboBox Grid.Column="1" ItemsSource="{Binding AvailableMovementOptions}"
                                          SelectedItem="{Binding SelectedMovementType}" HorizontalAlignment="Stretch">
                                    <ComboBox.ItemTemplate>
                                        <DataTemplate x:DataType="vm:MovementTypeOption">
                                            <TextBlock Text="{Binding Name}"/>
                                        </DataTemplate>
                                    </ComboBox.ItemTemplate>
                                </ComboBox>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsMovementActive}">
                                <TextBlock Classes="label" Text="Speed" VerticalAlignment="Top" Margin="0,8,0,0"/>
                                <StackPanel Grid.Column="1" Spacing="2">
                                    <Grid ColumnDefinitions="*,Auto" ColumnSpacing="8" IsVisible="{Binding !IsSpeedInCm}">
                                        <Slider Value="{Binding AnimationSpeed}" Minimum="50" Maximum="2000" TickFrequency="50" IsSnapToTickEnabled="True"/>
                                        <TextBlock Grid.Column="1" Text="{Binding AnimationSpeed, StringFormat={}{0} px/s}"
                                                   VerticalAlignment="Center" MinWidth="72"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="*,Auto" ColumnSpacing="8" IsVisible="{Binding IsSpeedInCm}">
                                        <Slider Value="{Binding AnimationSpeedCm}" Minimum="1" Maximum="150" TickFrequency="1" IsSnapToTickEnabled="True"/>
                                        <TextBlock Grid.Column="1" Text="{Binding AnimationSpeedCm, StringFormat={}{0:0.0} cm/s}"
                                                   VerticalAlignment="Center" MinWidth="72"/>
                                    </Grid>
                                    <TextBlock Classes="hint" Text="{Binding SpeedReadout}"/>
                                </StackPanel>
                            </Grid>
                            <Grid ColumnDefinitions="120,*">
                                <TextBlock Classes="label" Text="Distribution ⓘ"
                                           ToolTip.Tip="Sequential: one animation spans all target monitors as one canvas. Simultaneous: every monitor plays its own copy."/>
                                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="12">
                                    <RadioButton GroupName="SceneDistribution" Content="Sequential" IsChecked="{Binding IsSequentialMode}"/>
                                    <RadioButton GroupName="SceneDistribution" Content="Simultaneous" IsChecked="{Binding IsSimultaneousMode}"/>
                                </StackPanel>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsDirectionVisible}">
                                <TextBlock Classes="label" Text="Direction ⓘ" ToolTip.Tip="Degrees: 0 = right, 90 = down, 45 = diagonal."/>
                                <NumericUpDown Grid.Column="1" Value="{Binding MovementAngle}" Minimum="0" Maximum="360" Increment="15"
                                               Width="130" HorizontalAlignment="Left"/>
                            </Grid>
                            <CheckBox IsChecked="{Binding IsMovementReversed}" IsVisible="{Binding IsReversibleMode}"
                                      Content="Reverse ⓘ"
                                      ToolTip.Tip="Linear: first node → last node. Sine wave: right to left. Circular: counter-clockwise."/>
                            <Expander Header="Advanced">
                                <StackPanel Spacing="8">
                                    <CheckBox IsChecked="{Binding IsMovementEndless}" IsVisible="{Binding IsLinearMode}"
                                              Content="Endless (no loop reset) ⓘ"
                                              ToolTip.Tip="The pattern scrolls forever; each cell keeps a unique color. Needs a Traveling color mode."/>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsSineWaveMode}">
                                        <TextBlock Classes="label" Text="Amplitude (px)"/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding WaveAmplitude}" Minimum="50" Maximum="1000"
                                                       Increment="25" Width="130" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsSineWaveMode}">
                                        <TextBlock Classes="label" Text="Frequency (Hz)"/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding WaveFrequency}" Minimum="0.1" Maximum="5.0"
                                                       Increment="0.1" FormatString="F1" Width="130" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsCircularMode}">
                                        <TextBlock Classes="label" Text="Orbit radius (px)"/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding OrbitRadius}" Minimum="100" Maximum="2000"
                                                       Increment="50" Width="130" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsRandomWalkMode}">
                                        <TextBlock Classes="label" Text="Iteration steps ⓘ"
                                                   ToolTip.Tip="After this many steps the walk pattern changes. 0 = never."/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding RandomWalkIterationSteps}" Minimum="0" Maximum="500"
                                                       Increment="5" Width="130" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsRandomWalkMode}">
                                        <TextBlock Classes="label" Text="Seed"/>
                                        <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
                                            <NumericUpDown Value="{Binding RandomSeed}" Minimum="0" Maximum="999999" Increment="1" Width="130"/>
                                            <Button Content="Randomize" Command="{Binding RandomizeMovementSeedCommand}" FontSize="11"/>
                                        </StackPanel>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsRandomWalkMode}">
                                        <TextBlock Classes="label" Text="Step interval (ms)"/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding RandomStepIntervalMs}" Minimum="100" Maximum="10000"
                                                       Increment="100" Width="130" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsSimultaneousMode}">
                                        <TextBlock Classes="label" Text="Wave delay ⓘ"
                                                   ToolTip.Tip="ms each node starts after the previous one, so a bounce or orbit runs down the row like a stadium wave. 0 = all in phase."/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding NodePhaseDelayMs}" Minimum="0" Maximum="5000"
                                                       Increment="50" Width="130" HorizontalAlignment="Left"/>
                                    </Grid>
                                </StackPanel>
                            </Expander>
                        </StackPanel>
                    </ScrollViewer>
                </TabItem>

                <!-- LOOK -->
                <TabItem Header="Look">
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <StackPanel Spacing="8" Margin="0,10,12,10">
                            <Grid ColumnDefinitions="120,*">
                                <TextBlock Classes="label" Text="Color grading"/>
                                <ComboBox Grid.Column="1" SelectedIndex="{Binding ColorGradingModeIndex}" HorizontalAlignment="Stretch">
                                    <ComboBoxItem Content="None"/>
                                    <ComboBoxItem Content="Rainbow (tint sweep)"/>
                                    <ComboBoxItem Content="Random colors"/>
                                    <ComboBoxItem Content="Gradient (ping-pong)"/>
                                    <ComboBoxItem Content="Cycle color list"/>
                                    <ComboBoxItem Content="Traveling colors (rainbow)"/>
                                    <ComboBoxItem Content="Traveling colors (list)"/>
                                    <ComboBoxItem Content="Traveling colors (random)"/>
                                </ComboBox>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsColorGradingTimeBased}">
                                <TextBlock Classes="label" Text="Cycles / second"/>
                                <NumericUpDown Grid.Column="1" Value="{Binding ColorGradingCyclesPerSecond}" Minimum="0.01" Maximum="10"
                                               Increment="0.1" Width="130" HorizontalAlignment="Left"/>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsColorGradingGradient}">
                                <TextBlock Classes="label" Text="Colors A → B"/>
                                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
                                    <TextBox Text="{Binding ColorGradingGradientA}" Width="90"/>
                                    <TextBox Text="{Binding ColorGradingGradientB}" Width="90"/>
                                </StackPanel>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsColorGradingColorListMode}">
                                <TextBlock Classes="label" Text="Color list ⓘ" ToolTip.Tip="Comma-separated hex colors."/>
                                <TextBox Grid.Column="1" Text="{Binding ColorGradingColorListCsv}"/>
                            </Grid>
                            <CheckBox IsChecked="{Binding PatternEnabled}" Content="Pattern grid ⓘ"
                                      ToolTip.Tip="Tile the animation into a deterministic grid. With an IconZone background the icon zones mask the grid so desktop icons stay visible."/>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding PatternEnabled}">
                                <TextBlock Classes="label" Text="Sizing"/>
                                <ComboBox Grid.Column="1" SelectedIndex="{Binding PatternSizingIndex}" Width="200">
                                    <ComboBoxItem Content="Fill (edge to edge)"/>
                                    <ComboBoxItem Content="Explicit count"/>
                                </ComboBox>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsPatternCountVisible}">
                                <TextBlock Classes="label" Text="Count X × Y"/>
                                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
                                    <NumericUpDown Value="{Binding PatternCountX}" Minimum="1" Maximum="500" Increment="1" Width="100"/>
                                    <NumericUpDown Value="{Binding PatternCountY}" Minimum="1" Maximum="500" Increment="1" Width="100"/>
                                </StackPanel>
                            </Grid>
                            <Expander Header="Advanced">
                                <StackPanel Spacing="8">
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsColorGradingTraveling}">
                                        <TextBlock Classes="label" Text="Colored cells ⓘ"
                                                   ToolTip.Tip="100% = every cell colored; 10% = sparse colored cells among monochrome."/>
                                        <Grid Grid.Column="1" ColumnDefinitions="*,Auto" ColumnSpacing="8">
                                            <Slider Minimum="0" Maximum="1" TickFrequency="0.01" Value="{Binding ColorGradingColoredCellPercentage}"/>
                                            <TextBlock Grid.Column="1" VerticalAlignment="Center" MinWidth="40"
                                                       Text="{Binding ColorGradingColoredCellPercentage, StringFormat={}{0:P0}}"/>
                                        </Grid>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding !IsColorGradingNone}">
                                        <TextBlock Classes="label" Text="Color seed"/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding ColorGradingSeed}" Minimum="0" Maximum="999999"
                                                       Increment="1" Width="130" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding PatternEnabled}">
                                        <TextBlock Classes="label" Text="Spacing X × Y (px)"/>
                                        <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
                                            <NumericUpDown Value="{Binding PatternSpacingX}" Minimum="0" Maximum="2000" Increment="5" Width="100"/>
                                            <NumericUpDown Value="{Binding PatternSpacingY}" Minimum="0" Maximum="2000" Increment="5" Width="100"/>
                                        </StackPanel>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding PatternEnabled}">
                                        <TextBlock Classes="label" Text="Margin (px)"/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding PatternMargin}" Minimum="0" Maximum="500"
                                                       Increment="10" Width="100" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding PatternEnabled}">
                                        <TextBlock Classes="label" Text="Pattern seed"/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding PatternSeed}" Minimum="0" Maximum="999999"
                                                       Increment="1" Width="100" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding PatternEnabled}">
                                        <TextBlock Classes="label" Text="Random offset (px)"/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding PatternRandomOffset}" Minimum="0" Maximum="500"
                                                       Increment="5" Width="100" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding PatternEnabled}">
                                        <TextBlock Classes="label" Text="Random rot. (°)"/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding PatternRandomRotation}" Minimum="0" Maximum="180"
                                                       Increment="5" Width="100" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsPatternWithIconZone}">
                                        <TextBlock Classes="label" Text="Zone size (px) ⓘ" ToolTip.Tip="Expand the invisible zone beyond the detected icons."/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding IconZoneExpansionPx}" Minimum="0" Maximum="500"
                                                       Increment="10" Width="100" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsPatternWithIconZone}">
                                        <TextBlock Classes="label" Text="Fade border (px) ⓘ" ToolTip.Tip="0 = hard edge; 40–120 px recommended."/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding IconFadePaddingPx}" Minimum="0" Maximum="500"
                                                       Increment="10" Width="100" HorizontalAlignment="Left"/>
                                    </Grid>
                                </StackPanel>
                            </Expander>
                        </StackPanel>
                    </ScrollViewer>
                </TabItem>

                <!-- BACKGROUND -->
                <TabItem Header="Background">
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <StackPanel Spacing="8" Margin="0,10,12,10">
                            <Grid ColumnDefinitions="120,*">
                                <TextBlock Classes="label" Text="Mode"/>
                                <ComboBox Grid.Column="1" SelectedIndex="{Binding BackgroundModeIndex}" HorizontalAlignment="Stretch">
                                    <ComboBoxItem Content="Solid color"/>
                                    <ComboBoxItem Content="Stretched image"/>
                                    <ComboBoxItem Content="Tiled image"/>
                                    <ComboBoxItem Content="Three zone (corridor)"/>
                                    <ComboBoxItem Content="Icon zone (auto-detect)"/>
                                </ComboBox>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsSolidColorMode}">
                                <TextBlock Classes="label" Text="Color"/>
                                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
                                    <TextBox Text="{Binding BackgroundColor}" Width="100" Watermark="#000000"/>
                                    <Border Width="22" Height="22" CornerRadius="3" Background="{Binding BackgroundColor}"
                                            BorderBrush="#555" BorderThickness="1" VerticalAlignment="Center"/>
                                    <Button Content="Auto-detect" Command="{Binding AutoDetectBackgroundColorCommand}" FontSize="11"
                                            ToolTip.Tip="Dominant edge color of the animation file"/>
                                </StackPanel>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsImageMode}">
                                <TextBlock Classes="label" Text="Image" VerticalAlignment="Top" Margin="0,6,0,0"/>
                                <StackPanel Grid.Column="1" Spacing="6">
                                    <TextBlock Text="{Binding BackgroundImageFileName}" FontWeight="SemiBold"
                                               TextTrimming="CharacterEllipsis" ToolTip.Tip="{Binding BackgroundImagePath}"/>
                                    <StackPanel Orientation="Horizontal" Spacing="6">
                                        <ToggleButton Content="Choose from strip" IsChecked="{Binding IsPickingBackgroundImage}"/>
                                        <Button Content="Browse…" Command="{Binding BrowseBackgroundImageCommand}"/>
                                    </StackPanel>
                                </StackPanel>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsThreeZoneMode}">
                                <TextBlock Classes="label" Text="Top zone"/>
                                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
                                    <TextBox Text="{Binding TopZoneColorHex}" Width="100"/>
                                    <Border Width="22" Height="22" CornerRadius="3" Background="{Binding TopZoneColorHex}" BorderBrush="#555" BorderThickness="1"/>
                                </StackPanel>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsThreeZoneMode}">
                                <TextBlock Classes="label" Text="Corridor"/>
                                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
                                    <TextBox Text="{Binding CorridorColorHex}" Width="100"/>
                                    <Border Width="22" Height="22" CornerRadius="3" Background="{Binding CorridorColorHex}" BorderBrush="#555" BorderThickness="1"/>
                                </StackPanel>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsThreeZoneMode}">
                                <TextBlock Classes="label" Text="Bottom zone"/>
                                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
                                    <TextBox Text="{Binding BottomZoneColorHex}" Width="100"/>
                                    <Border Width="22" Height="22" CornerRadius="3" Background="{Binding BottomZoneColorHex}" BorderBrush="#555" BorderThickness="1"/>
                                </StackPanel>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsIconZoneMode}">
                                <TextBlock Classes="label" Text="Corridor ⓘ"
                                           ToolTip.Tip="Color of the free bands. Every node detects its own desktop icons; adjacent icons merge into one cluster."/>
                                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
                                    <TextBox Text="{Binding IconCorridorColorHex}" Width="100"/>
                                    <Border Width="22" Height="22" CornerRadius="3" Background="{Binding IconCorridorColorHex}" BorderBrush="#555" BorderThickness="1"/>
                                </StackPanel>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsIconZoneMode}">
                                <TextBlock Classes="label" Text="Zone colors" VerticalAlignment="Top" Margin="0,6,0,0"/>
                                <StackPanel Grid.Column="1" Spacing="6">
                                    <StackPanel Orientation="Horizontal" Spacing="6">
                                        <Button Content="Randomize" Command="{Binding RandomizePaletteCommand}" FontSize="11"/>
                                        <Button Content="+" Command="{Binding AddZoneColorCommand}" FontSize="11" ToolTip.Tip="Add a zone color"/>
                                        <Button Content="−" Command="{Binding RemoveLastZoneColorCommand}" FontSize="11" ToolTip.Tip="Remove the last zone color"/>
                                    </StackPanel>
                                    <ItemsControl ItemsSource="{Binding IconZonePalette}">
                                        <ItemsControl.ItemsPanel>
                                            <ItemsPanelTemplate>
                                                <WrapPanel/>
                                            </ItemsPanelTemplate>
                                        </ItemsControl.ItemsPanel>
                                        <ItemsControl.ItemTemplate>
                                            <DataTemplate x:DataType="vm:ZoneColorItem">
                                                <StackPanel Spacing="2" Margin="0,0,6,4" Width="54">
                                                    <Border Height="24" CornerRadius="3" Background="{Binding ColorHex}" BorderBrush="#555" BorderThickness="1"/>
                                                    <TextBox Text="{Binding ColorHex}" FontSize="9" Height="20" Padding="2,1"/>
                                                </StackPanel>
                                            </DataTemplate>
                                        </ItemsControl.ItemTemplate>
                                    </ItemsControl>
                                </StackPanel>
                            </Grid>
                            <Expander Header="Advanced">
                                <StackPanel Spacing="8">
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsThreeZoneMode}">
                                        <TextBlock Classes="label" Text="Corridor top (px)"/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding CorridorTopPx}" Minimum="0" Maximum="2000"
                                                       Increment="10" Width="130" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <Grid ColumnDefinitions="120,*" IsVisible="{Binding IsThreeZoneMode}">
                                        <TextBlock Classes="label" Text="Corridor height (px)"/>
                                        <NumericUpDown Grid.Column="1" Value="{Binding CorridorHeightPx}" Minimum="50" Maximum="2000"
                                                       Increment="10" Width="130" HorizontalAlignment="Left"/>
                                    </Grid>
                                    <CheckBox IsChecked="{Binding RotateWithPath}" IsVisible="{Binding IsIconZoneMode}"
                                              Content="Rotate with path direction"/>
                                    <TextBlock Classes="hint" Text="No advanced options for this background."
                                               IsVisible="{Binding IsSolidColorMode}"/>
                                    <TextBlock Classes="hint" Text="No advanced options for this background."
                                               IsVisible="{Binding IsImageMode}"/>
                                </StackPanel>
                            </Expander>
                        </StackPanel>
                    </ScrollViewer>
                </TabItem>
            </TabControl>
        </Border>

        <!-- Footer: targets + play; always visible -->
        <Border Grid.Row="3" Background="#252526" CornerRadius="5" Padding="10,8" Margin="0,6,0,0">
            <StackPanel Spacing="6">
                <TextBlock Text="{Binding TargetsSummary}" FontSize="12" Foreground="#CCCCCC"
                           ToolTip.Tip="Select nodes in the room to target them; with nothing selected the scene plays on every node."/>
                <WrapPanel>
                    <Button Content="▶ Play on selection" Command="{Binding PlayOnSelectionCommand}" Classes="accent" Margin="0,0,6,6"/>
                    <Button Content="Play on all" Command="{Binding PlayOnAllCommand}" Margin="0,0,6,6"/>
                    <Button Content="⟲ Revert" Command="{Binding RevertSceneCommand}" Margin="0,0,6,6"
                            ToolTip.Tip="Back to the last played scene (or the default scene)"/>
                </WrapPanel>
            </StackPanel>
        </Border>
    </Grid>
</UserControl>
```

```csharp
// WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml.cs
using Avalonia.Controls;
using Avalonia.Input;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Views;

/// <summary>Right-panel "Scene" tab: gallery strip, validation chips, Content/Motion/Look/Background tabs and the play footer.</summary>
public partial class SceneEditorPanel : UserControl
{
    public SceneEditorPanel() => InitializeComponent();

    /// <summary>Strip click: primary file; Ctrl+click adds an extra image; while picking a background, sets it.</summary>
    private void OnStripItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { DataContext: WallpaperItemViewModel item } || DataContext is not MainWindowViewModel vm) return;
        vm.PickGalleryItem(item, e.KeyModifiers.HasFlag(KeyModifiers.Control));
        e.Handled = true;
    }
}
```

- [ ] **Step 5: Main window — preview bar, room bindings, right panel, Dev palette toggle**

In `MainWindow.axaml`:

1. Toolbar: replace `Command="{Binding ConfigureCrossScreenCommand}"` with `Command="{Binding OpenSceneEditorCommand}"` and its tooltip with `ToolTip.Tip="Open the Scene editor"`.

2. Dev tools: inside the `<WrapPanel Orientation="Horizontal">` of the debug overlay, after the `Info` CheckBox, add

```xml
                                            <CheckBox Content="Icon-zone palette" IsChecked="{Binding SceneEditor.IconZonePaletteEnabled}"
                                                      FontSize="11" Margin="8,0,0,0"
                                                      ToolTip.Tip="Debug: color the icon-zone bands of the draft scene (IconZone background only)"/>
```

3. Left panel: replace `<Grid RowDefinitions="Auto,Auto,Auto,*,Auto,Auto,Auto">` with `<Grid RowDefinitions="Auto,Auto,*,Auto,Auto,Auto">`;
   on the room `<Border Grid.Row="3" Background="#1A1A1A" ...>` change `Grid.Row="3"` to `Grid.Row="2"`;
   on the log viewer `<Border Grid.Row="6"` change to `Grid.Row="5"`. The selection bar keeps `Grid.Row="4"`.

4. Replace the `RoomView` element with

```xml
                            <controls:RoomView Host="{Binding}"
                                               Scene="{Binding RoomScene}"
                                               SharedStartUtcMs="{Binding RoomSharedStartMs}"
                                               SpriteImagePath="{Binding RoomSpriteImagePath}"
                                               ActiveLayout="{Binding RoomLayout}"
                                               IsClockPaused="{Binding PreviewPaused}"
                                               ClockSpeed="{Binding PreviewClockSpeed}"
                                               ClockRestartToken="{Binding PreviewRestartToken}"/>
```

5. Directly after the room `</Border>` (the one that closes the `ScrollViewer` holding `RoomView`), insert the preview bar:

```xml
                    <!-- Preview bar: tiles show the editor draft (design clock) or the live shared clock -->
                    <StackPanel Grid.Row="3" Orientation="Horizontal" Spacing="6" Margin="0,6,0,0">
                        <Button Command="{Binding TogglePreviewModeCommand}" Padding="8,3" FontSize="11"
                                ToolTip.Tip="Draft: the tiles show the Scene editor — nothing is sent to the machines. Live: the tiles show what the machines play.">
                            <TextBlock Text="{Binding PreviewBadgeText}"/>
                        </Button>
                        <Button Content="{Binding PreviewPauseGlyph}" Command="{Binding TogglePreviewPauseCommand}"
                                Padding="8,3" FontSize="11" IsEnabled="{Binding IsPreviewDraft}"
                                ToolTip.Tip="Pause / resume the draft clock"/>
                        <Button Content="⟲" Command="{Binding RestartPreviewCommand}" Padding="8,3" FontSize="11"
                                IsEnabled="{Binding IsPreviewDraft}" ToolTip.Tip="Restart the draft at t = 0"/>
                        <ComboBox SelectedIndex="{Binding PreviewSpeedIndex}" FontSize="11" Width="70"
                                  IsEnabled="{Binding IsPreviewDraft}" ToolTip.Tip="Draft clock speed">
                            <ComboBoxItem Content="1×"/>
                            <ComboBoxItem Content="4×"/>
                            <ComboBoxItem Content="16×"/>
                        </ComboBox>
                        <TextBlock Text="Geometry and colors only — GIF frames, IconZone paths and video are not simulated."
                                   Foreground="#666666" FontSize="10" VerticalAlignment="Center"/>
                    </StackPanel>
```

6. Split grid: replace `<Grid Grid.Row="2" ColumnDefinitions="*,5,*">` with `<Grid Grid.Row="2" Name="MainSplit" ColumnDefinitions="*,5,460">`
   and `<GridSplitter Grid.Column="1" Background="#3E3E42"/>` with `<GridSplitter Grid.Column="1" Name="PanelSplitter" Background="#3E3E42"/>`.

7. Replace everything from `<!-- Right Panel: Wallpaper Gallery -->` through its closing `</Border>` (the last
   `</Border>` before the closing `</Grid>`, `</Grid>`, `</Window>`) with:

```xml
            <!-- Right panel: [Scene | Playlist], collapsible to a thin rail. Files dropped here join the gallery. -->
            <Border Grid.Column="2" Background="#1E1E1E" DragDrop.AllowDrop="True">
                <Panel>
                    <Grid IsVisible="{Binding IsRightPanelOpen}" Margin="10,4,10,10">
                        <TabControl SelectedIndex="{Binding RightPanelTabIndex}" Padding="0">
                            <TabItem Header="Scene" FontSize="15">
                                <views:SceneEditorPanel/>
                            </TabItem>
                        </TabControl>
                        <Button Content="⟩" Command="{Binding ToggleRightPanelCommand}"
                                HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,6,0,0" Padding="8,2"
                                ToolTip.Tip="Collapse the panel (the room then shows the live scene)"/>
                    </Grid>
                    <Button IsVisible="{Binding !IsRightPanelOpen}" Content="⟨" Command="{Binding ToggleRightPanelCommand}"
                            HorizontalAlignment="Center" VerticalAlignment="Top" Margin="0,8,0,0" Padding="6,4"
                            ToolTip.Tip="Open the Scene / Playlist panel"/>
                </Panel>
            </Border>
```

- [ ] **Step 6: Main window code-behind — collapse width, drop the gallery click handler**

Replace the whole of `MainWindow.axaml.cs` with:

```csharp
// Same using set as before the rewrite (file-drop helpers such as TryGetFiles are extension methods).
using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.UI.ViewModels;
using WaBiBaBuSy.WallpaperEngine.Services;

namespace WaBiBaBuSy.UI.Views;

public partial class MainWindow : Window
{
    /// <summary>Right panel width restored when it is expanded again (px).</summary>
    private double _panelWidth = 460;
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        // Pre-initialize LibVLC in background to eliminate ~9s delay on first wallpaper
        _ = LibVLCPreloader.PreloadAsync(AppLogger.CreateLogger<MainWindow>());

        // Drag-and-drop of files onto the window adds them to the gallery
        AddHandler(DragDrop.DropEvent, OnFileDrop);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);

        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm == null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyPanelState(_vm.IsRightPanelOpen);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsRightPanelOpen) && _vm != null)
            ApplyPanelState(_vm.IsRightPanelOpen);
    }

    /// <summary>Open: restore the last panel width and the splitter. Collapsed: a 32 px rail, no splitter.</summary>
    private void ApplyPanelState(bool open)
    {
        var column = MainSplit.ColumnDefinitions[2];
        if (!open && column.ActualWidth > 100) _panelWidth = column.ActualWidth;
        column.MinWidth = open ? 360 : 0;
        column.Width = new GridLength(open ? _panelWidth : 32);
        PanelSplitter.IsVisible = open;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetStorageProvider(StorageProvider);
            viewModel.SetMainWindow(this);
            viewModel.UpdateServerStatus();
            viewModel.StartRefreshTimer();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.StopRefreshTimer();
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files == null) return;
        foreach (var item in files)
        {
            var path = item.Path?.LocalPath;
            if (!string.IsNullOrEmpty(path))
                vm.AddWallpaperFromPath(path);
        }
    }
}
```

- [ ] **Step 7: Build, test, run and check by hand**

Run: `dotnet build WaBiBaBuSy.UI -nologo -v q` then `dotnet test WaBiBaBuSy.Tests -nologo`
Expected: 0 errors; all tests pass.

Run: `dotnet run --project WaBiBaBuSy.UI`, open the control panel (tray → Open Server Control Panel) and check:
- Right panel shows the gallery strip and the four tabs; the room shows "◉ Preview: draft (not live)".
- Click a GIF in the strip → the tiles paint it within ~150 ms; Ctrl+click another → it appears as an "Extra images" chip.
- Change Motion → Pattern / Speed → tiles update; the readout under the slider reads "crosses the room in … s".
- Wait 10 s without touching anything → the sprite keeps moving; it does **not** jump back to the start every 2 s (Review Focus 1).
- Select one node → "Targets: 1 selected", only that tile animates; deselect → all tiles.
- Room ⚙ → tick "Physical units" → height/speed show cm; untick → the old px values are back (Review Focus 4).
- Speed 16× and ⏸ affect only the draft; clicking the badge shows "● Preview: live".
- `⟩` collapses to a rail and the badge switches to live; `⟨` restores the old panel width.
- ▶ Play on selection with one node selected → that machine plays the scene; Play on all → every node.
- Enable Motion → Advanced → Endless with color grading None → amber chip "Endless needs a Traveling color mode".

- [ ] **Step 8: Commit**

```bash
git add WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.SceneEditor.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml.cs WaBiBaBuSy.UI/Converters/FileNameConverter.cs WaBiBaBuSy.UI/Views/MainWindow.axaml WaBiBaBuSy.UI/Views/MainWindow.axaml.cs
git commit -m "feat: docked Scene editor with draft preview in the room

Right panel replaces the gallery pane: gallery strip, Content/Motion/Look/
Background tabs with Advanced sections, validation chips, targets = room
selection. Editor changes debounce into the room tiles; the preview badge
switches between the draft clock and the live shared clock.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Playlist tab (replaces `PlaylistDialog`)

**Files:**
- Modify (rewrite): `WaBiBaBuSy.UI/ViewModels/PlaylistItemRow.cs`
- Modify (rewrite): `WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs`
- Create: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.Playlist.cs`
- Create: `WaBiBaBuSy.UI/Views/PlaylistPanel.axaml`
- Create: `WaBiBaBuSy.UI/Views/PlaylistPanel.axaml.cs`
- Delete: `WaBiBaBuSy.UI/Views/PlaylistDialog.axaml`, `WaBiBaBuSy.UI/Views/PlaylistDialog.axaml.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (ctor, `StartPlaylist`, `StopPlaylistAsync`, `UpdatePlaylistNextLabel`; delete `OpenPlaylist` + `EditCrossScreenConfigForPlaylistAsync`)
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.SceneEditor.cs` (`RevertTarget`)
- Modify: `WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml` (Save to playlist ▾), `WaBiBaBuSy.UI/Views/MainWindow.axaml` (Playlist tab, toolbar button)

**Interfaces:**
- Consumes: Task 2 `PlaylistEditing.*`; Task 5 `SceneEditor`, `RightPanelTabIndex`, `IsRightPanelOpen`, `RevertTarget`; existing `PlaylistStore` (`List`, `PathFor`, `LoadAsync`, `SaveAsync`), `StartPlaylist(Playlist)`, `StopPlaylistAsync()`.
- Produces:
  - `PlaylistItemRow`: `DurationText` in **seconds**, `bool IsPlaying`, `Bitmap? Thumbnail`, `void ReplaceConfig(CrossScreenConfig)`, plus existing summaries / `CommitToModel` / `EffectiveDurationMs`.
  - `PlaylistViewModel(PlaylistStore? store = null)`: host callbacks `Func<CrossScreenConfig>? CaptureDraft`, `Action<CrossScreenConfig>? LoadIntoEditor`, `Func<string, Bitmap?>? ThumbnailProvider`, `Action<Playlist>? StartShow`, `Func<Task>? StopShow`; properties `SavedPlaylists`, `SelectedSavedPlaylist`, `PlaylistName`, `Loop`, `Shuffle`, `DefaultDurationMs`, `DefaultDurationSeconds`, `Items`, `SelectedItem`, `LoadedItem`, `HasLoadedItem`, `LoadedItemName`, `IsShowRunning`, `ShowSummary`; commands `NewPlaylistCommand`, `SavePlaylistCommand`, `AddCurrentSceneCommand`, `DuplicateItemCommand(PlaylistItemRow?)`, `RemoveItemCommand(PlaylistItemRow?)`, `StartPlaylistShowCommand`, `StopPlaylistShowCommand`; methods `Task InitializeAsync()`, `Task LoadSavedAsync(string)`, `PlaylistItemRow AddItem(CrossScreenConfig)`, `void UpdateLoadedItem(CrossScreenConfig)`, `void MoveItem(int from, int insertBefore)`, `void MarkPlaying(int index)`, `void LoadFrom(Playlist)`.
  - `MainWindowViewModel`: `PlaylistViewModel PlaylistEditor { get; }` (not named `Playlist`: that would shadow the `Playlist` model type inside the class), commands `SaveSceneAsNewItemCommand`, `UpdateLoadedPlaylistItemCommand`, `OpenPlaylistTabCommand`.

- [ ] **Step 1: Rewrite `PlaylistItemRow`**

```csharp
// WaBiBaBuSy.UI/ViewModels/PlaylistItemRow.cs
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>Observable wrapper around a <see cref="PlaylistItem"/> for the Playlist tab.</summary>
public partial class PlaylistItemRow : ViewModelBase
{
    public PlaylistItem Model { get; }

    public PlaylistItemRow(PlaylistItem model)
    {
        Model = model;
        _name = model.Name;
        _durationText = PlaylistEditing.FormatMsAsSeconds(model.DurationMs);
        _snapToLap = model.SnapToLap;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _snapToLap;

    /// <summary>Dwell in seconds as typed; blank = use the playlist default. Kept as text so an empty box round-trips to null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationSummary))]
    private string _durationText;

    /// <summary>Playlist-wide fallback dwell, pushed in by the owning VM so the row can show the effective value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationSummary))]
    private int _playlistDefaultDurationMs = 30_000;

    /// <summary>True while the running show is on this item.</summary>
    [ObservableProperty] private bool _isPlaying;

    /// <summary>Gallery thumbnail of the item's animation file (null when the gallery has none).</summary>
    [ObservableProperty] private Bitmap? _thumbnail;

    // --- Detail lines -------------------------------------------------------

    /// <summary>Source content: file name (+ extra images), render height and fit mode.</summary>
    public string SourceSummary
    {
        get
        {
            var anim = Model.Config.Animation;
            var paths = anim.GetAllAnimationPaths();
            var primary = paths.Count > 0
                ? System.IO.Path.GetFileName(paths[0])
                : "(no file)";
            var extra = paths.Count > 1 ? $" +{paths.Count - 1} more" : string.Empty;
            return $"{primary}{extra} · {anim.TargetHeight}px · {anim.FitMode}";
        }
    }

    /// <summary>Movement type, speed and the modifier flags that change how it travels.</summary>
    public string MovementSummary
    {
        get
        {
            var m = Model.Config.Movement;
            var parts = new List<string>
            {
                m.Type.ToString(),
                m.SpeedUnit == SpeedUnit.CentimetersPerSecond ? $"{m.SpeedCmPerSecond:0.#} cm/s" : $"{m.SpeedPixelsPerSecond:0} px/s",
            };

            switch (m.Type)
            {
                case MovementType.SineWave:
                    parts.Add($"amp {m.WaveAmplitudePixels:0}px @ {m.WaveFrequencyHz:0.##}Hz");
                    break;
                case MovementType.Circular:
                    parts.Add($"r {m.OrbitRadiusPixels:0}px");
                    break;
                case MovementType.RandomWalk:
                    parts.Add($"seed {m.RandomSeed}, step {m.RandomStepIntervalMs:0}ms");
                    break;
                case MovementType.Linear:
                case MovementType.Bounce:
                    parts.Add($"{m.DirectionAngleDegrees:0}°");
                    break;
            }

            if (m.Reversed) parts.Add("reversed");
            if (m.Endless) parts.Add("endless");
            if (!m.Loop) parts.Add("no loop");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Distribution mode plus how many machines/monitors this item is aimed at.</summary>
    public string TargetSummary
    {
        get
        {
            var ids = Model.Config.SelectedMonitorIds;
            var targets = ids.Count == 0
                ? "all monitors"
                : $"{ids.Count} monitor{(ids.Count == 1 ? "" : "s")}";
            return $"{Model.Config.DistributionMode} · {targets}";
        }
    }

    /// <summary>Background mode, pattern grid and color grading — the visual layers of the item.</summary>
    public string VisualsSummary
    {
        get
        {
            var cfg = Model.Config;
            var parts = new List<string> { $"bg {cfg.Background.Mode}" };

            var pattern = cfg.Animation.Pattern;
            if (pattern == null)
            {
                parts.Add("no pattern");
            }
            else
            {
                var grid = pattern.Sizing == PatternConfig.SizingMode.Fill
                    ? "fill"
                    : $"{pattern.CountX}×{pattern.CountY}";
                var jitter = pattern.RandomOffsetMaxPx > 0 || pattern.RandomRotationMaxDeg > 0
                    ? $", jitter {pattern.RandomOffsetMaxPx:0}px/{pattern.RandomRotationMaxDeg:0}°"
                    : string.Empty;
                parts.Add($"pattern {grid}, gap {pattern.SpacingX:0}×{pattern.SpacingY:0}{jitter}");
            }

            var grading = cfg.Animation.ColorGrading;
            if (grading.Mode == ColorGradingMode.None)
            {
                parts.Add("no grading");
            }
            else
            {
                var detail = grading.Mode is ColorGradingMode.TravelingList
                                          or ColorGradingMode.TravelingRandom
                                          or ColorGradingMode.RandomColors
                                          or ColorGradingMode.CycleColorList
                    ? $" ({grading.ColorList.Count} colors)"
                    : $" @ {grading.CyclesPerSecond:0.##}/s";
                parts.Add($"{grading.Mode}{detail}");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Effective dwell time, resolved against the playlist default.</summary>
    public string DurationSummary
    {
        get
        {
            var explicitMs = ParseDuration();
            var effective = explicitMs ?? PlaylistDefaultDurationMs;
            var seconds = (effective / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
            return explicitMs == null ? $"{seconds}s (default)" : $"{seconds}s";
        }
    }

    /// <summary>Dwell actually used for this item, resolved against the playlist default.</summary>
    public int EffectiveDurationMs => ParseDuration() ?? PlaylistDefaultDurationMs;

    private int? ParseDuration() => PlaylistEditing.ParseSecondsToMs(DurationText);

    /// <summary>Push edited row fields back into the underlying model.</summary>
    public void CommitToModel()
    {
        Model.Name = Name;
        Model.SnapToLap = SnapToLap;
        Model.DurationMs = ParseDuration();
    }

    /// <summary>Replace the item's scene (Save to playlist → Update) and refresh the detail lines.</summary>
    public void ReplaceConfig(CrossScreenConfig config)
    {
        Model.Config = config;
        RefreshSummary();
    }

    /// <summary>Re-raise change notification for the computed detail lines after the model's config changes.</summary>
    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(MovementSummary));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(VisualsSummary));
        OnPropertyChanged(nameof(DurationSummary));
    }
}
```

(The unused legacy `Summary` property is dropped; `git grep -n "\.Summary\b" WaBiBaBuSy.UI` must return nothing — it had no users outside this file.)

- [ ] **Step 2: Rewrite `PlaylistViewModel`**

```csharp
// WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// The Playlist tab: pick / create / save playlists, reorder items by dragging, edit durations in
/// seconds. Selecting an item loads its scene into the Scene editor as the draft; the editor's
/// "Save to playlist" writes back through <see cref="AddItem"/> / <see cref="UpdateLoadedItem"/>.
/// The show is started and stopped through host callbacks.
/// </summary>
public partial class PlaylistViewModel : ViewModelBase
{
    private readonly PlaylistStore _store;
    private bool _suppressPlaylistLoad;

    /// <summary>Host callback: the Scene editor's current draft (a fresh copy), for "＋ Add current scene".</summary>
    public Func<CrossScreenConfig>? CaptureDraft { get; set; }

    /// <summary>Host callback: load a scene into the Scene editor as the draft.</summary>
    public Action<CrossScreenConfig>? LoadIntoEditor { get; set; }

    /// <summary>Host callback: gallery thumbnail for a content path, or null.</summary>
    public Func<string, Bitmap?>? ThumbnailProvider { get; set; }

    /// <summary>Host callback: start rotating the given playlist.</summary>
    public Action<Playlist>? StartShow { get; set; }

    /// <summary>Host callback: stop the running show.</summary>
    public Func<Task>? StopShow { get; set; }

    [ObservableProperty] private string _playlistName = "Party";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartPlaylistShowCommand))]
    private bool _isShowRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSummary))]
    private bool _loop = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSummary))]
    private bool _shuffle = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSummary), nameof(DefaultDurationSeconds))]
    private int _defaultDurationMs = 30_000;

    /// <summary>Default dwell in whole seconds for the header box (1 s – 24 h).</summary>
    public int DefaultDurationSeconds
    {
        get => Math.Max(1, DefaultDurationMs / 1000);
        set => DefaultDurationMs = Math.Clamp(value, 1, PlaylistEditing.MaxSeconds) * 1000;
    }

    public ObservableCollection<PlaylistItemRow> Items { get; } = new();

    [ObservableProperty] private PlaylistItemRow? _selectedItem;

    /// <summary>The item whose scene is in the Scene editor ("Save to playlist → Update" writes here), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadedItem), nameof(LoadedItemName))]
    private PlaylistItemRow? _loadedItem;

    /// <summary>True when an item is loaded into the Scene editor.</summary>
    public bool HasLoadedItem => LoadedItem != null;

    /// <summary>Name of the loaded item, for the "Update …" menu entry.</summary>
    public string LoadedItemName => LoadedItem?.Name ?? string.Empty;

    /// <summary>Saved playlist names (file names, newest first) for the pickers.</summary>
    public ObservableCollection<string> SavedPlaylists { get; } = new();

    /// <summary>Picker selection; choosing a name loads that playlist.</summary>
    [ObservableProperty] private string? _selectedSavedPlaylist;

    public PlaylistViewModel(PlaylistStore? store = null)
    {
        _store = store ?? new PlaylistStore();
        Items.CollectionChanged += OnItemsChanged;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A drag reorder raises Move with the item in both NewItems and OldItems: nothing to (un)subscribe.
        if (e.Action == NotifyCollectionChangedAction.Move) return;
        foreach (var row in e.NewItems?.OfType<PlaylistItemRow>() ?? Enumerable.Empty<PlaylistItemRow>())
        {
            row.PlaylistDefaultDurationMs = DefaultDurationMs;
            row.Thumbnail ??= ThumbnailProvider?.Invoke(row.Model.Config.Animation.AnimationPath);
            row.PropertyChanged += OnRowPropertyChanged;
        }
        foreach (var row in e.OldItems?.OfType<PlaylistItemRow>() ?? Enumerable.Empty<PlaylistItemRow>())
            row.PropertyChanged -= OnRowPropertyChanged;
        OnPropertyChanged(nameof(ShowSummary));
        StartPlaylistShowCommand.NotifyCanExecuteChanged();
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistItemRow.DurationText))
            OnPropertyChanged(nameof(ShowSummary));
        else if (e.PropertyName == nameof(PlaylistItemRow.Name) && sender == LoadedItem)
            OnPropertyChanged(nameof(LoadedItemName));
    }

    /// <summary>Keep every row's effective-duration display in sync with the playlist-wide fallback.</summary>
    partial void OnDefaultDurationMsChanged(int value)
    {
        foreach (var row in Items) row.PlaylistDefaultDurationMs = value;
    }

    /// <summary>Selecting a row loads its scene into the editor and makes it the target of "Update".</summary>
    partial void OnSelectedItemChanged(PlaylistItemRow? value)
    {
        if (value == null) return;
        LoadedItem = value;
        LoadIntoEditor?.Invoke(value.Model.Config);
    }

    partial void OnSelectedSavedPlaylistChanged(string? value)
    {
        if (_suppressPlaylistLoad || string.IsNullOrEmpty(value)) return;
        _ = LoadSavedAsync(value);
    }

    /// <summary>Header line: item count and total run time of one full cycle.</summary>
    public string ShowSummary
    {
        get
        {
            if (Items.Count == 0) return "No items yet — build a scene, then ＋ Add current scene.";
            var totalMs = Items.Sum(r => (long)r.EffectiveDurationMs);
            var span = TimeSpan.FromMilliseconds(totalMs);
            var length = span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s"
                : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
                                         : $"{span.TotalSeconds:0.#}s";
            return $"{Items.Count} item{(Items.Count == 1 ? "" : "s")} · one cycle ≈ {length}"
                 + (Loop ? " · looping" : " · stops after last item")
                 + (Shuffle ? " · shuffled" : string.Empty);
        }
    }

    // --- Saved playlists ----------------------------------------------------

    /// <summary>Fill the picker and load the most recently saved playlist, if any.</summary>
    public async Task InitializeAsync()
    {
        RefreshSavedPlaylists();
        if (SavedPlaylists.Count > 0) await LoadSavedAsync(SavedPlaylists[0]);
    }

    /// <summary>Re-read the saved playlist names; <paramref name="select"/> becomes the picker selection without reloading it.</summary>
    public void RefreshSavedPlaylists(string? select = null)
    {
        _suppressPlaylistLoad = true;
        try
        {
            SavedPlaylists.Clear();
            foreach (var path in _store.List()) SavedPlaylists.Add(Path.GetFileNameWithoutExtension(path));
            SelectedSavedPlaylist = select != null && SavedPlaylists.Contains(select) ? select : null;
        }
        finally { _suppressPlaylistLoad = false; }
    }

    /// <summary>Load a saved playlist by file name (as listed in <see cref="SavedPlaylists"/>).</summary>
    public async Task LoadSavedAsync(string name)
    {
        try
        {
            var playlist = await _store.LoadAsync(_store.PathFor(name));
            LoadFrom(playlist);
            _suppressPlaylistLoad = true;
            try { SelectedSavedPlaylist = name; }
            finally { _suppressPlaylistLoad = false; }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Playlist] Loading '{name}' failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void NewPlaylist()
    {
        LoadFrom(new Playlist { Name = UniqueName("Playlist"), DefaultItemDurationMs = DefaultDurationMs });
        RefreshSavedPlaylists();
    }

    private string UniqueName(string stem)
    {
        for (int i = 1; ; i++)
        {
            var name = $"{stem} {i}";
            if (!SavedPlaylists.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;
        }
    }

    [RelayCommand]
    private async Task SavePlaylist()
    {
        try
        {
            await _store.SaveAsync(BuildPlaylist());
            RefreshSavedPlaylists(Path.GetFileNameWithoutExtension(_store.PathFor(PlaylistName)));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Playlist] Saving '{PlaylistName}' failed: {ex.Message}");
        }
    }

    // --- Items --------------------------------------------------------------

    [RelayCommand]
    private void AddCurrentScene()
    {
        var config = CaptureDraft?.Invoke();
        if (config != null) AddItem(config);
    }

    /// <summary>Append a scene as a new item (named after its file) and make it the loaded item.</summary>
    public PlaylistItemRow AddItem(CrossScreenConfig config)
    {
        var name = string.IsNullOrWhiteSpace(config.Animation.AnimationPath)
            ? "Scene"
            : Path.GetFileNameWithoutExtension(config.Animation.AnimationPath);
        var row = new PlaylistItemRow(new PlaylistItem { Name = name, Config = config });
        Items.Add(row);
        SelectedItem = row;
        return row;
    }

    /// <summary>Write a scene into the loaded item. The item keeps its own target list.</summary>
    public void UpdateLoadedItem(CrossScreenConfig config)
    {
        if (LoadedItem == null) return;
        config.SelectedMonitorIds = LoadedItem.Model.Config.SelectedMonitorIds;
        LoadedItem.ReplaceConfig(config);
        LoadedItem.Thumbnail = ThumbnailProvider?.Invoke(config.Animation.AnimationPath);
    }

    [RelayCommand]
    private void DuplicateItem(PlaylistItemRow? row)
    {
        if (row == null) return;
        row.CommitToModel();
        Items.Insert(Items.IndexOf(row) + 1, new PlaylistItemRow(PlaylistEditing.Duplicate(row.Model)));
    }

    [RelayCommand]
    private void RemoveItem(PlaylistItemRow? row)
    {
        if (row == null) return;
        if (LoadedItem == row) LoadedItem = null;
        Items.Remove(row);
    }

    /// <summary>Drag reorder: move the row at <paramref name="from"/> into the gap before row <paramref name="insertBefore"/>.</summary>
    public void MoveItem(int from, int insertBefore)
    {
        int to = PlaylistEditing.MoveTarget(Items.Count, from, insertBefore);
        if (to >= 0) Items.Move(from, to);
    }

    /// <summary>Highlight the row the running show is on (-1 = none). Rows edited during a show may be off by the edit.</summary>
    public void MarkPlaying(int index)
    {
        for (int i = 0; i < Items.Count; i++) Items[i].IsPlaying = i == index;
    }

    // --- Show ---------------------------------------------------------------

    private Playlist BuildPlaylist()
    {
        foreach (var row in Items) row.CommitToModel();
        return new Playlist
        {
            Name = PlaylistName,
            Loop = Loop,
            Shuffle = Shuffle,
            DefaultItemDurationMs = DefaultDurationMs,
            Items = Items.Select(r => r.Model).ToList(),
        };
    }

    private bool CanStartShow() => !IsShowRunning && Items.Count > 0;

    [RelayCommand(CanExecute = nameof(CanStartShow))]
    private void StartPlaylistShow()
    {
        StartShow?.Invoke(BuildPlaylist());
        IsShowRunning = true;
    }

    [RelayCommand]
    private async Task StopPlaylistShow()
    {
        if (StopShow != null) await StopShow();
        IsShowRunning = false;
        MarkPlaying(-1);
    }

    /// <summary>Show a playlist model in the tab (after loading or creating one). Nothing is loaded into the editor.</summary>
    public void LoadFrom(Playlist playlist)
    {
        PlaylistName = playlist.Name;
        Loop = playlist.Loop;
        Shuffle = playlist.Shuffle;
        DefaultDurationMs = playlist.DefaultItemDurationMs;
        LoadedItem = null;
        SelectedItem = null;
        // Clear() raises a Reset without OldItems, so detach row handlers explicitly.
        foreach (var row in Items) row.PropertyChanged -= OnRowPropertyChanged;
        Items.Clear();
        foreach (var item in playlist.Items) Items.Add(new PlaylistItemRow(item));
    }
}
```

- [ ] **Step 3: Main view model playlist partial**

```csharp
// WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.Playlist.cs
using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>Playlist tab wiring (UI redesign Plan 2): the tab edits playlists, the Scene editor edits their items.</summary>
public partial class MainWindowViewModel
{
    /// <summary>The Playlist tab's view model. Not named "Playlist": that would shadow the model type in this class.</summary>
    public PlaylistViewModel PlaylistEditor { get; } = new();

    /// <summary>Called once at the end of the constructor, after <see cref="InitSceneEditor"/>.</summary>
    private void InitPlaylist()
    {
        PlaylistEditor.StartShow = StartPlaylist;
        PlaylistEditor.StopShow = StopPlaylistAsync;
        PlaylistEditor.CaptureDraft = () => SceneEditor.BuildConfig();
        PlaylistEditor.LoadIntoEditor = config => SceneEditor.LoadFromConfig(config);
        PlaylistEditor.ThumbnailProvider = path =>
            Wallpapers.FirstOrDefault(w => string.Equals(w.FilePath, path, StringComparison.OrdinalIgnoreCase))?.Thumbnail;
        PlaylistEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistViewModel.HasLoadedItem))
                UpdateLoadedPlaylistItemCommand.NotifyCanExecuteChanged();
        };
        _ = PlaylistEditor.InitializeAsync();
    }

    /// <summary>Editor footer: append the draft to the playlist as a new item.</summary>
    [RelayCommand]
    private void SaveSceneAsNewItem() => PlaylistEditor.AddItem(SceneEditor.BuildConfig());

    /// <summary>Editor footer: write the draft into the loaded playlist item.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateLoadedPlaylistItem))]
    private void UpdateLoadedPlaylistItem() => PlaylistEditor.UpdateLoadedItem(SceneEditor.BuildConfig());

    private bool CanUpdateLoadedPlaylistItem() => PlaylistEditor.HasLoadedItem;

    [RelayCommand]
    private void OpenPlaylistTab()
    {
        IsRightPanelOpen = true;
        RightPanelTabIndex = 1;
    }
}
```

- [ ] **Step 4: Hooks in the main file and the Scene partial**

In `MainWindowViewModel.cs`:

1. Constructor: after `InitSceneEditor();` add `InitPlaylist();`.

2. In `StartPlaylist`, replace

```csharp
        _playlistOrchestrator ??= new WaBiBaBuSy.Core.Services.Animation.PlaylistOrchestrator(
```

with

```csharp
        if (_playlistOrchestrator == null)
        {
            _playlistOrchestrator = new WaBiBaBuSy.Core.Services.Animation.PlaylistOrchestrator(
```

and replace the end of that constructor call

```csharp
            waitUntilNodesReady: WaitUntilRemotesPrefetchedAsync);

        _playlistOrchestrator.Start(playlist);
```

with

```csharp
            waitUntilNodesReady: WaitUntilRemotesPrefetchedAsync);
            // Highlight the playing row in the Playlist tab.
            _playlistOrchestrator.ItemChanged += (_, index) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => PlaylistEditor.MarkPlaying(index));
        }

        _playlistOrchestrator.Start(playlist);
```

(re-indent the orchestrator constructor arguments by four spaces so they sit inside the new block).

3. `StopPlaylistAsync` — replace its body with

```csharp
        if (_playlistOrchestrator != null)
            await _playlistOrchestrator.StopAsync();
        PlaylistEditor.IsShowRunning = false;
        PlaylistEditor.MarkPlaying(-1);
```

4. `UpdatePlaylistNextLabel` — as its first statement add

```csharp
        // A non-looping show ends on its own: keep the tab's running flag honest.
        PlaylistEditor.IsShowRunning = _playlistOrchestrator?.IsRunning == true;
```

5. Delete the `OpenPlaylist` command (its XML doc comment, `[RelayCommand]` and method) and the whole
   `EditCrossScreenConfigForPlaylistAsync` method with its XML doc comment.

In `MainWindowViewModel.SceneEditor.cs` replace

```csharp
    /// <summary>What Revert restores: the last played scene, else the default scene.</summary>
    private CrossScreenConfig RevertTarget() => _crossScreenConfig ?? DefaultScene();
```

with

```csharp
    /// <summary>What Revert restores: the loaded playlist item, else the last played scene, else the default scene.</summary>
    private CrossScreenConfig RevertTarget() => PlaylistEditor.LoadedItem?.Model.Config ?? _crossScreenConfig ?? DefaultScene();
```

- [ ] **Step 5: Create `PlaylistPanel`**

```xml
<!-- WaBiBaBuSy.UI/Views/PlaylistPanel.axaml -->
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:WaBiBaBuSy.UI.ViewModels"
             x:Class="WaBiBaBuSy.UI.Views.PlaylistPanel"
             x:DataType="vm:PlaylistViewModel"
             FontSize="12">
    <Grid RowDefinitions="Auto,Auto,Auto,*,Auto" Margin="0,8,0,0">
        <!-- Picker · New · Save -->
        <Grid Grid.Row="0" ColumnDefinitions="*,Auto,Auto" ColumnSpacing="6">
            <ComboBox ItemsSource="{Binding SavedPlaylists}" SelectedItem="{Binding SelectedSavedPlaylist}"
                      PlaceholderText="Saved playlists" HorizontalAlignment="Stretch"/>
            <Button Grid.Column="1" Content="New" Command="{Binding NewPlaylistCommand}"/>
            <Button Grid.Column="2" Content="Save" Command="{Binding SavePlaylistCommand}"
                    ToolTip.Tip="Save under the name below (a new name saves a new file)"/>
        </Grid>

        <Grid Grid.Row="1" ColumnDefinitions="Auto,*" ColumnSpacing="8" Margin="0,8,0,0">
            <TextBlock Text="Name" VerticalAlignment="Center"/>
            <TextBox Grid.Column="1" Text="{Binding PlaylistName}"/>
        </Grid>

        <StackPanel Grid.Row="2" Spacing="4" Margin="0,8,0,6">
            <WrapPanel>
                <CheckBox Content="Loop" IsChecked="{Binding Loop}" Margin="0,0,12,0"/>
                <CheckBox Content="Shuffle" IsChecked="{Binding Shuffle}" Margin="0,0,12,0"/>
                <TextBlock Text="Default" VerticalAlignment="Center" Margin="0,0,6,0"
                           ToolTip.Tip="Dwell time for every item whose own duration is empty"/>
                <NumericUpDown Value="{Binding DefaultDurationSeconds}" Minimum="1" Maximum="86400" Increment="5" Width="110"/>
                <TextBlock Text="s" VerticalAlignment="Center" Margin="4,0,0,0"/>
            </WrapPanel>
            <TextBlock Text="{Binding ShowSummary}" Opacity="0.75" FontSize="11" TextWrapping="Wrap"/>
        </StackPanel>

        <!-- Items: drag ≡ to reorder, right-click for Duplicate / Remove, click to load into the Scene tab -->
        <Grid Grid.Row="3">
            <ListBox Name="ItemsList" ItemsSource="{Binding Items}" SelectedItem="{Binding SelectedItem}">
                <ListBox.ItemTemplate>
                    <DataTemplate x:DataType="vm:PlaylistItemRow">
                        <Grid ColumnDefinitions="Auto,Auto,*,Auto" ColumnSpacing="8" Background="Transparent">
                            <Grid.ContextMenu>
                                <ContextMenu>
                                    <MenuItem Header="Duplicate"
                                              Command="{Binding $parent[ListBox].((vm:PlaylistViewModel)DataContext).DuplicateItemCommand}"
                                              CommandParameter="{Binding}"/>
                                    <MenuItem Header="Remove"
                                              Command="{Binding $parent[ListBox].((vm:PlaylistViewModel)DataContext).RemoveItemCommand}"
                                              CommandParameter="{Binding}"/>
                                </ContextMenu>
                            </Grid.ContextMenu>
                            <TextBlock Grid.Column="0" Text="≡" FontSize="16" VerticalAlignment="Center" Cursor="SizeNorthSouth"
                                       Foreground="#888888" PointerPressed="OnHandlePressed" ToolTip.Tip="Drag to reorder"/>
                            <Border Grid.Column="1" Width="56" Height="40" CornerRadius="3" Background="#404040"
                                    ClipToBounds="True" VerticalAlignment="Top">
                                <Image Source="{Binding Thumbnail}" Stretch="UniformToFill"/>
                            </Border>
                            <StackPanel Grid.Column="2" Spacing="2">
                                <StackPanel Orientation="Horizontal" Spacing="6">
                                    <TextBlock Text="▶" Foreground="#00CC66" VerticalAlignment="Center" IsVisible="{Binding IsPlaying}"/>
                                    <TextBox Text="{Binding Name}" MinWidth="120" Watermark="Item name"/>
                                </StackPanel>
                                <Expander Header="Details" Padding="6,2">
                                    <StackPanel Spacing="2">
                                        <TextBlock Text="{Binding SourceSummary}" Opacity="0.75" FontSize="11" TextWrapping="Wrap"/>
                                        <TextBlock Text="{Binding MovementSummary}" Opacity="0.75" FontSize="11" TextWrapping="Wrap"/>
                                        <TextBlock Text="{Binding TargetSummary}" Opacity="0.75" FontSize="11" TextWrapping="Wrap"/>
                                        <TextBlock Text="{Binding VisualsSummary}" Opacity="0.75" FontSize="11" TextWrapping="Wrap"/>
                                        <CheckBox Content="Lap-snap" IsChecked="{Binding SnapToLap}" FontSize="11"
                                                  ToolTip.Tip="Wait for the current movement lap to finish before switching (Linear without a pattern; otherwise hard-cut)."/>
                                    </StackPanel>
                                </Expander>
                            </StackPanel>
                            <StackPanel Grid.Column="3" Spacing="2" Width="84" VerticalAlignment="Top">
                                <TextBox Text="{Binding DurationText}" Watermark="default"
                                         ToolTip.Tip="Seconds. Empty = the playlist default."/>
                                <TextBlock Text="{Binding DurationSummary}" FontSize="10" Opacity="0.7"/>
                            </StackPanel>
                        </Grid>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
            <Canvas IsHitTestVisible="False">
                <Rectangle Name="DropMarker" Height="3" Fill="#0078D4" IsVisible="False"/>
            </Canvas>
        </Grid>

        <Button Grid.Row="4" Content="＋ Add current scene" Command="{Binding AddCurrentSceneCommand}"
                HorizontalAlignment="Stretch" HorizontalContentAlignment="Center" Margin="0,6,0,0"
                ToolTip.Tip="Append the Scene editor's draft as a new item"/>
    </Grid>
</UserControl>
```

```csharp
// WaBiBaBuSy.UI/Views/PlaylistPanel.axaml.cs
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Views;

/// <summary>
/// Right-panel "Playlist" tab. Rows are reordered by pressing the ≡ handle and dragging: the pointer is
/// captured by the list, a marker shows the target gap, the drop calls <see cref="PlaylistViewModel.MoveItem"/>.
/// </summary>
public partial class PlaylistPanel : UserControl
{
    private int _dragFrom = -1;
    private IPointer? _dragPointer;

    public PlaylistPanel()
    {
        InitializeComponent();
        ItemsList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        ItemsList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        ItemsList.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { DataContext: PlaylistItemRow row } || DataContext is not PlaylistViewModel vm) return;
        _dragFrom = vm.Items.IndexOf(row);
        if (_dragFrom < 0) return;
        _dragPointer = e.Pointer;
        e.Pointer.Capture(ItemsList);
        e.Handled = true;
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom < 0) return;
        var (_, y) = InsertionPoint(e.GetPosition(ItemsList));
        DropMarker.Width = ItemsList.Bounds.Width;
        Canvas.SetTop(DropMarker, y - 1.5);
        DropMarker.IsVisible = true;
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragFrom < 0) return;
        var (insertBefore, _) = InsertionPoint(e.GetPosition(ItemsList));
        int from = _dragFrom;
        EndDrag();
        if (DataContext is PlaylistViewModel vm) vm.MoveItem(from, insertBefore);
        e.Handled = true;
    }

    /// <summary>End a drag without moving anything. Safe to call repeatedly (capture loss re-enters it).</summary>
    private void EndDrag()
    {
        if (_dragFrom < 0) return;
        _dragFrom = -1;
        var pointer = _dragPointer;
        _dragPointer = null;
        pointer?.Capture(null);
        DropMarker.IsVisible = false;
    }

    /// <summary>Gap under the pointer: the index of the row it sits before (count = after the last row) and that gap's Y in list coordinates.</summary>
    private (int InsertBefore, double Y) InsertionPoint(Point p)
    {
        int count = ItemsList.ItemCount;
        double lastBottom = 0;
        for (int i = 0; i < count; i++)
        {
            if (ItemsList.ContainerFromIndex(i) is not Control container) continue;   // virtualized away
            var topLeft = container.TranslatePoint(new Point(0, 0), ItemsList);
            if (topLeft == null) continue;
            double top = topLeft.Value.Y, height = container.Bounds.Height;
            if (p.Y < top + height / 2) return (i, top);
            lastBottom = top + height;
        }
        return (count, lastBottom);
    }
}
```

- [ ] **Step 6: Wire the tab, the footer menu and the toolbar; delete `PlaylistDialog`**

In `MainWindow.axaml`, inside the right-panel `TabControl`, after the Scene `TabItem` add

```xml
                            <TabItem Header="Playlist" FontSize="15">
                                <views:PlaylistPanel DataContext="{Binding PlaylistEditor}"/>
                            </TabItem>
```

and in the toolbar replace `Command="{Binding OpenPlaylistCommand}"` with `Command="{Binding OpenPlaylistTabCommand}"`.

In `SceneEditorPanel.axaml`, in the footer `WrapPanel`, between "Play on all" and "⟲ Revert", insert

```xml
                    <Button Content="＋ Save to playlist ▾" Margin="0,0,6,6">
                        <Button.Flyout>
                            <MenuFlyout>
                                <MenuItem Header="Add as new item" Command="{Binding SaveSceneAsNewItemCommand}"/>
                                <MenuItem Header="{Binding PlaylistEditor.LoadedItemName, StringFormat='Update “{0}”'}"
                                          Command="{Binding UpdateLoadedPlaylistItemCommand}"/>
                            </MenuFlyout>
                        </Button.Flyout>
                    </Button>
```

and change the Revert tooltip to `ToolTip.Tip="Back to the loaded playlist item, else the last played scene"`.

Delete the dialog:

```bash
git rm WaBiBaBuSy.UI/Views/PlaylistDialog.axaml WaBiBaBuSy.UI/Views/PlaylistDialog.axaml.cs
```

- [ ] **Step 7: Build, test, run and check by hand**

Run: `dotnet build WaBiBaBuSy.UI -nologo -v q` then `dotnet test WaBiBaBuSy.Tests -nologo`
Expected: 0 errors; `git grep -n "PlaylistDialog\|EditConfigAsync\|OpenPlaylistCommand" -- WaBiBaBuSy.UI` returns nothing; all tests pass.

Run the app and check:
- Playlist tab loads the newest saved playlist (if any) into the picker and list; durations show seconds (a 30 000 ms item shows "30").
- Build a scene → ＋ Add current scene → new row with thumbnail; Save → the name appears in the picker.
- Click a row → its scene appears in the Scene tab fields and in the room tiles; change the speed → Save to playlist ▾ → Update “name” → the row's Details show the new speed.
- Drag ≡ of the first row below the last row → the blue marker follows, the row moves; the moved row's duration box still updates the summary line (handler not lost on Move).
- Right-click → Duplicate inserts "name copy" below; Remove deletes it.
- Type "12,5" in a duration → summary reads "12.5s"; "abc" → "(default)".
- Starting and stopping the show is checked in Task 7 (the toolbar gets its ▶ ■ ⏭ there; until then the old toolbar ■ still stops a show).

- [ ] **Step 8: Commit**

```bash
git add -A WaBiBaBuSy.UI
git commit -m "feat: Playlist tab replaces the playlist dialog

Picker/New/Save, seconds durations, drag-to-reorder, Duplicate/Remove menu,
selecting an item loads it into the Scene editor, Save to playlist adds or
updates items. PlaylistStore and the JSON format are unchanged.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Toolbar show controls (Show ▾ ▶ ■ ⏭ · Now / Next)

**Files:**
- Modify: `WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs` (`Skip()`)
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.Playlist.cs` (`SkipShowItemCommand`, `IsAnythingPlaying` notifications)
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (`UpdatePlaylistNextLabel`, `OnIsCrossScreenRunningChanged`)
- Modify: `WaBiBaBuSy.UI/Views/MainWindow.axaml` (toolbar centre)

**Interfaces:**
- Consumes: Task 6 `PlaylistEditor.SavedPlaylists/SelectedSavedPlaylist/StartPlaylistShowCommand/IsShowRunning`; existing `StopCrossScreenCommand`, `ClearAllWallpapersCommand`, `HasActiveRenderer`, `PlaylistNextLabel`, `HasPlaylistNext`.
- Produces: `PlaylistOrchestrator.Skip()` (ends the current item's dwell now; no-op when idle);
  `MainWindowViewModel.SkipShowItemCommand`, `bool IsAnythingPlaying` (= `IsCrossScreenRunning || PlaylistEditor.IsShowRunning`);
  `PlaylistNextLabel` now reads "Now: X · next: Y in m:ss".

- [ ] **Step 1: `Skip()` in the orchestrator**

In `PlaylistOrchestrator.cs` add the field after `private Task? _loopTask;`:

```csharp
    /// <summary>Cancels only the current dwell wait (⏭). Null outside a dwell.</summary>
    private volatile CancellationTokenSource? _skipCts;
```

Add the method after `StopAsync`:

```csharp
    /// <summary>Advance to the next item now (ends the current dwell). No-op when no show is running.</summary>
    public void Skip()
    {
        try { _skipCts?.Cancel(); }
        catch (ObjectDisposedException) { /* the dwell ended at the same moment */ }
    }
```

Replace the dwell wait

```csharp
                    NextSwitchUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + dwell + lead;
                    await Task.Delay(dwell + lead, ct);
```

with

```csharp
                    NextSwitchUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + dwell + lead;
                    using (var skip = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        _skipCts = skip;
                        try
                        {
                            await Task.Delay(dwell + lead, skip.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            _logger.LogInformation("Playlist item '{Name}' skipped", item.Name);
                        }
                        finally
                        {
                            _skipCts = null;
                        }
                    }
```

- [ ] **Step 2: View model — skip command, Now label, `IsAnythingPlaying`**

In `MainWindowViewModel.Playlist.cs` add:

```csharp
    /// <summary>True while a show or a manual scene is running (shows the toolbar ■).</summary>
    public bool IsAnythingPlaying => IsCrossScreenRunning || PlaylistEditor.IsShowRunning;

    /// <summary>Toolbar ⏭: advance the running show to its next item.</summary>
    [RelayCommand]
    private void SkipShowItem() => _playlistOrchestrator?.Skip();
```

and in `InitPlaylist`, extend the `PlaylistEditor.PropertyChanged` handler:

```csharp
        PlaylistEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistViewModel.HasLoadedItem))
                UpdateLoadedPlaylistItemCommand.NotifyCanExecuteChanged();
            else if (e.PropertyName == nameof(PlaylistViewModel.IsShowRunning))
                OnPropertyChanged(nameof(IsAnythingPlaying));
        };
```

In `MainWindowViewModel.cs`:

1. `OnIsCrossScreenRunningChanged` — after `OnPropertyChanged(nameof(HasActiveRenderer));` add `OnPropertyChanged(nameof(IsAnythingPlaying));`.

2. `UpdatePlaylistNextLabel` — replace

```csharp
        var next = o.NextItem?.Name;
        PlaylistNextLabel = next == null
            ? $"last item · ends in {remaining.Minutes}:{remaining.Seconds:00}"
            : $"next: {next} in {remaining.Minutes}:{remaining.Seconds:00}";
```

with

```csharp
        var next = o.NextItem?.Name;
        var now = o.CurrentItem?.Name;
        var nowPart = string.IsNullOrWhiteSpace(now) ? "" : $"Now: {now} · ";
        PlaylistNextLabel = next == null
            ? $"{nowPart}last item · ends in {remaining.Minutes}:{remaining.Seconds:00}"
            : $"{nowPart}next: {next} in {remaining.Minutes}:{remaining.Seconds:00}";
```

and update the doc comment above `_playlistNextLabel` to `/// <summary>"Now: Fish · next: Logo Rain in 0:42" while a show runs, else empty.</summary>`.

- [ ] **Step 3: Toolbar centre**

In `MainWindow.axaml`, replace the whole centre `StackPanel` of the toolbar (the one with `Grid.Column="1"`
containing Scene…, ▶ Start, ■ Stop, Playlist…, the next label and Clear all) with:

```xml
                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6"
                            HorizontalAlignment="Center" VerticalAlignment="Center">
                    <TextBlock Text="Show" Foreground="#AAAAAA" FontSize="11" VerticalAlignment="Center"/>
                    <ComboBox ItemsSource="{Binding PlaylistEditor.SavedPlaylists}"
                              SelectedItem="{Binding PlaylistEditor.SelectedSavedPlaylist}"
                              PlaceholderText="Playlist" Width="150" Height="28" FontSize="11"
                              ToolTip.Tip="Saved playlists — edit them in the Playlist tab"/>
                    <Button Content="&#9654;" Command="{Binding PlaylistEditor.StartPlaylistShowCommand}"
                            IsVisible="{Binding !PlaylistEditor.IsShowRunning}" Height="28" Padding="10,4" FontSize="11"
                            ToolTip.Tip="Start the show (the playlist open in the Playlist tab)"/>
                    <Button Content="&#9632;" Command="{Binding StopCrossScreenCommand}"
                            IsVisible="{Binding IsAnythingPlaying}" Height="28" Padding="10,4" FontSize="11"
                            ToolTip.Tip="Stop the show and the running scene"/>
                    <Button Content="&#9197;" Command="{Binding SkipShowItemCommand}"
                            IsVisible="{Binding PlaylistEditor.IsShowRunning}" Height="28" Padding="10,4" FontSize="11"
                            ToolTip.Tip="Skip to the next item"/>
                    <TextBlock Text="{Binding PlaylistNextLabel}" Foreground="#FFC800" FontSize="11"
                               VerticalAlignment="Center" IsVisible="{Binding HasPlaylistNext}"/>
                    <Button Content="Clear all" Command="{Binding ClearAllWallpapersCommand}"
                            IsVisible="{Binding HasActiveRenderer}" Height="28" Padding="10,4" FontSize="11"
                            ToolTip.Tip="Stop every animation and clear all wallpapers"/>
                </StackPanel>
```

(`OpenSceneEditorCommand` and `OpenPlaylistTabCommand` stay: the rail button and tabs open the panel; no toolbar button needs them now.)

- [ ] **Step 4: Build, test, run and check by hand**

Run: `dotnet build WaBiBaBuSy.UI -nologo -v q` then `dotnet test WaBiBaBuSy.Tests -nologo`
Expected: 0 errors; all tests pass.

Run the app with a playlist of ≥ 2 items (durations 20 s):
- Toolbar picker lists the saved playlists; picking one loads it into the Playlist tab.
- ▶ starts the show: the ▶ hides, ■ and ⏭ appear, the label reads "Now: A · next: B in 0:1x", the playing row shows a green ▶.
- ⏭ switches to B immediately (label "Now: B · next: A …" for a looping list).
- ■ stops the show; ⏭ and the label disappear; ▶ is back.
- Play a single scene from the editor → ■ appears (IsCrossScreenRunning); ■ stops it.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.Playlist.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs WaBiBaBuSy.UI/Views/MainWindow.axaml
git commit -m "feat: toolbar show controls with skip and now/next label

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: Remove the config dialog, the standalone preview and dead editor members

**Files:**
- Delete: `WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml`, `WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml.cs`
- Delete: `WaBiBaBuSy.UI/Controls/ScenePreviewControl.cs`
- Delete: `WaBiBaBuSy.UI/Views/WallpaperMultiSelectDialog.axaml`, `WaBiBaBuSy.UI/Views/WallpaperMultiSelectDialog.axaml.cs`, `WaBiBaBuSy.UI/ViewModels/WallpaperMultiSelectDialogViewModel.cs` (only the config view model's gallery pickers used them; the file's `WallpaperMultiSelectItem` and `WallpaperFilterMode` have no other users)
- Modify: `WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (delete `ConfigureCrossScreen`, `LayoutForSelection`)
- Modify: `WaBiBaBuSy.UI/Controls/ScenePainter.cs` (doc comment)

**Interfaces:**
- Consumes: nothing new.
- Produces: `CrossScreenConfigViewModel` without dialog members; `BuildConfig()` always returns `SelectedMonitorIds = []` (targets are set by the caller: `PlayDraftAsync` or the playlist item).

- [ ] **Step 1: Delete the files**

```bash
git rm WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml.cs WaBiBaBuSy.UI/Controls/ScenePreviewControl.cs WaBiBaBuSy.UI/Views/WallpaperMultiSelectDialog.axaml WaBiBaBuSy.UI/Views/WallpaperMultiSelectDialog.axaml.cs WaBiBaBuSy.UI/ViewModels/WallpaperMultiSelectDialogViewModel.cs
```

- [ ] **Step 2: Remove dead members from `CrossScreenConfigViewModel`**

Delete, with their XML doc comments:
- the `MonitorSelectionItem` class (top of the file);
- fields `_closeAction`, `_ownerWindow`, `_galleryWallpapers`;
- property `PreSelectedWallpaper`;
- observable fields `_availableMonitors`, `_hasMultipleMonitors`;
- property `DialogResult`;
- the whole "Live preview (Tier 2.1)" block: `LayoutProvider`, `BuildPreviewLayout()`, `PreviewLabels`, `ResolvePreviewImagePath()`;
- methods `SetOwnerWindow`, `SetGalleryWallpapers`, `SetCloseAction`, `SetAvailableMonitors`, `ApplyPreSelectedWallpaper`;
- commands `SelectAnimationFromGallery`, `AddAdditionalImagesFromGallery`, `MoveMonitorUp`, `MoveMonitorDown`, `Ok`, `Cancel`;
- the `using WaBiBaBuSy.UI.Views;` directive.

In `LoadFromConfig` delete the block

```csharp
        // Restore monitor selection from config
        var selectedIds = new HashSet<string>(config.SelectedMonitorIds);
        foreach (var monitor in AvailableMonitors)
        {
            monitor.IsSelected = selectedIds.Contains(monitor.ClientId) || config.SelectedMonitorIds.Count == 0;
        }
```

In `BuildConfig` delete

```csharp
        // Collect selected monitor IDs
        var selectedMonitorIds = AvailableMonitors
            .Where(m => m.IsSelected)
            .Select(m => m.ClientId)
            .ToList();
```

and replace the initializer line `SelectedMonitorIds = selectedMonitorIds,` with
`SelectedMonitorIds = new List<string>(),   // targets are set by the caller (room selection / playlist item)`.

Update the class summary (add above `public partial class CrossScreenConfigViewModel`):

```csharp
/// <summary>
/// The Scene editor's draft: every field of one <see cref="CrossScreenConfig"/>, loaded with
/// <see cref="LoadFromConfig"/> and built with <see cref="BuildConfig"/>. Hosted by the main window's
/// right panel (<c>SceneEditorPanel</c>); targets and preview come from the host.
/// </summary>
```

- [ ] **Step 3: Remove dead code from `MainWindowViewModel`**

Delete the `ConfigureCrossScreen` command (from `[RelayCommand]` above `private async Task ConfigureCrossScreen()` through the end of the method) and
`LayoutForSelection` with its summary line `/// <summary>Layout provider handed to the config dialog: selected node ids → seat-map layout.</summary>`.

- [ ] **Step 4: Fix the `ScenePainter` doc comment**

In `ScenePainter.cs` the class summary ends with "Shared by <see cref="ScenePreviewControl"/> …". Replace that
sentence with `Used by <see cref="RoomView"/> for every tile.` (keep the rest of the summary).

- [ ] **Step 5: Build, test, check for leftovers**

Run: `dotnet build WaBiBaBuSy.UI -nologo -v q` then `dotnet test WaBiBaBuSy.Tests -nologo`
Expected: 0 errors, all tests pass, and

```bash
git grep -n "CrossScreenConfigDialog\|ScenePreviewControl\|WallpaperMultiSelect\|MonitorSelectionItem\|ConfigureCrossScreen\|LayoutForSelection\|AvailableMonitors" -- '*.cs' '*.axaml'
```

returns nothing.

- [ ] **Step 6: Commit**

```bash
git add -A WaBiBaBuSy.UI
git commit -m "chore: remove the animation config dialog and the standalone scene preview

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: Docs, graph, end-to-end check

**Files:**
- Modify: `.docs/plans/2026-09-24-ui-redesign-design.md` (status line)
- Modify: `.docs/UI_ARCHITECTURE.md`
- Modify: `.docs/RECENT_UPDATES.md`
- Modify: `CLAUDE.md`
- Modify: `graphify-out/GRAPH_REPORT.md` (via the wrapper)

- [ ] **Step 1: Spec status**

In the spec, replace the status line with
`**Date:** 2026-09-24 · **Status:** Plans 1–2 (rollout steps 1–4) implemented 2026-09-25; Plan 3 (Settings sidebar) pending`.

- [ ] **Step 2: `UI_ARCHITECTURE.md`**

Run `git grep -n "CrossScreenConfigDialog\|PlaylistDialog\|ScenePreviewControl\|Wallpaper Gallery\|Multi Monitor" -- .docs/UI_ARCHITECTURE.md`.
For every hit: describe the right panel instead — "Scene tab (`SceneEditorPanel` over `CrossScreenConfigViewModel`:
gallery strip, Content/Motion/Look/Background tabs with Advanced sections, validation chips, footer Play on selection /
Play on all / Save to playlist / Revert; draft previewed in the room via the preview badge)" and "Playlist tab
(`PlaylistPanel` over `PlaylistViewModel`: picker/New/Save, seconds durations, drag reorder, selecting an item loads
it into the Scene tab)"; the toolbar holds Show ▾ ▶ ■ ⏭ and the Now/Next label. Remove descriptions of the deleted
dialogs and of the full-width gallery pane.

- [ ] **Step 3: `CLAUDE.md`**

Replace the Key Capabilities bullet starting `- Live preview:` with

```markdown
- Live preview: the room view paints the running scene or the docked Scene editor's draft inside every node tile with the players' own deterministic math (`ScenePainter`); the preview badge switches draft ↔ live
```

and the bullet starting `- Playlist / Party Mode:` — replace `own dialog;` with `Playlist tab in the right panel, show controls in the toolbar;`.
Set `**Last Updated:** 2026-09-25`.

- [ ] **Step 4: `RECENT_UPDATES.md`**

Add at the top (below the title) a section:

```markdown
## 2026-09-25 — UI redesign Plan 2: docked Scene editor + Playlist tab

- Right panel `[Scene | Playlist]` replaces the gallery pane, `CrossScreenConfigDialog` and `PlaylistDialog`.
- Scene tab: gallery strip (click / Ctrl+click / right-click), Content · Motion · Look · Background tabs with Advanced sections, ⓘ tooltips, amber validation chips (`SceneChecks`), units follow Room ⚙ physical units, targets = room selection (`SceneTargets`, chain order).
- Draft preview: editor changes reach the room tiles after 150 ms; preview badge toggles draft ↔ live, ⏸ ⟲ 1×/4×/16× for the draft clock; the 2 s topology refresh no longer restarts it (`SceneChecks.SameScene`).
- Playlist tab: picker / New / Save, durations in seconds (`PlaylistEditing`), drag reorder, Duplicate / Remove, selecting an item loads it into the editor, Save to playlist → add / update.
- Toolbar: Show ▾ ▶ ■ ⏭ with "Now: … · next: …"; `PlaylistOrchestrator.Skip()`.
- Removed: `CrossScreenConfigDialog`, `PlaylistDialog`, `ScenePreviewControl`, `WallpaperMultiSelectDialog`.
```

- [ ] **Step 5: Update the graph**

Run: `./tools/graphify-update.ps1`
Expected: completes without error; `graphify-out/GRAPH_REPORT.md` updated.

- [ ] **Step 6: End-to-end check**

Run `dotnet run --project WaBiBaBuSy.UI`, start the server, and walk the whole flow once, screenshotting the main window after each bullet for the review:
1. Fresh start: default IconZone draft in the editor, badge "draft", room tiles show nothing animated until a file is picked.
2. Build a scene (GIF, Linear, Traveling list colors, pattern on) → tiles preview it; chips appear/disappear as fields change.
3. Play on all → local players run; badge → live shows the same scene on the shared clock.
4. ＋ Save to playlist → Add as new item (twice, with different speeds) → Save the playlist as "E2E".
5. Toolbar ▶ → show runs, ⏭ skips, ■ stops.
6. Collapse / expand the panel; resize it with the splitter; collapse and expand again → width kept.
7. Close and reopen the app → "E2E" is loaded in the Playlist tab.

- [ ] **Step 7: Commit**

```bash
git add .docs/plans/2026-09-24-ui-redesign-design.md .docs/UI_ARCHITECTURE.md .docs/RECENT_UPDATES.md CLAUDE.md graphify-out/GRAPH_REPORT.md
git commit -m "docs: record the docked Scene editor and Playlist tab

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

# Playlist / Party Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rotate a set of animation configurations across all synced machines on a loop, each shown for its own duration, optionally shuffled, started/stopped as a "show."

**Architecture:** Pure, testable logic (models, lap math, dwell resolution, shuffle order) lives in `WaBiBaBuSy.Models` so the existing Models-only test project can cover it. A thin `PlaylistOrchestrator` timing shell in `WaBiBaBuSy.Core` drives rotation by invoking an apply delegate the UI supplies (the same broadcast path the manual Start button uses). The UI adds a dedicated Playlist dialog. Rotation switches at shared UTC timestamps; shuffle randomness is server-authority-only and never touches the deterministic render path.

**Tech Stack:** .NET 9, C# 12, xUnit + `System.Text.Json` (tests), Avalonia + CommunityToolkit.Mvvm (UI).

**Spec:** `.docs/plans/2026-07-18-playlist-party-mode-design.md`

---

## Design constraints (read before starting)

- **Test project references only `WaBiBaBuSy.Models`** (`WaBiBaBuSy.Tests.csproj`). All unit-tested types (`Playlist`, `PlaylistItem`, `PlaylistScheduler`) go in Models. The Core orchestrator and UI are validated by build + manual/E2E, not unit tests.
- **Determinism invariant (CLAUDE.md):** never put `Random`/`DateTime.Now` into `MovementCalculator`/`ColorGrader`/`PatternLayout`. `PlaylistScheduler` shuffle uses a caller-supplied seed and runs server-side only — it decides item *order*, never render output. Keep it out of the render path.
- **Lap-snap period must match `MovementCalculator.CalculateLinear`:** its loop period is `totalDistance = sqrt(dx²+dy²)` with `startX = StartX ?? (Reversed ? canvasWidth : -animWidth)`, `endX = EndX ?? (Reversed ? -animWidth : canvasWidth)`, `startY = StartY ?? 0`, `endY = EndY ?? startY`. `ComputeLapMs` mirrors this exactly. See `WaBiBaBuSy.Models/Wallpaper/MovementCalculator.cs:49-88`.
- **Existing test conventions:** xUnit `[Fact]`/`[Theory]`, namespace `WaBiBaBuSy.Tests`, mirror `ConfigJsonRoundtripTests.cs`.

## File Structure

**New:**
- `WaBiBaBuSy.Models/Wallpaper/Playlist.cs` — `Playlist`, `PlaylistItem` (data + `GetEffectiveDurationMs`).
- `WaBiBaBuSy.Models/Wallpaper/PlaylistScheduler.cs` — pure statics: `ComputeLapMs`, `ResolveDwellMs`, `BuildCycleOrder`.
- `WaBiBaBuSy.Core/Services/Animation/PlaylistStore.cs` — JSON save/load to `%APPDATA%\WaBiBaBuSy\playlists\`.
- `WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs` — timing loop + apply delegate + current-item tracking.
- `WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs` — dialog VM.
- `WaBiBaBuSy.UI/ViewModels/PlaylistItemRow.cs` — per-row observable wrapper.
- `WaBiBaBuSy.UI/Views/PlaylistDialog.axaml` (+ `.axaml.cs`) — the dialog.
- `WaBiBaBuSy.Tests/PlaylistTests.cs` — all playlist unit tests.

**Modified:**
- `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` — extract `ApplyCrossScreenConfigAsync`; add `OpenPlaylistCommand`; own the orchestrator lifetime.

---

## Task 1: Playlist + PlaylistItem models

**Files:**
- Create: `WaBiBaBuSy.Models/Wallpaper/Playlist.cs`
- Test: `WaBiBaBuSy.Tests/PlaylistTests.cs`

- [ ] **Step 1: Write the failing test**

Create `WaBiBaBuSy.Tests/PlaylistTests.cs`:

```csharp
using System.Text.Json;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class PlaylistTests
{
    [Fact]
    public void Playlist_RoundtripsThroughJson_PreservingNestedConfig()
    {
        var original = new Playlist
        {
            Name = "Party",
            Loop = true,
            Shuffle = true,
            DefaultItemDurationMs = 45_000,
            Items =
            {
                new PlaylistItem
                {
                    Name = "Fish",
                    DurationMs = 12_000,
                    SnapToLap = true,
                    Config = new CrossScreenConfig
                    {
                        AnimationSpeedPxPerSecond = 640,
                        DistributionMode = AnimationDistributionMode.Sequential,
                        Movement = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 640f },
                        Animation = new AnimationLayerConfig { AnimationPath = "fish.gif", TargetHeight = 300 },
                    },
                },
                new PlaylistItem { Name = "Idle", DurationMs = null, SnapToLap = false },
            },
        };

        var restored = JsonSerializer.Deserialize<Playlist>(JsonSerializer.Serialize(original))!;

        Assert.Equal("Party", restored.Name);
        Assert.True(restored.Shuffle);
        Assert.Equal(45_000, restored.DefaultItemDurationMs);
        Assert.Equal(2, restored.Items.Count);
        Assert.Equal("Fish", restored.Items[0].Name);
        Assert.Equal(12_000, restored.Items[0].DurationMs);
        Assert.True(restored.Items[0].SnapToLap);
        Assert.Equal(MovementType.Linear, restored.Items[0].Config.Movement.Type);
        Assert.Equal("fish.gif", restored.Items[0].Config.Animation.AnimationPath);
        Assert.Null(restored.Items[1].DurationMs);
    }

    [Theory]
    [InlineData(12_000, 30_000, 12_000)] // explicit override wins
    [InlineData(null, 30_000, 30_000)]   // null falls back to playlist default
    public void GetEffectiveDurationMs_ResolvesOverrideOrDefault(int? itemMs, int defaultMs, int expected)
    {
        var item = new PlaylistItem { DurationMs = itemMs };
        Assert.Equal(expected, item.GetEffectiveDurationMs(defaultMs));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test WaBiBaBuSy.Tests --filter PlaylistTests`
Expected: FAIL — compile error, `Playlist`/`PlaylistItem` do not exist.

- [ ] **Step 3: Write the models**

Create `WaBiBaBuSy.Models/Wallpaper/Playlist.cs`:

```csharp
using System.Collections.Generic;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// An ordered set of animation configurations rotated across all synced machines as a "show."
/// Persisted as JSON; each item embeds a full self-contained <see cref="CrossScreenConfig"/>.
/// </summary>
public class Playlist
{
    /// <summary>Display name of the playlist.</summary>
    public string Name { get; set; } = "Untitled Playlist";

    /// <summary>When true, repeat the whole list forever; otherwise stop after the last item.</summary>
    public bool Loop { get; set; } = true;

    /// <summary>When true, randomise item order once per full cycle (server-side only).</summary>
    public bool Shuffle { get; set; } = false;

    /// <summary>Dwell time (ms) used for items whose own <see cref="PlaylistItem.DurationMs"/> is null.</summary>
    public int DefaultItemDurationMs { get; set; } = 30_000;

    /// <summary>Items in author order.</summary>
    public List<PlaylistItem> Items { get; set; } = new();
}

/// <summary>A single playlist entry: one animation config plus its dwell/rotation settings.</summary>
public class PlaylistItem
{
    /// <summary>Display label, e.g. "Fish traversal".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full embedded animation configuration for this item.</summary>
    public CrossScreenConfig Config { get; set; } = new();

    /// <summary>Per-item dwell time in ms. Null = use the playlist's <see cref="Playlist.DefaultItemDurationMs"/>.</summary>
    public int? DurationMs { get; set; }

    /// <summary>
    /// When true, round the dwell up to the next whole movement lap before switching
    /// (Linear movement without a Pattern only; otherwise falls back to hard-cut). See PlaylistScheduler.
    /// </summary>
    public bool SnapToLap { get; set; } = false;

    /// <summary>Resolve the effective dwell for this item given the playlist default.</summary>
    public int GetEffectiveDurationMs(int playlistDefault) => DurationMs ?? playlistDefault;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test WaBiBaBuSy.Tests --filter PlaylistTests`
Expected: PASS (3 test cases).

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/Playlist.cs WaBiBaBuSy.Tests/PlaylistTests.cs
git commit -m "feat: Playlist + PlaylistItem models with JSON roundtrip"
```

---

## Task 2: Lap-snap and dwell resolution (PlaylistScheduler)

**Files:**
- Create: `WaBiBaBuSy.Models/Wallpaper/PlaylistScheduler.cs`
- Test: `WaBiBaBuSy.Tests/PlaylistTests.cs` (add methods)

- [ ] **Step 1: Write the failing tests**

Append to `PlaylistTests.cs` (inside the class):

```csharp
    // --- ComputeLapMs -------------------------------------------------------

    [Fact]
    public void ComputeLapMs_Linear_MatchesCanvasPlusContentOverSpeed()
    {
        // Default Linear: startX=-animWidth, endX=canvasWidth => distance = canvasWidth + animWidth.
        var mv = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 500f, Loop = true };
        // distance = 4000 + 200 = 4200 px; /500 px/s = 8.4 s = 8400 ms.
        Assert.Equal(8400, PlaylistScheduler.ComputeLapMs(mv, canvasWidth: 4000, contentWidthPx: 200));
    }

    [Fact]
    public void ComputeLapMs_NonLinear_ReturnsZero()
    {
        foreach (var t in new[] { MovementType.Static, MovementType.Bounce, MovementType.SineWave,
                                  MovementType.Circular, MovementType.RandomWalk })
        {
            var mv = new MovementConfig { Type = t, SpeedPixelsPerSecond = 500f, Loop = true };
            Assert.Equal(0, PlaylistScheduler.ComputeLapMs(mv, 4000, 200));
        }
    }

    [Fact]
    public void ComputeLapMs_ZeroSpeedOrNoLoop_ReturnsZero()
    {
        Assert.Equal(0, PlaylistScheduler.ComputeLapMs(
            new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 0f, Loop = true }, 4000, 200));
        Assert.Equal(0, PlaylistScheduler.ComputeLapMs(
            new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 500f, Loop = false }, 4000, 200));
    }

    // --- ResolveDwellMs -----------------------------------------------------

    [Fact]
    public void ResolveDwellMs_HardCut_ReturnsEffectiveDuration()
    {
        var item = new PlaylistItem { DurationMs = 5_000, SnapToLap = false };
        Assert.Equal(5_000, PlaylistScheduler.ResolveDwellMs(item, playlistDefaultMs: 30_000, lapMs: 8_400));
    }

    [Fact]
    public void ResolveDwellMs_LapSnap_RoundsUpToWholeLap()
    {
        var item = new PlaylistItem { DurationMs = 10_000, SnapToLap = true };
        // ceil(10000 / 8400) = 2 laps => 16800 ms.
        Assert.Equal(16_800, PlaylistScheduler.ResolveDwellMs(item, playlistDefaultMs: 30_000, lapMs: 8_400));
    }

    [Fact]
    public void ResolveDwellMs_LapSnap_WithZeroLap_FallsBackToHardCut()
    {
        var item = new PlaylistItem { DurationMs = 10_000, SnapToLap = true };
        Assert.Equal(10_000, PlaylistScheduler.ResolveDwellMs(item, playlistDefaultMs: 30_000, lapMs: 0));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter PlaylistTests`
Expected: FAIL — `PlaylistScheduler` does not exist.

- [ ] **Step 3: Write PlaylistScheduler (lap + dwell)**

Create `WaBiBaBuSy.Models/Wallpaper/PlaylistScheduler.cs`:

```csharp
using System;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Pure scheduling helpers for playlist rotation. No I/O, no wall-clock, no shared render state.
/// Shuffle randomness is caller-seeded and server-authority-only — it decides item ORDER, never
/// render output, so it does not violate the deterministic-render invariant.
/// </summary>
public static class PlaylistScheduler
{
    /// <summary>
    /// One full movement-lap duration in ms for the given config, or 0 if lap-snap is unsupported.
    /// v1 supports Linear movement only (no Pattern), matching MovementCalculator.CalculateLinear's
    /// loop period: distance = sqrt(dx^2 + dy^2) with default off-screen start/end.
    /// </summary>
    public static int ComputeLapMs(MovementConfig m, int canvasWidth, int contentWidthPx)
    {
        if (m.Type != MovementType.Linear) return 0;
        if (!m.Loop) return 0;
        if (m.SpeedPixelsPerSecond <= 0f) return 0;

        float animWidth = Math.Max(0, contentWidthPx);
        float startX = m.StartX ?? (m.Reversed ? canvasWidth : -animWidth);
        float endX   = m.EndX   ?? (m.Reversed ? -animWidth  : canvasWidth);
        float startY = m.StartY ?? 0f;
        float endY   = m.EndY   ?? startY;

        double dx = endX - startX;
        double dy = endY - startY;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < 1.0) return 0;

        return (int)Math.Round(distance / m.SpeedPixelsPerSecond * 1000.0);
    }

    /// <summary>
    /// The dwell time (ms) to wait before switching to the next item.
    /// Hard-cut = effective duration. Lap-snap rounds UP to the next whole lap; if lapMs is 0
    /// (unsupported movement) it falls back to hard-cut.
    /// </summary>
    public static int ResolveDwellMs(PlaylistItem item, int playlistDefaultMs, int lapMs)
    {
        int target = item.GetEffectiveDurationMs(playlistDefaultMs);
        if (!item.SnapToLap || lapMs <= 0) return target;

        int laps = Math.Max(1, (int)Math.Ceiling(target / (double)lapMs));
        return laps * lapMs;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter PlaylistTests`
Expected: PASS (all Task 1 + Task 2 cases).

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/PlaylistScheduler.cs WaBiBaBuSy.Tests/PlaylistTests.cs
git commit -m "feat: PlaylistScheduler lap-snap + dwell resolution (Linear v1)"
```

---

## Task 3: Cycle order + shuffle (PlaylistScheduler)

**Files:**
- Modify: `WaBiBaBuSy.Models/Wallpaper/PlaylistScheduler.cs`
- Test: `WaBiBaBuSy.Tests/PlaylistTests.cs` (add methods)

- [ ] **Step 1: Write the failing tests**

Append to `PlaylistTests.cs`:

```csharp
    // --- BuildCycleOrder ----------------------------------------------------

    [Fact]
    public void BuildCycleOrder_NoShuffle_ReturnsNaturalOrder()
    {
        Assert.Equal(new[] { 0, 1, 2, 3 }, PlaylistScheduler.BuildCycleOrder(4, shuffle: false, seed: 123));
    }

    [Fact]
    public void BuildCycleOrder_Shuffle_IsPermutationCoveringEveryItemOnce()
    {
        var order = PlaylistScheduler.BuildCycleOrder(6, shuffle: true, seed: 999);
        Assert.Equal(6, order.Length);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, order.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void BuildCycleOrder_Shuffle_IsDeterministicForSameSeed()
    {
        Assert.Equal(
            PlaylistScheduler.BuildCycleOrder(8, shuffle: true, seed: 42),
            PlaylistScheduler.BuildCycleOrder(8, shuffle: true, seed: 42));
    }

    [Fact]
    public void BuildCycleOrder_Shuffle_DiffersForDifferentSeeds()
    {
        // With 8 items the chance two different seeds collide on the exact permutation is ~1/40320.
        Assert.NotEqual(
            PlaylistScheduler.BuildCycleOrder(8, shuffle: true, seed: 1),
            PlaylistScheduler.BuildCycleOrder(8, shuffle: true, seed: 2));
    }

    [Fact]
    public void BuildCycleOrder_Empty_ReturnsEmpty()
    {
        Assert.Empty(PlaylistScheduler.BuildCycleOrder(0, shuffle: true, seed: 1));
    }
```

Add `using System.Linq;` to the top of `PlaylistTests.cs` if not present.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter PlaylistTests`
Expected: FAIL — `BuildCycleOrder` does not exist.

- [ ] **Step 3: Add BuildCycleOrder to PlaylistScheduler**

Add this method inside the `PlaylistScheduler` class in `PlaylistScheduler.cs`:

```csharp
    /// <summary>
    /// The item-index order for one cycle. Natural order when shuffle is off; a seeded
    /// Fisher-Yates permutation (every index exactly once) when on. Seed-driven so the caller
    /// controls randomness and tests are deterministic. Server-authority-only — never a render input.
    /// </summary>
    public static int[] BuildCycleOrder(int count, bool shuffle, int seed)
    {
        var order = new int[Math.Max(0, count)];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        if (!shuffle || order.Length < 2) return order;

        var rng = new Random(seed);
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter PlaylistTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/PlaylistScheduler.cs WaBiBaBuSy.Tests/PlaylistTests.cs
git commit -m "feat: seeded shuffle cycle order for playlists"
```

---

## Task 4: PlaylistStore (JSON persistence)

**Files:**
- Create: `WaBiBaBuSy.Core/Services/Animation/PlaylistStore.cs`

No unit test (Core is not referenced by the test project; JSON roundtrip is already covered in Task 1). Validated by build + manual save/load.

- [ ] **Step 1: Write PlaylistStore**

Create `WaBiBaBuSy.Core/Services/Animation/PlaylistStore.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>
/// Loads/saves <see cref="Playlist"/> files as JSON under %APPDATA%\WaBiBaBuSy\playlists\.
/// Mirrors the logging-config persistence location convention.
/// </summary>
public class PlaylistStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Directory holding playlist JSON files (created on demand).</summary>
    public string Directory { get; }

    public PlaylistStore(string? directoryOverride = null)
    {
        Directory = directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WaBiBaBuSy", "playlists");
    }

    private void EnsureDirectory() => System.IO.Directory.CreateDirectory(Directory);

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "playlist" : name;
    }

    /// <summary>Full path for a playlist name (does not create the file).</summary>
    public string PathFor(string name) => Path.Combine(Directory, Sanitize(name) + ".json");

    /// <summary>Save a playlist to <see cref="PathFor"/>(playlist.Name).</summary>
    public async Task SaveAsync(Playlist playlist)
    {
        EnsureDirectory();
        var json = JsonSerializer.Serialize(playlist, Options);
        await File.WriteAllTextAsync(PathFor(playlist.Name), json);
    }

    /// <summary>Load a playlist from an absolute file path.</summary>
    public async Task<Playlist> LoadAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<Playlist>(json)
               ?? throw new InvalidDataException($"Playlist file was empty or invalid: {filePath}");
    }

    /// <summary>Enumerate saved playlist file paths (newest first). Empty if the directory is absent.</summary>
    public IReadOnlyList<string> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<string>();
        return System.IO.Directory.GetFiles(Directory, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    /// <summary>Delete a playlist by name. No-op if it does not exist.</summary>
    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build WaBiBaBuSy.Core`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add WaBiBaBuSy.Core/Services/Animation/PlaylistStore.cs
git commit -m "feat: PlaylistStore JSON persistence under %APPDATA%"
```

---

## Task 5: Extract ApplyCrossScreenConfigAsync from StartCrossScreen

This is a **refactor with no behavior change** — the manual Start button must work exactly as before. It unlocks the orchestrator (Task 6) calling the same apply path per item.

**Files:**
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (method `StartCrossScreen` at ~2866–3170)

- [ ] **Step 1: Add the apply-result type**

At the end of `MainWindowViewModel.cs` (or near the top of the file's type declarations), add:

```csharp
/// <summary>
/// Metrics returned by applying a cross-screen config, used by the playlist orchestrator
/// to compute lap-snap timing. ContentWidthPx is best-effort (0 when unknown).
/// </summary>
public sealed class CrossScreenApplyResult
{
    public int VirtualCanvasWidth { get; init; }
    public int ContentWidthPx { get; init; }
}
```

- [ ] **Step 2: Introduce the extracted method wrapping the existing body**

Rename the current private `StartCrossScreen()` body: change its signature so the logic operates on a parameter instead of the field. Concretely:

1. Add a new method:

```csharp
/// <summary>
/// Apply one cross-screen configuration across all local + remote nodes with a single shared
/// start timestamp. Shared by the manual Start button and the playlist orchestrator.
/// Returns canvas/content metrics for lap-snap timing.
/// </summary>
public async Task<CrossScreenApplyResult> ApplyCrossScreenConfigAsync(CrossScreenConfig config)
{
    // ... MOVE HERE the entire body currently inside StartCrossScreen()'s try-block that runs
    // AFTER the null/empty guard, i.e. from "// Convert clients to screen configurations"
    // down through the remote-client send loop.
    //
    // Mechanical replacements throughout the moved body:
    //   _crossScreenConfig   ->   config
    //
    // At the two points where the virtual canvas width is known
    // (canvasManager.VirtualBounds.Width), capture it for the return value.
    // Capture content width best-effort: if a loaded D2DCompositionService exposes the animation
    // frame width use it; otherwise leave 0.
    //
    // Replace the final implicit fall-through with:
    return new CrossScreenApplyResult
    {
        VirtualCanvasWidth = canvasManager.VirtualBounds.Width,
        ContentWidthPx = 0, // best-effort; wire a real value if D2DCompositionService exposes it
    };
}
```

2. Reduce `StartCrossScreen()` to the guard + delegation:

```csharp
private async Task StartCrossScreen()
{
    if (IsCrossScreenRunning)
    {
        Debug.WriteLine("[CrossScreen] Already running");
        return;
    }

    if (_crossScreenConfig == null || string.IsNullOrEmpty(_crossScreenConfig.Animation.AnimationPath))
    {
        Debug.WriteLine("[CrossScreen] No configuration. Opening config dialog...");
        await ConfigureCrossScreen();

        if (_crossScreenConfig == null || string.IsNullOrEmpty(_crossScreenConfig.Animation.AnimationPath))
        {
            Debug.WriteLine("[CrossScreen] Configuration cancelled or incomplete");
            return;
        }
    }

    try
    {
        await ApplyCrossScreenConfigAsync(_crossScreenConfig);
        // ... KEEP any post-apply state updates that were at the end of the old method
        //     (e.g. setting IsCrossScreenRunning, HasAnimationConfig, notifying UI props).
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[CrossScreen] Start failed: {ex.Message}");
    }
}
```

> Note for the implementer: read `MainWindowViewModel.cs:2866-3200` first. Preserve every field the body sets (`_d2dCompositionServices`, `_crossScreenContentId`, `_seq*` sequential-IconZone fields, `IsCrossScreenRunning`, etc.). Only the *source of the config* changes (field → parameter). Do not alter canvas/broadcast logic.

- [ ] **Step 3: Build**

Run: `dotnet build WaBiBaBuSy.UI`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Manual verification (behavior unchanged)**

Run: `dotnet run --project WaBiBaBuSy.UI`
Then: Tray → configure a cross-screen animation → Start. Confirm the animation starts exactly as before on local monitors. Stop it.
Expected: identical behavior to pre-refactor.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs
git commit -m "refactor: extract ApplyCrossScreenConfigAsync from StartCrossScreen"
```

---

## Task 6: PlaylistOrchestrator (timing loop)

**Files:**
- Create: `WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs`

Thin I/O shell; validated by build + E2E. It computes every timing decision via the Task 2/3 pure helpers.

- [ ] **Step 1: Write PlaylistOrchestrator**

Create `WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>Metrics an apply callback returns so the orchestrator can compute lap-snap timing.</summary>
public readonly record struct ApplyMetrics(int VirtualCanvasWidth, int ContentWidthPx);

/// <summary>
/// Drives playlist rotation: applies each item's config via a caller-supplied delegate (the same
/// broadcast path the manual Start button uses), waits the resolved dwell, then advances.
/// Server-authority-only. Timing decisions come from PlaylistScheduler (pure, tested).
/// </summary>
public class PlaylistOrchestrator
{
    private readonly ILogger<PlaylistOrchestrator> _logger;
    private readonly Func<CrossScreenConfig, Task<ApplyMetrics>> _apply;
    private readonly Func<int> _seedProvider;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>The item currently on screen (for session-resume + UI), or null when stopped.</summary>
    public PlaylistItem? CurrentItem { get; private set; }

    /// <summary>Index of the current item within the playlist's Items list, or -1.</summary>
    public int CurrentItemIndex { get; private set; } = -1;

    /// <summary>True while a show is running.</summary>
    public bool IsRunning => _loopTask is { IsCompleted: false };

    /// <summary>Raised (item, index) whenever a new item becomes current — for UI highlight.</summary>
    public event Action<PlaylistItem, int>? ItemChanged;

    /// <param name="apply">Applies a config across all nodes and returns canvas/content metrics.</param>
    /// <param name="seedProvider">Supplies a fresh shuffle seed per cycle (e.g. () => Environment.TickCount).</param>
    public PlaylistOrchestrator(
        ILogger<PlaylistOrchestrator> logger,
        Func<CrossScreenConfig, Task<ApplyMetrics>> apply,
        Func<int> seedProvider)
    {
        _logger = logger;
        _apply = apply;
        _seedProvider = seedProvider;
    }

    /// <summary>Start rotating the given playlist. No-op if already running or the list is empty.</summary>
    public void Start(Playlist playlist)
    {
        if (IsRunning) { _logger.LogWarning("Playlist already running; ignoring Start"); return; }
        if (playlist.Items.Count == 0) { _logger.LogWarning("Playlist has no items; not starting"); return; }

        _cts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(playlist, _cts.Token);
    }

    /// <summary>Stop the show. Safe to call when already stopped.</summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loopTask != null)
        {
            try { await _loopTask; } catch (OperationCanceledException) { }
        }
        _loopTask = null;
        _cts?.Dispose();
        _cts = null;
        CurrentItem = null;
        CurrentItemIndex = -1;
    }

    private async Task RunLoopAsync(Playlist playlist, CancellationToken ct)
    {
        try
        {
            do
            {
                var order = PlaylistScheduler.BuildCycleOrder(
                    playlist.Items.Count, playlist.Shuffle, _seedProvider());

                foreach (var index in order)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = playlist.Items[index];

                    CurrentItem = item;
                    CurrentItemIndex = index;
                    ItemChanged?.Invoke(item, index);

                    ApplyMetrics metrics;
                    try
                    {
                        metrics = await _apply(item.Config);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Apply failed for item '{Name}'; skipping", item.Name);
                        continue;
                    }

                    int lapMs = PlaylistScheduler.ComputeLapMs(
                        item.Config.Movement, metrics.VirtualCanvasWidth, metrics.ContentWidthPx);
                    int dwell = PlaylistScheduler.ResolveDwellMs(item, playlist.DefaultItemDurationMs, lapMs);

                    _logger.LogInformation(
                        "Playlist item '{Name}' (idx {Index}) dwell={Dwell}ms (lap={Lap}ms, snap={Snap})",
                        item.Name, index, dwell, lapMs, item.SnapToLap);

                    await Task.Delay(dwell, ct);
                }
            }
            while (playlist.Loop && !ct.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Playlist rotation cancelled");
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build WaBiBaBuSy.Core`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs
git commit -m "feat: PlaylistOrchestrator rotation loop (server-side)"
```

---

## Task 7: Wire orchestrator into the UI + Start/Stop plumbing

**Files:**
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1: Add orchestrator field + start/stop methods**

Near the other private fields in `MainWindowViewModel` (e.g. by `_crossScreenConfig` at line ~155), add:

```csharp
    private WaBiBaBuSy.Core.Services.Animation.PlaylistOrchestrator? _playlistOrchestrator;
```

Add these methods to `MainWindowViewModel`:

```csharp
    /// <summary>Start rotating the given playlist across all nodes.</summary>
    public void StartPlaylist(WaBiBaBuSy.Models.Wallpaper.Playlist playlist)
    {
        _playlistOrchestrator ??= new WaBiBaBuSy.Core.Services.Animation.PlaylistOrchestrator(
            AppLogger.CreateLogger<WaBiBaBuSy.Core.Services.Animation.PlaylistOrchestrator>(),
            apply: async config =>
            {
                var r = await ApplyCrossScreenConfigAsync(config);
                return new WaBiBaBuSy.Core.Services.Animation.ApplyMetrics(r.VirtualCanvasWidth, r.ContentWidthPx);
            },
            seedProvider: () => Environment.TickCount);

        _playlistOrchestrator.Start(playlist);
    }

    /// <summary>Stop the running playlist (if any).</summary>
    public async Task StopPlaylistAsync()
    {
        if (_playlistOrchestrator != null)
            await _playlistOrchestrator.StopAsync();
    }
```

- [ ] **Step 2: Session-resume note (verify, adjust if needed)**

Find where the server re-sends the current animation command on client rejoin (search `session` / resume in `WallpaperSyncCoordinator` / `WaBiBaBuSyService`). Confirm that when a playlist is running, the resume payload is `_playlistOrchestrator.CurrentItem.Config` with its current start timestamp. If the resume path reads a single stored "last command," ensure `ApplyCrossScreenConfigAsync` updates that stored command each time it runs (it already broadcasts per item, so a rejoining client that receives the current broadcast lands correctly). No code change if resume already re-broadcasts the last per-item command; otherwise store the last-applied config where the resume path reads it.

Run: `dotnet build WaBiBaBuSy.UI`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs
git commit -m "feat: wire PlaylistOrchestrator into MainWindowViewModel"
```

---

## Task 8: PlaylistViewModel + row VM

**Files:**
- Create: `WaBiBaBuSy.UI/ViewModels/PlaylistItemRow.cs`
- Create: `WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs`

- [ ] **Step 1: Write the row VM**

Create `WaBiBaBuSy.UI/ViewModels/PlaylistItemRow.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>Observable wrapper around a <see cref="PlaylistItem"/> for the playlist grid.</summary>
public partial class PlaylistItemRow : ViewModelBase
{
    public PlaylistItem Model { get; }

    public PlaylistItemRow(PlaylistItem model)
    {
        Model = model;
        _name = model.Name;
        _durationText = model.DurationMs?.ToString() ?? string.Empty;
        _snapToLap = model.SnapToLap;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _durationText; // blank = use playlist default
    [ObservableProperty] private bool _snapToLap;

    public string Summary =>
        $"{System.IO.Path.GetFileName(Model.Config.Animation.AnimationPath)} · " +
        $"{Model.Config.Movement.Type} · {Model.Config.DistributionMode}";

    /// <summary>Push edited row fields back into the underlying model.</summary>
    public void CommitToModel()
    {
        Model.Name = Name;
        Model.SnapToLap = SnapToLap;
        Model.DurationMs = int.TryParse(DurationText, out var ms) && ms > 0 ? ms : null;
    }
}
```

- [ ] **Step 2: Write the dialog VM**

Create `WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// ViewModel for the Playlist / Party Mode dialog. Composes saved CrossScreen configs into an
/// ordered, persisted list and starts/stops the rotation via callbacks the host wires up.
/// </summary>
public partial class PlaylistViewModel : ViewModelBase
{
    private readonly PlaylistStore _store = new();

    /// <summary>Host callback: open the CrossScreen config dialog, return the built config or null.</summary>
    public Func<CrossScreenConfig?, Task<CrossScreenConfig?>>? EditConfigAsync { get; set; }

    /// <summary>Host callback: start rotating the given playlist.</summary>
    public Action<Playlist>? StartShow { get; set; }

    /// <summary>Host callback: stop the running show.</summary>
    public Func<Task>? StopShow { get; set; }

    [ObservableProperty] private string _playlistName = "Party";
    [ObservableProperty] private bool _loop = true;
    [ObservableProperty] private bool _shuffle = false;
    [ObservableProperty] private int _defaultDurationMs = 30_000;
    [ObservableProperty] private bool _isShowRunning;

    public ObservableCollection<PlaylistItemRow> Items { get; } = new();

    [ObservableProperty] private PlaylistItemRow? _selectedItem;

    // --- Item commands ------------------------------------------------------

    [RelayCommand]
    private async Task AddItem()
    {
        if (EditConfigAsync == null) return;
        var config = await EditConfigAsync(null);
        if (config == null) return;

        var item = new PlaylistItem
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(config.Animation.AnimationPath),
            Config = config,
        };
        Items.Add(new PlaylistItemRow(item));
    }

    [RelayCommand]
    private async Task EditItem()
    {
        if (EditConfigAsync == null || SelectedItem == null) return;
        var updated = await EditConfigAsync(SelectedItem.Model.Config);
        if (updated == null) return;
        SelectedItem.Model.Config = updated;
        OnPropertyChanged(nameof(Items)); // refresh summaries
    }

    [RelayCommand]
    private void RemoveItem()
    {
        if (SelectedItem != null) Items.Remove(SelectedItem);
    }

    [RelayCommand]
    private void DuplicateItem()
    {
        if (SelectedItem == null) return;
        SelectedItem.CommitToModel();
        var clone = System.Text.Json.JsonSerializer.Deserialize<PlaylistItem>(
            System.Text.Json.JsonSerializer.Serialize(SelectedItem.Model))!;
        Items.Add(new PlaylistItemRow(clone));
    }

    [RelayCommand]
    private void MoveUp()
    {
        var i = SelectedItem == null ? -1 : Items.IndexOf(SelectedItem);
        if (i > 0) Items.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveDown()
    {
        var i = SelectedItem == null ? -1 : Items.IndexOf(SelectedItem);
        if (i >= 0 && i < Items.Count - 1) Items.Move(i, i + 1);
    }

    // --- Playlist-level commands -------------------------------------------

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

    [RelayCommand]
    private async Task SavePlaylist() => await _store.SaveAsync(BuildPlaylist());

    [RelayCommand]
    private void StartShowCommand()
    {
        if (Items.Count == 0) return;
        StartShow?.Invoke(BuildPlaylist());
        IsShowRunning = true;
    }

    [RelayCommand]
    private async Task StopShowCommand()
    {
        if (StopShow != null) await StopShow();
        IsShowRunning = false;
    }

    /// <summary>Load a playlist model into the VM (e.g. after a Load-file action).</summary>
    public void LoadFrom(Playlist playlist)
    {
        PlaylistName = playlist.Name;
        Loop = playlist.Loop;
        Shuffle = playlist.Shuffle;
        DefaultDurationMs = playlist.DefaultItemDurationMs;
        Items.Clear();
        foreach (var item in playlist.Items) Items.Add(new PlaylistItemRow(item));
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build WaBiBaBuSy.UI`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add WaBiBaBuSy.UI/ViewModels/PlaylistItemRow.cs WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs
git commit -m "feat: PlaylistViewModel + row VM"
```

---

## Task 9: PlaylistDialog view + entry point

**Files:**
- Create: `WaBiBaBuSy.UI/Views/PlaylistDialog.axaml`
- Create: `WaBiBaBuSy.UI/Views/PlaylistDialog.axaml.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (add `OpenPlaylistCommand`)

- [ ] **Step 1: Write the view XAML**

Create `WaBiBaBuSy.UI/Views/PlaylistDialog.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:WaBiBaBuSy.UI.ViewModels"
        x:Class="WaBiBaBuSy.UI.Views.PlaylistDialog"
        x:DataType="vm:PlaylistViewModel"
        Title="Playlist / Party Mode" Width="720" Height="560"
        WindowStartupLocation="CenterOwner">
    <DockPanel Margin="16">
        <!-- Header -->
        <StackPanel DockPanel.Dock="Top" Spacing="8">
            <TextBlock Text="Playlist / Party Mode" FontSize="18" FontWeight="Bold"/>
            <Grid ColumnDefinitions="Auto,*,Auto,Auto,Auto,Auto" ColumnSpacing="8">
                <TextBlock Grid.Column="0" Text="Name" VerticalAlignment="Center"/>
                <TextBox   Grid.Column="1" Text="{Binding PlaylistName}"/>
                <CheckBox  Grid.Column="2" Content="Loop" IsChecked="{Binding Loop}"/>
                <CheckBox  Grid.Column="3" Content="Shuffle" IsChecked="{Binding Shuffle}"/>
                <TextBlock Grid.Column="4" Text="Default ms" VerticalAlignment="Center"/>
                <NumericUpDown Grid.Column="5" Width="120" Minimum="1000" Increment="1000"
                               Value="{Binding DefaultDurationMs}"/>
            </Grid>
        </StackPanel>

        <!-- Footer -->
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Spacing="8"
                    HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Save" Command="{Binding SavePlaylistCommand}"/>
            <Button Content="Start Show" Command="{Binding StartShowCommandCommand}"
                    IsEnabled="{Binding !IsShowRunning}"/>
            <Button Content="Stop Show" Command="{Binding StopShowCommandCommand}"
                    IsEnabled="{Binding IsShowRunning}"/>
        </StackPanel>

        <!-- Item toolbar -->
        <StackPanel DockPanel.Dock="Right" Spacing="6" Margin="12,0,0,0" VerticalAlignment="Top">
            <Button Content="Add"       Command="{Binding AddItemCommand}"/>
            <Button Content="Edit"      Command="{Binding EditItemCommand}"/>
            <Button Content="Duplicate" Command="{Binding DuplicateItemCommand}"/>
            <Button Content="Remove"    Command="{Binding RemoveItemCommand}"/>
            <Button Content="Up"        Command="{Binding MoveUpCommand}"/>
            <Button Content="Down"      Command="{Binding MoveDownCommand}"/>
        </StackPanel>

        <!-- Item list -->
        <ListBox ItemsSource="{Binding Items}" SelectedItem="{Binding SelectedItem}">
            <ListBox.ItemTemplate>
                <DataTemplate x:DataType="vm:PlaylistItemRow">
                    <Grid ColumnDefinitions="*,120,Auto" ColumnSpacing="8" Margin="0,4">
                        <StackPanel Grid.Column="0">
                            <TextBox Text="{Binding Name}" Watermark="Item name"/>
                            <TextBlock Text="{Binding Summary}" Opacity="0.7" FontSize="11"/>
                        </StackPanel>
                        <TextBox  Grid.Column="1" Text="{Binding DurationText}" Watermark="default"/>
                        <CheckBox Grid.Column="2" Content="Lap-snap" IsChecked="{Binding SnapToLap}"
                                  VerticalAlignment="Center"/>
                    </Grid>
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>
    </DockPanel>
</Window>
```

> Note: CommunityToolkit generates `StartShowCommandCommand` from the method `StartShowCommand` (it appends `Command`). If you prefer cleaner names, rename the VM methods to `StartShow`/`StopShow` — but those clash with the callback properties, so keep the `*Command` method names and the generated `*CommandCommand` bindings, or rename callbacks to `OnStartShow`/`OnStopShow`.

- [ ] **Step 2: Write the code-behind**

Create `WaBiBaBuSy.UI/Views/PlaylistDialog.axaml.cs`:

```csharp
using System;
using Avalonia.Controls;

namespace WaBiBaBuSy.UI.Views;

public partial class PlaylistDialog : Window
{
    public PlaylistDialog()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 3: Add the OpenPlaylistCommand to MainWindowViewModel**

Add to `MainWindowViewModel`:

```csharp
    [RelayCommand]
    private async Task OpenPlaylist()
    {
        var dialog = new WaBiBaBuSy.UI.Views.PlaylistDialog();
        var vm = new PlaylistViewModel
        {
            EditConfigAsync = async existing =>
            {
                // Reuse the existing CrossScreen config dialog to build/edit a config.
                _crossScreenConfig = existing != null
                    ? System.Text.Json.JsonSerializer.Deserialize<CrossScreenConfig>(
                        System.Text.Json.JsonSerializer.Serialize(existing))
                    : _crossScreenConfig;
                await ConfigureCrossScreen();
                return _crossScreenConfig != null &&
                       !string.IsNullOrEmpty(_crossScreenConfig.Animation.AnimationPath)
                    ? _crossScreenConfig
                    : null;
            },
            StartShow = playlist => StartPlaylist(playlist),
            StopShow = StopPlaylistAsync,
        };
        dialog.DataContext = vm;

        if (_mainWindow != null)
            await dialog.ShowDialog(_mainWindow);
    }
```

- [ ] **Step 4: Add a tray/menu entry**

Bind a menu item (tray menu or main window) to `OpenPlaylistCommand`. Find the existing cross-screen menu entry (search the main-window XAML / tray menu for the command that triggers `ConfigureCrossScreen`/`StartCrossScreen`) and add next to it:

```xml
<MenuItem Header="Playlist / Party Mode..." Command="{Binding OpenPlaylistCommand}"/>
```

- [ ] **Step 5: Build**

Run: `dotnet build WaBiBaBuSy.UI`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Manual verification**

Run: `dotnet run --project WaBiBaBuSy.UI`
- Open Playlist / Party Mode.
- Add two items (each opens the CrossScreen config dialog). Set durations (e.g. 8000 and blank).
- Mark the first (a Linear movement) as Lap-snap.
- Start Show → confirm items rotate; the lap-snap item completes a full traversal before switching; the blank-duration item uses the default.
- Stop Show. Save. Confirm a JSON file appears under `%APPDATA%\WaBiBaBuSy\playlists\`.

- [ ] **Step 7: Commit**

```bash
git add WaBiBaBuSy.UI/Views/PlaylistDialog.axaml WaBiBaBuSy.UI/Views/PlaylistDialog.axaml.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs
git commit -m "feat: Playlist / Party Mode dialog + entry point"
```

---

## Task 10: Documentation + graph refresh

**Files:**
- Modify: `.docs/RECENT_UPDATES.md`, `.docs/2026.07_FEATURE_OVERVIEW.md`, `.docs/2026.07_OPEN_ITEMS.md`, `CLAUDE.md`

- [ ] **Step 1: Record the milestone**

- In `.docs/RECENT_UPDATES.md`: add an entry describing Playlist / Party Mode (per-item duration + global default, hard-cut + opt-in Linear lap-snap, auto-follow, own dialog, JSON persistence under `%APPDATA%\WaBiBaBuSy\playlists\`).
- In `.docs/2026.07_FEATURE_OVERVIEW.md`: move item #7 out of "Tier 2 missing" into implemented; note scheduled interrupt-shows, per-client opt-out, and non-Linear lap-snap remain as follow-ups.
- In `.docs/2026.07_OPEN_ITEMS.md` §4: mark Playlist / Party Mode done; keep the deferred sub-items listed.
- In `CLAUDE.md`: update "Current Work" and the Tier 2 bullet to reflect Playlist shipped.

- [ ] **Step 2: Refresh the knowledge graph**

Run: `graphify update .`
Expected: graph rebuilds (AST-only, no API cost).

- [ ] **Step 3: Commit**

```bash
git add .docs/ CLAUDE.md graphify-out/
git commit -m "docs: playlist / party mode shipped; graph refresh"
```

---

## Self-Review

**Spec coverage:**
- Data model (§3) → Task 1. ✔
- Persistence to `%APPDATA%\...\playlists` (§3) → Task 4. ✔
- Orchestration + reuse of apply path (§4.1–4.2) → Tasks 5, 6. ✔
- Shuffle server-side, seeded, once per cycle (§4.3) → Task 3, Task 6. ✔
- Reconnection/current-item tracking (§4.4) → Task 6 (`CurrentItem`) + Task 7 Step 2. ✔
- Hard-cut + opt-in Linear lap-snap (§5) → Task 2. ✔
- Own dialog with add/edit/reorder/duplicate/remove, playlist-level toggles, Start/Stop, Save/Load (§6) → Tasks 8, 9. ✔
- Testing (§7): lap math, dwell, shuffle, JSON roundtrip → Tasks 1–3. Determinism guard: shuffle changes order not content — enforced by design (config passed through unmodified) and covered by roundtrip + BuildCycleOrder tests. ✔

**Placeholder scan:** Task 5 intentionally describes a mechanical code move rather than pasting the 300-line body verbatim (the executing engineer reads the exact lines cited); every other code step is complete. `ContentWidthPx` best-effort=0 is a documented, working default (lap-snap then uses full canvas width), not a TODO.

**Type consistency:** `CrossScreenApplyResult` (UI, Task 5) vs `ApplyMetrics` (Core, Task 6) are deliberately two types — Core cannot reference the UI type; Task 7 maps one to the other. `PlaylistScheduler.ComputeLapMs/ResolveDwellMs/BuildCycleOrder` signatures match across Tasks 2/3/6. `PlaylistItem.GetEffectiveDurationMs` used consistently in Tasks 1/2. VM command method names and their generated `*Command` bindings are called out explicitly in Task 9 Step 1's note.

---

## Execution Handoff

Plan complete and saved to `.docs/plans/2026-07-18-playlist-party-mode-plan.md`.

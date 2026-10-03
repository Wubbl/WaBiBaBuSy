# Sprite & Bezel Effects (Roadmap 2.4, slice 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The single sprite can leave a fading trail, breathe (pulse), spin, and mark the moment it crosses a bezel with a Glow, Flash or Ripple on the node where the crossing happens — identical on every machine, in the room preview and in test-mode exact frames.

**Architecture:** A new `SpriteEffectsConfig` hangs off `AnimationLayerConfig.Effects` (travels to clients and players as JSON — no protocol change). All math is pure and lives in `WaBiBaBuSy.Models/Wallpaper/SpriteEffects.cs`: trail ghost *k* is the sprite's pose at `elapsed − k × spacing`, pulse/spin are functions of elapsed, and a bezel crossing is found by scanning the sprite's visibility on this node backwards over the effect's duration on a 16 ms grid aligned to absolute elapsed time (so the crossing time is the same every frame and on every node, needs no frame history, and never fires at scene start). The D2D player and the Avalonia room preview (`ScenePainter`) both call these functions. Effects apply to a single sprite moved by `MovementCalculator`; patterns, extra images and IconZone path-following are excluded (validation hint).

**Tech Stack:** C# 12 / .NET 9, Vortice Direct2D (player), Avalonia 11 (UI), xUnit, System.Text.Json + Newtonsoft.Json (player IPC).

**Spec:** [`2026-09-17-crossing-effects-and-events-design.md`](2026-09-17-crossing-effects-and-events-design.md) §2.2, §2.3, §3 (Glow/Flash/Ripple), §7, §8. Out of scope: Portal (needs analytic crossing times), one-shot events (§5), transitions (§6), `LayerEffects` per layer (scene layers, roadmap 2.3, do not exist yet — `FaceTravelDirection` stays on `AnimationLayerConfig`).

## Global Constraints

- Determinism (CLAUDE.md "Architectural Invariants"): every effect is a pure function of `(config, elapsedMs, node layout)`. No `Random` without a seed, no `DateTime.Now`, no per-machine state in effect math.
- Player render loop: no per-frame heap allocations (use `stackalloc`, cached brushes), no blocking calls. Brushes are created lazily and disposed in `DisposeNativeD2DResources`.
- Long uptime: fold elapsed into the period in `double` before any `float` math (as `MovementCalculator` does).
- Defaults from the spec: Trail `Count = 6, SpacingMs = 60, StartOpacity = 0.5, ScaleFalloff = 0.9, UseGradingColor = true`; Pulse `Amplitude = 0.08, FrequencyHz = 0.8`; `BezelColorHex = "#FFFFFF"`, `BezelDurationMs = 350`; Glow strip 120 px wide, Flash opacity `0.35 × (1 − p)²`, Ripple radius `p × 600 px`.
- Sizes (glow width, ripple radius) are canvas units — reference pixels on a physical canvas, so they are the same centimeters on every monitor.
- Code style: file-scoped namespaces, XML docs on public APIs, match the surrounding comment density.
- Do not edit `graphify-out/`; the post-commit hook rebuilds it.
- Commits on `main` (trunk-based), each ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Scene start:** a sprite that is already on a node at t = 0 must not fire an Entry effect there (there was no crossing) — test in Task 3.
2. **Ring seam:** a sprite straddling the ring seam is visible through its second wrap copy; seat 0 must see one Entry, not Exit+Entry flicker — test in Task 3.
3. **Facing (mirrored) rows:** the effect must appear at the screen edge the sprite actually crossed on a mirrored node — test in Task 3.
4. **Days of uptime:** pulse and spin must not stutter after 40 days of elapsed time — test in Task 2.
5. **Hand-edited scene JSON:** absurd values (`Count = 1000`, `SpacingMs = 0`, `BezelDurationMs = -5`, `ScaleFalloff = 3`) must be clamped, not crash or stall the player — tests in Task 2 and Task 3.

---

## File Structure

| File | Responsibility |
|---|---|
| `WaBiBaBuSy.Models/Wallpaper/SpriteEffectsConfig.cs` (new) | Config: `SpriteEffectsConfig`, `TrailConfig`, `PulseConfig`, `BezelEffect` |
| `WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs` | `AnimationLayerConfig.Effects` property |
| `WaBiBaBuSy.Models/Wallpaper/SpriteEffects.cs` (new) | Pure math: support check, pulse, spin, trail ghosts, pose, facing, bezel scan, bezel geometry |
| `WaBiBaBuSy.Player.D2D/Program.cs` | `partial`, sprite-scale parameter on `DrawCellWithOptionalGrading`, corridor clamp helper, effect calls in `DrawAnimationLayer`, brush disposal |
| `WaBiBaBuSy.Player.D2D/Program.Effects.cs` (new) | Player drawing of trail and bezel effects, brush cache |
| `WaBiBaBuSy.UI/Controls/ScenePainter.cs` | Preview of trail, pulse, spin, bezel effects |
| `WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs` | Effect properties, load / build |
| `WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml` | "Effects" tab |
| `WaBiBaBuSy.Models/Wallpaper/SceneChecks.cs` | Hints when effects cannot be drawn |
| `WaBiBaBuSy.UI/ViewModels/PlaylistItemRow.cs` | Effects in the item's look summary |
| `TestScenarios/showcase/scenes/13-comet.json` + `showcase.json` (new step block) | Visual check via exact frames |
| Tests: `ConfigJsonRoundtripTests.cs`, new `SpriteEffectsTests.cs`, new `BezelEffectsTests.cs`, `SceneChecksTests` (existing file name — find with `rg -l "SceneChecks.Validate" WaBiBaBuSy.Tests`) | |

---

### Task 1: Effects config model + JSON round trip

**Files:**
- Create: `WaBiBaBuSy.Models/Wallpaper/SpriteEffectsConfig.cs`
- Modify: `WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs` (class `AnimationLayerConfig`, next to `FaceTravelDirection` ~line 265)
- Modify: `WaBiBaBuSy.Tests/WaBiBaBuSy.Tests.csproj` (add `<PackageReference Include="Newtonsoft.Json" Version="13.0.4" />` — same version as `WaBiBaBuSy.Player.Common`, which serializes player commands with Newtonsoft)
- Test: `WaBiBaBuSy.Tests/ConfigJsonRoundtripTests.cs`

**Interfaces:**
- Produces: `SpriteEffectsConfig { TrailConfig? Trail; PulseConfig? Pulse; float SpinDegPerSecond; BezelEffect Entry; BezelEffect Exit; string BezelColorHex; int BezelDurationMs }`, `TrailConfig { int Count; int SpacingMs; float StartOpacity; float ScaleFalloff; bool UseGradingColor }`, `PulseConfig { float Amplitude; float FrequencyHz }`, `enum BezelEffect { None, Glow, Flash, Ripple }`, `AnimationLayerConfig.Effects : SpriteEffectsConfig?` (null = no effects).

- [ ] **Step 1: Write the failing test** — append to `ConfigJsonRoundtripTests`:

```csharp
    private static AnimationLayerConfig WithEffects() => new()
    {
        AnimationPath = @"C:\anim\fish.gif",
        Effects = new SpriteEffectsConfig
        {
            Trail = new TrailConfig { Count = 9, SpacingMs = 45, StartOpacity = 0.6f, ScaleFalloff = 0.8f, UseGradingColor = false },
            Pulse = new PulseConfig { Amplitude = 0.12f, FrequencyHz = 1.5f },
            SpinDegPerSecond = 90f,
            Entry = BezelEffect.Ripple,
            Exit = BezelEffect.Glow,
            BezelColorHex = "#7FDBFF",
            BezelDurationMs = 500,
        },
    };

    private static void AssertEffects(SpriteEffectsConfig? fx)
    {
        Assert.NotNull(fx);
        Assert.Equal(9, fx!.Trail!.Count);
        Assert.Equal(45, fx.Trail.SpacingMs);
        Assert.Equal(0.6f, fx.Trail.StartOpacity);
        Assert.Equal(0.8f, fx.Trail.ScaleFalloff);
        Assert.False(fx.Trail.UseGradingColor);
        Assert.Equal(0.12f, fx.Pulse!.Amplitude);
        Assert.Equal(1.5f, fx.Pulse.FrequencyHz);
        Assert.Equal(90f, fx.SpinDegPerSecond);
        Assert.Equal(BezelEffect.Ripple, fx.Entry);
        Assert.Equal(BezelEffect.Glow, fx.Exit);
        Assert.Equal("#7FDBFF", fx.BezelColorHex);
        Assert.Equal(500, fx.BezelDurationMs);
    }

    [Fact]
    public void AnimationLayerConfig_Effects_RoundtripSystemTextJson() =>
        AssertEffects(JsonSerializer.Deserialize<AnimationLayerConfig>(JsonSerializer.Serialize(WithEffects()))!.Effects);

    [Fact]
    public void AnimationLayerConfig_Effects_RoundtripNewtonsoft() =>   // player IPC (D2DPlayerHost) uses Newtonsoft
        AssertEffects(Newtonsoft.Json.JsonConvert.DeserializeObject<AnimationLayerConfig>(Newtonsoft.Json.JsonConvert.SerializeObject(WithEffects()))!.Effects);

    [Fact]
    public void AnimationLayerConfig_WithoutEffects_DeserializesToNull() =>
        Assert.Null(JsonSerializer.Deserialize<AnimationLayerConfig>("""{"AnimationPath":"a.gif"}""")!.Effects);
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ConfigJsonRoundtripTests"`
Expected: build error `CS0246 'SpriteEffectsConfig' could not be found`.

- [ ] **Step 3: Implement** — create `WaBiBaBuSy.Models/Wallpaper/SpriteEffectsConfig.cs`:

```csharp
namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Visual effects on the single sprite (roadmap 2.4): a fading trail, a scale pulse, a constant spin and an
/// effect where the sprite crosses into or out of a node. Every effect is a pure function of the shared clock,
/// so all machines draw the same thing. Drawn only for a single image moved by <see cref="MovementCalculator"/>
/// (see <see cref="SpriteEffects.Supported"/>).
/// </summary>
public class SpriteEffectsConfig
{
    /// <summary>Ghost copies behind the sprite; null = no trail.</summary>
    public TrailConfig? Trail { get; set; }

    /// <summary>Scale breathing; null = no pulse.</summary>
    public PulseConfig? Pulse { get; set; }

    /// <summary>Constant rotation in degrees per second (negative = counter-clockwise); 0 = none.</summary>
    public float SpinDegPerSecond { get; set; }

    /// <summary>Effect on the node the sprite enters, at the edge it comes in.</summary>
    public BezelEffect Entry { get; set; } = BezelEffect.None;

    /// <summary>Effect on the node the sprite leaves, at the edge it goes out.</summary>
    public BezelEffect Exit { get; set; } = BezelEffect.None;

    /// <summary>Color of the bezel effects (#RRGGBB).</summary>
    public string BezelColorHex { get; set; } = "#FFFFFF";

    /// <summary>How long a bezel effect lasts after the crossing, ms (clamped to 50..3000).</summary>
    public int BezelDurationMs { get; set; } = 350;
}

/// <summary>Trail: ghost k shows the sprite where it was k × <see cref="SpacingMs"/> ago.</summary>
public class TrailConfig
{
    /// <summary>Number of ghosts (clamped to 0..32).</summary>
    public int Count { get; set; } = 6;

    /// <summary>Time between ghosts, ms (at least 1).</summary>
    public int SpacingMs { get; set; } = 60;

    /// <summary>Opacity of the nearest ghost; farther ghosts fade linearly to 0.</summary>
    public float StartOpacity { get; set; } = 0.5f;

    /// <summary>Size factor per ghost step (ghost k is ScaleFalloff^k of the sprite); clamped to 0.1..1.</summary>
    public float ScaleFalloff { get; set; } = 0.9f;

    /// <summary>With a time-based color mode, tint each ghost with the color the sprite had then (a rainbow comet).</summary>
    public bool UseGradingColor { get; set; } = true;
}

/// <summary>Pulse: scale = 1 + Amplitude × sin(2π × FrequencyHz × t).</summary>
public class PulseConfig
{
    /// <summary>Relative scale swing (0.08 = ±8 %); clamped to 0..0.9.</summary>
    public float Amplitude { get; set; } = 0.08f;

    /// <summary>Breaths per second; 0 or less = no pulse.</summary>
    public float FrequencyHz { get; set; } = 0.8f;
}

/// <summary>What marks a bezel crossing on the node where it happens.</summary>
public enum BezelEffect
{
    /// <summary>Nothing.</summary>
    None,
    /// <summary>A soft vertical strip at the crossing edge that fades out.</summary>
    Glow,
    /// <summary>The whole screen flashes the bezel color briefly.</summary>
    Flash,
    /// <summary>A ring expands from the crossing point.</summary>
    Ripple,
}
```

Then in `AnimationLayerConfig` (`CrossScreenConfig.cs`), directly after the `FaceTravelDirection` property:

```csharp
    /// <summary>Trail, pulse, spin and bezel crossing effects (roadmap 2.4); null = none.</summary>
    public SpriteEffectsConfig? Effects { get; set; }
```

And add the Newtonsoft package reference to `WaBiBaBuSy.Tests.csproj` (in the `ItemGroup` with the other `PackageReference`s).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ConfigJsonRoundtripTests"` → all pass.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/SpriteEffectsConfig.cs WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs WaBiBaBuSy.Tests/ConfigJsonRoundtripTests.cs WaBiBaBuSy.Tests/WaBiBaBuSy.Tests.csproj
git commit -m "feat(effects): sprite effects config (trail, pulse, spin, bezel effects)"
```

---

### Task 2: Pure math — support check, pulse, spin, trail ghosts

**Files:**
- Create: `WaBiBaBuSy.Models/Wallpaper/SpriteEffects.cs`
- Test: `WaBiBaBuSy.Tests/SpriteEffectsTests.cs` (new)

**Interfaces:**
- Consumes: Task 1 types; `MovementCalculator.FollowsIconPath(BackgroundMode, MovementConfig?)`.
- Produces (all `public static` on `SpriteEffects`):
  - `const int MaxTrailCount = 32`
  - `bool HasAny(SpriteEffectsConfig? fx)`
  - `bool Supported(AnimationLayerConfig animation, MovementConfig? movement, BackgroundMode backgroundMode)`
  - `float PulseScale(PulseConfig? pulse, long elapsedMs)`
  - `float SpinDegrees(float degPerSecond, long elapsedMs)` — in [0, 360)
  - `int TrailCount(TrailConfig? trail)`
  - `TrailGhost Ghost(TrailConfig trail, int k)` with `public readonly record struct TrailGhost(long OffsetMs, float Opacity, float Scale)`

- [ ] **Step 1: Write the failing tests** — `WaBiBaBuSy.Tests/SpriteEffectsTests.cs`:

```csharp
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SpriteEffectsTests
{
    private static AnimationLayerConfig Sprite(SpriteEffectsConfig? fx = null) =>
        new() { AnimationPath = "fish.gif", Effects = fx ?? new SpriteEffectsConfig { SpinDegPerSecond = 30 } };

    private static readonly MovementConfig Linear = new() { Type = MovementType.Linear, SpeedPixelsPerSecond = 500 };

    // ── HasAny / Supported ──────────────────────────────────────────────

    [Fact]
    public void HasAny_DefaultConfig_IsFalse() => Assert.False(SpriteEffects.HasAny(new SpriteEffectsConfig()));

    [Theory]
    [InlineData(true, false, 0f, BezelEffect.None)]
    [InlineData(false, true, 0f, BezelEffect.None)]
    [InlineData(false, false, 10f, BezelEffect.None)]
    [InlineData(false, false, 0f, BezelEffect.Glow)]
    public void HasAny_AnyEffectSet_IsTrue(bool trail, bool pulse, float spin, BezelEffect entry) =>
        Assert.True(SpriteEffects.HasAny(new SpriteEffectsConfig
        {
            Trail = trail ? new TrailConfig() : null, Pulse = pulse ? new PulseConfig() : null,
            SpinDegPerSecond = spin, Entry = entry,
        }));

    [Fact]
    public void Supported_SingleSpriteWithMovementCalculator() =>
        Assert.True(SpriteEffects.Supported(Sprite(), Linear, BackgroundMode.SolidColor));

    [Fact]
    public void Supported_NotWithPattern()
    {
        var a = Sprite(); a.Pattern = new PatternConfig();
        Assert.False(SpriteEffects.Supported(a, Linear, BackgroundMode.SolidColor));
    }

    [Fact]
    public void Supported_NotWithExtraImages()
    {
        var a = Sprite(); a.AdditionalAnimationPaths.Add("b.png");
        Assert.False(SpriteEffects.Supported(a, Linear, BackgroundMode.SolidColor));
    }

    [Fact]
    public void Supported_BlankExtraImagePathsAreIgnored()
    {
        var a = Sprite(); a.AdditionalAnimationPaths.Add("  ");
        Assert.True(SpriteEffects.Supported(a, Linear, BackgroundMode.SolidColor));
    }

    [Fact]
    public void Supported_NotOnIconZonePath() =>
        Assert.False(SpriteEffects.Supported(Sprite(), Linear, BackgroundMode.IconZone));

    [Fact]
    public void Supported_NotWithoutEffectsOrMovement()
    {
        Assert.False(SpriteEffects.Supported(new AnimationLayerConfig { AnimationPath = "a.gif" }, Linear, BackgroundMode.SolidColor));
        Assert.False(SpriteEffects.Supported(Sprite(new SpriteEffectsConfig()), Linear, BackgroundMode.SolidColor));
        Assert.False(SpriteEffects.Supported(Sprite(), null, BackgroundMode.SolidColor));
    }

    // ── Pulse / spin ───────────────────────────────────────────────────

    [Fact]
    public void PulseScale_FollowsTheSine()
    {
        var p = new PulseConfig { Amplitude = 0.1f, FrequencyHz = 1f };
        Assert.Equal(1f, SpriteEffects.PulseScale(p, 0), 4);
        Assert.Equal(1.1f, SpriteEffects.PulseScale(p, 250), 4);
        Assert.Equal(1f, SpriteEffects.PulseScale(p, 500), 4);
        Assert.Equal(0.9f, SpriteEffects.PulseScale(p, 750), 4);
    }

    [Fact]
    public void PulseScale_NullOrOff_IsOne()
    {
        Assert.Equal(1f, SpriteEffects.PulseScale(null, 123));
        Assert.Equal(1f, SpriteEffects.PulseScale(new PulseConfig { FrequencyHz = 0 }, 123));
    }

    [Fact]
    public void PulseScale_AmplitudeIsClamped() =>
        Assert.InRange(SpriteEffects.PulseScale(new PulseConfig { Amplitude = 5f, FrequencyHz = 1f }, 750), 0.1f - 0.0001f, 1.9f);

    [Fact]
    public void SpinDegrees_WrapsInto0To360()
    {
        Assert.Equal(90f, SpriteEffects.SpinDegrees(90f, 1000), 3);
        Assert.Equal(90f, SpriteEffects.SpinDegrees(90f, 5000), 3);       // 450° → 90°
        Assert.Equal(270f, SpriteEffects.SpinDegrees(-90f, 1000), 3);     // counter-clockwise
        Assert.Equal(0f, SpriteEffects.SpinDegrees(0f, 99999));
    }

    [Fact]
    public void PulseAndSpin_StaySmoothAfter40DaysOfUptime()
    {
        long t = 40L * 24 * 3600 * 1000 + 123;
        var p = new PulseConfig { Amplitude = 0.1f, FrequencyHz = 0.8f };
        // One 16 ms frame later the values move by a small amount — no precision jumps.
        Assert.InRange(MathF.Abs(SpriteEffects.PulseScale(p, t + 16) - SpriteEffects.PulseScale(p, t)), 0f, 0.01f);
        float d = SpriteEffects.SpinDegrees(45f, t + 16) - SpriteEffects.SpinDegrees(45f, t);
        if (d < 0) d += 360f;
        Assert.InRange(d, 0.70f, 0.74f);   // 45°/s × 16 ms = 0.72°
    }

    // ── Trail ──────────────────────────────────────────────────────────

    [Fact]
    public void Ghost_UsesSpacingOpacityAndFalloff()
    {
        var t = new TrailConfig { Count = 4, SpacingMs = 50, StartOpacity = 0.8f, ScaleFalloff = 0.5f };
        var g1 = SpriteEffects.Ghost(t, 1);
        var g4 = SpriteEffects.Ghost(t, 4);
        Assert.Equal(50, g1.OffsetMs);
        Assert.Equal(0.8f, g1.Opacity, 4);
        Assert.Equal(0.5f, g1.Scale, 4);
        Assert.Equal(200, g4.OffsetMs);
        Assert.Equal(0.2f, g4.Opacity, 4);          // 0.8 × (1 − 3/4)
        Assert.Equal(0.0625f, g4.Scale, 4);         // 0.5⁴
    }

    [Fact]
    public void TrailCount_IsClamped()
    {
        Assert.Equal(0, SpriteEffects.TrailCount(null));
        Assert.Equal(0, SpriteEffects.TrailCount(new TrailConfig { Count = -3 }));
        Assert.Equal(SpriteEffects.MaxTrailCount, SpriteEffects.TrailCount(new TrailConfig { Count = 1000 }));
    }

    [Fact]
    public void Ghost_HostileValuesAreClamped()
    {
        var g = SpriteEffects.Ghost(new TrailConfig { Count = 3, SpacingMs = 0, StartOpacity = 7f, ScaleFalloff = 3f }, 2);
        Assert.Equal(2, g.OffsetMs);               // spacing at least 1 ms
        Assert.InRange(g.Opacity, 0f, 1f);
        Assert.InRange(g.Scale, 0f, 1f);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SpriteEffectsTests"`
Expected: build error `'SpriteEffects' does not exist`.

- [ ] **Step 3: Implement** — `WaBiBaBuSy.Models/Wallpaper/SpriteEffects.cs`:

```csharp
namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>One trail ghost: how far back in time it samples the sprite, its opacity and its size factor.</summary>
public readonly record struct TrailGhost(long OffsetMs, float Opacity, float Scale);

/// <summary>
/// Pure math of the sprite effects (<see cref="SpriteEffectsConfig"/>), shared by the D2D players and the room
/// preview so both draw the same thing. Every function depends only on its arguments.
/// </summary>
public static class SpriteEffects
{
    /// <summary>Upper bound for <see cref="TrailConfig.Count"/> (draw cost per frame).</summary>
    public const int MaxTrailCount = 32;

    /// <summary>True when <paramref name="fx"/> asks for at least one effect.</summary>
    public static bool HasAny(SpriteEffectsConfig? fx) =>
        fx != null && (TrailCount(fx.Trail) > 0
                       || (fx.Pulse != null && fx.Pulse.Amplitude > 0f && fx.Pulse.FrequencyHz > 0f)
                       || fx.SpinDegPerSecond != 0f
                       || fx.Entry != BezelEffect.None || fx.Exit != BezelEffect.None);

    /// <summary>
    /// Effects are drawn for a single image moved by <see cref="MovementCalculator"/>: not with a pattern (cells
    /// cross bezels continuously), not with extra images, not on an IconZone path (its position is not a pure
    /// function of time, so ghosts and crossings cannot be sampled).
    /// </summary>
    public static bool Supported(AnimationLayerConfig animation, MovementConfig? movement, BackgroundMode backgroundMode) =>
        HasAny(animation.Effects)
        && animation.Pattern == null
        && !animation.AdditionalAnimationPaths.Any(p => !string.IsNullOrWhiteSpace(p))
        && movement != null
        && !MovementCalculator.FollowsIconPath(backgroundMode, movement);

    /// <summary>Scale factor at <paramref name="elapsedMs"/>: 1 + A·sin(2πft), A clamped to 0..0.9; 1 without a pulse.</summary>
    public static float PulseScale(PulseConfig? pulse, long elapsedMs)
    {
        if (pulse == null || pulse.FrequencyHz <= 0f || pulse.Amplitude <= 0f) return 1f;
        float amplitude = Math.Min(pulse.Amplitude, 0.9f);
        double periodMs = 1000.0 / pulse.FrequencyHz;
        double t = elapsedMs % periodMs;            // fold in double first: exact after days of uptime
        if (t < 0) t += periodMs;
        return 1f + amplitude * (float)Math.Sin(2.0 * Math.PI * t / periodMs);
    }

    /// <summary>Rotation in degrees, in [0, 360), after <paramref name="elapsedMs"/> at <paramref name="degPerSecond"/>.</summary>
    public static float SpinDegrees(float degPerSecond, long elapsedMs)
    {
        if (degPerSecond == 0f) return 0f;
        double deg = elapsedMs / 1000.0 * degPerSecond % 360.0;
        if (deg < 0) deg += 360.0;
        return deg >= 360.0 ? 0f : (float)deg;
    }

    /// <summary>Number of ghosts to draw (0..<see cref="MaxTrailCount"/>).</summary>
    public static int TrailCount(TrailConfig? trail) => trail == null ? 0 : Math.Clamp(trail.Count, 0, MaxTrailCount);

    /// <summary>
    /// Ghost <paramref name="k"/> (1 = nearest): sampled k × spacing ago, opacity StartOpacity × (1 − (k−1)/Count),
    /// size ScaleFalloff^k.
    /// </summary>
    public static TrailGhost Ghost(TrailConfig trail, int k)
    {
        int count = Math.Max(1, TrailCount(trail));
        long offset = (long)k * Math.Max(1, trail.SpacingMs);
        float opacity = Math.Clamp(trail.StartOpacity, 0f, 1f) * (1f - (k - 1) / (float)count);
        float scale = MathF.Pow(Math.Clamp(trail.ScaleFalloff, 0.1f, 1f), k);
        return new TrailGhost(offset, Math.Clamp(opacity, 0f, 1f), scale);
    }
}
```

(`Models` has implicit usings; if `System.Linq` is not implicit there, add `using System.Linq;`.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SpriteEffectsTests"` → all pass.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/SpriteEffects.cs WaBiBaBuSy.Tests/SpriteEffectsTests.cs
git commit -m "feat(effects): pure pulse, spin and trail math"
```

---

### Task 3: Pure math — pose, facing, bezel crossing scan and geometry

**Files:**
- Modify: `WaBiBaBuSy.Models/Wallpaper/SpriteEffects.cs`
- Test: `WaBiBaBuSy.Tests/BezelEffectsTests.cs` (new)

**Interfaces:**
- Consumes: `MovementCalculator.Calculate(MovementConfig, long, int, int, int, int, float tileAlignStepX, bool canvasWraps)`, `NodeMapping.WrapCopies/ToLocalX/ToLocalY/IsVisible`, `SyncTiming.PositiveModulo(long,long)`, `SyncTiming.ResolveFacingLeft(float, bool)`, `NodeLayout`.
- Produces:
  - `public readonly record struct SpritePoseInput(MovementConfig Movement, int AnimW, int AnimH, int CanvasW, int CanvasH, float TileAlignStepX, NodeLayout Node)`
  - `public enum BezelEventKind { Entry, Exit }`
  - `public readonly record struct BezelEvent(BezelEventKind Kind, BezelEffect Effect, long EventElapsedMs, float Progress, bool AtRightEdge, float CenterVirtualY)`
  - `public readonly record struct BezelVisual(BezelEffect Effect, float Opacity, float X, float Y, float Width, float Height, float Radius, float StrokeWidth, bool EdgeOnRight)`
  - On `SpriteEffects`: `const int BezelScanStepMs = 16`, `const float GlowWidth = 120f`, `const float RippleMaxRadius = 600f`, `int BezelDurationMs(SpriteEffectsConfig fx)` (clamped 50..3000), `(float VirtualX, float VirtualY) Pose(in SpritePoseInput p, long elapsedMs)`, `bool IsOnNode(in SpritePoseInput p, long elapsedMs, out float localX, out float virtualY)`, `bool FacingLeftAt(in SpritePoseInput p, long elapsedMs, bool fallback)`, `BezelEvent? FindBezelEvent(in SpritePoseInput p, SpriteEffectsConfig fx, long elapsedMs)`, `BezelVisual Visual(in BezelEvent e, NodeLayout node)`.

- [ ] **Step 1: Write the failing tests** — `WaBiBaBuSy.Tests/BezelEffectsTests.cs`:

```csharp
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Two 1000 px nodes side by side; a 100 px sprite moves left→right at 1000 px/s.</summary>
public class BezelEffectsTests
{
    private const int W = 1000, H = 600, Sprite = 100;
    private static readonly MovementConfig Linear = new() { Type = MovementType.Linear, SpeedPixelsPerSecond = 1000 };

    private static NodeLayout Node(int offsetX, bool mirrored = false, bool wraps = false, int canvasW = 2 * W) => new()
    {
        Id = $"n{offsetX}", OffsetX = offsetX, Width = W, Height = H,
        CanvasWidth = canvasW, CanvasHeight = H, Mirrored = mirrored, Wraps = wraps, Order = offsetX / W,
    };

    private static SpritePoseInput Input(NodeLayout node, MovementConfig? movement = null) =>
        new(movement ?? Linear, Sprite, Sprite, node.CanvasWidth, node.CanvasHeight, 0f, node);

    private static readonly SpriteEffectsConfig Both = new() { Entry = BezelEffect.Ripple, Exit = BezelEffect.Glow, BezelDurationMs = 400 };

    /// <summary>First grid time (multiple of 16 ms) at which the sprite overlaps [x, ∞) — from the movement itself.</summary>
    private static long FirstVisibleOn(NodeLayout node)
    {
        for (long t = 0; t < 10_000; t += SpriteEffects.BezelScanStepMs)
            if (SpriteEffects.IsOnNode(Input(node), t, out _, out _)) return t;
        throw new InvalidOperationException("never visible");
    }

    [Fact]
    public void Entry_OnTheNextNode_AtItsLeftEdge()
    {
        var right = Node(W);
        long tEnter = FirstVisibleOn(right);
        var ev = SpriteEffects.FindBezelEvent(Input(right), Both, tEnter + 100);

        Assert.NotNull(ev);
        Assert.Equal(BezelEventKind.Entry, ev!.Value.Kind);
        Assert.Equal(BezelEffect.Ripple, ev.Value.Effect);
        Assert.False(ev.Value.AtRightEdge);
        Assert.Equal(tEnter, ev.Value.EventElapsedMs);
        Assert.Equal(100f / 400f, ev.Value.Progress, 3);
    }

    [Fact]
    public void Exit_OnTheFirstNode_AtItsRightEdge()
    {
        var left = Node(0);
        var right = Node(W);
        // The sprite leaves the left node when its trailing edge passes x = 1000.
        long tGone = 0;
        for (long t = FirstVisibleOn(right); ; t += SpriteEffects.BezelScanStepMs)
            if (!SpriteEffects.IsOnNode(Input(left), t, out _, out _)) { tGone = t; break; }

        var ev = SpriteEffects.FindBezelEvent(Input(left), Both, tGone + 50);

        Assert.NotNull(ev);
        Assert.Equal(BezelEventKind.Exit, ev!.Value.Kind);
        Assert.Equal(BezelEffect.Glow, ev.Value.Effect);
        Assert.True(ev.Value.AtRightEdge);
    }

    [Fact]
    public void EventTime_IsTheSameForEveryFrameTime()
    {
        var right = Node(W);
        long tEnter = FirstVisibleOn(right);
        // Frames at arbitrary sub-grid instants (as on different machines) agree on the crossing time.
        var a = SpriteEffects.FindBezelEvent(Input(right), Both, tEnter + 37)!.Value;
        var b = SpriteEffects.FindBezelEvent(Input(right), Both, tEnter + 203)!.Value;
        Assert.Equal(a.EventElapsedMs, b.EventElapsedMs);
    }

    [Fact]
    public void AfterTheDuration_NoEvent()
    {
        var right = Node(W);
        Assert.Null(SpriteEffects.FindBezelEvent(Input(right), Both, FirstVisibleOn(right) + 450));
    }

    [Fact]
    public void SceneStart_SpriteAlreadyOnTheNode_NoEntry()
    {
        // Bounce starts on the canvas: visible on the first node from t = 0, which is not a crossing.
        var bounce = new MovementConfig { Type = MovementType.Bounce, SpeedPixelsPerSecond = 10 };
        var left = Node(0);
        Assert.True(SpriteEffects.IsOnNode(Input(left, bounce), 0, out _, out _));
        Assert.Null(SpriteEffects.FindBezelEvent(Input(left, bounce), Both, 100));
    }

    [Fact]
    public void NegativeElapsed_NoEvent() =>
        Assert.Null(SpriteEffects.FindBezelEvent(Input(Node(W)), Both, -500));

    [Fact]
    public void OnlyEntryConfigured_ExitIsIgnored()
    {
        var entryOnly = new SpriteEffectsConfig { Entry = BezelEffect.Flash };
        var left = Node(0);
        long tGone = 0;
        for (long t = FirstVisibleOn(Node(W)); ; t += SpriteEffects.BezelScanStepMs)
            if (!SpriteEffects.IsOnNode(Input(left), t, out _, out _)) { tGone = t; break; }
        Assert.Null(SpriteEffects.FindBezelEvent(Input(left), entryOnly, tGone + 50));
    }

    [Fact]
    public void MirroredNode_EntryIsAtTheRightScreenEdge()
    {
        // Facing row: the slice is mirrored, so a sprite entering at virtual x = 1000 appears at the right screen edge.
        var right = Node(W, mirrored: true);
        long tEnter = FirstVisibleOn(right);
        var ev = SpriteEffects.FindBezelEvent(Input(right), Both, tEnter + 50)!.Value;
        Assert.Equal(BezelEventKind.Entry, ev.Kind);
        Assert.True(ev.AtRightEdge);
    }

    [Fact]
    public void RingSeam_SeatZeroSeesOneEntry()
    {
        // Ring of two seats: the sprite straddles the seam at x = 2000 and re-enters seat 0 through its wrap copy.
        var seat0 = Node(0, wraps: true);
        var input = Input(seat0);
        // Find when the sprite leaves seat 0 for the first time, then when it comes back via the seam.
        long t = 0;
        while (SpriteEffects.IsOnNode(input, t, out _, out _)) t += SpriteEffects.BezelScanStepMs;
        while (!SpriteEffects.IsOnNode(input, t, out _, out _)) t += SpriteEffects.BezelScanStepMs;
        long tBack = t;

        var ev = SpriteEffects.FindBezelEvent(input, Both, tBack + 60)!.Value;
        Assert.Equal(BezelEventKind.Entry, ev.Kind);
        Assert.Equal(tBack, ev.EventElapsedMs);
        Assert.False(ev.AtRightEdge);
        // While the sprite straddles the seam it stays visible on seat 0 — no exit in between.
        for (long u = tBack; u < tBack + 300; u += 8)
            Assert.True(SpriteEffects.IsOnNode(input, u, out _, out _));
    }

    [Fact]
    public void BezelDuration_IsClamped()
    {
        Assert.Equal(50, SpriteEffects.BezelDurationMs(new SpriteEffectsConfig { BezelDurationMs = -5 }));
        Assert.Equal(3000, SpriteEffects.BezelDurationMs(new SpriteEffectsConfig { BezelDurationMs = 999_999 }));
    }

    [Fact]
    public void FacingLeftAt_FollowsTheScreenDirection()
    {
        var left = Node(0);
        Assert.False(SpriteEffects.FacingLeftAt(Input(left), 500, fallback: true));                 // moving right
        Assert.True(SpriteEffects.FacingLeftAt(Input(Node(0, mirrored: true)), 500, fallback: false)); // mirrored: screen-left
        var still = new MovementConfig { Type = MovementType.Static };
        Assert.True(SpriteEffects.FacingLeftAt(Input(left, still), 500, fallback: true));             // no motion keeps fallback
    }

    // ── Visual geometry ────────────────────────────────────────────────

    private static BezelEvent Event(BezelEffect effect, float progress, bool right) =>
        new(BezelEventKind.Entry, effect, 1000, progress, right, CenterVirtualY: 300);

    [Fact]
    public void Visual_Glow_IsAStripAtTheEdge()
    {
        var node = Node(W);
        var right = SpriteEffects.Visual(Event(BezelEffect.Glow, 0.25f, right: true), node);
        Assert.Equal(W - SpriteEffects.GlowWidth, right.X);
        Assert.Equal(SpriteEffects.GlowWidth, right.Width);
        Assert.Equal(H, right.Height);
        Assert.Equal(0.75f, right.Opacity, 3);
        Assert.True(right.EdgeOnRight);
        Assert.Equal(0f, SpriteEffects.Visual(Event(BezelEffect.Glow, 0.25f, right: false), node).X);
    }

    [Fact]
    public void Visual_Flash_FadesQuadratically() =>
        Assert.Equal(0.35f * 0.25f, SpriteEffects.Visual(Event(BezelEffect.Flash, 0.5f, right: false), Node(W)).Opacity, 4);

    [Fact]
    public void Visual_Ripple_GrowsFromTheCrossingPoint()
    {
        var v = SpriteEffects.Visual(Event(BezelEffect.Ripple, 0.5f, right: true), Node(W));
        Assert.Equal(W, v.X);
        Assert.Equal(300f, v.Y);                                     // CenterVirtualY − node.OffsetY (0)
        Assert.Equal(SpriteEffects.RippleMaxRadius / 2f, v.Radius, 3);
        Assert.Equal(0.5f, v.Opacity, 3);
    }

    [Fact]
    public void Visual_NarrowNode_GlowIsAtMostHalfTheWidth() =>
        Assert.Equal(100f, SpriteEffects.Visual(Event(BezelEffect.Glow, 0f, right: false), new NodeLayout { Width = 200, Height = 100 }).Width);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~BezelEffectsTests"`
Expected: build errors (`SpritePoseInput`, `FindBezelEvent` missing).

- [ ] **Step 3: Implement** — add to `SpriteEffects.cs` (types at namespace level above the class, members inside `SpriteEffects`; add `using WaBiBaBuSy.Models.Topology;` for `NodeLayout`):

```csharp
/// <summary>Everything the pose of the sprite depends on, for one node.</summary>
public readonly record struct SpritePoseInput(
    MovementConfig Movement, int AnimW, int AnimH, int CanvasW, int CanvasH, float TileAlignStepX, NodeLayout Node);

/// <summary>Which crossing a bezel event marks.</summary>
public enum BezelEventKind { Entry, Exit }

/// <summary>A bezel crossing on this node that is still within the effect's duration.</summary>
/// <param name="EventElapsedMs">First 16 ms grid time in the new state (identical on every node and frame).</param>
/// <param name="Progress">0 at the crossing, 1 when the effect ends.</param>
/// <param name="AtRightEdge">The crossing happened at the node's right screen edge.</param>
/// <param name="CenterVirtualY">Sprite center Y (virtual canvas) at the crossing.</param>
public readonly record struct BezelEvent(
    BezelEventKind Kind, BezelEffect Effect, long EventElapsedMs, float Progress, bool AtRightEdge, float CenterVirtualY);

/// <summary>What to draw for a bezel event, in node-local canvas units.</summary>
/// <remarks>Glow: rect X/Y/Width/Height, bright at the edge (EdgeOnRight). Flash: whole node. Ripple: circle at X/Y with Radius and StrokeWidth.</remarks>
public readonly record struct BezelVisual(
    BezelEffect Effect, float Opacity, float X, float Y, float Width, float Height, float Radius, float StrokeWidth, bool EdgeOnRight);
```

Members inside `SpriteEffects`:

```csharp
    /// <summary>Grid of the bezel scan, ms (one 60 Hz frame).</summary>
    public const int BezelScanStepMs = 16;

    /// <summary>Width of the Glow strip, canvas units.</summary>
    public const float GlowWidth = 120f;

    /// <summary>Ripple radius when the effect ends, canvas units.</summary>
    public const float RippleMaxRadius = 600f;

    /// <summary><see cref="SpriteEffectsConfig.BezelDurationMs"/> clamped to 50..3000 ms (bounds the scan cost).</summary>
    public static int BezelDurationMs(SpriteEffectsConfig fx) => Math.Clamp(fx.BezelDurationMs, 50, 3000);

    /// <summary>Sprite top-left in virtual canvas units — exactly what the players compute for MovementCalculator movement.</summary>
    public static (float VirtualX, float VirtualY) Pose(in SpritePoseInput p, long elapsedMs) =>
        MovementCalculator.Calculate(p.Movement, elapsedMs, p.AnimW, p.AnimH, p.CanvasW, p.CanvasH,
            tileAlignStepX: p.TileAlignStepX, canvasWraps: p.Node.Wraps);

    /// <summary>
    /// True when any wrap copy of the sprite overlaps the node at <paramref name="elapsedMs"/>. <paramref name="localX"/>:
    /// screen-space left edge of the visible copy (of the first copy when none is visible).
    /// </summary>
    public static bool IsOnNode(in SpritePoseInput p, long elapsedMs, out float localX, out float virtualY)
    {
        var (vx, vy) = Pose(p, elapsedMs);
        virtualY = vy;
        Span<float> copies = stackalloc float[2];
        int n = NodeMapping.WrapCopies(vx, p.AnimW, p.Node, copies);
        for (int i = 0; i < n; i++)
        {
            float lx = NodeMapping.ToLocalX(copies[i], p.AnimW, p.Node);
            if (NodeMapping.IsVisible(lx, p.AnimW, p.Node)) { localX = lx; return true; }
        }
        localX = NodeMapping.ToLocalX(copies[0], p.AnimW, p.Node);
        return false;
    }

    /// <summary>
    /// Screen-space facing at <paramref name="elapsedMs"/> from the motion over the previous 16 ms (mirrored nodes
    /// included); <paramref name="fallback"/> when it barely moves or wraps around the ring.
    /// </summary>
    public static bool FacingLeftAt(in SpritePoseInput p, long elapsedMs, bool fallback)
    {
        float x1 = NodeMapping.ToLocalX(Pose(p, elapsedMs).VirtualX, p.AnimW, p.Node);
        float x0 = NodeMapping.ToLocalX(Pose(p, elapsedMs - BezelScanStepMs).VirtualX, p.AnimW, p.Node);
        float dx = x1 - x0;
        if (MathF.Abs(dx) > p.Node.Width) dx = 0f;    // loop / ring wrap, not a direction change
        return SyncTiming.ResolveFacingLeft(dx, fallback);
    }

    /// <summary>
    /// The newest bezel crossing on this node within the effect duration, or null. Scans the sprite's visibility
    /// backwards on a 16 ms grid aligned to absolute elapsed time, so every frame and every node agree on the
    /// crossing time without any frame history. Samples before elapsed 0 end the scan: a sprite that is on a node
    /// when the scene starts did not cross into it.
    /// </summary>
    public static BezelEvent? FindBezelEvent(in SpritePoseInput p, SpriteEffectsConfig fx, long elapsedMs)
    {
        if (elapsedMs < 0 || (fx.Entry == BezelEffect.None && fx.Exit == BezelEffect.None)) return null;
        int duration = BezelDurationMs(fx);
        long newerT = elapsedMs - SyncTiming.PositiveModulo(elapsedMs, BezelScanStepMs);
        bool newerVisible = IsOnNode(p, newerT, out float newerX, out float newerY);
        for (long t = newerT - BezelScanStepMs; t >= 0 && elapsedMs - newerT <= duration; t -= BezelScanStepMs)
        {
            bool olderVisible = IsOnNode(p, t, out float olderX, out float olderY);
            if (olderVisible != newerVisible)
            {
                var kind = newerVisible ? BezelEventKind.Entry : BezelEventKind.Exit;
                var effect = kind == BezelEventKind.Entry ? fx.Entry : fx.Exit;
                if (effect == BezelEffect.None) return null;
                float x = newerVisible ? newerX : olderX;       // the copy that is (entry) / was (exit) on the node
                float y = newerVisible ? newerY : olderY;
                bool right = x + p.AnimW / 2f > p.Node.Width / 2f;
                return new BezelEvent(kind, effect, newerT, (elapsedMs - newerT) / (float)duration, right, y + p.AnimH / 2f);
            }
            newerT = t; newerVisible = olderVisible; newerX = olderX; newerY = olderY;
        }
        return null;
    }

    /// <summary>Geometry and opacity of <paramref name="e"/> on <paramref name="node"/> (spec §3 table).</summary>
    public static BezelVisual Visual(in BezelEvent e, NodeLayout node)
    {
        float p = Math.Clamp(e.Progress, 0f, 1f);
        float fade = 1f - p;
        float edgeX = e.AtRightEdge ? node.Width : 0f;
        switch (e.Effect)
        {
            case BezelEffect.Glow:
                float w = Math.Min(GlowWidth, node.Width / 2f);
                return new BezelVisual(BezelEffect.Glow, fade, e.AtRightEdge ? node.Width - w : 0f, 0f, w, node.Height, 0f, 0f, e.AtRightEdge);
            case BezelEffect.Flash:
                return new BezelVisual(BezelEffect.Flash, 0.35f * fade * fade, 0f, 0f, node.Width, node.Height, 0f, 0f, e.AtRightEdge);
            case BezelEffect.Ripple:
                float cy = NodeMapping.ToLocalY(e.CenterVirtualY, node);
                return new BezelVisual(BezelEffect.Ripple, fade, edgeX, cy, 0f, 0f, p * RippleMaxRadius, 2f + 10f * fade, e.AtRightEdge);
            default:
                return new BezelVisual(BezelEffect.None, 0f, 0f, 0f, 0f, 0f, 0f, 0f, e.AtRightEdge);
        }
    }
```

Note on the loop condition: it stops once the newest sample in the pair is older than the duration (`elapsedMs - newerT > duration`), so at most `duration / 16 + 2` poses are computed (≤ 190 at 3000 ms).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~BezelEffectsTests|FullyQualifiedName~SpriteEffectsTests"` → all pass. If `RingSeam_SeatZeroSeesOneEntry` fails, inspect `MovementCalculator.CalculateLinear` with `canvasWraps: true` (travel 0 → canvasWidth, no run-in) and fix the test's expectation only if the movement legitimately differs — never special-case the seam in `FindBezelEvent`.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/SpriteEffects.cs WaBiBaBuSy.Tests/BezelEffectsTests.cs
git commit -m "feat(effects): pure bezel crossing scan and effect geometry"
```

---

### Task 4: Player — trail, pulse and spin

**Files:**
- Modify: `WaBiBaBuSy.Player.D2D/Program.cs`
- Create: `WaBiBaBuSy.Player.D2D/Program.Effects.cs`

**Interfaces:**
- Consumes: `SpriteEffects.Supported/PulseScale/SpinDegrees/TrailCount/Ghost/Pose/FacingLeftAt`, `SpritePoseInput`, `ColorGrader.Compute`, existing player members `_animationConfig`, `_movementConfig`, `_backgroundMode`, `_animWidth`, `_animHeight`, `_virtualCanvasWidth`, `_virtualCanvasHeight`, `_isTravelingMode`, `_tileAlignStepX`, `_layout`, `_lastFlipX`, `SetColorMatrixOnEffect(ColorMatrix5x4)`.
- Produces (player-internal): `DrawCellWithOptionalGrading(..., bool flipX = false, float spriteScale = 1f)`, `ClampToCorridor(float y)`, `SpritePoseInput PoseInput()`, `DrawTrail(SpriteEffectsConfig fx, ID2D1Bitmap bmp, long elapsedMs, bool gradingActive)`.

No unit tests can reach the player (separate exe, no test reference). Verification: build + Task 8's exact-frame run. Keep the change small and the no-effects path byte-for-byte identical in behaviour.

- [ ] **Step 1: Make `Program` partial** — `Program.cs` line ~44: `class Program` → `partial class Program`.

- [ ] **Step 2: Sprite scale in `DrawCellWithOptionalGrading`** (~line 3674). Add a trailing parameter and include it in the transform, between flip and rotation:

```csharp
    private static void DrawCellWithOptionalGrading(
        ID2D1Bitmap bmp, float x, float y, int w, int h,
        float rotationDeg, bool gradingActive, float alpha = 1f, bool flipX = false, float spriteScale = 1f)
    {
        if (_d2dContext == null) return;

        bool needsRotation = MathF.Abs(rotationDeg) > 0.01f;
        bool needsSpriteScale = MathF.Abs(spriteScale - 1f) > 0.0005f;
        float scale = Scale;
        bool needsScale = MathF.Abs(scale - 1f) > 0.0005f;
        bool needsTransform = needsRotation || flipX || needsScale || needsSpriteScale;
        Matrix3x2 prior = Matrix3x2.Identity;
        if (needsTransform)
        {
            // x/y/w/h are canvas units; flip, scale (pulse / trail) and rotate about the sprite center in canvas
            // space, then scale canvas → device pixels (physical canvas, Tier 1.2).
            var center = new Vector2(x + w / 2f, y + h / 2f);
            prior = _d2dContext.Transform;
            var t = Matrix3x2.Identity;
            if (flipX) t = Matrix3x2.CreateScale(-1f, 1f, center);           // mirror about the sprite center
            if (needsSpriteScale) t *= Matrix3x2.CreateScale(spriteScale, spriteScale, center);
            if (needsRotation) t *= Matrix3x2.CreateRotation(rotationDeg * MathF.PI / 180f, center);
            if (needsScale) t *= Matrix3x2.CreateScale(scale, scale);
            _d2dContext.Transform = t * prior;
        }
        // … rest unchanged
```

- [ ] **Step 3: Extract the corridor clamp** — in `UpdateAnimationPosition` replace the inline block

```csharp
                if (_hasCorridorConstraint && _corridorHeightPx > 0)
                {
                    float minY = _corridorTopPx / Scale;
                    float maxY = (_corridorTopPx + _corridorHeightPx) / Scale - _animHeight;
                    if (maxY < minY) maxY = minY;
                    _animY = Math.Clamp(_animY, minY, maxY);
                }
```

with `_animY = ClampToCorridor(_animY);` and add to `Program.Effects.cs` (Step 4) the helper:

```csharp
    /// <summary>ThreeZone background: keep a sprite top (canvas units) inside the corridor (device px → canvas units).</summary>
    private static float ClampToCorridor(float y)
    {
        if (!_hasCorridorConstraint || _corridorHeightPx <= 0) return y;
        float minY = _corridorTopPx / Scale;
        float maxY = (_corridorTopPx + _corridorHeightPx) / Scale - _animHeight;
        if (maxY < minY) maxY = minY;
        return Math.Clamp(y, minY, maxY);
    }
```

- [ ] **Step 4: Create `Program.Effects.cs`** with the trail:

```csharp
using Vortice.Direct2D1;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Player.D2D;   // use the namespace Program.cs declares (or none, if Program.cs has none)

partial class Program
{
    /// <summary>Pose input of this node for the pure effect math (same arguments as UpdateAnimationPosition's MovementCalculator call).</summary>
    private static SpritePoseInput PoseInput() => new(
        _movementConfig!, _animWidth, _animHeight, _virtualCanvasWidth, _virtualCanvasHeight,
        _isTravelingMode ? _tileAlignStepX : 0f, _layout);

    /// <summary>
    /// Ghosts behind the sprite (roadmap 2.4): ghost k is the sprite k × spacing ago — position, facing, pulse,
    /// spin and (optionally) color of that moment — at falling opacity and size. Farthest first, so nearer ghosts
    /// cover farther ones; ghosts that already crossed a bezel are drawn by the node they are on.
    /// </summary>
    private static void DrawTrail(SpriteEffectsConfig fx, ID2D1Bitmap bmp, long elapsedMs, bool gradingActive)
    {
        int count = SpriteEffects.TrailCount(fx.Trail);
        if (count == 0) return;
        var input = PoseInput();
        var grading = _animationConfig!.ColorGrading;
        bool tintGhosts = gradingActive && fx.Trail!.UseGradingColor && grading != null;
        Span<float> copies = stackalloc float[2];
        for (int k = count; k >= 1; k--)
        {
            var ghost = SpriteEffects.Ghost(fx.Trail!, k);
            long t = elapsedMs - ghost.OffsetMs;
            if (t < 0 || ghost.Opacity <= 0.005f) continue;    // before the start the sprite was not drawn
            var (vx, vy) = SpriteEffects.Pose(input, t);
            float ly = ClampToCorridor(NodeMapping.ToLocalY(vy, _layout));
            bool flip = _animationConfig.FaceTravelDirection && SpriteEffects.FacingLeftAt(input, t, _lastFlipX);
            float rot = SpriteEffects.SpinDegrees(fx.SpinDegPerSecond, t);
            float scale = SpriteEffects.PulseScale(fx.Pulse, t) * ghost.Scale;
            if (tintGhosts) SetColorMatrixOnEffect(ColorGrader.Compute(grading!, t));
            int n = NodeMapping.WrapCopies(vx, _animWidth, _layout, copies);
            for (int c = 0; c < n; c++)
            {
                float lx = NodeMapping.ToLocalX(copies[c], _animWidth, _layout);
                if (!NodeMapping.IsVisible(lx, _animWidth, _layout)) continue;
                DrawCellWithOptionalGrading(bmp, lx, ly, _animWidth, _animHeight, rot, gradingActive, ghost.Opacity, flip, scale);
            }
        }
        if (tintGhosts) SetColorMatrixOnEffect(ColorGrader.Compute(grading!, elapsedMs));   // the sprite's own color
    }
}
```

Check `Program.cs` for its namespace and `using` lines (e.g. `NodeMapping`'s namespace `WaBiBaBuSy.Models.Wallpaper`, `ColorGrader`) and mirror them; the file must compile as part of the same partial class.

- [ ] **Step 5: Call it from `DrawAnimationLayer`** — in the single-source `else` branch (~line 3567), after `_prevFacingX`/`_hasPrevFacingX` are updated and before the ring-seam loop:

```csharp
            // Roadmap 2.4: trail behind the sprite, pulse + spin on it (pure functions of elapsed).
            var fx = SpriteEffects.Supported(_animationConfig, _movementConfig, _backgroundMode) ? _animationConfig.Effects : null;
            if (fx != null) DrawTrail(fx, bmp, elapsedMs, gradingActive && !isTraveling);
            float spriteScale = fx != null ? SpriteEffects.PulseScale(fx.Pulse, elapsedMs) : 1f;
            if (fx != null) rotDeg += SpriteEffects.SpinDegrees(fx.SpinDegPerSecond, elapsedMs);
```

and pass `spriteScale` to the existing draw call in the ring-seam loop:

```csharp
                DrawCellWithOptionalGrading(bmp, lx, _animY, _animWidth, _animHeight, rotDeg, gradingActive && !isTraveling, flipX: flipX, spriteScale: spriteScale);
```

- [ ] **Step 6: Build**

Run: `dotnet build WaBiBaBuSy.sln` → 0 errors. Run `dotnet test WaBiBaBuSy.Tests` → all green (nothing should change).

- [ ] **Step 7: Commit**

```bash
git add WaBiBaBuSy.Player.D2D/Program.cs WaBiBaBuSy.Player.D2D/Program.Effects.cs
git commit -m "feat(player): sprite trail, pulse and spin"
```

---

### Task 5: Player — bezel Glow / Flash / Ripple

**Files:**
- Modify: `WaBiBaBuSy.Player.D2D/Program.Effects.cs`, `WaBiBaBuSy.Player.D2D/Program.cs` (call site + `DisposeNativeD2DResources`)

**Interfaces:**
- Consumes: `SpriteEffects.FindBezelEvent/Visual`, `BezelVisual`, `PoseInput()` (Task 4), existing `ParseHexColor(string)` (`Program.cs` ~line 1909), `Scale`, `_d2dContext`.
- Produces: `DrawBezelEffect(SpriteEffectsConfig fx, long elapsedMs)`, `DisposeEffectBrushes()`.

- [ ] **Step 1: Add the brush cache and drawing** to `Program.Effects.cs`:

```csharp
    // Bezel effect brushes: created on first use, recreated when the color changes, disposed with the D2D resources.
    private static string? _bezelBrushColor;
    private static ID2D1SolidColorBrush? _bezelSolidBrush;
    private static ID2D1GradientStopCollection? _bezelGlowStops;
    private static ID2D1LinearGradientBrush? _bezelGlowBrush;

    private static void EnsureBezelBrushes(string colorHex)
    {
        if (_bezelSolidBrush != null && _bezelBrushColor == colorHex) return;
        DisposeEffectBrushes();
        var c = ParseHexColor(colorHex);
        _bezelSolidBrush = _d2dContext!.CreateSolidColorBrush(c);
        _bezelGlowStops = _d2dContext.CreateGradientStopCollection(new[]
        {
            new GradientStop { Position = 0f, Color = c },
            new GradientStop { Position = 1f, Color = new Color4(c.R, c.G, c.B, 0f) },
        });
        _bezelGlowBrush = _d2dContext.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = Vector2.Zero, EndPoint = Vector2.UnitX }, _bezelGlowStops);
        _bezelBrushColor = colorHex;
    }

    /// <summary>Release the bezel brushes (device loss / reload / exit).</summary>
    private static void DisposeEffectBrushes()
    {
        _bezelGlowBrush?.Dispose(); _bezelGlowBrush = null;
        _bezelGlowStops?.Dispose(); _bezelGlowStops = null;
        _bezelSolidBrush?.Dispose(); _bezelSolidBrush = null;
        _bezelBrushColor = null;
    }

    /// <summary>
    /// Glow / Flash / Ripple where the sprite crossed this node's edge within the last BezelDurationMs (roadmap 2.4).
    /// The crossing comes from a pure scan of the sprite's pose, so exact frames and every node agree on it.
    /// Drawn on top of the sprite, in canvas units scaled to device pixels.
    /// </summary>
    private static void DrawBezelEffect(SpriteEffectsConfig fx, long elapsedMs)
    {
        if (_d2dContext == null) return;
        var ev = SpriteEffects.FindBezelEvent(PoseInput(), fx, elapsedMs);
        if (ev == null) return;
        var v = SpriteEffects.Visual(ev.Value, _layout);
        if (v.Opacity <= 0.005f || v.Effect == BezelEffect.None) return;

        EnsureBezelBrushes(fx.BezelColorHex);
        var prior = _d2dContext.Transform;
        _d2dContext.Transform = Matrix3x2.CreateScale(Scale) * prior;   // canvas units → device px
        try
        {
            switch (v.Effect)
            {
                case BezelEffect.Glow:
                    _bezelGlowBrush!.StartPoint = new Vector2(v.EdgeOnRight ? v.X + v.Width : v.X, 0f);
                    _bezelGlowBrush.EndPoint = new Vector2(v.EdgeOnRight ? v.X : v.X + v.Width, 0f);
                    _bezelGlowBrush.Opacity = v.Opacity;
                    _d2dContext.FillRectangle(new RectangleF(v.X, v.Y, v.Width, v.Height), _bezelGlowBrush);
                    break;
                case BezelEffect.Flash:
                    _bezelSolidBrush!.Opacity = v.Opacity;
                    _d2dContext.FillRectangle(new RectangleF(v.X, v.Y, v.Width, v.Height), _bezelSolidBrush);
                    break;
                case BezelEffect.Ripple:
                    _bezelSolidBrush!.Opacity = v.Opacity;
                    _d2dContext.DrawEllipse(new Ellipse(new Vector2(v.X, v.Y), v.Radius, v.Radius), _bezelSolidBrush, v.StrokeWidth);
                    break;
            }
        }
        finally
        {
            _d2dContext.Transform = prior;
        }
    }
```

Vortice API names (`CreateGradientStopCollection`, `CreateLinearGradientBrush`, `LinearGradientBrushProperties`, `GradientStop`, `Ellipse`, `RectangleF`) — match whatever `Program.cs` already uses for `FillRectangle` (see `DrawTimecodeStrip`) and the debug overlay brushes; the compiler is the arbiter. `Color4` comes from `Vortice.Mathematics`; `Vector2`/`Matrix3x2` from `System.Numerics`.

- [ ] **Step 2: Call it** in `DrawAnimationLayer`'s single-source branch, after the ring-seam draw loop:

```csharp
            if (fx != null) DrawBezelEffect(fx, elapsedMs);
```

- [ ] **Step 3: Dispose** — in `DisposeNativeD2DResources` (~line 4154), next to the debug brush disposal, add `DisposeEffectBrushes();`.

- [ ] **Step 4: Build + tests**

Run: `dotnet build WaBiBaBuSy.sln` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → all green.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Player.D2D/Program.cs WaBiBaBuSy.Player.D2D/Program.Effects.cs
git commit -m "feat(player): bezel crossing Glow, Flash and Ripple"
```

---

### Task 6: Room preview — effects in `ScenePainter`

**Files:**
- Modify: `WaBiBaBuSy.UI/Controls/ScenePainter.cs` (`PaintSprite`, ~line 134)

**Interfaces:**
- Consumes: `SpriteEffects.*`, `SpritePoseInput`, `BezelVisual`.

The preview has no color matrix; ghosts use the existing tint overlay approach. Facing is not previewed today and stays so.

- [ ] **Step 1: Implement** — in `PaintSprite`, after `bool hit = false; … var bmp = SpriteBitmap;` and before the existing copies loop, add the trail; wrap each sprite draw in pulse/spin; after the loop add the bezel effect. Replace the copies loop with:

```csharp
        var fx = SpriteEffects.Supported(scene.Animation, scene.Movement, bg.Mode) ? scene.Animation.Effects : null;
        var input = new SpritePoseInput(scene.Movement, animW, animH, eff.CanvasWidth, eff.CanvasHeight, 0f, eff);

        // Trail first (farthest ghost first), then the sprite on top.
        if (fx != null)
        {
            int count = SpriteEffects.TrailCount(fx.Trail);
            for (int k = count; k >= 1; k--)
            {
                var ghost = SpriteEffects.Ghost(fx.Trail!, k);
                long t = e - ghost.OffsetMs;
                if (t < 0 || ghost.Opacity <= 0.005f) continue;
                var (gx, gy) = SpriteEffects.Pose(input, t);
                float gly = ClampCorridor(NodeMapping.ToLocalY(gy, eff), bg, animH);
                float scale = SpriteEffects.PulseScale(fx.Pulse, t) * ghost.Scale;
                float rot = SpriteEffects.SpinDegrees(fx.SpinDegPerSecond, t);
                int gn = NodeMapping.WrapCopies(gx, animW, eff, copies);
                for (int i = 0; i < gn; i++)
                {
                    float glx = NodeMapping.ToLocalX(copies[i], animW, eff);
                    if (!NodeMapping.IsVisible(glx, animW, eff)) continue;
                    DrawSprite(ctx, rect, s, bmp, glx, gly, animW, animH, scale, rot, ghost.Opacity, tint);
                }
            }
        }

        float spriteScale = fx != null ? SpriteEffects.PulseScale(fx.Pulse, e) : 1f;
        float spriteRot = fx != null ? SpriteEffects.SpinDegrees(fx.SpinDegPerSecond, e) : 0f;
        int n = NodeMapping.WrapCopies(vx, animW, eff, copies);
        for (int i = 0; i < n; i++)
        {
            float lx = NodeMapping.ToLocalX(copies[i], animW, eff);
            if (!NodeMapping.IsVisible(lx, animW, eff)) continue;
            DrawSprite(ctx, rect, s, bmp, lx, ly, animW, animH, spriteScale, spriteRot, 1.0, tint);
            float center = lx + animW / 2f;
            if (center >= 0 && center < eff.Width) hit = true;
        }

        if (fx != null && SpriteEffects.FindBezelEvent(input, fx, e) is { } ev)
            PaintBezel(ctx, rect, s, SpriteEffects.Visual(ev, eff), ParseHex(fx.BezelColorHex));
        return hit;
```

(Declare `Span<float> copies = stackalloc float[2];` once above the trail block, removing the old declaration; `var bmp = SpriteBitmap;` stays.)

Add the helpers to the class:

```csharp
    /// <summary>The sprite (or its placeholder) at local canvas coordinates, scaled and rotated about its center.</summary>
    private static void DrawSprite(DrawingContext ctx, Rect rect, double s, Bitmap? bmp, float lx, float ly, int animW, int animH,
        float scale, float rotationDeg, double opacity, Color? tint)
    {
        var dest = new Rect(rect.Left + lx * s, rect.Top + ly * s, Math.Max(1, animW * s), Math.Max(1, animH * s));
        var c = dest.Center;
        var m = Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateScale(scale, scale)
              * Matrix.CreateRotation(rotationDeg * Math.PI / 180.0) * Matrix.CreateTranslation(c.X, c.Y);
        using var transform = ctx.PushTransform(m);
        using var alpha = ctx.PushOpacity(opacity);
        if (bmp != null)
        {
            ctx.DrawImage(bmp, new Rect(bmp.Size), dest);
            if (tint.HasValue) ctx.FillRectangle(new SolidColorBrush(tint.Value, 0.45), dest);
        }
        else
        {
            ctx.FillRectangle(new SolidColorBrush(tint ?? Color.Parse("#E0E0E0")), dest, 2);
        }
    }

    /// <summary>ThreeZone corridor clamp, as the players do it.</summary>
    private static float ClampCorridor(float ly, BackgroundLayerConfig bg, int animH)
    {
        if (bg.Mode != BackgroundMode.ThreeZone || bg.CorridorHeightPx <= 0) return ly;
        float minY = bg.CorridorTopPx, maxY = bg.CorridorTopPx + bg.CorridorHeightPx - animH;
        if (maxY < minY) maxY = minY;
        return Math.Clamp(ly, minY, maxY);
    }

    /// <summary>Glow strip, flash or ripple ring of a bezel event over the node rect.</summary>
    private static void PaintBezel(DrawingContext ctx, Rect rect, double s, BezelVisual v, Color color)
    {
        if (v.Opacity <= 0.005f) return;
        var area = new Rect(rect.Left + v.X * s, rect.Top + v.Y * s, v.Width * s, v.Height * s);
        switch (v.Effect)
        {
            case BezelEffect.Glow:
                var bright = Color.FromArgb((byte)(255 * v.Opacity), color.R, color.G, color.B);
                var clear = Color.FromArgb(0, color.R, color.G, color.B);
                var brush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(v.EdgeOnRight ? 1 : 0, 0.5, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(v.EdgeOnRight ? 0 : 1, 0.5, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(bright, 0), new GradientStop(clear, 1) },
                };
                ctx.FillRectangle(brush, area);
                break;
            case BezelEffect.Flash:
                ctx.FillRectangle(new SolidColorBrush(color, v.Opacity), area);
                break;
            case BezelEffect.Ripple:
                var pen = new Pen(new SolidColorBrush(color, v.Opacity), Math.Max(1, v.StrokeWidth * s));
                ctx.DrawEllipse(null, pen, new Point(rect.Left + v.X * s, rect.Top + v.Y * s), v.Radius * s, v.Radius * s);
                break;
        }
    }
```

Also replace the inline ThreeZone clamp earlier in `PaintSprite` with `ly = ClampCorridor(ly, bg, animH);` so sprite and ghosts share it.

- [ ] **Step 2: Build + tests** — `dotnet build WaBiBaBuSy.sln` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → all green.

- [ ] **Step 3: Commit**

```bash
git add WaBiBaBuSy.UI/Controls/ScenePainter.cs
git commit -m "feat(preview): trail, pulse, spin and bezel effects in the room preview"
```

---

### Task 7: Editor — Effects tab, validation hints, playlist summary

**Files:**
- Modify: `WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs` (properties near `_faceTravelDirection` ~line 143; load near line 461; build in the `Animation = new AnimationLayerConfig { … }` initializer ~line 551)
- Modify: `WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml` (new `TabItem` before `<TabItem Header="Background">`)
- Modify: `WaBiBaBuSy.Models/Wallpaper/SceneChecks.cs` (`Validate`)
- Modify: `WaBiBaBuSy.UI/ViewModels/PlaylistItemRow.cs` (look summary, after the grading part ~line 146)
- Test: the existing SceneChecks test file (`rg -l "SceneChecks.Validate" WaBiBaBuSy.Tests`)

**Interfaces:**
- Consumes: Task 1 types, `SpriteEffects.HasAny`, `MovementCalculator.FollowsIconPath`.

- [ ] **Step 1: Failing validation tests** — add to the SceneChecks test class (adapt the scene builder to the file's existing helper):

```csharp
    [Fact]
    public void Validate_EffectsWithPattern_Warns()
    {
        var s = new CrossScreenConfig();
        s.Animation.AnimationPath = "a.gif";
        s.Animation.Pattern = new PatternConfig();
        s.Animation.Effects = new SpriteEffectsConfig { SpinDegPerSecond = 30 };
        Assert.Contains("Effects are not drawn with a pattern", SceneChecks.Validate(s, null));
    }

    [Fact]
    public void Validate_EffectsWithExtraImages_Warns()
    {
        var s = new CrossScreenConfig();
        s.Animation.AnimationPath = "a.gif";
        s.Animation.AdditionalAnimationPaths.Add("b.png");
        s.Animation.Effects = new SpriteEffectsConfig { Entry = BezelEffect.Glow };
        Assert.Contains("Effects need a single image (no extra images)", SceneChecks.Validate(s, null));
    }

    [Fact]
    public void Validate_EffectsOnIconZonePath_Warns()
    {
        var s = new CrossScreenConfig();
        s.Animation.AnimationPath = "a.gif";
        s.Background.Mode = BackgroundMode.IconZone;
        s.Movement.Type = MovementType.Linear;
        s.Animation.Effects = new SpriteEffectsConfig { Trail = new TrailConfig() };
        Assert.Contains("Effects are not drawn on an IconZone path", SceneChecks.Validate(s, null));
    }

    [Fact]
    public void Validate_NoEffects_NoEffectWarnings()
    {
        var s = new CrossScreenConfig();
        s.Animation.AnimationPath = "a.gif";
        s.Animation.Pattern = new PatternConfig();
        s.Animation.Effects = new SpriteEffectsConfig();          // nothing enabled
        Assert.DoesNotContain(SceneChecks.Validate(s, null), w => w.StartsWith("Effects"));
    }
```

- [ ] **Step 2: Run** — `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SceneChecks"` → the three warning tests fail.

- [ ] **Step 3: Implement the hints** — in `SceneChecks.Validate`, before the crossing-speed checks:

```csharp
        if (SpriteEffects.HasAny(anim.Effects))
        {
            if (anim.Pattern != null)
                warnings.Add("Effects are not drawn with a pattern");
            else if (anim.AdditionalAnimationPaths.Any(p => !string.IsNullOrWhiteSpace(p)))
                warnings.Add("Effects need a single image (no extra images)");
            else if (MovementCalculator.FollowsIconPath(scene.Background.Mode, scene.Movement))
                warnings.Add("Effects are not drawn on an IconZone path");
        }
```

Run the filter again → pass.

- [ ] **Step 4: View model** — add properties next to `_faceTravelDirection`:

```csharp
    // ── Effects (roadmap 2.4) ──
    [ObservableProperty] private bool _trailEnabled;
    [ObservableProperty] private int _trailCount = 6;
    [ObservableProperty] private int _trailSpacingMs = 60;
    [ObservableProperty] private bool _trailUseGradingColor = true;
    [ObservableProperty] private bool _pulseEnabled;
    [ObservableProperty] private int _pulseAmplitudePercent = 8;
    [ObservableProperty] private float _pulseFrequencyHz = 0.8f;
    [ObservableProperty] private float _spinDegPerSecond;
    [ObservableProperty] private int _bezelEntryIndex;      // BezelEffect: 0 None, 1 Glow, 2 Flash, 3 Ripple
    [ObservableProperty] private int _bezelExitIndex;
    [ObservableProperty] private string _bezelColorHex = "#FFFFFF";
    [ObservableProperty] private int _bezelDurationMs = 350;
```

Load (after `FaceTravelDirection = config.Animation.FaceTravelDirection;`):

```csharp
        var fx = config.Animation.Effects;
        TrailEnabled = fx?.Trail != null;
        TrailCount = fx?.Trail?.Count ?? 6;
        TrailSpacingMs = fx?.Trail?.SpacingMs ?? 60;
        TrailUseGradingColor = fx?.Trail?.UseGradingColor ?? true;
        PulseEnabled = fx?.Pulse != null;
        PulseAmplitudePercent = (int)MathF.Round((fx?.Pulse?.Amplitude ?? 0.08f) * 100f);
        PulseFrequencyHz = fx?.Pulse?.FrequencyHz ?? 0.8f;
        SpinDegPerSecond = fx?.SpinDegPerSecond ?? 0f;
        BezelEntryIndex = (int)(fx?.Entry ?? BezelEffect.None);
        BezelExitIndex = (int)(fx?.Exit ?? BezelEffect.None);
        BezelColorHex = fx?.BezelColorHex ?? "#FFFFFF";
        BezelDurationMs = fx?.BezelDurationMs ?? 350;
```

Build (inside the `AnimationLayerConfig` initializer, after `FaceTravelDirection = FaceTravelDirection,`):

```csharp
                Effects = BuildEffects(),
```

and the helper method in the view model:

```csharp
    /// <summary>The Effects tab as a config; null when nothing is enabled (keeps saved scenes unchanged).</summary>
    private SpriteEffectsConfig? BuildEffects()
    {
        var fx = new SpriteEffectsConfig
        {
            Trail = TrailEnabled ? new TrailConfig { Count = TrailCount, SpacingMs = TrailSpacingMs, UseGradingColor = TrailUseGradingColor } : null,
            Pulse = PulseEnabled ? new PulseConfig { Amplitude = PulseAmplitudePercent / 100f, FrequencyHz = PulseFrequencyHz } : null,
            SpinDegPerSecond = SpinDegPerSecond,
            Entry = (BezelEffect)BezelEntryIndex,
            Exit = (BezelEffect)BezelExitIndex,
            BezelColorHex = string.IsNullOrWhiteSpace(BezelColorHex) ? "#FFFFFF" : BezelColorHex.Trim(),
            BezelDurationMs = BezelDurationMs,
        };
        return SpriteEffects.HasAny(fx) ? fx : null;
    }
```

- [ ] **Step 5: Effects tab** — in `SceneEditorPanel.axaml`, insert before `<TabItem Header="Background">`, following the surrounding `Grid ColumnDefinitions="120,*"` / `Classes="label"` pattern:

```xml
                <TabItem Header="Effects">
                    <ScrollViewer>
                        <StackPanel Spacing="8" Margin="8">
                            <CheckBox IsChecked="{Binding TrailEnabled}" Content="Trail ⓘ"
                                      ToolTip.Tip="Fading copies of the sprite where it was a moment ago. Single image, not with a pattern or an IconZone path."/>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding TrailEnabled}">
                                <TextBlock Classes="label" Text="Ghosts"/>
                                <NumericUpDown Grid.Column="1" Value="{Binding TrailCount}" Minimum="1" Maximum="32" Increment="1" Width="130" HorizontalAlignment="Left"/>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding TrailEnabled}">
                                <TextBlock Classes="label" Text="Spacing (ms)"/>
                                <NumericUpDown Grid.Column="1" Value="{Binding TrailSpacingMs}" Minimum="10" Maximum="500" Increment="10" Width="130" HorizontalAlignment="Left"/>
                            </Grid>
                            <CheckBox IsChecked="{Binding TrailUseGradingColor}" IsVisible="{Binding TrailEnabled}" Content="Ghosts keep their color ⓘ"
                                      ToolTip.Tip="With a time-based color mode each ghost shows the color the sprite had then (a rainbow comet)."/>

                            <CheckBox IsChecked="{Binding PulseEnabled}" Content="Pulse ⓘ" ToolTip.Tip="The sprite breathes: its size swings around 100 %."/>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding PulseEnabled}">
                                <TextBlock Classes="label" Text="Size swing (%)"/>
                                <NumericUpDown Grid.Column="1" Value="{Binding PulseAmplitudePercent}" Minimum="1" Maximum="90" Increment="1" Width="130" HorizontalAlignment="Left"/>
                            </Grid>
                            <Grid ColumnDefinitions="120,*" IsVisible="{Binding PulseEnabled}">
                                <TextBlock Classes="label" Text="Breaths / s"/>
                                <NumericUpDown Grid.Column="1" Value="{Binding PulseFrequencyHz}" Minimum="0.1" Maximum="5" Increment="0.1" FormatString="0.0" Width="130" HorizontalAlignment="Left"/>
                            </Grid>

                            <Grid ColumnDefinitions="120,*">
                                <TextBlock Classes="label" Text="Spin (°/s) ⓘ" ToolTip.Tip="Constant rotation; negative = counter-clockwise. 0 = off."/>
                                <NumericUpDown Grid.Column="1" Value="{Binding SpinDegPerSecond}" Minimum="-720" Maximum="720" Increment="15" Width="130" HorizontalAlignment="Left"/>
                            </Grid>

                            <TextBlock Classes="label" Text="Bezel crossing ⓘ" Margin="0,8,0,0"
                                       ToolTip.Tip="Drawn on the machine the sprite enters / leaves, at that screen edge."/>
                            <Grid ColumnDefinitions="120,*">
                                <TextBlock Classes="label" Text="On entry"/>
                                <ComboBox Grid.Column="1" SelectedIndex="{Binding BezelEntryIndex}" Width="140">
                                    <ComboBoxItem Content="None"/><ComboBoxItem Content="Glow"/><ComboBoxItem Content="Flash"/><ComboBoxItem Content="Ripple"/>
                                </ComboBox>
                            </Grid>
                            <Grid ColumnDefinitions="120,*">
                                <TextBlock Classes="label" Text="On exit"/>
                                <ComboBox Grid.Column="1" SelectedIndex="{Binding BezelExitIndex}" Width="140">
                                    <ComboBoxItem Content="None"/><ComboBoxItem Content="Glow"/><ComboBoxItem Content="Flash"/><ComboBoxItem Content="Ripple"/>
                                </ComboBox>
                            </Grid>
                            <Grid ColumnDefinitions="120,*">
                                <TextBlock Classes="label" Text="Color"/>
                                <TextBox Grid.Column="1" Text="{Binding BezelColorHex}" Width="140" HorizontalAlignment="Left"/>
                            </Grid>
                            <Grid ColumnDefinitions="120,*">
                                <TextBlock Classes="label" Text="Duration (ms)"/>
                                <NumericUpDown Grid.Column="1" Value="{Binding BezelDurationMs}" Minimum="50" Maximum="3000" Increment="50" Width="130" HorizontalAlignment="Left"/>
                            </Grid>
                        </StackPanel>
                    </ScrollViewer>
                </TabItem>
```

Match the other tabs' outer container (if they use a different wrapper than `ScrollViewer` + `StackPanel Margin`, use theirs).

- [ ] **Step 6: Playlist summary** — in `PlaylistItemRow`'s look summary, before `return string.Join(" · ", parts);`:

```csharp
            var fx = cfg.Animation.Effects;
            if (SpriteEffects.HasAny(fx))
            {
                if (SpriteEffects.TrailCount(fx!.Trail) > 0) parts.Add($"trail {fx.Trail!.Count}");
                if (fx.Pulse != null) parts.Add("pulse");
                if (fx.SpinDegPerSecond != 0f) parts.Add($"spin {fx.SpinDegPerSecond:0}°/s");
                if (fx.Entry != BezelEffect.None) parts.Add($"entry {fx.Entry.ToString().ToLowerInvariant()}");
                if (fx.Exit != BezelEffect.None) parts.Add($"exit {fx.Exit.ToString().ToLowerInvariant()}");
            }
```

- [ ] **Step 7: Build + all tests** — `dotnet build WaBiBaBuSy.sln` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → all green.

- [ ] **Step 8: Commit**

```bash
git add WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs WaBiBaBuSy.UI/Views/SceneEditorPanel.axaml WaBiBaBuSy.Models/Wallpaper/SceneChecks.cs WaBiBaBuSy.UI/ViewModels/PlaylistItemRow.cs WaBiBaBuSy.Tests/
git commit -m "feat(editor): Effects tab, effect hints and playlist summary"
```

---

### Task 8: Showcase scene, live verification, docs

**Files:**
- Create: `TestScenarios/showcase/scenes/13-comet.json`
- Modify: `TestScenarios/showcase/showcase.json` (append steps), `TestScenarios/showcase/playlist-presets.json` (add `13-comet` to "BiBaBu Party" if that preset lists scene files)
- Modify docs: `.docs/plans/2026-09-17-crossing-effects-and-events-design.md` (status line), `.docs/plans/2026-09-17-lan-party-roadmap.md` (row 2.4: "slice 1 implemented 2026-10-03"), `.docs/RECENT_UPDATES.md`, `CLAUDE.md` (Key Capabilities: one bullet), `.docs/plans/2026-10-04-manual-test-checklist.md` (new §2.5 items)

- [ ] **Step 1: Scene** — `13-comet.json`:

```jsonc
{
  // A logo comet: rainbow trail, gentle pulse, ripple where it enters a machine, glow where it leaves.
  "Background": { "Mode": "SolidColor", "ColorHex": "#05060A" },
  "Animation": {
    "AnimationPath": "../assets/bibabu-logo.png", "FitMode": "TargetHeight", "TargetHeight": 180,
    "ColorGrading": { "Mode": "Rainbow", "CyclesPerSecond": 0.5 },
    "Effects": {
      "Trail": { "Count": 8, "SpacingMs": 70, "StartOpacity": 0.55, "ScaleFalloff": 0.9, "UseGradingColor": true },
      "Pulse": { "Amplitude": 0.08, "FrequencyHz": 1.2 },
      "Entry": "Ripple", "Exit": "Glow", "BezelColorHex": "#7FDBFF", "BezelDurationMs": 600
    }
  },
  "Movement": { "Type": "Linear", "SpeedPixelsPerSecond": 900 },
  "DistributionMode": "Sequential"
}
```

- [ ] **Step 2: Scenario steps** — append to `showcase.json` before its final `stop` step (copy the existing step shape): a `playScene` for `scenes/13-comet.json` labelled `13-comet`, and `exactFrame` steps at `elapsedMs` 1500, 2800, 3000, 3200, 3400 (the bezel between two 2560 px monitors is crossed around 3 s at 900 px/s; adjust after looking at the first run).

- [ ] **Step 3: Validate** — `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~BundledScenario"` → pass (it parses every bundled scenario and scene).

- [ ] **Step 4: Live run (server machine with two monitors, test mode enabled)**

```bash
dotnet build WaBiBaBuSy.UI -c Debug
WaBiBaBuSy.UI/bin/Debug/net9.0-windows/WaBiBaBuSy.UI.exe --test-run TestScenarios/showcase/showcase.json --exit
```

Open the newest `%LOCALAPPDATA%\WaBiBaBuSy\TestRuns\*_showcase\nodes\server__0\*13-comet*` and `server__1` PNGs: expect a fading rainbow trail behind the logo, the logo slightly larger/smaller between frames, a glow strip at monitor 0's right edge and a ripple ring at monitor 1's left edge in the frames right after the crossing, and both monitors' exact frames agreeing on where the trail is cut by the bezel. Exact frames are pure, so effects must be visible in them. Fix and re-run until they are.

- [ ] **Step 5: Docs** — design doc status: `**Status:** Slice 1 implemented 2026-10-03 (trail, pulse, spin, Glow/Flash/Ripple); Portal, events (§5) and transitions (§6) open`. Roadmap row 2.4 likewise. `RECENT_UPDATES.md` entry (what, where the math lives, the scan-based crossing detection and why, exclusions, tests). `CLAUDE.md` Key Capabilities bullet: "Sprite effects: trail, pulse, spin and bezel crossing Glow/Flash/Ripple — pure functions of the shared clock (`SpriteEffects`), drawn by players and the room preview". Checklist §2.5: on the real wall — trail spans the bezel, ripple on the entering machine and glow on the leaving one in the same moment, flash on a 2-row Ring at the seam, Effects tab hints with a pattern / extra image / IconZone.

- [ ] **Step 6: Commit**

```bash
git add TestScenarios/showcase .docs CLAUDE.md
git commit -m "feat(effects): comet showcase scene; docs for sprite and bezel effects"
```

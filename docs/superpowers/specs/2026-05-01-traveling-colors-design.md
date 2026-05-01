# Traveling Colors — Design Spec
**Date:** 2026-05-01  
**Status:** Approved  

## Overview

Add three new color grading modes where each pattern cell is assigned a **fixed color derived from its stable logical grid identity `(LogicalI, LogicalJ)`**. Unlike existing modes (which apply one color matrix per frame to all cells uniformly), Traveling Colors computes a unique, persistent color per cell that stays with it as it moves across the screen.

## New Enum Values

Three entries added to `ColorGradingMode` in `WaBiBaBuSy.Models/Wallpaper/ColorGradingConfig.cs`:

| Enum value | Display name | Color source |
|---|---|---|
| `TravelingRainbow` | Traveling Colors (Rainbow) | Full hue spectrum, derived from `Hash3(seed, i, j)` |
| `TravelingList` | Traveling Colors (List) | `ColorList[Hash3(seed, i, j) % count]` |
| `TravelingRandom` | Traveling Colors (Random) | `ColorList[Hash3(seed, i, j) % count]`, scattered |

`TravelingList` and `TravelingRandom` are algorithmically identical — both use `Hash3` to pick from `ColorList`. They are separate enum values for UI clarity, mirroring the existing `CycleColorList` / `RandomColors` distinction. No new config fields are needed; all three reuse existing `Seed` and `ColorList` properties.

## ColorGrader Changes

**File:** `WaBiBaBuSy.Models/Wallpaper/ColorGrader.cs`

Add one new public static method:

```csharp
public static ColorMatrix5x4 ComputeForCell(ColorGradingConfig config, int logicalI, int logicalJ)
```

Per-mode logic:

- **TravelingRainbow** — `hue = (int)((uint)Hash3(config.Seed, logicalI, logicalJ) % 360u)` → `HsvToRgb(hue, 1.0, 1.0)` → `TintMatrix(r, g, b)`
- **TravelingList / TravelingRandom** — `idx = (int)((uint)Hash3(config.Seed, logicalI, logicalJ) % (uint)colorCount)` → parse `ColorList[idx]` hex → `TintMatrix(r, g, b)`
- **All other modes** — fall back to `Compute(config, elapsedMs: 0)` so callers need no extra branching

`Hash3` lives in `PatternLayout.cs` as `private static`. It must be changed to `internal static` so `ColorGrader` (same assembly, `WaBiBaBuSy.Models`) can call `PatternLayout.Hash3(...)`.

## Program.cs Changes

**File:** `WaBiBaBuSy.Player.D2D/Program.cs` — `DrawAnimationLayer()`

Two changes inside the pattern cell draw loop:

1. **Detect Traveling mode** before the loop:
   ```csharp
   bool isTraveling = config.ColorGrading.Mode is
       ColorGradingMode.TravelingRainbow or
       ColorGradingMode.TravelingList or
       ColorGradingMode.TravelingRandom;
   ```

2. **Per-cell matrix injection** inside the loop, when `isTraveling`:
   ```csharp
   var cellMatrix = ColorGrader.ComputeForCell(config.ColorGrading, cell.LogicalI, cell.LogicalJ);
   SetColorMatrixOnEffect(cellMatrix);
   ```
   Then call `DrawCellWithOptionalGrading()` as usual.

`DrawCellWithOptionalGrading` itself is **unchanged** — it applies whatever matrix was last pushed to the D2D effect.

When not in a Traveling mode, the code path is identical to today (single shared matrix computed once per frame).

## UI Changes

**File:** Color mode dropdown / `ColorGradingMode` display name mapping (UI project)

- Add three entries in the same order as their enum values
- `TravelingList` and `TravelingRandom` share the existing `ColorList` editor UI — show it whenever `Mode` is `CycleColorList`, `RandomColors`, `TravelingList`, or `TravelingRandom`

## Performance

Per-cell matrix computation is trivial: one `Hash3` call (three integer multiplications) + HSV→RGB (a few float ops). At 100 visible cells the overhead is negligible. No allocations — `ColorMatrix5x4` is a value type. The D2D effect is reused across all cells as today.

## Non-Goals

- No animation or transition of the cell color over time — colors are static for the lifetime of each cell
- No per-cell color editor in the UI — colors are fully derived from the hash

# Traveling Colors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add three new `ColorGradingMode` values where each pattern cell gets a fixed color derived from its stable `(LogicalI, LogicalJ)` grid identity, so colors travel with elements across the screen instead of cycling uniformly.

**Architecture:** `ColorGrader.ComputeForCell(config, i, j)` computes a per-cell `ColorMatrix5x4` from a hash of the cell's logical coordinates. In `DrawAnimationLayer`, when a Traveling mode is active, the per-cell matrix is pushed to the D2D `ColorMatrix` effect before each cell is drawn. All other code paths are unchanged.

**Tech Stack:** C# 12 / .NET 8, Direct2D via Vortice.Windows, Avalonia UI (axaml)

---

## File Map

| File | Change |
|---|---|
| `WaBiBaBuSy.Models/Wallpaper/ColorGradingConfig.cs` | Add 3 new enum values + XML docs |
| `WaBiBaBuSy.Models/Wallpaper/PatternLayout.cs` | Change `Hash3` from `private` to `internal` |
| `WaBiBaBuSy.Models/Wallpaper/ColorGrader.cs` | Add `ComputeForCell` method; handle new modes in `Compute` |
| `WaBiBaBuSy.Player.D2D/Program.cs` | Detect traveling mode; push per-cell matrix inside cell loop |
| `WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs` | Update index comment; extend `IsColorGradingColorListMode`; add `IsColorGradingTimeBased` |
| `WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml` | Add 3 `ComboBoxItem` entries; bind `CyclesPerSecond` row to `IsColorGradingTimeBased` |

---

## Task 1: Add enum values to `ColorGradingMode`

**Files:**
- Modify: `WaBiBaBuSy.Models/Wallpaper/ColorGradingConfig.cs`

- [ ] **Step 1: Add three new enum values**

Replace the enum body in `ColorGradingConfig.cs` (currently ends at line 26) with:

```csharp
public enum ColorGradingMode
{
    /// <summary>No color grading — direct DrawBitmap, zero overhead.</summary>
    None,

    /// <summary>Cycle continuously through the full hue spectrum at <see cref="ColorGradingConfig.CyclesPerSecond"/>.</summary>
    Rainbow,

    /// <summary>Pick a color from <see cref="ColorGradingConfig.ColorList"/> at random (deterministic) every step.</summary>
    RandomColors,

    /// <summary>Ping-pong gradient between <see cref="ColorGradingConfig.GradientA"/> and <see cref="ColorGradingConfig.GradientB"/>.</summary>
    Gradient,

    /// <summary>Step through <see cref="ColorGradingConfig.ColorList"/> in order, looping.</summary>
    CycleColorList,

    /// <summary>Each pattern cell gets a fixed hue derived from its grid identity (LogicalI, LogicalJ).
    /// Colors travel with the cell as it moves — full hue spectrum distributed across cells.</summary>
    TravelingRainbow,

    /// <summary>Each pattern cell gets a fixed color from <see cref="ColorGradingConfig.ColorList"/>,
    /// chosen by hashing its grid identity. Colors travel with the cell as it moves.</summary>
    TravelingList,

    /// <summary>Each pattern cell gets a fixed color from <see cref="ColorGradingConfig.ColorList"/>,
    /// chosen by hashing its grid identity (scattered distribution). Colors travel with the cell as it moves.</summary>
    TravelingRandom
}
```

- [ ] **Step 2: Build to verify no errors**

```
dotnet build WaBiBaBuSy.Models
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/ColorGradingConfig.cs
git commit -m "feat: add TravelingRainbow, TravelingList, TravelingRandom enum values"
```

---

## Task 2: Expose `Hash3` as `internal`

**Files:**
- Modify: `WaBiBaBuSy.Models/Wallpaper/PatternLayout.cs:145`

- [ ] **Step 1: Change access modifier**

At line 145 in `PatternLayout.cs`, change:

```csharp
private static int Hash3(int seed, int i, int j)
```

to:

```csharp
internal static int Hash3(int seed, int i, int j)
```

- [ ] **Step 2: Build to verify**

```
dotnet build WaBiBaBuSy.Models
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/PatternLayout.cs
git commit -m "refactor: expose PatternLayout.Hash3 as internal for ColorGrader access"
```

---

## Task 3: Add `ComputeForCell` to `ColorGrader`

**Files:**
- Modify: `WaBiBaBuSy.Models/Wallpaper/ColorGrader.cs`

- [ ] **Step 1: Add the method after the existing `Compute` method (after line 105)**

Insert the following between the closing brace of `Compute` and the `TintMatrix` method:

```csharp
/// <summary>
/// Compute the color matrix for a single pattern cell identified by its logical grid coordinates.
/// Called once per cell when a Traveling color mode is active. Returns a deterministic, time-independent
/// matrix — the same (i, j) always yields the same color regardless of elapsed time.
/// </summary>
public static ColorMatrix5x4 ComputeForCell(ColorGradingConfig config, int logicalI, int logicalJ)
{
    if (config == null) return ColorMatrix5x4.Identity;

    switch (config.Mode)
    {
        case ColorGradingMode.TravelingRainbow:
        {
            int rawHash = PatternLayout.Hash3(config.Seed, logicalI, logicalJ);
            double hue = (uint)rawHash % 360u;
            var (r, g, b) = HsvToRgb(hue, 1.0, 1.0);
            return TintMatrix(r, g, b);
        }

        case ColorGradingMode.TravelingList:
        case ColorGradingMode.TravelingRandom:
        {
            if (config.ColorList == null || config.ColorList.Count == 0)
                return ColorMatrix5x4.Identity;
            int rawHash = PatternLayout.Hash3(config.Seed, logicalI, logicalJ);
            int idx = (int)((uint)rawHash % (uint)config.ColorList.Count);
            var (r, g, b) = HexToRgb(config.ColorList[idx]);
            return TintMatrix(r, g, b);
        }

        default:
            return ColorMatrix5x4.Identity;
    }
}
```

- [ ] **Step 2: Verify `TintMatrix` and `HsvToRgb` are accessible**

Both are `private static` in `ColorGrader` — `ComputeForCell` is in the same class, so they're accessible. No change needed.

- [ ] **Step 3: Add `using WaBiBaBuSy.Models.Wallpaper;` if not already present**

`PatternLayout` is in the same namespace (`WaBiBaBuSy.Models.Wallpaper`) as `ColorGrader`, so no `using` is needed.

- [ ] **Step 4: Build to verify**

```
dotnet build WaBiBaBuSy.Models
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Quick sanity check — verify hue spread**

Open a scratch C# expression or just reason through it:
- `Hash3(seed:1, i:0, j:0) % 360` → some hue in [0, 360)
- `Hash3(seed:1, i:1, j:0) % 360` → a different hue
- Two adjacent cells should get visually different hues (hash avalanche effect guarantees this).

- [ ] **Step 6: Commit**

```bash
git add WaBiBaBuSy.Models/Wallpaper/ColorGrader.cs
git commit -m "feat: add ColorGrader.ComputeForCell for per-cell traveling color modes"
```

---

## Task 4: Apply per-cell matrix in `DrawAnimationLayer`

**Files:**
- Modify: `WaBiBaBuSy.Player.D2D/Program.cs:2763-2830`

- [ ] **Step 1: Detect traveling mode before the cell loop**

In `DrawAnimationLayer`, replace the block at lines 2763–2771:

```csharp
// Color grading matrix (computed once per frame — same for every cell).
var grading = ColorGrader.Compute(_animationConfig.ColorGrading, elapsedMs);
bool gradingActive = _animationConfig.ColorGrading != null
                     && _animationConfig.ColorGrading.Mode != ColorGradingMode.None;
if (gradingActive)
{
    EnsureColorMatrixEffect();
    SetColorMatrixOnEffect(grading);
}
```

with:

```csharp
// Color grading: compute one matrix per frame for time-based modes; per-cell for Traveling modes.
bool gradingActive = _animationConfig.ColorGrading != null
                     && _animationConfig.ColorGrading.Mode != ColorGradingMode.None;
bool isTraveling = _animationConfig.ColorGrading != null &&
                   (_animationConfig.ColorGrading.Mode == ColorGradingMode.TravelingRainbow ||
                    _animationConfig.ColorGrading.Mode == ColorGradingMode.TravelingList ||
                    _animationConfig.ColorGrading.Mode == ColorGradingMode.TravelingRandom);
if (gradingActive)
{
    EnsureColorMatrixEffect();
    if (!isTraveling)
    {
        var grading = ColorGrader.Compute(_animationConfig.ColorGrading, elapsedMs);
        SetColorMatrixOnEffect(grading);
    }
}
```

- [ ] **Step 2: Inject per-cell matrix inside the pattern cell loop**

In the `foreach (var cell in cells)` loop (around line 2814), add the per-cell matrix call just before `DrawCellWithOptionalGrading`. Replace:

```csharp
DrawCellWithOptionalGrading(bmp, cell.ScreenX, cell.ScreenY, drawW, drawH, cell.RotationDeg, gradingActive, alpha);
```

with:

```csharp
if (isTraveling && gradingActive)
{
    var cellMatrix = ColorGrader.ComputeForCell(_animationConfig.ColorGrading, cell.LogicalI, cell.LogicalJ);
    SetColorMatrixOnEffect(cellMatrix);
}
DrawCellWithOptionalGrading(bmp, cell.ScreenX, cell.ScreenY, drawW, drawH, cell.RotationDeg, gradingActive, alpha);
```

- [ ] **Step 3: Build to verify**

```
dotnet build WaBiBaBuSy.Player.D2D
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Manual smoke test**

Run the full solution, open the CrossScreen config dialog, set mode to any existing mode (Rainbow) and confirm it still works. Then proceed to Task 5 (UI) before testing the new modes end-to-end.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Player.D2D/Program.cs
git commit -m "feat: apply per-cell color matrix in DrawAnimationLayer for Traveling modes"
```

---

## Task 5: Update ViewModel

**Files:**
- Modify: `WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs:156-173`

- [ ] **Step 1: Update the index comment and properties**

Replace lines 156–173 in `CrossScreenConfigViewModel.cs`:

```csharp
// ── Color grading (F2) ───────────────────────────────────────────────────
// Index maps to ColorGradingMode: 0=None,1=Rainbow,2=RandomColors,3=Gradient,4=CycleColorList
[ObservableProperty] private int _colorGradingModeIndex = 0;
[ObservableProperty] private double _colorGradingCyclesPerSecond = 0.1;
[ObservableProperty] private string _colorGradingGradientA = "#FF0000";
[ObservableProperty] private string _colorGradingGradientB = "#0000FF";
[ObservableProperty] private int _colorGradingSeed = 1;
[ObservableProperty] private string _colorGradingColorListCsv = "#FF0000,#FFFF00,#00FF00,#00FFFF,#0000FF,#FF00FF";

public bool IsColorGradingNone           => ColorGradingModeIndex == 0;
public bool IsColorGradingGradient       => ColorGradingModeIndex == 3;
public bool IsColorGradingColorListMode  => ColorGradingModeIndex == 2 || ColorGradingModeIndex == 4;

partial void OnColorGradingModeIndexChanged(int value)
{
    OnPropertyChanged(nameof(IsColorGradingNone));
    OnPropertyChanged(nameof(IsColorGradingGradient));
    OnPropertyChanged(nameof(IsColorGradingColorListMode));
}
```

with:

```csharp
// ── Color grading (F2) ───────────────────────────────────────────────────
// Index maps to ColorGradingMode: 0=None,1=Rainbow,2=RandomColors,3=Gradient,4=CycleColorList,
//   5=TravelingRainbow, 6=TravelingList, 7=TravelingRandom
[ObservableProperty] private int _colorGradingModeIndex = 0;
[ObservableProperty] private double _colorGradingCyclesPerSecond = 0.1;
[ObservableProperty] private string _colorGradingGradientA = "#FF0000";
[ObservableProperty] private string _colorGradingGradientB = "#0000FF";
[ObservableProperty] private int _colorGradingSeed = 1;
[ObservableProperty] private string _colorGradingColorListCsv = "#FF0000,#FFFF00,#00FF00,#00FFFF,#0000FF,#FF00FF";

public bool IsColorGradingNone           => ColorGradingModeIndex == 0;
public bool IsColorGradingGradient       => ColorGradingModeIndex == 3;
public bool IsColorGradingColorListMode  => ColorGradingModeIndex == 2 || ColorGradingModeIndex == 4
                                         || ColorGradingModeIndex == 6 || ColorGradingModeIndex == 7;
// Traveling modes (5-7) are time-independent — hide the CyclesPerSecond control for them.
public bool IsColorGradingTimeBased      => ColorGradingModeIndex >= 1 && ColorGradingModeIndex <= 4;

partial void OnColorGradingModeIndexChanged(int value)
{
    OnPropertyChanged(nameof(IsColorGradingNone));
    OnPropertyChanged(nameof(IsColorGradingGradient));
    OnPropertyChanged(nameof(IsColorGradingColorListMode));
    OnPropertyChanged(nameof(IsColorGradingTimeBased));
}
```

- [ ] **Step 2: Build to verify**

```
dotnet build WaBiBaBuSy.UI
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs
git commit -m "feat: extend CrossScreenConfigViewModel for Traveling color modes"
```

---

## Task 6: Update AXAML dropdown

**Files:**
- Modify: `WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml:235-260`

- [ ] **Step 1: Add three new ComboBoxItems**

Replace the `<ComboBox>` block (lines 235–241) in `CrossScreenConfigDialog.axaml`:

```xml
<ComboBox Grid.Column="1" SelectedIndex="{Binding ColorGradingModeIndex}" Width="220" HorizontalAlignment="Left">
    <ComboBoxItem Content="None"/>
    <ComboBoxItem Content="Rainbow (tint sweep)"/>
    <ComboBoxItem Content="Random Colors"/>
    <ComboBoxItem Content="Gradient (ping-pong)"/>
    <ComboBoxItem Content="Cycle Color List"/>
</ComboBox>
```

with:

```xml
<ComboBox Grid.Column="1" SelectedIndex="{Binding ColorGradingModeIndex}" Width="220" HorizontalAlignment="Left">
    <ComboBoxItem Content="None"/>
    <ComboBoxItem Content="Rainbow (tint sweep)"/>
    <ComboBoxItem Content="Random Colors"/>
    <ComboBoxItem Content="Gradient (ping-pong)"/>
    <ComboBoxItem Content="Cycle Color List"/>
    <ComboBoxItem Content="Traveling Colors (Rainbow)"/>
    <ComboBoxItem Content="Traveling Colors (List)"/>
    <ComboBoxItem Content="Traveling Colors (Random)"/>
</ComboBox>
```

- [ ] **Step 2: Bind CyclesPerSecond row to `IsColorGradingTimeBased`**

Find the `Grid` that shows the Cycles per second control (line 243). Replace its `IsVisible` binding:

```xml
<Grid ColumnDefinitions="Auto,*" ColumnSpacing="10" IsVisible="{Binding !IsColorGradingNone}">
    <TextBlock Grid.Column="0" Text="Cycles per second:" VerticalAlignment="Center"/>
    <NumericUpDown Grid.Column="1" Value="{Binding ColorGradingCyclesPerSecond}" Minimum="0.01" Maximum="10" Increment="0.1" HorizontalAlignment="Left" Width="120"/>
</Grid>
```

with:

```xml
<Grid ColumnDefinitions="Auto,*" ColumnSpacing="10" IsVisible="{Binding IsColorGradingTimeBased}">
    <TextBlock Grid.Column="0" Text="Cycles per second:" VerticalAlignment="Center"/>
    <NumericUpDown Grid.Column="1" Value="{Binding ColorGradingCyclesPerSecond}" Minimum="0.01" Maximum="10" Increment="0.1" HorizontalAlignment="Left" Width="120"/>
</Grid>
```

- [ ] **Step 3: Build to verify**

```
dotnet build WaBiBaBuSy.UI
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml
git commit -m "feat: add Traveling Colors entries to color mode dropdown"
```

---

## Task 7: End-to-end test

- [ ] **Step 1: Run the full solution**

```
dotnet run --project WaBiBaBuSy.UI
```

- [ ] **Step 2: Open CrossScreen config → Color Grading section**

Verify the dropdown now shows:
- None
- Rainbow (tint sweep)
- Random Colors
- Gradient (ping-pong)
- Cycle Color List
- Traveling Colors (Rainbow)
- Traveling Colors (List)
- Traveling Colors (Random)

- [ ] **Step 3: Test Traveling Colors (Rainbow)**

Select a pattern with multiple logos. Choose "Traveling Colors (Rainbow)". Start the animation. Observe that:
- Each logo has a distinct fixed color (not all the same)
- Colors do **not** change over time
- Colors travel with each logo as it moves across the screen

- [ ] **Step 4: Test Traveling Colors (List)**

Enter a short `ColorList` (e.g. `#FF0000,#00FF00,#0000FF` — red, green, blue). Choose "Traveling Colors (List)". Observe that logos are colored red, green, or blue, fixed, traveling.

- [ ] **Step 5: Test Traveling Colors (Random)**

Same ColorList as above. Choose "Traveling Colors (Random)". Observe same behavior — colors are scattered across the grid rather than sequential.

- [ ] **Step 6: Regression — verify existing modes still work**

Switch back to Rainbow, Gradient, CycleColorList. Confirm they animate as before (colors cycle uniformly across all cells).

- [ ] **Step 7: Commit final**

```bash
git commit --allow-empty -m "test: Traveling Colors verified end-to-end"
```

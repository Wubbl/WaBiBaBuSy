# Plan: Corridor Animation System

**Created:** 2026-03-31
**Status:** Implementation in progress

---

## Context

The goal is to animate a sprite (e.g. a small animal) that runs across all monitor nodes without covering desktop icons. Users typically cluster icons in the screen borders (top/bottom), leaving a clear horizontal band in the middle.

**The user's key insight**: Instead of computing icon positions dynamically (complex), let the user manually define a three-zone background:
- **Top zone** (colored): icon area
- **Middle corridor** (darker): the animation path
- **Bottom zone** (colored): icon area

This gives a perfect visual test harness AND constrains where the animation travels, replacing the need for real-time icon avoidance in Phase 1.

This plan covers **Phase 1** (ThreeZone background + corridor-constrained animation) in full detail, and **Phase 2** (desktop icon detection + auto-corridor) at architecture level.

---

## Phase 1: ThreeZone Background + Corridor Animation

### Files to Modify

| File | Change |
|---|---|
| `WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs` | Add enum value + 5 fields to BackgroundLayerConfig |
| `WaBiBaBuSy.Player.D2D/Program.cs` | InitializeBackground, DrawBackground, CalculateAnimationLayout, UpdateAnimationPosition + state vars |
| `WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs` | 5 new observable properties + BuildConfig() |
| `WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml` | New ComboBox item + conditional panel |

---

### Step 1 — Model: `CrossScreenConfig.cs`

**Add `ThreeZone` to `BackgroundMode` enum** (after `TiledImage`):
```csharp
/// <summary>Top zone + middle corridor + bottom zone, each with distinct color.</summary>
ThreeZone
```

**Add 5 fields to `BackgroundLayerConfig`** (after `ImagePath`):
```csharp
/// <summary>ThreeZone: pixels from top where the corridor starts.</summary>
public int CorridorTopPx { get; set; } = 324;          // 30% of 1080p

/// <summary>ThreeZone: height of the corridor in pixels.</summary>
public int CorridorHeightPx { get; set; } = 432;        // 40% of 1080p

/// <summary>ThreeZone: color for the top zone (icon area).</summary>
public string TopZoneColorHex { get; set; } = "#1A3A5C";

/// <summary>ThreeZone: color for the bottom zone (icon area).</summary>
public string BottomZoneColorHex { get; set; } = "#3C1A5C";

/// <summary>ThreeZone: color for the middle corridor.</summary>
public string CorridorColorHex { get; set; } = "#1E1E1E";
```

---

### Step 2 — D2D Player: `Program.cs`

#### 2a. New state variables (near existing background state, ~line 192)
```csharp
// ThreeZone background brushes (pre-created, reused each frame)
private static ID2D1SolidColorBrush? _topZoneBrush;
private static ID2D1SolidColorBrush? _bottomZoneBrush;
private static ID2D1SolidColorBrush? _corridorBrush;

// Corridor constraint (derived from ThreeZone background)
private static int  _corridorTopPx;
private static int  _corridorHeightPx;
private static bool _hasCorridorConstraint;
```

#### 2b. `InitializeBackground()` — reset + ThreeZone case

At the **top** of `InitializeBackground()`, before the existing switch, reset corridor and dispose old brushes:
```csharp
// Reset corridor constraint and dispose old brushes
_hasCorridorConstraint = false;
_corridorTopPx = 0;
_corridorHeightPx = 0;
_topZoneBrush?.Dispose();   _topZoneBrush   = null;
_bottomZoneBrush?.Dispose(); _bottomZoneBrush = null;
_corridorBrush?.Dispose();  _corridorBrush   = null;
```

Add case to the existing switch:
```csharp
case BackgroundMode.ThreeZone:
    _topZoneBrush   = _d2dContext!.CreateSolidColorBrush(ParseHexColor(config.TopZoneColorHex));
    _bottomZoneBrush = _d2dContext.CreateSolidColorBrush(ParseHexColor(config.BottomZoneColorHex));
    _corridorBrush  = _d2dContext.CreateSolidColorBrush(ParseHexColor(config.CorridorColorHex));
    _corridorTopPx    = config.CorridorTopPx;
    _corridorHeightPx = config.CorridorHeightPx;
    _hasCorridorConstraint = true;
    _logger?.LogInformation("[BG-D2D] ThreeZone: corridorTop={T}px height={H}px",
        _corridorTopPx, _corridorHeightPx);
    break;
```

Also add brush disposal to the program exit/cleanup path (wherever `_backgroundImageBitmap?.Dispose()` is called at shutdown).

#### 2c. `DrawBackground()` — add ThreeZone case
```csharp
case BackgroundMode.ThreeZone:
    // Top zone
    if (_topZoneBrush != null && _corridorTopPx > 0)
        _d2dContext.FillRectangle(new RectangleF(0, 0, _width, _corridorTopPx), _topZoneBrush);
    // Corridor
    if (_corridorBrush != null)
        _d2dContext.FillRectangle(new RectangleF(0, _corridorTopPx, _width, _corridorHeightPx), _corridorBrush);
    // Bottom zone
    int bottomY = _corridorTopPx + _corridorHeightPx;
    if (_bottomZoneBrush != null && bottomY < _height)
        _d2dContext.FillRectangle(new RectangleF(0, bottomY, _width, _height - bottomY), _bottomZoneBrush);
    break;
```

#### 2d. `CalculateAnimationLayout()` — center Y within corridor when active

Replace the `VerticalAlign` switch block with a corridor-aware block:
```csharp
// Vertical placement
if (_hasCorridorConstraint && _corridorHeightPx > 0)
{
    // Center within the corridor
    _animY = _corridorTopPx + (_corridorHeightPx - _animHeight) / 2f;
    _animY = MathF.Max(_corridorTopPx, _animY);
}
else
{
    // Existing VerticalAlign switch (unchanged)
    _animY = config.VerticalAlign switch { ... };
}
```

#### 2e. `UpdateAnimationPosition()` — clamp Y to corridor

After the line `_animY = vy;` (line ~1326), add:
```csharp
if (_hasCorridorConstraint && _corridorHeightPx > 0)
{
    float minY = _corridorTopPx;
    float maxY = _corridorTopPx + _corridorHeightPx - _animHeight;
    if (maxY < minY) maxY = minY; // animation taller than corridor: pin to top
    _animY = Math.Clamp(_animY, minY, maxY);
}
```

---

### Step 3 — ViewModel: `CrossScreenConfigViewModel.cs`

**Add 5 observable properties:**
```csharp
[ObservableProperty] private int    _corridorTopPx       = 324;
[ObservableProperty] private int    _corridorHeightPx    = 432;
[ObservableProperty] private string _topZoneColorHex     = "#1A3A5C";
[ObservableProperty] private string _bottomZoneColorHex  = "#3C1A5C";
[ObservableProperty] private string _corridorColorHex    = "#1E1E1E";
```

**Add computed property** (alongside `IsMovementActive` etc.):
```csharp
public bool IsThreeZoneMode => BackgroundModeIndex == 3;
```

Trigger `OnPropertyChanged(nameof(IsThreeZoneMode))` from `OnBackgroundModeIndexChanged`.

**Update `BuildConfig()`** — in the background config section:
```csharp
Mode = (BackgroundMode)BackgroundModeIndex,
ColorHex = BackgroundColor,
ImagePath = BackgroundImagePath,
// ThreeZone fields (ignored when mode != ThreeZone)
CorridorTopPx      = CorridorTopPx,
CorridorHeightPx   = CorridorHeightPx,
TopZoneColorHex    = TopZoneColorHex,
BottomZoneColorHex = BottomZoneColorHex,
CorridorColorHex   = CorridorColorHex,
```

**Update `LoadConfig()`** — populate the new fields when loading an existing config.

---

### Step 4 — UI: `CrossScreenConfigDialog.axaml`

**Background mode ComboBox** — add item at index 3:
```xml
<ComboBoxItem>Three Zone (Corridor)</ComboBoxItem>
```

**Add ThreeZone config panel** (IsVisible bound to `IsThreeZoneMode`):
```
┌─ Three Zone Settings ────────────────────────────────────┐
│ Corridor Top (px):    [NumericUpDown min=0 max=2000 step=10] │
│ Corridor Height (px): [NumericUpDown min=50 max=2000 step=10]│
│ Top Zone Color:       [TextBox "#1A3A5C"] [■ preview]    │
│ Bottom Zone Color:    [TextBox "#3C1A5C"] [■ preview]    │
│ Corridor Color:       [TextBox "#1E1E1E"] [■ preview]    │
│                                                          │
│ ℹ Place icons in top/bottom zones. Animation travels    │
│   through the center corridor.                          │
└──────────────────────────────────────────────────────────┘
```

Color preview is a 16×16 `Border` whose `Background` updates when the TextBox changes. Same pattern as the existing `BackgroundColor` field auto-detect button.

---

## Phase 2: Desktop Icon Detection + Auto-Corridor (Architecture)

> Not implemented in this phase. Design for future reference.

### New Files

**`WaBiBaBuSy.Core/Services/Desktop/DesktopIconService.cs`**
- `GetIconPositions() → List<DesktopIconRect>`
- Win32: find `SysListView32` inside `Progman → SHELLDLL_DefView`
- Cross-process read via `VirtualAllocEx` + `LVM_GETITEMPOSITION` + `ReadProcessMemory`
- Returns positions in virtual desktop coords; transform to screen-local via `GetWindowRect`

**`WaBiBaBuSy.Models/Desktop/DesktopIconLayout.cs`**
- `record DesktopIconRect(string Name, int X, int Y, int W, int H)`
- `class DesktopIconLayout { string ClientId; List<DesktopIconRect> Icons; }`

**`WaBiBaBuSy.Server/Services/CorridorPlanner.cs`**
- Input: all clients' `DesktopIconLayout` + monitor heights
- Algorithm: scan horizontal bands (e.g. 50px steps), find the tallest contiguous band with no icon overlap
- Output: `CorridorTopPx` + `CorridorHeightPx` per monitor

### Flow
```
Client starts → DesktopIconService.GetIconPositions() → gRPC → Server
Server → CorridorPlanner.Compute() → BackgroundLayerConfig{ThreeZone}
Server → broadcasts updated BackgroundLayerConfig to all clients
Clients → InitializeBackground(new config) → animate within computed corridor
```

### Polling
- Client polls icon positions every 30 seconds
- Server recomputes + re-broadcasts if corridor changes significantly (>20px delta)

---

## Verification

1. `dotnet build` — zero errors
2. Open Animation Config dialog → Background Mode = "Three Zone"
3. Verify ThreeZone panel appears; set Corridor Top=300px, Height=480px
4. Set movement to **Bounce** (DirectionAngle=45°)
5. Apply → verify:
   - Top band in TopZoneColor, bottom band in BottomZoneColor, center band in CorridorColor
   - Animation bounces but never leaves the corridor vertically
6. Switch to **SineWave** → verify wave amplitude is clipped at corridor bounds
7. Switch to **RandomWalk** → verify random Y positions stay within corridor
8. **Multi-monitor (Sequential mode)**: same vertical zones appear on all monitors, animation enters/exits each monitor at the same corridor Y position
9. Switch background back to SolidColor → verify corridor constraint is cleared (animation uses VerticalAlign again)

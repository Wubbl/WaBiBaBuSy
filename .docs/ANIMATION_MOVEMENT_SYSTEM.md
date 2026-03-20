# Animation Movement System

**Status:** ✅ IMPLEMENTED (2026-03-19)
**Key files:** `MovementCalculator.cs`, `MovementConfig` in `CrossScreenConfig.cs`, `CrossScreenConfigDialog.axaml`

## Context
The animation system currently only supports left-to-right linear scrolling (`_animX = -animWidth + elapsed * pxPerSec`). The user has 2x1920x1080 monitors and wants a small (e.g. 200x200) animation to wander across screens with various movement patterns. Transparency is deferred. Background color from main window already works.

## Decisions
- **Transparency:** Skipped for now
- **Movement types:** All 6 (Static, Linear, Bounce, SineWave, Circular, RandomWalk)
- **UI location:** `CrossScreenConfigDialog.axaml` (multi-screen animation config dialog)

---

## Step 1: Add Movement Models
**File:** `WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs`

Add enums and config class:
```csharp
public enum MovementType { Static, Linear, Bounce, SineWave, Circular, RandomWalk }

public class MovementConfig
{
    public MovementType Type { get; set; } = MovementType.Static;
    public float SpeedPixelsPerSecond { get; set; } = 500f;
    // Linear: start/end virtual canvas coords (null = auto)
    public float? StartX, StartY, EndX, EndY;
    // Bounce: initial direction (degrees: 0=right, 90=down, 45=diagonal)
    public float DirectionAngleDegrees { get; set; } = 30f;
    // Circular: orbit center (null=canvas center) + radius
    public float? OrbitCenterX, OrbitCenterY;
    public float OrbitRadiusPixels { get; set; } = 500f;
    // SineWave: vertical oscillation
    public float WaveAmplitudePixels { get; set; } = 200f;
    public float WaveFrequencyHz { get; set; } = 0.5f;
    // RandomWalk: deterministic seed + step interval
    public int RandomSeed { get; set; } = 42;
    public float RandomStepIntervalMs { get; set; } = 1000f;
    public bool Loop { get; set; } = true;
}
```

Add property to `CrossScreenConfig`:
```csharp
public MovementConfig Movement { get; set; } = new();
```

## Step 2: Create MovementCalculator
**New file:** `WaBiBaBuSy.Models/Wallpaper/MovementCalculator.cs`

Pure static math class - maps `(elapsedMs, config, animSize, canvasSize)` -> `(float X, float Y)` in virtual canvas coordinates. Deterministic = all monitors compute same position independently.

| Type | Algorithm |
|------|-----------|
| **Static** | Centered: `(canvasW/2 - animW/2, canvasH/2 - animH/2)` |
| **Linear** | `start + normalize(end-start) * speed * elapsed`. Auto start=(-animW, canvasH/2), end=(canvasW, canvasH/2). Loop via modulo on total distance. |
| **Bounce** | Decompose speed into vx/vy via angle. For each axis: `period = 2*(extent-animSize)`, `pos = abs(distance % period - period/2)`. Infinite bouncing. |
| **SineWave** | X: linear left-to-right (wrapping). Y: `centerY + amplitude * sin(2*PI*freq*elapsed)` |
| **Circular** | `x = cx + r*cos(omega*t)`, `y = cy + r*sin(omega*t)`, `omega = speed/radius` |
| **RandomWalk** | `stepIndex = elapsed/stepInterval`, `Random(seed+stepIndex)` generates target point, lerp between consecutive targets |

## Step 3: Update IPC Messages
**File:** `WaBiBaBuSy.Player.Common/Messages/PlayerCommandLoadAnimation.cs`

Add:
```csharp
public MovementConfig? MovementConfig { get; set; }
public int VirtualCanvasWidth { get; set; } = 1920;
public int MonitorOffsetX { get; set; } = 0;
```

## Step 4: Update Player.D2D
**File:** `WaBiBaBuSy.Player.D2D/Program.cs`

- Add fields: `_movementConfig`, `_virtualCanvasWidth`, `_monitorOffsetX`
- `HandleLoadAnimationCommand()`: store new fields from JSON command
- Replace `UpdateAnimationPosition(elapsedMs)` with `MovementCalculator.Calculate()`
- Backward compat: if `_movementConfig == null && _pixelsPerSecond > 0`, use legacy linear formula

## Step 5: Update CrossScreenConfigDialog UI
**File:** `WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml`

Add a new "Movement" section (Border) between "Animation Speed" and "Target Monitors" with:
- ComboBox for movement type selection (6 types)
- Conditional panels for type-specific parameters (direction, wave, orbit)

## Step 6: Update CrossScreenConfigViewModel
**File:** `WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs`

Add observable properties for movement type, angle, wave params, orbit radius.
Update `BuildConfig()` and `LoadFromConfig()`.

## Step 7: Wire MovementConfig Through D2DCompositionService
**File:** `WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs`

When building `PlayerCommandLoadAnimation`, populate:
- `MovementConfig` from `CrossScreenConfig.Movement`
- `VirtualCanvasWidth` from `VirtualCanvasManager.TotalWidth`
- `MonitorOffsetX` from `ScreenMapping.VirtualBounds.X`

## Step 8: Update AnimationLayerRenderer (composition fallback)
**File:** `WaBiBaBuSy.WallpaperEngine/Composition/AnimationLayerRenderer.cs`

Update `UpdatePosition()` to use `MovementCalculator` instead of hardcoded horizontal scrolling.

---

## Key Design: Deterministic Pure Math
All movement is computed from elapsed time using pure math. No per-frame IPC between host and player. Each Player.D2D process computes the same virtual canvas position independently -> automatic multi-monitor sync, zero main-process CPU overhead.

## Critical Files
| File | Action |
|------|--------|
| `WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs` | Add MovementType, MovementConfig |
| `WaBiBaBuSy.Models/Wallpaper/MovementCalculator.cs` | **NEW** - shared movement math |
| `WaBiBaBuSy.Player.Common/Messages/PlayerCommandLoadAnimation.cs` | Add MovementConfig, VirtualCanvasWidth, MonitorOffsetX |
| `WaBiBaBuSy.Player.D2D/Program.cs` | Use MovementCalculator, store new fields |
| `WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml` | Add Movement section |
| `WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs` | Movement properties, BuildConfig, LoadFromConfig |
| `WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs` | Pass MovementConfig + canvas info to player |
| `WaBiBaBuSy.WallpaperEngine/Composition/AnimationLayerRenderer.cs` | Use MovementCalculator |

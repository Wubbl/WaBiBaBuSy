# IconZone Path Variation, RandomWalk Offset & Rotation Fixes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix rotation in sequential mode, add RandomWalk lateral variation in IconZone, and implement host-driven per-lap A* path refresh so the animation takes a different corridor each loop.

**Architecture:** Four independent but layered changes: (A) 1-line RotateWithPath clone fix, (B) copy the GIF rotation block to the video render path, (C) add a smooth random perpendicular offset for RandomWalk inside the existing IconZone path-following block, (D) a new signal/command pair — player emits `SIGNAL:LAP_COMPLETE:N` on stderr when `_traverseCount` increments in sequential mode; host recomputes a new global A* path with seed N and pushes `cmd_update_path` JSON to every player.

**Tech Stack:** C# 12, .NET 8/9, Direct2D (Vortice), Newtonsoft.Json for IPC, `Interlocked.Exchange` for lock-free pending-path swap, `System.Numerics.Matrix3x2` for D2D rotation transform.

---

## File Map

| File | Role |
|------|------|
| `WaBiBaBuSy.Player.Common/Messages/PlayerCommandUpdatePath.cs` | **New** — IPC message `cmd_update_path` |
| `WaBiBaBuSy.Player.D2D/Program.cs` | Changes B, C, D1, D2 — rotation + RandomWalk offset + signal + handler |
| `WaBiBaBuSy.WallpaperEngine/Direct2D/D2DPlayerHost.cs` | Change D3 — `LapCompleted` event + parse + `SendUpdatePathAsync` |
| `WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs` | Change D4 — `GlobalLapCompleted` event + deduplicate + `BroadcastNewPathAsync` |
| `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` | Changes A, D5 — clone fix + save params + subscribe + recompute |

---

## Task 1 — Fix `RotateWithPath` in sequential clone + video rotation

**Files:**
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` ~line 2792
- Modify: `WaBiBaBuSy.Player.D2D/Program.cs` ~line 1047

### Change A — RotateWithPath clone fix

- [ ] Open `MainWindowViewModel.cs`. Find the `AnimationLayerConfig` object initializer inside the `if (!perMonitor && _crossScreenConfig.Background.Mode == BackgroundMode.IconZone …)` block (~line 2792). It currently ends with `PrecomputedPath = globalLayout.Path`. Add one line:

```csharp
animationConfig = new AnimationLayerConfig
{
    AnimationPath         = _crossScreenConfig.Animation.AnimationPath,
    TargetHeight          = _crossScreenConfig.Animation.TargetHeight,
    Loop                  = _crossScreenConfig.Animation.Loop,
    VerticalAlign         = _crossScreenConfig.Animation.VerticalAlign,
    CenterInitialPosition = _crossScreenConfig.Animation.CenterInitialPosition,
    SpeedMultiplier       = _crossScreenConfig.Animation.SpeedMultiplier,
    FitMode               = _crossScreenConfig.Animation.FitMode,
    RotateWithPath        = _crossScreenConfig.Animation.RotateWithPath,   // ← ADD THIS
    PrecomputedPath       = globalLayout.Path
};
```

### Change B — Rotation for video D2D render path

- [ ] In `Program.cs`, find the video D2D render loop (search for `_currentVideoD2DBitmap` + `DrawBitmap`). The current block looks like:

```csharp
var destRect = new System.Drawing.RectangleF(_animX, _animY, _animWidth, _animHeight);
_d2dContext.DrawBitmap(
    _currentVideoD2DBitmap,
    destRect,
    1.0f,
    BitmapInterpolationMode.Linear,
    null);
```

Replace with:

```csharp
var destRect = new System.Drawing.RectangleF(_animX, _animY, _animWidth, _animHeight);
bool hasRotation = _rotateWithPath && _animRotationRad != 0f && _animPath.Count >= 2;
if (hasRotation)
    _d2dContext.Transform = Matrix3x2.CreateRotation(
        _animRotationRad, new Vector2(_animX + _animWidth / 2f, _animY + _animHeight / 2f));
_d2dContext.DrawBitmap(
    _currentVideoD2DBitmap,
    destRect,
    1.0f,
    BitmapInterpolationMode.Linear,
    null);
if (hasRotation)
    _d2dContext.Transform = Matrix3x2.Identity;
```

### Build & commit

- [ ] Build the solution: `dotnet build WaBiBaBuSy.sln`  
  Expected: 0 errors.

- [ ] Commit:

```bash
git add WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs
git add WaBiBaBuSy.Player.D2D/Program.cs
git commit -m "fix: forward RotateWithPath in sequential AnimationLayerConfig clone; add rotation to video D2D render path"
```

---

## Task 2 — RandomWalk perpendicular offset in IconZone

**Files:**
- Modify: `WaBiBaBuSy.Player.D2D/Program.cs` — `UpdateAnimationPosition` + new helper

### Step 1 — Add `GeneratePerpOffset` helper

- [ ] In `Program.cs`, find `ComputePathLength` (~line 2050). Add this method immediately after it:

```csharp
/// Returns a deterministic value in [-1, 1] for the given seed and step index.
private static float GeneratePerpOffset(int seed, int stepIndex)
{
    var rng = new Random(seed ^ (int)((uint)stepIndex * 2654435761u));
    return (float)(rng.NextDouble() * 2.0 - 1.0);
}
```

### Step 2 — Extend `needsTangent` guard

- [ ] In `UpdateAnimationPosition`, find:

```csharp
bool needsTangent = _rotateWithPath || _movementConfig?.Type == MovementType.SineWave;
```

Replace with:

```csharp
bool needsTangent = _rotateWithPath
    || _movementConfig?.Type == MovementType.SineWave
    || (_movementConfig?.Type == MovementType.RandomWalk && _movementConfig.WaveAmplitudePixels > 0);
```

### Step 3 — Add RandomWalk offset block

- [ ] Find the SineWave perpendicular block ending with `py = Math.Clamp(py, …);`. Immediately after its closing brace, and before the `_animX = px - _animWidth / 2f - _monitorOffsetX;` line, add:

```csharp
if (_movementConfig?.Type == MovementType.RandomWalk && _movementConfig.WaveAmplitudePixels > 0)
{
    float stepIntervalMs = MathF.Max(100f, _movementConfig.RandomStepIntervalMs);
    int stepIndex  = (int)(elapsedMs / stepIntervalMs);
    float withinStep = (elapsedMs % stepIntervalMs) / stepIntervalMs;  // 0..1
    int lapSeed = _movementConfig.RandomSeed ^ (int)((uint)_traverseCount * 2246822519u);
    float t0 = GeneratePerpOffset(lapSeed, stepIndex);
    float t1 = GeneratePerpOffset(lapSeed, stepIndex + 1);
    float perpOffset = (t0 + (t1 - t0) * withinStep) * _movementConfig.WaveAmplitudePixels;
    float perpX = -MathF.Sin(tangentAngle);
    float perpY =  MathF.Cos(tangentAngle);
    px += perpX * perpOffset;
    py += perpY * perpOffset;
    py = Math.Clamp(py, _animHeight / 2f, Math.Max(_animHeight / 2f, _height - _animHeight / 2f));
}
```

### Build & commit

- [ ] `dotnet build WaBiBaBuSy.sln` — expected: 0 errors.

- [ ] Commit:

```bash
git add WaBiBaBuSy.Player.D2D/Program.cs
git commit -m "feat: add RandomWalk perpendicular offset in IconZone path-following mode"
```

---

## Task 3 — New `PlayerCommandUpdatePath` IPC message

**Files:**
- Create: `WaBiBaBuSy.Player.Common/Messages/PlayerCommandUpdatePath.cs`

- [ ] Create the file:

```csharp
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Sent from host to D2D player to replace the active IconZone path mid-session.
/// Used for per-lap A* path variation in sequential mode.
/// Fire-and-forget — no READY response expected.
/// </summary>
public class PlayerCommandUpdatePath : PlayerMessageBase
{
    public PlayerCommandUpdatePath()
    {
        MessageType = "cmd_update_path";
    }

    public List<WaypointF> Path { get; set; } = new();
}
```

- [ ] `dotnet build WaBiBaBuSy.sln` — expected: 0 errors.

- [ ] Commit:

```bash
git add WaBiBaBuSy.Player.Common/Messages/PlayerCommandUpdatePath.cs
git commit -m "feat: add PlayerCommandUpdatePath IPC message for per-lap path refresh"
```

---

## Task 4 — Player: pending path swap + LAP_COMPLETE signal + `cmd_update_path` handler

**Files:**
- Modify: `WaBiBaBuSy.Player.D2D/Program.cs`

### Step 1 — Add `_pendingNewPath` field

- [ ] In `Program.cs`, find the IconZone fields block (~line 397, near `_animPath` and `_traverseCount`). Add:

```csharp
// Pending path pushed by host (cmd_update_path). Applied at start of next UpdateAnimationPosition.
private static List<WaypointF>? _pendingNewPath;
```

### Step 2 — Apply pending path at top of `UpdateAnimationPosition`

- [ ] At the very beginning of `UpdateAnimationPosition(long elapsedMs)`, before the `if (_backgroundMode == BackgroundMode.IconZone …)` check, add:

```csharp
var pending = Interlocked.Exchange(ref _pendingNewPath, null);
if (pending != null && _backgroundMode == BackgroundMode.IconZone)
{
    _animPath.Clear();
    foreach (var wp in pending) _animPath.Add((wp.X, wp.Y));
    _animPathTotalLength = ComputePathLength(_animPath);
    _logger?.LogInformation("[IconZone] Path refreshed from host: {Pts} waypoints", _animPath.Count);
}
```

### Step 3 — Emit `SIGNAL:LAP_COMPLETE:N` in sequential mode

- [ ] In `UpdateAnimationPosition`, find the traverse-detection block:

```csharp
if (newTraverseCount > _traverseCount)
{
    _traverseCount = newTraverseCount;
    RebuildPathOnly(_traverseCount);
}
```

Replace with:

```csharp
if (newTraverseCount > _traverseCount)
{
    _traverseCount = newTraverseCount;
    RebuildPathOnly(_traverseCount);
    if (_animationConfig?.PrecomputedPath?.Count > 0)
    {
        Console.Error.WriteLine($"SIGNAL:LAP_COMPLETE:{_traverseCount}");
        Console.Error.Flush();
    }
}
```

### Step 4 — Clear `_pendingNewPath` on animation reload

- [ ] In `Program.cs`, find the reset block inside `InitializeBackground` (or wherever `_animPath.Clear()` and `_traverseCount = 0` are set together, ~line 1461). Add immediately after:

```csharp
_pendingNewPath = null;
```

### Step 5 — Handle `cmd_update_path` in `HandleJsonCommand`

- [ ] In `HandleJsonCommand`, find the `switch (wrapper.MessageType)` block. Add a new case before the `default`/closing brace:

```csharp
case "cmd_update_path":
    var pathCmd = JsonConvert.DeserializeObject<PlayerCommandUpdatePath>(json);
    if (pathCmd?.Path?.Count > 0)
        Interlocked.Exchange(ref _pendingNewPath, pathCmd.Path);
    break;
```

### Build & commit

- [ ] `dotnet build WaBiBaBuSy.sln` — expected: 0 errors.

- [ ] Commit:

```bash
git add WaBiBaBuSy.Player.D2D/Program.cs
git commit -m "feat: player emits LAP_COMPLETE signal and accepts cmd_update_path for per-lap path refresh"
```

---

## Task 5 — `D2DPlayerHost`: `LapCompleted` event + parse signal + `SendUpdatePathAsync`

**Files:**
- Modify: `WaBiBaBuSy.WallpaperEngine/Direct2D/D2DPlayerHost.cs`

### Step 1 — Add `LapCompleted` event

- [ ] In `D2DPlayerHost.cs`, near the existing public interface (find `public event` or `public bool IsRunning`). Add:

```csharp
/// <summary>Raised when the player completes a full path traversal in sequential mode.</summary>
public event EventHandler<int>? LapCompleted;
```

### Step 2 — Parse `SIGNAL:LAP_COMPLETE:N` in `OnPlayerError`

- [ ] Find `OnPlayerError` (the `ErrorDataReceived` handler, ~line 629). It already handles `SIGNAL:NEEDS_REPARENT`. Add a new block immediately before that existing check (or after — order doesn't matter):

```csharp
if (line != null && line.StartsWith("SIGNAL:LAP_COMPLETE:") &&
    int.TryParse(line[20..], out int lapNum))
{
    _logger.LogInformation("[Player] LAP_COMPLETE signal: lap {Lap}", lapNum);
    LapCompleted?.Invoke(this, lapNum);
    return;
}
```

### Step 3 — Add `SendUpdatePathAsync`

- [ ] After `SendToggleDebugOverlayAsync` or any of the existing `SendXxxAsync` methods, add:

```csharp
/// <summary>
/// Sends a new IconZone path to the player for mid-session path refresh.
/// Fire-and-forget — no READY response expected.
/// </summary>
public async Task SendUpdatePathAsync(List<WaypointF> path)
{
    if (!IsRunning) return;
    var cmd = new PlayerCommandUpdatePath { Path = path };
    await SendCommandAsync(JsonConvert.SerializeObject(cmd));
}
```

### Build & commit

- [ ] `dotnet build WaBiBaBuSy.sln` — expected: 0 errors.

- [ ] Commit:

```bash
git add WaBiBaBuSy.WallpaperEngine/Direct2D/D2DPlayerHost.cs
git commit -m "feat: D2DPlayerHost exposes LapCompleted event and SendUpdatePathAsync"
```

---

## Task 6 — `D2DCompositionService`: aggregate, deduplicate, broadcast

**Files:**
- Modify: `WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs`

### Step 1 — Add fields and event

- [ ] In the private fields block (~line 22), add:

```csharp
private int _lastBroadcastLap = -1;
```

- [ ] Near the public `IsRunning` property, add:

```csharp
/// <summary>
/// Raised once per completed global path traversal in sequential mode (deduplicated across all players).
/// Arg is the lap number (1-based).
/// </summary>
public event EventHandler<int>? GlobalLapCompleted;
```

### Step 2 — Subscribe to player `LapCompleted` in `InitializeAsync`

- [ ] In `InitializeAsync`, find the line `_playerHosts[screen.Order] = playerHost;`. Immediately after it, add:

```csharp
playerHost.LapCompleted += (_, lapNum) =>
{
    if (lapNum > _lastBroadcastLap)
    {
        _lastBroadcastLap = lapNum;
        GlobalLapCompleted?.Invoke(this, lapNum);
    }
};
```

### Step 3 — Reset `_lastBroadcastLap` in `StopAsync`

- [ ] In `StopAsync`, after `_isRunning = false;`, add:

```csharp
_lastBroadcastLap = -1;
```

### Step 4 — Add `BroadcastNewPathAsync`

- [ ] After `SendToggleDebugOverlayAsync`, add:

```csharp
/// <summary>
/// Sends an updated IconZone path to every running player.
/// Fire-and-forget per player — individual failures are logged but do not abort the broadcast.
/// </summary>
public async Task BroadcastNewPathAsync(List<WaypointF> path)
{
    foreach (var host in _playerHosts.Values)
    {
        if (!host.IsRunning) continue;
        try { await host.SendUpdatePathAsync(path); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to broadcast new path to player"); }
    }
}
```

### Build & commit

- [ ] `dotnet build WaBiBaBuSy.sln` — expected: 0 errors.

- [ ] Commit:

```bash
git add WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs
git commit -m "feat: D2DCompositionService aggregates LapCompleted into GlobalLapCompleted and adds BroadcastNewPathAsync"
```

---

## Task 7 — `MainWindowViewModel`: save params + subscribe + recompute + broadcast

**Files:**
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs`

### Step 1 — Add per-session sequential fields

- [ ] In the private fields section of `MainWindowViewModel`, add:

```csharp
// Parameters saved at sequential+IconZone startup for per-lap path recomputation.
private int _lastGlobalLap = -1;
private int _seqCellW, _seqCellH, _seqVirtualCanvasWidth, _seqVirtualH;
private int _seqPathPaddingPx, _seqVisualPaddingPx;
private List<string> _seqPaletteHexes = new();
private string _seqCorridorColorHex = "#1E1E1E";
```

### Step 2 — Save params after global path computation

- [ ] In `StartCrossScreenD2DAsync`, find the block that computes `globalLayout` (the `ZonePlanner.Compute` call, ~line 2782). After the `Debug.WriteLine($"[CrossScreen] IconZone sequential …")` line and before the closing `catch`, add:

```csharp
_seqCellW             = cellW;
_seqCellH             = cellH;
_seqVirtualCanvasWidth = canvasManager.VirtualBounds.Width;
_seqVirtualH           = virtualH;
_seqPathPaddingPx      = pathPaddingPx;
_seqVisualPaddingPx    = Math.Max(4, cellW / 10);
_seqPaletteHexes       = _crossScreenConfig.Background.IconZonePaletteHexes;
_seqCorridorColorHex   = _crossScreenConfig.Background.IconCorridorColorHex;
```

### Step 3 — Subscribe to `GlobalLapCompleted` after `InitializeAsync`

- [ ] In `StartCrossScreenD2DAsync`, find `await d2dService.InitializeAsync(…)`. Immediately after it (before `newServices.Add(…)`), add:

```csharp
if (!perMonitor && _crossScreenConfig.Background.Mode == BackgroundMode.IconZone)
    d2dService.GlobalLapCompleted += OnSequentialLapCompleted;
```

### Step 4 — Add `OnSequentialLapCompleted` handler

- [ ] Add this private method near `StopCrossScreen`:

```csharp
private void OnSequentialLapCompleted(object? sender, int lapNum)
{
    if (lapNum <= _lastGlobalLap) return;
    _lastGlobalLap = lapNum;

    _ = Task.Run(async () =>
    {
        try
        {
            var iconService = new WaBiBaBuSy.Core.Services.Desktop.DesktopIconService();
            var allIcons    = iconService.GetIconPositions();
            var newLayout   = WaBiBaBuSy.WallpaperEngine.Desktop.ZonePlanner.Compute(
                allIcons.Select(i => (i.PixelX, i.PixelY)),
                _seqCellW, _seqCellH,
                _seqVirtualCanvasWidth, _seqVirtualH,
                _seqPaletteHexes, _seqCorridorColorHex,
                paddingPx:      _seqPathPaddingPx,
                visualPaddingPx: _seqVisualPaddingPx,
                pathVariationSeed: lapNum);

            foreach (var (_, svc) in _d2dCompositionServices)
                await svc.BroadcastNewPathAsync(newLayout.Path);

            Debug.WriteLine($"[SeqPath] Lap {lapNum}: broadcast {newLayout.Path.Count} waypoints");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SeqPath] Recompute failed for lap {lapNum}: {ex.Message}");
        }
    });
}
```

### Step 5 — Unsubscribe and reset in `StopCrossScreen`

- [ ] In `StopCrossScreen`, find the foreach loop that calls `StopAsync` + `Dispose` on each service (~line 3051). Add the unsubscribe before `StopAsync`:

```csharp
foreach (var kvp in _d2dCompositionServices)
{
    kvp.Value.GlobalLapCompleted -= OnSequentialLapCompleted;  // ← ADD
    try { await kvp.Value.StopAsync(); } catch { }
    try { kvp.Value.Dispose(); } catch { }
}
_d2dCompositionServices.Clear();
_lastGlobalLap = -1;  // ← ADD after Clear()
```

### Build & commit

- [ ] `dotnet build WaBiBaBuSy.sln` — expected: 0 errors.

- [ ] Commit:

```bash
git add WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs
git commit -m "feat: MainWindowViewModel recomputes and broadcasts new A* path on each sequential lap completion"
```

---

## Task 8 — Final build + manual integration checklist

- [ ] Full solution build: `dotnet build WaBiBaBuSy.sln`  
  Expected: 0 errors, 0 warnings introduced by this work.

- [ ] **Test A — RotateWithPath in sequential mode**  
  1. Configure: `DistributionMode = Sequential`, `BackgroundMode = IconZone`, `RotateWithPath = true`, any GIF.  
  2. Start cross-screen. Animation should visibly rotate to align with path direction on every monitor.  
  Previously: never rotated in sequential mode.

- [ ] **Test B — Video rotation**  
  1. Same config, substitute a video (`.mp4`) for the GIF.  
  2. Rotation should still apply.  
  Previously: video path had no rotation code.

- [ ] **Test C — RandomWalk lateral variation**  
  1. Configure: `MovementType = RandomWalk`, `WaveAmplitudePixels = 120`, `RandomStepIntervalMs = 2000`, `BackgroundMode = IconZone`.  
  2. Observe: animation wanders ±120 px perpendicular to the A* corridor, changing direction every ~2 s.  
  3. Let it run through at least 2 full laps. The wander pattern should be visibly different each lap (different seed).  
  Previously: RandomWalk looked identical to Linear in IconZone mode.

- [ ] **Test D — Host-driven path refresh**  
  1. Sequential + IconZone with any movement type. Watch the debug overlay (F11 → Show Path).  
  2. After each full traversal, the A* path displayed should change (different start/end rows through the icon grid).  
  3. Verify in logs: `[SeqPath] Lap N: broadcast X waypoints` appears after each lap, and player logs show `[IconZone] Path refreshed from host: X waypoints`.  
  Previously: path was fixed for the entire session.

- [ ] **Test E — Simultaneous mode unaffected**  
  1. Switch to `DistributionMode = Simultaneous` with same config.  
  2. No `LAP_COMPLETE` signals should fire (guard: `_animationConfig?.PrecomputedPath?.Count > 0` is false in simultaneous).  
  3. RandomWalk offset, rotation, and local `RebuildPathOnly` all continue to work as before.

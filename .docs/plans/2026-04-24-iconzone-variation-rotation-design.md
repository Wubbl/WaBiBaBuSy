# IconZone Path Variation, RandomWalk Offset & Rotation Fixes

**Date:** 2026-04-24  
**Status:** Approved  
**Scope:** WaBiBaBuSy.Player.D2D, WaBiBaBuSy.WallpaperEngine, WaBiBaBuSy.UI, WaBiBaBuSy.Player.Common

---

## Problem Summary

Four issues in IconZone sequential mode:

1. **Rotation broken in sequential mode** — `AnimationLayerConfig` clone in `MainWindowViewModel.cs:2792` omits `RotateWithPath`, which defaults to `false`. Players never rotate in sequential mode.
2. **Rotation missing for video content** — The video D2D render path (`Program.cs` ~line 1047) has no `Matrix3x2.CreateRotation` block. Rotation only works for GIF content.
3. **All movement types look identical in IconZone** — Only `SineWave` adds perpendicular offset to path position. `RandomWalk`, `Linear`, `Bounce`, `Circular` all traverse the A* path identically with no lateral variation.
4. **Sequential path never varies between laps** — A* path is fixed at startup via `PrecomputedPath`. Per-lap path variation was disabled to prevent local per-monitor recomputation (which breaks the global spanning path).

---

## Design

### Change A — Fix `RotateWithPath` in sequential clone (1 line)

**File:** `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` line ~2792

Add `RotateWithPath = _crossScreenConfig.Animation.RotateWithPath` to the `AnimationLayerConfig` object initializer that clones the animation config for sequential+IconZone mode.

---

### Change B — Rotation for video D2D render path (~10 lines)

**File:** `WaBiBaBuSy.Player.D2D/Program.cs` (video render loop, ~line 1047)

After `UpdateAnimationPosition(elapsedMs)`, add the same `hasRotation`/`Matrix3x2.CreateRotation` block already present in the GIF render path:

```csharp
bool hasRotation = _rotateWithPath && _animRotationRad != 0f && _animPath.Count >= 2;
if (hasRotation)
    _d2dContext.Transform = Matrix3x2.CreateRotation(_animRotationRad,
        new Vector2(_animX + _animWidth / 2f, _animY + _animHeight / 2f));
// ... DrawBitmap ...
if (hasRotation)
    _d2dContext.Transform = Matrix3x2.Identity;
```

---

### Change C — RandomWalk perpendicular offset in IconZone (~30 lines)

**File:** `WaBiBaBuSy.Player.D2D/Program.cs` — `UpdateAnimationPosition`

**Trigger conditions:**
- `_backgroundMode == BackgroundMode.IconZone`
- `_movementConfig.Type == MovementType.RandomWalk`
- `_movementConfig.WaveAmplitudePixels > 0` (zero = strict path-following, no lateral motion)

**Parameters reused from `MovementConfig`:**
- `WaveAmplitudePixels` — maximum perpendicular offset in pixels
- `RandomStepIntervalMs` — interval between random targets
- `RandomSeed` — base seed (XORed with lap counter)

**Algorithm:**

1. Extend `needsTangent` guard:
   ```csharp
   bool needsTangent = _rotateWithPath
       || _movementConfig?.Type == MovementType.SineWave
       || (_movementConfig?.Type == MovementType.RandomWalk && _movementConfig.WaveAmplitudePixels > 0);
   ```

2. After the SineWave block, add a RandomWalk block that runs only when the above conditions are met:
   ```csharp
   float stepIntervalMs = MathF.Max(100f, _movementConfig.RandomStepIntervalMs);
   int stepIndex = (int)(elapsedMs / stepIntervalMs);
   float withinStep = (elapsedMs % stepIntervalMs) / stepIntervalMs;   // 0..1
   int lapSeed = _movementConfig.RandomSeed ^ (int)((uint)_traverseCount * 2246822519u);
   float t0 = GeneratePerpOffset(lapSeed, stepIndex);
   float t1 = GeneratePerpOffset(lapSeed, stepIndex + 1);
   float perpOffset = (t0 + (t1 - t0) * withinStep) * _movementConfig.WaveAmplitudePixels;
   float perpX = -MathF.Sin(tangentAngle);
   float perpY =  MathF.Cos(tangentAngle);
   px += perpX * perpOffset;
   py += perpY * perpOffset;
   py = Math.Clamp(py, _animHeight / 2f, Math.Max(_animHeight / 2f, _height - _animHeight / 2f));
   ```

3. `GeneratePerpOffset(seed, stepIndex)` returns a value in `[-1, 1]`:
   ```csharp
   static float GeneratePerpOffset(int seed, int stepIndex)
   {
       var rng = new Random(seed ^ (int)((uint)stepIndex * 2654435761u));
       return (float)(rng.NextDouble() * 2.0 - 1.0);
   }
   ```

**Per-lap variation:** `_traverseCount` already increments in sequential mode (it's updated before `RebuildPathOnly` early-returns). XORing it into `lapSeed` gives a fresh random pattern every lap — no host involvement needed for this part.

---

### Change D — Host-driven per-lap A* path refresh (~110 lines across 5 files)

#### D1. Player emits LAP_COMPLETE signal

**File:** `WaBiBaBuSy.Player.D2D/Program.cs` — `UpdateAnimationPosition`

When `_traverseCount` increments AND `_animationConfig?.PrecomputedPath?.Count > 0` (sequential mode marker):

```csharp
Console.Error.WriteLine($"SIGNAL:LAP_COMPLETE:{_traverseCount}");
Console.Error.Flush();
```

Placed immediately after `_traverseCount = newTraverseCount;`, same pattern as `SIGNAL:NEEDS_REPARENT`.

#### D2. Player receives `cmd_update_path`

**New file:** `WaBiBaBuSy.Player.Common/Messages/PlayerCommandUpdatePath.cs`

```csharp
public class PlayerCommandUpdatePath : PlayerMessageBase
{
    public PlayerCommandUpdatePath() { MessageType = "cmd_update_path"; }
    public List<WaypointF> Path { get; set; } = new();
}
```

**File:** `WaBiBaBuSy.Player.D2D/Program.cs`

New field:
```csharp
private static volatile List<WaypointF>? _pendingNewPath = null;
```

New case in `HandleJsonCommand`:
```csharp
case "cmd_update_path":
    var pathCmd = JsonConvert.DeserializeObject<PlayerCommandUpdatePath>(json);
    if (pathCmd?.Path?.Count > 0)
        _pendingNewPath = pathCmd.Path;
    break;
```

Applied at the **top** of `UpdateAnimationPosition` (before traverse detection):
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

Thread safety: `Interlocked.Exchange` ensures the command thread's write and the render thread's read are atomic. The field is `volatile` so the render thread always sees the latest value. `List<WaypointF>` assignment is a reference swap — safe.

#### D3. D2DPlayerHost: parse signal + send update

**File:** `WaBiBaBuSy.WallpaperEngine/Direct2D/D2DPlayerHost.cs`

New event:
```csharp
public event EventHandler<int>? LapCompleted;
```

In `OnPlayerError`, add before the logging fallthrough:
```csharp
if (line != null && line.StartsWith("SIGNAL:LAP_COMPLETE:") &&
    int.TryParse(line[20..], out int lapNum))
{
    _logger.LogInformation("[Player] LAP_COMPLETE signal: lap {Lap}", lapNum);
    LapCompleted?.Invoke(this, lapNum);
    return;
}
```

New method (fire-and-forget, no READY response):
```csharp
public async Task SendUpdatePathAsync(List<WaypointF> path)
{
    if (!IsRunning) return;
    var cmd = new PlayerCommandUpdatePath { Path = path };
    await SendCommandAsync(JsonConvert.SerializeObject(cmd));
}
```

#### D4. D2DCompositionService: aggregate + deduplicate + broadcast

**File:** `WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs`

New field:
```csharp
private int _lastBroadcastLap = -1;
```

New event:
```csharp
public event EventHandler<int>? GlobalLapCompleted;
```

In `InitializeAsync`, after `_playerHosts[screen.Order] = playerHost;`:
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

New method:
```csharp
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

Reset on stop: set `_lastBroadcastLap = -1` in `StopAsync`.

#### D5. MainWindowViewModel: subscribe + recompute + broadcast

**File:** `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs`

New fields (populated at the end of `StartCrossScreenD2DAsync` when sequential+IconZone):
```csharp
private int _lastGlobalLap = -1;
private int _seqCellW, _seqCellH, _seqVirtualCanvasWidth, _seqVirtualH;
private int _seqPathPaddingPx, _seqVisualPaddingPx;
private List<string> _seqPaletteHexes = new();
private string _seqCorridorColorHex = "#1E1E1E";
```

Subscribe to each service's event after `d2dService.InitializeAsync(...)`:
```csharp
d2dService.GlobalLapCompleted += OnSequentialLapCompleted;
```

Handler:
```csharp
private void OnSequentialLapCompleted(object? sender, int lapNum)
{
    if (lapNum <= _lastGlobalLap) return;
    _lastGlobalLap = lapNum;

    _ = Task.Run(async () =>
    {
        try
        {
            var iconService = new DesktopIconService();
            var allIcons = iconService.GetIconPositions();
            var newLayout = ZonePlanner.Compute(
                allIcons.Select(i => (i.PixelX, i.PixelY)),
                _seqCellW, _seqCellH,
                _seqVirtualCanvasWidth, _seqVirtualH,
                _seqPaletteHexes, _seqCorridorColorHex,
                paddingPx: _seqPathPaddingPx,
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

Unsubscribe when stopping: `d2dService.GlobalLapCompleted -= OnSequentialLapCompleted;` and reset `_lastGlobalLap = -1`.

---

## Files Changed

| File | Change |
|------|--------|
| `WaBiBaBuSy.Player.Common/Messages/PlayerCommandUpdatePath.cs` | **New** — IPC message |
| `WaBiBaBuSy.Player.D2D/Program.cs` | Changes B, C, D1, D2 |
| `WaBiBaBuSy.WallpaperEngine/Direct2D/D2DPlayerHost.cs` | Change D3 |
| `WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs` | Change D4 |
| `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` | Changes A, D5 |

---

## Invariants Preserved

- **No READY handshake for `cmd_update_path`**: fire-and-forget, consistent with `cmd_toggle_debug_overlay`.
- **Player remains authoritative for its own path in simultaneous mode**: `_pendingNewPath` guard checks `BackgroundMode.IconZone` but path update only arrives when sequential (host only broadcasts in `OnSequentialLapCompleted`).
- **`PrecomputedPath` stays as the initial-path signal**: existing early-return in `RebuildPathOnly` is unchanged. `cmd_update_path` is the runtime refresh channel.
- **Sync preserved**: all players receive the same new path from the host at approximately the same time. The ±IPC latency (~1ms localhost) is negligible vs the ±50ms sync tolerance.

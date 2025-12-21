# Direct2D/Composition Rendering Architecture

**Last Updated:** 2025-12-11
**Status:** Implementation Complete
**Build:** 0 Errors

---

## Overview

The Direct2D composition system renders animated wallpapers (GIFs, videos) by compositing multiple layers (background + animation) into frames, then displaying them via a dedicated rendering window. This document explains the architecture to prevent future mismatches.

---

## Key Architecture Principle

> **CRITICAL:** On Windows 11 24H2+ (layered desktop mode), you CANNOT draw directly to WorkerW via `GetDC()`. You MUST create a separate window and parent it to Progman.

### Why This Matters

| Approach | Windows 10/Legacy | Windows 11 24H2+ |
|----------|-------------------|------------------|
| `GetDC(workerW)` + GDI+ | ✅ Works | ❌ Invisible |
| Window parented to Progman | ✅ Works | ✅ Works |

The standalone renderers (GIF, Video, Image) work because they create their own windows. The Direct2D composition system must do the same.

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          COMPOSITION PIPELINE                                │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────────┐                                                    │
│  │ LocalAnimationRender│  Orchestrates render loop (60 FPS timer)           │
│  │    ingService       │  Calculates elapsed time                           │
│  └──────────┬──────────┘  Coordinates composer + renderer                   │
│             │                                                               │
│             ▼                                                               │
│  ┌─────────────────────┐                                                    │
│  │   ComposerService   │  Service wrapper for composition                   │
│  │                     │  Manages initialization lifecycle                   │
│  └──────────┬──────────┘                                                    │
│             │                                                               │
│             ▼                                                               │
│  ┌─────────────────────┐                                                    │
│  │ CompositionRenderer │  Composites layers into final bitmap               │
│  │                     │  Calls UpdateAnimationPosition()                   │
│  │                     │  Calls ComposeForScreen()                          │
│  └──────────┬──────────┘                                                    │
│             │                                                               │
│      ┌──────┴──────┐                                                        │
│      ▼             ▼                                                        │
│  ┌─────────┐  ┌─────────────────┐                                           │
│  │Background│  │AnimationLayer   │                                           │
│  │LayerRend.│  │Renderer         │  Uses HEADLESS mode renderers            │
│  └─────────┘  └────────┬────────┘                                           │
│                        │                                                    │
│                        ▼                                                    │
│               ┌────────────────┐                                            │
│               │GifWallpaper    │  HeadlessMode=true                         │
│               │Renderer        │  NO window created                         │
│               │                │  Only provides frames via                  │
│               │                │  GetFrameAtPosition()                      │
│               └────────────────┘                                            │
│                                                                             │
├─────────────────────────────────────────────────────────────────────────────┤
│                          DISPLAY PIPELINE                                    │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────────┐                                                    │
│  │  Direct2DRenderer   │  Creates Form + PictureBox                         │
│  │                     │  Parents to Progman via SetAsWallpaperWindow()     │
│  │                     │  Updates PictureBox.Image each frame               │
│  └─────────────────────┘                                                    │
│                                                                             │
│  Window Hierarchy (Windows 11 24H2+):                                       │
│                                                                             │
│    Progman (65894)                                                          │
│    ├── SHELLDLL_DefView (65898) ← Desktop icons                             │
│    ├── WorkerW (2687554)                                                    │
│    └── Direct2DRenderer Form ← Our wallpaper window                         │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Component Responsibilities

### 1. LocalAnimationRenderingService
**File:** `WaBiBaBuSy.WallpaperEngine/Services/LocalAnimationRenderingService.cs`

- Manages 60 FPS render timer
- Calculates elapsed time since render start
- Calls `ComposerService.ComposeSingle()` each frame
- Calls `Direct2DRenderer.DisplayFrame()` with composed bitmap
- Handles Start/Stop lifecycle

### 2. ComposerService
**File:** `WaBiBaBuSy.WallpaperEngine/Composition/ComposerService.cs`

- Service wrapper around `CompositionRenderer`
- Manages initialization with `VirtualCanvasManager`, background config, animation config
- Passes elapsed time through to composition layer

### 3. CompositionRenderer
**File:** `WaBiBaBuSy.WallpaperEngine/Composition/CompositionRenderer.cs`

- Creates and manages `BackgroundLayerRenderer` and `AnimationLayerRenderer`
- `UpdateAnimationPosition(timestampMs, pixelsPerSecond)` - updates animation X position
- `ComposeForScreen(screen)` - composites background + animation into final bitmap

### 4. AnimationLayerRenderer
**File:** `WaBiBaBuSy.WallpaperEngine/Composition/AnimationLayerRenderer.cs`

- Manages animation position (X, Y) on virtual canvas
- Creates appropriate renderer (GIF, Video) in **HeadlessMode**
- Provides `RenderForScreen(screen)` returning composed animation bitmap
- Tracks `_currentElapsedMs` for frame selection synchronization

### 5. GifWallpaperRenderer (Headless Mode)
**File:** `WaBiBaBuSy.WallpaperEngine/Renderers/GifWallpaperRenderer.cs`

- When `HeadlessMode=true`: Loads GIF, extracts frame delays, but **creates no window**
- Provides `GetFrameAtPosition(elapsedMs)` returning correct frame for timing
- When `HeadlessMode=false`: Creates Form+PictureBox (standalone mode)

### 6. Direct2DRenderer
**File:** `WaBiBaBuSy.WallpaperEngine/Direct2D/Direct2DRenderer.cs`

- Creates Form + PictureBox for frame display
- Parents window to Progman via `SetAsWallpaperWindow()`
- Updates `PictureBox.Image` each frame
- **CRITICAL:** Must create window, cannot use `GetDC(workerW)` on Windows 11 24H2+

---

## Headless Mode

**Purpose:** Allow renderers to be used purely for frame extraction without creating windows.

### WallpaperConfig.HeadlessMode Property
**File:** `WaBiBaBuSy.Models/WallpaperConfig.cs`

```csharp
/// <summary>
/// Headless mode - when true, renderer only provides frames via GetFrameAtPosition()
/// without creating a window. Used for composition/Direct2D rendering pipeline.
/// </summary>
public bool HeadlessMode { get; set; } = false;
```

### Usage in AnimationLayerRenderer

```csharp
var wallpaperConfig = new WallpaperConfig
{
    FilePath = config.AnimationPath,
    Type = extension == ".gif" ? WallpaperType.Gif : WallpaperType.Video,
    Loop = true,
    HardwareAcceleration = true,
    MaxFPS = 60,
    MonitorIndex = monitorIndex,
    HeadlessMode = true  // CRITICAL for composition pipeline
};

await _sourceRenderer.InitializeAsync(wallpaperConfig);
```

### GifWallpaperRenderer Behavior

```csharp
if (!config.HeadlessMode)
{
    await CreateRenderWindowAsync(config);  // Standalone mode
}
else
{
    _logger.LogInformation("GIF renderer initialized in HEADLESS mode (no window created)");
}
```

---

## Timing Synchronization

### Problem Solved
Previously, two different clocks were used:
1. Position calculation used render loop elapsed time
2. Frame selection used `DateTime.UtcNow`

This caused animation position and frame selection to be out of sync.

### Solution
`AnimationLayerRenderer` stores `_currentElapsedMs` from `UpdatePosition()` and uses it in `GetAnimationFrame()`:

```csharp
// In UpdatePosition()
_currentElapsedMs = elapsedMs;

// In GetAnimationFrame()
var elapsedMs = _currentElapsedMs;  // Use stored value, not DateTime.UtcNow
var frame = _sourceRenderer.GetFrameAtPosition(elapsedMs);
```

---

## Window Parenting (Windows 11 24H2+)

### The Issue
Windows 11 24H2+ uses "layered desktop mode" where the desktop composition is different:
- `WS_EX_NOREDIRECTIONBITMAP` extended style on Progman
- Direct GDI+ drawing to WorkerW doesn't render visibly
- Wallpaper windows must be children of Progman with proper z-order

### The Solution
All renderers (standalone and composition) use `DesktopWindowManager.SetAsWallpaperWindow()`:

```csharp
// In Direct2DRenderer.Initialize()
_desktopWindowManager.SetAsWallpaperWindow(_renderForm.Handle, screen.ScreenBounds);
```

This method:
1. Detects Windows version (layered vs legacy mode)
2. Adds `WS_CHILD` style to window
3. Adds `WS_EX_LAYERED` with full opacity
4. Parents window to Progman
5. Sets z-order below `SHELLDLL_DefView`

---

## Data Flow (Frame Rendering)

```
1. Timer fires (every 16ms for 60 FPS)
   │
2. LocalAnimationRenderingService.RenderFrame()
   │ Calculate: elapsedMs = currentTime - _startTimestampMs
   │
3. ComposerService.ComposeSingle(screen, elapsedMs, pixelsPerSecond)
   │
4. CompositionRenderer.UpdateAnimationPosition(elapsedMs, pps)
   │ AnimationLayerRenderer._currentVirtualX = -width + (elapsed * pps)
   │ AnimationLayerRenderer._currentElapsedMs = elapsedMs  ← Stored for frame selection
   │
5. CompositionRenderer.ComposeForScreen(screen)
   │ BackgroundLayerRenderer.RenderForScreen() → background bitmap
   │ AnimationLayerRenderer.RenderForScreen()
   │   │ Check IsVisibleOnScreen() - return null if off-screen
   │   │ GetAnimationFrame() - uses _currentElapsedMs
   │   │   │ GifWallpaperRenderer.GetFrameAtPosition(_currentElapsedMs)
   │   │   └─→ Returns correct frame based on GIF timing
   │   └─→ Draw frame portion visible on screen
   │
6. Composite background + animation → final Bitmap
   │
7. Direct2DRenderer.DisplayFrame(bitmap)
   │ _pictureBox.Image = frame
   │
8. Windows displays frame on desktop (via Progman-parented window)
```

---

## Common Pitfalls

### 1. Direct GDI+ Drawing to WorkerW
❌ **Wrong:**
```csharp
var dc = GetDC(workerW);
using (var g = Graphics.FromHdc(dc))
{
    g.DrawImage(frame, 0, 0);  // Won't be visible on Windows 11 24H2+
}
ReleaseDC(workerW, dc);
```

✅ **Correct:**
```csharp
// Create window and parent to Progman
_renderForm = new Form { ... };
_desktopWindowManager.SetAsWallpaperWindow(_renderForm.Handle, bounds);
_pictureBox.Image = frame;  // Update each frame
```

### 2. Missing HeadlessMode for Composition
❌ **Wrong:**
```csharp
var config = new WallpaperConfig { FilePath = path };
// HeadlessMode defaults to false - creates duplicate window!
```

✅ **Correct:**
```csharp
var config = new WallpaperConfig
{
    FilePath = path,
    HeadlessMode = true  // Required for composition pipeline
};
```

### 3. Separate Clocks for Position and Frame Selection
❌ **Wrong:**
```csharp
// In UpdatePosition: uses elapsedMs from render loop
// In GetAnimationFrame: uses DateTime.UtcNow - _animationStartTime
```

✅ **Correct:**
```csharp
// UpdatePosition stores the elapsed time
_currentElapsedMs = elapsedMs;

// GetAnimationFrame uses the stored value
var frame = _sourceRenderer.GetFrameAtPosition(_currentElapsedMs);
```

---

## Testing Checklist

When making changes to the rendering pipeline:

- [ ] Static images work with "Apply Locally" (LibVLC renderer)
- [ ] GIFs work with "Apply Locally" (standalone GIF renderer)
- [ ] Videos work with "Apply Locally" (LibVLC renderer)
- [ ] GIFs work with "Apply via Direct2D" (composition pipeline)
- [ ] Animation position updates correctly (starts at -width, moves right)
- [ ] Animation becomes visible when X >= 0
- [ ] Frame rate is ~60 FPS (not 1 FPS)
- [ ] No duplicate windows created
- [ ] Disposal completes without freezing

---

## Related Files

| File | Purpose |
|------|---------|
| `LocalAnimationRenderingService.cs` | Render loop orchestration |
| `ComposerService.cs` | Composition service wrapper |
| `CompositionRenderer.cs` | Layer composition |
| `AnimationLayerRenderer.cs` | Animation position and frame retrieval |
| `BackgroundLayerRenderer.cs` | Background rendering |
| `Direct2DRenderer.cs` | Frame display via Form+PictureBox |
| `GifWallpaperRenderer.cs` | GIF loading and frame extraction |
| `VideoWallpaperRenderer.cs` | Video frame extraction |
| `DesktopWindowManager.cs` | Window parenting to Progman |
| `WallpaperConfig.cs` | HeadlessMode property |

---

## Related Documentation

- `.docs/TIMESTAMP_OVERFLOW_FIX_2025-12-08.md` - Integer overflow bug in position calculation
- `.docs/CRITICAL_DISCOVERY_FRAME_RATE_2025-12-08.md` - Frame rate investigation
- `.docs/DEADLOCK_FIX_SUMMARY.md` - Timer disposal deadlock fix
- `.docs/DIRECT2D_IMPLEMENTATION_COMPLETE.md` - Original Direct2D implementation notes

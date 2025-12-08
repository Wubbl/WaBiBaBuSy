# Direct2D Render Loop Diagnostics - 2025-12-08

**Status:** ✅ Comprehensive logging added to trace frame composition and rendering pipeline

---

## What Was Added

Comprehensive diagnostic logging has been added to the render pipeline to trace where GIF/video frames are being lost when rendering via Direct2D. The logging creates a detailed breadcrumb trail from frame composition through to screen display.

### Instrumented Components

#### 1. **LocalAnimationRenderingService.RenderFrame()** - Main render loop
- **Log Level:** Information (every 1 second) for frame count statistics
- **What it logs:**
  - `[RenderLoop]` Frame count per second (e.g., "Rendered 60 frames in last 1000ms")
  - `[Compose]` Requests to ComposeSingle with timestamp and screen info
  - `[Display]` Calls to DisplayFrame with client ID
  - Trace-level logs for all intermediate steps
  - **Diagnostics:** Answers "Is the render loop firing?"

**Key metric:** Should see `[RenderLoop] Rendered ~60 frames...` every second

#### 2. **ComposerService.ComposeSingle()** - Frame composition orchestration
- **Log Level:** Information for final composition, Trace for intermediate steps
- **What it logs:**
  - `[Composer]` Entry point with timestamp and pixels-per-second
  - Animation position update calls
  - `[Composer]` Final composed frame dimensions
  - **Diagnostics:** Answers "Is composition happening and returning frames?"

**Key metric:** Should see `[Composer] Frame composed for screen X: WxH` every frame

#### 3. **CompositionRenderer.ComposeForScreen()** - Layer composition
- **Log Level:** Information for final result, Trace for layer details
- **What it logs:**
  - `[Composition]` Background layer rendering
  - `[Composition]` Animation layer rendering
  - `[Composition]` Compositing of animation on top of background
  - Dimensions of background and animation layers
  - **Diagnostics:** Answers "Are both layers being rendered? What are their sizes?"

**Key metric:** Should see `[Composition] Frame composition complete for screen X: WxH`

#### 4. **AnimationLayerRenderer.GetAnimationFrame()** - Animation frame retrieval
- **Log Level:** Information and Trace
- **What it logs:**
  - `[AnimFrame]` Renderer type being used (GifWallpaperRenderer, VideoWallpaperRenderer, etc.)
  - Elapsed milliseconds since animation start
  - Frame dimensions returned
  - **Diagnostics:** Answers "Is GetFrameAtPosition() being called and returning frames?"

**Key metric:** Should see `[AnimFrame] Received frame: WxH`

#### 5. **Direct2DRenderer.DisplayFrame() & RenderFrameToScreen()** - Screen display
- **Log Level:** Information for final result, Trace for GDI+ details
- **What it logs:**
  - `[GDI+]` Getting device context for WorkerW window
  - `[GDI+]` Graphics object creation
  - Frame drawing to screen with dimensions
  - Device context release
  - **Diagnostics:** Answers "Is the frame actually being drawn to screen? Is WorkerW accessible?"

**Key metric:** Should see `[GDI+] Frame rendered to screen X via GDI+ (WxH)`

---

## How to Use These Logs

### Scenario 1: Render Loop Not Firing
**You would see:**
- No `[RenderLoop]` entries every second
- No `[Compose]` entries

**This means:** The timer in `LocalAnimationRenderingService.Start()` is not firing, or the service was never started.

**How to fix:** Check if `Start()` was called after initialization.

### Scenario 2: Frames Not Being Composed
**You would see:**
- `[RenderLoop]` entries appear ✓
- But NO `[Compose]` entries

**This means:** The render loop is running but ComposeSingle is failing silently (exception caught).

**How to fix:** Check the Exception log entries in `[RenderFrame]` catches.

### Scenario 3: Animation Frames Not Retrieved
**You would see:**
- `[RenderLoop]` entries ✓
- `[Compose]` entries ✓
- `[Composition]` entries ✓
- But NO `[AnimFrame]` entries

**This means:** AnimationLayerRenderer.GetAnimationFrame() returned null early.

**How to fix:** The condition check failed - config, renderer, or path is null.

### Scenario 4: GetFrameAtPosition Not Working
**You would see:**
- `[AnimFrame]` "Requesting frame from GifWallpaperRenderer at elapsed Xms" ✓
- But NO `[AnimFrame]` "Received frame" entry

**This means:** GifWallpaperRenderer.GetFrameAtPosition() returned null.

**How to fix:** Check GIF loading, frame extraction logic, or error handling.

### Scenario 5: Frame Not Reaching Screen
**You would see:**
- All logs through `[Composition] Frame composition complete` ✓
- But NO `[GDI+] Frame rendered to screen` entries

**This means:** DisplayFrame was called but RenderFrameToScreen is failing (WorkerW not found or GDI+ error).

**How to fix:** Check WorkerW window handle, or GDI+ device context retrieval.

---

## Log Output Example (Expected)

When everything works correctly, you should see output like:

```
[19:45:32.123] [Information] LocalAnimationRenderingService: Initializing local animation rendering service for monitor 0
[19:45:32.456] [Information] ComposerService: Composer service initialized successfully
[19:45:32.789] [Information] LocalAnimationRenderingService: Starting local animation rendering
[19:45:33.000] [Information] [RenderLoop] Rendered 61 frames in last 1000ms, elapsed=1001ms, pixelsPerSecond=100
[19:45:33.016] [Trace] [Compose] ComposeSingle called for screen 0, timestampMs=1, pixelsPerSecond=100
[19:45:33.016] [Trace] [Composer] Updating animation position
[19:45:33.016] [Trace] [Composition] ComposeForScreen called for screen 0
[19:45:33.016] [Trace] [Composition] Rendering background layer
[19:45:33.016] [Trace] [Composition] Background layer rendered: 1920x1080
[19:45:33.016] [Trace] [Composition] Rendering animation layer
[19:45:33.016] [Trace] [AnimFrame] Requesting frame from GifWallpaperRenderer at elapsed 1ms
[19:45:33.016] [Trace] [AnimFrame] Received frame: 1920x1080
[19:45:33.016] [Trace] [Composition] Animation layer rendered: 1920x1080
[19:45:33.016] [Trace] [Composition] Compositing animation layer on top of background
[19:45:33.016] [Information] [Composition] Frame composition complete for screen 0: 1920x1080
[19:45:33.016] [Information] [Composer] Frame composed for screen 0: 1920x1080
[19:45:33.016] [Trace] [Display] Calling DisplayFrame for clientId=LOCAL
[19:45:33.016] [Trace] [GDI+] Getting device context for WorkerW=0x000900A2
[19:45:33.016] [Trace] [GDI+] Got device context=0x000DC3A0, drawing frame 1920x1080 to screen bounds {X=0,Y=0,Width=1920,Height=1080}
[19:45:33.016] [Trace] [GDI+] Created graphics object, clearing to black
[19:45:33.016] [Trace] [GDI+] Drawing frame to screen position (0,0) with size 1920x1080
[19:45:33.016] [Trace] [GDI+] Frame drawn successfully
[19:45:33.016] [Trace] [GDI+] Released device context
[19:45:33.016] [Information] [GDI+] Frame rendered to screen 0 via GDI+ (1920x1080)
[19:45:33.016] [Display] DisplayFrame completed
```

This pattern repeats ~60 times per second.

---

## Testing Instructions

1. **Build the project** (already done - 0 errors)
2. **Run the application** as normal
3. **Enable Trace-level logging** in settings or config
4. **Select a GIF animation** from the gallery
5. **Click "Apply Via Direct2D"** button
6. **Monitor the logs** for the patterns described above
7. **Take note of where the logs stop** - this indicates where the pipeline is breaking

---

## Files Modified

All modifications are logging only - no functional changes:

1. `LocalAnimationRenderingService.cs:RenderFrame()` - Added frame counter and diagnostic logging
2. `ComposerService.cs:ComposeSingle()` - Added detailed trace logging
3. `CompositionRenderer.cs:ComposeForScreen()` - Added layer-level trace logging
4. `AnimationLayerRenderer.cs:GetAnimationFrame()` - Added renderer type and frame dimension logging
5. `Direct2DRenderer.cs:RenderFrameToScreen()` - Added GDI+ device context and drawing logging

**Total lines added:** ~120 lines of logging statements
**Build status:** ✅ Zero errors, zero new warnings

---

## Next Steps

1. **Run the application with these logs enabled**
2. **Observe where the log trail stops**
3. **Report findings** - specifically which logs appear and which don't
4. **Once bottleneck identified**, we can drill down further

This should definitively show us whether:
- The render loop is actually firing ✓
- Frames are being composed ✓
- Animation frames are being retrieved ✓
- Frames are being drawn to screen ✓

Or where the pipeline breaks.

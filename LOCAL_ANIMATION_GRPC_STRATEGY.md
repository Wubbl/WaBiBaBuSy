# Local Animation via Unified gRPC System - Strategy Document

**Date:** 2025-11-03
**Status:** PLANNING - Decision Pending
**Priority:** High
**Effort:** 4-10 hours (depending on renderer choice)

---

## Executive Summary

**Goal:** Support cross-screen animations on LOCAL machine (server + local client) using the same unified gRPC system that works for remote machines.

**Current Situation:**
- Remote clients work perfectly via distributed animation system (Phases 1-4)
- Local animations use centralized frame composition (CPU-intensive, old architecture)
- Local frame display broken due to Windows Forms + WorkerW incompatibility

**Solution:** Treat the local machine as a standard animation client that receives gRPC messages

**Implementation Options:**
1. **LibVLC-Based Renderer** (Recommended MVP) - 4-6 hours, low risk
2. **Direct2D Native Renderer** (Better, complex) - 8-10 hours, medium risk

---

## Current Architecture (Broken)

```
Server (Local Machine)
├─ Compose frames at 30 FPS
├─ Store frames in memory (bitmaps)
├─ Try to display via WorkerW
└─ ❌ FAILS: Windows Forms incompatible with parenting

Problem: Windows Forms immediately un-parents when SetParent called
Result: Wallpaper invisible on local machine
```

---

## Proposed Architecture (Unified gRPC)

```
┌─────────────────────────────────────────────────────┐
│ Server (WaBiBaBuSy running as SERVER)               │
│                                                      │
│ AnimationDistributor                                 │
│ ├─ Receives StartAnimation request                  │
│ ├─ Loads animation file & background config        │
│ └─ Broadcasts AnimationStart to ALL clients:       │
│    ├─ Remote Client 1                              │
│    ├─ Remote Client 2                              │
│    └─ LOCAL CLIENT (on same machine)               │
└─────────────────────────────────────────────────────┘
              │                    │                   │
         gRPC AnimationStart   (same message for all)
              │                    │                   │
    ┌─────────▼──────────┐  ┌──────▼──────────┐  ┌────▼──────────────┐
    │ Remote Client A    │  │ Remote Client B │  │ Local Client      │
    │                    │  │                 │  │ (Same Machine)    │
    │ ┌────────────────┐ │  │┌──────────────┐ │  │┌──────────────────┐
    │ │ Renderer:      │ │  ││ Renderer:    │ │  ││ Renderer:        │
    │ │ VideoRenderer+ │ │  ││ VideoRenderer│ │  ││ Composed         │
    │ │ Background    │ │  ││ Background  │ │  ││ VideoRenderer+   │
    │ │ @ 30 FPS      │ │  ││ @ 30 FPS    │ │  ││ Background       │
    │ └────────────────┘ │  │└──────────────┘ │  ││ @ 30 FPS         │
    │                    │  │                 │  │└──────────────────┘
    │ Display via        │  │ Display via     │  │ Display via       │
    │ WorkerW (remote)   │  │ WorkerW (remote)│  │ WorkerW (local)   │
    └────────────────────┘  └─────────────────┘  └───────────────────┘

Benefits:
✅ LOCAL and REMOTE use IDENTICAL code path
✅ Same gRPC messages, same renderer logic
✅ Server doesn't care where client runs
✅ No special-case code for "local mode"
✅ Scales uniformly
```

---

## Implementation Option 1: Direct2D-Based Renderer (RECOMMENDED MVP - REVISED)

### ⚠️ IMPORTANT CORRECTION (2025-11-03)

**Original LibVLC recommendation was inaccurate.** LibVLC is designed for rendering VIDEO/IMAGE FILES, not arbitrary bitmaps from composition. Direct2D is purpose-built for bitmap rendering and is the correct choice.

### Architecture

```csharp
// Server sends AnimationPrepare message to local client
// Local client receives and creates Direct2D renderer

public class ComposedAnimationRenderer : IWallpaperRenderer
{
    private CompositionRenderer _compositor;
    private ID2D1Factory _d2dFactory;
    private ID2D1HwndRenderTarget _renderTarget;

    public async Task InitializeAsync(AnimationMetadata metadata)
    {
        // 1. Load animation file + background using EXISTING renderers
        var animationFile = await AnimationFileDownloader.DownloadAsync(metadata.AnimationFile);
        var bgFile = metadata.BackgroundImage;

        // 2. Compose frames using EXISTING CompositionRenderer
        _compositor = new CompositionRenderer(_logger, _loggerFactory);
        var canvas = new VirtualCanvasManager(metadata.Monitors);
        await _compositor.InitializeAsync(
            canvas,
            metadata.BackgroundConfig,
            metadata.AnimationConfig
        );

        // 3. Create Direct2D render target for wallpaper window
        await SetupDirect2DRenderTarget();
    }

    public async Task OnTimingSyncAsync(AnimationTimingSync sync)
    {
        // Update composition position based on server timing
        _compositor.UpdateAnimationPosition(sync.ServerTimestamp, metadata.SpeedPixelsPerSec);

        // Compose frame for each screen
        foreach (var screen in metadata.Monitors)
        {
            var composedBitmap = _compositor.ComposeForScreen(screen);

            // Render composed bitmap via Direct2D
            await RenderViaD2DAsync(composedBitmap, screen);

            composedBitmap.Dispose();
        }
    }

    private async Task RenderViaD2DAsync(Bitmap bitmap, ScreenMapping screen)
    {
        // 1. Convert GDI bitmap to Direct2D bitmap
        using (var d2dBitmap = CreateD2DBitmapFromGDI(bitmap))
        {
            // 2. Render to wallpaper window
            _renderTarget.BeginDraw();

            // Draw at screen position
            var rect = new D2D_RECT_F
            {
                Left = screen.VirtualX,
                Top = screen.VirtualY,
                Right = screen.VirtualX + screen.Width,
                Bottom = screen.VirtualY + screen.Height
            };

            _renderTarget.DrawBitmap(d2dBitmap, rect);
            _renderTarget.EndDraw();
        }
    }

    private async Task SetupDirect2DRenderTarget()
    {
        // Create Direct2D factory
        _d2dFactory = new ID2D1Factory1Impl();

        // Get wallpaper window handle from DesktopWindowManager
        var wallpaperHwnd = await _desktopManager.GetWallpaperWindowAsync();

        // Create render target for wallpaper window
        _renderTarget = _d2dFactory.CreateHwndRenderTarget(wallpaperHwnd);
    }
}
```

### Why Direct2D is Correct (NOT LibVLC)

**LibVLC is designed for:**
- ✅ Loading video files and playing them
- ✅ Loading image files and displaying them
- ✅ Hardware-accelerated playback
- ❌ Rendering arbitrary bitmaps from memory
- ❌ Compositing pre-rendered frames
- ❌ Direct bitmap-to-screen rendering

**Direct2D is designed for:**
- ✅ Rendering bitmaps to screen
- ✅ Compositing multiple bitmaps
- ✅ Hardware-accelerated 2D graphics
- ✅ Direct GPU rendering
- ✅ Wallpaper/desktop window integration

### Implementation Steps

1. **Create ComposedAnimationRenderer class** (~250 lines)
   - Reuses existing CompositionRenderer for frame composition
   - Adds Direct2D rendering pipeline for display
   - Receives AnimationMetadata and TimingSync via gRPC
   - Renders composed frames to wallpaper window

2. **Add Direct2D interop** (~150 lines)
   - P/Invoke declarations for Direct2D APIs
   - Helper class for bitmap conversion (GDI → Direct2D)
   - Render target management

3. **Register local client in AnimationDistributor**
   - When server starts, register local machine as animation client
   - LocalClientId = "localhost" or "127.0.0.1"
   - Send AnimationPrepare to local client same as remote clients

4. **Wire gRPC messages for local client**
   - Local client listens to same gRPC streams as remote clients
   - Same StartClientAnimation RPC → OnAnimationPrepare
   - Same BroadcastAnimationSync RPC → OnTimingSyncAsync

5. **Update MainWindowViewModel**
   - Create local animation client when server starts in animation mode
   - Register with AnimationDistributor
   - Subscribe to gRPC timing sync messages

### Files to Create

**New File: ComposedAnimationRenderer.cs** (~250 lines)
```
WaBiBaBuSy.WallpaperEngine/Renderers/ComposedAnimationRenderer.cs

├─ Constructor: Injected dependencies (logger, factory, desktop manager)
├─ InitializeAsync(metadata): Setup composition + Direct2D
├─ OnTimingSyncAsync(sync): Receive timing, update composition, render
├─ RenderViaD2DAsync(bitmap, screen): Direct2D rendering
├─ SetupDirect2DRenderTarget(): Create D2D render target
└─ DisposeAsync: Cleanup Direct2D resources
```

**New File: Direct2DInterop.cs** (~150 lines)
```
WaBiBaBuSy.WallpaperEngine/Native/Direct2DInterop.cs

├─ ID2D1Factory P/Invoke + COM wrapper
├─ ID2D1HwndRenderTarget P/Invoke + COM wrapper
├─ ID2D1Bitmap P/Invoke
├─ CreateD2DBitmapFromGDI helper
└─ D2D_RECT_F structure
```

### Files to Modify

**AnimationDistributor.cs** (~15 lines)
```
- In StartSequentialAnimationAsync or similar:
  - Detect local client (from configuration or hardcoded)
  - Add local client ID to animation target list
  - Send AnimationPrepare to local client (same as remote)
```

**MainWindowViewModel.cs** (~40 lines)
```
- When server starts in animation mode:
  - Create local animation client
  - Initialize ComposedAnimationRenderer
  - Subscribe to gRPC timing sync messages
  - Call OnTimingSyncAsync when messages arrive
```

**CrossScreenConfig.cs** (~5 lines)
```
- Add: UseUnifiedGrpcRendering = true (default)
- Add: AnimationRenderingMethod = "Direct2D" or "Legacy"
```

### Effort Estimate

- Design: 30 minutes
- ComposedAnimationRenderer: 2-3 hours
- Direct2D interop: 1-2 hours
- Integration: 30 minutes
- Testing: 1-2 hours
- **Total: 5-7 hours (was 4-6, but includes proper Direct2D instead of LibVLC workaround)**

### Risk Level: **LOW-MEDIUM**

- ✅ Direct2D is standard Windows API (proven, stable)
- ✅ Reuses existing CompositionRenderer (no new composition logic)
- ⚠️ New P/Invoke bindings (standard but requires testing)
- ✅ Can fall back to old centralized system if issues
- ✅ Remote clients completely unaffected

### Expected Outcome

```
Before (Broken):
┌─ Server composes frames
├─ Tries to display locally via Windows Forms
└─ ❌ Windows Forms incompatible with WorkerW

After (Working):
┌─ Server sends AnimationPrepare to local client
├─ Local client composes frames via CompositionRenderer
├─ Local client renders via Direct2D (GPU-accelerated)
├─ Direct2D renders to wallpaper window behind desktop icons
└─ ✅ Wallpaper displays correctly on local machine (unified with remote)
```

### Why This is Better Than LibVLC

| Aspect | LibVLC Workaround | Direct2D (Correct) |
|--------|-------------------|-------------------|
| **Purpose** | Video playback | Bitmap rendering |
| **Composing bitmaps** | Not designed for it | Purpose-built |
| **Performance** | File I/O overhead | Direct GPU rendering |
| **Workarounds needed** | Yes (temp files) | No |
| **Code clarity** | Confusing | Clear intent |
| **Maintainability** | Fragile | Stable |

---

## Implementation Option 2: LibVLC Workaround (NOT RECOMMENDED)

### ⚠️ Why LibVLC Doesn't Work for This Use Case

LibVLC is **NOT designed for rendering pre-composed bitmaps**. The only workarounds are fragile and slow:

**Workaround A: Temp File I/O** (Ugly)
```csharp
// For each composed frame:
var bitmap = _compositor.ComposeForScreen(screen);
bitmap.Save("C:\\Temp\\frame_000.png");           // Write to disk
_libVLC.PlayFile("C:\\Temp\\frame_000.png");      // Reload
await Task.Delay(33);                              // Wait
File.Delete("C:\\Temp\\frame_000.png");           // Cleanup

// 30 FPS × 33ms = 990ms I/O overhead per second!
```

**Workaround B: Texture Interop** (Complex)
```csharp
// Get LibVLC internals, convert bitmap to DirectX texture, render
// Result: Fragile code, undocumented APIs, maintenance nightmare
```

### Trade-offs

| Aspect | Direct2D (Correct) | LibVLC (Workaround) |
|--------|---------------------|-------------------|
| **Purpose Match** | ✅ Purpose-built | ❌ Wrong tool |
| **Performance** | ✅ GPU rendering | ❌ Disk I/O |
| **Code Clarity** | ✅ Clear intent | ❌ Confusing |
| **Maintainability** | ✅ Stable | ❌ Fragile |
| **Complexity** | ⚠️ Medium | ❌ Very High |

### Conclusion

LibVLC is best left for what it does well:
- ✅ Load and play video files
- ✅ Load and display image files
- ❌ DO NOT use for rendering bitmaps

---

## Final Decision: Use Direct2D (CORRECTED)

**Direct2D IS the correct MVP choice** - not a future optimization.

| Criteria | Direct2D |
|----------|----------|
| **Purpose Match** | ✅ Purpose-built for bitmap rendering |
| **Performance** | ✅ GPU-accelerated rendering |
| **Proven Tech** | ✅ Standard Windows API |
| **Complexity** | ⚠️ Medium (5-7 hours, not a blocker) |
| **Risk** | ✅ LOW (standard APIs) |
| **Maintenance** | ✅ Stable, no workarounds |
| **Long-term** | ✅ Best for scaling |

**Recommendation:** **Use Direct2D for MVP (it's the right tool for the job)**

---

## Implementation Checklist (Direct2D Option)

### Phase 1: Renderer Implementation (2 hours)
- [ ] Create ComposedAnimationRenderer.cs
- [ ] Implement InitializeAsync (load animation + background)
- [ ] Implement OnRenderFrame (composition logic)
- [ ] Test renderer in isolation

### Phase 2: Server Integration (1 hour)
- [ ] Update AnimationDistributor to register local client
- [ ] Wire gRPC messages to local client
- [ ] Add configuration flag UseUnifiedGrpcRendering

### Phase 3: UI Integration (1 hour)
- [ ] Update MainWindowViewModel to create local animation client
- [ ] Test local animation on single machine
- [ ] Verify display appears behind desktop icons

### Phase 4: Testing (1-2 hours)
- [ ] Local animation alone
- [ ] Local + 1 remote client
- [ ] Local + 2 remote clients
- [ ] Performance validation (CPU, bandwidth)
- [ ] Drift synchronization

### Phase 5: Cleanup (30 min)
- [ ] Remove old frame-composition code (optional, can deprecate)
- [ ] Update documentation
- [ ] Mark old system as legacy

---

## Configuration

Add to `config.json`:

```json
{
  "Animation": {
    "UseUnifiedGrpcRendering": true,
    "LocalAnimationEnabled": true,
    "CompositionMethod": "LibVLC"
  }
}
```

Or post-MVP:

```json
{
  "Animation": {
    "UseUnifiedGrpcRendering": true,
    "LocalAnimationEnabled": true,
    "CompositionMethod": "Direct2D",
    "EnableGpu": true
  }
}
```

---

## Success Criteria

✅ **Functional Requirements:**
- Local machine displays cross-screen animation
- Animation visible behind desktop icons
- Desktop icons remain clickable
- Taskbar remains functional

✅ **Performance Requirements:**
- Server CPU <5% (same as with remote-only)
- Network bandwidth <1 MB/s (no change)
- Local machine CPU <15% (same as regular wallpaper)
- Memory <200MB per animation

✅ **Quality Requirements:**
- No visual glitches or flicker
- Smooth 30 FPS playback
- ±50ms sync tolerance maintained
- Animation transition timing respected

✅ **Compatibility Requirements:**
- Works on Windows 10 and 11
- Compatible with multi-monitor setups
- Works with animation sequences

---

## Next Steps

**DECISION MADE: Use Direct2D**

Direct2D is the correct solution for this problem. It's:
- Purpose-built for bitmap rendering
- Hardware-accelerated
- Standard Windows API
- Low risk (5-7 hours, manageable for MVP)
- Best for long-term scalability

### Implementation Plan:

1. **Create ComposedAnimationRenderer.cs** (2-3 hours)
   - Reuse existing CompositionRenderer for frame composition
   - Add Direct2D rendering pipeline
   - Receive AnimationMetadata and TimingSync via gRPC

2. **Add Direct2D Interop** (1-2 hours)
   - P/Invoke declarations for Direct2D APIs
   - GDI → Direct2D bitmap conversion
   - Render target management

3. **Register Local Client** (30 min)
   - Update AnimationDistributor to send to local client
   - Wire gRPC messages to local client

4. **Update MainWindowViewModel** (30 min)
   - Create local animation client on server start
   - Subscribe to timing sync messages

5. **Testing & Validation** (1-2 hours)
   - Local animation alone
   - Local + remote clients
   - Performance validation

---

## Related Documentation

- **DISTRIBUTED_ANIMATION_SYSTEM.md** - Phases 1-4 implementation (gRPC system)
- **OpenIssues.md** - Issue #2 tracking (cross-screen local frame display)
- **CrossScreenSpanningDesign.md** - Old centralized system (being replaced)

---

**Created:** 2025-11-03
**Author:** Development Team
**Status:** Awaiting decision on LibVLC vs Direct2D

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

## Implementation Option 1: LibVLC-Based Renderer (RECOMMENDED MVP)

### Architecture

```csharp
// Server sends AnimationStart message to local client
// Local client receives and creates renderer

public class ComposedAnimationRenderer : IWallpaperRenderer
{
    private LibVLC _libVLC;
    private VideoWallpaperRenderer _animationRenderer;
    private ImageWallpaperRenderer _backgroundRenderer;

    public async Task InitializeAsync(string animationPath, BackgroundConfig bg)
    {
        // Same initialization as remote clients
        _animationRenderer = new VideoWallpaperRenderer(_libVLC);
        _backgroundRenderer = new ImageWallpaperRenderer(_libVLC);

        // Compose and display locally
        await ComposeAndDisplay();
    }

    private async Task ComposeAndDisplay()
    {
        // For each frame received via AnimationTimingSync:
        while (isPlaying)
        {
            // Get animation frame via LibVLC
            var animFrame = _animationRenderer.GetCurrentFrame();

            // Get background via LibVLC
            var bgFrame = _backgroundRenderer.GetCurrentFrame();

            // Composite frames
            var composed = ComposeLayers(bgFrame, animFrame);

            // Display directly via WorkerW (no bitmap conversion needed!)
            await _wallpaperWindow.RenderFrame(composed);
        }
    }
}
```

### Why LibVLC Works

1. **Native Rendering** - LibVLC talks directly to GPU, not through Windows Forms
2. **Proven Architecture** - We already use it for VideoRenderer, ImageRenderer
3. **Composition Built-in** - Can layer images/videos natively
4. **WorkerW Compatible** - LibVLC native windows parent correctly
5. **No Bitmap Overhead** - Direct GPU-to-screen rendering

### Implementation Steps

1. **Create ComposedAnimationRenderer class**
   - Similar to CompositionRenderer but receives gRPC AnimationMetadata
   - Creates local instances of VideoWallpaperRenderer and ImageWallpaperRenderer
   - Composes frames and displays via WorkerW

2. **Register local client in AnimationDistributor**
   - When server starts, register local machine as animation client
   - LocalClientId = "localhost" or "127.0.0.1"
   - When animation starts, send AnimationStart to local client too

3. **Wire gRPC messages for local client**
   - Local client listens to same gRPC streams as remote clients
   - Same StartClientAnimation RPC
   - Same BroadcastAnimationSync RPC

4. **Update MainWindowViewModel**
   - Create local animation client when server starts
   - Register with AnimationDistributor
   - Start receiving gRPC messages like any other client

### Files to Create

**New File: ComposedAnimationRenderer.cs** (~300 lines)
```
WaBiBaBuSy.WallpaperEngine/Renderers/ComposedAnimationRenderer.cs

├─ Constructor: Takes AnimationMetadata from gRPC
├─ InitializeAsync: Load animation file + background
├─ OnRenderFrame: Composite and display frames (called by gRPC timing messages)
├─ PlayAsync/PauseAsync/StopAsync: Control playback
└─ DisposeAsync: Cleanup resources
```

### Files to Modify

**AnimationDistributor.cs** (~10 lines)
```
- In ctor or StartSequentialAnimationAsync:
  - Detect local client (configuration or hardcoded)
  - Add local client to animation targets
  - Send AnimationStart message to local client gRPC stream
```

**MainWindowViewModel.cs** (~30 lines)
```
- When server starts (_service.IsServerRunning):
  - Create local animation client
  - Register with AnimationDistributor
  - Subscribe to gRPC messages
```

**CrossScreenConfig.cs** (~5 lines)
```
- Add: UseUnifiedGrpcRendering = true (default)
- When true: Use gRPC-based rendering (new)
- When false: Use old composition+bitmap system (deprecated)
```

### Effort Estimate

- Design: 30 minutes
- Implementation: 3-4 hours
- Integration: 30 minutes
- Testing: 1 hour
- **Total: 4-6 hours**

### Risk Level: **LOW**

- ✅ Uses proven LibVLC technology
- ✅ Code is extension of existing renderers
- ✅ No new Win32 APIs or complex architecture
- ✅ Can fall back to old system if issues
- ✅ Remote clients unaffected

### Expected Outcome

```
Before (Broken):
┌─ Server composes frames
├─ Tries to display locally
└─ ❌ Windows Forms incompatible

After (Working):
┌─ Server broadcasts AnimationStart to local client
├─ Local client renders via ComposedAnimationRenderer (LibVLC)
├─ LibVLC handles rendering natively
└─ ✅ Wallpaper displays correctly on local machine
```

---

## Implementation Option 2: Direct2D Native Renderer (Advanced)

### Architecture

```csharp
public class Direct2DCompositionRenderer : IWallpaperRenderer
{
    private ID2D1Factory _d2dFactory;
    private ID2D1HwndRenderTarget _renderTarget;
    private ID2D1Bitmap _animationBitmap;
    private ID2D1Bitmap _backgroundBitmap;

    public async Task InitializeAsync(string animationPath, BackgroundConfig bg)
    {
        // Create Direct2D render target for wallpaper window
        _renderTarget = _d2dFactory.CreateHwndRenderTarget(wallpaperHwnd);

        // Load animation and background as Direct2D bitmaps
        _animationBitmap = LoadBitmapFromFile(animationPath);
        _backgroundBitmap = LoadBitmapFromFile(bg.ImagePath);
    }

    private void OnRenderFrame()
    {
        _renderTarget.BeginDraw();

        // Draw background
        _renderTarget.DrawBitmap(_backgroundBitmap);

        // Draw animation layer
        _renderTarget.DrawBitmap(_animationBitmap, opacity: 1.0f);

        _renderTarget.EndDraw();
    }
}
```

### Why Direct2D is Better

1. **Pure Native** - No managed framework, direct GPU access
2. **Best Performance** - Hardware-accelerated composition
3. **Full Control** - Can implement custom blending, effects
4. **Scales Best** - Direct2D designed for this use case
5. **Lower Overhead** - No LibVLC translation layer

### Trade-offs

| Aspect | LibVLC | Direct2D |
|--------|--------|----------|
| **Performance** | Good | Better |
| **Complexity** | Simple | High |
| **Risk** | Low | Medium |
| **Dev Time** | 4-6h | 8-10h |
| **Maintenance** | Proven lib | Custom code |
| **Async Support** | Built-in | Manual |

### When to Use Direct2D

- Post-MVP optimization
- When performance critical
- When scaling to 50+ clients
- When custom effects needed
- After validating architecture with LibVLC

---

## Decision Matrix

| Criteria | LibVLC | Direct2D |
|----------|--------|----------|
| **MVP Timeline** | ✅ Fits | ❌ Tight |
| **Risk** | ✅ Low | ⚠️ Medium |
| **Code Reuse** | ✅ High | ❌ Low |
| **Performance** | ✅ Good | ✅ Better |
| **Proven Tech** | ✅ Yes | ⚠️ New |
| **Maintenance** | ✅ Easy | ⚠️ Complex |
| **Long-term** | ⚠️ OK | ✅ Better |

**Recommendation:** **Use LibVLC for MVP, migrate to Direct2D post-MVP**

---

## Implementation Checklist (LibVLC Option)

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

1. **Approve Decision:**
   - LibVLC (Option 1) - Ready to implement
   - Direct2D (Option 2) - Plan for post-MVP

2. **If LibVLC Approved:**
   - Create ComposedAnimationRenderer.cs
   - Integrate with AnimationDistributor
   - Test end-to-end

3. **If Direct2D Approved:**
   - Research Direct2D API
   - Create prototype renderer
   - Plan integration

---

## Related Documentation

- **DISTRIBUTED_ANIMATION_SYSTEM.md** - Phases 1-4 implementation (gRPC system)
- **OpenIssues.md** - Issue #2 tracking (cross-screen local frame display)
- **CrossScreenSpanningDesign.md** - Old centralized system (being replaced)

---

**Created:** 2025-11-03
**Author:** Development Team
**Status:** Awaiting decision on LibVLC vs Direct2D

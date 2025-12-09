# Animation Layer Diagnostics - 2025-12-08

**Status:** ✅ Detailed logging added to identify why animation layer returns null
**Build:** 0 errors
**Purpose:** Debug why GIFs don't render in Direct2D composition

---

## The Problem

Current logs show:
```
[Composition] Animation layer returned null, returning background only
```

This means `AnimationLayerRenderer.RenderForScreen()` is returning `null` instead of a bitmap with the animation. **Why?**

There are several reasons this could happen:
1. Animation not initialized properly
2. Animation position calculations wrong (not overlapping screen)
3. Animation frame retrieval returning null
4. Visible region calculations wrong

---

## New Diagnostic Logs

### 1. Initialization - What animation is configured?

```
[AnimLayer] Animation layer initialized: Size=2900x1627, StartPosition=-2900,406,
  RendererType=GifWallpaperRenderer, AnimationPath=C:\path\to\file.gif
```

**Check:**
- ✅ Is animation path correct?
- ✅ Is renderer type correct (GifWallpaperRenderer)?
- ✅ Are dimensions calculated (not 0x0)?
- ✅ Is start position correct (negative X = off-screen left)?

### 2. Position Updates - Is animation moving?

```
[AnimLayer] Position updated: X=100 (was 0), elapsed=0.5s, pixelsPerSecond=500, AnimWidth=2900
[AnimLayer] Position updated: X=200 (was 100), elapsed=1.0s, pixelsPerSecond=500, AnimWidth=2900
```

**Check:**
- ✅ Is X position changing (was/is different)?
- ✅ Does the rate of change match pixelsPerSecond?
- ✅ Is animation moving across screen (going from negative to positive X)?

### 3. Visibility Check - Is animation overlapping the screen?

**If visible:**
```
[AnimLayer] Rendering animation for screen 0: ScreenPos=(0,0), AnimPos=(100,406), AnimSize=2900x1627
```

**If NOT visible:**
```
[AnimLayer] Animation not visible on screen 0: AnimX=100, AnimWidth=2900, ScreenX=0, ScreenWidth=2560
```

**Check if NOT visible:**
- Is AnimX beyond the screen width? (e.g., AnimX=5000, ScreenWidth=2560)
- Is the animation completely off-screen?

### 4. Region Intersection - Do the bounds actually overlap?

```
[AnimLayer] Animation bounds: {X=-2900,Y=406,Width=2900,Height=1627},
            Screen bounds: {X=0,Y=0,Width=2560,Height=1440}
[AnimLayer] Visible region: {X=0,Y=406,Width=0,Height=1021}
```

**Check:**
- Is visible region empty or has zero width?
- Do the rectangles actually overlap?

### 5. Frame Retrieval - Is GetAnimationFrame returning a frame?

**If successful:**
```
[AnimLayer] Got animation frame: 2560x1440, drawing to bitmap
[AnimLayer] Animation frame drawn to bitmap
[AnimLayer] Returning composed animation bitmap for screen 0: 2560x1440
```

**If frame retrieval failed:**
```
[AnimLayer] GetAnimationFrame returned null - animation will not be visible!
```

---

## Diagnostic Decision Tree

```
Are you seeing logs with [AnimLayer] prefix?
├─ NO
│  └─ Animation layer not initialized or not being called
│
└─ YES
   └─ Check initialization log:
      ├─ AnimationPath = null or empty?
      │  └─ Animation file path not set
      ├─ RendererType = GifWallpaperRenderer?
      │  └─ YES - Good, GIF is loaded
      │  └─ NO - Wrong file type
      └─ Size = 0x0?
         └─ Animation dimensions not calculated

   └─ Check position update logs:
      ├─ X position NOT changing?
      │  └─ Animation timestamp not updating
      │  └─ Or pixelsPerSecond = 0
      └─ X position changing but still negative?
         └─ Animation hasn't reached screen yet (normal at start)

   └─ Check visibility:
      ├─ "Animation not visible on screen"?
      │  └─ Animation X position outside screen bounds
      │  └─ Check: Is AnimX + AnimWidth > ScreenX?
      │  └─ Check: Is AnimX < ScreenX + ScreenWidth?
      └─ Visible but "Visible region is empty"?
         └─ Rectangles don't actually overlap
         └─ Check animation Y position vs screen Y

   └─ Check frame retrieval:
      ├─ "GetAnimationFrame returned null"?
      │  └─ GifWallpaperRenderer.GetFrameAtPosition() failing
      │  └─ Check [AnimFrame] logs separately
      └─ Frame is returned?
         └─ Animation should be rendered
         └─ If not visible, check composition layer
```

---

## What To Look For

### Scenario 1: Animation Never Appears in Logs

**Problem:** Animation layer not being called
**Check:**
- Is animation initialized? (Look for `[AnimLayer] Animation layer initialized`)
- Is AnimationLayerRenderer being used? (Check renderer factory code)

### Scenario 2: Animation Initialized But Not Visible

**Logs to check:**
1. Initialization - Is AnimationPath set correctly?
2. Position updates - Is X changing?
3. Visibility check - Is animation overlapping screen?

**Example bad output:**
```
[AnimLayer] Animation layer initialized: Size=2900x1627, StartPosition=-2900,406
[AnimLayer] Position updated: X=100 (was 0), ...
[AnimLayer] Animation not visible on screen 0: AnimX=100, AnimWidth=2900, ScreenX=0, ScreenWidth=2560
```

In this case:
- AnimX = 100 (animation at X=100)
- ScreenX = 0, ScreenWidth = 2560 (screen goes from 0 to 2560)
- AnimWidth = 2900

**The issue:** Animation is **very wide** (2900px) but positioned at only 100px. The visible region calculation might be wrong, OR the screen is too small to show the animation at its native aspect ratio.

### Scenario 3: Animation Visible But Frame is Null

**Logs to check:**
```
[AnimLayer] Rendering animation for screen 0: ...
[AnimLayer] Visible region: ...
[AnimLayer] GetAnimationFrame returned null
```

**This means:**
- Animation geometry is correct
- But `GetAnimationFrame()` failed
- Check the `[AnimFrame]` logs for why GifWallpaperRenderer.GetFrameAtPosition() returned null

---

## Key Metrics to Monitor

| Metric | Expected | Problem if |
|--------|----------|-----------|
| `AnimationPath` | Not empty | Empty or null |
| `AnimSize` | > 0x0 | 0x0 or very small |
| `StartPosition` | Negative X | Not negative (off-screen) |
| `X position` | Changes over time | Stays constant |
| `Visible region` | Not empty | Empty or zero width/height |
| `AnimationFrame` | Not null | Null (frame retrieval failed) |

---

## Example Successful Log Sequence

When everything works:

```
[AnimLayer] Animation layer initialized: Size=2900x1627, StartPosition=-2900,406, RendererType=GifWallpaperRenderer
[AnimLayer] Position updated: X=-2800 (was -2900), elapsed=0.1s, pixelsPerSecond=100, AnimWidth=2900
[AnimLayer] Rendering animation for screen 0: ScreenPos=(0,0), AnimPos=(-2800,406), AnimSize=2900x1627
[AnimLayer] Animation bounds: {X=-2800,Y=406,Width=2900,Height=1627}, Screen bounds: {X=0,Y=0,Width=2560,Height=1440}
[AnimLayer] Visible region: {X=0,Y=406,Width=100,Height=1034}
[AnimLayer] Got animation frame: 2560x1440, drawing to bitmap
[AnimLayer] Animation frame drawn to bitmap
[AnimLayer] Returning composed animation bitmap for screen 0: 2560x1440
```

This would result in the animation layer returning a bitmap, which would then be composited with the background.

---

## Testing Instructions

1. **Build project** (already done ✅)
2. **Run application**
3. **Load a GIF animation**
4. **Click "Apply Via Direct2D"**
5. **Check logs for `[AnimLayer]` entries**
6. **Follow the diagnostic tree** to identify which step is failing

---

## Logging Locations

All new logs use the `[AnimLayer]` prefix for easy filtering:

**In logs, search for:**
```
[AnimLayer]
```

This will show you:
- Initialization details
- Position updates
- Visibility checks
- Visible region calculations
- Frame retrieval results
- Final bitmap return

---

## Next Steps After Diagnosis

Once you identify where the logs stop:

1. **Initialization issue** → Check animation configuration, file path
2. **Position issue** → Check timestamp calculations, pixelsPerSecond
3. **Visibility issue** → Check animation dimensions vs screen size
4. **Frame retrieval issue** → Check `[AnimFrame]` logs, GifWallpaperRenderer

Each issue has a different root cause that needs different fixes.

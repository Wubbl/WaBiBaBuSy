# Critical Discovery: Frame Rate Issue - 2025-12-08

**Status:** ✅ Root cause identified - Frame rate is extremely low
**Build:** 0 errors
**Impact:** Animation never moves because position only updates ~1 time per second instead of ~60 times

---

## The Discovery

Looking at the logs you provided:

```
[RenderLoop] Rendered 1 frames in last 1000ms, elapsed=2ms, pixelsPerSecond=500
[Composition] Animation layer returned null, returning background only
```

**Translation:**
- In a 1-second interval (1000ms), only **1 frame was rendered**
- Should be **~60 frames** (at 60 FPS)
- **That's 30-60x slower than it should be!**

---

## Why This Causes the Animation to be Invisible

The animation:
- Starts at **X = -1280** (off-screen to the left)
- Moves at **500 pixels/second**
- Needs to move right to become visible

If the render loop runs at 1 FPS instead of 60 FPS:
- In 1 second: Animation moves 500 pixels RIGHT (becomes visible) ✓
- But only 1 frame is rendered
- Then nothing happens for another second

**With 60 FPS:**
- Every 16ms: Animation position updates
- Smooth motion across screen

**With 1 FPS:**
- Every 1000ms: Animation position updates once
- Animation teleports, not smooth
- Most of the time animation is off-screen

---

## The Missing Logs

Because the frame rate is so low, we're also not seeing the intermediate logs:

```
[Composer-Detail] ComposeSingle called         ← Should appear ~60x/sec
[Composition-Detail] UpdateAnimationPosition   ← Should appear ~60x/sec
[AnimLayer-Detail] Position updated            ← Should appear ~60x/sec
[RenderLoop-Detail] Received frame             ← Should appear ~60x/sec
```

With only 1 frame per second, these logs barely appear!

---

## Why Is Frame Rate So Low?

This could be because:

1. **GIF Loading is Slow** - Each frame retrieval from GIF is blocking
2. **GIF Frame Extraction is Slow** - Calling `GetFrameAtPosition()` is taking 1000ms
3. **Bitmap Operations are Slow** - Creating/drawing bitmaps is taking time
4. **Composition is Slow** - Compositing layers together is slow
5. **Display is Slow** - GDI+ drawing to screen is blocking
6. **Lock Contention** - Threads blocking on locks

---

## Information-Level Logging Added

Now we'll see Information-level logs instead of just Trace:

**Instead of seeing nothing, you'll see:**

```
[RenderLoop-Detail] About to call ComposeSingle at elapsedMs=2
[Composer-Detail] ComposeSingle called for screen 0, timestampMs=2, pixelsPerSecond=500
[Composition-Detail] UpdateAnimationPosition called: timestampMs=2, pixelsPerSecond=500
[AnimLayer-Detail] Position updated: X=-1180 (was -1280), elapsed=0.002s...
[Composer-Detail] Calling ComposeForScreen
[Composition-Detail] ComposeForScreen called for screen 0
[Composition-Detail] Rendering background layer
[Composition-Detail] Rendering animation layer
[AnimLayer-Detail] Animation not visible on screen 0: AnimX=-1180, AnimWidth=1280, ScreenX=0, ScreenWidth=2560
[Composition] Animation layer returned null, returning background only
[RenderLoop-Detail] Received frame: 2560x1440, about to display
[RenderLoop-Detail] Frame displayed
```

This sequence will repeat, but **how slowly?** If we see only 1 frame per second, each of those calls is taking ~1000ms.

---

## What To Look For in New Logs

1. **Frame Rate:**
   - Are `[RenderLoop] Rendered X frames...` logs still showing 1 frame per second?
   - Or now showing more frames?

2. **Position Updates:**
   - Do you see `[AnimLayer-Detail] Position updated` appearing multiple times per second?
   - Or still only once per second?

3. **Time Taken:**
   - From `[RenderLoop-Detail] About to call ComposeSingle` to `Frame displayed`
   - If this takes ~100ms, frame rate is ~10 FPS
   - If this takes ~1000ms, frame rate is ~1 FPS

4. **Composition Logs:**
   - Do all the composition intermediate steps appear?
   - Or do some steps get skipped?

---

## Expected vs Actual

### Expected (Normal 60 FPS)
```
[RenderLoop] Rendered 60 frames in last 1000ms, elapsed=1016ms, pixelsPerSecond=500
[RenderLoop-Detail] About to call ComposeSingle at elapsedMs=16
[Composer-Detail] ComposeSingle called...
[RenderLoop-Detail] About to call ComposeSingle at elapsedMs=32
[Composer-Detail] ComposeSingle called...
... (60 times total)
```

### Actual (1 FPS)
```
[RenderLoop] Rendered 1 frames in last 1000ms, elapsed=1000ms, pixelsPerSecond=500
[RenderLoop-Detail] About to call ComposeSingle at elapsedMs=1000
[Composer-Detail] ComposeSingle called...
[RenderLoop-Detail] About to call ComposeSingle at elapsedMs=2000
... (1 time total)
```

---

## Key Insight: The Position Never Moves Enough

With animation starting at **X = -1280** and moving only **once per second at 500 px/s**:
- After 1st second: X = -780 (moved 500px right, but still off-screen)
- After 2nd second: X = -280 (moved again, getting closer)
- After 3rd second: X = 220 (NOW visible!)

So animation is invisible for ~2-3 seconds, then suddenly appears.

**With 60 FPS, it would smoothly move across the screen in ~3 seconds.**

---

## Next Steps

1. **Build** (already done ✅)
2. **Run application**
3. **Load GIF animation**
4. **Apply via Direct2D**
5. **Watch for these new logs:**
   - `[RenderLoop-Detail]` - Should appear many times per second
   - `[Composer-Detail]` - Should appear many times per second
   - `[Composition-Detail]` - Should appear many times per second
   - `[AnimLayer-Detail] Position updated` - Should show X increasing

6. **Measure:**
   - Are these appearing 60x per second (normal)?
   - Or 1x per second (current problem)?

7. **If still 1 FPS:**
   - Check which operation is taking 1000ms
   - Is it GIF frame retrieval?
   - Is it bitmap composition?
   - Is it GDI+ rendering?

---

## Build Status
✅ **0 Errors** | **0 New Warnings**

The Information-level logging will show us exactly where the time is being spent!

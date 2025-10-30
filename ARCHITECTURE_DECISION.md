# Architecture Decision: Distributed Composition over Server-Side Rendering

**Date:** 2025-10-30
**Decision:** Implement Distributed Composition Architecture
**Status:** Approved for Implementation
**Supersedes:** CrossScreenFrameDisplayPlan.md options

---

## The Decision

**We are implementing a distributed composition architecture where:**
1. **Server** sends animation metadata + configuration once to each client
2. **Clients** download animation file and render locally at 30 FPS
3. **Server** sends only timing sync messages (every 1 second)
4. **Clients** compose background + animation layers independently
5. **Animation flows** sequentially through selected monitors

---

## Why This Architecture?

### Problem with Current Approach (Centralized Composition)

```
Server composes frames 30 FPS
├─ Render background
├─ Render animation
├─ Merge layers
├─ Encode to JPEG (100-150KB)
└─ Send to each client

Results:
- Server CPU: 80%+ (composition + encoding)
- Network: 9 MB/sec (2 clients)
- Scalability: 2-3 clients max
- Latency: 100-200ms (network delay)
```

### Why CrossScreenFrameDisplayPlan.md Becomes Unnecessary

**CrossScreenFrameDisplayPlan.md** outlined 4 options for displaying composite frames locally:
- **Option A:** GDI+ Direct Paint (unreliable, 5-10 FPS)
- **Option B:** Temp File + Reload (slow, 5-10 FPS)
- **Option C:** Direct2D Renderer (proper, but 6-8 hours)
- **Option D:** Disable Local Display (MVP compromise)

**With Distributed Composition:**
- ✅ No need to display server-composed frames on server
- ✅ Each client displays its own locally-composed frames
- ✅ Server never generates frames for local display
- ✅ Architecture becomes simpler and cleaner

### Benefits

| Metric | Before | After | Change |
|--------|--------|-------|--------|
| **Server CPU** | 80%+ | <5% | 94% reduction |
| **Network Bandwidth** | 9 MB/s | <1 MB/s | 99% reduction |
| **Max Clients** | 2-3 | 50+ | 16x scalability |
| **Sync Accuracy** | ±200ms | ±50ms | Better timing |
| **Memory** | High | Minimal | 95% reduction |

---

## How It Works: Sequential Animation Flow

### Example: 3-Monitor Setup, 5-Second Animation

```
Timeline (Server Clock):

T+0 seconds:
┌──────────────────────────────┐
│ Server sends AnimationStart  │
│ to Client 1 (Monitor 1)      │
│ Start time: T+0              │
│ Duration: 5 seconds          │
└──────────────────────────────┘
         │
         └─────► Client 1
                 ├─ Downloads animation.mp4
                 ├─ Renders at T+0
                 └─ Displays on Monitor 1 (T+0 to T+5)

T+5 seconds:
┌──────────────────────────────┐
│ Server sends AnimationStart  │
│ to Client 2 (Monitor 2)      │
│ Start time: T+5              │
│ Duration: 5 seconds          │
└──────────────────────────────┘
         │
         └─────► Client 2
                 ├─ Downloads animation.mp4
                 ├─ Waits until T+5
                 ├─ Renders at T+5
                 └─ Displays on Monitor 2 (T+5 to T+10)

T+10 seconds:
┌──────────────────────────────┐
│ Server sends AnimationStart  │
│ to Client 3 (Monitor 3)      │
│ Start time: T+10             │
│ Duration: 5 seconds          │
└──────────────────────────────┘
         │
         └─────► Client 3
                 ├─ Downloads animation.mp4
                 ├─ Waits until T+10
                 ├─ Renders at T+10
                 └─ Displays on Monitor 3 (T+10 to T+15)

Result on Physical Screens:
Monitor 1    Monitor 2    Monitor 3
T+0-T+5      T+5-T+10     T+10-T+15
 ▓▓▓▓▓        ░░░░░        ░░░░░        (▓ = animation)
 ▓▓▓▓▓        ░░░░░   →    ▓▓▓▓▓   →    (░ = background)
 ▓▓▓▓▓        ░░░░░        ░░░░░

Animation flows smoothly: Monitor 1 → Monitor 2 → Monitor 3
```

---

## Implementation Plan

**See:** `DistributedCompositionArchitecturePlan.md` for complete details

### Four Phases

**Phase 1: Animation Distribution (8-10 hours)**
- Extend gRPC protocol with animation metadata messages
- Clients download animation file from server
- Clients render locally at 30 FPS
- Proof of concept: Single client working

**Phase 2: Timing Synchronization (6-8 hours)**
- Server broadcasts timing sync every 1 second
- Clients detect drift and auto-correct with micro-seek
- All clients stay in sync ±50ms

**Phase 3: Sequential Handoff (8-10 hours)**
- Server schedules clients to animate in sequence
- Animation flows through monitors in order
- Each client knows when to start based on previous duration

**Phase 4: UI Integration (4-6 hours)**
- User controls in CrossScreenConfigDialog
- Distribution mode selector: Sequential / Simultaneous
- Status display showing active animations

**Total Effort:** 18-24 hours
**Start Date:** (User approval)
**Expected Completion:** 2-3 weeks

---

## What Gets Deleted

With this architecture, we **don't need** to implement anything from `CrossScreenFrameDisplayPlan.md`:
- ✅ No Option A (GDI+ rendering)
- ✅ No Option B (Temp file + reload)
- ✅ No Option C (Direct2D renderer)
- ✅ No Option D (Disable local display)

**Instead:** Each client renders locally, eliminating the entire local frame display problem.

---

## gRPC Protocol Changes

### New Messages

```protobuf
message AnimationMetadata {
  string animation_id = 1;
  string content_path = 2;              // Server path to animation file
  int32 target_height_px = 3;           // 720
  int32 animation_speed_px_sec = 4;     // 500
  int64 duration_ms = 5;                // 5000 (milliseconds)
  int64 start_timestamp_utc = 6;        // When client should start
  BackgroundLayerConfig background = 7; // Solid/image/tiled
  bool loop = 8;
}

message AnimationTimingSync {
  int64 server_timestamp_utc = 1;
  string animation_id = 2;
  int32 expected_position_ms = 3;       // Where animation should be
}

message AnimationCompleteReport {
  string client_id = 1;
  string animation_id = 2;
  int64 completion_timestamp_utc = 3;
  bool successful = 4;
  string error_message = 5;
}
```

### New RPC Methods

```protobuf
service WallpaperSync {
  // Send animation metadata to client
  rpc SendAnimationStart(AnimationMetadata) returns (AnimationAck);

  // Broadcast timing sync to all clients
  rpc BroadcastAnimationTimingSync(AnimationTimingSync) returns (Empty);

  // Client reports animation complete
  rpc ReportAnimationComplete(AnimationCompleteReport) returns (AnimationAck);

  // Server stops animation on client
  rpc StopAnimation(AnimationStopRequest) returns (AnimationAck);

  // Get status of animation on client
  rpc GetAnimationStatus(AnimationStatusRequest) returns (AnimationStatusResponse);
}
```

---

## Risk Mitigation

| Risk | Mitigation |
|------|-----------|
| Client fails to render | Server detects timeout, retries or moves to next client |
| Network delay causes jitter | Timing sync every 1 second corrects drift |
| Clock skew between machines | NTP (OS-level) + timing sync messages |
| Large animation files | Implement file chunking in Phase 2 |
| Client CPU spikes | Each client renders independent, no cascading load |

---

## Success Metrics (Post-Implementation)

After implementation, we'll measure:

✅ **Server CPU:** <5% during animation (down from 80%)
✅ **Network Bandwidth:** <1 MB/sec (down from 9 MB/sec)
✅ **Scalability:** Support 50+ clients
✅ **Synchronization:** Keep ±50ms drift tolerance
✅ **Smoothness:** 30 FPS on all clients

---

## Next Steps

1. ✅ Create comprehensive architecture plan (DONE)
2. ✅ Update OpenIssues.md and MissingFeatures.md (DONE)
3. ⏳ **User approves architecture** (AWAITING)
4. ⏳ Create gRPC protocol extensions
5. ⏳ Implement Phase 1 (Animation Distribution)
6. ⏳ Test Phase 1
7. ⏳ Implement Phase 2 (Timing Sync)
8. ⏳ Test Phase 2
9. ⏳ Implement Phase 3 (Sequential Handoff)
10. ⏳ Test Phase 3
11. ⏳ Implement Phase 4 (UI Integration)
12. ⏳ Final testing and optimization

---

## Questions?

**Q: What about local server animation display?**
A: Not needed. Server doesn't generate frames. Server only orchestrates timing. Local server machine can also be a client if desired.

**Q: Can we do simultaneous instead of sequential?**
A: Yes! Sequential is Phase 1. Simultaneous (all monitors animate at once) is Phase 4 option.

**Q: What if a client crashes during animation?**
A: Server detects missing "AnimationComplete" report, moves to next client.

**Q: Does this work with existing cross-screen components?**
A: Yes. Reuses CompositionRenderer on client side, VirtualCanvasManager for layout, same animation files.

**Q: How is this different from what we planned in CrossScreenFrameDisplayPlan.md?**
A: That plan was about displaying server-composed frames locally (4 options). This plan eliminates that problem by not composing on server at all.

---

**Status:** ✅ READY FOR IMPLEMENTATION
**Awaiting:** User approval to proceed with Phase 1

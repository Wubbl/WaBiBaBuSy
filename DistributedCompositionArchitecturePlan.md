# Distributed Composition Architecture Plan
## Client-Side Animation Composition with Server-Coordinated Timing

**Date Created:** 2025-10-30
**Status:** Planning Phase - Awaiting Implementation
**Related Documents:** `CrossScreenFrameDisplayPlan.md`, `MissingFeatures.md` Issue #3
**Priority:** HIGH
**Effort Estimate:** 18-24 hours
**Dependencies:** Requires completion before tackling CrossScreenFrameDisplayPlan.md solutions

---

## Executive Summary

**Current Problem:**
- Server generates all composite frames (background + animation) at 30 FPS
- Server encodes frames to JPEG and broadcasts to all clients
- **Results in:** 80%+ server CPU, 9 MB/sec network traffic per 2 clients, poor scalability

**Proposed Solution:**
- Server sends animation file + metadata (background, speed, duration) to each client
- Each client composes and renders locally at 30 FPS
- Server sends only timing synchronization messages (once per second)
- **Results in:** <5% server CPU, <1 MB/sec total network, scales to 50+ clients

**Key Innovation: Sequential Animation Distribution**
- Animation plays on Client 1 from T+0 to T+5 (duration 5 seconds)
- Server detects completion, sends animation to Client 2 with start time T+5
- Animation flows seamlessly across selected monitors in order
- Each client independently composes background + animation layers

---

## Problem Statement

### Current Centralized Architecture

```
Server (Rendering Bottleneck)
├─ Load animation file (MP4, GIF)
├─ For each frame (30 FPS):
│  ├─ Compose background layer
│  ├─ Compose animation layer
│  ├─ Merge layers into single Bitmap
│  ├─ Encode to JPEG (~100-150KB)
│  └─ Send to all selected clients (broadcast)
└─ Repeat 30 times per second

Performance Impact:
- Server CPU: 80%+ (rendering + JPEG encoding dominates)
- Network: 100-150KB × 30 FPS × N clients = 9 MB/sec (2 clients)
- Memory: Multiple frame buffers, JPEG buffers
- Scalability: Adding clients linearly increases server load
```

### Key Issues with Current Approach

1. **Server CPU Bottleneck**
   - Server is single point of rendering for all clients
   - Rendering is CPU-intensive (compositing, JPEG encoding)
   - Can't support more than 2-3 clients without maxing CPU

2. **Network Bandwidth Explosion**
   - Each frame sent to each client
   - 30 frames/sec × 150KB per frame × N clients = massive traffic
   - Two clients = 9 MB/sec sustained (overkill for LAN animation)

3. **Memory Pressure**
   - Frame buffer for each screen maintained on server
   - JPEG encoding buffers for each stream
   - Total memory usage grows with client count

4. **Latency and Reliability**
   - Network delay between server frame generation and client display
   - Frame loss → visible stutters/gaps
   - Requires perfect network conditions

---

## Proposed Solution: Distributed Composition Architecture

### High-Level Architecture

```
┌─────────────────────────────────────────────┐
│         Server (Coordinator)                 │
│  Role: Control timing, schedule animation   │
│                                              │
│  1. Choose animation file                   │
│  2. Calculate duration (e.g., 5 seconds)    │
│  3. Choose client/monitor for playback      │
│  4. Send AnimationStart with metadata       │
│  5. Broadcast timing sync every 1 second    │
│  6. When client finishes, move to next      │
└──────────┬──────────────────────────────────┘
           │
       gRPC streaming
       (~1-10KB/sec)
           │
    ┌──────┴──────┬──────────┬──────────┐
    │             │          │          │
    ▼             ▼          ▼          ▼
┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐
│ Client 1 │ │ Client 2 │ │ Client 3 │ │ Client N │
│          │ │          │ │          │ │          │
│ Receives │ │ Receives │ │ Receives │ │ Receives │
│ animation│ │ animation│ │ animation│ │ animation│
│ file + │ │ file + │ │ file + │ │ file + │
│ metadata │ │ metadata │ │ metadata │ │ metadata │
│          │ │          │ │          │ │          │
│ Composes │ │ Composes │ │ Composes │ │ Composes │
│ locally  │ │ locally  │ │ locally  │ │ locally  │
│ 30 FPS   │ │ 30 FPS   │ │ 30 FPS   │ │ 30 FPS   │
│          │ │          │ │          │ │          │
│ Reports  │ │ Reports  │ │ Reports  │ │ Reports  │
│completion│ │completion│ │completion│ │completion│
└──────────┘ └──────────┘ └──────────┘ └──────────┘
```

### Sequential Animation Playback (Flowing Animation)

```
Timeline (Clock runs on Server, Clients sync every 1 second)

Server Time: 0 ────┬──────────────────────────────┐
                   │                              │
                   ▼                              ▼
          Animation Start on Client 1   Animation Start on Client 2
               (Duration: 5 sec)             (Start at T+5, Duration: 5 sec)
               ▼                            ▼
         ┌─────────────────────┐          ┌─────────────────────┐
         │ Client 1            │          │ Client 2            │
         │ Rendering           │          │ Waiting for start   │
         │ (T+0 to T+5)        │          │ (Idle until T+5)    │
         │                     │          │                     │
         │ Frame rendered      │          │ Frame rendered      │
         │ and displayed       │          │ and displayed       │
         │ at 30 FPS locally   │          │ at 30 FPS locally   │
         └─────────────────────┘          └─────────────────────┘

Physical Screen Layout:
  Monitor 0            Monitor 1            Monitor 2
  (Client 1)           (Client 2)           (Client 3)
     ↓                    ↓                    ↓
  ┌──────┐            ┌──────┐            ┌──────┐
  │ Anim │  ──────▶   │      │  ──────▶   │      │
  │      │            │      │            │      │
  └──────┘            └──────┘            └──────┘
  T+0 to T+5          T+5 to T+10         T+10 to T+15
```

### Benefits Over Current Approach

| Metric | Current | Proposed | Improvement |
|--------|---------|----------|-------------|
| **Server CPU** | 80%+ | <5% | **94% reduction** |
| **Network BW** | 9 MB/s | <1 MB/s | **99% reduction** |
| **Max Clients** | 2-3 | 50+ | **16x better scalability** |
| **Latency** | 100-200ms | 0ms | **No display delay** |
| **Memory** | 200-300MB | <50MB | **95% reduction** |

---

## Detailed Architecture

### Phase 1: Animation Distribution (Server → Clients)

**What Server Sends:**

```protobuf
message AnimationMetadata {
  string animation_id = 1;              // Unique ID (file hash or UUID)
  string content_path = 2;              // Server-side path (e.g., "/Content/animation.mp4")
  int32 target_height_px = 3;           // 720 (clients scale animation to this height)
  int32 animation_speed_px_sec = 4;     // 500 (pixels per second horizontal movement)
  int64 duration_ms = 5;                // 5000 (animation duration in milliseconds)
  int64 start_timestamp_utc = 6;        // When client should start (Unix ms)
  BackgroundLayerConfig background = 7; // Background: solid color/image/tiled
  bool loop = 8;                        // true = repeat animation, false = stop after one play
  int32 target_monitor_index = 9;       // Which physical monitor (for screen bounds)
}
```

**Client-Side Processing:**

```csharp
// 1. Receive AnimationMetadata
OnReceiveAnimationStart(metadata) {
    // 2. Download animation file from server (if not cached)
    var filePath = await DownloadAnimationFile(metadata.content_path, metadata.animation_id);

    // 3. Load animation (reuse existing CompositionRenderer system)
    var animationRenderer = CreateAnimationRenderer(filePath, metadata.target_height_px);

    // 4. Pre-calculate animation duration
    var actualDuration = animationRenderer.GetDuration();

    // 5. Wait until start_timestamp_utc
    var delayMs = metadata.start_timestamp_utc - SystemClock.UtcNow().Ticks / 10000;
    if (delayMs > 0) {
        await Task.Delay((int)delayMs);
    }

    // 6. Start local 30 FPS rendering loop
    StartLocalRenderingLoop(metadata);
}

// 7. In render loop (every 33ms):
OnRenderFrame() {
    // Get current timestamp from system clock (NOT waiting for server)
    var currentTime = SystemClock.UtcNow();
    var elapsedMs = (currentTime - _animationStartTime).TotalMilliseconds;

    // Get animation frame at current elapsed time
    var animationFrame = _animationRenderer.GetFrameAtTime(elapsedMs);

    // Compose with background layer (same CompositionRenderer.ComposeForScreen logic)
    var composed = CompositionRenderer.ComposeFrame(
        background: metadata.background,
        animation: animationFrame,
        position: CalculateAnimationPosition(elapsedMs, metadata.animation_speed_px_sec)
    );

    // Display on wallpaper window
    DisplayOnWallpaper(composed);
}

// 8. When animation completes:
OnAnimationComplete() {
    if (metadata.loop) {
        // Reset and start again
        RestartAnimation();
    } else {
        // Report completion to server
        await ReportAnimationComplete(metadata.animation_id, success: true);
    }
}
```

### Phase 2: Timing Synchronization (Server → All Animating Clients)

**Problem Solved:**
- Client clocks can drift from server clock (OS time is not perfectly synchronized across machines)
- After 5 seconds, client might be 50ms ahead or behind target time
- Visible as stuttering or animation jumping

**Solution:**
- Server broadcasts timing sync message to all clients every 1 second
- Clients measure their own time drift and auto-correct with micro-seek

**Timing Sync Message:**

```protobuf
message AnimationTimingSync {
  int64 server_timestamp_utc = 1;       // Current UTC time on server
  string animation_id = 2;              // Which animation this is for
  int32 expected_position_ms = 3;       // Where animation should be at this time
}
```

**Client-Side Sync Handling:**

```csharp
OnReceiveTimingSync(sync) {
    // 1. Measure our local time
    var clientNow = SystemClock.UtcNow();
    var ourTime = (clientNow - _animationStartTime).TotalMilliseconds;

    // 2. Calculate drift
    var drift = ourTime - sync.expected_position_ms;

    // 3. If drift exceeds tolerance, correct
    if (Math.Abs(drift) > 50) {  // 50ms threshold
        _logger.LogWarning("Animation drift detected: {DriftMs}ms, correcting...", drift);

        // Seek to correct position
        _animationRenderer.Seek(sync.expected_position_ms);
    }
}
```

### Phase 3: Sequential Handoff (Animation Moves Between Clients)

**Server Orchestration Logic:**

```csharp
public class AnimationScheduler {
    public async Task StartSequentialAnimation(
        CrossScreenConfig config,
        List<ClientInfo> selectedClients) {

        // 1. Get animation properties
        var animationFile = config.Animation.AnimationPath;
        var duration = GetAnimationDuration(animationFile);  // e.g., 5000ms

        // 2. For each client in selected order
        var currentStartTime = SystemClock.UtcNow().ToUnixTimeMilliseconds();

        foreach (var client in selectedClients) {
            // 3. Create metadata for this client
            var metadata = new AnimationMetadata {
                animation_id = Guid.NewGuid().ToString(),
                content_path = animationFile,
                target_height_px = config.Animation.TargetHeightPx,
                animation_speed_px_sec = config.AnimationSpeedPxPerSecond,
                duration_ms = duration,
                start_timestamp_utc = currentStartTime,
                background = config.Background,
                loop = false,  // Sequential mode: no looping per client
            };

            // 4. Send to client
            _logger.LogInformation("Starting animation on {ClientId} at T+{DelayMs}ms",
                client.ClientId, currentStartTime - now);

            await SendAnimationStart(client, metadata);

            // 5. Schedule next client to start when this one ends
            currentStartTime += duration;
        }

        // 6. Start timing sync loop
        StartTimingSyncBroadcast();
    }

    private async Task StartTimingSyncBroadcast() {
        while (_animationRunning) {
            var animatingClients = GetAnimatingClients();

            foreach (var client in animatingClients) {
                var sync = new AnimationTimingSync {
                    server_timestamp_utc = SystemClock.UtcNow().ToUnixTimeMilliseconds(),
                    animation_id = client.CurrentAnimationId,
                    expected_position_ms = CalculateExpectedPosition(client),
                };

                await SendTimingSync(client, sync);
            }

            await Task.Delay(1000);  // Sync every 1 second
        }
    }
}
```

---

## gRPC Protocol Extensions

### New Messages

```protobuf
message AnimationMetadata {
  string animation_id = 1;
  string content_path = 2;
  int32 target_height_px = 3;
  int32 animation_speed_px_sec = 4;
  int64 duration_ms = 5;
  int64 start_timestamp_utc = 6;
  BackgroundLayerConfig background = 7;
  bool loop = 8;
  int32 target_monitor_index = 9;
}

message AnimationTimingSync {
  int64 server_timestamp_utc = 1;
  string animation_id = 2;
  int32 expected_position_ms = 3;
}

message AnimationCompleteReport {
  string client_id = 1;
  string animation_id = 2;
  int64 completion_timestamp_utc = 3;
  bool successful = 4;
  string error_message = 5;  // If not successful
}

message AnimationStopRequest {
  string animation_id = 1;
}

message AnimationStatusRequest {
  string animation_id = 1;
}

message AnimationStatusResponse {
  string animation_id = 1;
  enum Status {
    IDLE = 0;
    WAITING = 1;         // Waiting for start time
    RENDERING = 2;       // Currently playing
    PAUSED = 3;
    COMPLETED = 4;
    FAILED = 5;
  }
  Status status = 2;
  int32 current_position_ms = 3;  // Where we are in animation
  string error_message = 4;       // If status is FAILED
}
```

### New RPC Methods

```protobuf
service WallpaperSync {
  // Existing RPCs...

  // Distributed animation control:

  // Server → Client: Start animation with metadata
  rpc SendAnimationStart(AnimationMetadata) returns (AnimationAck);

  // Client → Server: Animation finished playing
  rpc ReportAnimationComplete(AnimationCompleteReport) returns (AnimationAck);

  // Server → All Clients: Timing sync (every 1 second)
  rpc BroadcastAnimationTimingSync(AnimationTimingSync) returns (Empty);

  // Server → Client: Stop current animation
  rpc StopAnimation(AnimationStopRequest) returns (AnimationAck);

  // Server ← Client: Get current status of animation
  rpc GetAnimationStatus(AnimationStatusRequest) returns (AnimationStatusResponse);
}

message AnimationAck {
  bool success = 1;
  string message = 2;
}

message Empty {}
```

---

## Configuration Model Update

### CrossScreenConfig Changes

```csharp
public class CrossScreenConfig
{
    // EXISTING FIELDS:
    public BackgroundLayerConfig Background { get; set; }
    public AnimationLayerConfig Animation { get; set; }
    public int AnimationSpeedPxPerSecond { get; set; }
    public List<string> SelectedMonitorIds { get; set; }

    // NEW FIELDS FOR DISTRIBUTED COMPOSITION:
    public bool UseDistributedComposition { get; set; } = true;  // Default: enabled

    // Distribution modes:
    public enum DistributionMode {
        Sequential,          // Animation flows through monitors in order
        Simultaneous,        // All monitors animate at same time
        GroupedSequential,   // Divide monitors into groups, groups animate sequentially
    }
    public DistributionMode Mode { get; set; } = DistributionMode.Sequential;

    // Timing:
    public int TimingSyncIntervalMs { get; set; } = 1000;  // How often to send sync (1 sec)
    public int MaxAllowedClockDriftMs { get; set; } = 50;  // Tolerance before correcting

    // Client-side rendering:
    public int ClientRenderFPS { get; set; } = 30;  // Frames per second on each client
    public bool ClientSideCaching { get; set; } = true;  // Cache animation files locally
}
```

---

## Implementation Phases

### Phase 1: Animation Distribution & Local Composition (8-10 hours)

**Objective:** Enable clients to compose and render animation locally

**Tasks:**
1. [ ] Extend gRPC protocol with `AnimationMetadata` message
2. [ ] Implement `SendAnimationStart()` RPC (server → client)
3. [ ] Create client-side animation loader (download + cache animation file)
4. [ ] Implement local composition on client:
   - Use existing CompositionRenderer system on client side
   - Clients get background config + animation file
   - Clients render frames locally at 30 FPS
5. [ ] Implement `ReportAnimationComplete()` RPC (client → server)
6. [ ] Proof of concept: Single animation on single client working

**Files to Create:**
- `WaBiBaBuSy.Models/Animation/AnimationMetadata.cs`
- `WaBiBaBuSy.Core/Services/ClientAnimationRenderer.cs` (client-side rendering)
- `WaBiBaBuSy.Core/Services/AnimationDistributor.cs` (server-side distribution)

**Files to Modify:**
- `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` (add messages + RPCs)
- `WaBiBaBuSy.Grpc/Services/WallpaperSyncServiceImpl.cs` (implement server RPCs)
- `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs` (implement client RPCs)

**Success Criteria:**
- [ ] Client receives animation file from server
- [ ] Client renders animation locally at 30 FPS
- [ ] Wallpaper displays composite (background + animation)
- [ ] Animation completes and reports to server

**Expected Effort:** 8-10 hours

---

### Phase 2: Timing Synchronization & Drift Correction (6-8 hours)

**Objective:** Keep all clients' animations synchronized to server clock

**Tasks:**
1. [ ] Extend gRPC protocol with `AnimationTimingSync` message
2. [ ] Implement `BroadcastAnimationTimingSync()` RPC (server → all clients)
3. [ ] Implement server-side timing sync scheduler (broadcast every 1 second)
4. [ ] Implement client-side drift detection:
   - Track elapsed time since animation start
   - Receive sync message with expected position
   - Compare: `drift = actualTime - expectedTime`
5. [ ] Implement client-side drift correction:
   - If `|drift| > 50ms`, perform micro-seek to correct position
   - Seek uses existing animation renderer seek capability
6. [ ] Testing: Run on 2-3 clients simultaneously, verify sync

**Files to Create:**
- `WaBiBaBuSy.Models/Animation/AnimationTimingSync.cs`
- `WaBiBaBuSy.Core/Services/ClientTimingSynchronizer.cs` (client-side sync)
- `WaBiBaBuSy.Core/Services/ServerTimingSynchronizer.cs` (server-side broadcast)

**Files to Modify:**
- `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` (add sync message + RPC)
- `WaBiBaBuSy.Grpc/Services/WallpaperSyncServiceImpl.cs` (implement broadcast)
- `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs` (implement client handler)
- `WaBiBaBuSy.Core/Services/ClientAnimationRenderer.cs` (add drift correction)

**Success Criteria:**
- [ ] Server broadcasts timing sync every 1 second
- [ ] Clients detect drift >50ms and correct
- [ ] Animations stay in sync ±50ms across all clients
- [ ] No visible stuttering or desynchronization

**Expected Effort:** 6-8 hours

---

### Phase 3: Sequential Animation Handoff (8-10 hours)

**Objective:** Animation flows through monitors in order, each monitor gets defined duration

**Tasks:**
1. [ ] Implement server-side animation scheduler:
   - Get animation duration
   - For each selected client, calculate start time (previous_start + duration)
   - Send AnimationStart to each client with staggered times
2. [ ] Implement client completion detection:
   - When animation finishes, client sends AnimationCompleteReport
3. [ ] Implement server-side handoff orchestration:
   - When client reports completion, immediately send animation to next client
   - Schedule next client to start when previous completes
4. [ ] Add looping support:
   - After last client finishes, optionally send animation back to first client
5. [ ] Testing: Run on 3+ clients, verify smooth animation flow

**Files to Create:**
- `WaBiBaBuSy.Models/Animation/AnimationCompleteReport.cs`
- `WaBiBaBuSy.Core/Services/AnimationOrchestrator.cs` (sequential scheduling)

**Files to Modify:**
- `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` (add completion message + RPC)
- `WaBiBaBuSy.Grpc/Services/WallpaperSyncServiceImpl.cs` (implement orchestration)
- `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs` (send completion)
- `WaBiBaBuSy.Core/Services/ClientAnimationRenderer.cs` (report completion)
- `WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs` (add distribution mode)

**Success Criteria:**
- [ ] Animation starts on Client 1 at T+0
- [ ] At T+duration, Client 2 receives animation with start time T+duration
- [ ] Animation seamlessly transitions from monitor 1 → 2 → 3 → ...
- [ ] Looping works: after last client, animation restarts on first client

**Expected Effort:** 8-10 hours

---

### Phase 4: UI Integration & Configuration (4-6 hours)

**Objective:** Expose new options in UI, let user control distribution behavior

**Tasks:**
1. [ ] Add toggle to CrossScreenConfigDialog: "Use Distributed Composition"
2. [ ] Add mode selector: Sequential / Simultaneous / Grouped
3. [ ] Add configuration:
   - Timing sync interval (default 1000ms)
   - Max allowed drift (default 50ms)
   - Client-side caching (enabled by default)
4. [ ] Add status display showing which clients are animating
5. [ ] Add performance metrics:
   - Server CPU during animation (should be <5%)
   - Network bandwidth usage
   - Per-client rendering status
6. [ ] Testing: Verify UI reflects correct state

**Files to Create:**
- Update `CrossScreenConfigDialog.axaml` with new controls
- Add `DistributedAnimationStatusViewModel.cs` (show live status)

**Files to Modify:**
- `WaBiBaBuSy.UI/Views/CrossScreenConfigDialog.axaml` (add UI controls)
- `WaBiBaBuSy.UI/ViewModels/CrossScreenConfigViewModel.cs` (handle UI changes)
- `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (display metrics)
- `WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs` (store settings)

**Success Criteria:**
- [ ] User can enable/disable distributed composition
- [ ] User can select animation mode (Sequential/Simultaneous/Grouped)
- [ ] Settings persist in config.json
- [ ] Status display shows active clients and animations

**Expected Effort:** 4-6 hours

---

## Architecture Comparison Matrix

| Aspect | Current (Centralized) | Proposed (Distributed) |
|--------|----------------------|------------------------|
| **Frame Generation** | Server renders all frames | Clients render locally |
| **Composition** | Server composes layers | Clients compose layers |
| **Network Transfer** | 100-150KB per frame × 30 FPS | ~1KB timing sync per second |
| **Server CPU** | 80%+ sustained | <5% orchestration only |
| **Client CPU** | <5% decoding | ~20% rendering locally |
| **Network Bandwidth** | 9 MB/sec (2 clients) | <1 MB/sec (unlimited clients) |
| **Scalability** | ~2-3 clients max | 50+ clients easily |
| **Latency** | 100-200ms | 0-50ms |
| **Animation Quality** | JPEG compressed | Lossless (client-side) |
| **Sync Accuracy** | ±200ms (frame-based) | ±50ms (time-based) |
| **Complexity** | Simple, monolithic | Distributed, orchestrated |

---

## Data Flow Diagrams

### Sequential Animation Handoff Flow

```
┌─────────────────────────────────────────────────────────────────┐
│ Server: AnimationOrchestrator                                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                   │
│ 1. User clicks "Start Animation" (Sequential mode)              │
│    SelectedMonitorIds: [Client1, Client2, Client3]              │
│    AnimationFile: "/Content/wave.mp4" (duration: 5000ms)        │
│                                                                   │
│ 2. Calculate start times:                                        │
│    Client1: now + 0ms                                            │
│    Client2: now + 5000ms (after Client1 finishes)               │
│    Client3: now + 10000ms (after Client2 finishes)              │
│                                                                   │
│ 3. Send AnimationStart to each client:                          │
│                                                                   │
│    ┌─────────────────────────────────────────────────────────┐  │
│    │ To Client1:                                              │  │
│    │ {                                                         │  │
│    │   animation_id: "wave_001",                              │  │
│    │   content_path: "/Content/wave.mp4",                     │  │
│    │   duration_ms: 5000,                                     │  │
│    │   start_timestamp_utc: 1730304000000,  ← NOW             │  │
│    │   background: {...},                                     │  │
│    │   loop: false                                             │  │
│    │ }                                                         │  │
│    └─────────────────────────────────────────────────────────┘  │
│                                    │                              │
│                                    ▼                              │
│    ┌─────────────────────────────────────────────────────────┐  │
│    │ To Client2:                                              │  │
│    │ {                                                         │  │
│    │   animation_id: "wave_002",                              │  │
│    │   content_path: "/Content/wave.mp4",                     │  │
│    │   duration_ms: 5000,                                     │  │
│    │   start_timestamp_utc: 1730304005000,  ← NOW + 5 seconds │  │
│    │   background: {...},                                     │  │
│    │   loop: false                                             │  │
│    │ }                                                         │  │
│    └─────────────────────────────────────────────────────────┘  │
│                                    │                              │
│                                    ▼                              │
│    ┌─────────────────────────────────────────────────────────┐  │
│    │ To Client3:                                              │  │
│    │ {                                                         │  │
│    │   animation_id: "wave_003",                              │  │
│    │   content_path: "/Content/wave.mp4",                     │  │
│    │   duration_ms: 5000,                                     │  │
│    │   start_timestamp_utc: 1730304010000,  ← NOW + 10 secs  │  │
│    │   background: {...},                                     │  │
│    │   loop: false                                             │  │
│    │ }                                                         │  │
│    └─────────────────────────────────────────────────────────┘  │
│                                                                   │
│ 4. Start timing sync broadcast (every 1 second):                │
│    For each animating client, send:                              │
│    {                                                             │
│      server_timestamp: <current UTC time>,                       │
│      animation_id: <which animation>,                            │
│      expected_position: <where animation should be>              │
│    }                                                             │
│                                                                   │
└─────────────────────────────────────────────────────────────────┘
                            │
            ┌───────────────┼───────────────┬─────────────────┐
            │               │               │                 │
            ▼               ▼               ▼                 ▼
      ┌──────────┐    ┌──────────┐    ┌──────────┐      ┌──────────┐
      │ Client 1 │    │ Client 2 │    │ Client 3 │      │ Broadcast│
      │          │    │          │    │          │      │ Every 1s │
      │ Receives │    │ Receives │    │ Receives │      │          │
      │ (IMMEDIATE)   │ (Waits)  │    │ (Waits)  │      │          │
      │ start: T+0    │ start: T+5    │ start: T+10  │      │          │
      │          │    │ waits 5 sec   │ waits 10 sec  │      │          │
      │ Starts  │    │ (idle)        │ (idle)        │      │          │
      │ rendering    │          │    │          │      │          │
      │ @ 30FPS │    │          │    │          │      │          │
      │          │    │          │    │          │      │          │
      │ T+0-T+5 │    │ T+5-T+10 │    │ T+10-T+15│      │          │
      │ animates│    │ animates │    │ animates │      │          │
      │          │    │          │    │          │      │          │
      │ T+5:    │    │ T+5:     │    │          │      │          │
      │ Report  │    │ Start    │    │          │      │          │
      │ complete◄────┤rendering │    │          │      │          │
      │          │    │          │    │          │      │          │
      │          │    │ T+10:    │    │ T+10:    │      │          │
      │          │    │ Report   │    │ Start    │      │          │
      │          │    │ complete ◄────┤rendering │      │          │
      │          │    │          │    │          │      │          │
      │          │    │          │    │ T+15:    │      │          │
      │          │    │          │    │ Report   │      │          │
      │          │    │          │    │ complete │      │          │
      └──────────┘    └──────────┘    └──────────┘      └──────────┘

Physical Result on Monitors:
┌─────────────────────────────────────────────────────────────────────┐
│                                                                       │
│  T+0 to T+5:    Animation flows ──────▶   (Client 1 rendering)     │
│  ┌─────────┐    ┌─────────┐    ┌─────────┐                         │
│  │ ▓▓▓▓▓▓▓ │    │ ░░░░░░░ │    │ ░░░░░░░ │  (░ = background)      │
│  │ ▓▓▓▓▓▓▓ │    │ ░░░░░░░ │    │ ░░░░░░░ │  (▓ = animation)       │
│  └─────────┘    └─────────┘    └─────────┘                         │
│   Monitor 1      Monitor 2       Monitor 3                           │
│                                                                       │
│  T+5 to T+10:   Animation flows ──────▶   (Client 2 rendering)     │
│  ┌─────────┐    ┌─────────┐    ┌─────────┐                         │
│  │ ░░░░░░░ │    │ ▓▓▓▓▓▓▓ │    │ ░░░░░░░ │                         │
│  │ ░░░░░░░ │    │ ▓▓▓▓▓▓▓ │    │ ░░░░░░░ │                         │
│  └─────────┘    └─────────┘    └─────────┘                         │
│   Monitor 1      Monitor 2       Monitor 3                           │
│                                                                       │
│  T+10 to T+15:  Animation flows ──────▶   (Client 3 rendering)     │
│  ┌─────────┐    ┌─────────┐    ┌─────────┐                         │
│  │ ░░░░░░░ │    │ ░░░░░░░ │    │ ▓▓▓▓▓▓▓ │                         │
│  │ ░░░░░░░ │    │ ░░░░░░░ │    │ ▓▓▓▓▓▓▓ │                         │
│  └─────────┘    └─────────┘    └─────────┘                         │
│   Monitor 1      Monitor 2       Monitor 3                           │
│                                                                       │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|-----------|
| **Client fails to start animation** | Animation doesn't appear on next monitor | Server detects AnimationCompleteReport timeout, auto-retry or move to next client |
| **Network delay → animation stutters** | Visible jitter/stutter during playback | Timing sync every 1s corrects drift >50ms |
| **Clock skew between server/clients** | Animations desynchronize | NTP time sync (OS-level), timing sync messages correct residual drift |
| **Client rendering quality varies** | Inconsistent appearance across monitors | Clients use same CompositionRenderer code (deterministic) |
| **File caching issues** | Stale animation on some clients | Cache with file hash + last modified time as key |
| **Looping causes frame glitches** | Animation jumps/stutters at loop point | Test looping thoroughly, may need special handling |
| **Large animation files (1GB+)** | Network transfer times out | Implement streaming/chunking for animation files (Phase 5) |

---

## Testing Strategy

### Unit Tests
- [ ] AnimationMetadata serialization/deserialization
- [ ] Timing sync drift calculation
- [ ] Animation start time scheduling
- [ ] Duration calculation from video files

### Integration Tests
- [ ] Single client receives animation, renders, reports completion
- [ ] Server broadcasts timing sync, client receives
- [ ] Sequential handoff: Client 1 → Client 2 → Client 3
- [ ] Drift correction: Client clock drifts 100ms, sync corrects to ±50ms
- [ ] Looping: After last client, animation restarts on first

### Load Tests
- [ ] 10 simultaneous clients, sequential animation
- [ ] 50 clients with timing sync
- [ ] Network latency simulation (100ms delay)
- [ ] Verify server CPU <5%, network <1 MB/s

### Regression Tests
- [ ] Existing cross-screen animation (centralized) still works
- [ ] Existing file transfer still works
- [ ] Existing wallpaper playback unaffected
- [ ] Settings/config loading/saving

---

## Success Metrics (Post-Implementation)

After completing all phases, we should measure:

1. **Server CPU Usage**
   - Target: <5% during animation (was 80%+)
   - Success: 94% reduction achieved

2. **Network Bandwidth**
   - Target: <1 MB/sec total (was 9 MB/sec)
   - Success: 99% reduction achieved

3. **Scalability**
   - Target: Support 50+ clients without degradation
   - Success: Add client = minimal impact

4. **Synchronization**
   - Target: Maintain ±50ms drift (same as current)
   - Success: Timing sync messages keep clients in sync

5. **Animation Smoothness**
   - Target: 30 FPS locally on clients
   - Success: No stuttering, smooth playback

---

## Backward Compatibility

**Key Design Decision:** Keep centralized rendering as fallback option

```csharp
public class CrossScreenConfig {
    public bool UseDistributedComposition { get; set; } = true;

    // If set to false, server falls back to old centralized rendering
    // (all clients receive frames from server)
}
```

**Migration Strategy:**
1. Implement distributed rendering (all phases)
2. Default to enabled in new installations
3. Existing users can toggle: "Use Distributed Composition" checkbox
4. Old clients can still work with centralized mode
5. Post-MVP: Deprecate centralized mode (recommend distributed)

---

## Dependency on CrossScreenFrameDisplayPlan.md

**Current Status:** CrossScreenFrameDisplayPlan.md outlines 4 solutions for local frame display:
- Option A: GDI+ Direct Paint (unreliable)
- Option B: Temp File + Reload (slow)
- Option C: Direct2D Renderer (proper but complex)
- Option D: Disable Local Display (MVP)

**With Distributed Composition:**
- **Option D becomes unnecessary** - Clients handle their own display
- **Direct2D Renderer not needed for MVP** - Each client displays locally
- **Architecture becomes cleaner** - Server doesn't compose frames at all
- **Solution: Skip CrossScreenFrameDisplayPlan entirely**, implement distributed composition instead

---

## Next Steps (Execution Order)

1. **Review & Approve This Plan** - Confirm architecture direction with user
2. **Create gRPC Protocol Extensions** - Define all new messages and RPCs
3. **Implement Phase 1** - Animation distribution (proof of concept)
4. **Test Phase 1** - Verify single client can receive, download, render
5. **Implement Phase 2** - Timing synchronization
6. **Test Phase 2** - Verify drift correction on 2+ clients
7. **Implement Phase 3** - Sequential handoff
8. **Test Phase 3** - Verify smooth animation flow across monitors
9. **Implement Phase 4** - UI integration
10. **Final Testing** - Load tests, regression tests, performance verification
11. **Documentation** - Update design docs, user guides

---

## Document Status

**Status:** ✅ READY FOR REVIEW
**Created:** 2025-10-30
**Estimated Duration:** 18-24 hours total implementation
**Awaiting:** User approval before implementation begins

---

**Key Question:** Should we implement this distributed composition architecture instead of pursuing the CrossScreenFrameDisplayPlan.md solutions?

# One-Shot Show Events (Roadmap 2.4, slice 2) — Design

**Created:** 2026-10-03 · **Status:** approved design · **Plan:** [`2026-10-03-show-events-plan.md`](2026-10-03-show-events-plan.md)
**Parent spec:** [`2026-09-17-crossing-effects-and-events-design.md`](2026-09-17-crossing-effects-and-events-design.md) §5 (model, transport, event types, triggers) and §7 (determinism checklist). This document records the decisions made for the implementation; where it differs from the parent (Traversal image instead of `SceneLayer`, `TargetOrder` instead of `TargetSeatId`) this document wins.
**Slice 1 (shipped 2026-10-03):** sprite and bezel effects — [`2026-10-03-sprite-and-bezel-effects-plan.md`](2026-10-03-sprite-and-bezel-effects-plan.md). Same split: pure math in `WaBiBaBuSy.Models`, drawn by the D2D player and the room preview (`ScenePainter`).

---

## 1. Goal and success criteria

The server can fire **one-shot moments** across every seat while a scene plays: spotlight a seat, a
confetti burst over the whole room, the club logo swimming the ring exactly once, a blackout.

- Every event has an explicit shared start `T0` (now + lead) and a duration. Players draw active
  events **above** the scene until `T0 + Duration`; the scene clock is never interrupted.
- Every node draws its slice of the same event, pixel-identical across machines: all event visuals
  are pure functions of `(event, node layout, event-local ms)`.
- Late joiners need no state beyond the event itself; expired events are simply not drawn.
- Old clients ignore the new command.
- Triggers: an Events strip in the main window, global hotkeys (work with the window closed),
  optionally scheduled playlist interrupts.
- Test-mode exact frames render events (visual check via a showcase scenario).

Scope: Spotlight, Celebration, Traversal, Blackout; then Announce; playlist interrupts last and only
if time allows.

## 2. Model (`WaBiBaBuSy.Models/Show/`)

```csharp
public enum ShowEventType { Spotlight, Celebration, Traversal, Blackout, Announce }

public sealed class ShowEvent
{
    public string EventId { get; set; } = "";       // unique per firing (dedupe on replay)
    public ShowEventType Type { get; set; }
    public long T0Utc { get; set; }                 // shared start; on clients = SyncCommand.timestamp_utc after clock conversion
    public int DurationMs { get; set; }
    public int Seed { get; set; }                   // drawn once on the server, broadcast
    public string ColorHex { get; set; } = "#FFD700";
    // Spotlight
    public int TargetOrder { get; set; } = -1;      // traversal index of the target seat (NodeLayout.Order / node_order)
    public float TargetCenterX { get; set; }        // target slice center on the canvas (chevron direction)
    // Traversal
    public string? ImagePath { get; set; }          // rewritten to the client's cache on receipt
    public float SpriteAspect { get; set; } = 1f;   // width / height of the Traversal image or Announce text, measured once on the server
    public float HeightFraction { get; set; } = 0.5f; // sprite height / canvas height
    public bool Reverse { get; set; }
    // Announce
    public string? Text { get; set; }
}
```

Defaults (persisted in `ShowEventSettings`, `%APPDATA%\WaBiBaBuSy\show-events.json`): Spotlight
8 s, Celebration 5 s, Traversal speed 600 canvas px/s (one lap), Blackout 3 s (`#000000`), Announce
one lap; Traversal image empty = the current scene's sprite file.

## 3. Pure math (`ShowEventMath`, `ShowEventLog`, `ShowEventFactory`)

All functions depend only on their arguments. Long uptimes: fold in `double` before `float` math.

### 3.1 Timing
- `LocalMs(evt, sceneStartUtc, sceneRawElapsedMs) = sceneRawElapsedMs + sceneStartUtc − evt.T0Utc`.
  Live frames pass the raw (un-phased) scene elapsed; exact frames pass the probe's elapsed, so an
  exact frame at elapsed X shows the event exactly as a live frame at X would. Wave-mode phase does
  not apply to events.
- `IsActive(evt, localMs)`: `0 ≤ localMs < DurationMs` (duration clamped to 250 ms … 10 min).
- `Envelope(localMs, durationMs, fadeMs = 250)`: 0→1 over the first `fadeMs`, 1→0 over the last.

### 3.2 Spotlight
`Spotlight(evt, node, localMs)` → target flag, dim opacity, border opacity/width, chevron direction
and offset.
- Target node (`node.Order == TargetOrder`): border in `ColorHex`, width 16 canvas units, opacity
  `envelope × (0.6 + 0.4 sin(2π·1.5 Hz·t))`.
- Other nodes: black overlay at `0.6 × envelope` (scene dimmed to 40 %), plus a chevron in the
  node's center pointing along the traversal toward the target, sliding 40 units back and forth.
  Direction: sign of `TargetCenterX − node center` on the canvas; on a wrapping (Ring) layout the
  shorter way around; negated on mirrored nodes. Simultaneous mode (no shared canvas): sign of
  `TargetOrder − node.Order`.

### 3.3 Celebration
Seeded closed-form ballistic burst over the whole canvas. Particle count
`N = clamp(ceil(CanvasW / 1000 × 250), 50, 6000)`. Particle *i*:
- `x0 = (i + u0) / N × CanvasW` (`uk = Hash3(seed, i, k)` mapped to [0,1)), so particles are sorted by
  x0 and a node only evaluates the index range covering its slice ± the maximum horizontal drift;
- launched from below the bottom edge with `vy0 = −(0.9 + 0.5 u1) × CanvasH / 1.1 s`, gravity
  `g = 1.6 × CanvasH / s²`, drift `vx = (u2 − 0.5) × 300 units/s`, launch delay `u3 × 600 ms`;
- color from `{ColorHex + 5 party colors}` by `u4`, size `8 + 10 u5` units, spin from `u6`;
- `x(t) = x0 + vx t`, `y(t) = y0 + vy0 t + ½ g t²`; wraps modulo `CanvasW` on a Ring; opacity ×
  envelope. Mapped to the node with `NodeMapping.ToLocalX/ToLocalY`.
The particle math takes a caller-provided `Span` (no per-frame allocation in the player).

### 3.4 Traversal
`p = localMs / DurationMs`; sprite height `HeightFraction × CanvasH` (clamped 0.05…1), width
`height × SpriteAspect` (the event's aspect, not the decoded bitmap's — identical on every node);
`x = −w + p × (CanvasW + w)` (non-wrapping) or `x = −w + p × CanvasW` (Ring, wrapped by
`NodeMapping.WrapCopies`), mirrored by `Reverse` (`x' = CanvasW − w − x`); vertically centered; opacity
× envelope (hides the Ring pop-in). Drawn through `NodeMapping.WrapCopies/ToLocalX` like the scene sprite.
`TraversalDurationMs(canvasW, spriteW, wraps, pxPerSecond)` gives exactly one lap, clamped to
2 s … 120 s. In Simultaneous mode each node uses its own canvas width; all finish at `T0 + Duration`.

### 3.5 Blackout
Full-node fill in `ColorHex` at opacity `envelope`.

### 3.6 Announce (if time allows)
Text in `ColorHex` that scrolls one lap exactly like Traversal (same pose function). The player has no
DirectWrite, so the text is rendered once into a GDI+ bitmap (`TextSprite`, WallpaperEngine) and drawn
like the Traversal image. The server measures the text's aspect with the same `TextSprite` and sends it
as `SpriteAspect`; every node draws the bitmap into the box `SpriteAspect × height`, so positions agree
even if a node's font metrics differ slightly. The preview draws `FormattedText` into the same box.

### 3.7 `ShowEventLog`
Add (dedupe by `EventId`, cap 16 — oldest dropped), `Active(nowUtc)` (events not yet ended, including
ones whose `T0` is in the future), `Prune(nowUtc)` drops events ended more than 60 s ago. Expired
events are skipped at draw time, not deleted immediately, so an exact frame requested shortly after
still finds them. Used by the player, both playback hosts and the preview.

### 3.8 `ShowEventFactory`
Builds an event from type, target seat, active layout and settings: new `EventId`, `Seed` from the
server's `Random` (drawn once, broadcast — determinism holds), `T0 = now + SyncTiming.ComputeStartLeadMs(maxRtt, 0)`,
duration from settings (Traversal: one lap from the canvas width), `TargetOrder` / `TargetCenterX`
from the seat's `NodeLayout`.

## 4. Transport

### 4.1 Proto (`WaBiBaBuSy.Grpc/Protos/wabibabusy.proto`) — new numbers only
```proto
enum CommandType { …  EVENT = 12; }               // one-shot show event (ShowEvent JSON in event_json)
message SyncCommand { …  string event_json = 6; }  // EVENT: JSON ShowEvent; its T0 is timestamp_utc
```
- `T0` travels in `timestamp_utc`, so the client's existing server→local clock conversion (offset and
  simulated test skew) applies; the client overwrites `ShowEvent.T0Utc` with the converted value.
- The Traversal image travels in the existing `params.assets` (role `event`); the server registers it
  for download like scene assets.
- Old clients: the unknown enum value reaches `WallpaperPlaybackService`, which runs every command in
  its own task and logs "Unknown command type" in the `default:` branch — no blocking, no crash.
- `ShowEventCodec` (Core): `ToCommand(evt, seq, asset?)` / `TryFromCommand(cmd, out evt)`.

### 4.2 Server
`ServerPlaybackHost.FireEventAsync(type, targetSeatId?)`: factory → own log → local players →
`WallpaperSyncCoordinator.BroadcastShowEventAsync` (all connected clients) → `EventFired` (preview).
Refused (status text) when nothing plays. The Traversal image is prefetched (`BroadcastPrefetchAsync`)
when it is picked and on every scene start.

### 4.3 Client
`WallpaperPlaybackService.OnSyncCommandReceived` handles `EVENT` **before** the timestamp wait:
ensure the asset is cached (`EnsureCachedAsync`), rewrite `ImagePath` to the cache, call the
`ShowEventDelegate` (registered via `WaBiBaBuSyService`, like the D2D delegates).
`RemotePlaybackHost` adds the event to its log and fans it out to its players. If the download is
late, the player starts drawing mid-lap at the correct position (the pose is a pure function of time).

### 4.4 Replay
Both hosts send `log.Active(now)` to every newly started player (after its start command). Covers
playlist item changes during an event (players are respawned per apply) and late joiners.

### 4.5 Player IPC
`PlayerCommandShowEvent { ShowEvent Event }` (`cmd_show_event`, Newtonsoft like the other player
commands, no stdout reply) → `D2DPlayerHost.SendShowEventAsync` → `D2DCompositionService` sends to all
its hosts.

## 5. Player drawing (`WaBiBaBuSy.Player.D2D/Program.ShowEvents.cs`)

- `DrawShowEvents(sceneRawElapsedMs)` is called in the frame tail **before** `DrawDebugOverlay` (so
  events are above the scene, below the debug overlay and the timecode strip — a Blackout never
  breaks drift probes), only while a scene draws; and in `RenderDueExactFrame` after
  `DrawNativeGifScene`, with the probe's elapsed.
- Drawing in canvas units under `Scale` (as the bezel effects). Brushes cached, disposed in
  `DisposeNativeD2DResources`. No per-frame heap allocations (particle `stackalloc`/pooled span).
- The event list is guarded by a lock; the stdin thread adds, the render thread reads and prunes.
- **Traversal image (the one extra sprite):** the stdin thread decodes the file with Magick.NET into
  BGRA frames + delays (coalesced, downscaled to ≤ 1024 px high); the render thread uploads them to
  `ID2D1Bitmap`s on first use (the D2D context stays single-threaded; a decode never stalls a frame).
  Animated GIFs run on the event-local clock. Plain `DrawBitmap` — no grading, pattern or effects.
  Bitmaps are released when the event is pruned. A missing or unreadable file → the event is skipped
  with a log line.

## 6. Room preview

`ScenePainter.PaintShowEvents(ctx, rect, s, node, events, sceneStartUtc, rawElapsed)` uses the same
math per node tile. In Live mode `RoomView` paints the host's active events; the Traversal image is
loaded as an Avalonia bitmap and cached by path.

## 7. Triggers

- **Events strip** under the room view (server mode): ★ Spotlight, ✦ Celebration, ↻ Traversal,
  ■ Blackout (+ Announce with a text box), a seat picker (defaults to the node selected in the room
  view), the Traversal image picker. Enabled only while a scene plays. `ShowEventsViewModel`;
  settings persisted in `ShowEventSettings`.
- **Global hotkeys** (`GlobalHotkeyService`, app-lifetime, owned by the tray): a dedicated thread with
  its own message queue (`RegisterHotKey(NULL, …)` posts `WM_HOTKEY` to that thread — no window
  needed), Ctrl+Alt+1 Spotlight (picked seat), 2 Celebration,
  3 Traversal, 4 Blackout, 5 Announce. Active in server mode only. A key taken by another app is
  logged and shown as a status line — no crash.
- **Scheduled interrupts** (optional): `Playlist.Interrupts: List<{ EveryMs, Event template }>` driven
  by `PlaylistOrchestrator`; fires at `T0 = now + lead`, does not advance the item.

## 8. Visual check

New scenario step `showEvent { event, atElapsedMs }`: `T0 = sharedStart + atElapsedMs` via a new
`ITestHost.FireShowEventAsync`. `TestScenarios/showcase/events.json` plays one scene, fires each
event and takes `exactFrame`s inside each event's window.

## 9. Determinism checklist (parent §7, applied)

- Event visuals sample only `LocalMs` (shared clock), the event and the node layout. No
  `DateTime.Now`, no `Random` in players or preview; the seed is drawn once on the server.
- `T0` is explicit and converted per client like scene starts; expired events are not drawn.
- Exact frames derive event time from the probe's elapsed, never from the wall clock.

## 10. Tests (TDD, `WaBiBaBuSy.Tests`)

- Timing: `LocalMs` live vs exact equivalence; `IsActive` bounds; envelope; duration clamp.
- Log: dedupe, cap, `Active` excludes ended events (late joiner), includes future `T0`, prune after 60 s.
- Celebration: same seed, two nodes with different `OffsetX` → a particle seen by both has identical
  virtual coordinates; the union of node index ranges covers every visible particle exactly once;
  different seed → different burst; Ring wrap.
- Spotlight: target vs others; chevron direction on mirrored nodes and the Ring short way.
- Traversal: off-canvas at local 0 and at Duration (non-wrapping); Ring returns to the start; Reverse;
  one-lap duration from speed.
- Codec: proto byte round trip; `T0` from `timestamp_utc`; unknown enum value parses; malformed JSON
  rejected.
- Factory: T0 = now + lead, target order/center from layout.
- Scenario: `showEvent` step loads; the runner fires with `T0 = sharedStart + atElapsedMs` (fake host).

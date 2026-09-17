# Crossing Effects, Sprite Effects & One-Shot Events — Design

**Created:** 2026-09-17 · **Status:** Proposal · **Roadmap:** Tier 2.4 (+ 0.5, 0.7, 2.5)
**Solves:** F6 in [`2026-09-17-lan-party-roadmap.md`](2026-09-17-lan-party-roadmap.md)
**Depends on:** seat map (ring), show-reliability (pre-announced timestamps) for events/transitions

---

## 1. Goal

Make the traversal *visible as an event*: the moment a sprite crosses a bezel should read as
"it jumped to the next machine," the sprite should look alive (faces its direction, leaves a
trail, breathes), and the server should be able to fire **one-shot moments** across all 20 screens
(spotlight a winner, celebration burst, the club logo swims the ring once).

Everything stays deterministic: every effect is a pure function of the shared clock and config,
evaluated locally. One-shot events carry a shared `T0`.

## 2. Sprite effects (`LayerEffects`, per layer)

```csharp
public class LayerEffects
{
    public bool FaceTravelDirection { get; set; } = true;   // roadmap 0.5
    public TrailConfig? Trail { get; set; }                  // ghost copies behind the sprite
    public PulseConfig? Pulse { get; set; }                  // scale breathing
    public float SpinDegPerSecond { get; set; }              // constant rotation
    public BezelEffect Entry { get; set; } = BezelEffect.None;
    public BezelEffect Exit  { get; set; } = BezelEffect.None;
    public string BezelColorHex { get; set; } = "#FFFFFF";
    public int BezelDurationMs { get; set; } = 350;
}
public class TrailConfig { public int Count = 6; public int SpacingMs = 60; public float StartOpacity = 0.5f; public float ScaleFalloff = 0.9f; public bool UseGradingColor = true; }
public class PulseConfig { public float Amplitude = 0.08f; public float FrequencyHz = 0.8f; }
public enum BezelEffect { None, Glow, Flash, Ripple, Portal }
```

### 2.1 Face travel direction (roadmap 0.5, S)
`dx = X(elapsed) − X(elapsed − 16 ms)` in **screen space** (after mirroring). `dx < 0` → draw with
`Scale(−1, 1)` about the sprite center. Hysteresis: only flip when `|dx| > 0.5 px` to avoid
flicker at bounce apexes. Works for Bounce, RandomWalk, Circular, IconZone path, mirrored nodes.

### 2.2 Trail
No history buffer needed: positions are pure functions, so ghost *k* is simply
`Position(elapsed − k × SpacingMs)` drawn with opacity `StartOpacity × (1 − k/Count)` and scale
`ScaleFalloff^k`. With Traveling colors the ghost keeps the cell color; with time-based grading
`UseGradingColor` tints ghosts with `ColorGrader.Compute(elapsed − k × SpacingMs)` (a rainbow
comet). Cost: `Count` extra draws per layer. Ghosts crossing a bezel naturally appear on the
previous node — the trail spans machines for free.

### 2.3 Pulse / spin
`scale = 1 + Amplitude × sin(2π f elapsed)`, `rotation = SpinDegPerSecond × elapsed / 1000` (fold
in double, as `MovementCalculator` does). Rotation composes with `RotateWithPath`.

## 3. Bezel crossing effects

Each player knows its slice. Define the sprite's screen-space rect `R(t)` and the crossing times:
- **entry**: first `t` where `R(t)` intersects the screen (leading edge crosses x = 0 or x = W,
  depending on direction);
- **exit**: last `t` before `R(t)` leaves.

Detection can be done frame-to-frame (`intersects(t) != intersects(t − dt)`) — the effect is only
shown on the node where it happens, so no cross-machine agreement is needed; the shared clock makes
the *timing* identical anyway. Record `tEntry`/`tExit` and render the effect for `BezelDurationMs`:

| Effect | Rendering (D2D, at the crossing edge) |
|---|---|
| **Glow** | vertical linear-gradient strip 120 px wide at the edge, `BezelColorHex` → transparent, opacity `1 − p` (p = progress 0..1) |
| **Flash** | full-screen fill `BezelColorHex` at opacity `0.35 × (1 − p)²` — use sparingly, great for a bass hit |
| **Ripple** | expanding ellipse ring centered at the sprite's crossing point, radius `p × 600 px`, stroke fading |
| **Portal** | ellipse "door" at the edge that opens (0→sprite height × 1.3) before entry and closes after exit; the sprite is clipped to it during the crossing |

`Portal` needs the crossing time slightly *ahead* (to open before the sprite arrives): compute
`tEntry` analytically for Linear/SineWave (`(OffsetX − x0)/speed`) and fall back to "open on
detection" for others. Traveling patterns (`Pattern != null`) disable bezel effects (a grid
crosses bezels continuously).

## 4. Node phase / Wave mode (roadmap 0.7, S)

`MovementConfig.NodePhaseDelayMs` (default 0). In **Simultaneous** distribution the player uses
`elapsed' = elapsed − Order × NodePhaseDelayMs` (`Order` from `NodeLayout`). A Bounce/Pulse then
runs down the row as a wave; with `Ring`, `Order` follows the traversal so the wave goes around the
room. Negative elapsed' before the wave arrives → the layer is not drawn (same "hold until 0" rule
as the future-T0 start). A "wave" is thus a Simultaneous scene with a phase delay — no new movement
type.

## 5. One-shot events

### 5.1 Model & transport
```csharp
public enum ShowEventType { Spotlight, Celebration, Traversal, Announce, Blackout }
public class ShowEvent
{
    public string EventId; public ShowEventType Type;
    public long T0Utc;                 // shared start (now + lead, see show-reliability)
    public int DurationMs;
    public string? TargetSeatId;       // Spotlight / Announce
    public SceneLayer? Layer;          // Traversal: a temporary layer (e.g. the big logo, one lap)
    public string? Text; public string ColorHex = "#FFD700"; public int Seed;
}
```
New `SyncCommand.Type = EVENT` with `event_json`. Server broadcasts to all seats (or the target);
the player keeps a small list of active events and renders them **above** the scene until
`elapsed ≥ T0 + Duration`. Events do not interrupt the scene clock — the scene keeps running.

### 5.2 Event types
- **Spotlight (target seat)**: the target's border pulses `ColorHex`, everything else dims to 40 %
  for the duration; other seats show a small arrow-chevron pointing along the traversal toward the
  target ("look over there"). Use: match winner, birthday, "Max, your pizza is here."
- **Celebration**: seeded confetti/particle burst across the whole ring. Particle *i* has
  `(x0 = hash(seed,i) × P, vy, color)`; position at `t` is closed-form (ballistic), so every node
  draws its slice of the same burst. 300–600 particles per node is trivial for D2D.
- **Traversal**: a temporary `SceneLayer` (typically the big club logo, `Linear`, `Ring`) that runs
  exactly one lap from `T0` and despawns (`DespawnAtMs = lapMs`). This is the "every 30 minutes
  the logo swims once" item deferred in the playlist design, and the manual "do it now" button.
- **Announce**: big text (`Text`) that scrolls along the ring like the sprite (DirectWrite; the
  debug overlay already has a text path) — "Round 3 starts in 5 min."
- **Blackout / Whiteout**: all nodes fade to a color for the duration (bass drop, countdown).

### 5.3 Triggers
- Main window: "Events" toolbar (buttons + target seat picker) and **hotkeys** (global
  `RegisterHotKey` already exists in the player; add them in the main app: e.g. Ctrl+Alt+1..5).
- **Scheduled interrupts** on the playlist: `Playlist.Interrupts: List<{ EveryMs, Event }>` driven
  by `PlaylistOrchestrator` (fires at `T0 = now + lead`, does not advance the item).
- Tier 3: tiny local web page served by the server (phone on the floor triggers events).

## 6. Cross-item transitions (roadmap 2.5)

With pre-announced item switches (show-reliability §4) every node knows `T0_next` and the next
config ahead of time. The player can then load the next item into a second `LayerRuntime` set and
at `T0_next` run one of: **Cut** (today), **Fade** (`opacity_old = 1 − p, opacity_new = p` over
`TransitionMs`), **Wipe along the ring** (a boundary at `x = p × P` sweeps the canvas; nodes left
of it show the new scene — the wipe visibly runs around the room), **Bezel-by-bezel** (each node
switches when the boundary passes its `OffsetX`). `PlaylistItem.TransitionIn` +
`Playlist.DefaultTransition`.

## 7. Determinism checklist for reviewers

- Every effect samples the shared clock; no `DateTime.Now`, no `Random` without `Seed`.
- Frame-to-frame *detection* of bezel crossings is allowed because the effect is local to the node
  where the crossing happens; the crossing *time* is still the same on all clocks.
- Events are broadcast with an explicit `T0`; players ignore events whose `T0 + Duration` has
  passed (late joiners) — no state is needed to resume them.

## 8. Tests

- `TrailTests`: ghost positions equal `Position(elapsed − k×spacing)`; ghosts are drawn on the
  previous node when the sprite just crossed (position with negative local X).
- `FacingTests`: sign of dx in screen space on mirrored vs non-mirrored nodes.
- `CrossingTimeTests`: analytic `tEntry` for Linear/SineWave equals frame-scan detection ±1 frame.
- `ParticleBurstTests`: closed-form particle positions identical across two "nodes" with different
  offsets for the same seed.
- Manual on two monitors: Glow/Ripple at the bezel, Spotlight target, one Traversal lap.

## 9. Rollout

1. 0.5 facing + 0.7 wave (S each).
2. Trail + pulse/spin (S–M).
3. Bezel Glow/Flash/Ripple (M); Portal after the analytic crossing time exists.
4. Events: transport + Spotlight + Traversal (M), then Celebration/Announce/Blackout.
5. Transitions once pre-announce exists.

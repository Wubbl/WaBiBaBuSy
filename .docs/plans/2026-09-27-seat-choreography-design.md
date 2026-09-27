# Seat Choreography — Design

**Created:** 2026-09-27 · **Status:** Proposal · **Roadmap:** new Tier 2.6 (see [`2026-09-17-lan-party-roadmap.md`](2026-09-17-lan-party-roadmap.md))
**Depends on:** seat map + ring (Tier 1.1, done), physical canvas (Tier 1.2, done), live preview (Tier 2.1, done)
**Related:** [crossing effects & events](2026-09-17-crossing-effects-and-events-design.md) (Tier 2.4), [scene layers](2026-09-17-scene-layers-design.md) (Tier 2.3), [authoring UX](2026-09-17-authoring-ux-and-preview-design.md) (Tier 2.2 presets)

---

## 1. Problem

The six movement types (`Static`, `Linear`, `Bounce`, `SineWave`, `Circular`, `RandomWalk`) are
single-screen screensaver physics stretched across the room. For the 2 × 10 party setup:

- **Linear on the ring is a conveyor belt**: constant speed, no pauses. Any one person sees an empty
  screen most of the time and the logo never *arrives* anywhere.
- **Bounce / SineWave / Circular** lose their shape over 20 screens (a sine over 20 m is a wobble).
- **Nothing speaks in seats.** The audience thinks "it's on my screen now, now it's on Max's."
  `MovementCalculator` only knows `canvasWidth`; it has no idea where the seats are.

This design adds a seat-aware vocabulary on top of the existing deterministic model. It is aimed
at the typical content: **one team logo or an animated GIF**.

### Scope decisions (Patrick, 2026-09-27)

- **No deliberate hops across the table** (row A seat k → facing row B seat k). Whether seats line
  up across the table is not guaranteed, and it is not wanted. No "opposite seat" link in the seat
  map. Travel between rows always goes **along the traversal path** (around the ring turn).
- **Random hopping between all seats: yes** — a seeded "who's next?" across the whole room.

## 2. Features at a glance

| # | Feature | What the room sees | Size |
|---|---|---|---|
| A | **Hop movement** (`MovementType.Hop`) | Logo lands on a seat, rests with a little idle, flies eased to the next seat | M |
| B | **Seat sequences** for Hop | In order around the ring, ping-pong, **seeded random across all seats (shuffle bag)** | S (with A) |
| C | **Seat light** | Each screen lights up as the logo passes / rests, fades after; optional "anticipation" glow before arrival | S–M |
| D | **Speed profiles** for Linear/SineWave | Surging / breathing speed, eased start instead of a constant belt | S |
| E | **Tempo grid** (manual BPM) | Hops, pulses and light flashes land on the beat | M |
| F | **Convoy** | K evenly spaced copies around the ring (Linear and Hop) | S |
| G | **Row mosaic** | One giant logo spanning a whole row, revealed seat by seat | M |
| H | **Motion segments** | One item plays a sequence: 2 laps → hop tour → gather at seat 7 → … | L |
| I | **GIF motion sync** | GIF frames advance with distance (walk cycles), hold while resting | S |
| J | **Sprite styling** | Drop shadow / outline / glow so a logo reads on any background | S |

All of it keeps the architectural invariant: every position, scale, color and light level is a
pure function of `(config, elapsedMs, seat stops, seed)`. No per-frame IPC, no `Random` without a
seed, no per-machine state in the math.

## 3. Foundation: seat stops

Everything seat-aware needs the same data on every node: **where each seat is on the canvas**.

```csharp
/// <summary>One seat on the shared canvas, in traversal order.</summary>
public readonly record struct SeatStop(int Order, int RowIndex, float CenterX, float Width);
```

- Computed once by the apply path from the full layout list (the same `VirtualCanvasManager` /
  `SeatMap` output that produces each `NodeLayout`): `CenterX = OffsetX + sliceWidth / 2` in canvas
  units (reference px in Physical mode, so it is already unit-correct), ordered by `NodeLayout.Order`.
- Transport: add `List<SeatStop> SeatStops` to **`NodeLayout`**. `NodeLayout` already travels to
  remotes in `SyncParameters.layout_json` (proto field 25) and to players in
  `PlayerCommandLoadAnimation.Layout`, so no new proto field or IPC field is needed. Every node gets
  the identical list; old players ignore the extra JSON property.
- `ScenePainter` (room preview) builds the same list from the room model — the preview then shows
  Hop, Seat light and Mosaic with the players' own math.
- Y: seat stops share the canvas vertical center (respecting `SeatMap.VerticalAnchor`); Hop adds
  its arc on top. No per-seat Y.
- Stops are frozen at apply time. A topology change re-applies the scene (as today), so all nodes
  always agree on the list.

New pure helper `SeatPath` (in `WaBiBaBuSy.Models/Wallpaper/`, next to `MovementCalculator`):
`Stops`, `Perimeter` (= canvasWidth when `Wraps`), and `SignedDistance(fromX, toX)` that picks the
shorter direction around a ring (or the only direction on an open Snake path).

## 4. A — Hop movement

### 4.1 Model

```csharp
public enum HopSequence { Forward, PingPong, RandomShuffle, RandomFree }
public enum HopTravel   { Fly, Teleport }
public enum Easing      { Linear, EaseInOut, EaseOutBack, EaseOutBounce }

public class HopConfig
{
    public HopSequence Sequence { get; set; } = HopSequence.Forward;
    public HopTravel Travel { get; set; } = HopTravel.Fly;
    public int DwellMs { get; set; } = 2500;          // rest on each seat
    public int TravelMs { get; set; } = 900;          // flight to the next seat
    public Easing Easing { get; set; } = Easing.EaseInOut;
    public float ArcHeight { get; set; } = 120f;      // canvas px (resolved from cm in Physical mode)
    public float ArcHeightCm { get; set; } = 4f;
    public HopIdle Idle { get; set; } = HopIdle.Bob; // None, Bob, Breathe, Spin, Wiggle
    public float LandingSquash { get; set; } = 0.12f; // 0 = off; scale (1+a, 1−a) decaying after touchdown
    public int StartSeat { get; set; } = 0;           // traversal order index
    public int Seed { get; set; } = 42;               // Random* sequences
}
```

`MovementConfig.Hop` (nullable, used when `Type == Hop`). `ArcHeightCm` follows the same
`SpeedUnit`-style rule as speed and is resolved by `PhysicalUnits` before broadcast.

### 4.2 Math

The hop period is **fixed**: `P = DwellMs + TravelMs`. That keeps the lookup O(1):

```
k      = floor(elapsed / P)          // hop index (long; fold in double as MovementCalculator does)
local  = elapsed − k·P
from   = Seat(k),  to = Seat(k + 1)
if local < DwellMs:  pos = center(from)                      + idle(local)
else:                p = ease((local − DwellMs) / TravelMs)
                     x = center(from) + p · SignedDistance(from, to)   (mod Perimeter on a ring)
                     y = baseY − ArcHeight · sin(π · p)
```

A fixed period means a long random hop flies faster than a short one. That reads as a deliberate
"zoom" and is the right trade-off against variable periods, which would need prefix sums over all
earlier hops. Seam-straddling flights on the ring reuse `NodeMapping.WrapCopies` unchanged.

**Teleport** instead of Fly: the sprite scales/fades out at `from` during the first half of
`TravelMs` and in at `to` during the second half — no flight across the intermediate screens.
Good for random sequences where the target is far away. Pairs with the Portal bezel effect from
Tier 2.4 once that exists.

### 4.3 Seat sequences — `Seat(k)`

With `N = SeatStops.Count`:

| Sequence | `Seat(k)` |
|---|---|
| `Forward` | `(StartSeat + k) mod N` — around the ring; `Reversed` flips direction |
| `PingPong` | reflect `k` over `[0, N−1]` (period `2N−2`) — the natural fit for an open Snake path |
| `RandomShuffle` | **shuffle bag**: round `r = k / N`, permutation `π_r = FisherYates(Hash(Seed, r))`, `Seat(k) = π_r[k mod N]`. Every seat gets exactly one visit per round. If `π_r[0] == π_{r−1}[N−1]`, swap `π_r[0]` and `π_r[1]` so it never "hops" in place |
| `RandomFree` | `Seat(k) = Hash(Seed, k) mod (N−1)`, skipping `Seat(k−1)` — can revisit soon, never twice in a row |

`RandomShuffle` is the recommended default for random: at a party, everyone wants their turn. The
hash is the existing `PatternLayout.Hash3`-style integer hash; no `System.Random`.

Travel always follows the traversal path (`SignedDistance`), so a random hop from row A to row B
goes around the ring turn, never across the table.

### 4.4 Idle & landing

- `Bob`: `y += A·sin(2π·f·local)`, small A. `Breathe`: scale pulse. `Spin`: one turn per dwell.
  `Wiggle`: ±8° rotation. All pure functions of `local`.
- Landing squash: for `local` in `[0, 250 ms]`, `sx = 1 + a·e^(−local/80)·cos(…)`, `sy = 1/sx`.
- Facing (`FaceTravelDirection`, Tier 0.5): the player currently flips on the frame-to-frame `dx`.
  During dwell `dx = 0`; Hop instead derives facing from `sign(SignedDistance(Seat(k−1), Seat(k)))`
  of the last flight, which is deterministic and flicker-free.

### 4.5 Constraints

- Only meaningful in **Sequential** distribution. In Simultaneous each node has one "seat" → Hop
  degenerates to Static + idle; `SceneChecks` warns.
- Not combined with **Pattern** grids (a grid has no single position) and not with **IconZone**
  path following. `SceneChecks` flags both.
- `N < 2` → Static + idle.

## 5. C — Seat light

Each screen reacts to the logo: brightens while the logo is there, fades after it leaves — like
fairground chaser lights. It makes the motion readable from across the room even when the logo is
15 screens away.

```csharp
public enum SeatLightStyle { Off, BackgroundTint, BottomBar, Vignette, Frame }
public class SeatLightConfig
{
    public SeatLightStyle Style { get; set; } = SeatLightStyle.Off;
    public string ColorHex { get; set; } = "#FFFFFF";
    public bool UseGradingColor { get; set; } = false; // color = ColorGrader at the pass time → rainbow chase
    public float Strength { get; set; } = 0.35f;        // max opacity of the effect
    public int DecayMs { get; set; } = 3000;            // fade after the logo leaves
    public int AnticipationMs { get; set; } = 0;        // glow *before* the logo arrives (the clock is shared, the future is known)
}
```

**Math (generic, works for every movement type):** sample the deterministic position on a coarse
grid of past (and, with anticipation, future) times and take the strongest contribution:

```
light(t) = max over i in [−A/Δ, D/Δ] of  overlap(spriteRect(t − i·Δ), mySlice) · falloff(i·Δ)
Δ = 50 ms,  falloff = linear/exp decay to 0 at DecayMs (past) or AnticipationMs (future)
```

With D = 3 s that is ~60 extra `MovementCalculator` calls per frame per node — negligible. It is a
pure function of `t`, so all nodes and the preview agree, and a late joiner sees the correct
lights immediately. Hop gets a closed-form shortcut (dwell on me → full; else time since my last
departure within the last N hops) but the sampled path is the reference implementation.

Draw order: background → **seat light** → sprite. It lives in `AnimationLayerConfig` (per layer,
so it moves to `SceneLayer` in Tier 2.3). Pattern scenes: disabled (a grid touches every screen).

## 6. D — Speed profiles (Linear, SineWave)

Constant speed is what makes the ring feel mechanical. Distance must stay a closed-form integral of
speed, folded in double:

```csharp
public enum SpeedProfile { Constant, Surge, EaseIn }
// MovementConfig: SpeedProfile, SurgeAmount (0..0.9), SurgePeriodMs, EaseInMs
```

- `Surge`: `v(t) = v0·(1 + a·sin(ωt))` → `s(t) = v0·t + (a·v0/ω)·(1 − cos ωt)`. Speeds up and slows
  down around the room, like a swimming fish.
- `EaseIn`: `v` ramps 0 → v0 over `EaseInMs` after the item starts (`s` = quadratic, then linear).
  The logo "sets off" instead of appearing at full speed.

Lap-snap in the playlist uses `LapMs`; with `Surge`, choose `SurgePeriodMs` so an integer number of
periods fits a lap (`SceneChecks` hint) or lap-snap falls back to the mean speed.

**Later, not in this design:** position-dependent speed (slow in row turns) needs a precomputed
distance/time table per config — deterministic, but a separate piece of work.

## 7. E — Tempo grid (manual BPM)

A cheap 80 % of "audio-reactive" without audio capture. Scene-level:

```csharp
public class TempoConfig { public bool Enabled; public float Bpm = 120; public int BeatsPerBar = 4; }
```

- **Beat clock:** `beat(t) = (t − epoch) · Bpm / 60000`, epoch = the shared scene start.
- **Hop** with tempo: `DwellBeats` / `TravelBeats` replace the ms fields → landings fall on beats.
- **Pulse / Seat light / Bezel flash** (Tier 2.4): optional "on beat" / "on bar" triggers.
- **Tap tempo (phase 2):** a toolbar button during a show. A tempo change is a new clock segment
  `(T0Utc, beatAtT0, bpm)` starting at the next beat boundary, broadcast as a small command with a
  lead time (Tier 0.4 pattern). Players keep the list of segments → the beat clock stays continuous
  and deterministic; late joiners get the list in the scene state.
- Tier 3 audio-reactive then only has to *feed* this grid.

## 8. F — Convoy

`MovementConfig.CopyCount` (1–N). Copy `j` runs at `elapsed + j · Period / CopyCount` where Period
is the lap time (Linear on a ring) or `N/CopyCount` seats ahead (Hop, `Forward` only). "A logo on
every 5th seat, all moving together." Draw cost: `CopyCount` sprites. Random sequences: each copy
uses `Seed + j` (they are independent then, and may meet on a seat — acceptable).

Opposite-direction chasers and "meeting point burst" need scene layers (2.3) and events (2.4) and
are out of scope here.

## 9. G — Row mosaic

One giant logo spanning a whole row of 10 screens — the opener or closer of a round.

- New `MovementType.Mosaic`: the image is fitted to a **row's span** (from the seat stops of that
  row: `min(OffsetX)` … `max(OffsetX + Width)`), centered vertically.
- `MosaicRows = EachRow | FirstRow`: `EachRow` shows the full logo on both rows, each in its own
  reading direction (row 1 facing → its span runs the other way on the canvas; the logo is placed
  so it reads left-to-right for the people looking at it).
- **Reveal:** node with in-row index `i` fades/drops in its crop at `t = i · RevealStepMs`
  (`RevealStyle = Fade | DropIn | Flip`), then holds. Optional `HoldMs` then un-reveal in reverse.
- Bezel gaps are real distance in Physical mode, so the logo lines up across the bezels.
- Later: "shatter" into N small logos that Hop to their seats (needs segments, §10).

## 10. H — Motion segments

Playlists sequence *scenes*; segments sequence *motion inside one scene*:

```csharp
public class MotionSegment { public int DurationMs; public MovementConfig Movement = new(); }
// MovementConfig.Segments: List<MotionSegment>? (null = today's single movement); SegmentsLoop = true
```

Example "club tour": `Linear 2 laps` → `Hop RandomShuffle, 1 round` → `Hop to seat 7, dwell 5 s`
(Gather) → `Mosaic reveal`.

- Lookup: prefix sums over `DurationMs` (computed once at load) → current segment + local time.
- **Continuity:** a segment's start position is the previous segment's end position, computed at
  load by evaluating the previous segment at its end (pure). Linear gets `StartX` from it; Hop
  starts at the seat nearest to it. A short automatic blend (200 ms ease) hides any residual jump.
- Duration helpers in the editor: "N laps", "1 shuffle round" compute `DurationMs` from speed /
  `N · Period`.
- Biggest item; do it after A–G have proven their math.

## 11. I — GIF motion sync

The GIF frame index is already derived from the shared clock (`GetCurrentGifFrameIndex` /
`GetCurrentFrameIndexForSource` in `Program.cs`, looped with `SyncTiming.PositiveModulo`), so all
nodes show the same frame. Add:

- `AnimationLayerConfig.GifClock = Time | Distance`: with `Distance` the frame time is
  `distanceTraveled(t) / StrideLength × gifDurationMs` — a walking/swimming cycle matches ground
  speed. `distanceTraveled` is closed-form for Linear (+ speed profiles) and Hop.
- `HoldFrameWhileDwelling` (Hop): freeze on a chosen frame or keep looping at a slower rate.

## 12. J — Sprite styling

Logos come with all kinds of edges and colors; a party background can be anything.

```csharp
public class SpriteStyle
{
    public bool Shadow; public float ShadowBlur = 8; public float ShadowOffsetCm = 0.3f; public float ShadowOpacity = 0.5f;
    public bool Outline; public string OutlineColorHex = "#FFFFFF"; public float OutlineWidthPx = 3;
    public bool Glow;    public string GlowColorHex = "#FFD700";  public float GlowRadiusPx = 12;
}
```

D2D effect graph (`Shadow`, `Morphology` dilate + flood for outline, `GaussianBlur` for glow)
composed per draw. For patterns above ~50 cells, style is disabled (cost) — `SceneChecks` hint.

## 13. Authoring UI (docked Scene editor)

- **Motion:** new types `Hop` and `Mosaic` in the movement picker. Hop shows Sequence, Travel,
  Dwell/Travel (seconds, or beats with Tempo), Easing, Arc (cm), Idle, Seed + "Randomize".
  Speed profile under Linear/SineWave.
- **Look:** Seat light and Sprite style sections; Convoy count.
- **Scene:** Tempo (BPM, beats per bar) — later with Tap in the toolbar.
- **Preview:** `ScenePainter` uses the seat stops and the same functions, so every new option is
  visible in the room view before it goes to 20 machines.
- **Starter presets** (feed Tier 2.2's preset library): *Relay lap* (Hop Forward, bob, seat light
  bottom bar), *Who's next?* (Hop RandomShuffle, teleport, anticipation glow), *Chaser lights*
  (Linear surge + rainbow seat light), *Logo parade* (Linear convoy ×4), *Row reveal* (Mosaic).

## 14. Determinism checklist for reviewers

- Seat stops come from the apply path, identical on all nodes; players never compute them from
  their own monitor.
- Every new function takes `elapsedMs` (or beat time) and config only; long/double folding as in
  `MovementCalculator` for multi-day uptime.
- Random sequences use an integer hash of `(Seed, round/k)`, never `System.Random`.
- Seat light samples the same `MovementCalculator` — never a history buffer of rendered positions.
- Facing during dwell comes from the hop index, not from frame-to-frame state.

## 15. Tests

- `SeatPathTests`: stop centers from mixed-width / Physical layouts; shortest signed distance on a
  ring incl. across the seam; open Snake path.
- `HopMovementTests`: dwell → exact seat center; arc apex at `p = 0.5`; continuity at every
  segment boundary; long-run (days) without drift; two "nodes" with different offsets agree.
- `HopSequenceTests`: shuffle bag visits every seat exactly once per round, never repeats across a
  round boundary, identical for the same seed; `RandomFree` never repeats consecutively; PingPong
  period.
- `SeatLightTests`: light = full while the sprite is on the slice, decays to 0 at `DecayMs`,
  anticipation rises before arrival; identical for any render start time (late joiner).
- `SpeedProfileTests`: `s(t)` equals numeric integration of `v(t)`; monotonic.
- `TempoTests`: hop landings on beat boundaries; segmented tempo clock continuous.
- `MosaicTests`: row span from stops; reveal time per in-row index; facing row reads correctly.
- `SceneChecks` tests for the new warnings (Hop + Simultaneous, Hop + Pattern, etc.).
- Manual: two local monitors (Hop Forward + Random, seat light, mosaic), then the E2E multi-client run.

## 16. Rollout

1. **Seat stops + Hop (Forward, PingPong, RandomShuffle, RandomFree) + idle/landing** — model,
   `SeatPath`, calculator, player, preview, editor. *Biggest visible win.*
2. **Seat light** (sampled implementation).
3. **Speed profiles** + **Convoy**.
4. **GIF motion sync** + **Sprite styling**.
5. **Row mosaic**.
6. **Tempo grid** (manual BPM), then tap tempo.
7. **Motion segments.**

Each step is independently shippable and keeps old configs byte-identical (new fields default to
off; `MovementType` values appended at the end of the enum).

## 17. Open questions

| Question | Assumption until decided |
|---|---|
| Hop default timing | Dwell 2.5 s, travel 0.9 s → a 20-seat Forward round ≈ 68 s |
| Random default | `RandomShuffle` (everyone gets a turn per round) |
| Offline seats during a show | Still visited (the stop list is frozen at apply); re-apply to drop them. A "skip empty seats" option would need a re-apply on disconnect — decide after E2E testing |
| Seat light per layer or per scene | Per layer (moves with the sprite that causes it) |

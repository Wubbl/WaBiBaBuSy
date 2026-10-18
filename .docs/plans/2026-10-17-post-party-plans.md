# Post-Party Plans (2026-10-17)

**Status:** proposal, nothing implemented · **Context:** written the week before the first LAN party (~30 people,
seat map like the 2026-10-17 sketch: two tables, four rows, BiBaBu members and guests mixed, seats that can be empty).
Everything here waits for party feedback; the order in §0 is a recommendation, not a commitment.

Covers: playlist extras (per-client opt-out, cross-item transitions, lap-snap beyond Linear), roadmap 2.4 Portal,
2.3 layer follow-ups, seat-map extras (per-seat DPI override, standby / two-phase switch), and — across all of them —
**machines dropping in and out all the time**.

Related: [`2026-09-17-lan-party-roadmap.md`](2026-09-17-lan-party-roadmap.md),
[`2026-09-17-show-reliability-design.md`](2026-09-17-show-reliability-design.md) §4,
[`2026-09-17-crossing-effects-and-events-design.md`](2026-09-17-crossing-effects-and-events-design.md) §3, §6,
[`2026-09-17-scene-layers-design.md`](2026-09-17-scene-layers-design.md),
[`2026-09-17-physical-canvas-design.md`](2026-09-17-physical-canvas-design.md) §6.

---

## 0. Recommended order

| # | Item | Size | Why this position |
|---|---|---|---|
| 1 | ~~**Ghost seats** (§1)~~ **done 2026-10-17** (`29d3659`) | S–M | Dropouts happen all night; today they reshape the ring. Everything else builds on a stable layout |
| 2 | ~~**Per-client opt-out** (§2.1)~~ **dropped 2026-10-18** — quitting the app is the opt-out; its seat becomes a ghost | — | — |
| 3 | ~~**Standby / two-phase switch** (§5.2)~~ **done 2026-10-18**, simpler than designed (see §5.2) | S | Needed for gapless item switches and for transitions |
| 4 | ~~**Cross-item transitions** (§2.2)~~ **Fade (through black) + ring Wipe done 2026-10-18**; true crossfade open (needs both scenes in one player) | M | Rides on 3 |
| 5 | ~~**Lap-snap for every movement** (§2.3)~~ **done 2026-10-18** (Bounce snaps to the horizontal round trip only; no lcm of both axes) | S–M | Small math, but only noticeable once transitions exist |
| 6 | ~~**Portal** (§3)~~ **done 2026-10-18** — forward scan on the 16 ms grid instead of an analytic crossing time; no sprite clipping to the door | M | Needs the analytic crossing time (§3.1), which also feeds lap-snap |
| 7 | **Layer follow-ups** (§4) | S each, video M | Independent slices, pick by demand |
| 8 | **Per-seat DPI override** (§5.1) | S | Only if a seat's reported DPI turns out wrong at the party |

---

## 1. Dropouts as the normal case ("ghost seats")

### 1.1 What happens today

- A client that closes, sleeps or loses the network is removed from the connected list (stream end, or the
  heartbeat sweep after ~40 s, `WallpaperSyncService.SweepDeadClients` → `RemoveClient`).
- The room is built from **connected** clients only (`RoomNodeSources.ServerNodes` ← `GetConnectedClients()`),
  so the next **Play / playlist item / stand-in event scene** is laid out without that seat: the ring gets
  shorter, the sprite skips the empty table spot, every later seat's `OffsetX` shifts and the lap time changes.
- A scene already playing is not re-laid-out (good): the dropped seat just shows nothing; when it comes back,
  session resume puts it mid-scene at the right position.
- `topology.json` keeps the machine's order + gap, and `tags.json` its tags, so a returning machine gets its
  old seat back. Only the *layout in between* is wrong.

### 1.2 Design

**A seat outlives its machine.** The room keeps every known topology entry as a seat; a seat whose machine is
offline becomes a **ghost**: it keeps its place, width and gap in the layout, nothing is sent to it, and its tile
is drawn greyed ("offline 12 min").

- `TopologyEntry` gains `LastWidthPx`, `LastHeightPx`, `LastPixelsPerCm` (written when a client registers or
  reports its monitor). A ghost is laid out with those, so the canvas stays identical whether the person is at
  their PC or not.
- `RoomNodeSources.ServerNodes` merges connected clients with topology entries not connected → `RoomNode` with
  `IsConnected = false` (already a field). `CrossScreenPlanner` keeps ghosts in the **layout**, but drops them
  from the **send list** (no command to a client that is not there).
- Room view: ghosts greyed, right-click → *Forget seat* (removes the entry, the ring closes) and *Mark empty
  seat…* (a ghost with a chosen width, no machine; for "nobody sits here tonight"). The latter replaces the
  row-start gap trick of 2026-10-17 for whole-seat holes; the gap stays for odd spacing.
- Rejoin: unchanged — the resume path already sends the active command with its original `T0`. With ghosts the
  rejoining seat lands *exactly* where the sprite is, because the layout never changed.
- Events: a targeted event for an offline seat is a no-op for that seat (nothing to send). Identify on a ghost
  is meaningless; the status line says "3 of 5 targets online".
- Health strip: "27/30 online · 3 ghost seats".

**Determinism:** layouts depend only on topology entries + seat map, not on who happens to be connected at the
moment of Play → every node computes the same canvas no matter the join order. Test: build the layout with a
seat connected and disconnected → identical `NodeLayout`s for the others.

**Edge cases:** a machine that never connected this evening but has a stale entry from last month → ghosts from
old parties. Mitigation: *Forget offline seats* button, and ghosts older than N days (setting, default 14) are
not loaded.

---

## 2. Playlist extras

### 2.1 Per-client opt-out — dropped 2026-10-18

Not needed: someone who wants out quits WaBiBaBuSy; thanks to ghost seats (§1) their seat keeps its place and the
show goes on around it. Kept below for reference.

A player who wants their own wallpaper (or to free GPU while gaming) opts out; the show goes on around them.

- **Two entry points, one flag:** client tray menu *Take a break from the show* (the person at the PC decides),
  and room view right-click *Exclude from show* (the operator decides). Both set `ClientInfo.OptedOut` on the
  server (client → server via a small `SetOptOut` RPC or a heartbeat field).
- Layout: an opted-out seat behaves **like a ghost** (§1): it keeps its place so the ring timing does not change;
  it just gets no command (and a Stop when it opts out mid-scene). That is why §1 comes first.
- Events: Everyone-targeted events skip opted-out seats; an event aimed at that seat or tag explicitly still
  reaches it? **No** — opt-out wins, otherwise a "break" is not a break. Identify is the exception (setup tool).
- Fullscreen games already pause the player locally (`OnFullscreenStateChanged`); opt-out is the explicit,
  persistent version. Persist per client id in `topology.json` (survives restarts), cleared with *Rejoin show*.
- Tile: "on a break" badge. Health strip counts it separately from offline.

### 2.2 Cross-item transitions (roadmap 2.5) — Fade + Wipe implemented 2026-10-18

Built on the standby switch instead of a second runtime: **Fade** = each player darkens its own frame (old one before
the switch, new one after — a dip through black, not a crossfade); **Wipe** = the incoming player clips its *window*
with `SetWindowRgn` to the part of its slice the boundary has passed. Measured: window alpha is ignored for the
flip-model swap chain, regions are honored. Open: a true crossfade (both scenes in one player, as designed below),
BezelByBezel (trivial now: a per-seat show delay), tile dissolve (region of seeded tiles). Original design:

From the effects design §6, on top of the two-phase switch (§5.2):

- `PlaylistItem.TransitionIn` (`Cut` default, `Fade`, `RingWipe`, `BezelByBezel`) + `Playlist.DefaultTransition`
  + `TransitionMs` (default 1500).
- Player: the standby scene (§5.2) loads into a second set of `LayerRuntime`s; from `T0_next` it draws both:
  - **Fade:** old × (1 − p), new × p.
  - **RingWipe:** boundary `b = p × CanvasWidth` (wrapping); a node clips old/new at `b − OffsetX` in its own
    slice → the wipe visibly runs around the room. Mirrored nodes clip in mirrored local x (`NodeMapping`).
  - **BezelByBezel:** each node cuts when `b` passes its `OffsetX` — cheapest, no double draw.
- Room preview: `ScenePainter` paints both scenes with the same p (same math helper `TransitionMath.Progress`).
- **Dropouts:** a node that joins mid-transition gets the *new* scene only (resume sends the active command;
  after `T0_next` that is the new one) — it pops in, which is fine. A node that misses `LOAD_NEXT` falls back to
  a Cut when the regular start command arrives.
- GPU: two scenes at once for `TransitionMs`; with video layers (§4.1) cap to one video per runtime.

### 2.3 Lap-snap for every movement — implemented 2026-10-18

Done as below, simplified: Bounce uses the horizontal round trip only (no lcm search), the sprite size is the
server's estimate (`ComputeEffectiveAnimationSize`, images/GIFs exact, video = TargetHeight square), the dwell is
rounded up without a cap (as before for Linear). Original plan:

Today lap-snap rounds an item's dwell to whole laps for **Linear** only, using `ContentWidthPx = 0`
("best-effort"). Goal: the item ends when the motion is at a natural rest point for every movement type.

- New pure `MovementPeriod.PeriodMs(MovementConfig, canvas, spriteSize)` in Models:
  - Linear / Endless: `(CanvasWidth + spriteW) / speed` (Ring: `CanvasWidth / speed`).
  - Bounce: the 2D bounce is periodic in x and y separately → `lcm(2·(W−w)/vx, 2·(H−h)/vy)` rounded to frames;
    if the lcm is longer than 3× dwell, snap to the x period only (the "back at the left edge" moment).
  - SineWave: the horizontal lap, as Linear (the wave phase is cosmetic).
  - Circular: `2π / angularSpeed`.
  - RandomWalk / Static / IconZone: no period → no snap (dwell as configured).
- `ContentWidthPx` becomes real: the player reports `_animWidth` in `READY:{width}` (show-reliability §4 note),
  or the server computes it from the decoded image size it already measures for Suggest (`MediaAnalyzer`).
  Prefer the server-side computation: no protocol change, works for ghosts.
- Dwell = `ceil(dwell / period) × period`, capped at dwell + 1 period. Unit tests per movement type against a
  brute-force position sampler (`MovementCalculator` at t and t + period equal within 1 px).
- **Dropouts:** none — the period is a property of the layout, which §1 keeps stable.

---

## 3. Roadmap 2.4 — Portal — implemented 2026-10-18 (simplified)

Built without §3.1: positions are pure functions of time, so `SpriteEffects.FindPortal` simply scans the 16 ms grid
±400 ms around now (ahead as well as back) for the sprite straddling this node's edge — works for every movement type,
deterministic on every seat. The door is drawn over the sprite at the edge; the sprite is **not** clipped to the
ellipse (the screen edge already cuts it). Original plan:

A sprite leaving a screen looks like it goes through a door and comes out of a matching door on the next seat.

### 3.1 Analytic crossing time (shared prerequisite)

`CrossingMath.NextCrossing(MovementConfig, layout, nodeOffsetX, elapsedMs) → (tEntryMs, tExitMs)?`:

- Linear / Endless / SineWave: closed form `(edge − x0) / vx` with the wrap of a Ring.
- Bounce: closed form per segment (reflection is piecewise linear).
- Circular, RandomWalk, IconZone path: **sampling** — step 16 ms ahead up to `PortalLeadMs` and detect the
  sign change of `x − edge`; still deterministic (pure function of elapsed), only slower. Cap the lookahead.
- Used by Portal (open before arrival), lap-snap (§2.3, Bounce period cross-check) and later "seat light"
  (seat-choreography design).

### 3.2 Drawing

- `BezelEffect.Portal` (the enum value exists). Per node, per crossing: an ellipse at the edge, opening
  0 → `1.3 × spriteH` over `PortalLeadMs` (default 400) before `tEntry`, closing after `tExit`; the sprite is
  clipped to the ellipse while it straddles the edge (D2D layer with an ellipse geometry mask).
- Both sides: the exit portal on node *k* and the entry portal on node *k + 1* open on the same `tEntry` — both
  derive it from the same math, no messaging.
- Ring seam and row turns: the turn gap is invisible space; the portal pair is at the end of row *r* and the
  start of row *r + 1* — this is where Portal reads best ("it teleported to the other table").
- Room preview: `ScenePainter` draws the same ellipse (shared `PortalMath.Radius(tLocal)`).
- Disabled with Pattern grids (continuous crossings), as the other bezel effects.
- **Dropouts:** a ghost seat (§1) shows nothing, but its neighbours still open their portals on time, so the
  sprite visibly "dives into" the empty seat and comes out after it — arguably nicer than a gap.

### 3.3 Tests

`CrossingMathTests` (closed form vs sampled for Linear/Bounce/Sine, Ring wrap, mirrored rows), determinism
guard (same inputs → same times on two calls with different start offsets), exact-frame test-mode step with
Portal at `tEntry − 200 ms`, `tEntry`, `tExit + 200 ms`.

---

## 4. Roadmap 2.3 — Scene-layer follow-ups

Independent slices; each keeps the layer invariants (CLAUDE.md "Scene Layers").

### 4.1 Video on upper layers (M)

- Player: a `LayerRuntime` gets its own LibVLC media player when `Animation.AnimationPath` is a video; **cap 2
  video layers per scene** (SceneChecks error above), decode to a D2D bitmap per layer as layer 0 does.
- Sync: video position = `LayerElapsed mod duration` with the same seek-on-drift rule as layer 0.
- Remote: assets already travel per layer; nothing new on the wire.
- Risk: GPU/CPU on weaker guest PCs → the health strip should show per-node frame time before we encourage it.

### 4.2 Spawn / despawn windows (S–M)

- `SceneLayer.VisibleFromMs` / `VisibleUntilMs` (nullable) and `RepeatEveryMs` (0 = once): a layer appears on
  the shared clock, e.g. "the cat walks in every 60 s for 10 s". Pure function of `LayerElapsed`, fade 250 ms
  (reuse `ShowEventMath.Envelope`).
- Editor: two number fields + repeat in the Layers strip's layer settings; preview honours them.
- Cheap alternative already available: playlist interrupts with a Traversal event. Only build if people want
  layered *scenes* that evolve.

### 4.3 Layer presets — "Add layer from preset" (S)

- Layers strip ＋ ▾ → *From preset…*: pick a preset, take its **top** layer (or choose which), append it with its
  assets. Assets copy like preset import (`PresetBundle` relative paths).
- Reverse: right-click a layer → *Save layer as preset* (a scene with an empty background and that one layer).

### 4.4 Suggest "add as layer" (S)

- Suggest card menu: *Keep* (today, replaces the scene) and *Add as layer* (appends the card's sprite layer on
  top of the current draft, keeps the draft's background). Recipe builder already returns a scene; take its
  layers ≥ 0 that carry the picked media.

### 4.5 Debug overlay for upper layers (S)

F11 overlay shows layer 0's path / trail only; add a layer selector (cycle with F12) — dev comfort, low priority.

---

## 5. Seat-map extras

### 5.1 Per-seat DPI override (S)

Physical canvas uses each node's reported `PixelsPerCm`. Windows sometimes reports nonsense (TVs, some
projectors, EDID without size) → that seat's sprite is too big or small in cm.

- `TopologyEntry.PixelsPerCmOverride` (nullable) and/or `PanelDiagonalInch` (friendlier: "27\"" → ppcm from the
  resolution). Room view right-click → *Screen size…* with the diagonal field and a live "sprite 10 cm = N px".
- Planner uses override ?? reported. Persisted with the seat, so it also applies to a ghost (§1).
- Quick check at the party: show a 10 cm ruler event on all seats and measure (could be an Identify variant).

### 5.2 Standby / two-phase switch — implemented 2026-10-18 (simplified)

**What was built** (not the design below): no `LOAD_NEXT` message, no second runtime inside the player. Each host
keeps the monitor's old *player process* while it spawns the new one; the new player keeps its window hidden until
the shared start (`ReachedSharedStart`), then shows on top, and the host stops the old one 250 ms later
(`RetiringPlayers`). The start lead every switch already has (0.8–4 s, ≥ 3 × worst RTT) is the standby window.
Dropouts need nothing extra: a rejoining seat gets the active command as before. Two processes per monitor for the
lead time is the cost. Transitions (§2.2) can build on it: Fade = ramp the new window's layered alpha
(`SetLayeredWindowAttributes`) over `TransitionMs` instead of showing it at once; wipes need the in-player
design below. The original design, for reference:

What it means: today every item switch is "stop old players → start new players at a future T0" — each node
spawns and decodes the next item in the lead time, then reveals on the same frame. That already looks
synchronous, but there is a short black/blank gap and a heavy load spike at every switch. **Standby** loads the
next item **while the current one still plays**, then flips at the same instant everywhere — no gap, and the
precondition for transitions (§2.2).

From show-reliability §4, made concrete:

- `t_switch = T0_current + dwell`. At `t_switch − AnnounceLeadMs` (default 3000, ≥ 3 × worst RTT) the
  orchestrator broadcasts `LOAD_NEXT(scene, layout, T0 = t_switch)` — content is already prefetched.
- Player: `PlayerCommandLoadAnimation { Standby = true }` builds the second runtime set (scene-layers refactor
  made this possible: everything per layer lives in `LayerRuntime`); `READY:standby` back. At `T0` it swaps (or
  runs the transition).
- Server waits for nothing: if a node is not ready at `T0`, it simply shows the new scene when it is (it still
  computes the position from `T0`, so it is in sync, just late) and the health strip marks it "late switch".
- **Dropouts (the important part):**
  - Node offline at announce time → it is a ghost; nothing sent.
  - Node rejoins between announce and `t_switch` → resume sends active **and** next (stored on the
    coordinator), so it switches with everyone.
  - Node drops between announce and switch → nothing to do; on rejoin it gets the new active command.
  - Server restarts mid-show → existing behaviour (show restore), no standby state to recover.
- Memory: two scenes resident for `AnnounceLeadMs` + `TransitionMs`; with big GIFs on 4 GB GPUs, fall back to
  "stop then start" when the standby load reports out-of-memory (`ERROR:` reply) — graceful degradation.
- Tests: orchestrator timeline test (announce at t − lead, switch at t), player IPC test for Standby + swap,
  test-mode scenario `standby-switch.json` with exact frames 100 ms before/after `T0` on 2+ machines, plus a
  `fault` step that drops a client inside the announce window.

---

## 6. Open questions for after the party

1. How many people actually wanted out of the show (decides how prominent §2.1 must be)?
2. Did seats change a lot (ghost seats) or did people mostly stay?
3. Were item switches noticeably gappy (decides whether §5.2 jumps ahead)?
4. Any monitor with a wrong size in physical mode (§5.1)?
5. Which events got used — would *scheduled* targeted events (tag + playlist interrupt) be wanted?

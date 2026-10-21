# BiBaBo — "Bier, Bart und Bombe" (room-wide bomb-defuse mini-game) — Design

**Created:** 2026-10-21 · **Status:** implemented 2026-10-21 (plan `2026-10-21-bibabo-bomb-game-plan.md`); clicks / Recycle Bin / multi-machine still to be checked by hand (manual checklist §8c); Claude's own calls marked *[call]* and summarised in §12
**Builds on:** one-shot show events ([`2026-10-03-show-events-design.md`](2026-10-03-show-events-design.md)) — same model, transport, replay, player/preview drawing split. Read that first.

---

## 1. Intent

A party gag for the LAN: on a trigger (Events-strip button or Ctrl+Alt+B) every free seat goes black, a bomb (the BiBaBu logo with
bombs instead of boobs) lands on one random seat with a countdown, and three cables — red, green, blue —
run from the bomb across the neighbouring monitors to three other seats. At each cable end sits a small
riddle that tells whether that cable is **live** (connected to the trigger) or **dead**. People have to
get up, walk the room, follow the cables, solve the riddles, come back and **click the right cable at the
bomb** before the timer runs out. Easter egg: dragging the Windows **Recycle Bin** desktop icon onto the
bomb defuses it.

Success = people physically walk around laughing, the outcome (BOOM / defused) hits every screen at the
same moment, and it never breaks a running show.

### Goals
- One trigger starts the game on every seat; it ends by itself (defused / exploded / timeout / aborted)
  and the previous scene or idle wallpaper comes back.
- Cables are continuous across monitors (same seat-map canvas as the scene sprite), so following them
  along the row works physically.
- Riddles look scary at first glance and are solvable in < 1 min with a clear head (a beer in hand is fine).
- Only the bomb seat accepts the cut; the server decides the outcome (one authority, first action wins).

### Non-goals (v1)
- Sound (players have no audio path for events). See Q7.
- Scores / leaderboard / multiple rounds in a row.
- Per-seat answer entry (typing a solution on the end seat) — people carry the answer back in their heads.
- Interactivity anywhere except the bomb seat.

### Anti-goals
- Swallowing or blocking normal mouse/desktop input on any seat (we only *observe* input).
- Anything that could crash explorer (no `WS_EX_TRANSPARENT`, no DXGI in the main process — invariants stand).
- Anything that makes the answer readable from the network traffic on a seat (the live cable stays on the server).

## 2. Player-facing flow (timeline, all on the shared clock)

`localMs` = event-local time exactly as for other show events (`ShowEventMath.LocalMs`).

| Phase | localMs (default) | Every seat | Bomb seat | Cable-end seats |
|---|---|---|---|---|
| Blackout | 0 – 1 000 | scene fades to solid black (stays black for the whole game) | | |
| Roulette | 1 000 – 4 000 | a white "bomb shadow" hops seat to seat, decelerating (seeded sequence), lands on the bomb seat | | |
| Drop | 4 000 – 5 000 | | bomb logo drops in, centre screen, slight bounce | |
| Cables | 5 000 – 8 000 | cables grow from the bomb along the canvas, over every seat they cross | 3 coloured terminals on the bomb | card fades in when its cable arrives |
| Countdown | 8 000 – 8 000 + `TimerMs` (default 5:00) | small countdown in a corner of every seat | big 7-segment LED timer next to the bomb, fuse spark animates; last 30 s: timer blinks red, cables pulse | riddle card at the cable end |
| Outcome | from the outcome event's T0, ~8 s | BOOM or DEFUSED (§6) | | |

Timer reaching 0:00 does **not** explode by itself on the players: they hold "0:00" blinking until the
server's outcome event arrives (§5.3). That keeps one authority and avoids a defuse-at-0:01 race.

## 3. Model (`WaBiBaBuSy.Models/Show/`)

Two new `ShowEventType` values: **`Bomb`** and **`BombOutcome`** (string-serialized → old clients ignore them).
`ShowEvent` gets one nullable sub-object instead of many loose fields *[call]*:

```csharp
public sealed class BombGame                       // ShowEvent.Bomb, set on Bomb and BombOutcome events
{
    public string GameId { get; set; } = "";       // = EventId of the Bomb event; BombOutcome refers to it
    public int BombOrder { get; set; }             // NodeLayout.Order of the bomb seat
    public float BombX { get; set; }               // bomb centre, canvas units (unwrapped)
    public float BombY { get; set; }
    public float BombSize { get; set; }            // bomb sprite height, canvas units
    public int[] RouletteOrders { get; set; } = [];// seat order hops, last = BombOrder
    public int IntroMs { get; set; } = 8_000;      // blackout + roulette + drop + cables
    public int TimerMs { get; set; } = 300_000;
    public List<BombCable> Cables { get; set; } = new();
    // BombOutcome only:
    public BombResult Result { get; set; }         // Defused, Recycled, WrongCable, Timeout, Aborted
    public int CutCable { get; set; } = -1;        // index into Cables (WrongCable / Defused)
    public int RemainingMs { get; set; }           // frozen timer shown on Defused
}

public sealed class BombCable
{
    public string ColorHex { get; set; } = "";     // #E53935 / #43A047 / #1E88E5
    public int EndOrder { get; set; }              // seat with the riddle card
    public float[] Points { get; set; } = [];      // x0,y0,x1,y1,… canvas units, UNWRAPPED x (may exceed CanvasWidth on a Ring)
    public BombRiddle Riddle { get; set; } = new();
}

public sealed class BombRiddle
{
    public RiddleKind Kind { get; set; }           // Math, Tangle
    public string? Formula { get; set; }           // Math: single line, Unicode superscripts (see §4.1)
    public float FormulaAspect { get; set; }       // measured on the server with TextSprite (like Announce)
    public float[][]? Strands { get; set; }        // Tangle: 3 polylines in card-local units 0..1
    public int[]? StrandExits { get; set; }        // Tangle: which exit (0 = trigger, 1/2 = ground) each strand ends at
}
```

**The live cable is not in the event.** The server keeps it in `BombGameService` (§5). Clients only get
what is drawn; the answer is derivable only by solving the riddles.

Everything a player draws is a pure function of `(event, NodeLayout, localMs)`, as for every other event.
Geometry (routes, card positions, tangle strands) is **computed once on the server and shipped** *[call]* —
players never run the generator, so there is nothing to keep bit-identical across machines except the
existing drawing math.

### 3.1 Duration
`Bomb.DurationMs = IntroMs + TimerMs + 15 000` (outcome reserve). `ShowEventMath.MaxDurationMs` (10 min)
caps the timer at ~9:30 — fine for v1 *[call]*; raise the cap only if a longer timer is wanted.
A Bomb event stops being drawn at the T0 of its `BombOutcome` (players and preview look the outcome up by `GameId`).

## 4. Generation (`BombGameFactory`, pure, seeded, `WaBiBaBuSy.Models`)

Inputs: the room's `SeatMapLayoutResult` (always the seat-map canvas, whatever the scene's layer
distribution is), seed, settings. BiBaBo needs a shared canvas; with fewer than 2 free machines it refuses (status line "Not enough free seats — n busy").

- **Who plays:** the seats selected in the Events strip target (Everyone / tag / seat — the same `EventTarget` as
  every other event; the hotkey uses the strip's current target), server and projector included, **minus busy seats**
  (§8.1). Busy seats are neither bomb nor cable end; they still receive the event (they draw it behind their window).
- **Bomb seat:** uniform random over the free target machines (seat = machine, see §12).
- **End seats:** 3 distinct machines ≠ bomb machine, preferring machines ≥ 2 away along the traversal so people have
  to walk. Fewer than 4 seats → ends may repeat or land on the bomb seat's far side *[call]*.
- **Roulette:** 8–12 hops over random seats, last = bomb seat.
- **Cable route:** Ring/Snake canvases are one long strip in seat order, so the cable runs along **x** from
  the bomb terminal to the end seat's card (Ring: shorter way round; x stays unwrapped, players draw with
  `NodeMapping.WrapCopies` per segment). y = smooth seeded noise (3 summed sines) kept inside the vertical
  extent of the seat under each x, so the three cables cross each other repeatedly over the seats in between.
  One point every ~30 canvas units. Bezel/distance gaps in the canvas simply hide the cable — that matches the room.
  Parallel traversal (rows stacked): same routine, y eases from the start row band to the end row band
  in the middle third — works, may look odd; acceptable for v1 *[call]*.
- **Card:** centred on the end seat, ~55 % of its height, the cable ends at the card's left/right edge.

### 4.1 Riddles
House rule printed on the bomb seat under the timer: **"LIVE ⇔ result = 1"** / for tangles **"LIVE ⇔ leads to the DETONATOR"** (all on-screen text in English).
Exactly one cable is live; each card shows its cable's colour as a header stripe.

**Math** (single line, Unicode superscripts `⁰¹²³⁴⁵⁶⁷⁸⁹⁽⁾⁺⁻` — GDI+ Segoe UI renders them, so `TextSprite`
works unchanged). Templates with random numbers, each with a live (=1) and a dead (≠1) form, so dead and
live cards look equally scary:

| Trick | Live (= 1) | Dead (≠ 1) |
|---|---|---|
| power of zero | `(17·3 − 4²)⁽⁹⁻⁹⁾` | `(17·3 − 4²)⁽⁹⁻⁹⁾ · 0⁷` → 0 |
| zero factorial | `0! · (88 − 87)¹²³` | `0 · 7! + 1¹²³ − 1` → 0 |
| hidden zero factor | `(4·5 − 19)²⁰²⁶` | `(19 − 4·5 + 1) · 987 654` → 0 |
| sign power | `(−1)²⁰²⁶` | `(−1)²⁰²⁷` → −1 |
| order of operations | `12 − 3 · 4 + 1` | `(12 − 3) · 4 + 1` → 37 |
| difference of squares | `101² − 100² − 200` | `99 · 101 − 100²` → −1 |
| reciprocal | `(7/8) · (8/7)` | `(7/8) ÷ (8/7)` → 49/64 |

No ambiguous notation (`6 ÷ 2(1+2)` style) — arguments are fun, but the game must have one right answer.
Generator unit test: every generated formula is evaluated by a tiny evaluator in the test and matches its
intended live/dead value.

**Tangle** (the "labyrinth with the cables"): the card shows three spaghetti strands entering on one side;
one continues the cable's colour at the entry, all three turn grey after 10 % of the way and cross each
other 6–10 times; the far side has three exits — one **DETONATOR** (detonator icon), two **GROUND** (⏚).
Live ⇔ the cable's strand ends at the DETONATOR. Strands are the same seeded-sine routine as the cables.

Mix per game: each card random Math or Tangle (setting: Math only / Tangle only / mixed, default mixed).
Exactly one live cable, chosen uniformly.

## 5. Server

### 5.1 Trigger
- **Button** in the Events strip: 💣 BiBaBo, next to the other event buttons (enabled in server mode, also when
  nothing plays — the stand-in scene carries it). While a game runs it reads **■ Abort BiBaBo**.
- Global hotkey **Ctrl+Alt+B** (server only, same `GlobalHotkeyService` thread; works with the window closed;
  AltGr+B on German keyboards — not a common shortcut). Respects the strip's "Hotkeys" check box.
- Pressing the trigger while a game runs = **abort** (outcome `Aborted`, quiet fade back).

### 5.2 `BombGameService` (Core, app-lifetime, owned next to `ServerPlaybackHost`)
- `StartAsync()`: refuse if a game runs or < 2 seats; build the event with the factory; pick the live
  cable; ship it through the existing `ServerPlaybackHost.FireEventAsync` path (stand-in scene when idle, replay
  on resume/Resync, dedupe — all reused). Asset: the bomb logo travels as the event's `ImagePath` (existing single-asset slot).
- Holds `{GameId, LiveCable, BombSeatNodeId, DeadlineUtc = T0 + IntroMs + TimerMs}`.
- `OnAction(clientId, gameId, action)` — first valid action wins, everything after is ignored:
  - wrong game id / not the bomb seat's client / after deadline + 300 ms grace → ignored (logged);
  - `Cut(i)`: i == live → `Defused`, else `WrongCable`;
  - `Recycle` → `Recycled` (counts as defused);
- Timer task at the deadline (+300 ms grace for in-flight reports) → `Timeout`.
- Outcome = new `BombOutcome` event (`T0 = now + lead`, same `Bomb` object with `Result`/`CutCable`/`RemainingMs`),
  fired through the same path; `KeepIdleSceneUntil` extends to its end.
- Stop/new show while a game runs: game ends silently (`Aborted`); playlist item switches don't end it (§12) *[call]*.

### 5.3 Client → server channel
New unary RPC (proto, new numbers only):
```proto
rpc ReportShowAction(ShowActionReport) returns (ShowActionAck);
message ShowActionReport { string client_id = 1; string event_id = 2; string action = 3; int32 cable = 4; int64 client_utc_ms = 5; }
message ShowActionAck { bool accepted = 1; }
```
*[call]* A unary RPC rather than a new field on the `SyncStream` upstream: the stream is ack-only today and a
separate call is simpler to test. The server machine's own seats call `BombGameService.OnAction` directly.

## 6. Outcome visuals (pure math, `BombMath`)

- **WrongCable / Timeout → BOOM:** cut cable snaps (two ends sag); white flash on the bomb seat; a shockwave
  ring expands over the canvas from the bomb centre (each seat lights up when the ring reaches it — a room-wide
  wave); fire-palette particle burst (reuse `CelebrationMath` with a fire palette and the bomb x as origin
  band); seeded screen shake (±6 units, decaying) on every seat; "BOOM!" text sprite on every seat.
- **Defused:** remaining cables go grey, timer freezes at `RemainingMs`, green "DEFUSED — 2:31 left",
  confetti (`CelebrationMath`, party palette).
- **Recycled:** bomb shrinks into the Recycle Bin position on that seat with a spin; "RECYCLED ♻" + confetti.
- **Aborted:** 1 s fade back, no text.
- Then 1 s fade back to the scene underneath (the event ends; the stand-in scene stops if it carried it).

## 7. Drawing

- **Player** (`Program.Bomb.cs`, next to `Program.ShowEvents.cs`): drawn in the same pass as the other events,
  in canvas units under `Scale`. Primitives: `FillRectangle` (black fill, 7-segment LED segments — no text needed
  for the timer), `DrawLine` with round caps per cable segment (only segments intersecting this slice), ellipses
  (terminals, shockwave), `DrawBitmap` (bomb logo; TextSprite bitmaps for formulas, "BOOM!", labels — rendered
  on the stdin thread on receipt like Announce). Missing logo → procedural bomb (black circle, fuse, spark) *[call]*.
  Brushes cached; no per-frame allocations.
- **Room preview** (`ScenePainter.Bomb.cs`): same math with Avalonia drawing, so the operator sees the
  whole game (and the cable routes across the room). The preview does **not** reveal the live cable *[call — Q6]*.
- Desktop icons stay on top of the wallpaper (WorkerW) — a card may be partly covered by icons; accepted.
- The bomb logo may be an **animated GIF** (fuse): the existing event-sprite path already decodes GIF frames and
  runs them on the event-local clock (`DrawEventSprite`).

## 8. Input on the bomb seat (`BombInputWatcher`, WallpaperEngine, main process)

Started by `RemotePlaybackHost` / `ServerPlaybackHost` only when a Bomb event targets one of **its own**
monitors as bomb seat; stopped at the outcome / deadline. Nothing runs on other seats.

**Clicks.** The player window never gets clicks (SysListView32 covers it). Use **Raw Input with
`RIDEV_INPUTSINK`** on a hidden message-only window (Lively's technique, `Lively-reference/.../RawInputMsgWindow.xaml.cs`;
plain P/Invoke, no hook, no new library) *[call]*. On left-button-down:
1. foreground window is the desktop (Progman / WorkerW / its DefView) — otherwise ignore (no cutting from inside a game);
2. `GetCursorPos` (main process is per-monitor DPI aware? → verify in the spike; otherwise convert) →
   monitor-local px → canvas units (inverse of `NodeMapping.ToLocalX`, ÷ `Scale`);
3. hit-test: the 3 terminal "cut zones" (short cable stubs next to the bomb, generous 40-unit radius); hit → `Cut(i)`.
A wire-cutter cursor would be nice but cannot be set on the desktop — skipped.

**Recycle Bin.** Poll every 250 ms during the countdown: `IShellWindows.FindWindowSW(SWC_DESKTOP)` →
`IShellBrowser.QueryActiveShellView` → `IFolderView` → enumerate items, match the Recycle Bin by parsing name
`::{645FF040-5081-101B-9F08-00AA002F954E}` (locale-independent), `GetItemPosition` + icon spacing →
screen rect → canvas. Fires `Recycle` when the icon rect overlaps the bomb rect **and** it was not
overlapping when the countdown started (no free win when the icon happens to sit there). COM path fails / bin
hidden from desktop → easter egg off on that seat, logged.

### 8.1 Busy seats

A monitor is **busy** when a visible, non-minimised, non-cloaked top-level window that is not the desktop
(Progman / WorkerW / DefView), not a tool window and not our own covers ≥ 80 % of it (`EnumWindows` +
`DwmGetWindowAttribute(DWMWA_CLOAKED)` + rect intersection; a fullscreen game is the extreme case). Generalises
the existing foreground-only `FullscreenDetectionService` into a per-monitor `MonitorBusyProbe` (Core).

- Clients sample it every 2 s and send the busy monitors in the heartbeat (new field
  `repeated int32 busy_monitor_indexes = 9;` on `HeartbeatRequest`; old clients send none = all free *[call]*).
  The server machine samples its own monitors directly.
- `BombGameService.StartAsync` filters the target seats with the latest report (stale > 10 s = treat as free).
- Busy is only checked at the start. A seat that becomes busy mid-game stays in the game *[call]*; the bomb seat
  can only cut while its desktop is in front anyway.
- The room view can show a small "busy" badge per node from the same data — nice for the host, cheap; optional.

## 9. Determinism checklist
- Seed drawn once on the server; all geometry generated server-side and shipped.
- Players and preview sample only `(event, NodeLayout, localMs)`; no `DateTime.Now`, no `Random`.
- Text sprites are drawn into server-measured aspect boxes (Announce rule), so slightly different font
  metrics never move anything.
- Outcomes are separate events with their own T0 — late joiners and resynced seats get both via the existing replay.

## 10. Build order
0. **Spike (throwaway, 1–2 h, on Patrick's 24H2 machine):** with the player parented, does raw input
   with INPUTSINK see clicks on the desktop and is the foreground Progman/WorkerW? Does IFolderView return the
   Recycle Bin position, and in which coordinate space (multi-monitor, a monitor left of the primary, 125 % DPI)?
   `.docs/2026.04_IconZoneAnimation_TODO.md:47` reports the desktop right-click menu disappearing while
   WaBiBaBuSy runs — check clicks still reach DefView. **If the spike fails, the click falls back to a hotkey on
   the bomb seat (F1/F2/F3 = red/green/blue)** and the Recycle Bin egg is dropped.
1. Models: `BombGame` types, `BombGameFactory` (seats, roulette, routes, riddles, tangle), `BombMath`
   (timeline, timer digits/7-seg, cable prefix length, shockwave/shake/outcome poses) + tests.
2. Player `Program.Bomb.cs` + preview `ScenePainter.Bomb.cs`; showcase scenario `TestScenarios/showcase/bibabo.json`
   (exact frames: roulette, cables, countdown, each outcome — needs a scenario step `bombOutcome`).
3. Server: `BombGameService`, trigger (hotkey + Announce egg + abort), outcome dispatch, `ReportShowAction` RPC.
4. Input: `BombInputWatcher` (raw input + Recycle Bin poll), busy probe + heartbeat field, client/host wiring.
5. Docs: CLAUDE.md capabilities line, RECENT_UPDATES, manual checklist entry (2 machines: defuse, wrong cable, timeout, recycle, abort, reconnect mid-game).

## 11. Tests (TDD, `WaBiBaBuSy.Tests`)
- Factory: same seed → same game; bomb ≠ end seats; ends distinct with ≥ 4 seats; route endpoints sit on the
  bomb terminal and the card edge; every point's y inside the seat under it; Ring route takes the shorter way.
- Riddles: exactly one live cable; each formula evaluates to its intended value (test-side evaluator);
  tangle: each strand ends at the declared exit, exactly one strand per exit.
- `BombMath`: phase boundaries; timer digits at `IntroMs + TimerMs − 1` = 0:00, holds at 0:00 after; bomb not
  drawn after its outcome's T0; shockwave reaches a seat at distance / speed.
- Service: first action wins; wrong client / wrong game / after grace ignored; timeout fires once; abort.
- Codec: Bomb/BombOutcome round trip; old type names still parse.

## 12. Builder-check calls (settled by Claude, check these)

- **Seat = machine, not monitor** *[call]*: bomb and cable ends go to distinct *machines* (client ids), each placed on
  that machine's first node in traversal order, so a dual-monitor desk never holds bomb + end. "≥ 2 away" counts machines.
  The click / Recycle Bin watcher is per machine anyway.
- **Playlist during a game** *[call]*: the black fill covers every scene, so the game simply survives playlist item
  switches (the existing event replay re-sends it to respawned players). While a game runs, playlist interrupts and
  other show events are suppressed. Stop / a new show → `Aborted`. Afterwards whatever the playlist is on by then comes back.
- **Answer secrecy** *[call]*: "the live flag is not on the wire" is enough — it is a party among friends. Formulas and
  strand exits travel as data (players need them to draw). §1's anti-goal means exactly that, no more.
- **Assets** *[call]*: only the bomb logo is a file asset (the existing `ImagePath` slot). Formulas, "BOOM!" and labels are
  text sprites rendered on the player from event text (as Announce / Identify do), so the one-asset transfer stays;
  the player's `_eventSprites` becomes keyed by `(EventId, slot)`.
- **Logo** *[call]*: embedded resource (placeholder = procedural bomb until Patrick's artwork exists), overridable by
  dropping `bibabo-bomb.gif` or `.png` into `%APPDATA%\WaBiBaBuSy\` (GIF wins) — no setting needed.
- **Busy rule** *[call]*: "covered by a window ≥ 80 %" (§8.1) — a small chat window doesn't make a seat busy, a
  maximised browser does.

## 13. Patrick's answers (2026-10-21)

1. **Busy seats:** skip seats that are busy with something else → §8.1 (busy detection) and §4 (selection).
2. **Wrong cable:** instant BOOM.
3. **Trigger:** Ctrl+Alt+B is fine, plus a visible button → Events strip (§5.1).
4. **Language:** English.
5. **Seats:** all selected seats (the Events-strip target), server and projector included; no empty seats (= busy/absent are skipped).
6. **Operator view** marks the live cable: no.
7. **Sound:** no.
8. **Timer:** 5:00.
9. **Logo:** Patrick tries a GIF → supported (§7).

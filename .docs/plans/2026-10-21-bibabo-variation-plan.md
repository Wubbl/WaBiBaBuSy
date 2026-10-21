# BiBaBo Variation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every BiBaBo game plays differently: seven riddle kinds that each yield a digit, a rule rolled per game (Classic / Cut order / Keypad), an optional RULES card on another seat and an optional junction box that re-colours cables.

**Architecture:** All rolls happen in the seeded server-side `BombGameFactory`; the answers stay in a server-only `BombSolution`. Progress (cut cables, typed digits) travels as a new show-event type `BombStep`; players draw the newest step of a game. Drawing stays pure (`BombMath` + event + layout).

**Tech Stack:** as the BiBaBo plan (.NET 9, Vortice D2D player, Avalonia preview, xUnit).

**Spec:** [`2026-10-21-bibabo-bomb-game-design.md`](2026-10-21-bibabo-bomb-game-design.md) §14 (on top of §1–§13)

## Global Constraints
- Values: Math / Sequence / Tangle / Labyrinth / Dots 0–9, Logic 0–7, Clock 1–9.
- Mode roll: Classic 50 %, Cut order 25 %, Keypad 25 %; junction 40 % (needs ≥ 2 cables the same way); rule card elsewhere 50 % (needs a spare free machine).
- Rule texts (English, exact): `LIVE ⇔ result = {k}`, `LIVE ⇔ result is odd`, `LIVE ⇔ result is even`, `LIVE ⇔ result is a prime (2 3 5 7)`, `LIVE ⇔ the LARGEST result`, `LIVE ⇔ the SMALLEST result`; cut order: `LIVE ⇔ result is even|odd|greater than 4` + `Cut both — smallest result first|largest result first`; keypad: `CODE = {COLOUR} · {COLOUR} · {COLOUR}` + `(colours at the bomb)`; junction: rules mean colours at the bomb.
- Nothing in `BombGame` / `BombRiddle` JSON reveals a value; the solution never leaves the server.
- Players: no per-frame allocation (sprite ids built once per event, `BombSpriteIds`).

## Review Focus
1. **Every rule has exactly the intended number of live cables** for every roll — pinned by a 300-seed factory test per mode.
2. **A riddle's displayed content really yields its value** — independent solvers in the tests (formula evaluator, maze BFS, gate evaluator, mirrored clock, dot count, sequence pattern check, strand exits).
3. **Keypad clicks vs cable clicks** — in Keypad mode cable clicks do nothing; outside it keypad keys do not exist. Pinned in Task 4.
4. **Cut order with one correct cut, then reconnect** — the resumed client shows that cable as cut. Pinned by ShowEventLog test (steps replay while booked).
5. **Junction with a Ring seam** — routes through the box keep the short way. Pinned in Task 2 (`Junction_RoutesThroughTheBox`).

---

### Task 1: Value riddles (7 kinds)
**Files:** Modify `WaBiBaBuSy.Models/Show/BombRiddles.cs`, `BombGame.cs` (`RiddleKind` + riddle fields); Test `WaBiBaBuSy.Tests/BombRiddleTests.cs` (rewrite).
**Produces:**
```csharp
public enum RiddleKind { Math, Tangle, Maze, Logic, Sequence, Clock, Dots }
public static (int Min, int Max) BombRiddles.Range(RiddleKind kind);
public static BombRiddle BombRiddles.Make(RiddleKind kind, int value, Random rng);
public static string BombRiddles.Subtitle(BombRiddle r);   // the card's instruction line
// BombRiddle: Formula, FormulaAspect, Strands, StrandExits, ExitLabels, MazeW, MazeH, MazeWalls (bit 1 right, 2 bottom),
//             MazeEntryRow, ExitRows, LogicInputs[3], LogicGates[3] ("AND A B"), ClockHour, ClockMinute, Dots (x,y 0..1),
//             DotColors, DotPalette, DotTarget, DotTargetName
```
- [ ] Tests (per kind, 200 seeds × every value in range): independent solver → equals the value. Plus: maze has exactly one reachable exit; exit labels distinct; logic value ≤ 7; clock hour 1–9.
- [ ] Run `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~BombRiddle"` — FAIL, implement, PASS. Commit.

### Task 2: Modes, rules, rule card, junction (factory)
**Files:** Modify `BombGame.cs` (`BombMode`, `BombSolution`, `BombJunction`, game fields `Mode`, `RuleLines`, `RuleNodeId`, `Junction`, `CodeOrder`, `CutCables`, `Typed`; cable `CardColorHex`, `SwapIndex`), `BombGameFactory.cs` (returns `(BombGame?, BombSolution?)`; request gains `ForceMode`, `ForceKinds`, `ForceJunction`, `ForceRuleCard`), callers (`ServerPlaybackHost`, `TestRunner`); Test `BombGameFactoryTests.cs`.
- [ ] Tests: per mode × 300 seeds — Classic exactly 1 live, CutOrder exactly 2 live with distinct values and the order matches the rule, Keypad code = values in `CodeOrder`; rule lines match the solution; rule card seat ∉ bomb/end machines; junction: cables through it change colour (`CardColorHex` ≠ `ColorHex` for ≥ 1), routes pass the box, short way on a Ring; JSON has no "Solution"/"Value".
- [ ] FAIL → implement → PASS → full suite. Commit.

### Task 3: Referee + BombStep
**Files:** `ShowEvent.cs` (`BombStep`), `BombGameService.cs` (`Begin(ShowEvent, BombSolution)`, `OnActionAsync(machine, gameId, action, value)` with `cut` / `key` / `recycle`; dispatches BombStep / BombOutcome), `ShowEventLog.cs` (BombStep lifetime like BombOutcome), `ServerPlaybackHost.cs`, `BombInputWatcher.cs` + `RemotePlaybackHost.cs` (report `key`); Tests `BombGameServiceTests.cs`, `ShowEventLogTests.cs`.
- [ ] Tests: CutOrder right-right → Defused with a step in between; right-wrong → WrongCable; Keypad 3 right digits → Defused, wrong → WrongCable, C clears, steps carry `Typed`; Classic unchanged; keys ignored outside Keypad, cuts ignored in Keypad; steps replay while the bomb is booked.
- [ ] FAIL → implement → PASS. Commit.

### Task 4: Keypad geometry + hit test
**Files:** `BombMath.cs` (`KeyRect(BombGame g, int key)` canvas units, keys 0–9, 10 = C), `BombHit.cs` (`KeyAt`), `BombInputWatcher.cs`; Test `BombHitTests.cs`.
- [ ] Tests: key centres hit their key on scaled / mirrored nodes; miss → −1; `KeyAt` is −1 outside Keypad mode; `CableAt` is −1 in Keypad mode.
- [ ] FAIL → implement → PASS. Commit.

### Task 5: Drawing (player + preview)
**Files:** `Program.Bomb.cs` (cards for every kind, digit exit labels, subtitle, keypad + typed display, cut cables, junction box with visible crossing wires + colour change at `SwapIndex`, RULES card / "RULES: somewhere in the room"), `Program.ShowEvents.cs` (BombStep = layer 4, never drawn itself), `ScenePainter.Bomb.cs` (simplified).
- [ ] `dotnet build` 0 errors; showcase run (Task 6) shows every kind.

### Task 6: Showcase + docs
**Files:** `TestScenarios/showcase/bibabo.json` (games forcing each mode and each riddle kind), `TestRunner` / `TestScenario` (`bombMode`, `bombKinds`, `bombJunction`, `bombRuleCard` on the Bomb step), docs (`claude.md`, `RECENT_UPDATES.md`, checklist §8c).
- [ ] Run the showcase; frames show all seven kinds, keypad, junction, rule card. Full suite + build. Commit. Whole-branch review, fix pass.

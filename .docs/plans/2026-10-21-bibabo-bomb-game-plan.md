# BiBaBo Bomb Game Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A room-wide bomb-defuse mini-game: black screens, a bomb lands on a random free seat with a 5:00 timer, three cables (red/green/blue) run across the seats to three riddle cards; the bomb seat's user clicks the right cable (or drags the Recycle Bin onto the bomb) before the timer ends.

**Architecture:** Two new show-event types (`Bomb`, `BombOutcome`) carry a `BombGame` object built once on the server by a pure seeded factory (`WaBiBaBuSy.Models/Show/Bomb*`); everything is drawn by the players and the room preview with pure math (`BombMath`). The live cable stays on the server (`BombGameService`, Core). The bomb seat's machine watches clicks (Raw Input sink) and the Recycle Bin icon (`IFolderView`) and reports through a new unary RPC `ReportShowAction`; the server resolves the game (first action wins) and fires the outcome event. Busy seats (a window covers ≥ 80 % of the monitor) are reported in the heartbeat and skipped.

**Tech Stack:** .NET 9, gRPC proto, Vortice Direct2D (player), GDI+ `TextSprite`, Win32 Raw Input + Shell COM (`IShellWindows`/`IFolderView`), Avalonia 11, xUnit.

**Spec:** [`2026-10-21-bibabo-bomb-game-design.md`](2026-10-21-bibabo-bomb-game-design.md) (approved, answers in §13)

## Global Constraints

- New proto numbers only: `rpc ReportShowAction(ShowActionReport) returns (ShowActionAck)`; `HeartbeatRequest.busy_monitor_indexes = 9` (repeated int32); `ConnectedClient.busy_monitor_indexes = 15`, `ConnectedClient.busy_report_utc = 16`.
- `ShowEventType` gains `Bomb`, `BombOutcome` (string-serialized; old clients ignore).
- Players and preview draw only from `(ShowEvent, NodeLayout, localMs)` — no `Random`, no `DateTime.Now` in drawing. All randomness in the server-side factory from `ShowEvent.Seed`.
- The live cable index never goes into `ShowEvent` / `BombGame` JSON.
- On-screen text English: `LIVE ⇔ result = 1`, `LIVE ⇔ leads to the DETONATOR`, `DETONATOR`, `GROUND`, `BOOM!`, `DEFUSED — 2:31 left`, `RECYCLED ♻`.
- Cable colours `#E53935` (red), `#43A047` (green), `#1E88E5` (blue). Timer default 300 000 ms, intro 8 000 ms, outcome 8 000 ms.
- Seats = machines: node ids `{client}` / `{client}_MONITOR_n` / `SERVER_LOCALHOST_MONITOR_n` / `LOCAL_MACHINE_MONITOR_n` group by machine (`BombSeats.MachineOf`).
- Busy = a visible, non-minimised, non-cloaked top-level window (not desktop/Progman/WorkerW, not a tool window, not our process) covers ≥ 80 % of the monitor.
- Input: observe only (Raw Input `RIDEV_INPUTSINK`), never block; only while a Bomb game targets one of this machine's monitors; click counts only when the foreground window is Progman / WorkerW / SHELLDLL_DefView's tree.
- Invariants: no `WS_EX_TRANSPARENT`, no DXGI in the main process.

## Review Focus

1. **Defuse click in the last second** — a cut whose report arrives up to 300 ms after the deadline still wins; one arriving later is ignored and Timeout fires. Pinned in Task 3 (`Service_CutWithinGrace_Wins`, `Service_CutAfterGrace_Ignored`).
2. **Two actions at once** (cut + Recycle Bin, or two clicks) — exactly one outcome event. Pinned in Task 3 (`Service_FirstActionWins`).
3. **Recycle Bin already lying where the bomb lands** — no free win: only a move onto the bomb counts. Pinned in Task 5 (`RecycleWatch_IgnoresStartingOverlap`).
4. **Too few free seats (1 machine, or all busy)** — refused with a status line, nothing drawn. Pinned in Task 1 (`Factory_RefusesFewerThanTwoMachines`) + Task 3.
5. **Ring wrap: bomb near the seam, end seat on the other side** — cable takes the short way across the seam and is drawn on both sides. Pinned in Task 1 (`Route_RingTakesShortWayAcrossSeam`) + `BombMath_SegmentsOnNode_WrapCopies` in Task 2.

---

### Task 1: Model, seats, factory (routes, riddles, tangle)

**Files:**
- Modify: `WaBiBaBuSy.Models/Show/ShowEvent.cs` (enum + `public BombGame? Bomb { get; set; }`)
- Create: `WaBiBaBuSy.Models/Show/BombGame.cs`, `BombSeats.cs`, `BombRiddles.cs`, `BombGameFactory.cs`
- Test: `WaBiBaBuSy.Tests/BombGameFactoryTests.cs`, `WaBiBaBuSy.Tests/BombRiddleTests.cs`

**Interfaces — Produces:**
```csharp
public enum BombResult { None, Defused, Recycled, WrongCable, Timeout, Aborted }
public enum RiddleKind { Math, Tangle }
public sealed class BombRiddle { RiddleKind Kind; string? Formula; float FormulaAspect; float[][]? Strands; int[]? StrandExits; }
public sealed class BombCable { string ColorHex; string EndNodeId; int EndOrder; float[] Points; float CardX, CardY, CardW, CardH; BombRiddle Riddle; }
public sealed class BombGame
{
    string GameId; string BombNodeId; int BombOrder; float BombX, BombY, BombSize;
    int[] RouletteOrders; int IntroMs = 8000; int TimerMs = 300_000; List<BombCable> Cables;
    BombResult Result; int CutCable = -1; int RemainingMs;   // BombOutcome only
}
public static class BombSeats
{
    public static string MachineOf(string nodeId);          // strips "_MONITOR_n"; local prefixes → "this-machine"
    // one node per free machine (its lowest Order), in traversal order
    public static IReadOnlyList<NodeLayout> Candidates(SeatMapLayoutResult layout, IReadOnlyCollection<string>? targetNodeIds, Func<string, bool> isBusy);
}
public static class BombRiddles
{
    public const string MathRule = "LIVE ⇔ result = 1";
    public const string TangleRule = "LIVE ⇔ leads to the DETONATOR";
    public static BombRiddle Math(Random rng, bool live);      // formula with Unicode superscripts
    public static BombRiddle Tangle(Random rng, bool live);    // 3 strands, strand 0 = this cable; exit 0 = DETONATOR
}
public sealed record BombGameRequest(SeatMapLayoutResult Layout, IReadOnlyCollection<string>? TargetNodeIds, Func<string, bool> IsBusy,
    int Seed, string GameId, int TimerMs = 300_000);
public static class BombGameFactory
{
    // the game, the live cable index (server only), or null + error ("Not enough free seats — 3 busy.")
    public static (BombGame? Game, int LiveCable) Create(BombGameRequest r, out string? error);
    public static float[] Route(SeatMapLayoutResult layout, float fromX, float fromY, float toX, float toY, Random rng); // x,y pairs
}
```
Rules (spec §4): fewer than 2 candidate machines → error. Bomb = uniform over candidates. Ends = 3 distinct machines ≠ bomb, preferring traversal distance ≥ 2 machines; with < 4 machines ends repeat round-robin over the others. `RouletteOrders` = 8–12 random candidate orders, last = bomb. Bomb centred on its node, `BombSize = 0.35 × node height`. Card = 55 % of end node height, aspect 1.4, centred on the end node; when k cables end on the same node (few machines) the cards sit side by side, each in 1/k of the node width (shrunk to fit). Route: x from bomb terminal (bomb right edge, cable i offset vertically by `(i−1) × 0.12 × BombSize`) to the card's near edge; on a wrapping layout the shorter way (x may leave [0, CanvasWidth) — unwrapped); point every 30 canvas units; y = start→end linear blend + `Σ_{k=1..3} a_k sin(2π f_k t + φ_k)` (amplitude ≤ 0.25 × node height, from `rng`), clamped into `[node.OffsetY + 0.05h, node.OffsetY + 0.95h]` of the node under that x (use the bomb node's band in gaps). Math riddle `FormulaAspect` stays 0 here — the server measures it with `TextSprite` (Task 3).

Math templates (live = 1, dead ≠ 1; numbers from `rng`, spec §4.1 table): power-of-zero, zero-factorial, hidden-zero-factor, sign-power, order-of-operations, difference-of-squares, reciprocal. Superscripts: `⁰¹²³⁴⁵⁶⁷⁸⁹⁽⁾⁺⁻`; operators `·`, `÷`, `−`, `!`.

- [ ] **Step 1: Write the failing tests** (the test project gets a tiny evaluator `TestFormula.Evaluate(string)` in `BombRiddleTests.cs` that understands digits, `+ − · ÷ ( ) !` and superscript exponent groups)

```csharp
// BombRiddleTests
[Theory][MemberData(nameof(Seeds))] public void Math_LiveEvaluatesToOne(int seed)   // 200 seeds × live → |value − 1| < 1e-9
[Theory][MemberData(nameof(Seeds))] public void Math_DeadNeverOne(int seed)         // 200 seeds × dead → |value − 1| > 1e-6
[Fact] public void Math_UsesEveryTemplate()                                          // 500 seeds → ≥ 7 distinct shapes (first operator pattern)
[Theory][MemberData(nameof(Seeds))] public void Tangle_StrandZeroEndsAtDetonatorIffLive(int seed)
[Fact] public void Tangle_EachExitUsedOnce()
// BombGameFactoryTests (helper Layout(n machines, wraps) builds a single-row SeatMapLayoutResult of 1920×1080 nodes)
[Fact] public void Factory_SameSeed_SameGame()                                       // JSON equal
[Fact] public void Factory_BombAndEndsOnDistinctMachines()                           // 8 machines, 2 monitors each
[Fact] public void Factory_RefusesFewerThanTwoMachines()                             // 1 machine → null, error contains "free seats"
[Fact] public void Factory_SkipsBusyAndUntargetedSeats()
[Fact] public void Factory_ExactlyOneLiveCable_NotInJson()                           // LiveCable in 0..2; serialized game has no "Live"
[Fact] public void Route_StartsAtTerminal_EndsAtCard()
[Fact] public void Route_YStaysInsideTheNodeUnderEachPoint()
[Fact] public void Route_RingTakesShortWayAcrossSeam()                                // bomb on node 0, end on last node of 10, wraps → min x < 0
[Fact] public void Roulette_EndsOnBomb()
[Fact] public void MachineOf_GroupsMonitors()                                        // "c1_MONITOR_2" → "c1"; "SERVER_LOCALHOST_MONITOR_1" → "this-machine"
```

- [ ] **Step 2: Run** `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~Bomb"` — Expected: build FAIL.
- [ ] **Step 3: Implement** the four files.
- [ ] **Step 4: Run** the filter — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(bibabo): game model, seats, seeded factory with routes and riddles`

---

### Task 2: `BombMath` + player drawing + room preview

**Files:**
- Create: `WaBiBaBuSy.Models/Show/BombMath.cs`, `WaBiBaBuSy.Player.D2D/Program.Bomb.cs`, `WaBiBaBuSy.UI/Controls/ScenePainter.Bomb.cs`
- Modify: `WaBiBaBuSy.Player.D2D/Program.ShowEvents.cs` (dispatch `Bomb` / `BombOutcome` in `DrawShowEvents`, layer 4 = above everything; text sprites for formulas/rules/labels via the existing `StartEventSpriteDecode` path keyed `(EventId, slot)`; bomb logo = the event's `ImagePath` sprite, animated GIFs on the event clock), `WaBiBaBuSy.UI/Controls/ScenePainter.Events.cs` (same dispatch), `WaBiBaBuSy.Player.D2D/Program.Agenda.cs` (`_agendaHiddenByEvent` also while a Bomb/BombOutcome is active on this node)
- Test: `WaBiBaBuSy.Tests/BombMathTests.cs`

**Interfaces — Produces:**
```csharp
public enum BombPhase { Blackout, Roulette, Drop, Cables, Countdown, Hold, Ended }
public static class BombMath
{
    public const int BlackoutMs = 1000, RouletteEndMs = 4000, DropEndMs = 5000;   // cables grow until IntroMs
    public static BombPhase Phase(BombGame g, long localMs);
    public static float BlackOpacity(long localMs);                                // 0→1 over BlackoutMs, then 1
    public static int RouletteOrder(BombGame g, long localMs);                     // hop sequence, decelerating; -1 outside
    public static float CableProgress(BombGame g, long localMs);                    // 0..1 over DropEndMs..IntroMs
    public static int RemainingMs(BombGame g, long localMs);                        // TimerMs − (local − IntroMs), clamped ≥ 0; full before IntroMs
    public static bool[] SevenSegment(int digit);                                   // a..g
    public static bool TimerBlink(BombGame g, long localMs);                         // last 30 s / Hold: 2 Hz
    public static int SegmentsOnNode(float[] points, int pointCount, NodeLayout node, Span<(int I, float Shift)> output); // segments visible on this node (+ wrap shift)
    public static int PrefixPointCount(float[] points, float progress);              // points drawn while growing
    // outcome (local = outcome-event local ms)
    public static float ShockwaveRadius(long outcomeLocalMs, float canvasUnitsPerMs); // ring from the bomb centre
    public static (float X, float Y) Shake(int seed, long outcomeLocalMs);            // ±6 units, decays over 1.5 s
    public static bool BombVisible(BombGame g, long bombLocalMs, ShowEvent? outcome, long outcomeLocalMs);
}
```
Drawing (spec §6–§7): black fill at `BlackOpacity`; roulette: white rect border 12 units on the seat with `node.Order == RouletteOrder`; bomb (logo sprite or procedural: black circle, fuse line, spark flicker) on the bomb node with a slight drop bounce; three terminals (colored circles r = 0.06 × BombSize) at the bomb's right edge; cables = `DrawLine` per visible segment, width 10 units, round caps, `CableProgress` prefix; 7-segment timer right of the bomb (segment rects, red `#FF1744`, blink in last 30 s / Hold); rule line under the timer (`TextSprite`); small `m:ss` timer bottom-right of every seat (7-segment, 4 % of node height); cards on end nodes once the cable arrived: dark card, colour stripe, formula sprite (Math) or strands + `DETONATOR` / `GROUND` labels (Tangle). Outcome: per spec §6 (BOOM: shake + white flash on bomb seat + shockwave ring + fire-palette `CelebrationMath` burst + `BOOM!`; Defused/Recycled: grey cables, frozen timer, green text + confetti; Aborted: fade). Preview: same calls with Avalonia primitives.

- [ ] **Step 1: Write the failing tests** (`BombMathTests`)

```csharp
[Fact] public void Phase_Boundaries()                    // 0 Blackout, 1000 Roulette, 4000 Drop, 5000 Cables, 8000 Countdown, 308000 Hold
[Fact] public void Remaining_CountsDown_HoldsAtZero()   // local 8000 → 300000; 307999 → 1; 400000 → 0
[Fact] public void Roulette_LandsOnBombBeforeDrop()      // RouletteOrder(g, 3999) == g.BombOrder
[Fact] public void SevenSegment_Digits()                 // 8 → all true; 1 → b,c only
[Fact] public void CableProgress_GrowsDuringCablesPhase()
[Fact] public void SegmentsOnNode_OnlyVisibleSegments()
[Fact] public void SegmentsOnNode_WrapCopies()           // ring: a segment at x < 0 appears on the last node with shift +CanvasWidth
[Fact] public void BombHidden_AfterOutcomeT0()
[Fact] public void Shake_DecaysToZero()
```
- [ ] **Step 2: Run** `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~BombMath"` — Expected: build FAIL.
- [ ] **Step 3: Implement** `BombMath`, then the player and preview drawing.
- [ ] **Step 4: Run** the filter, then `dotnet build` — Expected: PASS, 0 errors.
- [ ] **Step 5: Commit** — `feat(bibabo): bomb math, player and preview drawing`

---

### Task 3: Server game service, transport, trigger

**Files:**
- Modify: `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` (`ReportShowAction` RPC + messages; heartbeat/ConnectedClient busy fields — busy wiring itself is Task 4)
- Modify: `WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs` (`ReportShowAction` override → `event EventHandler<ShowActionReport>? ShowActionReported`, ack `accepted = true` when someone handled it)
- Create: `WaBiBaBuSy.Core/Services/Show/BombGameService.cs`
- Modify: `WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs` (expose `Task<bool> FirePreparedEventAsync(Func<SeatMapLayoutResult, ShowEvent?> build, IReadOnlyList<string>? targetNodeIds)` = the stand-in + dispatch half of `FireEventAsync`; owns a `BombGameService`; suppresses other events and playlist interrupts while a game runs; aborts the game in `StopAsync` / `ClearAllAsync`)
- Modify: `WaBiBaBuSy.Models/Show/ShowEventHotkeys.cs` (`BiBaBoSlot = 66`, gesture "Ctrl+Alt+B"), `WaBiBaBuSy.UI/Services/GlobalHotkeyService.cs` (register `'B'` too), `WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs` (slot 66 → `ToggleBiBaBoAsync`)
- Modify: `WaBiBaBuSy.UI/ViewModels/ShowEventsViewModel.cs` + `WaBiBaBuSy.UI/Views/MainWindow.axaml` (button "💣 BiBaBo" / "■ Abort BiBaBo")
- Test: `WaBiBaBuSy.Tests/BombGameServiceTests.cs`

**Interfaces:**
- Consumes: `BombGameFactory.Create`, `BombGame`, `TextSprite.MeasureAspect` (fills `FormulaAspect`).
- Produces:
```csharp
public sealed class BombGameService   // Core; clock + dispatch injected, testable
{
    public BombGameService(Func<long> nowUtcMs, Func<ShowEvent, Task> dispatch);
    public bool IsRunning { get; }
    public BombGame? Current { get; }
    public string? BombMachine { get; }                                  // BombSeats.MachineOf(Current.BombNodeId)
    public void Begin(ShowEvent bombEvent, int liveCable);              // starts the deadline timer
    public Task<bool> OnActionAsync(string machine, string gameId, string action, int cable); // "cut" | "recycle"
    public Task AbortAsync();
    public Task TickAsync();                                             // deadline check (timer calls it; tests call it)
}
// ServerPlaybackHost
public Task<bool> ToggleBiBaBoAsync(IReadOnlyList<string>? targetNodeIds);   // start, or abort a running game
public bool IsBiBaBoRunning { get; }                                          // [ObservableProperty]
```
Outcome event: `Type = BombOutcome`, `EventId = new`, `T0 = now + lead`, `DurationMs = 8000`, `Bomb = clone of the game with Result/CutCable/RemainingMs`, same `TargetNodeIds`. Grace = 300 ms after `T0 + IntroMs + TimerMs`. Machine check: the action's machine must equal `BombMachine` (`client_id` for remotes, `"this-machine"` for the server's own seats).

- [ ] **Step 1: Write the failing tests** (`BombGameServiceTests`, fake clock + list of dispatched events)

```csharp
[Fact] public async Task Service_CutLive_Defuses()            // Result Defused, CutCable, RemainingMs > 0
[Fact] public async Task Service_CutDead_Booms()              // WrongCable
[Fact] public async Task Service_Recycle_Recycles()
[Fact] public async Task Service_FirstActionWins()            // second action → false, one outcome dispatched
[Fact] public async Task Service_WrongMachineOrGame_Ignored()
[Fact] public async Task Service_CutWithinGrace_Wins()        // now = deadline + 200
[Fact] public async Task Service_CutAfterGrace_Ignored()      // now = deadline + 400 → false; TickAsync → Timeout
[Fact] public async Task Service_TimeoutFiresOnce()
[Fact] public async Task Service_Abort_DispatchesAborted()
```
- [ ] **Step 2: Run** `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~BombGameService"` — Expected: build FAIL.
- [ ] **Step 3: Implement** service, RPC, host wiring, trigger + button.
- [ ] **Step 4: Run** the filter, full suite, `dotnet build` — Expected: PASS, 0 errors.
- [ ] **Step 5: Commit** — `feat(bibabo): game service, ReportShowAction RPC, button and Ctrl+Alt+B`

---

### Task 4: Busy seats

**Files:**
- Create: `WaBiBaBuSy.Models/Show/BusyRule.cs` (pure: `bool IsBusy(Rect monitor, IEnumerable<Rect> windows)` with ≥ 80 % union coverage on a 32-px grid), `WaBiBaBuSy.WallpaperEngine/Native/MonitorBusyProbe.cs` (`IReadOnlyList<int> BusyMonitors()` via `EnumWindows` + `IsWindowVisible` + `IsIconic` + `DWMWA_CLOAKED` + `GetWindowRect`, skipping Progman/WorkerW/own process/`WS_EX_TOOLWINDOW`)
- Modify: proto `HeartbeatRequest`/`ConnectedClient` (Task 3 added the numbers), `WallpaperSyncClient` (`BusyMonitorsProvider` like `PrefetchStatusProvider`), `WallpaperSyncService.Heartbeat` (store + `busy_report_utc`), `ServerPlaybackHost` (`IsBusy(nodeId)`: local → probe now; remote → client report < 10 s old, monitor index from the node id, whole-machine node = any monitor busy), client wiring in `TrayViewModel`/`RemotePlaybackHost` (provider set once)
- Test: `WaBiBaBuSy.Tests/BusyRuleTests.cs`

- [ ] **Step 1: Write the failing tests**
```csharp
[Fact] public void Maximised_IsBusy()           // window = monitor → true
[Fact] public void SmallWindow_IsFree()        // 40 % → false
[Fact] public void TwoHalves_AreBusy()          // two windows each 50 %, side by side → true
[Fact] public void OverlapCountsOnce()          // two 60 % windows overlapping the same area → false
[Fact] public void OffMonitorPartIgnored()
```
- [ ] **Step 2–4:** run `--filter "FullyQualifiedName~BusyRule"` (FAIL → implement → PASS), full suite, build.
- [ ] **Step 5: Commit** — `feat(bibabo): busy seats from a window-coverage probe, reported in the heartbeat`

---

### Task 5: Input on the bomb seat (clicks + Recycle Bin)

**Files:**
- Create: `WaBiBaBuSy.Models/Show/BombHit.cs` (pure: `int CableAt(BombGame g, NodeLayout node, float localPxX, float localPxY)` → 0..2 or −1, terminals + first 15 % of each cable, radius 40 canvas units; `bool CoversBomb(BombGame g, NodeLayout node, (float X, float Y, float W, float H) localPxRect)`), `RecycleWatch` (pure state: `Start(bool overlapping)`, `bool Update(bool overlapping)` → true once when the icon moves onto the bomb after not overlapping)
- Create: `WaBiBaBuSy.WallpaperEngine/Native/RawMouseSink.cs` (hidden message-only window on its own thread, `RegisterRawInputDevices` mouse `RIDEV_INPUTSINK`, raises `LeftButtonDown(int screenX, int screenY)` when foreground is the desktop), `WaBiBaBuSy.WallpaperEngine/Native/RecycleBinLocator.cs` (`Rectangle? Find()` — `IShellWindows.FindWindowSW(SWC_DESKTOP)` → `IShellBrowser.QueryActiveShellView` → `IFolderView.Items` + `GetItemPosition`, matches parsing name `::{645FF040-5081-101B-9F08-00AA002F954E}`; icon size from `SPI_ICONHSPACING/VSPACING`)
- Create: `WaBiBaBuSy.UI/Services/BombInputWatcher.cs` (per host: started when a `Bomb` event targets this machine's bomb node, stopped at its outcome / deadline; maps screen px → node local px via the monitor bounds; `Func<string action, int cable, Task> report`)
- Modify: `RemotePlaybackHost` (keeps `NodeLayout` per monitor from each apply; on Bomb events starts the watcher; report → `WallpaperSyncClient.ReportShowActionAsync`), `ServerPlaybackHost` (local bomb seat → watcher → `BombGameService.OnActionAsync("this-machine", …)`), `WallpaperSyncClient` (`Task<bool> ReportShowActionAsync(string eventId, string action, int cable)`)
- Test: `WaBiBaBuSy.Tests/BombHitTests.cs`

- [ ] **Step 1: Write the failing tests**
```csharp
[Fact] public void CableAt_TerminalHit()              // click on terminal 1 centre → 1
[Fact] public void CableAt_Miss()                      // far away → −1
[Fact] public void CableAt_ScaledAndMirroredNode()     // Scale 1.5, Mirrored → still 1
[Fact] public void CoversBomb_IconOnBomb()
[Fact] public void RecycleWatch_IgnoresStartingOverlap()   // Start(true); Update(true) → false
[Fact] public void RecycleWatch_FiresOnceOnMoveOnto()      // Start(false); Update(true) → true; Update(true) → false
```
- [ ] **Step 2–4:** run `--filter "FullyQualifiedName~BombHit"` (FAIL → implement → PASS), full suite, build.
- [ ] **Step 5: Spike check on this machine (non-interactive part):** a `--bibabo-probe` CLI switch in `WaBiBaBuSy.UI` (`App.axaml.cs` arg handling, like `--test-run`) prints the Recycle Bin rect from `RecycleBinLocator.Find()` and the busy monitors, then exits. Run: `dotnet run --project WaBiBaBuSy.UI -- --bibabo-probe` — Expected: a rect (or "not on desktop") and a monitor list. The click path needs a person: listed in the manual checklist.
- [ ] **Step 6: Commit** — `feat(bibabo): bomb-seat input — raw-input clicks and the Recycle Bin`

---

### Task 6: Showcase scenario + docs

**Files:**
- Modify: test runner step `showEvent` to accept `"event": "Bomb"` (fires a seeded game with a fixed seed) and a new `"bombOutcome": "Defused|WrongCable|…"` field (`WaBiBaBuSy.Core/Services/Testing/*` — follow the existing `showEvent` step)
- Create: `TestScenarios/showcase/bibabo.json` (exact frames at roulette 2 s, cables 6 s, countdown 60 s, outcome BOOM +1 s)
- Modify: `claude.md` (capabilities), `.docs/RECENT_UPDATES.md`, `.docs/plans/2026-10-04-manual-test-checklist.md` (§ BiBaBo: 2 machines — defuse, wrong cable, timeout, recycle, abort, busy seat skipped, reconnect mid-game, click while a game is in front does nothing)
- [ ] **Step 1:** run the scenario single-machine: `dotnet run --project WaBiBaBuSy.UI -- --test-run TestScenarios/showcase/bibabo.json --exit` — Expected: exit 0, frames show black + bomb + cables + timer + BOOM.
- [ ] **Step 2:** full suite + build. **Step 3: Commit** — `docs(bibabo): showcase scenario, capabilities, checklist`

# Agenda Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A LAN-day timetable (Fri–Sun, notes) shown as a corner panel on every connected seat — or full-size on seats tagged `agenda` — on top of whatever plays, kept until switched off.

**Architecture:** Pure model + math in `WaBiBaBuSy.Models/Show` (rows, "now/next", layout rects, text parser). Its own room-wide transport (`CommandType.AGENDA`, not a show event): the server sends the whole agenda to every client on change and on register; each host (`RemotePlaybackHost`, `ServerPlaybackHost`) keeps it, forwards it to every player (`cmd_agenda`) and runs a local stand-in player (own desktop wallpaper) on monitors with no scene. Players draw it after the show events with `TextSprite` bitmaps; the room preview paints the same rows.

**Tech Stack:** .NET 9, gRPC proto, Vortice Direct2D (player), GDI+ `TextSprite`, Avalonia 11 + CommunityToolkit.Mvvm, xUnit.

**Spec:** [`2026-10-21-agenda-overlay-design.md`](2026-10-21-agenda-overlay-design.md)

## Global Constraints

- New proto numbers only: `CommandType.AGENDA = 14`, `SyncCommand.agenda_json = 7`. Old clients log "unknown" and ignore.
- The agenda is not a show event: never put it into `ShowEventLog`, never clear it on Stop / Clear / Play.
- Times travel as UTC ms on the server clock and are shifted on receipt by `command.TimestampUtc − agenda.SentUtcMs` (the client's clock conversion already applied to `TimestampUtc`).
- Player: no per-frame heap allocations in `DrawAgenda`; text sprites rendered off the render thread; D2D context only on the render thread.
- All on-screen text English. Day labels `Fri`/`Sat`/…, times `HH:mm` (24 h), in the server's local time (`Agenda.UtcOffsetMinutes`).
- Corner panel: max 6 rows, ≤ 30 % of screen height, card 85 % opaque `#141414`, white text, highlight = `#FFD700`.
- Full-size agenda screen: any node tagged `agenda` (case-insensitive, `NodeTagStore`).
- Never `WS_EX_TRANSPARENT`, no DXGI in the main process (CLAUDE.md invariants). Stand-ins are ordinary `D2DCompositionService` players.

## Review Focus

1. **Empty agenda / everything in the past / nothing yet started** — panel still sensible (title + notes; "no items"; last item kept as past). Pinned in Task 1 tests.
2. **Item list crossing midnight without day prefixes** (`23:00 Movie` then `01:00 Sleep`) — `01:00` lands on the next day, not 22 h earlier. Pinned in Task 1 parser tests.
3. **Client clock off by minutes** — "now/next" must still flip at the server's time. Pinned in Task 2 codec test (shift).
4. **Seat with no scene, then a scene starts, then Stop** — panel visible the whole time apart from the start-lead blink; no orphaned stand-in player. Pinned by `AgendaStandIns.Plan` tests in Task 4 + manual check in Task 7.
5. **Agenda switched off while players run** — every panel disappears and every stand-in stops. Pinned in Task 4 (`Plan` with disabled agenda → stop all) + manual check.

---

### Task 1: Model, math, parser, store

**Files:**
- Create: `WaBiBaBuSy.Models/Show/Agenda.cs`, `WaBiBaBuSy.Models/Show/AgendaMath.cs`, `WaBiBaBuSy.Models/Show/AgendaText.cs`, `WaBiBaBuSy.Models/Show/AgendaLayout.cs`, `WaBiBaBuSy.Models/Configuration/AgendaStore.cs`
- Test: `WaBiBaBuSy.Tests/AgendaMathTests.cs`, `WaBiBaBuSy.Tests/AgendaTextTests.cs`, `WaBiBaBuSy.Tests/AgendaLayoutTests.cs`

**Interfaces — Produces:**
```csharp
public enum AgendaCorner { TopLeft, TopRight, BottomLeft, BottomRight }
public sealed class AgendaItem { public long StartUtcMs { get; set; } public string Text { get; set; } = ""; }
public sealed class Agenda
{
    public bool Enabled { get; set; }
    public string Title { get; set; } = "";
    public List<AgendaItem> Items { get; set; } = new();     // sorted by StartUtcMs
    public List<string> Notes { get; set; } = new();
    public AgendaCorner Corner { get; set; } = AgendaCorner.TopRight;
    public int UtcOffsetMinutes { get; set; }                 // server local offset at build time
    public List<string> ScreenNodeIds { get; set; } = new();  // nodes tagged "agenda": full-size layout
    public long SentUtcMs { get; set; }                       // set by the codec
    public bool IsScreen(IEnumerable<string> nodeIdCandidates); // any candidate in ScreenNodeIds (ordinal)
}
public enum AgendaRowState { Past, Current, Next, Upcoming }
public sealed record AgendaRow(int ItemIndex, string Day, string Time, string Text, AgendaRowState State, string? Countdown, bool Pulse, bool DayHeader);
public static class AgendaMath
{
    public const int MaxPanelRows = 6;
    public const long PulseBeforeMs = 10 * 60_000;
    public static IReadOnlyList<AgendaRow> Rows(Agenda a, long nowUtcMs, int maxRows = MaxPanelRows); // panel window
    public static IReadOnlyList<(string Day, IReadOnlyList<AgendaRow> Rows)> Days(Agenda a, long nowUtcMs); // full screen
    public static string? Countdown(long nowUtcMs, long startUtcMs);   // "in 23 min" (<60 min, ceil), "in 2:05 h", null if started
    public static void ShiftTimes(Agenda a, long deltaMs);
}
public static class AgendaText
{
    public static List<AgendaItem> Parse(string text, DateOnly firstDay, TimeZoneInfo tz);
}
public sealed record AgendaRect(float X, float Y, float W, float H);
public sealed record AgendaPanelLayout(AgendaRect Card, AgendaRect Title, IReadOnlyList<AgendaRect> Rows, IReadOnlyList<AgendaRect> Notes);
public static class AgendaLayout
{
    public static AgendaPanelLayout Panel(float screenW, float screenH, AgendaCorner corner, bool hasTitle, int rows, int noteLines);
    public static AgendaPanelLayout Screen(float screenW, float screenH, int dayColumns, int maxRowsPerDay, int noteLines);  // Rows = column-major
}
public sealed class AgendaDocument   // editor state, persisted
{
    public bool Enabled { get; set; }
    public string Title { get; set; } = "";
    public string ItemsText { get; set; } = "";   // "Fri 18:00 Pizza" lines
    public string NotesText { get; set; } = "";
    public DateOnly FirstDay { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public AgendaCorner Corner { get; set; } = AgendaCorner.TopRight;
    public Agenda Build(TimeZoneInfo tz, IReadOnlyList<string> screenNodeIds);
}
public static class AgendaStore { public static string DefaultPath { get; } /* AppDataPaths.Root\agenda.json */ public static AgendaDocument Load(string? path = null); public static void Save(AgendaDocument doc, string? path = null); }
```

Rules (from the spec, exact):
- `Rows`: Current = last item with `Start ≤ now`; Next = first with `Start > now`. Window starts at Current − 1 (the last past item, if any; Current itself if it is the first) and takes up to `maxRows` items in order. With no Current yet: window starts at item 0. `Countdown` only on the Next row; `Pulse` = Next row and `Start − now ≤ PulseBeforeMs`. `DayHeader` = first row of the window, or the day differs from the previous row's day. Day/Time labels from `StartUtcMs + UtcOffsetMinutes`.
- `Parse`: one item per non-blank line `[Day] H:mm|HH:mm Text`. Day tokens case-insensitive: `Mon Tue Wed Thu Fri Sat Sun`, German `Mo Di Mi Do Fr Sa So`, also full English names. A day token resolves to the first date ≥ `firstDay` with that weekday. A line without a day token takes the previous item's day; if its time is earlier than the previous item's time, the next day. First line without a day = `firstDay`. Lines without a valid time are skipped. Result sorted by start. Local → UTC with `tz`.
- `AgendaLayout.Panel`: card ≤ 30 % of `screenH`, width = 26 % of `screenW` (min 360 px), margin 2 % of `screenH` from the chosen corner; title row 1.3 × row height; equal row heights; notes rows 0.8 × row height.
- `AgendaDocument.Build`: `Notes` = non-blank lines of `NotesText`; `UtcOffsetMinutes` = `tz.GetUtcOffset(now)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// AgendaMathTests — times built with offset 0 so labels equal UTC
[Fact] public void Rows_MarksCurrentAndNext_KeepsLastPast()      // items 10:00,12:00,14:00,16:00; now 12:30 → 10:00 Past, 12:00 Current, 14:00 Next (Countdown "in 1:30 h"), 16:00 Upcoming
[Fact] public void Rows_BeforeFirstItem_StartsAtFirst_NoCurrent() // now 09:00 → first row State Next, Countdown "in 1:00 h"; now 09:30 → "in 30 min"
[Fact] public void Rows_AfterLastItem_LastIsCurrent()             // now 23:00 → last two rows: Past, Current; no Countdown anywhere
[Fact] public void Rows_EmptyAgenda_ReturnsEmpty()
[Fact] public void Rows_CapsAtSix()                               // 10 items → Count == 6
[Fact] public void Rows_PulseOnlyWithinTenMinutes()               // next at +9 min → Pulse; +11 min → no Pulse
[Fact] public void Rows_DayHeaderOnDayChange()                    // Fri 22:00, Sat 10:00 → second row DayHeader, Day == "Sat"
[Fact] public void Countdown_RoundsUpMinutes()                    // 22 min 10 s → "in 23 min"; 2 h 5 min → "in 2:05 h"; started → null
[Fact] public void Days_GroupsByLocalDay()                        // offset +120: 23:30 UTC Fri = 01:30 Sat → under "Sat"
[Fact] public void ShiftTimes_MovesEveryItem()
[Fact] public void IsScreen_MatchesAnyCandidate()
// AgendaTextTests (tz = UTC, firstDay = Fri 2026-10-23)
[Fact] public void Parse_DayPrefixes_ResolveFromFirstDay()        // "Sat 18:00 Pizza" → 2026-10-24 18:00
[Fact] public void Parse_NoDay_FollowsPreviousAndRollsPastMidnight() // "Fri 23:00 Movie\n01:00 Sleep" → Sleep on 2026-10-24 01:00
[Fact] public void Parse_GermanDays_AndSingleDigitHours()         // "Sa 9:30 Frühstück" → 2026-10-24 09:30
[Fact] public void Parse_SkipsJunkLines_SortsResult()
// AgendaLayoutTests
[Fact] public void Panel_StaysInsideScreen_AllCorners()           // 1920×1080 and 1280×1024, each corner: card inside [0,w]×[0,h], H ≤ 0.3h
[Fact] public void Panel_RowsAreStackedInsideCard()
[Fact] public void Screen_OneColumnPerDay()
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~Agenda"`
Expected: build FAIL (types missing).

- [ ] **Step 3: Implement the five files** (signatures above; `AgendaStore` follows `NodeTagStore`'s load/save pattern: missing/unreadable file → default document, logged to `Console.Error`; `System.Text.Json` with `WriteIndented`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~Agenda"`
Expected: PASS.

- [ ] **Step 5: Commit** — `git commit -m "feat(agenda): model, now/next math, text parser, layout, store"`

---

### Task 2: Transport (proto, codec, coordinator, client dispatch)

**Files:**
- Modify: `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` (`AGENDA = 14;` in `CommandType`, `string agenda_json = 7;` in `SyncCommand`)
- Create: `WaBiBaBuSy.Core/Services/Show/AgendaCodec.cs`
- Modify: `WaBiBaBuSy.Core/Services/WallpaperSyncCoordinator.cs` (provider, broadcast, send on register)
- Modify: `WaBiBaBuSy.Core/Services/WaBiBaBuSyService.cs` (`AgendaProvider` handed to the coordinator at creation, `SetAgendaDelegate`, null on `DisconnectFromServerAsync`)
- Modify: `WaBiBaBuSy.Core/Services/ClientPlaybackDelegates.cs`, `WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs`
- Test: `WaBiBaBuSy.Tests/AgendaTransportTests.cs`

**Interfaces:**
- Consumes: `Agenda`, `AgendaMath.ShiftTimes` (Task 1).
- Produces:
```csharp
public static class AgendaCodec
{
    public static SyncCommand ToCommand(Agenda a, int sequenceNumber, long nowUtcMs); // copies a, SentUtcMs = TimestampUtc = nowUtcMs
    public static bool TryFromCommand(SyncCommand c, [NotNullWhen(true)] out Agenda? a); // shifts by TimestampUtc − SentUtcMs
}
// WallpaperSyncCoordinator
public Func<Agenda?>? AgendaProvider { get; set; }
public Task<int> BroadcastAgendaAsync(Agenda agenda);   // every connected client
// OnClientRegistered: also send AgendaProvider() (if non-null) once the stream is up — same 10 × 500 ms retry,
// independent of whether the client has an active cross-screen command.
// WaBiBaBuSyService
public Func<Agenda?>? AgendaProvider { get; set; }       // copied to each new coordinator
public void SetAgendaDelegate(Func<Agenda?, Task>? agenda);
// ClientPlaybackDelegates.Agenda / WallpaperPlaybackService.AgendaDelegate : Func<Agenda?, Task>?
internal Task HandleAgendaAsync(SyncCommand command);    // WallpaperPlaybackService; Dispatch routes AGENDA here without the timing wait
```
`DisconnectFromServerAsync` calls `PlaybackDelegates.Agenda?.Invoke(null)` after disposing the playback service (an explicit disconnect hides the agenda; a dropped link keeps it).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Codec_RoundTrip_KeepsItemsAndScreens()
[Fact] public void Codec_ShiftsTimesByTheConvertedTimestamp()   // ToCommand(now=1_000_000); cmd.TimestampUtc -= 90_000 (client 90 s behind); item 1_600_000 → 1_510_000
[Fact] public void Codec_RejectsOtherTypesAndBadJson()
[Fact] public async Task Client_Agenda_ForwardedWithoutWaiting()  // HandleAgendaAsync with TimestampUtc a minute ahead → delegate called within 5 s (pattern: ShowEventClientTests)
[Fact] public async Task Client_Dispatch_RoutesAgendaToDelegate()
```

- [ ] **Step 2: Run** `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~AgendaTransport"` — Expected: build FAIL.
- [ ] **Step 3: Implement** (proto regenerates on build; codec mirrors `ShowEventCodec`; coordinator broadcast mirrors `BroadcastShowEventAsync` without targets/assets).
- [ ] **Step 4: Run** the same filter, then the full suite `dotnet test WaBiBaBuSy.Tests` — Expected: PASS, no regressions.
- [ ] **Step 5: Commit** — `git commit -m "feat(agenda): AGENDA command, codec with clock shift, send on register"`

---

### Task 3: Player drawing (`cmd_agenda`)

**Files:**
- Create: `WaBiBaBuSy.Player.Common/Messages/PlayerCommandAgenda.cs` (`MessageType = "cmd_agenda"`, `Agenda? Agenda`, `bool FullScreen`)
- Modify: `WaBiBaBuSy.WallpaperEngine/Direct2D/D2DPlayerHost.cs` (`Task SendAgendaAsync(Agenda? agenda, bool fullScreen)`, same write path as `SendShowEventAsync`)
- Modify: `WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs` (`Task SendAgendaAsync(Agenda? agenda, bool fullScreen)` to all its hosts)
- Create: `WaBiBaBuSy.Player.D2D/Program.Agenda.cs`
- Modify: `WaBiBaBuSy.Player.D2D/Program.cs` — `case "cmd_agenda"` next to `cmd_show_event` (and in the no-reply list at ~3374); call `DrawAgenda(sceneStartUtc + raw)` right after both `DrawShowEvents(...)` calls (live ~1410, exact frame ~2215); dispose agenda resources in `DisposeNativeD2DResources`.

**Interfaces:**
- Consumes: `AgendaMath.Rows/Days`, `AgendaLayout.Panel/Screen`, `TextSprite.Render(text, colorHex)`.
- Produces: `PlayerCommandAgenda`, `D2DCompositionService.SendAgendaAsync`.

Player behaviour:
- stdin thread stores `(Agenda?, FullScreen)` under a lock and bumps a version; text sprites are cached by `(text, color)` in a dictionary filled by a worker `Task.Run` (`TextSprite.Render`), uploaded to `ID2D1Bitmap` on the render thread on first use; a missing sprite is simply skipped that frame. Cache cleared when the version changes (except strings still used). Countdown strings come and go once a minute — same cache.
- `DrawAgenda(long nowUtcMs)`: nothing when `Agenda == null || !Enabled`, or while an active Blackout event covers this node. Draw in device pixels (identity transform, not the canvas `Scale`). Panel: card fill, title, rows (`Time  Text` left, `Countdown`/`now` right-aligned), `DayHeader` rows get the day before the time, Past rows at 50 % opacity, Current row highlight bar `#FFD700` at 25 %, Next row pulse = highlight at `0.15 + 0.15·sin(2π·0.5 Hz·t)` when `Pulse`. Full screen: 60 % black dim over the scene, one column per day, notes at the bottom.
- Sprites are drawn into their layout box at the box height, width = min(aspect × height, box width) (squeezed if too long).

- [ ] **Step 1:** Build: `dotnet build WaBiBaBuSy.Player.D2D` — Expected: succeeds after the edits (no unit tests in the player; logic is in Task 1).
- [ ] **Step 2: Manual smoke** — temporarily send `cmd_agenda` from an existing test path is not needed; verified in Task 4/7.
- [ ] **Step 3: Commit** — `git commit -m "feat(agenda): player draws the agenda panel / full-size agenda"`

---

### Task 4: Hosts — forward, stand-ins, server state

**Files:**
- Create: `WaBiBaBuSy.Models/Show/AgendaStandIns.cs`
- Create: `WaBiBaBuSy.UI/Services/AgendaDisplay.cs`
- Modify: `WaBiBaBuSy.UI/Services/RemotePlaybackHost.cs`, `WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs`
- Test: `WaBiBaBuSy.Tests/AgendaStandInsTests.cs`

**Interfaces:**
- Produces:
```csharp
public static class AgendaStandIns
{
    // which monitors must get a stand-in started / stopped
    public static (IReadOnlyList<int> Start, IReadOnlyList<int> Stop) Plan(bool agendaOn, int monitorCount,
        IReadOnlySet<int> withScene, IReadOnlySet<int> withStandIn);
}
public sealed class AgendaDisplay   // UI/Services, one per host
{
    public AgendaDisplay(DesktopWindowManager desktopManager, Func<int, IReadOnlyList<string>> nodeIdsOfMonitor);
    public Agenda? Current { get; }
    public Task SetAsync(Agenda? agenda, IReadOnlyDictionary<int, D2DCompositionService> scenePlayers); // store, send to scene players + stand-ins, reconcile
    public Task SendToAsync(int monitorIndex, D2DCompositionService player);                              // a scene player just started
    public Task ReconcileAsync(IReadOnlyDictionary<int, D2DCompositionService> scenePlayers);             // after any start/stop
    public Task StopAllAsync();
}
// ServerPlaybackHost
public AgendaDocument AgendaDocument { get; }          // loaded from AgendaStore at construction
public Agenda? CurrentAgenda { get; }                   // [ObservableProperty]
public Task ApplyAgendaAsync(AgendaDocument doc);       // save, Build(TimeZoneInfo.Local, Tags.NodesWith("agenda")), local display, broadcast
```
- Stand-in = `D2DCompositionService` initialized like `RemotePlaybackHost.ApplyPlainCoreAsync`, but background `StretchedImage(IdleImage.EnsurePath(), UseLocalDesktopWallpaper = true)`, **no layers**, started with `startTimestampMs: 0`; then `SendAgendaAsync`. Not registered in the host's `_players`, never adopted for thumbnails, never part of `HasActiveRenderer` / `IsCrossScreenRunning`.
- `FullScreen` per monitor = `agenda.IsScreen(nodeIdsOfMonitor(index))`. Remote: candidates `{clientId}` and `{clientId}_MONITOR_{n}` (`_service.Client?.ClientId`). Server: `NodeIds.ServerLocalPrefix + n` and `NodeIds.LocalMachinePrefix + n`.
- `RemotePlaybackHost.Register()` adds `_service.SetAgendaDelegate(OnAgendaAsync)`; `OnAgendaAsync` runs through the existing `_gate` (`SerializedAsync`). After a player is added in `ApplyCrossScreenCoreAsync` / `ApplyPlainCoreAsync`: `await _agenda.SendToAsync(monitorIndex, player); await _agenda.ReconcileAsync(_players)`; at the end of `StopAllCoreAsync`: `await _agenda.ReconcileAsync(_players)`.
- `ServerPlaybackHost`: constructor sets `_service.AgendaProvider = () => CurrentAgenda` and, if the stored document is enabled, builds `CurrentAgenda` and calls `_agenda.SetAsync` (fire-and-forget with logging). A private `SyncAgendaAsync()` (= `_agenda.ReconcileAsync(_players)`) is awaited after every place `_players` gains or loses entries (lines ~575-581, ~630, ~667-675, ~719-725, ~1004, ~1434, ~1541-1557), and `_agenda.SendToAsync` after each new local player. `ApplyAgendaAsync` broadcasts via `_service.SyncCoordinator?.BroadcastAgendaAsync`.

- [ ] **Step 1: Write the failing tests** (`AgendaStandInsTests`)

```csharp
[Fact] public void Plan_On_StartsOnMonitorsWithoutSceneOrStandIn()   // 3 monitors, scene on {1} → Start {0,2}
[Fact] public void Plan_On_StopsStandInWhereASceneStarted()          // standIn {0,1}, scene {1} → Stop {1}
[Fact] public void Plan_Off_StopsEveryStandIn()                      // agendaOn false, standIn {0,2} → Stop {0,2}, Start {}
[Fact] public void Plan_IgnoresMonitorsOutOfRange()                   // standIn {5} with 2 monitors → Stop {5}
```

- [ ] **Step 2: Run** `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~AgendaStandIns"` — Expected: build FAIL.
- [ ] **Step 3: Implement** `AgendaStandIns`, `AgendaDisplay`, host wiring.
- [ ] **Step 4: Run** the filter, then `dotnet build` (whole solution) and `dotnet test WaBiBaBuSy.Tests` — Expected: PASS, zero build errors.
- [ ] **Step 5: Commit** — `git commit -m "feat(agenda): hosts forward the agenda and keep stand-ins on idle monitors"`

---

### Task 5: Room preview

**Files:**
- Create: `WaBiBaBuSy.UI/Controls/ScenePainter.Agenda.cs`
- Modify: `WaBiBaBuSy.UI/Controls/RoomView.cs` (an `Agenda? Agenda` styled property, invalidated on change; painted over every tile's screen rect after the events — also on tiles without a scene, over the plain `SceneBg`), `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (forward `ServerPlaybackHost.CurrentAgenda` to the room view in the existing `PropertyChanged` switch at ~717), `WaBiBaBuSy.UI/Views/MainWindow.axaml` (bind it)

**Interfaces:**
- Consumes: `AgendaMath.Rows/Days`, `AgendaLayout.Panel/Screen`.
- Produces: `void ScenePainter.PaintAgenda(DrawingContext ctx, Rect screen, double s, string nodeId, Agenda agenda, long nowUtcMs)` — same layout in node pixels scaled by `s`; `FormattedText` instead of sprites; full-size when `agenda.IsScreen([nodeId])`.
- The room view redraws at least once a minute while an agenda is enabled (existing animation tick if running, else a `DispatcherTimer` 30 s).

- [ ] **Step 1:** Implement; `dotnet build WaBiBaBuSy.UI` — Expected: 0 errors.
- [ ] **Step 2: Commit** — `git commit -m "feat(agenda): room preview paints the agenda"`

---

### Task 6: Agenda tab + tray toggle

**Files:**
- Create: `WaBiBaBuSy.UI/ViewModels/AgendaViewModel.cs`, `WaBiBaBuSy.UI/Views/AgendaPanel.axaml`, `WaBiBaBuSy.UI/Views/AgendaPanel.axaml.cs`
- Modify: `WaBiBaBuSy.UI/Views/MainWindow.axaml` (third `TabItem Header="Agenda"` after Playlist), `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (`public AgendaViewModel Agenda { get; }` built from `_server`), `WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs` (`NativeMenuItem "Agenda"` toggle with check mark, next to "Quick start", hidden while connected to another server as a client — same rule as Quick start)

**Interfaces:**
- Consumes: `ServerPlaybackHost.AgendaDocument`, `ApplyAgendaAsync`, `AgendaText.Parse`.
- Produces: `AgendaViewModel` with `[ObservableProperty]` `Enabled`, `Title`, `ItemsText`, `NotesText`, `FirstDay` (`DateTimeOffset?` for `DatePicker`), `Corner`; read-only `Preview` (`IReadOnlyList<string>`, e.g. `"Sat 18:00  Pizza"`, re-parsed on every change), `StatusText`. Every change restarts a 1 s debounce (`DispatcherTimer`), then `ApplyAgendaAsync(doc)`.

Panel layout (top to bottom): `ToggleSwitch` "Show agenda on all screens"; Title `TextBox`; "First day" `DatePicker` + Corner `ComboBox`; Items `TextBox` (multi-line, `AcceptsReturn`, watermark `Fri 18:00 Pizza`); parsed preview list (small, grey); Notes `TextBox` (multi-line); hint line "Seats tagged 'agenda' show the full timetable."

- [ ] **Step 1:** Implement; `dotnet build` — Expected: 0 errors.
- [ ] **Step 2: Commit** — `git commit -m "feat(agenda): Agenda tab and tray toggle"`

---

### Task 7: Docs + manual verification

**Files:**
- Modify: `CLAUDE.md` (Key Capabilities line for the agenda), `.docs/RECENT_UPDATES.md`, `.docs/plans/2026-10-04-manual-test-checklist.md` (new § "Agenda": idle seat shows it on its own wallpaper; scene start/stop keeps it; Stop/Clear keep it; switch off removes it everywhere; `agenda` tag → full size; client clock skew via test mode still flips at server time; explicit Disconnect hides it).

- [ ] **Step 1:** Run `dotnet build` and `dotnet test WaBiBaBuSy.Tests` — Expected: 0 errors, all tests pass.
- [ ] **Step 2:** Single-machine check: `dotnet run --project WaBiBaBuSy.UI`, enable the agenda with three items around now, observe the panel on the desktop with nothing playing, start a preset, Stop, switch off. Note results in the checklist.
- [ ] **Step 3:** `./tools/graphify-update.ps1`
- [ ] **Step 4: Commit** — `git commit -m "docs(agenda): capabilities, changelog, manual checklist"`

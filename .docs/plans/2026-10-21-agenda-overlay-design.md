# Agenda Overlay — LAN-day timetable on every screen — Design

**Created:** 2026-10-21 · **Status:** implemented 2026-10-21 (plan `2026-10-21-agenda-overlay-plan.md`, review fixes included); multi-machine check pending (manual checklist §8b). Claude's defaults marked *[call]*
**Related:** one-shot show events ([`2026-10-03-show-events-design.md`](2026-10-03-show-events-design.md)) — same drawing split (pure math in Models, player + room preview), but its own transport (§4). Sibling: [BiBaBo](2026-10-21-bibabo-bomb-game-design.md).

---

## 1. Intent

Patrick: *"a text/reminder on all screens as overlay onto the rest. Not the announcement but something like an
agenda or timetable for the LAN day."*

A small, calm timetable panel that sits on top of whatever wallpaper / scene plays, on every seat, for hours:
"18:00 Pizza · 20:00 CS2 tournament · 23:00 BiBaBo 💣", with the current item highlighted and the next one
counting down. Edited on the server; changes show up everywhere within a second.

### Goals
- Persistent (hours, across playlist items, scene changes, Stop / Clear / idle, reconnects, server restarts) until switched off.
- On every connected seat, whether or not it is part of the running scene.
- Readable from the seat, never in the way: one corner panel per screen, semi-transparent.
- "Now / next" is automatic from the clock — nobody has to touch it during the party.
- Same look on every seat (it is per monitor, not spanning the canvas).

### Non-goals (v1)
- Not a one-shot Announce (that already exists and scrolls a text lap).
- No per-seat content, no interaction, no sound.
- Not on top of games: it is wallpaper like everything else, visible when the desktop is.

## 2. What it looks like *[call — see Q1, Q2]*

```
┌──────────────────────────────┐
│ BiBaBu LAN · Sat             │   title (free text)
│  16:00  Arrival & setup      │   past items dimmed (last one kept)
│ ▶ 18:00  Pizza      in 0:23  │   current/next item: highlighted, countdown to start
│  20:00  CS2 tournament       │
│  23:00  BiBaBo 💣            │
│ WiFi: bibabu · pw on fridge  │   optional note line(s)
└──────────────────────────────┘
```
- Default top-right corner, 30 % of the screen height max, 85 % opaque dark card, white text, accent = show colour.
- Shows at most 6 items: the last past one, the current one, then the upcoming ones. Multi-day: a small day header
  ("Sat") above the first row of each day in the window; items on a later day than today are only shown when
  fewer than 6 rows of today remain.
- Notes (free text, multi-line) at the bottom of the card.
- The highlight moves to an item at its start time; 10 min before an item starts it pulses gently.
- Sized in screen fractions (not cm) so it looks the same on a 24" and the projector *[call]*.

## 3. Model (`WaBiBaBuSy.Models/Show/Agenda.cs`)

```csharp
public sealed class Agenda
{
    public bool Enabled { get; set; }
    public string Title { get; set; } = "";
    public List<AgendaItem> Items { get; set; } = new();   // sorted by StartUtcMs
    public List<string> Notes { get; set; } = new();
    public AgendaCorner Corner { get; set; } = AgendaCorner.TopRight;
    public int UtcOffsetMinutes { get; set; }               // server's local offset, for day grouping + "18:00" labels
}
public sealed class AgendaItem { public long StartUtcMs { get; set; } public string Text { get; set; } = ""; }

// Multi-day (Fri–Sun): items carry full UTC instants; the day a row belongs to is derived from the server's
// local date of StartUtcMs, sent along as Agenda.UtcOffsetMinutes so every seat groups and labels days the same way.
```
Persisted server-side in `%APPDATA%\WaBiBaBuSy\agenda.json`. Times are entered as local wall time on the server
("18:00", date = the LAN day, rolls past midnight) and stored as UTC — every seat compares against the shared,
server-synced clock, so "now" is identical everywhere.

Pure math `AgendaMath.Visible(agenda, nowUtcMs)` → the rows to show, which one is current, countdown text,
pulse phase. Tested like the other event math.

## 4. Transport — its own room-wide state, not a show event *[call]*

A builder check (2026-10-21) showed a "sticky show event" does not fit: Stop / Play / Clear wipe the event log,
the log caps at 16 events, and events only reach the running scene's seats. The agenda is room information, not
part of a scene, so it gets its own small state path:

- **Proto:** `CommandType AGENDA = 14;` + `string agenda_json = 7;` on `SyncCommand` (new numbers only; old
  clients log "unknown" and ignore it). The whole `Agenda` each time — it is a few hundred bytes.
- **Server** (`AgendaService`, Core, app-lifetime): loads `agenda.json`, saves on edit, sends AGENDA to **every
  connected client** on each change and to each client when it registers or resumes. Not tied to scenes,
  playlists, Stop, Clear or the event log. On server start an enabled agenda goes out as clients connect
  (no operator click needed — a timetable that needs restarting is no timetable).
- **Client** (`RemotePlaybackHost`): keeps the latest agenda and sends it (`cmd_agenda`) to every player it starts
  and to the running ones on change. Item times are shifted by the client's server-clock offset on receipt, like
  `T0Utc` in `ShowEventCodec` (the player clock is plain `DateTime.UtcNow`).
- **No scene on a seat** (idle, after Stop / Clear): the host starts a **local stand-in** player — the same idle config
  the server uses for events (`UseLocalDesktopWallpaper`, no layers) — so the panel sits on the seat's own Windows
  wallpaper. It stops when the agenda is switched off or a scene arrives. The server machine does the same for its
  own monitors. The stand-in is **not** a running scene: toolbar, health strip and Quick start ignore it.
- **Stop / Clear all / per-seat Clear** clear scenes only; the agenda stays until switched off (Agenda tab, or tray
  menu "Agenda on/off"). A blink of about the start lead when a scene starts is accepted.

## 5. Drawing

- **Player** (`Program.Agenda.cs`): drawn after the event pass (above the scene and other events, below the debug
  overlay and the test timecode strip). Hidden during Blackout and while a BiBaBo game runs *[call]*.
  Text via `TextSprite` (GDI+, the player has no DirectWrite): each row and the title rendered once per agenda
  version on the stdin thread; the countdown ("in 0:23") changes once a minute → re-rendered on a worker when the
  minute changes, never on the render thread. Card + highlight bar + pulse are D2D rectangles.
  Drawn in monitor pixels into the corner, regardless of the scene's canvas / Scale.
- **Room preview** (`ScenePainter`): the same panel per node tile, so the host sees it.

## 6. Editor (server UI)

An **Agenda** tab in the right panel, next to Playlist *[call]*:
- On/off switch, title, corner picker.
- Rows: time + text, add / remove / reorder by time; a "paste" box that takes lines like `18:00 Pizza`.
- Notes (multi-line).
- Every change is sent ~1 s after typing stops (debounced) — no Apply button.

## 7. Tests
- `AgendaMath`: current item at boundaries, past-midnight items, at most 6 rows, countdown text, empty agenda.
- Codec round trip; clock-offset shift of item times; disabled agenda draws nothing; local stand-in starts/stops with enabled + no scene.
- Paste parser: `18:00 Pizza`, `9:30 Breakfast`, junk lines ignored, times after midnight roll to the next day.

## 8. Build order
1. Model + `AgendaMath` + paste parser + tests.
2. Transport: AGENDA command, `AgendaService`, send on change / register / resume, client keeps + forwards, clock shift, local stand-in.
3. Player + preview drawing.
4. Agenda tab (rows with day + time, paste box with optional `Sat 18:00 Pizza` day prefix, notes).
4b. Agenda screen (`agenda` tag, full-size layout).
5. Docs + a showcase scenario with exact frames (before / during / after an item).

### 5.1 Agenda screen (full-size timetable) — Patrick: "extra timetable projector sounds nice"
Any seat tagged **`agenda`** (existing `tags.json` tags) shows the **full agenda big** instead of the corner panel:
all days as columns (Fri | Sat | Sun), current item highlighted, notes at the bottom, filling the screen over the
seat's own wallpaper or scene (dimmed 60 % behind it). Same model and math, a second layout. Handy for a spare
monitor or TV at the entrance *[call]*.

## 9. Patrick's answers (2026-10-21)
1. Always visible in a corner — yes.
2. Big reminder when an item starts — no (for now).
3. Multi-day: **Fri–Sun** → day headers (§2) and day columns on the agenda screen.
4. No projector, but an extra timetable screen "sounds nice" → §5.1 (`agenda` tag).
5. Free-text notes — yes.

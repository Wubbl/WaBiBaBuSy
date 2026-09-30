# GUI + Multi-Client Test Checklist (2026-09-28)

**Purpose:** first hands-on validation of everything built since July: Tier 1 reliability (2026-07),
LAN-party Tier 0 / 1.1 / 1.2 / 1.3 / 2.1 (2026-09-17), UI redesign Plans 1–3 + testing feedback
(2026-09-24 … 27). Until now all of it has been verified by build + unit tests only.
**Sources:** manual-check steps in the three UI redesign plans, `2026.07_OPEN_ITEMS.md §1`,
the Tier 0 / 1.x rows of `2026-09-17-lan-party-roadmap.md`.

**How to use:** tick items as you go. Anything that fails → a line in `.docs/2025.12_OpenIssues.md`
(what you did, what you saw, which machine, log excerpt). Screenshots help for layout issues.

---

## 0. Setup

- [ ] Same build on all machines: `dotnet build -c Release` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → all green. Note the commit hash: `______`
- [ ] Machines: server `______` (ideally 2 monitors) · client 1 `______` · client 2 `______` · client 3 `______`
- [ ] At least one pair with **different resolutions** (1080p + 1440p) for the physical-canvas checks
- [ ] Logging: Settings → Logging → "Log to file" on all machines (also covers the file-logging validation, §5)
- [ ] Test content ready: a transparent PNG logo, an animated GIF (ideally ~20 MB), a second image for backgrounds / extra images, one short video
- [ ] Clocks: note whether the machines are NTP-synced (for the skew test in §4)

## 1. GUI — single machine (server, no clients yet)

Launch `dotnet run --project WaBiBaBuSy.UI` → tray → Start Server → Open Server Control Panel.

### 1.1 Main window & toolbar
- [y] Window opens at 1400×800 and comes to the front when opened from the tray
- [y] One toolbar row: server button + badges left, show controls in the middle, `⋯` right
- [y] Connect row (IP, port, Connect, Find, Disconnect) sits next to Start Server; placeholder "Server IP (empty = auto)"
- [y] `⋯` → Settings…, Open log folder, collapsed "Developer tools"; the flyout is wide enough, LibVLC button not clipped

### 1.2 Room view
- [y] Lanes with headers ("Row 1 →"), one tile per node with #n, name, resolution, status dot
- [y] Room ⚙ → Split evenly into 2 rows → two lanes; row 2 header shows "←" and a "↕ facing" chip
- [y] Changing Path (Ring / Snake / Parallel) updates the Room ⚙ button text
- [y] Click / Ctrl+click / Shift+click / rubber band select; the selection bar follows; Ctrl+click a selected tile deselects it
- [y] Ctrl+click two nodes → "2 selected" + "Make row from selection" → creates a new lane
- [y] Drag a tile within its lane → insert marker, drop reorders, #n badges renumber
- [y] Drag a tile into the other lane and onto "+ new row" → lanes update
- [y] Escape mid-drag → nothing moves
- [y] Drop, wait two refresh cycles (~5 s) → order stays, no jump back
- [n] Right-click a tile → Start new row here (disabled on first seat), Merge into previous row (disabled in row 1), Clear, Resync, View logs (remote nodes only)
All good but View logs never comes back. Shows "Requesting logs .." but that's it
- [n] "Start new row here" on the 3rd tile of a 5-tile row → new lane with the last 3 nodes
Works but the order of the last 3 nodes is different in the new row
- [y] Select A and B, right-click C → only C selected, "Clear wallpaper" without "(n)", clears only C
- [y] Lane chip toggles "↕ facing" ↔ "⇉ same side"; lane `⋯` → Rename… / Move row up/down / Delete row
- [y] Gap number between tiles → inline editor: 12 → shows 12; empty → 0; 900 → clamped to 500
- [y] Close and reopen the app → lanes, traversal and gaps are kept (`seatmap.json`, `topology.json`)

### 1.3 Scene editor (right panel)
- [y] Gallery: multi-row grid of thumbnails with caption band (name, resolution, GIF frames + loop length, video length) and a tooltip; `+ Add` bottom-right; splitter between gallery and tabs works
- [y] Badge reads "◉ Preview: draft (not live)"; a fresh start shows the default draft, tiles still until a file is picked
- [y] Click a GIF → tiles paint it within ~150 ms; Ctrl+click another → "Extra images" chip; right-click removes it
Works i guess but what is the Extra images chip ?
- [y] Motion → Pattern / Speed changes → tiles update; readout says "crosses the room in … s"
- [y] Leave it 10 s untouched → sprite keeps moving, does **not** restart every 2 s
- [y] Select one node → "Targets: 1 selected", only that tile animates; deselect → all tiles
- [y] Room ⚙ → "Physical units" on → size/speed fields show cm; off → the old px values are back
- [y] Draft controls: 16× and ⏸ affect only the draft; clicking the badge → "● Preview: live"
- [y] `⟩` collapses the panel to a rail (badge → live); `⟨` restores the width; resize with splitter, collapse/expand again → width kept
- [n] Motion → Advanced → Endless with color grading None → amber chip "Endless needs a Traveling color mode"
There is no "Endless" in Motion -> Advanced ?
- [y] Hidden params exposed: Random seed + "Randomize", step interval, speed multiplier, Wave delay, Face travel direction
- [y] ▶ Play on selection with one node → only that machine plays; Play on all → every node; badge live shows the same scene
- [ ] Motion → Static with the default IconZone background → Play → sprite stays centered on every machine (no path wandering, no rotation)
- [ ] Gallery right-click → "Show as plain wallpaper on all nodes" on a JPG, a GIF and an MP4 → each fills every monitor, still / looping, no colors; the editor shows SolidColor · Fill · Simultaneous · Static
- [ ] Footer "▣ Plain wallpaper ▾" → On selection (disabled with nothing selected) → only the selected node shows the current file plainly

### 1.4 Playlist tab & show controls
- [ ] Tab loads the newest saved playlist; a 30 000 ms item shows "30"
- [ ] `＋ Add current scene` → row with thumbnail; Save → name appears in the picker
- [ ] Click a row → scene loads into the editor and the tiles; change speed → Save to playlist ▾ → Update "name" → Details show the new speed
- [ ] Drag ≡ of the first row below the last → marker follows, row moves, its duration box still updates the summary
- [ ] Right-click → Duplicate ("name copy" below) / Remove
- [ ] Duration "12,5" → "12.5s"; "abc" → "(default)"
- [ ] Toolbar picker lists playlists; picking one loads it into the tab
- [ ] ▶ starts the show: ■ and ⏭ appear, label "Now: A · next: B in 0:1x", playing row has a green ▶
- [ ] ⏭ switches immediately; ■ stops the show, ⏭ and label disappear
- [ ] Play from the editor during a show → the show stops first, then the scene plays; ■ stops it
- [ ] Save a playlist "E2E" with 3 items (different content + movement), close and reopen → "E2E" loaded

### 1.5 Settings
- [ ] `⋯` → Settings opens centered, sidebar, Server selected; each item shows only its section
- [ ] Advanced expanders collapsed (Server: Max Clients + Service Name; Client: Heartbeat + Max Cache Size)
- [ ] Browse buttons open a folder picker; Cancel keeps the value
- [ ] Open again from tray and from `⋯` → no second window, existing one to front
- [ ] Change Server Address → Cancel → reopen → old value; Save → reopen → new value
- [ ] `client-config.json` read-only → Save → amber error line, window stays open (remove read-only after)
- [ ] `clientId` in `client-config.json` unchanged after Save
- [ ] Log Directory only visible with "Log to file"; Enter = Save, Esc = Cancel

### 1.6 Local wall (server's own monitors)
- [ ] Sequential Linear GIF crosses the bezel between the two local monitors without a jump
- [ ] Face travel direction: a Bounce sprite flips when it reverses (no backwards swimming, no flicker at the apex)
- [ ] Wave mode: Simultaneous + Bounce + 250 ms node delay → the bounce runs from monitor 1 to 2
- [ ] Mixed refresh rates between local monitors → ⚠ on the tile with a tooltip
- [ ] F11 debug overlay in the player still works

## 2. Multi-client — connect & identity (server + 2–3 clients)

- [ ] Client: type the server IP → Connect connects directly (no discovery override)
- [ ] Client: empty IP → Connect/Find discovers the server via mDNS
- [ ] All clients appear as tiles within ~5 s with correct name, resolution, refresh rate, status dot
- [ ] Arrange: 2 rows (e.g. server + client 1 in row 1, clients 2–3 in row 2), facing, Ring; set gaps
- [ ] **Restart a client app** → same tile comes back in the same seat/order (persistent ClientId)
- [ ] **Restart the server** → order, rows and gaps intact (`%APPDATA%\WaBiBaBuSy\topology.json`)
- [ ] Right-click a remote tile → View logs works; Resync re-sends that node's animation

## 3. Multi-client — animation parity

Play each scene on all nodes; compare the wall with the room preview ("sprite on: <host>").

- [ ] **Clean start:** the sprite appears on the same frame on all nodes (future-start lead), no ragged start
- [ ] **Sequential Linear across machines:** the sprite leaves one machine and enters the next at the same moment, no jump, speed identical
- [ ] **SineWave + Reversed:** identical path on remotes
- [ ] **Simultaneous on a remote:** each node plays its own copy; Wave delay runs across machines in node order
- [ ] **Pattern + Traveling Colors:** pixel-identical cells and colors on server and remotes
- [ ] **IconZone** with global path on remotes behaves like locally
- [ ] **Multi-image / background image item:** the extra images and background render on remotes (all assets transferred)
- [ ] **Ring:** sprite leaves the last seat and enters seat 1 in the same frame (no teleport, seam-safe)
- [ ] **Facing rows:** the sprite travels left→right as seen by the people on both sides
- [ ] **Mixed resolutions (1080p + 1440p), pixel mode:** no vertical jump at the bezel (canvas height fix)
- [ ] **Physical units on:** same sprite height in cm on both monitors (measure with a ruler), same cm/s; Target-height fit mode honors the cm value
- [ ] **Room preview vs wall:** "sprite on: …" matches the machine that actually shows it
- [ ] **Face travel direction** on a remote matches the server

## 4. Multi-client — show reliability & sync

- [ ] Start Show "E2E" on all nodes: tiles show "⬇ x/y", then "✓ cached" before the first item plays
- [ ] Item switches without a download pause; "next: … in m:ss" counts down correctly
- [ ] ⏭ skip propagates to all remotes at once
- [ ] Restart a client during the show → it rejoins the current item at the right position, no re-download (`cache-index.json`)
- [ ] Health strip reflects reality; "Resync all" re-sends the running animation to everyone
- [ ] Linear lap-snap: an item switch waits for the lap end when enabled
- [ ] Tiles show a green "ping N ms" (LAN: 0–2 ms); the tooltip shows sync ±, ping and the corrected clock offset
- [ ] Skew one client's clock by ±2 s *before* starting → animation stays aligned; the tooltip's clock offset shows ≈ ±2000 ms, the label stays green
- [ ] Step a client's clock *during* a scene (`w32tm /resync` or set the time) → server log warns "Clock offset … jumped" once; Resync all realigns it
- [ ] Close the server window → client thumbnail uploads stop within one heartbeat (5 s); reopen → thumbnails return within ~6 s
- [ ] Prefetch a large video to all clients → total server upload stays at the Settings → Server → Upload Limit (default 20 MB/s, Task Manager → Ethernet)
- [ ] **10-minute run:** no visible drift between machines (MVP target ±50 ms); watch CPU (<15%) / GPU (<10%) / memory (<200 MB) on a client

## 5. Failure & recovery

- [ ] Pull a client's network cable mid-animation for ~10 s → reconnects (1 s … 30 s backoff) and rejoins at the correct position; time it: `___ s` (target < 5 s after the link is back)
- [ ] Close a client app during a room drag → drag cancels (or applies if the tile stays offline); refresh continues
- [ ] Hard power-off a client → the server drops the tile within ~40 s
- [ ] Stop the server while clients play → clients keep their wallpaper and reconnect when the server is back
- [ ] File logging: log files exist at `%LOCALAPPDATA%\WaBiBaBuSy\Logs\` on every machine, rolling daily

## 6. Optional

- [ ] Installer on a clean Windows 10/11 machine (Inno Setup, `/Installer/`); fallback version 2.6.3
- [ ] Auto-update from an older build (SHA-256 check, rollback on failure)

## Results

| Section | Pass | Fail | Notes / issue ids |
|---|---|---|---|
| 1 GUI | | | |
| 2 Connect & identity | | | |
| 3 Animation parity | | | |
| 4 Show & sync | | | |
| 5 Failure & recovery | | | |
| 6 Optional | | | |

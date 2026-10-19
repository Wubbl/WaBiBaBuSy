# Manual Test Checklist (2026-10-04)

**Purpose:** one day of hands-on testing. It carries over everything still unticked from
[`2026-09-28-gui-and-e2e-test-checklist.md`](2026-09-28-gui-and-e2e-test-checklist.md) (that file keeps the 09-29 results),
re-checks the four `[n]` findings from 09-29, and adds what the 2026-10-02/03 work left **build-verified only**
(test-mode UI, CLI, player probe handling, networking).

**How to use:** tick `[x]` pass / `[n]` fail as you go, note what you saw under the item. Failures → a line in
`.docs/2025.12_OpenIssues.md` (what you did, what you saw, which machine, log excerpt). Order matters: the
automated runs (§4) are quick and cover timing + pixel parity, so §5 only needs eyes for what software cannot judge.

**First multi-machine session (2026-10-09):** order, commands and pre-flight results in [`2026-10-09-remote-session-runbook.md`](2026-10-09-remote-session-runbook.md).

**Time budget:** §0–§2 ≈ 1 h (server alone) · §3–§4 ≈ 1 h · §5–§6 ≈ 1.5 h · §7 ≈ 30 min · §8 ≈ 45 min · §9 ≈ 1 h (optional).

---

## 0. Setup

- [ ] Same build everywhere: `git pull`, `dotnet build -c Release` → 0 errors, `dotnet test WaBiBaBuSy.Tests` → all green. Commit: `______`
- [ ] Machines: server `______` (2 monitors) · client 1 `______` · client 2 `______` · client 3 `______`
- [ ] At least one pair with **different resolutions** (1080p + 1440p)
- [ ] Settings → Logging → "Log to file" on every machine
- [ ] Server: Settings → Server → Developer → **Enable test mode** on, then restart the server
- [ ] Clients: Settings → Client → "Answer automated test runs from the server" is on (default)
- [ ] Content: transparent PNG logo, an animated GIF (~20 MB), a second image (background / extra image), a short video
- [ ] Note whether the machine clocks are NTP-synced (§6 skew items)

## 1. Re-check the 09-29 findings

- [ ] **View logs** (right-click a remote tile): the logs appear within 10 s, header "=== Logs from <hostname> ===". On 09-29 it stayed at "Requesting logs…"; fixed 09-30 (`6ac1607`), display reworked 10-02 (`f5949c4`).
- [ ] **View logs, no reply:** stop the client app first → after 10 s "No reply from … Its logs are in its configured log folder on that machine (default …)".
- [ ] **Start new row here** on the 3rd tile of a 5-tile row. With Path = Ring or Snake the new row runs **←** (right to left), so its tiles show in reverse on screen while the chain order (#n badges) stays 3, 4, 5. Check whether that is what you saw on 09-29. If the #n badges themselves are out of order, it is a bug. Also note which you would expect in a real room: ______
- [ ] **Endless:** Motion → Movement **Linear** → Advanced → "Endless (no loop reset)" (the box only appears for Linear) → with color grading None the amber chip "Endless needs a Traveling color mode" appears above the tabs.
- [ ] **Extra images:** Ctrl+click a second thumbnail → it is listed under "Extra images". These are additional sprites: with a pattern each cell picks one of the images; without a pattern the copies are spread by the "spread (px)" / "time offset (ms)" fields under Motion. Is that clear enough in the UI? Ideas: ______

## 2. GUI — server alone (carried over from 09-28 §1.3–§1.6)

### 2.1 Scene editor
- [ ] Motion → Static with the default IconZone background → Play → sprite stays centered on every monitor (no path wandering, no rotation)
- [ ] Gallery right-click → "Show as plain wallpaper on all nodes" on a JPG, a GIF and an MP4 → each fills every monitor, still / looping, no colors; the editor shows SolidColor · Fill · Simultaneous · Static
- [ ] Footer "▣ Plain wallpaper ▾" → On selection (disabled with nothing selected) → only the selected node shows the current file plainly

### 2.2 Playlist tab & show controls
- [ ] Tab loads the newest saved playlist; a 30 000 ms item shows "30"
- [ ] `＋ Add current scene` → row with thumbnail; Save → name appears in the picker
- [ ] Click a row → scene loads into the editor and the tiles; change speed → Save to playlist ▾ → Update "name" → Details show the new speed
- [ ] Drag ≡ of the first row below the last → marker follows, row moves, its duration box still updates the summary
- [ ] Right-click → Duplicate ("name copy" below) / Remove
- [ ] Duration "12,5" → "12.5s"; "abc" → "(default)"
- [ ] Toolbar picker lists playlists (incl. the presets: BiBaBu Party, Chill Lounge, Hype — `dotnet run tools/build-playlist-presets.cs` if missing); picking one loads it into the tab
- [ ] ▶ starts the show: ■ and ⏭ appear, label "Now: A · next: B in 0:1x", playing row has a green ▶
- [ ] ⏭ switches immediately; ■ stops the show, ⏭ and label disappear
- [ ] Play from the editor during a show → the show stops first, then the scene plays; ■ stops it
- [ ] Save a playlist "E2E" with 3 items (different content + movement), close and reopen → "E2E" loaded

### 2.3 Settings
- [ ] `⋯` → Settings opens centered, sidebar, Server selected; each item shows only its section
- [ ] Advanced expanders collapsed (Server: Max Clients + Service Name; Client: Heartbeat + Max Cache Size)
- [ ] Browse buttons open a folder picker; Cancel keeps the value
- [ ] Open again from tray and from `⋯` → no second window, existing one to front
- [ ] Change Server Address → Cancel → reopen → old value; Save → reopen → new value
- [ ] `client-config.json` read-only → Save → amber error line, window stays open (remove read-only after)
- [ ] `clientId` in `client-config.json` unchanged after Save
- [ ] Log Directory only visible with "Log to file"; Enter = Save, Esc = Cancel

### 2.4 Local wall (server's own monitors)
- [ ] Sequential Linear GIF crosses the bezel between the two local monitors without a jump
- [ ] Face travel direction: a Bounce sprite flips when it reverses (no backwards swimming, no flicker at the apex)
- [ ] Wave mode: Simultaneous + Bounce + 250 ms node delay → the bounce runs from monitor 1 to 2
- [ ] Mixed refresh rates between local monitors → ⚠ on the tile with a tooltip
- [ ] F11 debug overlay in the player still works

### 2.5 Sprite effects (new 10-03; exact frames verified on the server, editor and preview build-verified only)
- [ ] Scene editor → **Effects** tab: Trail on → the room preview shows the fading trail (rainbow with a time-based color mode, like the wall); Pulse / Spin visible in the preview
- [ ] Effects + Face travel direction on a Bounce scene: sprite and ghosts flip together at the turn, no flicker
- [ ] Bezel crossing: Entry Ripple + Exit Glow → on the wall the leaving monitor glows at its edge and the entering one shows the ripple at the same moment
- [ ] Flash on a 2-row Ring: one flash per crossing, none at the seam while the sprite straddles it, none on the screen where the sprite starts
- [ ] Effects tab with a pattern / an extra image / an IconZone background → amber hint, nothing drawn
- [ ] Playlist row for a scene with effects lists them ("trail 8 · pulse · entry ripple · exit glow")
- [ ] Showcase preset "BiBaBu Party" → the "Comet" item plays with its trail on all nodes
- [ ] Facing rows (mirrored): the glow / ripple appears at the edge the sprite actually crossed

### 2.6 Close / reopen the server window (new 10-03, build-verified only)
Close with the window's ✕, reopen from the tray. Before 10-03 reopening blacked out the wall and lost the show.
- [ ] **During a scene:** Play a Sequential scene on all → close → the wall keeps playing → reopen → the wall does **not** blink or go black; toolbar ■ visible; tiles marked as playing; toggle the preview badge to "● Preview: live" → the room paints the scene in step with the wall
- [ ] … then ■ stops it on every node (remotes too); Play another scene → the old one is replaced, no second sprite stacked on a monitor
- [ ] **During a show:** start a looping show (short items) → close → wait for at least one item switch (the wall switches; remotes too) → reopen → toolbar ■ ⏭ and "Now: … · next: … in m:ss"; Playlist tab shows that playlist with the green ▶ on the current row
- [ ] … ⏭ switches at once and the ▶ row follows; Resync all re-sends to the remotes; ■ stops the show; Clear all clears the wall
- [ ] **Reopen several times** during a show → still one player per monitor (Task Manager: `WaBiBaBuSy.Player.D2D` count = monitors playing)
- [ ] **During a test run:** Developer tools → Run test suite → `smoke-local` → close the window mid-run → the run finishes (report written) and restores what played before → reopen mid-run instead → Cancel and the status line are back and keep updating
- [ ] **Control API with the window closed:** `POST /test/run` → refused ("open the server control panel first"), as before
- [ ] **Debug overlay:** F11 on → close / reopen → the overlay checkbox is still on, the player still shows it
- [ ] **Plain wallpaper** (Developer tools D2D or LibVLC) → close / reopen → still playing; Clear all removes it
- [ ] **Exit from the tray** with the window closed during a show → no `WaBiBaBuSy.Player.D2D` left in Task Manager, desktop wallpaper restored

### 2.7 Presets & tray Quick start (new 10-03, build-verified only)
Preset folder: `%APPDATA%\WaBiBaBuSy\presets\`. Rename it first to test the first start.
- [ ] **First start** with no preset folder: 14 presets + `assets\` appear (Logo Conveyor … Party Colors); no Lunar Tears
- [ ] Editor **Presets ▾ → Comet**: the draft is the comet (trail in the preview); the room selection is unchanged; Play on all plays it
- [ ] **Save as preset…** with a new name → it appears in Presets ▾, Playlist "＋ Add ▾ → From preset" and the tray's Quick start without a restart; typing an existing name → the button says "Replace preset"
- [ ] **Export…** to a USB / other folder → `Name.json` + `assets\` next to it; on a second machine (or after deleting the preset) **Import…** it → loads into the editor and plays
- [ ] **Import…** a raw showcase scene (`TestScenarios\showcase\scenes\05-cat-parade.json` next to the exe) → its assets are copied into `presets\assets\`; importing it again → "05-cat-parade (2)", no second copy of `cat.gif`
- [ ] Playlist **＋ Add ▾ → From preset → Mexican Wave** → item named "Mexican Wave", plays on all seats in the show
- [ ] **Window never opened** after an app restart: tray **Quick start ▸ Mexican Wave** → plays on every local monitor; Stop / Clear all from the window afterwards stop it
- [ ] Server started earlier, a client connected, window **closed**: Quick start → plays on the client too (the host builds the room itself)
- [ ] Quick start **while a show runs** → the show stops, the preset plays; open the window → toolbar shows the running scene, the room marks the nodes
- [ ] Connected **as a client** to another server: Quick start is hidden in the tray menu
- [ ] A hand-broken preset JSON in the folder (bad JSON, or e.g. `"ColorGrading": { "ColorList": null }`): Presets ▾ lists it, loading it shows "Could not load …" under the footer buttons; nothing crashes
- [ ] Quick start of that broken preset (or one whose asset was deleted): nothing plays, and the Quick start submenu now starts with a greyed "⚠ <name>: <reason>" line; a successful Quick start removes it
- [ ] **Ring Cat** with the room's Path = Ring: the cat faces where it runs and wraps seamlessly around both rows
- [ ] Bezel gaps set in a standalone room (no server) survive closing / reopening the window

## 3. Test mode — UI, CLI and API (server alone; new 10-02, build-verified only)

- [ ] **Settings refresh:** turn "Enable test mode" off → Save → `⋯` → Developer tools no longer offers "Run test suite" without reopening the window; on again → it is back
- [ ] **Menu run:** Developer tools → Run test suite… → `TestScenarios\smoke-local.json` → status line shows progress, Cancel visible; ends with "Test: pass · <folder>"
- [ ] **Double-click guard:** double-click "Run test suite…" quickly → only one file picker opens
- [ ] **Open results:** "Open last results" opens `report.html`; delete that run folder first → "Could not open the report: …" in the status line, no crash
- [ ] **report.html:** verdict, step table, drift chart, annotated captures (timecode strip + marker boxes), "Node failures" section absent on a clean run
- [ ] **CLI from a terminal** (main window open on the server): `start /wait WaBiBaBuSy.UI.exe --test-run TestScenarios\smoke-local.json --exit` (cmd) or `Start-Process -Wait …` (PowerShell) → the result line appears in that terminal, `echo %errorlevel%` = 0; **while it runs, the main window menu shows the run with Cancel**
- [ ] **CLI typo:** `WaBiBaBuSy.UI.exe --tset-run x` → warning + usage line in the terminal
- [ ] **Control API:** `curl -X POST -H "Content-Type: application/json" -d "{\"scenario\":\"C:/…/TestScenarios/smoke-local.json\"}" http://127.0.0.1:50052/test/run` → 202; the menu shows the run; `curl http://127.0.0.1:50052/test/status` while it runs
- [ ] **API from another machine:** from a client, `curl http://<server-ip>:50051/test/status` → 404 (not 415/400/405)
- [ ] **Port 50052 busy:** start something on 50052 first (e.g. `python -m http.server 50052 --bind 127.0.0.1`), then Start Server → the server starts (clients can connect), the log warns that the test control API is off
- [ ] **No logs popup:** during a run with a remote (§4) the main window's client-logs panel does **not** open when the run fetches logs at the end
- [ ] **Frame statistics:** in the run's `logs\server.log` search for `DWM fallback`. If it appears, note what was on screen (maximized / fullscreen window over the desktop? monitor asleep? HDR?): ______. Each sample in `report.json` has `presentSource` (`dxgi`, `dxgi-stepback`, `dwm`, `nominal`); none should lack a present time
- [ ] **DWM model calibration:** when DXGI works, the log shows `DXGI present X ms after Present; dwm model Y ms (diff ±Z ms)` — Z mostly within one refresh period: ______

## 4. Automated runs — 2+ machines (covers 09-28 §3–§4 timing and parity)

Connect the clients first (§5.1). Attach each `report.html` path to the results table.

- [ ] `sync-basic.json` (~11 min: clean start, bezel crossing, 10-minute drift / CPU / GPU / memory series) → verdict `______`, worst spread `___ ms`
- [ ] `parity-matrix.json`, room in **pixel mode** → verdict `______`, all exact-frame comparisons 0 % differing
- [ ] `parity-matrix.json` again with **Room ⚙ → Physical units** on → verdict `______`
- [ ] `clock-skew.json` with `REMOTE-HOSTNAME-1/-2` replaced by the tile names → verdict `______`; the testMode step passes (skew key matched) and the first probe's skew check passes
- [ ] **Lost node:** start `sync-basic`, pull one client's network cable after ~1 min → the run continues; `report.html` lists the client under "Node failures" with a time ≈ when the cable came out (within one probe); run verdict at least warn
- [ ] **Client logs:** each run folder has `logs\<client>.log` per client, no two clients sharing a file even with similar names

## 5. Multi-client — manual (carried over from 09-28 §2–§3)

### 5.1 Connect & identity
- [ ] Client: type the server IP → Connect connects directly (no discovery override)
- [ ] Client: empty IP → Connect / Find discovers the server via mDNS
- [ ] **Client connected from the tray only** (main window never opened on that client): Play a Sequential scene on all → it crosses screens in sync with the others; the client log has no `D2DCrossScreenApplyDelegate is NULL` (fixed 10-03, `a434e43`, build-verified only)
- [ ] On that client, open the main window while the scene plays → the wallpaper keeps playing (the window no longer kills the client's players); close it again → still playing
- [ ] Server: play a scene, close the main window, reopen it, press Play again → one player per monitor (Task Manager: `WaBiBaBuSy.Player.D2D` count = monitors), no stacked or flickering sprites
- [ ] All clients appear as tiles within ~5 s with correct name, resolution, refresh rate, status dot
- [ ] Arrange: 2 rows (server + client 1 in row 1, clients 2–3 in row 2), facing, Ring; set gaps
- [ ] **Restart a client app** → same tile, same seat / order (persistent ClientId)
- [ ] **Restart the server** → order, rows and gaps intact (`%APPDATA%\WaBiBaBuSy\topology.json`)
- [ ] Right-click a remote tile → Resync re-sends that node's animation
- [ ] Remote tiles show live thumbnails; after a client reconnect (§7) they come back without replaying a scene

### 5.2 What only eyes can judge
- [ ] **Clean start:** the sprite appears on the same frame on all nodes, no ragged start
- [ ] **Ring:** sprite leaves the last seat and enters seat 1 in the same frame (no teleport)
- [ ] **Facing rows:** the sprite travels left → right as seen by the people on both sides
- [ ] **Mixed resolutions, pixel mode:** no vertical jump at the bezel
- [ ] **Physical units:** same sprite height in cm on 1080p and 1440p (ruler: ___ cm / ___ cm), same cm/s; Target-height fit honors the cm value
- [ ] **Room preview vs wall:** "sprite on: …" matches the machine that shows it
- [ ] **Face travel direction** on a remote matches the server
- [ ] **IconZone** with global path on remotes behaves like locally
- [ ] **Multi-image / background image item** renders on remotes (assets transferred)
- [ ] **Simultaneous + Wave delay** runs across machines in node order

## 6. Show reliability & sync (carried over from 09-28 §4)

- [ ] Start Show "E2E" on all nodes: tiles show "⬇ x/y", then "✓ cached" before the first item plays
- [ ] Item switches without a download pause; "next: … in m:ss" counts down correctly
- [ ] ⏭ skip propagates to all remotes at once
- [ ] Restart a client during the show → rejoins the current item at the right position, no re-download (`cache-index.json`)
- [ ] Health strip reflects reality; "Resync all" re-sends the running animation to everyone
- [ ] Linear lap-snap: an item switch waits for the lap end when enabled
- [ ] Tiles show a green "ping N ms" (LAN 0–2 ms); tooltip: sync ±, ping, corrected clock offset
- [ ] Skew a client's clock ±2 s *before* starting → stays aligned; tooltip offset ≈ ±2000 ms, label stays green
- [ ] Step a client's clock *during* a scene → server log warns "Clock offset … jumped" once; Resync all realigns it
- [ ] Close the server window → client thumbnail uploads stop within ~5 s; reopen → back within ~6 s
- [ ] Prefetch a large video to all clients → server upload stays at the Upload Limit (default 20 MB/s, Task Manager → Ethernet)
- [ ] **Show + test run** (restore added 10-03, build-verified only): start a looping show, ⏭ to item 2 or 3, run `smoke-local` → afterwards the show is back on that item from its start; toolbar now/next, ■ / ⏭ and the ▶ row in the Playlist tab show it; remotes play it. Blank time before it appears: ___ s
- [ ] Same with **shuffle** on → the following items keep the interrupted cycle's order
- [ ] Test run while a **plain scene** plays → the scene comes back; while **nothing** plays → everything stays stopped
- [ ] **Cancel** a run mid-way while a show was running → the show still comes back
- [ ] Press ▶ / Play while the restore is in progress → refused or waits, no double start
- [ ] A **non-looping** show resumed on its last item plays it and ends normally

## 7. Failure & recovery (carried over from 09-28 §5)

- [ ] Pull a client's network cable mid-animation for ~10 s → reconnects and rejoins at the right position; time it: `___ s` (target < 5 s after the link is back)
- [ ] Close a client app during a room drag → drag cancels (or applies if the tile stays offline); refresh continues
- [ ] Hard power-off a client → the server drops the tile within ~40 s
- [ ] Stop the server while clients play → clients keep their wallpaper and reconnect when the server is back
- [ ] File logging: log files at `%LOCALAPPDATA%\WaBiBaBuSy\Logs\` on every machine, rolling daily

## 8. Show events (roadmap 2.4 slice 2, build-verified only)

- [ ] **Showcase exact frames:** Developer tools → Run test suite… → `TestScenarios/showcase/events.json` → report shows confetti rising/falling, the spotlight border on seat 1 (others dimmed with a chevron), the cat mid-lap, whiteout half / full (the Blackout fires in white so it shows on the dark scene), the announce text.
- [ ] **Celebration on 2+ machines:** confetti crosses the bezel continuously (a piece leaving one screen appears on the next at the same height).
- [ ] **Spotlight:** pick a seat in the strip (or select its tile) → its border pulses gold for 8 s; every other seat dims and its chevron points toward it (mirrored row: still toward it; Ring: the shorter way).
- [ ] **Traversal:** default (scene sprite) and a picked image (club logo) each lap the room exactly once and vanish; on a client that never had the image it still appears (download), at most late — never at the wrong place. With a video scene and no picked image the strip refuses with a status message.
- [ ] **Blackout / Announce:** blackout fades in and out over 3 s on all seats; the announce text laps once, readable, same position on all seats.
- [ ] **Playlist item switch during an event:** fire a Celebration 1 s before an item switch → the burst continues across the switch without restarting.
- [ ] **Stop clears events:** fire a 10 s Spotlight, press Play / Quick start (or Clear nodes) → no stale event on any seat afterwards.
- [ ] **Late join:** fire a 10 s Spotlight, then connect a client 3 s later and start a scene → no stale event after it ended; during it, Resync shows it at the right position.
- [ ] **Reconnect / Resync during an event:** fire a 20 s Traversal (or 10 s Spotlight); pull a seat's network cable for ~3 s (or restart the client app) → after it resumes, that seat draws the event at the same position as its neighbours. Also: Resync all and node Resync mid-event → the event stays at its position on every seat (no restart, no duplicate).
- [ ] **Scene seats only:** play a scene on a subset of nodes, fire a Celebration → idle clients (not in the scene) draw nothing; Stop reaches the same seats.
- [ ] **Hotkeys:** with the server running, Ctrl+Alt+1..5 fire the events with the main window closed (tray only); on a client machine the keys do nothing (not registered); a key held by another app shows "Hotkey(s) taken by another app" in the strip.
- [ ] **Hotkeys toggle:** untick "Hotkeys (Ctrl+Alt+1..5)" in the Events strip → the keys are free at once (on a German keyboard AltGr+2 types ² again, no event fires); tick it → events fire again; the setting survives an app restart.
- [ ] **Old client:** a client on the previous build stays on its scene when an event is fired (its log shows "Unknown command type: 12").
- [ ] **Room preview (Live):** the tiles show the same events at the same moments.
- [ ] **Performance:** Celebration on a 4K seat stays at the monitor's refresh rate; player CPU stays under 15 %.
- [ ] **Interrupts:** a playlist with "Every 1 min Traversal" laps the logo once a minute without changing the item; stopping the show (or a non-looping show ending) stops the interrupts.

## 9. Optional

- [ ] Installer on a clean Windows 10/11 machine (Inno Setup, `/Installer/`); fallback version 2.6.3
- [ ] Auto-update a client from an older build (SHA-256 check, rollback on failure)

## Results

| Section | Pass | Fail | Notes / issue ids / report paths |
|---|---|---|---|
| 1 Re-checks | | | |
| 2 GUI server alone | | | |
| 3 Test mode UI / CLI / API | | | |
| 4 Automated runs | | | |
| 5 Multi-client manual | | | |
| 6 Show & sync | | | |
| 7 Failure & recovery | | | |
| 8 Show events | | | |
| 9 Optional | | | |

# WaBiBaBuSy - Recent Updates & Changelog

**Last Updated:** 2026-10-21

---

## 2026-10-21 (late night) — BiBaBo after the first hands-on test: bigger bomb, rules beside it, typed keypad codes

- **Bigger bomb**: 50 % of the seat's height (was 35 %), centred at 42 % so the keypad fits below; the countdown in the box grows with it. Rules (or "RULES: somewhere in the room") moved **left of the bomb**, right-aligned beside the bombs below the cable terminals, on a dark panel so cables pass behind; keypad keys keep their on-screen size. The timer above a custom logo moved into the space above (`BombMath.TimerAboveTop/Height`).
- **Fixes**: the logo's bombs were cut off at the bottom of `bibabo-bomb.gif` (the SVG page ended at the bra's bottom; now 12 mm more, `LogoTimerBox` re-measured); "YOU EARNED A BEER!" was cut at the top of the beer GIF (now top-anchored and height-capped; the beer GIF is 1.6 bomb sizes for the bigger bomb, the explosion 2.2).
- **Typing the keypad code**: the keypad only took clicks on the drawn keys. `RawMouseSink` → `RawInputSink` also observes the keyboard: number row, numpad (Num Lock on or off), Backspace / Delete / C = clear — only while the desktop has the focus (like clicks: typing into a chat or game never counts), only on the bomb machine during a keypad game, auto-repeat ignored (`BombHit.KeyFromVirtualKey`). Rule line now says "click or type it on the keypad".
- **Verified**: build + 1305 tests (key mapping); showcase frames on this machine: bigger bomb with intact logo, rules beside it, keypad below, BOOM, beer with the full title. **Not verified**: typing on a real keyboard (needs a person at the desktop).

## 2026-10-21 (night) — BiBaBo logo: bombs instead of Busen, countdown in the timer box; explosion and beer GIFs

- **Beer on DEFUSED**: the bomb seat plays `Content/bibabo-beer.gif` once and holds its last frame (8 s, 2 × the bomb size): the logo with the frozen green time for 2.5 s, then the logo's Bier glides to the middle, grows and morphs into the cartoon beer (`bier_sticker`), which bobs among sparkles under "YOU EARNED A BEER!". Only with the shipped logo (its first frame is that logo); RECYCLED unchanged. `BombGame.OutcomeImageScale` (outcome animation size in bomb sizes: boom 3, beer 2) drives the player's `DrawOutcomeAnimation` for both; `LogoTimerBox` is now in bomb sizes (no image size needed); GIF frame delays are capped at 5 s (was 1 s). Made by BiBaBu `tools/bibabo/beer_art.py` (`--text` for other wording). Showcase frames DEFUSED +3 s / +6 s show the morph and the beer.

- **Explosion**: on BOOM the bomb seat plays `Content/bibabo-boom.gif` **once** (1.7 s, 3 × the bomb size, over the flash / shockwave / fire): a Klein-rendered cartoon fireball swells and turns to smoke while 22 BiBaBu logo styles (gold Bart, neon Bier, pretzel Busen, … from `bibabu_styles`) burst out spinning. The BOOM outcome carries it as its image (`BombLogo.OutcomeImage`; DEFUSED / RECYCLED keep the logo), player `DrawBoomOnce`; `%APPDATA%\WaBiBaBuSy\bibabo-boom.gif` overrides. Made by BiBaBu `tools/bibabo/boom_art.py`. Verified in the showcase (new exact frame "BOOM t=+1.2s"); the "BOOM!" text covers the fireball's centre.

- **What**: the bomb is now the **BiBaBo logo** — "BiBaBo" (the u of BiBaBu turned into an o), Bart, Bier, and two round bombs where the Busen was, strapped together by a band with a timer box in the middle (white line art in the logo's line weight). Shipped as the default (`WaBiBaBuSy.UI/Content/bibabo-bomb.gif`, 615 × 768, fuse sparks flicker); the bomb seat's live countdown (red, green when defused) is drawn **into the box** instead of above the bomb. `%APPDATA%\WaBiBaBuSy\bibabo-bomb.gif|png` still overrides (no box: timer above, as before).
- **How**: art made in the private BiBaBu repo, `tools/bibabo/bomb_art.py` (vector `Single_Bomb.svg` + `BierBartBomb.svg` next to the BiBaBu SVGs, PNG logos, the GIF, and the box as image fractions). `BombGame.LogoTimerBox` (set by `BombLogo.Resolve` in Core, used by the server and test mode), `BombMath.LogoTimerRect` (shared fit math), player `DrawTimerInLogo`, room preview `PaintBombTimer`.
- **Verified**: build + 1294 tests (`LogoTimerRect`); `TestScenarios/showcase/bibabo.json` on this machine: logo on the bomb seat, digits sit inside the box for countdown and DEFUSED. **Note**: the box is small at the game's bomb size (digits ≈ 25 px on a 1440p seat); the corner timer stays on every seat. Room-preview drawing not checked in the GUI.

## 2026-10-21 (late) — Real pause on fullscreen, per monitor

- **What**: a fullscreen or borderless-fullscreen app now **stops rendering**, not just hides the wallpaper. Before, "Pause on fullscreen" only hid the player windows (`ShowWindow(SW_HIDE)`): the render loop kept drawing and presenting, and LibVLC kept decoding. It also hid every monitor when any one was fullscreen, and looked only at the foreground window (alt-tab to a chat on the other screen brought the wallpaper back behind the game). New Settings option **"Pause only the screen the fullscreen app is on"** (`ClientConfiguration.PauseOnlyFullscreenMonitor`, default off = the whole machine pauses, so a heavy game gets everything). Networking is untouched: a paused seat stays connected, keeps heartbeating and shows online.
- **How**: new player IPC `cmd_pause` (`PlayerCommandPause`, no reply; `Program.Pause.cs`): paused = window hidden, render loop only pumps messages and sleeps 100 ms (no draw, no Present, no z-order upkeep), the video sync timers hold their LibVLC players; resume shows the window and the next sync seeks to the shared clock. `FullscreenDetectionService` now polls `MonitorBusyProbe.FullscreenMonitors` (every visible window; `BusyRule.IsFullscreen` = one window contains the whole monitor) every 2 s and reports the set on every check; `ServerPlaybackHost` re-applies it to the scene players, the remote-driven players and the agenda stand-ins (`BusyRule.IsPausedMonitor`), `D2DPlayerHost.SendPauseAsync` sends only changes. A player started mid-game is paused on the next check (≤ 2 s).
- **Verified**: build + 1293 tests (full-cover rule, whole-machine vs per-screen). Standalone player (`--test`): 4–6 % CPU rendering → **0.3 %** paused → back on resume; window hidden while paused, visible again after. **Not verified**: a real game on a client seat, per-screen mode on a two-monitor seat, video layers resuming in sync after a long pause.

## 2026-10-21 (evening) — BiBaBo variation: 7 riddle kinds, 3 modes, rule card, junction box

- **What**: every game rolls its own variant (design §14). **Riddles** now each yield a digit — Math (any digit), number **Sequence** (`96, 48, 24, 12, 6, ?`, arithmetic, growing differences, interleaved), **Tangle** (digit exits), **Labyrinth** (one reachable exit), **Logic gates** (AND/OR/XOR/NAND/NOR, lamps 4 2 1), **Mirrored clock** (the real hour), **Dots** (count one of four similar reds). **Modes**: Classic 50 % (one live cable; rule rolled: `result = k`, odd, even, prime, LARGEST, SMALLEST), Cut order 25 % (two live cables, cut smallest or largest result first; progress shows on every seat), Keypad 25 % (code = three results in a stated colour order, typed on a keypad below the bomb, digits show on every seat, C clears). **RULES card** on a spare machine in half the games ("RULES: somewhere in the room" at the bomb); **junction box** in 40 % on the bomb's neighbour seat — the cables passing it come out in each other's colours (visible wires inside), rules always mean the colours at the bomb.
- **How**: `BombRiddles.Make(kind, value)`; `BombGameFactory` rolls mode / rule / kinds / junction / rule card and returns a server-only `BombSolution`; new `ShowEventType.BombStep` for progress (the log keeps only the newest step per game); referee handles `cut` / `key` / `recycle` per mode; keypad geometry `BombMath.KeyRect` (relative to the bomb's on-screen centre, reads 1 2 3 on mirrored seats); `BombHit.KeyAt`; player draws all kinds (`Program.Bomb.cs`); preview names the riddle kinds.
- **Verified**: build + 1288 tests (every riddle kind × every value × 30 seeds against independent solvers; 300 seeds per mode for "exactly the intended live cables"; referee steps; keypad hits incl. mirrored). Showcase frames on this machine show all seven kinds, the keypad and the rules. **Not visually verified**: junction box and RULES card (need ≥ 4 seats), cut-order progress, typed digits.

## 2026-10-21 — BiBaBo bomb-defuse mini-game; player stdin is UTF-8

- **What**: *💣 BiBaBo* in the Events strip (target = the strip's target) or **Ctrl+Alt+B** (everyone; again = abort). Every free seat fades to black, a roulette picks a random free machine, the bomb (Patrick's logo from `%APPDATA%\WaBiBaBuSy\bibabo-bomb.gif|png`, else a drawn bomb) drops in with a red 7-segment 5:00 timer and the rules, three cables grow along the seat chain (across bezels, the short way round on a Ring) to three other machines, where riddle cards appear: **Math** (`(17·3 − 4²)⁽⁹⁻⁹⁾`, `0! · (57 − 56)⁷`, `(−1)²⁰²⁶`, `101² − 100² − 200`, … — LIVE ⇔ result = 1) or **Tangle** (three crossing strands, LIVE ⇔ the cable's strand reaches the DETONATOR). Every seat shows a small corner timer. The bomb machine's user clicks the live cable's terminal / stub on the desktop → **DEFUSED — m:ss left** (grey cables, green timer, confetti); a dead cable or 0:00 → **BOOM!** (shake, white flash, orange shockwave rolling through the room, fire particles); dragging the Recycle Bin onto the bomb → **RECYCLED ♻**. One PC with several monitors plays on the monitors.
- **How**: show events `Bomb` / `BombOutcome` carrying a `BombGame` built once on the server by the seeded `BombGameFactory` (seats, roulette, routes, cards, riddles); the live cable stays in `BombGameService` (Core), which referees the new `ReportShowAction` RPC (first valid action from the bomb machine wins, 300 ms grace after the deadline, timeout, abort on Stop / Clear all). Players draw from `BombMath` (`Program.Bomb.cs`, text as per-slot `TextSprite`s "id#slot"), the room preview from the same math (`ScenePainter.Bomb.cs`). Input on the bomb machine: `RawMouseSink` (Raw Input `RIDEV_INPUTSINK`, desktop clicks only) + `RecycleBinLocator` (`IShellWindows` → `IFolderView`, parsing name `::{645FF040-…}`), hit-tested by `BombHit`. Busy seats: `MonitorBusyProbe` (`BusyRule`, ≥ 80 % window coverage) in the heartbeat (`busy_monitor_indexes`). The event log drops a Bomb with its outcome (`ShowEventLog`), so a replay never brings a decided bomb back. Other events and playlist interrupts wait while a game runs; the agenda hides under it.
- **Fix found on the way**: the player's stdin was read in the console code page, so any non-ASCII event or agenda text (·, superscripts, ü, ♻) arrived garbled — host and player now use UTF-8 (`D2DPlayerHost`, `Program.ProcessCommands`).
- **Verified**: build + 1859 tests (Bomb factory / riddles with a test-side formula evaluator over 200 seeds, math, referee, hit-testing, busy rule, event-log lifetime). `TestScenarios/showcase/bibabo.json` on this machine's two monitors: frames show roulette, bomb + cables, cards with readable formulas and tangles, LED timers, BOOM and DEFUSED. Recycle Bin lookup verified on Windows 11 (found at the top-left). **Not verified**: clicking a cable on a real desktop (needs a person), the Recycle Bin drag, the game across machines.
- **Design / plan**: `.docs/plans/2026-10-21-bibabo-bomb-game-design.md`, `-plan.md`.

## 2026-10-21 — Agenda overlay (LAN-day timetable on every screen)

- **What**: an **Agenda** tab (right panel, next to Playlist) and a tray toggle "Agenda on all screens". The timetable is typed as lines (`Fri 18:00 Pizza`, `20:00 CS2`, `Sat 10:00 Breakfast`, German day names too; a line without a day stays on the previous day, past midnight rolls over), plus title, notes (WiFi, house rules), first LAN day and corner. Every connected seat shows a corner panel (≤ 30 % of the screen height): the last finished item dimmed, the current one highlighted with "now", the next one with a countdown ("in 23 min", pulsing 10 min before), day headers for Fri–Sun. Seats tagged `agenda` show the full-size timetable (one column per day). The room view paints the same panel on every tile. Edits go out ~1 s after typing stops.
- **How**: its own room-wide transport, not a show event (`CommandType.AGENDA = 14`, `SyncCommand.agenda_json`; `AgendaCodec` shifts item times by the client's clock offset). The server sends it on change and to every client when it registers; clients keep it (an explicit Disconnect hides it). Each host forwards it to its players (`cmd_agenda`) and runs a stand-in player (the seat's own Windows wallpaper, no layers) on every monitor without a scene (`AgendaDisplay`, `AgendaStandIns`), so Stop / Clear all / idle keep the panel. Players draw it after the show events in device pixels from `TextSprite` bitmaps, rows rebuilt once a second (`Program.Agenda.cs`); hidden under a Blackout. Pure math in `WaBiBaBuSy.Models/Show` (`AgendaMath`, `AgendaLayout`, `AgendaText`); persisted in `%APPDATA%\WaBiBaBuSygenda.json`.
- **Verified**: build + 1206 tests (34 agenda tests). Single-machine test-mode run (`--test-run … --exit`) with an enabled agenda: the captured player frame shows the panel (title, dimmed past item, "now" highlight, "in 7 min", Sat header, notes), stand-ins started at launch, replaced by the scene, back after Stop, gone at exit. Not checked: a client seat, an `agenda`-tagged full-size screen, the Agenda tab and tray toggle by hand.
- **Design / plan**: `.docs/plans/2026-10-21-agenda-overlay-design.md`, `-plan.md`. Also planned the same day: BiBaBo bomb game (`2026-10-21-bibabo-bomb-game-design.md`).

## 2026-10-20 — Panorama: tallest screen per row; preset fixes; coherent rooms

- **Panorama on mixed heights**: the image now fills the tallest screen of each row (`NodeLayout.RowHeight`, set by `SeatMapLayoutBuilder` for Path and Parallel), shorter screens show the vertically centred part. Before, it was stretched over the whole canvas height: on a Parallel room every row got only its own horizontal band of the picture. Older servers (RowHeight 0) keep the old mapping. Test `PanoramaSource_FillsTheRowsTallestScreen_ShorterScreensCropCentred`.
- **Add layers from preset on an empty draft** loads the whole preset (background included): adding a Panorama preset to the default scene kept the empty placeholder layer and the IconZone background, so the panorama never showed. A draft with sprites still keeps its background; the status line says so.
- **Content (BiBaBu repo)**: the "2× logo on the first spot" in the Panorama presets and the scattered Bier parade came from `MultiImageSpread` (a ±px scatter in X and Y) used as a spacing; parades are now one-row pattern grids. BiBaBu Swarm: Busen orbit and the lone comet replaced by a neon Bier/Bart/Busen convoy with Portals, Logo drift and Bier parade sparser, 16 new logo styles (108 sprites). All six panoramas re-rendered with `panorama.py --whole` (one coherent landscape for the whole ring, guide §11 in the BiBaBu repo) and a BiBaBu hint on every screen.
- **Verified**: 65 NodeMapping / SeatMap / Panorama tests; UI build. Not checked in the GUI: the room preview / players with a mixed-height row, the new presets on the wall.

## 2026-10-19 — Usability: full-fidelity preview, events without a scene, layer presets

- **Room preview**: pattern layers draw the real images per cell (source pick + rotation as on the players) instead of grey boxes, and multi-image layers draw every image at its seeded spread offset. The spread math moved to `PatternLayout.MultiImageOffset`, which the player now calls too. Sprites are cached downscaled to 256 px with their native size (`ScenePainter.Sprite`); the cache now holds 256 images instead of 32 (BiBaBu Swarm has 80).
- **Event stand-in**: with nothing playing, the stand-in scene's background is each seat's own Windows wallpaper (`BackgroundLayerConfig.UseLocalDesktopWallpaper`; the player reads `SPI_GETDESKWALLPAPER` and crops it to fill, uses the desktop colour without a picture and falls back to the black image). A seat now looks unchanged until the event reaches it. Ceiling: one wallpaper per machine (per-monitor wallpapers would need `IDesktopWallpaper`).
- **Events strip**: the Spotlight seat combobox is gone. Spotlight aims at the node selected in the room (the button reads "★ Spotlight #2") and is disabled with no selection. Before, the picker was filled only from a running layout, so Spotlight without a scene always failed. Traversal is disabled when there is no picked image and no picture sprite in the running scene.
- **Presets**: "Save selected layer as layer preset" writes to `presets\layers\` (`PresetStore.Layers`). The Layers ▾ menu has *Add layer preset* and *Add layers from scene preset*, and layer presets no longer appear in the scene Presets ▾ list. Layer presets saved earlier stay in `presets\` (move them by hand). The *Showcase Tour (all scenes)* playlist preset was dropped; the showcase scenes stay as the starter presets.
- **Verified**: build + 1171 tests (new: `LayerPresets_LiveInTheirOwnFolder_NotInTheSceneList`). Not checked in the GUI: preview of BiBaBu Swarm, events over the desktop wallpaper, Spotlight button label.

## 2026-10-19 — Playlist rows: thumbnails, colored chips, mode icons

- **What**: playlist rows drop the "Details" expander. Each row shows a 16:9 thumbnail on the scene's background color (green frame while playing), a full-width name box and colored chips: source file blue, motion + speed amber, distribution violet, background / grid / grading / effects teal, transition override + lap-snap pink (hover a chip for the full detail line). The transition picker and Lap-snap appear on the selected row only. Thumbnails now load for every item (gallery thumbnail, else a video frame, else the file decoded at 192 px), not only for gallery files. The Scene editor's Pattern, Distribution and Background pickers show the same icons and colors.
- **Gallery buttons**: *Get content ▾* and *+ Add* moved from over the gallery cards into the right panel's tab header row (Scene tab only); the panel's collapse button is now a small › handle on the splitter (`MainWindow.axaml`).
- **Code**: `ViewModels/SceneGlyphs.cs` (icons + category brushes, `SceneChip`), `PlaylistItemRow.Chips / ThumbnailSource / SceneBackground`, `PlaylistViewModel.ThumbnailProvider` now async, `MainWindowViewModel.PlaylistThumbnailAsync`.
- **Verified**: screenshots of the Playlist tab (selected row on a dark blue highlight so the chips stay readable) and the Scene editor Motion / Background tabs on the showcase playlist. Not checked: a video-only item thumbnail, a running show (green frame).

## 2026-10-19 — Party content moved to a private repo

- **What**: the ComfyUI content scripts (`tools/comfyui/`), the Panorama content guide and `tools/get-cat-content.ps1` moved to the private repo `Wubbl/BiBaBu`, with their history. The app does not use them; the showcase logo and cat GIF stay here for the starter presets and tests. The entry below describes that work; its paths now refer to the content repo.

## 2026-10-18 — Panorama content: clean seams, native resolution, LAN party, logo styles

- **What**: new ComfyUI scripts (FLUX.2 Klein 9B through the ComfyUI API) replace the one-click workflow for finished wallpapers. Panoramas are rendered at **1920 × 1088 per screen** (no upscale, no 16,384 px limit) and the hard vertical lines at the repainted seam bands are gone: the band is blended with a per-row colour correction over a 96 px ramp instead of a 24 px blur with a per-column gain. New concept **LAN Party** (pixel art gym hall full of gamers, BiBaBu banner, Bart banner, Bier and Busen neons); Pixel Quest, Neon Harbour, Mountains and Fantasy Realm re-done with denser beats and more BiBaBu. **Logo styles**: the Bier, Bart and Busen line drawings redrawn in 20 styles each (gold, chrome, pixel, LEGO, stained glass, neon, origami, gingerbread, …) as transparent sprites; **BiBaBu Swarm** preset (all styles drifting in a grid, a Bier parade, bouncing Barts, orbiting Busen, a neon Bier comet with portals) and one *Panorama – …* preset per wallpaper.
- **Code**: `tools/comfyui/panorama.py` (+ `panorama_concepts.py`; `--coarse N` coarse-to-fine mode, untested), `logo_styles.py` (flood-fill background keying, glow alpha for neon), `swarm_preset.py` (copies sprites / rooms into the preset assets, writes the presets), `comfy_api.py` (shared API client). Guide §9–§10.
- **Verified**: all five wallpapers rendered at 19,200 × 1,080 and every join checked by eye (failed seams re-rolled: Mountains 2 / 8, Neon Harbour 5 / 7, Fantasy 4 / 7, Pixel Quest panel 3); 60 sprites; six presets load through `SceneFile.Load` with no `SceneChecks` warnings; coarse-to-fine compared on Mountains (guide §9). Later the same day: **Cat World** (pixel art, a world run by cats above a cut-away underground; seams told to slope the surface between differing heights) and a second coarse-to-fine round (guide §9). Pending: look at them on the real room; Fantasy join 7 still shows a misty vertical edge (re-roll with `--seed <n> --reseed s7`); Cat World keeps a few platform-like surface steps.

## 2026-10-18 — Ghost seats: a machine that leaves keeps its seat

- **What**: a client that disconnects (closed, asleep, network gone) stays in the room as a greyed **ghost seat** ("offline 12 min") with its old place, size and gap, so Play, playlist items and event stand-in scenes no longer shrink the ring or shift the other seats. Nothing is sent to a ghost; its scene command is parked, so when the machine comes back mid-scene the session resume puts it exactly where the sprite is. Right-click a ghost → **Forget seat** (the ring closes). Seats not seen for 14 days are not loaded (old parties).
- **Code**: `TopologyEntry.LastWidthPx/LastHeightPx/LastPixelsPerCm/LastSeenUtc`, `TopologyStore.RecordMonitors` (on register; multi-monitor clients per `{id}_MONITOR_n` entry, created at the chain end when missing) / `MarkSeen` (on disconnect) / `Remove`; `Resolve` moves monitor entries along on a hostname re-bind. `WallpaperSyncService.GetSeats` / `ForgetSeat`; ghost order edits persist. `RoomNodeSources.ServerNodes(..., seats, now)` appends ghosts (`ClientDisconnected`) → `RoomNode.IsGhost`; `CrossScreenPlanner` keeps ghosts in the layout, `plan.Ghosts` instead of `plan.Remote`; `StartCrossScreenD2DOnClientAsync(parkOnly)`. UI: `ClientNodeViewModel.IsGhost/OfflineSinceUtc`, tile at 45 % opacity, `IRoomHost.ForgetSeatAsync`.
- **Verified**: build 0 errors; 1146 unit tests green (+3, `GhostSeatTests`: node layouts of all seats identical with two machines — one dual-monitor — connected vs offline; 14-day cutoff; forget). Pending: GUI check (greyed tile, Forget seat), multi-machine drop / rejoin. Not done from §1: *Mark empty seat…*, *Forget offline seats* button, health-strip ghost count, "n of m targets online" event status, 14 days as a setting.

## 2026-10-18 — Video on upper layers

- **What**: any layer can now be a video, not just the bottom one — at most 2 videos per scene (each decodes on every seat; the editor warns above that and players skip the extra ones). E.g. a still or GIF background scene with a video clip floating on top, or two videos stacked with opacity.
- **Code**: player `LayerVideo` (Program.LayerVideo.cs): own LibVLC + media player decoding into a double buffer, a persistent D2D bitmap updated by `CurrentFrame()`, a 500 ms sync timer holding it to the layer's own clock (`SyncTiming.LayerElapsed`, same seek / rate rules as layer 0); stored on `LayerRuntime.Video`, created by `LoadActiveLayerSprite` (now returns it), drawn by `DrawUpperLayers` via `DrawAnimationLayer(videoBitmapOverride)`, disposed by `DisposeUpperLayers`. Layer 0's video branch is unchanged. `SceneChecks.MaxVideoLayers` replaces the "only the bottom layer can play a video" warning.
- **Verified**: build 0 errors; 1170 unit tests green (check test rewritten); live probes on the server's second monitor with a 4 s 30 fps test clip on layer 2 over a logo: frame 29 at 1 s, 88 at 3 s, 29 again at 5 s (looped), layer opacity applied. Pending: two videos at once on a weaker seat (CPU/GPU), multi-machine parity of a video layer.

## 2026-10-18 — Layer follow-ups: layers from presets, Suggest "+ as layer", spawn windows

- **What**: Layers strip **▾** → *Add layers from preset* (a preset's layers go on top of the stack, its background is ignored; at most 6 layers) and *Save selected layer as preset* (the layer alone, default background, a new preset named after it). Suggest strip **＋ as layer**: closes with the scene from before Suggest plus the clicked card's sprite layer(s) on top (a fresh draft's empty layer is dropped). Motion tab **Appears every … s, for … s**: a spawn window — the layer shows up once per cycle of its own clock, stays for the given time and fades 250 ms in and out (0 = always shown); its motion keeps running while hidden.
- **Code**: `CrossScreenConfigViewModel.AppendLayers` / `SelectedLayerSnapshot`; `MainWindowViewModel.LayerPresets`, `AddPresetLayersAsync`, `SaveLayerAsPresetCommand`; `SceneSuggester.AddAsLayers` (pure) + `SuggestionsViewModel.KeepAsLayerCommand`; `SceneLayer.ShowEveryMs` / `ShowForMs` + `LayerSchedule.Visibility` (pure, on `SyncTiming.LayerElapsed`), player `DrawAnimationLayer` wraps the old body (`…Core`) with the window alpha (new per-layer statics in `LayerRuntime` + `SaveInto`/`LoadFrom`), `ScenePainter.PaintLayers` multiplies the same value.
- **Verified**: build 0 errors; 1170 unit tests green (+4, `LayerFollowUpTests`); exact frames of a 2-layer scene (cat every 4 s for 1.5 s): cat at 0.8 s, gone at 2.5 s, back at 4.8 s. Pending: GUI click-through of the ▾ menu, ＋ as layer and the two Motion fields. Open from plan §4: video on upper layers (M), debug overlay for upper layers.

## 2026-10-18 — Portal bezel effect

- **What**: Effects tab → *On entry* / *On exit* → **Portal**: a door (translucent ellipse with a bright rim in the bezel color) opens at the screen edge in the 400 ms before the sprite reaches it, stays open while the sprite passes through, and closes over 400 ms after. The seat it leaves and the seat it enters open their doors on the same frame; at a row turn or the Ring seam it reads as "it teleported to the other table". *On entry* = doors where the sprite comes in, *On exit* = where it leaves.
- **Code**: `BezelEffect.Portal`; `SpriteEffects.FindPortal` scans the pose on the shared 16 ms grid ±`PortalLeadMs` around now — ahead as well as back, since the position is a pure function of time, so no analytic crossing time was needed (plan §3.1) — and eases the door open by the distance to the nearest straddle sample; entering vs leaving from the visible overlap one sample later. `FindBezelEvent` skips Portal. Drawn by the player (`DrawPortal`, Program.Effects.cs) and the room preview (`ScenePainter`), same math.
- **Verified**: build 0 errors; 1166 unit tests green (+4, `PortalTests`: both seats same openness on the same frame, shut 500 ms ahead, open while passing, entry-only / exit-only); exact frames on the server's two monitors (logo at 1200 px/s): doors only at the screen edges at the crossing times, logo passing through the rim. Pending: real seats, mirrored rows by eye.

## 2026-10-18 — Lap-snap for every movement type

- **What**: an item's *Lap-snap* now rounds its duration up to whole periods for Linear, **SineWave** (horizontal lap), **Bounce** (horizontal round trip — vertical when it only moves up and down) and **Circular** (one orbit), so the switch (and its transition) comes when the sprite is back where it started. Static, Random walk and the IconZone path still switch on time. Linear laps are also exact now: the sprite width was never passed in (`ContentWidthPx = 0`), so every Linear lap was one sprite width short.
- **Code**: `PlaylistScheduler.ComputeLapMs(MovementConfig, LapGeometry, BackgroundMode)` (`LapGeometry` = canvas, sprite as drawn, speed after cm→px, Ring) mirrors `MovementCalculator` per type; the old overloads wrap it. `ServerPlaybackHost.ComputeEffectiveAnimationSize` (was `…Height`) returns width too; `CrossScreenApplyResult` / `ApplyMetrics` carry sprite and canvas size. The room preview's "lap ≈" line now shows for the new types as well.
- **Verified**: build 0 errors; 1162 unit tests green (+5): each period is checked against `MovementCalculator` positions at t and t + period (three start times, < 1.5 px). Pending: a show with Lap-snap on Bounce / Circular items on the real wall.

## 2026-10-18 — Playlist transitions: Fade (through black) and ring Wipe

- **What**: Playlist tab → *Transition* (Cut / Fade / Wipe + seconds) next to the default duration; each item can override it under Details → *Comes in with*. **Fade**: every seat darkens the old item to black over the half before the switch and brightens the new one over the half after. **Wipe**: a boundary runs once along the seat-map path (around the Ring) in the given time; behind it the new item shows, ahead of it the old one — the change visibly travels from seat to seat.
- **Why this way**: old and new item are separate player processes (standby switch). Measured on the real desktop: window alpha (`SetLayeredWindowAttributes`) is ignored for the flip-model swap chain, so a true crossfade would need both scenes in one player (big refactor, not done); window regions (`SetWindowRgn`) are honored, which is what the wipe uses.
- **Code**: `SceneTransition` / `TransitionKind` / `TransitionMath` (Models, pure); `PlaylistItem.Transition`, `Playlist.DefaultTransition`; `PlaylistOrchestrator.CurrentTransition` (item, else playlist default) → `ServerPlaybackHost.ApplyCrossScreenConfigAsync(config, transition)`; remotes get it in the so far unused `SyncParameters.transition_effect` ("Fade:1500", "" = Cut; an older client ignores it = Cut). Player IPC `cmd_transition` (in = before the new player's start, out = to the player being replaced); `Program.Transitions.cs`: black veil (`DrawTransitionVeil`, also on the first visible frame) and the wipe region (`UpdateWipeRegion`, render/window thread, follows `_sequentialLayout` incl. mirrored rows). Old players stay until the wipe ends (`OutgoingHoldMs`). Test mode: `playScene` `"transition"`.
- **Verified**: build 0 errors; 1157 unit tests green (+11, `SceneTransitionTests`); live on the server's second monitor, sampling desktop pixels every 80 ms: Fade 2 s = red 255→0 over 1 s then blue 0→255 over 1 s; Wipe 3 s = left edge blue first, right edge 2.6 s later. Pending: GUI check of the pickers, Wipe across several seats and mirrored rows on real machines.

## 2026-10-18 — Standby switch: no blank gap between playlist items

- **What**: a playlist item switch (and a scene applied over a running one) no longer blanks the wall. Every seat keeps showing the old item while the new one loads; the new player stays hidden until the shared start, appears there on top, and the old player stops 250 ms later. Before: the old player was stopped first, so each seat showed the desktop wallpaper for load time + start lead (~1–4 s).
- **Code**: player shows its window only once `NowUtc ≥` the shared start (`ReachedSharedStart`, Program.cs); `RetiringPlayers` (UI/Services) keeps the replaced `D2DCompositionService`s and stops them at start + 250 ms (at once on a failed apply, Stop, Clear all, Exit); used by `ServerPlaybackHost.ApplyCrossScreenConfigAsync` and `RemotePlaybackHost.ApplyCrossScreenCoreAsync` (which also drops its old 300 ms dispose pause). No protocol or orchestrator change: the start lead every switch already has is the standby window. Test mode: `playScene` `"switch": true` (`ITestHost.SwitchSceneAsync`), scenario `standby-switch.json`.
- **Verified**: build 0 errors; 1146 unit tests green; on 2 local monitors a watcher sampling visible player windows every 20 ms: standby switch never below 2 visible (4 for ~0.3 s during the handover), the old stop-then-start path showed 0 for 1.8 s; `standby-switch.json` (local copy, `minRemoteNodes: 0`) pass, probes 150 ms after each switch spread 12 ms. Pending: multi-machine run, GPU memory with two big GIF scenes resident during the handover.

## 2026-10-17 — Targeted events, tags, events without a scene, empty seat at a row start

- **What**: Events strip **To:** picker — Everyone, *Tag: X* or one seat; every event button fires for that target only. **Tags**: right-click a node (or a selection) → *Tags…* (comma-separated, blank clears); tiles show them as a green chip; stored in `%APPDATA%\WaBiBaBuSy\tags.json`. **No scene needed**: an event fired while nothing plays starts a black stand-in scene on the target seats, and stops it once the last event of it has ended (Play replaces it at once). Identify labels are now numbered by the server like the tile badge (`#N` + host name). Right-click a node → *Gap before this seat (cm)…*: on the first seat of a row (after a turn, or seat 0 of a Ring) the gap is now honored — an empty seat at the row start; seat 0 of an open path and Parallel row starts still ignore it.
- **Code**: `ShowEvent.TargetNodeIds` / `Labels` / `IsFor` — the player (`DrawShowEvents`, sprite decode) and `ScenePainter` skip events not for their node, so resume / Resync replays stay correct; the per-monitor layout of Simultaneous layers now carries the node id. `NodeTagStore`, `EventTarget` (Models). `ServerPlaybackHost.FireEventAsync(type, seat, targetNodeIds)`, `StartIdleSceneAsync` / `KeepIdleSceneUntil` (stand-in = StretchedImage of a 64×36 black PNG, `IdleImage`, so remotes receive it; never `LastScene`; `StopAsync` stops the remotes of the scene actually playing). `CanFireEvents` = no start in flight. `SeatMapLayoutBuilder.BuildPath` row-start gap. Hotkeys and playlist interrupts still fire for everyone.
- **Verified**: build 0 errors; 1143 unit tests green (+6, `EventTargetingTests`); test-mode run on 2 local monitors: background-only stand-in scene (no layers) plays, an Identify targeted at monitor 0 with a server label shows "7  LABEL-FROM-SERVER" there and nothing on monitor 1. Pending: GUI click-through (To: picker, Tags… editor, tag chip, gap menu, stand-in start/stop from the buttons), multi-machine run.

## 2026-10-17 — Identify event + installer firewall rules

- **What**: Events strip **# Identify**: every seat dims and shows its seat number (traversal order + 1, as in the room view) and machine name for 10 s, for sorting the room at the party. Like every event it needs a running scene. Installer (admin install) adds Private-profile firewall rules for TCP 50051 (gRPC) and UDP 5353 (mDNS), removed on uninstall; fallback version 2.7.0.
- **Code**: `ShowEventType.Identify` (appended — older clients ignore it), `ShowEventSettings.IdentifyDurationMs`; player renders its own label with `TextSprite` on receipt and draws it above Blackout (`DrawIdentify`); not drawn in the room preview (tiles already carry the names). No hotkey.
- **Verified**: build 0 errors; 1137 unit tests green (+1); `showcase/events.json` (new identify step) exit 0 on 2 local monitors, exact frames show "2 SEEPC" / "1 SEEPC". Installer compiles. Pending: multi-machine run, admin install check of the rules.

## 2026-10-14 — Panorama background, branch `feat/scene-layers`

- **What**: background mode **Panorama (spans all screens)**: one wide picture is stretched over the whole seat-map canvas and every player shows its own slice, so it continues across bezel gaps (and around a Ring if its right edge continues into its left). The Background tab shows the size that covers this room 1:1 ("Sharpest at 19,200 × 1,080 px"); the room preview paints each node's slice. ✨ Suggest / Auto from folder detect panoramas (opaque still, ≥ 3:1, ≥ 1.5 screens wide) and offer the **Panorama** recipe: background only, or with the other picked files riding across it (Linear, Sequential). A panorama-only scene is now playable and reaches remotes (the background image travels as the primary content).
- **Code**: `BackgroundMode.Panorama`, `BackgroundLayerConfig.UsesImage`, `NodeMapping.PanoramaSource` (shared by player and `ScenePainter`); player `CropPanorama` cuts the slice with GDI+ before upload (a 10-screen image exceeds D2D's 16384 px bitmap limit), mirrored nodes flip it; `SceneAssets.RewriteToLocal` never hands the primary to an empty layer 0; `MediaAnalyzer.IsPanorama`, `SelectionProfile` treats a panorama as the big file, `Showcase` no longer takes panoramas.
- **Verified**: build 0 errors; 1136 unit tests green (+7); local test-mode run on 2 monitors with an 8000 px test image: each player loaded a 1781 × 1080 slice and the exact frames show x 0–1781 and 6219–8000 with the marker on top. Pending: GUI check of the editor hint + preview, multi-machine run.

## 2026-10-14 — Scene layers (roadmap 2.3, slice 1), branch `feat/scene-layers` — v2.7.0

- **What**: a scene is a background plus N image/GIF layers drawn bottom to top, each with its own file(s), movement, color grading, effects, opacity, Sequential/Simultaneous distribution and phase offset. Scene editor: Layers strip above the tabs (select, enable, rename via right-click, + / ⧉ / ✕ / ◀ ▶); Content / Motion / Look / Effects edit the selected layer; Opacity in Look, Phase offset + per-layer Distribution in Motion. The room preview paints every layer. New showcase scene `16-layers-party.json` (in `showcase.json` and `parity-matrix.json`).
- **Code**: `SceneLayer`, `CrossScreenConfig.Layers` (full replacement of `Animation` / `Movement` / `DistributionMode` / `AnimationSpeedPxPerSecond`; legacy JSON migrates on read, `LegacySceneMigration`); player `Program.Layers.cs` (`LayerRuntime` swap of the per-sprite statics, layer 0 = resting state, upper layers: no video, no IconZone/corridor); `SyncTiming.LayerElapsed` shared by players and `ScenePainter`; `PhysicalUnits.ResolveLayer/ResolveScene`; `SceneAssets` per layer (missing layer file → that layer disabled); `SceneChecks` per-layer warnings + limits (6 layers, 2 pattern layers, video only at the bottom); proto `scene_json = 38` replaces the loose canvas / movement / per-part JSON fields (reserved); clients < 2.7.0 get no scene and the room flags them "needs update". The IconZone global path is now attached to the layer config itself (the old field-by-field clone dropped `Effects` / `FaceTravelDirection` on remotes). Repo scene files rewritten to the `Layers` shape.
- **Verified**: build 0 errors; 1126 unit tests green (+19 incl. every repo scene file loading); local test-mode run (`--test-run`, 2 monitors) of the 4-layer scene: all layers load on both players, exact frames show rain / bubbles 60 % / cat / sponsor 50 % in order; legacy-converted single-layer scene unchanged. Pending: GUI click-through of the Layers strip, multi-machine `parity-matrix`. Spec `.docs/plans/2026-09-17-scene-layers-design.md` (revised), plan `.docs/plans/2026-10-14-scene-layers-plan.md`.

## 2026-10-14 — Auto from folder and "Get content", branch `feat/folder-playlist`

- **What**: Playlist tab ＋ Add ▾ → **Auto from folder…** reads a folder's pictures, GIFs and videos (max 200, by name), adds them to the gallery, profiles them like ✨ Suggest and appends a varied party playlist: full-screen files and videos as Showcases (video dwell = its length, max 2 min), look-alike transparent sprites in groups of 3–5, everything else solo; rhythm group / solo / group / showcase, no recipe twice in a row, GIF dwell on whole loops. A status line shows progress and the result. The gallery gets **Get content ▾**: free GIF / sprite sites, AI generators and clean-up tools, opened in the browser.
- **Code**: pure `PlaylistBuilder` (Models/Wallpaper/Suggest) and `ContentLinks` (Models/Content); `SceneSuggester.Ranked` / `BuildCard`; UI `ProfileFilesAsync` shared by Suggest and the folder flow (reads GIF loops / video lengths the gallery has not measured yet).
- **Verified**: build 0 errors; 1084 unit tests green (+30); real decode + build on the showcase assets and a 10-file folder from a console. GUI click-through pending. Spec `docs/superpowers/specs/2026-10-14-folder-playlist-and-content-links-design.md`.

## 2026-10-14 — ✨ Suggest: scenes built for the picked media, branch `feat/auto-suggest`

- **What**: a `✨ Suggest` button in the Scene editor's Content tab. It profiles the picked files (size vs the primary monitor, aspect, transparency, mono vs colourful, dominant colours, brightness, GIF loop length) and shows four cards from nine recipes modelled on the starter presets (DVD Bounce, Logo Conveyor, Marquee, Orbit, Pulse Grid, Fireflies, Comet, Parade, Showcase). Clicking a card makes it the draft (room preview shows it), 🎲 Shuffle offers others, Keep / ✕ close. Mono logos get tinted, colourful ones keep their colours; cycles snap to whole GIF loops; a full-screen file picked with small ones gets a Showcase card and the note "needs scene layers".
- **Code**: pure `WaBiBaBuSy.Models/Wallpaper/Suggest/` (`MediaAnalyzer`, `MediaProfile`, `SelectionProfile`, `SuggestKit`, `SceneRecipes`, `SceneSuggester`); UI `MediaProbe` (Magick.NET first-frame decode), `SuggestionsViewModel`, strip markup in `SceneEditorPanel.axaml`. Deterministic: recipes use `PatternLayout.Hash3` seeds only; every offered scene passes `SceneChecks.Validate` with no warnings.
- **Verified**: build 0 errors; 1054 unit tests green (+93); real decode of the showcase assets checked from a console. GUI click-through pending (plan Task 5 step 7). Spec `docs/superpowers/specs/2026-10-14-auto-suggest-design.md`, plan `docs/superpowers/plans/2026-10-14-auto-suggest.md`.


## 2026-10-13/14 — Full code review (8 parts) and fixes, branch `fix/review-batch-a`

- **Review**: whole codebase in 8 parts, ranked list in `.docs/plans/2026-10-13-full-code-review.md` (batches A–F, statuses per item, coverage gaps). Trust model decided: trusted LAN (no auth / package signing).
- **A — test results**: `--test-run --exit` now really returns 0/1/2 (`Main` dropped it); exit 1 also for lost nodes, unanswered probes and runs that measured nothing; drift grades render-time-fallback nodes; dead local players count as missing; lost captures fail the probe; scenario JSON rejects unknown fields; `clock-skew.json` needs one client. Video follows the shared clock (seek > 200 ms, ±10 % rate below, LibVLC-internal loop). Bad grading hex no longer throws every frame.
- **B — security (trusted LAN)**: client download paths can no longer leave the cache (`{cache}\{hash16}\{name}`, hash required, temp + verify + move); unused `TransferContent` upload removed; imports carry only media; probe uploads only during a test run.
- **C — races**: newest LOAD/STOP wins on the client; server/client players serialized and disposed on failure; Stop / Clear all / Exit wait for a start in flight; item switches stop uncovered seats; playlist Stop vs new Start; bounded command writes + parallel sends + HTTP/2 keep-alive; sweep compare-and-remove; Disconnect during reconnect; single instance.
- **D — player**: frame holds the composition lock (no use-after-free on scene switch); crashed players respawn in step (verified); device loss → exit → respawn; IPC replies paired with stale-reply skipping and timeouts; no popup without WorkerW; icon reads with timeout.
- **E — correctness**: auto-update could never start (trailing `\` in a quoted path) — fixed; rollback restores every overwritten file; atomic settings / state writes; cache eviction keeps index + recent files; Endless pattern jitter; Ring lap-snap; stable image count on a missing extra image; editor keeps explicit start / end / orbit points; preview matches players for Traveling + pattern + Linear; duplicate hostnames keep their seats; stores honor the test path redirect; tray Connect uses the saved server; log retention 14 days.
- **F — dead code**: ~4,700 lines removed (`Player.Image`, `D2DTest`, in-process DXGI renderer, second IPC stack, Phase-3 animation stack and RPCs, unused models).
- **Verified**: build 0 errors; 961 unit tests green (+94); `smoke-local` passes on the server alone; killing a player mid-run respawns it in step. Multi-machine run still pending — **use a build from this branch** (older builds always exit 0).


## 2026-10-10 — Test mode spec 2a: fault injection (+ two session-recovery fixes)

- **What**: scenario step `fault` — `drop` (the client tears its link down as if the cable came out and cannot reach the server for `ms`, then reconnects through its real backoff and session resume) and `silent` (no heartbeats, sync responses or thumbnails for `ms` while the connection stays open: a powered-off client as the server sees it). The runner watches the server's client registry and reports the reconnect time after the link is back (limit 5 s) or the time until the dead-client sweep dropped the node (limit 45 s), per step and in a **Faults** table in `report.html`. New `TestScenarios/recovery.json` (~3 min). Transport: `CommandType.TEST_FAULT = 13`, `SyncParameters.test_fault_kind = 36`, `test_fault_ms = 37` (**proto change**; old clients ignore it). Gated by test mode on the server and "Answer automated test runs" on the client; the client clamps faults to 1–120 s.
- **Fix — swept client stayed invisible**: a client the sweep removed while its connection survived (sleep, Wi-Fi hiccup > 30 s) kept getting unacknowledged heartbeats and ignored them — it believed it was connected while the server no longer listed it, until the app restarted. An unacknowledged heartbeat now triggers the normal reconnect (also covers a server restart).
- **Fix — stale stream removed the new session**: `SyncStream`'s `finally` removed the client whenever any of its streams ended, also an old stream that dies after the client already reconnected (a pulled cable is noticed late by the server). Only the stream still registered for the client ends its session now.
- **Review follow-ups**: re-registration forgets the client's old stream (session resume no longer writes into a dead one); a simulated drop tears down once, on the reconnect loop; fault `ms` validated against the client's 1–120 s; a tile name shared by two remotes fails the step; a node lost earlier cannot be faulted; a silent node not dropped 15 s after its silence fails then instead of after the 100 s watch; `recovery.json` drops 6 s for a stable verdict.
- **Tests never touch real settings**: `AppDataPaths.Root` (used by `ConfigurationManager` and `TopologyStore`) is redirected to a temp folder by the test assembly; tests that start a real server / client had rewritten the developer's `topology.json` and client id.
- **Expect**: with backoff 1/2/4/8/16/30 s, a 10 s outage reconnects ≈ 5 s after the link is back — right at the target. `recovery.json` uses a 6 s drop for a stable verdict (≈ 1 s after the link is back); edit `ms` to measure the 8–15 s gap. Tuning the backoff is a follow-up if it misses.
- **Verified**: build 0 errors; 896 unit tests green, incl. loopback gRPC integration tests (real server host + real client: drop, silent, swept client, test runs disabled), stream lifecycle tests, fault verdict tests and runner fault-step tests. Multi-machine run pending (runbook §2 row 5). Design: `.docs/plans/2026-09-30-automated-test-mode-design.md` §12.


## 2026-10-09 — Fix: desktop kept the last frame after the players stopped

- **Symptom**: after a test run (or Stop / Clear all / exit) the desktop still showed the last animation frame instead of the user's wallpaper; opening Windows' wallpaper settings brought it back.
- **Cause**: the player window was destroyed, but nothing told Explorer to repaint the wallpaper. The only refresh (`DesktopWindowManager.RefreshDesktop`) sits on a path the D2D players never use, and `RestoreDesktop()` on exit only closes a WorkerW the main process never tracks.
- **Fix**: `D2DPlayerHost` counts live player processes app-wide; when the last one is disposed it calls `DesktopWindowManager.RefreshUserWallpaper()` (`SPI_SETDESKWALLPAPER`, null, `SPIF_UPDATEINIFILE` — what Lively does after closing a wallpaper). Covers Stop, Clear all, clear nodes, shutdown, remote stop and the test-run restore, which all end in `Dispose`. `PlayerProcesses.Kill` (orphans from a crashed session, Clear all) refreshes too. Scene switches replace players monitor by monitor, so with 2+ monitors nothing flashes; a single-monitor machine shows its own wallpaper during the start gap.
- **Verified**: `smoke-local` on the raised desktop (Win11 build 26200, slideshow wallpaper): repaint logged at both full stops, wallpaper file and registry entry unchanged, no player left. 867 unit tests green.

## 2026-10-09 — Pre-flight for the first multi-machine session

- **What**: the server machine alone, on `da7215e`, before the first real network run. `dotnet build` 0 errors / 0 warnings; 867 / 867 unit tests green; `smoke-local` pass (CLI, exit 0); `parity-matrix` as a temporary copy with `minRemoteNodes: 0` pass (18/18 steps on 3 local monitors); `showcase/events` exit 0, every event visible in its exact frames (confetti, spotlight, traversal, whiteout covering the full frame, announce).
- **Docs**: new `.docs/plans/2026-10-09-remote-session-runbook.md` (session order, commands, firewall / network notes, time budget, "if time is short" cut; tick-boxes stay in the 2026-10-04 checklist). `.docs/2026.07_OPEN_ITEMS.md` refreshed (test count, the 10-03 test-mode follow-ups and the tray-client fix were still listed as open), `CLAUDE.md` Current Work points at the runbook.
- **No code change.**

## 2026-10-03 — One-shot show events (roadmap 2.4 slice 2)

- **What**: five moments the server fires over every seat while a scene plays, drawn above the scene by every player and the room preview: **Spotlight** (a seat's border pulses, every other seat dims and shows a chevron toward it — mirrored rows and the Ring's shorter way respected), **Celebration** (a closed-form seeded confetti burst that crosses the bezels continuously), **Traversal** (the scene sprite or a picked image laps the room exactly once), **Blackout** (fade to a color and back) and **Announce** (a text lap around the room; GDI+ text sprite, box measured on the server, so every seat draws the same aspect). Triggers: the new **Events strip** in the toolbar (seat picker for Spotlight, image picker for Traversal), global hotkeys **Ctrl+Alt+1..5** (server only; "Hotkeys" toggle in the strip — on German / Austrian keyboards Ctrl+Alt+digit is AltGr+digit) and **playlist interrupts** ("every N min, fire event X", the running item is unchanged).
- **How**: model, timing, math and the event log are pure code in `WaBiBaBuSy.Models/Show` (`ShowEvent`, `ShowEventFactory`, `ShowEventMath` timing / envelope / expiry, `CelebrationMath`, `SpotlightMath`, `TraversalMath`, `ShowEventLog`, `ShowEventSettings`, `ShowEventHotkeys`) — functions of the shared T0, used by the D2D players (`Player.D2D`, drawn above the scene) and the room preview (`ScenePainter.Events`), so test-mode exact frames show events and a seat joining late computes the same pixels. T0 = now + a lead, so every machine has the event before it starts; durations are clamped; targets are seat orders (`TargetOrder`). Transport: `SyncCommand` type **EVENT = 12** with `event_json = 6` (**proto change**; an older client ignores it — it logs "Unknown command type: 12" and keeps its scene). The client handles EVENT before the timing wait, fetches the traversal image through the normal asset path and forwards it to its players (`cmd_show_event`); `ServerPlaybackHost` / `RemotePlaybackHost` fire, log and replay events and prefetch the traversal image.
- **Replay rule**: the server keeps an event log; events survive playlist item switches (replayed to the new players, unfinished ones keep their position), and a seat of the running scene that reconnects (session resume, also after a client restart) or is Resynced (Resync all / node Resync) gets the unfinished ones right after its scene command (`WallpaperSyncCoordinator.ActiveShowEventsProvider`), so it draws them at their current position; the client dedupes by event id. A client that is not part of the running scene gets none. Events go only to the scene's remote seats — the same nodes Stop reaches (selected nodes, empty = all), never to idle clients. The log is **cleared whenever the server sends Stop to nodes** (Play or Quick start during a show, Clear nodes), so all seats stay consistent.
- **Player resources**: event GPU resources (celebration confetti, spotlight / announce sprites, the traversal bitmap) are kept across scene loads within one player process and released only on player exit. A sprite that fails to decode is skipped on that node (logged once, no retry); the other nodes still draw it.
- **Refusals / limits**: a Traversal whose fallback sprite is a video is refused with a status message (pick a traversal image). Hotkeys use a thread message queue (`RegisterHotKey(NULL, …)`), registered only while the server runs; a key held by another app shows "Hotkey(s) taken by another app" in the strip. Playlist interrupts end with the show (also when a non-looping show finishes on its own). The showcase Announce uses the measured `spriteAspect` 8.2.
- **Test mode**: scenario step `showEvent` (`event`, `atElapsedMs`; the runner assigns `eventId` and `t0Utc`) and `TestScenarios/showcase/events.json` (exact frames per event type; the Blackout fires in white — a "whiteout" — so its frames differ from the dark scene, and a 3 s wait after the Traversal lets remote clients fetch its image).
- **Review fixes along the way**: celebration copies on oversized wrapping nodes, cull slack and a separate spin hash (`92af2dd`); event resources surviving scene loads, no per-frame decode restart, sprite caps (`3169d9a`); event log cleared on node stop, video traversal sprites refused (`785df73`); interrupts ending with the show and null-safe `Interrupts` (`02a0b1b`); final review: resume / Resync replay, events only to the scene's seats, the traversal image inside the show prefetch (a separate PREFETCH reset the clients' counters), Spotlight picker names, per-monitor preview layout for events in Simultaneous mode, Announce measured trimmed, Hotkeys toggle, Announce text saved on fire, hotkey errors shown.
- **Verified**: build (0 errors) + 867 unit tests (all green). The UI and multi-machine behaviour were not run — checks in `.docs/plans/2026-10-04-manual-test-checklist.md` §8 / §9. Design: `.docs/plans/2026-10-03-show-events-design.md`.

## 2026-10-03 — Scene presets, import/export, tray Quick start (roadmap 2.2)

- **What**: the docked Scene editor gets **Presets ▾** (load; the room selection stays the target), **Save as preset…** (name field, the button says "Replace preset" when the name exists), **Export…** and **Import…**; the Playlist tab's **＋ Add ▾** offers "Current scene" and "From preset ▸" (a copy, all seats). The tray menu gets **Quick start ▸ <presets>**: plays a preset on every seat through the app-lifetime `ServerPlaybackHost`, with the control panel closed (even never opened since start). Hidden while connected to another server as a client.
- **Format & assets**: a preset is a scene file (`SceneFile` — PascalCase, enum names, comments), so a showcase scene is a valid preset and an export a valid scene. `PresetStore` (`WaBiBaBuSy.Core/Services/Animation`, `%APPDATA%\WaBiBaBuSy\presets\`) never stores targets. **Save** references gallery files in place (no copy of large videos). **Import, starter seeding and export** copy the assets into an `assets\` folder next to the JSON and reference them as `assets/<file>` (`PresetBundle`, Models): the source (install folder, repo, USB stick, Downloads) may move or vanish, and a relative folder stays self-contained and portable. Same file name + same content is reused; a different file with the same name becomes `<stem>-<hash8><ext>`. A missing asset is an error naming the file.
- **Starter presets**: on startup, when the preset folder holds no `*.json`, `PresetSeeder` imports `TestScenarios/showcase/starter-presets.json` (already shipped next to the exe for test mode): 14 presets. Design list → scenes: Mexican Wave = `04-la-ola`, Orbit = `06-cat-orbit-wave`, Logo Rain = "Logo Conveyor" `01-rainbow-conveyor` (Endless scrolls horizontally only), Ring Fish = new `14-ring-cat` (cat facing travel + trail; the loop comes from the room's Ring path), Party Colors = new `15-party-colors`; all other showcase scenes incl. `13-comet`. `12-lunar-tears` is left out (70 MB video outside the repo).
- **Room without a window**: `RoomNodeSources` (Core) builds the node list — server monitors + connected clients (multi-monitor clients expanded), or the standalone monitors — for both the control panel and the host; `LocalMonitors` (UI) enumerates the monitors. The host rebuilds the room from these sources when no window keeps it (`EnsureRoom`) and gains `QuickStartAsync`. Session gaps (standalone monitors, expanded nodes in client mode) moved into the host, so they survive closing the window.
- **Limits**: the Quick start menu follows the store (`Changed`) — preset files copied into the folder by hand appear after a restart. Deleting every preset brings the starters back on the next start ("seed when empty"). Remote nodes' prefetch counts are still not copied into the room's node list (pre-existing, unchanged by the move).
- **Review fixes**: a preset with a nested `null` (`ColorList`, `IconZonePaletteHexes`, `ColorGrading`, `SelectedMonitorIds`) is now rejected by `SceneFile.Load` like a top-level null — before, it loaded and crashed the app in the editor; every preset action in the editor catches everything into its status line; a failed tray Quick start shows "⚠ <preset>: <reason>" at the top of the Quick start submenu instead of doing nothing.
- **Verified**: build (0 errors) + 732 unit tests, 43 new (`SceneFileWriteTests`, `PresetBundleTests`, `PresetStoreTests`, `PresetSeederTests` incl. seeding the shipped manifest, `RoomNodeSourcesTests`). The UI was not run — checks in `.docs/plans/2026-10-04-manual-test-checklist.md` §2.7.

## 2026-10-03 — Fix: closing the server window lost the running scene / show

- **Symptom**: on the server, closing the main window kept the wall playing, but reopening it blacked the wall out (the new view model killed every player it did not track) and lost the show: no toolbar ■ / ⏭ / "Now: … next: …", no ▶ row, no live preview, and Stop / Clear all no longer reached what played. A show kept switching items through the closed window's view model, spawning players nothing tracked.
- **Cause**: the server's local players, the running scene, the playlist orchestrator and the room's live state lived in `MainWindowViewModel`, which exists only while the window is open (`TrayViewModel.ShowWindow` creates a new one each time). Same lifetime bug as client mode before `a434e43`.
- **Fix**: `ServerPlaybackHost` (`WaBiBaBuSy.UI/Services`, next to `RemotePlaybackHost`), created once by the tray, owns this machine's D2D / LibVLC players, thumbnails, fullscreen detection, the show (`PlaylistOrchestrator`), the running scene and its live state (`ActiveScene`, shared start, layout), the Play guard, the IconZone lap recompute, the debug overlay switches and the test-run entry points. The view model is a view over it: forwarding properties (XAML unchanged), host events (`SceneApplied`, `NodesStopped`, `ShowChanged`), `Detach()` on window close (also ends a leak — closed view models stayed subscribed to the service). It pushes the room (nodes as `RoomNode`, seat map, reference DPI) to the host on every topology refresh and room edit; with the window closed, scenes and show items use the last room it pushed. Show prefetch readiness is read from the server's client list, so it no longer depends on the window. On open, the Playlist tab loads the running show (a copy) on its current row, the room re-marks the playing nodes, and the debug overlay switches match the players. Opening the window no longer kills players (every player has an app-lifetime owner; prior-session orphans are still killed at app start). `TestHostAdapter` wraps the host, so a test run survives closing / reopening the window (starting one still needs the window open, as before). Exit stops the show and the local players through the host.
- **Pure parts** (`496d81c`, tested): `CrossScreenPlanner` (targets, local start order, per-node size / DPI fallbacks, seat-map layout, cm→px — the first half of the old apply method), `RoomNode` / `NodeIds`, `TestNodeList`, `ShowStatus` (the now/next line — minutes no longer wrap at an hour — and the prefetch rule). 22 new tests.
- **Removed**: dead code that touched the moved state (`StartOrchestrationAnimation`, `OnDistributedAnimationRender`, `ApplyGifWithLibVLCAsync`, the unreachable schedule-id stop branch) and the player-PID plumbing that only served the kill-on-open.
- **Limit**: a client that joins while the window is closed is part of the show's next items only once the window was open again (the host lays scenes out over the last room the window pushed).
- **Verified**: build + 686 unit tests (all green); `smoke-local` via `--test-run --exit` passes through the new test host (8/8 steps, spread 11-12 ms, no players left after exit). Close / reopen checks added to `.docs/plans/2026-10-04-manual-test-checklist.md` §2.6.

## 2026-10-03 — Sprite and bezel effects (roadmap 2.4, slice 1)

- **What**: a single sprite can leave a fading **trail** (ghost k = the sprite k × spacing ago, with that moment's position, facing, pulse, spin and — with a time-based color mode — color), **pulse** (scale breathing) and **spin**, and mark a **bezel crossing** with a Glow strip, a Flash or a Ripple ring on the machine where it enters / leaves, at that screen edge. Config: `AnimationLayerConfig.Effects` (`SpriteEffectsConfig`, travels as JSON — no protocol change). Editor: new **Effects** tab; hints when effects cannot be drawn (pattern, extra images, IconZone path); playlist rows list the effects.
- **How**: all math is pure (`WaBiBaBuSy.Models/Wallpaper/SpriteEffects.cs`) and shared by the players (`Player.D2D/Program.Effects.cs`) and the room preview (`ScenePainter`). A crossing is found by scanning the sprite's visibility on this node backwards over the effect duration on a 16 ms grid aligned to absolute elapsed time — every frame and every machine agree on the crossing time, a sprite that starts on a node fires no Entry there, and test-mode exact frames show the effects. On a non-ring wall the room's outer edges count as crossings too (Entry on the first screen as the sprite appears, Exit on the last as it leaves) — set the effect to None if that is unwanted. A crossing whose kind has no effect does not cut a running effect short. An invalid bezel color draws white and shows an editor hint (never throws). Pulse / spin fold elapsed in double (40-day test). Glow width and ripple radius are canvas units (same cm on a physical canvas).
- **Review follow-ups** (`9194b2c`, `9014ee3`): RandomWalk waypoints memoized per thread (bit-identical, no allocation per sample — the effects sample the walk ~30×/frame); tinted ghosts use layers bounded to their cell; with effects on the sprite faces by the pose like its ghosts (exact frames agree); preview ghosts keep their own color; playlist summary from clamped / active values.
- **Limits**: single image moved by `MovementCalculator` only — not with patterns, extra images or IconZone path-following (their position is not a pure function of time). Portal, one-shot events and transitions are still open.
- **Verified**: 58 new unit tests (scan timing, ring seam, mirrored rows, scene start, clamps, uptime, color parsing, editor round trip); exact frames on the dev machine show the rainbow comet trail, the exit Glow at the leaving monitor's edge and the entry Ripple on the entering one. Showcase: `TestScenarios/showcase/scenes/13-comet.json` (also in `showcase.json`, quick run `showcase/comet.json`, "BiBaBu Party" preset).

## 2026-10-03 — Tray-connected clients in sync · DWM present-time fallback · show restored after test runs

- **Tray-connected clients** (`a434e43`): client-mode rendering (cross-screen D2D, plain D2D, Stop, thumbnails, test probes) moved from `MainWindowViewModel` (exists only while the window is open) to the app-lifetime `RemotePlaybackHost` (`WaBiBaBuSy.UI/Services`), created and registered once by the tray. Before, a client connected from the tray played cross-screen scenes unsynchronized ("D2DCrossScreenApplyDelegate is NULL - falling back to plain D2D"). `WaBiBaBuSyService` keeps the delegates (`ClientPlaybackDelegates`) across connections. Player cleanup: prior-session orphans are killed at app start (players older than the app); opening the main window still kills untracked players but spares the host's; Clear all stops the host's players first; Exit stops them.
- **DWM present-time fallback** (`7ffb27b`): when DXGI frame statistics are unusable for 3 frames (the all-zero state of 10-02), a probe's present time comes from `DwmGetCompositionTimingInfo` (`DwmPresentEstimator`), or Present + 1.5 refresh periods when DWM's clock is another display. `PresentSource` / `PresentNote` on probe replies and samples; the drift message names estimated monitors. Live: 16/16 samples resolved, spread 8–10 ms (render-time fallback had shown 0 ms).
- **Show restore** (`c9ce043`): a test run brings back a running playlist show at the interrupted item (shuffle order kept), not only a plain scene.
- **Test seams** (`398c000`, `3ae2858`): `TimeProvider` seam + `SkewedSchedulingTests`; FETCH_LOGS replies matched by request id (`log_request_id = 35`, `request_id = 5`). `InternalsVisibleTo("WaBiBaBuSy.Tests")` on Core.
- Fix: drift messages named local monitors "server #1#1".

## 2026-10-02 — Test-mode follow-ups resolved · fix: control API leaked test mode to the LAN

All ~30 deferred findings from the test-mode reviews re-checked against the code and fixed, found already fixed, or closed with a reason — per-item record in `.docs/plans/2026-09-30-automated-test-mode-followups.md`. Unit tests 437 → 553.

- **Control API gate** (`f5806dc`): the loopback/port check was an endpoint filter, which runs after binding — a LAN peer on the gRPC port with a forged Host header got 415 / 400 / 405 and could tell test mode was on. Now middleware on the `/test` prefix, 404 before routing's own 415/405 endpoints and binding. Device paths (`\\?\`, `\\.\`, `\??\`) rejected explicitly; scenarios, scenes and assets on UNC shares are refused before any `File.Exists`.
- **Lost nodes** (`27c18ef`): the runner checks the server's node list after every step and when a remote misses a probe — a dropped or silent remote gets one timestamped `nodeFailures` entry and is skipped afterwards.
- **Runner / report** (`0b3d383`): run-unique per-node file names, step timeouts only from the step's own timer, probe timeouts include long `at` offsets, scenes validated before preflight probes, cancelled steps get duration + verdict, parity compared one frame at a time, skew check deferred until the client has a clock estimate, HTML-escaping / culture tests, fallback report.html if rendering throws; `PerfSampler` forgets exited players.
- **Player / host** (`4559c81`, `eb555f3`, `9a82b1b`): probes no frame takes are answered with an error after 1 s (and on loop stop) instead of hanging; malformed test commands no longer break stdout pairing; staging texture released if Present throws; probe waits cancellable; `SIGNAL:PROBE` parsing in `ProbeFanOut` with tests; no per-frame allocations from the timecode strip or present-time step-back.
- **Networking** (`e9f2938`, `59a90a0`, `f5949c4`): log fetches queued per client and windowed across midnight; probe uploads capped (128 MB) and only accepted from clients with an open command stream; one subscriber can no longer fail the RPC; remote thumbnails / probe targets survive reconnects; TEST_MODE re-sent to late players; test-run log fetches no longer open the logs panel.
- **UI / startup** (`0f770cd`): CLI and API runs show in the menu (Cancel + final status); a busy port 50052 no longer stops the gRPC server; CLI result line written to the calling console; unknown arguments warned; double-click / Settings-refresh / port-clamp fixes. Build-verified only.
- **Zero frame statistics** (diagnostic `d36c525`): probes that get no present time now log the `GetFrameStatistics` result. On 2026-10-02 the dev machine switched between full statistics (20:34, 16/16) and S_OK with all-zero counts (21:02–21:04, both the old and the merged build) without a code change — the cause behind the 2026-09-30 second-monitor nulls is environmental. Runs still pass (render-time fallback, noted in the report).

## 2026-10-02 — Showcase scenes + playlist presets · fix: color-graded sprites ignored their size

- **Fix: color grading drew sprites at native size** (`Player.D2D` `DrawCellWithOptionalGrading`): the graded branch drew the ColorMatrix output with `DrawImage(output, offset)`, which has no destination rect, so every tinted cell / sprite rendered at the source bitmap's size and ignored `TargetHeight`. Visible in pattern scenes as huge, overlapping colored logos next to correctly sized uncolored ones (`ColoredCellPercentage` < 1). Fix: scale the effect output to the cell (w×h) with a transform, like the `DrawBitmap` path. Not caught by `parity-matrix` because `marker.png` is natively 64 px with `TargetHeight` 64. Found and verified with the showcase run below (exact frames before/after).
- **Showcase** (`TestScenarios/showcase/`): 12 curated scenes (BiBaBu logo, cat GIF, video) and `showcase.json`, which plays each one and renders `exactFrame` stills at 1 / 4 / 9 / 20 s — a contact sheet in `report.html` for judging scenes without watching them live (~2 min, server only).
- **Playlist presets**: `TestScenarios/showcase/playlist-presets.json` (BiBaBu Party, Chill Lounge, Hype, Showcase Tour) → `dotnet run tools/build-playlist-presets.cs` loads the scenes with `SceneFile`, validates the folder's scenarios, and writes `Playlist` JSON (numeric enums, like `PlaylistStore`) to `%APPDATA%\WaBiBaBuSy\playlists\`.
- **Notes**: `FaceTravelDirection` is derived from the previous live frame's position, so `exactFrame` stills cannot show it (check facing with live `probe` captures). On a single server, `NodePhaseDelayMs` scenes look identical on both monitors (phase is per node/machine).

## 2026-09-30 — Automated test mode (spec 1) · fix: View logs hang · fix: remote thumbnails

- **Test mode**: scenario-driven runs across server + clients (`WaBiBaBuSy.Models/Testing`, `WaBiBaBuSy.Core/Services/Testing`). Steps: `testMode` (timecode strip, simulated clock skew), `playScene`, `probe` / `probeSeries` (present-time drift per node, marker position check, CPU/GPU/memory), `exactFrame` (offscreen render, pixel parity between nodes), `wait`, `stop`. Probe instants are server UTC ms, at least 500 ms ahead; series are scheduled on time (sequential only when `everyMs` < `MinLeadMs` + 100). The simulated clock skew also drives the client's command-scheduling clock (`WallpaperSyncClient.NowUtcMs`). The player draws a binary timecode strip (bottom-left) and answers `SIGNAL:PROBE` on stderr; clients upload results via `SubmitProbeResult`.
- **Entry points**: Developer tools > Run test suite, `--test-run <file> [--exit]` (exit 0 = no failed step, 1 = failed step, 2 = aborted), localhost control API on 127.0.0.1:50052 (`POST /test/run`, `GET /test/status`, `DELETE /test/run`, `GET /test/runs`; gated on the real loopback connection, application/json and local paths only). Server setting `EnableTestMode` (default off), client setting `AllowTestRuns` (default on).
- **Results**: `%LOCALAPPDATA%\WaBiBaBuSy\TestRuns\<yyyy-MM-dd_HHmm>_<scenario>\` with `report.json`, self-contained `report.html` (drift chart, annotated captures), `nodes/<node>/*.png`, `logs/`. `ProbeReport.Error` carries per-node failures.
- **Bundled**: `TestScenarios/` (`smoke-local`, `sync-basic`, `parity-matrix`, `clock-skew`, scenes, `assets/marker.png` from `tools/make-test-marker.ps1`), copied next to the exe; `BundledScenario_IsValid` tests parse them. See `TestScenarios/README.md`.
- **Design corrections**: the drift metric adds `phaseMs` back (`error = renderedElapsedMs + phaseMs − (presentServerUtcMs − sharedStart)`); the timecode strip has no text (DirectWrite not used by the player yet). Ring / Snake / facing / Physical units come from the room, not the scenario.
- **Fix: View logs hang** (`6ac1607`): the log is read through the logger's file lock, the client always replies and the server times out after 10 s.
- **Fix: remote thumbnails** (`580245f`): remote clients never uploaded thumbnails because the capture service was never assigned.
- **First single-machine runs** (`smoke-local`, server with two monitors, Debug build): captures, timecode strip, marker boxes and pattern parity (0.000 % differing pixels) were right, but the 30 s series spiked (34 ms, then 121 and 57 ms) on the 75 Hz monitor. Cause: `PollPresentStats` stepped an earlier present back by the nominal refresh rate (75 Hz) while the player presents at ~165 fps, so the estimated present time landed up to 115 ms before the render time. Fix: step back by the measured mean frame interval and clamp to >= render time. The server memory (540-575 MB with two local players) also tripped the 200 MB client threshold; it is now applied to remote clients only. Re-run after the fix: two runs `pass`, exit code 0, series spread 6-18 ms, no present time before render time (3 and 6 of 18 samples `presentEstimated`). Multi-machine validation is still pending.
- **Final-review fix wave**: drift spread from present-time samples only when >= 2 have one (`DriftResult.RenderSpreadMs` keeps fallback nodes visible); capture probes queue `CopyResource` before `Present` and map after it; `D2DPlayerHost` serializes stdin writes and the PROBE handler catches everything; app exit waits up to 10 s for a cancelled run (partial report, test mode off) and clients drop test mode / skew when the stream is lost; skew keys match the node name case-insensitively, an unmatched key fails the `testMode` step and the first probe checks the offset moved by about -skew; a remote lost mid-run gets one `nodeFailures` entry (run verdict at least warn); JSON nulls and IO errors in scenarios / scenes are `ScenarioException`s; the menu start never crashes; drift chart limits follow the scenario thresholds.

## 2026-09-30 — Fix: misleading "±2153ms" drift label · LAN bandwidth diet

- **Symptom**: remote tiles showed a red "±2153ms" that never changed, read as a 2-second ping on a local network.
- **Root cause**: the label (and the server's "exceeds sync tolerance" warning) showed the raw clock offset between the two PCs' wall clocks. Windows only resyncs its clock about weekly, so seconds of offset are normal — and every client already subtracts that offset from all server timestamps (`WallpaperSyncClient` SyncStream). The 2026-07-18 drift-telemetry design assumed "offset IS the sync error"; it is not. What remains after the correction is the estimate's uncertainty, ±RTT/2 (well under 1 ms on a LAN).
- **Fix**: `DriftMonitor.SyncErrorMs(rtt)` = RTT/2 drives `DriftState` (Ok ≤25 · Warn ≤50 · Breach >50 ms). Tiles show `ping N ms` coloured by that state; the tooltip lists sync ±, ping and the (corrected) clock offset; the health strip shows the worst ping. `DriftMonitor.Record` returns `DriftEvent` flags: `Breach` (new RTT breach) and `ClockStep` (offset jumped > 50 ms between reports — a wall clock was adjusted, and since players pace on wall-clock time a running scene there is off by the jump until Resync all). Both are logged once.
- **Clock-step recovery**: `ClockOffsetEstimator` restarts its 16-sample window when a new sample contradicts the best one beyond both ±RTT/2 bounds + 20 ms. Before, the min-RTT pick kept serving the pre-step offset for up to 80 s.
- **Thumbnails**: `HeartbeatResponse.thumbnails_paused` — the server sets it while its main window is closed (`WaBiBaBuSyService.ThumbnailsWanted`, driven by the refresh timer start/stop). Clients then skip capture (a full-resolution `PrintWindow` copy) and upload. When wanted: at most every 5 s (was 1 s), and unchanged frames (static wallpapers) are not resent. Default false keeps older servers receiving thumbnails.
- **Upload limit**: `BandwidthLimiter` (Models/Networking) — one shared byte-rate schedule for every server → client transfer (`DownloadContent` and `DownloadUpdate`). `ServerConfiguration.UploadLimitMBps` defaults to 20 MB/s (0 = unlimited); Settings → Server → Advanced → Upload Limit. Applied when the server starts. The 6-slot download semaphore stays.
- Tests: `DriftMonitorTests`, `ClockOffsetEstimatorTests` (clock step), `BandwidthLimiterTests` (6 tests).

## 2026-09-29 — Fix: Static movement ignored on IconZone backgrounds · Plain wallpaper button

- **Symptom**: movement set to Static (colors off) but the machines still moved and rotated the sprite; the room preview showed it still.
- **Root cause**: `Player.D2D` `UpdateAnimationPosition` ran IconZone A* path-following for every movement type, before `MovementCalculator` was consulted — Static was never checked and the path speed came from `SpeedPixelsPerSecond` (500 by default). The editor's default scene uses an IconZone background with RotateWithPath on, so any "Static" scene built from it wandered. `ScenePainter` does not simulate the path, which hid it in the preview.
- **Fix**: `MovementCalculator.FollowsIconPath(backgroundMode, movement)` (false for Static; a missing movement config keeps the legacy path-following) gates the player's path branch, its "no path" warning and the preview's "IconZone path not previewed" note. Static on IconZone now paints the zones and keeps the sprite centered.
- **Plain wallpaper**: `PlainScene.Create(path)` (Models) — SolidColor background, Fill, Simultaneous, Static, no color grading / pattern / rotation, loop on. Gallery right-click → "Show as plain wallpaper on selection / on all nodes"; Scene editor footer → "▣ Plain wallpaper ▾" (current file). The plain scene is loaded into the editor before playing, so preview, Revert and Save to playlist match what is live. Remote machines need the updated player.
- Tests: `WaBiBaBuSy.Tests/PlainSceneTests.cs` (10 tests).

## 2026-09-29 — Fix: auto-update crashed on clients (System.CommandLine missing)

- **Symptom**: remote client update failed without an updater log; Event Viewer showed `WaBiBaBuSy.Updater.exe` FileNotFound for `System.CommandLine, Version=2.0.12.0`.
- **Root cause**: the server-built update package copied only `WaBiBaBuSy.Updater.*` into `updater/`. The updater is framework-dependent, so its NuGet dependency ships as a separate DLL; the client ran the package updater first and it died while JIT-ing `Main`, before the log file was opened.
- **Server fix**: `UpdaterFiles.CopyUpdater` (`WaBiBaBuSy.Common/Update/UpdaterFiles.cs`) reads `WaBiBaBuSy.Updater.deps.json` and ships every runtime dependency (plus available satellite resources) in `updater/`. The package file name now includes the build number (`UpdatePackage_{version}.{build}.zip`) so a rebuilt server no longer serves a stale cached package.
- **Client fix**: `UpdateApplicator` repairs legacy packages by copying missing updater dependencies from `binaries/`; if still incomplete it falls back to the installed updater.
- **Recovery for already-broken clients**: copy `binaries\System.CommandLine.dll` from `%LOCALAPPDATA%\WaBiBaBuSy\Updates\Pending\Extract_<ver>\` into its `updater\` folder and run the updater manually (admin, `--process-id 0`).
- Tests: `WaBiBaBuSy.Tests/UpdaterFilesTests.cs` (6 tests).

## 2026-09-27 — UI redesign testing feedback

- **Connect row in the toolbar**: Server IP · Port · Connect · Find… (or Disconnect) now sit next to `Start Server` on the left (hidden in server mode) instead of inside the `⋯` flyout — first-run users see both roles immediately. Placeholder "Server IP (empty = auto)" + tooltip.
- **Connect semantics fix**: a typed server address is now connected to directly; only an empty field runs mDNS auto-discovery (fallback localhost). Before, Connect always ran discovery first and silently preferred the first discovered server over the typed IP.
- **Main window**: starts at 1400×800 with the right panel at 650 px. Fix: `MainWindow.axaml.cs` hard-coded the panel width to 460 px and overrode the XAML column width; it now takes its initial width from the XAML. The window is activated when opened from the tray so the first click (e.g. on the splitter) is not spent on activation.
- **Developer tools**: flyout widened to 400 px, composition row wraps so `Apply via LibVLC` is no longer clipped.
- **Scene gallery**: multi-row wrapping thumbnail grid (vertical scroll) instead of a single horizontal strip; a row splitter between gallery and editor tabs lets the user trade space between them (gallery min 120 px, editor min 260 px).
- **Gallery thumbnails 192×144** with a caption band (name + resolution, GIF frames + loop length, video length) and a multi-line tooltip (type · resolution · size, frames / loop / ≈fps for GIFs, length / fps for videos, full path). `+ Add` sits in the gallery's bottom-right corner.
- **Media metadata**: `WallpaperGalleryItem` persists `DurationMs`, `FrameCount`, `FrameRate`; read once in the background (`GifMetadataReader` via System.Drawing — delays normalized like the D2D player, `VideoThumbnailGenerator.ProbeVideo` via FFProbe) for new files and for galleries saved before these fields. Formatting is pure in `MediaDetails` (Models), tests: `MediaDetailsTests`.

## 2026-09-27 — UI redesign Plan 3: Settings sidebar

- **Settings sidebar**: 150-px ListBox with four sections (Server · Client · Wallpaper · Logging), `SelectedSectionIndex` binding; Advanced expanders per section.
- **Server section**: Port, Content Directory (Browse button), Enable Auto-Discovery (mDNS). Advanced: Max Clients, Service Name.
- **Client section**: Server Address, Server Port, Cache Directory (Browse button), Auto-connect on startup, Prefer auto-discovery. Advanced: Heartbeat Interval (s), Max Cache Size (MB).
- **Wallpaper section**: Pause wallpaper when a fullscreen app is detected (only control).
- **Logging section**: Global level (Information/Debug/Warning/Error); Log to file toggle (shows Log Directory textbox when on); Component filters (UI, D2D Player, Composition, Renderers, Networking, Animation, File Transfer). Advanced: Performance metrics, Frame-by-frame.
- **Fix: Settings Save preserves identity and operational fields** — `ConfigurationManager.UpdateServerConfiguration / UpdateClientConfiguration / UpdateLoggingConfiguration(Action<T>)` pattern (read-modify-write) preserves `ClientId`, `ServiceType`, `UpdateSettings`, log file limits; these fields are not editable in the UI but survive save operations. Tests: `WaBiBaBuSy.Tests/ConfigurationUpdateTests.cs` (6 tests).
- **Browse buttons**: Content Directory and Cache Directory only (folder picker).
- **Persistence**: footer shows save errors in amber; window stays open on error. `SettingsWindow.ShowSingle()` keeps one instance (tray icon, ⋯ menu). Enter = Save, Esc = Cancel; CenterScreen.
- **GUI verification pending** (user will do interactive checks).

## 2026-09-25 — UI redesign Plan 2: docked Scene editor + Playlist tab

- Right panel `[Scene | Playlist]` replaces the gallery pane, `CrossScreenConfigDialog` and `PlaylistDialog`.
- Scene tab: gallery strip (click / Ctrl+click / right-click), Content · Motion · Look · Background tabs with Advanced sections, ⓘ tooltips, amber validation chips (`SceneChecks`), units follow Room ⚙ physical units, targets = room selection (`SceneTargets`, chain order).
- Draft preview: editor changes reach the room tiles after 150 ms; preview badge toggles draft ↔ live, ⏸ ⟲ 1×/4×/16× for the draft clock; the 2 s topology refresh no longer restarts it (`SceneChecks.SameScene`).
- Playlist tab: picker / New / Save, durations in seconds (`PlaylistEditing`), drag reorder, Duplicate / Remove, selecting an item loads it into the editor, Save to playlist → add / update.
- Play on selection / Play on all first stop a running scene; a running show is stopped and every node cleared before the scene starts.
- Toolbar: Show ▾ ▶ ■ ⏭ with "Now: … · next: …"; `PlaylistOrchestrator.Skip()`.
- Removed: `CrossScreenConfigDialog`, `PlaylistDialog`, `ScenePreviewControl`, `WallpaperMultiSelectDialog`.

## 2026-09-24 — UI redesign Plan 1: room view

Design: `.docs/plans/2026-09-24-ui-redesign-design.md` (Plan 1: toolbar + RoomView; docked editor
and further polish are Plans 2–3, pending).

- **Multi Monitor dialog fix**: the `Scene…` dialog (`CrossScreenConfigDialog`) failed to open on
  this branch — a `FindControl` call during `InitializeComponent` ran before the named control
  existed. Fixed; the dialog now opens reliably as a child of the main window.
- **Compact toolbar**: the main window's top bar collapsed to one row — status (left), show
  controls `Scene…` / `▶ Start` / `■ Stop` / `Playlist…` / next-item label / `Clear all` (center),
  and a `⋯` flyout for connect-to-server, `Settings…`, `Open log folder` and a "Developer tools"
  expander (composition mode, D2D/LibVLC apply, background color, debug overlay toggles) (right).
- **Room ⚙ popover + split evenly**: room-wide layout settings (path, turn gap, row gap, physical
  units, vertical align) moved into a popover off a single "Room ⚙" button; added "Split evenly
  into N rows" for a quick starting layout.
- **`RoomView`** (`WaBiBaBuSy.UI/Controls/RoomView.cs`, `RoomView.Menus.cs`): row lanes of node
  tiles, each tile painting the live running scene; drag a tile between rows/positions; right-click
  context menus on tiles and row headers (split/merge row, move row, rename, toggle facing,
  per-node Clear/Resync/logs); inline gap editor on a tile; an empty "+ new row" drop zone. Backed
  by `RoomGrid` (`WaBiBaBuSy.Models/Topology/RoomGrid.cs`, geometry/hit-testing) and `SeatMapEditor`
  (`WaBiBaBuSy.Models/Topology/SeatMapEditor.cs`, pure row edits); talks to the view model only
  through `IRoomHost` (`WaBiBaBuSy.UI/Controls/IRoomHost.cs`).
- **Selection bar**: replaces per-node UI with a bar shown while nodes are selected — summary,
  Clear, Resync (server mode), Logs (single node), "Make row from selection" (multi-select).
- **`ScenePainter`/`SceneClock` extraction**: `ScenePreviewControl`'s drawing and clock logic split
  into standalone classes so `RoomView` can reuse them for the live tiles; `ScenePreviewControl`
  itself is unchanged in behavior and still used by `CrossScreenConfigDialog`.
- **Removed panels**: the "Selected Client Details" border (order ▲/▼, distance textbox — superseded
  by drag-to-reorder and the gap editor) and the "Active Animation" info panel in the gallery pane
  (file/mode/speed/background + its own `ScenePreviewControl` — superseded by the live scene in
  every room tile; the "next:" label lives in the toolbar).
- **Final-review fixes**: tiles stop painting the sprite when the show stops (Stop / Clear all /
  per-node Clear) and, while live, paint with the layout the players were started with
  (`RoomView.ActiveLayout`) — only on animating nodes that were part of the start; row edits that
  change row 0 keep which rows face each other (`SeatMapEditor.Compose` flips orientations);
  gap edits no longer snap back on refresh (server-local and expanded multi-monitor nodes read their
  persisted gap back; local-only and client-mode expanded nodes keep a session value); a second
  button/pointer during a drag is ignored and the refresh pause is idempotent; dragging near the
  scroll viewport edge auto-scrolls; right-click on a row header opens the row menu.
- **Known issues**: in local-only mode (no server running) gap edits last for the session only (no
  distance store), and a drag that reorders nodes snaps back to monitor order on the next refresh
  (the local-only refresh assigns `Order = monitor index`; pre-existing).

## 2026-09-17 — Show reliability: content pipeline (Tier 1.3)

Design: `.docs/plans/2026-09-17-show-reliability-design.md` §3 + §5 (§2 future T0 landed in Tier 0; §4 standby/two-phase switch and §6 auto-start deferred). 9 new unit tests (141 total).

- **Content ids identify bytes** (`WaBiBaBuSy.Models/Content/ContentIdentity`): `{fileName}-{sha256[..16]}`, hash cached per (path, size, mtime). Same name + new bytes → new id → clients fetch the new version; no more stale content after re-exporting a GIF under the same name.
- **Persistent client cache index** (`ContentCacheIndex`, `cache-index.json` in the cache dir): the playback service restores contentId → path on startup and saves after every download/registration. A client restart no longer re-downloads the whole show. LRU touch on cache hits.
- **All assets travel**: `SceneAssets.CollectPaths` lists primary + additional images + background image; the apply path registers each (`ContentAssetRef`) and ships them in `SyncParameters.assets` (proto `ContentRef`, field 26). The client downloads every asset before applying and `SceneAssets.RewriteToLocal` rewrites the configs to local cache paths — multi-image presets and image backgrounds now render on remotes instead of degrading. Missing assets still degrade gracefully (dropped / solid color).
- **Prefetch**: new `CommandType.PREFETCH` (9); `PlaylistOrchestrator.Start` asks every client to cache the whole show (`WallpaperSyncCoordinator.BroadcastPrefetchAsync`) and waits up to 15 s until all remote nodes report ready via heartbeat (`prefetch_ready/total`, HeartbeatRequest 7/8, ConnectedClient 12/13); item switches then never wait for a download. Client downloads are serialized (a prefetch cannot starve a LOAD).
- **Server throttle**: at most 6 concurrent `DownloadContent` streams (`SemaphoreSlim`), so 20 first-time fetches queue instead of thrashing.
- **Show health**: topology header shows "N/M remote online · cached/fetching · stale · worst ±X ms"; nodes carry a "⬇ 3/7" / "✓ cached" badge; **Resync all** re-sends every active cross-screen command (original shared start) — fixes a node that missed a command. Active Animation panel shows "next: <item> in m:ss" while a show runs (`PlaylistOrchestrator.NextItem/NextSwitchUtcMs`).
- **Known**: downloaded files keep their original file name in the cache, so two different files with the same name overwrite each other on disk (the index then points both ids at the newest bytes — acceptable, documented).

## 2026-09-17 — Physical canvas for mixed monitors (Tier 1.2)

Design: `.docs/plans/2026-09-17-physical-canvas-design.md`. 15 new unit tests (132 total).

- **Model**: `SeatMap.CanvasMode` (Pixels | Physical) and `SeatMap.VerticalAnchor` (Center | Top | Bottom); `NodeLayout.Scale` (= node ppcm / reference ppcm) and `RefPixelsPerCm`. `SeatMapLayoutBuilder.Build(map, nodes, referencePixelsPerCm)`: on a physical canvas each node's slice is `px / Scale` reference pixels and bezel/turn gaps use the reference DPI; unknown DPI → Scale 1. Pixel mode is bit-identical to before (tested).
- **Units**: `AnimationLayerConfig.SizeUnit` + `TargetHeightCm`, `MovementConfig.SpeedUnit` + `SpeedCmPerSecond`. New fit mode `ContentFitMode.TargetHeight` ("scale to a fixed height") — the first fit mode where the height field affects the main sprite (Center = native, Fit/Fill/Stretch derive from the screen; the dialog now hides the height field for those). `PhysicalUnits.ResolveForCanvas` converts cm → canvas px right before broadcast, so players and the deterministic math only ever see pixel values.
- **Reference DPI**: the server's primary monitor (`MainWindowViewModel.ReferencePixelsPerCm`, cached); shown in the Room summary, with a warning when unknown.
- **Player**: animation-layer math (layout, fit, movement, pattern, IconZone planner inputs, corridor clamp) runs in canvas units `_cw × _ch = layout.Width × layout.Height`; `DrawCellWithOptionalGrading` applies `Scale` as a D2D transform (composed after flip/rotation). Zone bands are converted back to device px for drawing. Per-monitor (Simultaneous) mode has no layout and therefore no scale.
- **Playlist**: `ApplyMetrics.SpeedPxPerSecond` / `PlaylistScheduler.ComputeLapMs(..., speedPxOverride)` so lap-snap uses the resolved canvas speed.
- **UI**: Room panel gains "Physical units (cm)" and "Align" (Center/Top/Bottom); animation dialog gains the "Target height" fit mode, a px/cm toggle on the height, a px/s / cm/s toggle on the speed (own slider 1–150 cm/s); topology node caption shows the monitor's physical size in cm; the preview resolves units the same way and reports canvas length in metres and sprite width in cm.
- **Not covered**: EDID-based DPI verification / per-seat DPI override (design §6) — `MonitorDpiHelper` already prefers raw DPI; add the override if a monitor reports nonsense.

## 2026-09-17 — Live preview (Tier 2.1)

Design: `.docs/plans/2026-09-17-authoring-ux-and-preview-design.md` §2 (the preview part; tabs/presets are still open).

- **`ScenePreviewControl`** (`WaBiBaBuSy.UI/Controls/`): custom Avalonia control that draws the room's lanes from a `SeatMapLayoutResult` and animates the scene with the players' own pure functions (`MovementCalculator` incl. ring wrap, `NodeMapping` mirroring + seam copies, `PatternLayout` cells with traveling colors via the new `ColorGrader.ComputeCellColor`/`IsCellColored`, time-based tints via `ComputeCurrentColor`, `SyncTiming` node phase / future-start gating, ThreeZone corridor clamp). Sprite drawn from the image file (gallery thumbnail for videos), tinted when grading is active. Legend: t, lap time, canvas size/ring, "sprite on: <host> (#n)"; the node currently holding the sprite gets a green outline.
- **Config dialog**: preview pane above the form with Pause/Restart and 1×/4×/16× design clock; every view-model change (incl. monitor selection) rebuilds scene + layout after a 150 ms debounce and restarts the clock. Layout comes from `MainWindowViewModel.LayoutForSelection` → identical to what Start will use.
- **Main window**: the "Active Animation" panel now contains the live view on the players' shared clock (`ActiveLayout/ActiveScene/ActiveSharedStartMs` set by the apply path) — this is the "where is the fish right now" display from the roadmap.
- **Not simulated** (stated in the UI): GIF frame timing, IconZone path-following, video frames, face-travel flip.

## 2026-09-17 — Seat map & ring topology (Tier 1.1)

Design: `.docs/plans/2026-09-17-seat-map-and-ring-topology-design.md` (implemented as the "row breaks over the ordered chain" variant — see the status note in that doc). 29 new unit tests (112 total).

- **Model** (`WaBiBaBuSy.Models/Topology/`): `SeatMap` (rows with `SeatCount` + `Orientation`, `Traversal` Ring/Snake/Parallel, turn/row gaps), `NodeLayout` (offset, canvas size, `Mirrored`, `Wraps`, order, row/index-in-row), pure `SeatMapLayoutBuilder` (path or stacked layout, mirroring rule `dir_r × screenAxis_r < 0`, turn gaps, Ring closing gap), `NodeMapping` (virtual→local incl. mirroring, seam `WrapCopies`), `SeatMapStore` (`%APPDATA%\WaBiBaBuSy\seatmap.json`, enums by name).
- **Movement**: `MovementCalculator.Calculate(..., canvasWraps)` — Linear/SineWave laps are exactly one perimeter on a Ring (0 → P, no off-screen run-in); Bounce/Circular/RandomWalk unchanged.
- **Transport**: `SyncParameters.layout_json` (field 25), `CrossScreenApplyRequest.LayoutJson`, `PlayerCommandLoadAnimation.Layout`; loose width/offset fields stay populated for older clients.
- **Player**: keeps `_layout` + `_animVirtualX`; all local X mapping goes through `NodeMapping` (mirrored nodes flip inside their slice, pattern cells too); a seam-straddling sprite is drawn at x and x − P.
- **Apply path**: `ApplyCrossScreenConfigAsync` builds the canvas with `SeatMapLayoutBuilder` over the topology order (a single-row seat map reproduces the old `VirtualCanvasManager` layout exactly, incl. bezel gaps and vertical centering); IconZone global path uses the same canvas width.
- **UI**: "Room" panel above the topology (rows, seats per row, rows face each other, path Ring/Snake/Parallel, turn gap, summary); topology renders one lane per row with odd rows placed right-to-left so the ring reads as a loop, row captions with direction, stacked-turn arrows and a closing Ring arrow; drag-reorder maps lanes back to traversal order. `MainWindowViewModel.BuildSeatLayout` is shared by apply path, lanes and preview.
- **Defaults**: fresh installs stay single-row (Snake) = today's behavior. For the party: Rows = 2, Seats/row = 10, facing on, Path = Ring.
- **Deferred from the design**: per-seat labels/bindings, Identify + click-to-order tools, `UpdateSeatMap` RPC (the seat map is server-local; clients only receive their `NodeLayout`).

## 2026-09-17 — Tier 0 quick wins (LAN-party roadmap)

Roadmap: `.docs/plans/2026-09-17-lan-party-roadmap.md` §4 Tier 0. All seven items; 30 new unit tests (83 total).

- **0.1 Canvas height + vertical centering** — `VirtualCanvasManager` now centers each screen inside the canvas height (`ScreenMapping.VirtualBounds.Y`); `D2DCompositionService` sends the wall's tallest height as `VirtualCanvasHeight` plus `MonitorOffsetY`; the player runs `MovementCalculator` against the canvas height and subtracts its Y offset. Fixes the vertical jump between a 1080-px and a 1440-px node. Remote transport: `SyncParameters.virtual_canvas_height` / `monitor_offset_y` (fields 22/23).
- **0.2 Persistent client identity** — `ClientConfiguration.ClientId` stores the server-assigned id; `WallpaperSyncClient` sends it on every registration, persists it via `ConfigurationManager.UpdateClientId`, and no longer clears it on disconnect. A restarted client is the same node.
- **0.3 Persistent topology** — new `TopologyStore` (`%APPDATA%\WaBiBaBuSy	opology.json`): order + bezel distance per node, bound by ClientId with hostname fallback (re-imaged machine keeps its seat). `WallpaperSyncService.RegisterClient` re-attaches known machines and appends new ones; order/distance edits (gRPC and server-mode direct) persist.
- **0.4 Future start timestamp** — `SyncTiming.ComputeStartLeadMs` (3×worst RTT + measured local load time, clamped 800–4000 ms) schedules the shared start ahead; players draw background only until `elapsed ≥ 0`, so all nodes reveal the sprite on the same frame. `PlaylistOrchestrator` measures dwell from the scheduled start (`ApplyMetrics.StartLeadMs`).
- **0.5 Face travel direction** — `AnimationLayerConfig.FaceTravelDirection`: the single-sprite draw path mirrors the bitmap when screen-space dx < 0 (0.5 px hysteresis, loop wraps ignored). Off by default; checkbox in the dialog.
- **0.6 Exposed parameters** — RandomWalk seed + step interval (with "Randomize"), playback speed multiplier; these were hardcoded (42 / 1000) or unreachable in `CrossScreenConfigViewModel.BuildConfig`.
- **0.7 Wave mode** — `MovementConfig.NodePhaseDelayMs`: in Simultaneous distribution each node shifts its clock by `order × delay` (`SyncTiming.ApplyNodePhase`), so a bounce/orbit runs down the row like a stadium wave. `NodeOrder` travels in IPC and gRPC (`node_order`, field 24). Ignored in Sequential mode.
- Tests: `SyncTimingTests`, `TopologyStoreTests`, roundtrip coverage for the two new config fields. E2E on real remotes still pending (see OPEN_ITEMS §1).

## 2026-07-18 — Playlist / Party Mode (Tier 2 #7)

Rotate a set of animation configs across all synced machines as a "show." Plan: `.docs/plans/2026-07-18-playlist-party-mode-plan.md`, design: `.docs/plans/2026-07-18-playlist-party-mode-design.md`.

- **Data model** (`WaBiBaBuSy.Models/Wallpaper/Playlist.cs`): `Playlist` (Name, Loop, Shuffle, DefaultItemDurationMs, Items) + `PlaylistItem` (Name, embedded `CrossScreenConfig`, `DurationMs?`, `SnapToLap`). Each item embeds a full self-contained config — this is the first on-disk persistence of `CrossScreenConfig`.
- **Scheduling** (`PlaylistScheduler.cs`, pure + unit-tested): `ComputeLapMs` (Linear-only lap period, mirrors `MovementCalculator.CalculateLinear`), `ResolveDwellMs` (per-item duration with global default; opt-in lap-snap rounds up to a whole lap), `BuildCycleOrder` (seeded Fisher–Yates shuffle, server-authority-only — never a render input).
- **Orchestration** (`WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs`): server-side rotation loop that re-invokes the existing shared-timestamp broadcast path per item, waits the resolved dwell, advances; loop/shuffle/stop; pauses on apply failure to avoid busy-looping. Tracks `CurrentItem` so the existing session-resume path (`_activeCrossScreenCommands`) lands a reconnecting client on the current item.
- **Apply path refactor**: extracted `MainWindowViewModel.ApplyCrossScreenConfigAsync(CrossScreenConfig)` from `StartCrossScreen()` (behavior-neutral) so both the manual Start button and the orchestrator share it.
- **Persistence** (`PlaylistStore.cs`): JSON under `%APPDATA%\WaBiBaBuSy\playlists\`.
- **UI**: dedicated `PlaylistDialog` (`PlaylistViewModel` + `PlaylistItemRow`) — add/edit (reuses the CrossScreen config dialog), reorder, duplicate, remove; Loop/Shuffle/default duration; Start/Stop Show; Save. Entry-point button next to the cross-screen actions in `MainWindow`.
- **UX decisions**: per-item duration + global default; hard-cut with opt-in lap-snap; remotes auto-follow (no per-client opt-out — use the existing per-monitor selection to exclude); own dialog.
- **Known limitations (v1):** lap-snap is Linear-only (other movement types hard-cut); `ContentWidthPx` is best-effort 0 so lap-snap uses canvas-width traversal distance; consecutive items targeting *different* monitor sets leave stale D2D services on dropped monitors until Stop (fine when all items use the same monitor set); scheduled interrupt-shows, per-client opt-out, and cross-item transitions are deferred.
- Tests: 11 new playlist unit tests (models roundtrip, lap/dwell math, shuffle). Full run pending E2E multi-client validation.

## 2026-07-18 — Legacy code cleanup (GDI+ composition stack + frame streaming)
- **Archived** the dead GDI+ composition stack to `_archive/legacy-gdi-composition/` (outside the build): `CompositionRenderer`, `AnimationLayerRenderer`, `BackgroundLayerRenderer`, `GifWallpaperRenderer`, `CrossScreenFrameRenderer`. See the folder's README for the dead-code verification.
- **Removed** the server-side frame-streaming gRPC path end-to-end: `StreamCrossScreenFrames` RPC, `CrossScreenFrame`/`FrameAcknowledgment` messages, `CompressionType` enum, `CROSSSCREEN_START/STOP` command types (tags 6/7 `reserved`), client frame-stream machinery, coordinator `SendCrossScreenFrameAsync`, `UseDistributedRendering` config flag, and the player's never-initialized "video fallback" render branch (incl. `ConvertBitmapToD2D`).
- **Removed** the unimplemented `DistributionMode.GroupedSequential` enum value (2D topology covers the underlying need).
- **Kept** `VideoWallpaperRenderer` + `ImageWallpaperRendererLibVLC` — the 2026-07 audit mislabeled them dead; they drive simple per-monitor playback.
- Verified: full solution builds 0 errors, 37/37 tests green.

## 2026-07-18 — Drift telemetry (replaces TimingSynchronizer)
- Clients report clock offset + RTT via heartbeat (`has_drift_report` guards the sample-less first beat)
- Server stores reports on `ConnectedClient`; `DriftMonitor` logs once per new ±50ms breach
- Topology nodes show color-coded "±Xms" (green ≤25 / yellow ≤50 / red >50 / grey stale)
- Deleted dead `TimingSynchronizer`, `BroadcastAnimationTimingSync` RPC + proto message, and the orphaned `BroadcastCommandAsync` helper

## 2026-07-07 — Movement Polish + mDNS Server Browser (Tier 2 start)

- **FIXED: Long-uptime float precision** — `MovementCalculator` folds elapsed time into each movement's period in double before float math; positions stay sub-pixel accurate after weeks of uptime (previously multi-pixel stutter after ~2 days, unit-tested at 40 days).
- **FIXED: RandomWalk seed-rotation teleport** — boundary waypoints pinned to the previous iteration's seed; the walk is now continuous across seed rotations while still never repeating.
- **FIXED: Drift telemetry computed as 0** in `ClientAnimationRenderer`; `TimingSynchronizer` latent bugs fixed (session-scoped clients, CS1998) — the loop itself remains unwired (orchestrator path only).
- **NEW: mDNS Server Browser** — "Find..." button next to Connect opens a live dialog of discovered servers (`ServerBrowserDialog`); double-click connects. Uses the existing `MdnsClientDiscoveryService`.
- Tests: 19 total (7 new movement determinism/precision tests).

## 2026-07-07 — Tier 1 Reliability: Trustworthy Multi-Machine Sync

Plan: `.docs/plans/2026-07-07-tier1-reliability.md`. New `WaBiBaBuSy.Tests` xunit project (12 tests).

- **NEW: Clock-offset compensation** — every heartbeat feeds an NTP-style min-RTT `ClockOffsetEstimator` (Models.Networking); incoming command timestamps (`TimestampUtc`, `SharedStartTimestampMs`) are converted from server-clock to local-clock terms at the sync-stream boundary. The ±50ms target no longer requires machines to have synced Windows clocks.
- **NEW: Auto-reconnection** — `WallpaperSyncClient` reconnects with 1s..30s exponential backoff on sync-stream loss or 3 consecutive heartbeat failures; fires `ConnectionStatusChanged` immediately (previously it stayed a "connected" zombie).
- **NEW: Session resume** — the coordinator remembers the active cross-screen command per client and re-sends it when the client re-registers; epoch back-dating lands the rejoining machine at the correct mid-animation position.
- **FIXED: Remote parameter parity** — `SyncParameters` gains `movement_json`/`animation_json`/`background_json`/`target_monitor_index`; remote clients now receive the full `MovementConfig` (Reversed/Endless/wave/orbit/seed), `AnimationLayerConfig` (TargetHeight, SpeedMultiplier, precomputed IconZone A* path, pattern, color grading) and `BackgroundLayerConfig`. Delegate is now `Func<CrossScreenApplyRequest, Task>` (new Models DTO).
- **FIXED: Simultaneous mode on remotes** — per-monitor mode no longer receives the spanning canvas/offset overrides (was silently rendering as Sequential on remote machines).
- **FIXED: Server hardening** — bind failures surface (`await app.StartAsync()`); per-client `SemaphoreSlim` serializes gRPC stream writes (overlapping writes silently dropped commands); 10s sweep removes clients with heartbeat >30s old; client/host `Dispose` no longer risks UI deadlock.
- **NEW: Real remote monitor geometry** — clients report physical pixels-per-cm per monitor (`GetDpiForMonitor`) at registration; server gap math uses it instead of assuming its own monitor model.
- **Known remaining gaps:** background images / multi-image sources are not file-transferred to remotes (graceful fallback); TimingSynchronizer drift loop still dead code; float-precision folding + RandomWalk seed-rotation continuity still open (see OpenIssues).

## 2026-07-07 — Documentation Audit (retroactive changelog)

The changelog was not maintained between 2026-03-29 and 2026-05-19. The entries below reconstruct that period from git history. Version 2.0 → 2.6.3 during this window.

## 2026-05-13 to 2026-05-19 — Remote Networking Fixes + Maintenance

- **FIXED: Remote networking — Pattern and Coloring modes** (`237cc4d`) — `PatternConfig` and `ColorGradingConfig` now serialized as JSON (`pattern_json` / `color_grading_json` in `SyncParameters`) so remote clients render the same pattern grid and colors as local monitors. **Known gap:** full `MovementConfig` (Reversed/Endless/wave/orbit/seed) and non-solid backgrounds are still NOT transmitted to remote clients — only `movement_type` int + bg color.
- **Updater fixed again** (`8437cde`), **NuGet packages updated** (`abcad3d`), **Graphify knowledge graph set up** (`f338c21`).

## 2026-05-02 to 2026-05-11 — Traveling Colors + Pattern Polish

- **NEW: Traveling Colors** (plan: `.docs/plans/2026-05-01-traveling-colors.md`) — three per-cell `ColorGradingMode` values (`TravelingRainbow`, `TravelingList`, `TravelingRandom`): each pattern cell gets a fixed color from its `(LogicalI, LogicalJ)` grid identity via `ColorGrader.ComputeForCell`, so colors travel with elements across monitors instead of cycling uniformly. Fixes in the series: hue distribution, TravelingList overflow, stale matrix in non-pattern paths.
- **Pattern fixes** — Pattern Life fixed, pattern gradient + white icon zones fixed, pattern fading improvements, "best fading logic".
- **Reverse option** extended to all applicable movement types (Linear, SineWave, Circular) (`81d6a21`).
- **Remote node fixes** (`0c27fd6`); refresh rate now always shown in topology nodes (`97d707e`) with mismatch warning (see tearing issue in OpenIssues).

## 2026-04-04 to 2026-04-24 — Corridor + IconZone Animation Systems

- **NEW: Corridor Animation System** (v1 → v2.3; plan: `.docs/plans/corridor-animation-system.md`) — `ThreeZone` background mode (top zone / darker middle corridor / bottom zone) constrains the animation path to a user-defined horizontal band so it avoids desktop-icon areas.
- **NEW: IconZone Animation** (v2.4 → v2.5; open issues: `.docs/2026.04_IconZoneAnimation_TODO.md`) — `IconZone` background mode reads real desktop icon positions (`SysListView32` / `LVM_GETITEMPOSITION`), builds occupancy zones (`ZonePlanner`), and routes the animation along an **A\* path** through icon-free space. Dynamic zone sizing, zone merge/expansion, feathered fades, per-cluster palettes (debug), desktop-refresh handling.
- **Sequential pathing** — corridor/A* path regenerates per traverse; RandomWalk iteration seed rotation; traverse-detection fix preventing 1-frame position jumps; Bounce traverse formula fix; sequential pathing reset fix.
- **FIXED: Flickering** — `ValidateRect` in `WM_PAINT` prevents invalidation feedback loop.
- **Debug overlay** — per-flag controls in main window toolbar (path, icon rects, zone band outlines, info panel); `ShowZoneBandOutlines` rendering implemented; `ToggleDebugOverlay` state sync fixed.
- **UI** — Animation Config dialog rework, uniform 28px toolbar control heights, icon zone config layout fixes.
- **Maintenance** — NuGets with vulnerabilities updated; `.worktrees/` gitignored.

## 2026-03-30 to 2026-03-31 — Static Images + Updater + Remote Animation

- **FIXED: Static image rendering** — JPG/PNG/BMP load as a single Magick.NET frame through the native D2D GIF pipeline.
- **Updater fixes** (multiple rounds), player deployment issues fixed, Bezier arrow head in topology view fixed, remote animation fixes.
- **Docs updated** with rendering architecture (`ab1b1bc`).

---

## 2026-03-29 — File Logging Fix + Remote Client D2D Wiring

### FIXED: File logging empty on remote clients
All service loggers in `TrayViewModel`, `MainWindowViewModel`, `WaBiBaBuSyService`, and all sub-services (WallpaperSyncClient, WallpaperPlaybackService, WallpaperSyncCoordinator, etc.) were constructed with private `LoggerFactory.Create(...AddConsole())` instances. These bypass `AppLogger` entirely — nothing reached the file provider.

**Fix:** Removed all `LoggerFactory.Create()` calls in production code. All logger creation now uses `AppLogger.CreateLogger<T>()` or `AppLogger.Factory`. Added `AppLogger.Factory` static property to expose the underlying `ILoggerFactory` for services that require it.

**Rule:** Never call `LoggerFactory.Create()` directly anywhere in the UI or Core layers.

### FIXED: D2D wallpaper not applied on remote clients (race condition)
`WallpaperSyncClient.ConnectAsync()` calls `StartSyncStream()` before returning, so gRPC commands can arrive immediately. Previously `D2DApplyDelegate` was set *after* `ConnectToServerAsync()` returned — a LOAD command arriving in that window saw `null` and silently fell back to the non-functional LibVLC path.

**Fix:** `ConnectToServerAsync(address, port, d2dApplyDelegate)` now accepts the delegate as a parameter and sets it on `WallpaperPlaybackService` immediately after construction, before returning. Both `TrayViewModel` and `MainWindowViewModel` pass the delegate at call time.

**Rule:** Always pass the D2D delegate to `ConnectToServerAsync`. Never call `SetD2DApplyDelegate` separately after connection. See Software Architecture doc for details.

### FIXED: `TrayViewModel.Connect()` async anti-pattern
Was `void Connect()` using `Task.Delay(2000).ContinueWith(async _ => {...})`. The `async` lambda inside `ContinueWith` returns `Task<Task>` — the outer task completes when the lambda starts, not when the body finishes. Exceptions were silently swallowed.

**Fix:** Changed to `async Task Connect()` with `await Task.Delay(2000)`.

### Tray menu simplified
Removed "Server Mode" submenu (Start Server / Stop Server) — server machine always opens main window. Removed "Client Mode" submenu — Connect and Disconnect are now top-level tray items.

---

## 2026-03-20 — Animation Mode Fix + Logging Diagnostics

- **FIXED: Distribution mode ignored** — `StartCrossScreen()` was always starting all monitors in spanning mode (regression from D2D architecture refactor). Now checks `DistributionMode` and passes `perMonitorMode` flag to `D2DCompositionService.InitializeAsync()`.
- **FIXED: File logging silent failure** — `FileLoggerProvider.GetWriter()` had empty `catch` block that swallowed all file creation errors. Now logs errors to `Console.Error`.
- **Added: Logging diagnostics** — `AppLogger.ApplyConfig()` attempts file creation when `LogToFile=true` and reports success/failure to console.
- **FIXED: CrossScreenConfigDialog PlatformImpl null** — Added input event guards for Avalonia timing issue.
- **Added: Architectural Invariants section** in `CLAUDE.md`.

## 2026-03-17 — UI Redesign: Animation Controls & Gallery

- **FIXED: Background color (ISSUE-011)** — `BackgroundColorDetector` auto-detects dominant edge color; manual hex override with preview.
- **Gallery selection highlighting** — Blue border (`#0078D4`) via `Classes.selected` binding.
- **Removed confusing Animation toggle** — Standard wallpaper controls always visible; added "Multi Monitor Animation" button.
- **"From Gallery..." stubs replaced** — Dialog auto-populates from `PreSelectedWallpaper`.
- **Topology animation indicators** — Green (`#00AA44`) for animating, gold (`#FFD700`) for current target.
- **Active Animation Info Panel** — Shows file, mode, speed, background color.
- **Clear Wallpaper command** — Stops animations, disposes all D2D/LibVLC renderers.
- **Logging UI complete (TASK-012)** — Settings panel with log level, component toggles, file output.

## 2026-02-26 — Native D2D Composition (All P0 Fixed)

- **Native D2D Composition** — Replaced GDI+ pipeline with pure Direct2D for GIF rendering in Player.D2D.
- **Persistent ID2D1DeviceContext** — No per-frame render target recreation.
- **GPU-resident GIF frames** — Magick.NET → `ID2D1Bitmap[]` (zero per-frame allocation).
- **FIXED: GIF speed (ISSUE-004)** — SpeedMultiplier applied to elapsed time.
- **FIXED: GIF looping (ISSUE-005)** — Modulo-based seamless infinite looping.
- **FIXED: Memory leak (ISSUE-007)** — No GDI+ Bitmap allocation in render loop.
- **FIXED: High CPU (TASK-009)** — No GDI+→D2D conversion per frame.
- **FIXED: UI freeze (ISSUE-002)** — Proper disposal via `DisposeNativeD2DResources()`.
- **Security:** Magick.NET upgraded to 14.10.3 (fixes 36 Dependabot alerts).

## 2026-02-05 — DXGI Flip Model + Explorer Crash Fixes

- **FIXED: Only first frame visible** — D2D render target recreated after every `Present()` for FlipSequential buffer rotation.
- **FIXED: Explorer crash on loop #5** — Replaced `WS_EX_TRANSPARENT` with `WS_EX_LAYERED + SetLayeredWindowAttributes`.
- **FIXED: Player crash on GIF loop #4-5** — Removed synchronous Stop/Play from `OnMediaEndReached`; uses seek-based looping.

## 2025-12-27 — Separate Player Process (Windows 11 24H2+)

- **CRITICAL FIX:** DXGI swap chain windows crash explorer when parented to desktop on 24H2+.
- **New `WaBiBaBuSy.Player.D2D` project** — Runs DXGI rendering in isolated process.
- **IPC Protocol** — stdin/stdout JSON for PARENT, COLOR, LOAD, EXIT commands.
- **D2DPlayerHost** — Spawns player processes, manages lifecycle.

## 2025-12-24 — Native Win32 Architecture

- **Native Win32 window creation** — Eliminated Windows Forms dependency that caused freezing.
- **D2DVorticeRenderer rewrite** — `CreateWindowEx`, `RegisterClassEx`, native window procedure.
- **WS_EX_TRANSPARENT** — Mouse clicks pass through to desktop icons.

## 2025-12-11 — Direct2D Architecture Fix

- **Windows 11 24H2+ compatibility** — D2D renderer creates dedicated window instead of `GetDC(WorkerW)`.
- **HeadlessMode** — `WallpaperConfig.HeadlessMode` for composition pipeline (no window).
- **Timing sync fixed** — Animation position and frame selection use same elapsed time.
- **Timestamp overflow fixed** — No more `int.MinValue` overflow.

## 2025-11-07 — GIF & Video Support

- **GIF animation** — `GetFrameAtPosition()` with frame timing calculation.
- **Video playback** — Frame caching + LRU eviction (MVP uses placeholder frames).

## 2025-11-03 to 2025-11-05 — Distributed Animation System

- **Phases 1-4 complete** — Animation distribution, timing sync, sequential/simultaneous modes, UI integration.
- **Gallery-based selection** — Multi-select dialog for animation + background.
- **Auto-update system** — Version detection, chunked download, SHA-256 verification, standalone updater.
- **LibVLC pre-initialization** — Startup time 9s → instant.
- **Network topology** — Rectangle drag + Ctrl+Click multi-select.

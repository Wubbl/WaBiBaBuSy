# Scene Presets, Import/Export, Tray Quick Start, Starter Presets — Design

**Created:** 2026-10-03 · **Status:** approved, implementation pending · **Roadmap:** Tier 2.2 (rollout step 4 of
[authoring UX & preview](2026-09-17-authoring-ux-and-preview-design.md) §4; steps 1–3 and 5 are done)

## Goal

Make a good scene one click away: save the editor's scene as a named preset, load it back, share it with the club
as a file, add it to a playlist, and start it from the tray on all seats without opening the control panel. A fresh
install comes with curated starter presets (the showcase scenes).

## Decisions

- **The room for Quick start is built by the host.** After an app restart nobody has opened the control panel, so
  `ServerPlaybackHost` has no room (`UpdateRoom` is only called by the window). The room build moves into a pure
  builder that the window and the host both use.
- **Quick start plays on what is there:** server running → the server's monitors + connected clients; otherwise this
  machine's monitors. It never starts the server. Hidden in client mode (connected to another server).
- **Seeding** runs when the presets folder holds no `*.json` (spec: "if empty"). Deleting every preset therefore
  brings the starters back on the next launch.

## 1. Format

A preset **is a scene file**: a `CrossScreenConfig` serialized with `SceneFile.JsonOptions` (PascalCase, enum
names) plus indentation, read back with `SceneFile.Load` (comments, trailing commas, relative asset paths resolved
against the file's folder, missing assets reported, UNC rejected). Any showcase scene is a valid preset file and an
exported preset is a valid scene file. `SelectedMonitorIds` is cleared on every save / import / export — targets
belong to the room.

## 2. Assets

| Operation | Assets | Paths in the JSON |
|---|---|---|
| Save as preset (editor) | referenced in place | absolute (gallery files are the user's own library on this machine; copying a multi-GB video into every preset wastes disk); paths already under `presets\` are written relative |
| Import / starter seeding | copied into `presets\assets\` | `assets/<file>` relative to the preset |
| Export | copied into `<export dir>\assets\` next to `<name>.json` | `assets/<file>` |

**Why copy on import / seed / export:** the source (install folder, repo checkout, USB stick, Downloads) can move,
be updated or vanish; a relative `assets/` folder keeps the presets folder — and an export — self-contained and
movable to another server, while staying a valid scene file because `SceneFile.Load` resolves relative paths.

`PresetBundle` (Models) does the copy + rewrite: for each path from `SceneAssets.CollectPaths`, copy to
`<dir>\assets\<file name>`; an existing file with identical content is reused; a different file with the same name
gets `<stem>-<hash8><ext>` (hash from `ContentIdentity`). A missing asset is an error naming the file.

## 3. Store and seeding (Core / Models, unit-tested)

- `PresetStore` (`WaBiBaBuSy.Core/Services/Animation`, next to `PlaylistStore`), folder
  `%APPDATA%\WaBiBaBuSy\presets\` (override for tests): `List()` (names, alphabetical), `PathFor(name)`,
  `Exists(name)`, `Load(name)`, `Save(name, config)`, `Import(sourceFile, name?)` → saved name,
  `Export(name or config, targetFile)`, `Delete(name)`, `UniqueName(name)` ("Comet (2)"). Name sanitization as in
  `PlaylistStore`.
- `PresetSeeder.SeedIfEmpty(store, starterDir)`: reads `starterDir\starter-presets.json`
  (`{ "presets": [ { "name": "...", "scene": "scenes/xx.json" } ] }`) and imports each entry; a bad entry is logged
  and skipped; returns the number seeded. Called once at startup by the tray with `AppContext.BaseDirectory\Presets`.

## 4. Starter presets

Source: `TestScenarios/showcase/` — new manifest `starter-presets.json`, the listed `scenes/*.json` and `assets/`.
The UI csproj already ships `TestScenarios\**` next to the exe (test mode), so the seeder reads
`<AppBase>\TestScenarios\showcase\starter-presets.json` directly — no second copy; the installer ships `publish\*`
recursively. `12-lunar-tears` is excluded (absolute path to a 70 MB video outside the repo).

Mapping of the authoring design's list:

| Design | Scene | |
|---|---|---|
| Mexican Wave | `04-la-ola` | existing |
| Orbit | `06-cat-orbit-wave` | existing |
| Logo Rain | `01-rainbow-conveyor` | existing — Endless scrolling is horizontal only (`Player.D2D/Program.cs` Endless branch), so "rain" is a conveyor |
| Ring Fish | `14-ring-cat` | **new**: `cat.gif`, Linear, `FaceTravelDirection`, trail; the closed loop comes from the room's Ring traversal |
| Party Colors | `15-party-colors` | **new**: static centred logo, fast `CycleColorList`, Simultaneous |

All other showcase scenes (incl. `13-comet` with trail + bezel effects) ship too — 14 starters.

## 5. Room builder

- `LocalMonitor` record (Models/Topology): index, size, position, primary, device name, refresh Hz, px/cm.
- `RoomNodeSources` (Core, pure): builds the node list the window builds today —
  server mode: `SERVER_LOCALHOST_MONITOR_i` (persisted order, else i; persisted gap, else session gap) then the
  connected clients, multi-monitor clients expanded into `<id>_MONITOR_m` nodes (persisted order/gap); standalone:
  `LOCAL_MACHINE_MONITOR_i` (session gaps). `ToRoomNode(client)` maps a node to a `RoomNode` including the monitor
  suffix → monitor info parse. The window's `RefreshTopology` / `ShowLocalMachineNode` / `UpdateClientList` use it.
- `LocalMonitors` (UI/Services): WinForms + native enumeration → `LocalMonitor` list; `ReferencePixelsPerCm`
  (moved from the view model).
- Session gaps move from `MainWindowViewModel._sessionGapCm` into the host (app-lifetime; survive window close).
- `ServerPlaybackHost`: the window's `Detach()` releases the pushed room; `EnsureRoom()` rebuilds from sources
  (`RoomNodeSources` + `LocalMonitors` + `SeatMapStore.Load()`) when no window holds it or it is empty. Quick start
  calls it. Show items and Play are unchanged.

## 6. UI

- **Scene editor footer:** "Presets ▾" (load: replaces the draft, keeps the room's target selection),
  "Save as preset…" (name prompt; asks before overwriting), "Export…" / "Import…" (Avalonia file pickers). Failures
  go to a status line.
- **Playlist tab:** "＋ Add ▾" → "Current scene" / "From preset ▸ <names>" (adds a copy, targets = all seats).
- **Tray:** "Quick start ▸ <presets>", rebuilt when the menu opens. Click → load, targets = all, `EnsureRoom`, Play
  guard, `PlaySceneAsync` (stops a running show / scene like ▶ Play). An open window follows via host events.

## 7. Tests

`PresetStoreTests` (roundtrip, sanitization, unique names, alphabetical list, targets stripped, import/export),
`PresetBundleTests` (copy + rewrite, dedupe, hash suffix on clash, missing asset), `PresetSeederTests` (empty vs.
non-empty, bad entry skipped, the real manifest seeded into a temp folder and every preset loads),
`RoomNodeSourcesTests` (server / standalone / expanded multi-monitor, order and gaps, `ToRoomNode`). UI pieces are
manual (`.docs/plans/2026-10-04-manual-test-checklist.md`).

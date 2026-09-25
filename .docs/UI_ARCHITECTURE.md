# WaBiBaBuSy - UI Architecture

**Last Updated:** 2026-09-25
**Framework:** Avalonia 12.1.2 + CommunityToolkit.Mvvm
**Pattern:** MVVM (Model-View-ViewModel)

---

## Overview

WaBiBaBuSy runs as a **system tray application**. The main window is hidden by default. Users interact through the tray icon context menu and modal dialogs. The server control panel (topology + gallery + controls) is integrated into `MainWindow`.

---

## Views

| View | File | Purpose |
|------|------|---------|
| **Main Window** | `MainWindow.axaml` | Room-first server control panel: one-row toolbar, `RoomView` (live scene in every node tile), selection bar, right panel `[Scene \| Playlist]`. Hidden by default, shown from tray. |
| **Settings** | `SettingsWindow.axaml` | Server/client config, ports, directories, logging toggles |

> **Note:** Server topology management and client connection are integrated into `MainWindow`, not separate windows.

---

## ViewModels

| ViewModel | Responsibility |
|-----------|---------------|
| `MainWindowViewModel` | Primary UI logic: gallery, topology, playback controls, animation start/stop, D2D composition lifecycle; implements `IRoomHost` for `RoomView` (selection, drag/split/merge, per-node clear/resync, gap edits) |
| `TrayViewModel` | System tray context menu: Start/Stop Server, Connect, Settings, Exit |
| `SettingsViewModel` | Configuration load/save, logging settings (level, component toggles, file output) |
| `CrossScreenConfigViewModel` | Animation mode selection, distance settings; backs the Scene tab's `SceneEditorPanel` |
| `PlaylistViewModel` | Playlist load/save, item durations, drag reorder, Duplicate/Remove; backs the Playlist tab's `PlaylistPanel` |
| `WallpaperItemViewModel` | Single gallery item (thumbnail, name, type, selection state) |
| `ClientNodeViewModel` | Topology node (client name, status, animation indicators) |
| `ViewModelBase` | Base class with `INotifyPropertyChanged` |

---

## Key UI Features

### System Tray
- Minimized operation with context menu
- Start/Stop Server, Connect to Server, Settings, Exit
- Main window toggled from tray

### Server Control Panel (MainWindow) — room-first (UI redesign Plan 1, 2026-09-24; docked editor + Playlist tab, Plan 2, 2026-09-25)
- **Toolbar (one row)**: role/status (left) · show controls — `Show ▾` picker, `▶`/`■`, `⏭` (skip,
  `PlaylistOrchestrator.Skip()`), "Now: … · next: …" label, `Clear all` (center) · `⋯` flyout for
  connect-to-server, `Settings…`, `Open log folder` and a "Developer tools" expander (composition
  mode, D2D/LibVLC apply, background color, debug overlay toggles) (right)
- **Room ⚙ popover**: room-wide layout — traversal path (Ring/Snake/Parallel), turn gap, row gap,
  physical units toggle, vertical alignment, and "Split evenly into N rows"
- **`RoomView`** (`Controls/RoomView.cs` + `RoomView.Menus.cs`): the room itself — row lanes with
  node tiles, each tile painting the running scene or the docked editor's draft
  (`ScenePainter`/`SceneClock`); drag a tile between rows/positions, right-click a tile or a row
  header for context menus (split/merge row, move row, rename, toggle facing, per-node
  Clear/Resync/logs); an empty "+ new row" drop zone below the last lane. Talks to the view model
  only through `IRoomHost` (`Controls/IRoomHost.cs`), so the control itself has no
  networking/persistence code. Row/tile geometry and hit-testing come from `RoomGrid`
  (`WaBiBaBuSy.Models/Topology/RoomGrid.cs`); row edits (split, merge, move, reorder) are pure
  functions in `SeatMapEditor` (`WaBiBaBuSy.Models/Topology/SeatMapEditor.cs`), which normalizes
  rows back into the seat map that `SeatMapLayoutBuilder` lays out.
- **Selection bar**: appears while nodes are selected — summary, Clear, Resync (server mode),
  Logs (single node), "Make row from selection" (multi-select), Deselect
- **Right panel `[Scene | Playlist]`**: replaces the gallery pane and the old modal dialogs.
  - **Scene tab** (`SceneEditorPanel` over `CrossScreenConfigViewModel`): gallery strip (click =
    primary file, Ctrl+click = extra image, right-click = remove); tabs Content/Motion/Look/
    Background with Advanced expanders; ⓘ tooltips; amber validation chips (`SceneChecks`); units
    follow the Room ⚙ physical-units toggle; targets = the room selection (`SceneTargets`, chain
    order); footer Play on selection / Play on all / Save to playlist ▾ / Revert — Play on
    selection / Play on all first stop a running scene; a running show is stopped and every node
    cleared before the scene starts. Draft preview: editor changes reach the room tiles after
    150 ms; a preview bar under the room shows a draft/live badge, ⏸, ⟲, and 1×/4×/16× for the
    draft clock.
  - **Playlist tab** (`PlaylistPanel` over `PlaylistViewModel`): picker/New/Save, name, Loop/
    Shuffle, default seconds, seconds per item, drag ≡ to reorder, right-click Duplicate/Remove,
    Details expander with lap-snap, ＋ Add current scene; selecting an item loads it into the
    Scene tab.

### Background Color
- Auto-detected from edge pixels of the animation file (`BackgroundColorDetector`)
- Manual hex override with inline color preview
- Applied to D2D player background layer

### Gallery Selection
- Blue border on selected item via `Classes.selected` binding
- `HexToColorConverter` for dynamic color rendering

---

## Custom Controls

| Control | File | Purpose |
|---------|------|---------|
| `RoomView` | `Controls/RoomView.cs`, `RoomView.Menus.cs` | The main window's room: row lanes of node tiles laid out by `RoomGrid`, each tile painted with the live running scene via `ScenePainter`/`SceneClock`. Drag a tile between rows, right-click menus (split/merge/move row, rename, facing, per-node clear/resync/logs). Reads/writes exclusively through `IRoomHost`. |
| `ScenePainter` / `SceneClock` | `Controls/ScenePainter.cs`, `Controls/SceneClock.cs` | `SceneClock` resolves elapsed time (design clock or live shared clock); `ScenePainter` draws sprite/pattern for a scene + layout at that time using the Models' pure math (`MovementCalculator`, `NodeMapping`, `PatternLayout`, `ColorGrader`, `SyncTiming`). Shared by `RoomView` (running scene or the Scene tab's draft) and the Scene tab's own preview bar. |
| `SceneEditorPanel` | `Views/SceneEditorPanel.axaml.cs` | Docked Scene tab: gallery strip, Content/Motion/Look/Background tabs with Advanced expanders, validation chips (`SceneChecks`), footer Play on selection/all, Save to playlist, Revert. Drives the room's draft preview instead of a standalone preview control. |
| `PlaylistPanel` | `Views/PlaylistPanel.axaml.cs` | Docked Playlist tab: picker/New/Save, durations (`PlaylistEditing`), drag reorder, Duplicate/Remove, Details expander. Selecting an item loads it into `SceneEditorPanel`. |

## Value Converters

| Converter | Purpose |
|-----------|---------|
| `BoolToTextConverter` | Boolean to display text |
| `HexToColorConverter` | Hex string → Avalonia Color for XAML bindings |

---

## Services (UI Layer)

| Service | Purpose |
|---------|---------|
| `VideoThumbnailGenerator` | Extracts video thumbnails for gallery display |

---

## User Flows

### Starting as Server
1. Tray icon → "Start Server"
2. Server binds to gRPC port, starts mDNS advertisement
3. Open Main Window → see topology panel
4. Select wallpaper from gallery → Set as wallpaper or start animation

### Starting as Client
1. Tray icon → "Connect to Server"
2. Enter server IP or browse mDNS discovery
3. Client registers with server (version info sent)
4. Server pushes sync commands, client renders locally

### Starting Multi-Monitor Animation
1. Open the Scene tab in the right panel
2. Click a file in the gallery strip (Ctrl+click for extra images) to build the scene
3. Configure distribution mode (Sequential/Simultaneous), speed, movement, background color across
   the Content/Motion/Look/Background tabs — the room tiles preview the draft as fields change
4. Footer "Play on all" → `D2DCompositionService` spawns player processes per monitor; the preview
   badge switches from draft to live

---

## Architecture Notes

- **No code-behind** in views — all logic in ViewModels
- **CommunityToolkit.Mvvm** for `[ObservableProperty]`, `[RelayCommand]`
- **Avalonia Fluent Theme** for consistent Windows 11 styling
- **No Windows Forms dependency** — pure Avalonia + native Win32 for rendering

# WaBiBaBuSy - UI Architecture

**Last Updated:** 2026-09-24
**Framework:** Avalonia 12.1.2 + CommunityToolkit.Mvvm
**Pattern:** MVVM (Model-View-ViewModel)

---

## Overview

WaBiBaBuSy runs as a **system tray application**. The main window is hidden by default. Users interact through the tray icon context menu and modal dialogs. The server control panel (topology + gallery + controls) is integrated into `MainWindow`.

---

## Views

| View | File | Purpose |
|------|------|---------|
| **Main Window** | `MainWindow.axaml` | Room-first server control panel: one-row toolbar, `RoomView` (live scene in every node tile), selection bar, wallpaper gallery. Hidden by default, shown from tray. |
| **Settings** | `SettingsWindow.axaml` | Server/client config, ports, directories, logging toggles |
| **Cross-Screen Config** | `CrossScreenConfigDialog.axaml` | Animation distribution mode picker (Sequential vs Simultaneous) |
| **Multi-Select Dialog** | `WallpaperMultiSelectDialog.axaml` | Gallery-based wallpaper + background selection |

> **Note:** Server topology management and client connection are integrated into `MainWindow`, not separate windows.

---

## ViewModels

| ViewModel | Responsibility |
|-----------|---------------|
| `MainWindowViewModel` | Primary UI logic: gallery, topology, playback controls, animation start/stop, D2D composition lifecycle; implements `IRoomHost` for `RoomView` (selection, drag/split/merge, per-node clear/resync, gap edits) |
| `TrayViewModel` | System tray context menu: Start/Stop Server, Connect, Settings, Exit |
| `SettingsViewModel` | Configuration load/save, logging settings (level, component toggles, file output) |
| `CrossScreenConfigViewModel` | Animation mode selection, distance settings |
| `WallpaperMultiSelectDialogViewModel` | Gallery multi-select with filtering |
| `WallpaperItemViewModel` | Single gallery item (thumbnail, name, type, selection state) |
| `ClientNodeViewModel` | Topology node (client name, status, animation indicators) |
| `ViewModelBase` | Base class with `INotifyPropertyChanged` |

---

## Key UI Features

### System Tray
- Minimized operation with context menu
- Start/Stop Server, Connect to Server, Settings, Exit
- Main window toggled from tray

### Server Control Panel (MainWindow) — room-first (UI redesign Plan 1, 2026-09-24)
- **Toolbar (one row)**: role/status (left) · show controls — `Scene…`, `▶ Start`/`■ Stop`,
  `Playlist…`, playlist next-label, `Clear all` (center) · `⋯` flyout for connect-to-server,
  `Settings…`, `Open log folder` and a "Developer tools" expander (composition mode, D2D/LibVLC
  apply, background color, debug overlay toggles) (right)
- **Room ⚙ popover**: room-wide layout — traversal path (Ring/Snake/Parallel), turn gap, row gap,
  physical units toggle, vertical alignment, and "Split evenly into N rows"
- **`RoomView`** (`Controls/RoomView.cs` + `RoomView.Menus.cs`): the room itself — row lanes with
  node tiles, each tile painting the live running scene (`ScenePainter`/`SceneClock`); drag a tile
  between rows/positions, right-click a tile or a row header for context menus (split/merge row,
  move row, rename, toggle facing, per-node Clear/Resync/logs); an empty "+ new row" drop zone
  below the last lane. Talks to the view model only through `IRoomHost` (`Controls/IRoomHost.cs`),
  so the control itself has no networking/persistence code. Row/tile geometry and hit-testing come
  from `RoomGrid` (`WaBiBaBuSy.Models/Topology/RoomGrid.cs`); row edits (split, merge, move,
  reorder) are pure functions in `SeatMapEditor` (`WaBiBaBuSy.Models/Topology/SeatMapEditor.cs`),
  which normalizes rows back into the seat map that `SeatMapLayoutBuilder` lays out.
- **Selection bar**: appears while nodes are selected — summary, Clear, Resync (server mode),
  Logs (single node), "Make row from selection" (multi-select), Deselect
- **Wallpaper Gallery**: thumbnails with blue selection border (`#0078D4`); "Selected Wallpaper"
  preview pane
- **Playback Controls**: `Scene…` opens `CrossScreenConfigDialog`; Start/Stop drive
  `D2DCompositionService`

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
| `ScenePainter` / `SceneClock` | `Controls/ScenePainter.cs`, `Controls/SceneClock.cs` | Extracted from `ScenePreviewControl`: `SceneClock` resolves elapsed time (design clock or live shared clock); `ScenePainter` draws sprite/pattern for a scene + layout at that time using the Models' pure math (`MovementCalculator`, `NodeMapping`, `PatternLayout`, `ColorGrader`, `SyncTiming`). Shared by `ScenePreviewControl` and `RoomView`. |
| `ScenePreviewControl` | `Controls/ScenePreviewControl.cs` | Live preview of a `CrossScreenConfig` over a `SeatMapLayoutResult`, built on `ScenePainter`/`SceneClock`. Still used by `CrossScreenConfigDialog` (design clock, pause / 1×-4×-16×); the main window now uses `RoomView` instead. |

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
1. Select wallpaper in gallery (Video or GIF)
2. Click "Scene…" in the toolbar
3. `CrossScreenConfigDialog` opens → pick Sequential or Simultaneous
4. Configure speed, movement direction, background color
5. Start → `D2DCompositionService` spawns player processes per monitor

---

## Architecture Notes

- **No code-behind** in views — all logic in ViewModels
- **CommunityToolkit.Mvvm** for `[ObservableProperty]`, `[RelayCommand]`
- **Avalonia Fluent Theme** for consistent Windows 11 styling
- **No Windows Forms dependency** — pure Avalonia + native Win32 for rendering

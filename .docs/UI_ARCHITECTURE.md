# WaBiBaBuSy - UI Architecture

**Last Updated:** 2026-03-24
**Framework:** Avalonia 11.x + CommunityToolkit.Mvvm
**Pattern:** MVVM (Model-View-ViewModel)

---

## Overview

WaBiBaBuSy runs as a **system tray application**. The main window is hidden by default. Users interact through the tray icon context menu and modal dialogs. The server control panel (topology + gallery + controls) is integrated into `MainWindow`.

---

## Views

| View | File | Purpose |
|------|------|---------|
| **Main Window** | `MainWindow.axaml` | Server control panel with topology visualization, wallpaper gallery, playback controls, and animation info. Hidden by default, shown from tray. |
| **Settings** | `SettingsWindow.axaml` | Server/client config, ports, directories, logging toggles |
| **Cross-Screen Config** | `CrossScreenConfigDialog.axaml` | Animation distribution mode picker (Sequential vs Simultaneous) |
| **Multi-Select Dialog** | `WallpaperMultiSelectDialog.axaml` | Gallery-based wallpaper + background selection |

> **Note:** Server topology management and client connection are integrated into `MainWindow`, not separate windows.

---

## ViewModels

| ViewModel | Responsibility |
|-----------|---------------|
| `MainWindowViewModel` | Primary UI logic: gallery, topology, playback controls, animation start/stop, D2D composition lifecycle |
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

### Server Control Panel (MainWindow)
- **Topology Visualization**: Drag-and-drop client ordering, rectangle selection, Ctrl+Click multi-select
- **Animation Indicators**: Green border for animating clients, gold for current target
- **Wallpaper Gallery**: Thumbnails with blue selection border (`#0078D4`)
- **Playback Controls**: Load, Play, Pause, Seek, Stop, Clear
- **Multi-Monitor Animation Button**: Opens `CrossScreenConfigDialog`
- **Active Animation Info Panel**: Shows file name, distribution mode, speed, background color

### Background Color
- Auto-detected from edge pixels of the animation file (`BackgroundColorDetector`)
- Manual hex override with inline color preview
- Applied to D2D player background layer

### Gallery Selection
- Blue border on selected item via `Classes.selected` binding
- `HexToColorConverter` for dynamic color rendering

---

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
2. Click "Multi Monitor Animation"
3. `CrossScreenConfigDialog` opens → pick Sequential or Simultaneous
4. Configure speed, movement direction, background color
5. Start → `D2DCompositionService` spawns player processes per monitor

---

## Architecture Notes

- **No code-behind** in views — all logic in ViewModels
- **CommunityToolkit.Mvvm** for `[ObservableProperty]`, `[RelayCommand]`
- **Avalonia Fluent Theme** for consistent Windows 11 styling
- **No Windows Forms dependency** — pure Avalonia + native Win32 for rendering

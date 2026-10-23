# Linux Client

**Status:** 2026-10-23: caught up with server 2.7.x: scene layers, show events, BiBaBo, agenda, transitions.
First version 2026-10 (built for the October LAN party), verified in a headless container under Mutter (GNOME's window
manager, X11 mode) and KWin (KDE, X11 mode). The 2.7 port is covered by offscreen render tests only; **not yet tested on a
real GNOME/KDE desktop or against a live server**.

A headless client that lets a Linux machine join a WaBiBaBuSy wall. The server stays on Windows
and all configuration happens in its UI. The Linux machine finds the server, connects, and renders
whatever the server starts, in sync with the Windows clients.

---

## Quick start

On the build machine (Windows or Linux, .NET 9 SDK):

```powershell
powershell -File tools\linux\publish-client.ps1        # or: bash tools/linux/publish-client.sh
```

That writes `publish-linux/wabibabusy-client-linux-x64.tar.gz` (~75 MB, self-contained: no .NET
needed on the Linux machines). It is deliberately not written to `publish/`, because the Windows installer
packs that whole folder.

On each Linux machine:

```bash
tar xzf wabibabusy-client-linux-x64.tar.gz
bash wabibabusy-client-linux-x64/packaging/install.sh          # add --args "--server 192.168.1.10" to skip discovery
wabibabusy-client --demo                                        # check placement without a server, Ctrl+C to quit
```

`install.sh` copies the program to `~/.local/share/wabibabusy-client`, links
`~/.local/bin/wabibabusy-client`, and adds an autostart entry. `--uninstall` removes everything.

### Packages

The client needs X11 libraries. They are present on any desktop install; on minimal systems install them with:

| Distro | Command |
|--------|---------|
| Fedora | `sudo dnf install libX11 libXrandr libXext libICE libSM fontconfig` |
| Ubuntu/Debian | `sudo apt install libx11-6 libxrandr2 libxext6 libice6 libsm6 libfontconfig1` |

On Wayland sessions the client runs through **XWayland**, which GNOME and KDE enable by default.

### Network

- Server TCP **50051** must be reachable (same as for Windows clients).
- Auto-discovery needs mDNS, UDP **5353**: `sudo firewall-cmd --permanent --add-service=mdns && sudo firewall-cmd --reload`
  (Fedora) or `sudo ufw allow 5353/udp` (Ubuntu). Otherwise pass `--server <ip>`.
- Discovery also fails on machines with IPv6 fully disabled (`ipv6.disable=1`): the mDNS library
  always opens an IPv6 socket. Use `--server` there.
- Clocks should be NTP-synced, as on Windows: `timedatectl` should show *System clock synchronized: yes*.
  The client also applies the heartbeat-measured server clock offset (same code as Windows).

---

## Options

| Option | Meaning |
|--------|---------|
| `--server HOST[:PORT]` | Connect directly instead of mDNS discovery |
| `--placement desktop` | *(default)* `_NET_WM_WINDOW_TYPE_DESKTOP`: behind every app, like a real wallpaper |
| `--placement below` | Normal window kept below others; fallback if `desktop` misbehaves |
| `--placement normal` | Plain window, for debugging |
| `--demo` | Local test animation sweeping across all monitors; no server needed |
| `--software` | CPU rendering, if the GPU driver shows a black or garbled wallpaper |
| `--log-dir DIR` / `--verbose` | Logging |

---

## How it works

```
Windows server ──gRPC──► WaBiBaBuSy.Core (net9.0 build)      same networking/download/clock code as Windows
                              │ CrossScreenApplyRequest (scene_json + layout_json), EVENT, AGENDA
                              ▼
                         LinuxClientHost                      port of RemotePlaybackHost (+ AgendaDisplay, BombInputWatcher)
                              │ PlayerCommandLoadAnimation / StartAnimation (same messages the D2D player gets)
                              ▼
                         WallpaperScene (SkiaSharp)           one per monitor = one D2D player process
                           ├ SceneContent                     background (incl. Panorama) + SpriteLayer per scene layer
                           ├ ShowEventPainter (+ .Bomb)       port of Program.ShowEvents.cs / Program.Bomb.cs
                           └ AgendaPainter                    port of Program.Agenda.cs
                              │ uses MovementCalculator / PatternLayout / ColorGrader / SpriteEffects / NodeMapping /
                              │ ShowEventMath / BombMath / AgendaLayout / ZonePlanner
                              ▼
                         WallpaperWindow (Avalonia, X11)      one per monitor, EWMH desktop hints + click-through
```

- **Same math as Windows.** Positions, pattern cells, colours, effects, events and the BiBaBo geometry come from the
  shared `WaBiBaBuSy.Models` helpers. `ZonePlanner.cs` is compiled in via a link. The D2D 5×4 colour matrix maps
  one-to-one to `SKColorFilter.CreateColorMatrix` (a unit test covers the conversion).
- **Scene layers.** The server sends the whole scene (background + N layers, cm already resolved) and this seat's
  seat-map layout. Each layer has its own canvas (Sequential = the seat map, Simultaneous = this monitor), clock
  (`SyncTiming.LayerElapsed`: Wave phase + phase offset), opacity, spawn window and "one source per seat". The Windows
  player keeps per-layer state in statics it swaps; here each layer is a `SpriteLayer` object.
- **Version.** The client is stamped with the Windows app's version (read from `WaBiBaBuSy.UI.csproj`, or the
  newest `vX.Y.Z` git tag + commits since it), because the server only sends scenes to clients ≥ 2.7.0.
  It ignores update offers; update Linux seats by re-running the publish script.
- **Standby switch and transitions.** A new scene replaces the old one at the shared start. Until then the old one keeps
  playing. *Fade* dips through black around the start; *Wipe* shows the new scene behind a boundary running along the
  seat map. Windows does this with two player windows, Linux with one window drawing both scenes.
- **Show events.** Spotlight, Celebration, Traversal, Announce, Blackout and Identify are drawn above the scene,
  targeted at all seats, a tag or one seat. Text is rendered with the first available font of
  Segoe UI / Noto Sans / DejaVu Sans / Liberation Sans.
- **BiBaBo.** Bomb, cables, junction box, riddle cards, RULES, keypad, LED timers and BOOM / DEFUSED are all drawn. When
  the bomb lands on one of this machine's monitors, that wallpaper window stops being click-through until the game ends.
  Because it sits below every app, it only gets clicks on the bare desktop, like the Windows Raw Input sink.
  A click on a cable's cut zone or a keypad key, and digits / Backspace / Delete / C typed after clicking the desktop,
  go to the server (`ReportShowAction`), which referees. The heartbeat reports *busy* monitors (an app window covers
  ≥ 80 %), so the server skips them.
  Not on Linux: the Recycle Bin trick (there is no Recycle Bin icon to drag).
- **Agenda.** The corner panel, or the full timetable on monitors tagged `agenda`, drawn on every monitor. A monitor
  without a scene gets a stand-in that shows this desktop's own wallpaper (GNOME: `gsettings`; KDE: the Plasma applet
  config; otherwise the scene's fallback image).
- **Monitors.** XRandR monitors are reported left to right, exactly like the Windows client. The
  server's `TargetMonitorIndex` indexes that list.
- **Desktop placement (replaces WorkerW).** The window gets `_NET_WM_WINDOW_TYPE_DESKTOP` plus
  sticky, skip-taskbar and skip-pager, and an empty XShape input region so clicks reach the desktop and apps
  below. In desktop mode it is *raised within the desktop layer* and re-raised every 0.5 s if another
  desktop-type window ends up above it. Without that, KDE's plasmashell desktop, which is itself a
  desktop-type window, would cover the wallpaper.
- **Pause on fullscreen.** Every 2 s it checks which monitors a fullscreen window covers. By default any fullscreen app
  pauses every monitor; with `PauseOnlyFullscreenMonitor` in the client config only the covered one pauses
  (`PauseOnFullscreen` turns it off). Time keeps running, so playback resumes in sync. Only X11/XWayland
  windows are visible to this check (and to the BiBaBo busy check).
- **Stop** drops the scenes; monitors without a scene or agenda stand-in are hidden, so the normal wallpaper shows again.
- **Single process.** The Windows "separate player process" rule exists because of an
  `explorer.exe` crash on 24H2. That does not apply on Linux, so rendering runs in-process.

### Desktop environments

| Desktop | Expected behaviour |
|---------|--------------------|
| KDE Plasma (X11 or Wayland) | Wallpaper sits above Plasma's own wallpaper, below all apps. Desktop icons are covered visually but stay clickable. |
| GNOME (X11 or Wayland) | Wallpaper sits in Mutter's desktop layer, below all apps. If it does not stay behind windows, install the fallback extension: `install.sh --gnome-extension`, log out and back in. |
| Other X11 WMs (Xfce, Cinnamon, i3…) | Most honour `_NET_WM_WINDOW_TYPE_DESKTOP`; try `--placement below` if not. |

Fallback for KDE without code changes: *System Settings → Window Management → Window Rules*, add a rule
for window class `wabibabusy-wallpaper`, and set *Window type: Desktop* (Force).

---

## Not supported yet

| Feature | Linux client behaviour |
|---------|------------------------|
| Video layers (MP4/MKV/…) | Not drawn (the rest of the scene plays), with a warning in the log. Next step: LibVLCSharp with system libvlc (`vlc-libs` / `libvlc5`) |
| IconZone with real desktop icons | Runs as if the desktop had no icons (GNOME shows none by default; KDE's positions are private to plasmashell) |
| BiBaBo Recycle Bin | No Recycle Bin icon on Linux desktops; cut / keypad work |
| Native Wayland windows | Not seen by the fullscreen-pause and busy-seat checks (XWayland windows are) |
| Zone-fade masks, F11 debug overlay, test-mode probes, live thumbnails | Not ported |
| Auto-update | Ignored (the server's package is the Windows build) |
| Running as the server | Windows only; the UI is not ported |
| Multi-image additional sources | Static images only, same as the D2D player |

## Testing

- `dotnet test WaBiBaBuSy.Client.Linux.Tests` (offscreen Skia renders) covers:
  - colour-matrix conversion and movement parity with `MovementCalculator`
  - seam continuity across two seats, Simultaneous layers, layer order / opacity / spawn window, one source per seat
  - Panorama slices, the standby switch, Fade and Wipe transitions
  - GIF timing (0-delay rule, no 1 s cap), traveling-colour patterns, sprite effects, video fallback
  - blackout and targeted events, every other event type, full BiBaBo games in all three modes
  - BiBaBo hit-testing on the drawn terminals, the agenda panel (hidden under a blackout), text sprites, keypad keys
- Checked by hand (first version, before the 2.7 port) under Xvfb with Mutter and KWin, with a stand-in plasmashell desktop
  window: stacking, click-through, fullscreen pause, hide/re-show, and end to end against a throwaway headless server
  built from `WaBiBaBuSy.Core`.
- Still to check on real hardware: everything from the 2.7 port against a live Windows server (scene layers,
  transitions, events, BiBaBo clicks / keys on the bomb seat, agenda), GNOME and KDE **Wayland** sessions (XWayland),
  HiDPI scaling, NVIDIA drivers, and multi-monitor machines.

# Linux Client

**Status:** first version, 2026-10 (built for the October LAN party). Verified in a headless
container under Mutter (GNOME's window manager, X11 mode) and KWin (KDE, X11 mode); **not yet tested on a real
GNOME/KDE Wayland desktop**.

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
                              │ CrossScreenApplyRequest
                              ▼
                         LinuxClientHost                      port of MainWindowViewModel.ApplyCrossScreenD2DFromRemoteAsync
                              │ PlayerCommandLoadAnimation / StartAnimation (same messages the D2D player gets)
                              ▼
                         WallpaperScene (SkiaSharp)           port of Player.D2D/Program.cs drawing code
                              │ uses MovementCalculator / PatternLayout / ColorGrader / ZonePlanner
                              ▼
                         WallpaperWindow (Avalonia, X11)      one per monitor, EWMH desktop hints + click-through
```

- **Same math as Windows.** Positions, pattern cells and colors come from the shared
  `WaBiBaBuSy.Models` helpers. `ZonePlanner.cs` is compiled in via a link. The D2D 5×4 color matrix maps
  one-to-one to `SKColorFilter.CreateColorMatrix` (a unit test covers the conversion).
- **Monitors.** XRandR monitors are reported left to right, exactly like the Windows client. The
  server's `TargetMonitorIndex` indexes that list.
- **Desktop placement (replaces WorkerW).** The window gets `_NET_WM_WINDOW_TYPE_DESKTOP` plus
  sticky, skip-taskbar and skip-pager, and an empty XShape input region so clicks reach the desktop and apps
  below. In desktop mode it is *raised within the desktop layer* and re-raised every 0.5 s if another
  desktop-type window ends up above it. Without that, KDE's plasmashell desktop, which is itself a
  desktop-type window, would cover the wallpaper.
- **Fullscreen pause.** Every 2 s it checks whether the focused window is fullscreen and pauses
  drawing while it is. Time keeps running, so playback resumes in sync. Only X11/XWayland windows are visible
  to this check.
- **Stop** hides the windows, so the normal wallpaper shows again; the next start re-shows them.
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
| Video wallpapers (MP4/MKV/…) | Background only, with a warning in the log. Next step: LibVLCSharp with system libvlc (`vlc-libs` / `libvlc5`) |
| IconZone with real desktop icons | Runs as if the desktop had no icons (GNOME shows none by default; KDE's positions are private to plasmashell) |
| Zone-fade masks, F11 debug overlay, live thumbnails | Not ported |
| Running as the server | Windows only; the UI is not ported |
| Multi-image additional sources | Static images only, same as the D2D player |

One small, deliberate difference: with colour grading active, the D2D player draws the graded single
image at its native size (`DrawImage` at a point). The Skia port scales it to the layout size like the
ungraded path. The two only differ when grading is combined with a non-`Center` fit mode.

---

## Testing

- `dotnet test WaBiBaBuSy.Client.Linux.Tests`: offscreen Skia renders. Covers colour-matrix conversion,
  Linear movement parity with `MovementCalculator`, seam continuity across two monitors in
  Sequential mode, GIF frame timing and the 0-delay rule, opaque JPEG loading, traveling-colour patterns and video fallback.
- Checked by hand under Xvfb with Mutter and KWin (with a stand-in plasmashell desktop window): stacking,
  click-through, fullscreen pause, hide/re-show, and end to end against a throwaway headless server
  built from `WaBiBaBuSy.Core` (registration, content download, cross-screen start, stop,
  auto-reconnect).
- Still to check on real hardware: GNOME and KDE **Wayland** sessions (XWayland), HiDPI scaling, NVIDIA
  drivers, and multi-monitor machines.

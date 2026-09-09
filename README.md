# WaBiBaBuSy

**Synchronized animated wallpapers across every machine in the room.**

WaBiBaBuSy turns a wall of Windows PCs into one continuous animated canvas. One machine acts as the
server; every other machine renders the same wallpaper locally, in lockstep, so an animation can
drift across a whole row of monitors as if they were a single display.

Built by the **BiBaBu** crew for LAN parties, event walls, and offices where one screen simply isn't enough.

![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6)
![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

---

## Why it's different

Most wallpaper tools stream frames or fake synchronization with a "start now" command. WaBiBaBuSy
doesn't stream anything. Instead:

- The server broadcasts **animation metadata** — file, canvas geometry, movement type, seed, and a
  shared UTC start timestamp.
- Every client independently computes the exact position of the animation for the current moment
  from `elapsedTime` alone. Same math, same seed, same result.
- Nothing but a heartbeat travels over the wire after the initial handshake.

The result: **±50 ms drift**, **<1 Mbps of network traffic**, and 50+ clients on a server that idles
below 5% CPU.

---

## Features

**Sync & topology**
- Server/client architecture over gRPC with mDNS auto-discovery (no IP typing required)
- Visual network topology: order clients left-to-right, set physical distances and bezel gaps so the
  animation crosses the seams correctly
- Clock-offset compensation and drift telemetry per client
- Automatic reconnection with exponential backoff and session resume

**Rendering**
- Hardware-accelerated Direct2D rendering in a dedicated player process
- Images (JPG/PNG/BMP), video (MP4/AVI/MKV), and animated GIFs
- Multi-monitor: **Sequential** (one animation spanning all monitors) or **Simultaneous**
  (independent copy per monitor)

**Animation**
- 6 movement types: Static, Linear, Bounce, SineWave, Circular, RandomWalk — each with Reversed and
  Endless variants
- Pattern grid multiplier: tile the animation into an infinite deterministic world-space grid with
  spacing, jitter, rotation, and multi-image support
- 8 color-grading modes, including per-cell "Traveling Colors" that ripple across the grid
- Backgrounds: solid colour, stretched or tiled image, three-zone corridor, and **IconZone** — A*
  pathfinding that routes the animation around your real desktop icons

**Party mode**
- Playlists that rotate animation configs across every machine on a loop, with per-item durations,
  shuffle, and optional lap-snapping so a Linear animation restarts on a clean boundary

**Operations**
- System tray operation with a minimal footprint
- Auto-update with SHA-256 verification and rollback
- Inno Setup installer
- In-player debug overlay (F11): A* path, icon rectangles, zone bands, movement trail, info panel

---

## Requirements

- Windows 10 (1809+) or Windows 11
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) to build, or the .NET 9 Desktop
  Runtime to run a published build
- A GPU with Direct3D 11 / Direct2D support (any card from the last decade)
- All machines on the same LAN, with TCP **50051** reachable on the server

Optional: FFmpeg (`ffmpeg.exe` + `ffprobe.exe`) for video thumbnails — see
[`WaBiBaBuSy.UI/ffmpeg/README.md`](WaBiBaBuSy.UI/ffmpeg/README.md). Without it everything works,
you just don't get video previews in the picker.

---

## Quick start

```bash
git clone https://github.com/Pfnetsch/WaBiBaBuSy.git
cd WaBiBaBuSy
dotnet restore
dotnet build
dotnet run --project WaBiBaBuSy.UI
```

The app starts in the system tray.

**On the server machine**
1. Tray icon → **Start Server**
2. Tray icon → **Open Server Control Panel**
3. Add your clients, drag them into their physical left-to-right order, set the bezel gaps
4. Pick your media, choose a distribution mode, hit apply

**On each client machine**
1. Tray icon → **Connect to Server**
2. Use auto-discovery, or enter the server's IP directly

Configuration lives in `%APPDATA%\WaBiBaBuSy\`; logs in `%LOCALAPPDATA%\WaBiBaBuSy\Logs\`.

---

## Distribution modes

| Mode | What happens | Use it when |
|------|--------------|-------------|
| **Sequential** | All monitors form one large virtual canvas; the animation spans across them | Monitors are physically side by side and you want one continuous scene |
| **Simultaneous** | Each monitor gets its own independent canvas and its own copy of the animation | Monitors are apart, or you want the same effect mirrored everywhere |

Both modes start every player at the same instant with the same timestamp — the only difference is
how the virtual canvas width and per-monitor offset are computed.

---

## Repository layout

| Project | Role |
|---------|------|
| `WaBiBaBuSy.UI` | Avalonia desktop app, tray, server control panel, all dialogs |
| `WaBiBaBuSy.Core` | Services, orchestration, configuration |
| `WaBiBaBuSy.Models` | Configuration and animation models |
| `WaBiBaBuSy.Grpc` | Protobuf contracts and gRPC server/client |
| `WaBiBaBuSy.WallpaperEngine` | Win32 desktop integration, Direct2D composition |
| `WaBiBaBuSy.Player.D2D` | Standalone Direct2D player process |
| `WaBiBaBuSy.Player.Image` | Standalone WPF image/GIF player process |
| `WaBiBaBuSy.Player.Common` | Shared player IPC protocol |
| `WaBiBaBuSy.Updater` | Auto-update and rollback |
| `WaBiBaBuSy.Common` | Cross-cutting helpers and logging |
| `WaBiBaBuSy.Tests` | xUnit test suite |
| `Installer/` | Inno Setup script and build automation |

Rendering runs in **separate processes on purpose**: DXGI swap-chain windows parented to the desktop
inside the main process crash `explorer.exe` on Windows 11 24H2+. Please keep it that way.

---

## Architecture notes

Deeper documentation lives in [`.docs/`](.docs/):

- [Software Architecture](.docs/SOFTWARE_ARCHITECTURE.md) — full system design, networking, config reference
- [Direct2D Rendering Architecture](.docs/2025.12_DIRECT2D_RENDERING_ARCHITECTURE.md) — read this before touching rendering
- [Distributed Animation System](.docs/2025.11_DISTRIBUTED_ANIMATION_SYSTEM.md) — how the sync actually works
- [Animation Movement System](.docs/ANIMATION_MOVEMENT_SYSTEM.md) — deterministic position math
- [UI Architecture](.docs/UI_ARCHITECTURE.md) — views, view models, user flows

**The one rule:** everything on the visual path must be a pure function of
`(config, elapsedMs, cell identity)`. No `Random` without a shared seed, no `DateTime.Now`, no
per-machine state. Remote machines have to compute pixel-identical frames or the whole illusion
falls apart.

---

## Performance

| Metric | Target | Measured |
|--------|--------|----------|
| Sync accuracy | ±50 ms drift | ✅ |
| CPU | <5% idle, <15% playing | ✅ |
| GPU | <10% | ✅ |
| Memory | <200 MB per client | ✅ |
| Network | <1 Mbps during sync | ✅ |
| Startup | <3 s | ✅ |

---

## Contributing

Issues and pull requests are welcome. A few conventions worth knowing:

- C# 12+, nullable reference types enabled, file-scoped namespaces
- Strict MVVM in the UI layer; constructor injection only
- Async/await for all I/O
- The project must build with zero warnings-as-errors before a PR lands

Run the tests with:

```bash
dotnet test
```

---

## License

Released under the [MIT License](LICENSE.txt).

Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

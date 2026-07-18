# Archived: Legacy GDI+ Composition & Frame-Streaming Stack

**Archived:** 2026-07-18
**Why:** This entire stack implemented the original *centralized* rendering design:
the server composed wallpaper frames with GDI+ and streamed JPEG frames to clients
over gRPC. It was superseded by the metadata-based D2D architecture (separate
`WaBiBaBuSy.Player.D2D` process, deterministic client-side rendering from a shared
UTC timestamp — see `.docs/2025.12_DIRECT2D_RENDERING_ARCHITECTURE.md`). None of
this code was invoked at runtime anymore; verification notes below.

This folder is outside every `.csproj`, so nothing here compiles. Full history is
in git (files were moved with `git mv`).

## Archived files

| File | Was | Dead because |
|---|---|---|
| `CompositionRenderer.cs` | GDI+ layer compositor (background + animation → frame) | Never constructed anywhere (`new CompositionRenderer` had zero hits) |
| `AnimationLayerRenderer.cs` | GDI+ animation layer (movement + frame extraction) | Only used by `CompositionRenderer` |
| `BackgroundLayerRenderer.cs` | GDI+ background layer | Only used by `CompositionRenderer` |
| `GifWallpaperRenderer.cs` | Magick.NET GIF renderer (pre-LibVLC) | No references; GIFs go through LibVLC/D2D pipeline |
| `CrossScreenFrameRenderer.cs` | Displayed server-streamed frames on the client | No references; frame streaming removed |

**Explicitly NOT archived** (still live, despite being listed as dead in earlier docs):
`VideoWallpaperRenderer` and `ImageWallpaperRendererLibVLC` — used for simple
per-monitor playback in `TrayViewModel.CreateRendererFactory` and
`MainWindowViewModel.ApplyWallpaperLocallyInternal`.

## Removed gRPC frame-streaming path

Removed rather than moved (it spanned proto + server + client). Verified dead at
both ends: no caller of `WallpaperSyncCoordinator.SendCrossScreenFrameAsync`, no
subscriber to `WallpaperSyncClient.CrossScreenFrameReceived`.

Removed pieces (recover via git history of this commit):

- **`WaBiBaBuSy.Grpc/Protos/wabibabusy.proto`**: `rpc StreamCrossScreenFrames(stream FrameAcknowledgment) returns (stream CrossScreenFrame)`, messages `CrossScreenFrame`, `FrameAcknowledgment`, enum `CompressionType`
- **`WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs`**: region "Cross-Screen Frame Streaming" — `StreamCrossScreenFrames` override, `SendCrossScreenFrameAsync`, `Register/UnregisterCrossScreenStream`, `_crossScreenStreams`
- **`WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs`**: `StartCrossScreenFrameStream`, `StopCrossScreenFrameStream`, `SendFrameAcknowledgmentAsync`, `CrossScreenFrameReceived` event, `CrossScreenFrameReceivedEventArgs`, `IsCrossScreenActive`, frame-stream fields; the `CrossscreenStart`/`CrossscreenStop` command handler block that opened/closed the stream
- **`WaBiBaBuSy.Core/Services/WallpaperSyncCoordinator.cs`**: `SendCrossScreenFrameAsync`
- **`WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs`**: `UseDistributedRendering` flag (was never read)
- **`WaBiBaBuSy.Player.D2D/Program.cs`**: never-initialized "video fallback" render branch (`_compositionRenderer`, `_canvasManager`, `_compositionInitialized`, `ConvertBitmapToD2D`)

Also removed the same day: unimplemented `DistributionMode.GroupedSequential`
enum value in `WaBiBaBuSy.Core/Services/Animation/AnimationOrchestrator.cs`.

The `CommandType.CROSSSCREEN_START`/`CROSSSCREEN_STOP` enum values turned out to have
no remaining code references either (the live cross-screen path uses `renderer_type =
"d2d_crossscreen"` in `SyncParameters` instead) — they were removed and their tag
numbers 6/7 marked `reserved` in the proto.

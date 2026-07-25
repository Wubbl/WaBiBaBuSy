# Playlist / Party Mode — Design Spec

**Created:** 2026-07-18
**Status:** Design approved, pre-implementation
**Roadmap item:** Tier 2 #7 (`.docs/2026.07_FEATURE_OVERVIEW.md`), Open Items §4.1 (`.docs/2026.07_OPEN_ITEMS.md`)
**Depends on:** Tier 1 complete (remote parameter parity, shared-timestamp sync, session resume)

---

## 1. Summary

Rotate a set of animation configurations across all synced machines on a loop, each shown
for its own duration, optionally shuffled, started and stopped as a "show." All infrastructure
(content distribution, shared UTC start timestamps, session resume) already exists — this feature
is **config + orchestration + UI** layered on top of the existing single-config broadcast path.

The four UX questions from the roadmap were settled as:

| Question | Decision |
|---|---|
| Per-item duration vs. global interval | **Per-item duration with a global default fallback** |
| Hard-cut vs. wait-for-lap on rotation | **Hard-cut by default; opt-in per-item "finish the lap" (lap-snap)** |
| Remote opt-out | **Auto-follow, no opt-out** (exclude machines via existing per-monitor selection) |
| Where the UI lives | **Own dedicated Playlist / Party Mode dialog** |

## 2. Scope

**In scope (v1):**
- An ordered, named list of animation configs the server rotates through on a loop.
- Per-item duration with a playlist-level default.
- Shuffle and loop-playlist toggles.
- Opt-in per-item lap-snap (Linear movement only in v1).
- Start Show / Stop Show, plus save/load playlists to disk.
- Server-driven rotation using the existing shared-timestamp broadcast path.
- First on-disk persistence of `CrossScreenConfig` (as embedded playlist items).

**Out of scope (explicitly deferred, noted here so the spec stays focused):**
- **Scheduled interrupt-shows** ("every 30 min the fish swims once, then the background returns").
  A separate scheduling layer on top of the rotation; revisit after v1.
- **Per-client opt-out.** The party model is one unified show; exclusion is handled up front
  by the existing per-monitor selection filter (`CrossScreenConfig.SelectedMonitorIds`).
- **Cross-item transition effects.** Covered by the separate Tier-2 "bezel-crossing transition"
  item (`SyncParameters.transition_effect`); not part of playlist rotation.
- **Lap-snap for non-Linear movement.** Static/RandomWalk/Bounce/Circular fall back to hard-cut
  in v1 (see §5).

## 3. Data model

New types in `WaBiBaBuSy.Models` (e.g. `WaBiBaBuSy.Models/Wallpaper/Playlist.cs`):

```csharp
public class Playlist
{
    public string Name { get; set; } = "Untitled Playlist";
    public bool   Loop { get; set; } = true;            // repeat whole list forever
    public bool   Shuffle { get; set; } = false;
    public int    DefaultItemDurationMs { get; set; } = 30_000;
    public List<PlaylistItem> Items { get; set; } = new();
}

public class PlaylistItem
{
    public string            Name { get; set; } = string.Empty;   // display label
    public CrossScreenConfig Config { get; set; } = new();        // full embedded config
    public int?              DurationMs { get; set; }             // null => playlist default
    public bool              SnapToLap { get; set; } = false;     // opt-in "finish the lap"

    /// <summary>Resolve the effective dwell time for this item.</summary>
    public int GetEffectiveDurationMs(int playlistDefault) => DurationMs ?? playlistDefault;
}
```

**Rationale for embedding the full `CrossScreenConfig` (vs. referencing a named saved config):**
CrossScreen configs are not persisted anywhere today — `MainWindowViewModel._crossScreenConfig`
is a single in-memory object built from the dialog and discarded on restart. There is no config
library to reference. Embedding a self-contained copy per item is the simplest correct model and
makes the playlist file portable. This feature therefore introduces the first on-disk persistence
of `CrossScreenConfig`.

**Persistence:** JSON files under `%APPDATA%\WaBiBaBuSy\playlists\<name>.json`, mirroring the
existing `logging-config.json` persistence pattern. Serialize with `System.Text.Json`. Keep the
Models layer serializer-free (persistence helper lives in Core/UI, not on the model type).

## 4. Orchestration (server-side)

New `PlaylistOrchestrator` in `WaBiBaBuSy.Core/Services/Animation/`.

### 4.1 Reuse of the existing apply path

Refactor the body of `MainWindowViewModel.StartCrossScreen()` (lines ~2866–3160) into a reusable
`ApplyCrossScreenConfigAsync(CrossScreenConfig config)` that:
- builds the virtual canvas from connected nodes (`VirtualCanvasManager`),
- computes the sequential IconZone global A* path if applicable,
- broadcasts to remote clients (`WallpaperSyncCoordinator.StartCrossScreenD2DOnClientAsync`) and
  applies locally to server D2D monitors,
- all with a single shared UTC start timestamp.

Both the manual **Start Cross-Screen** button and the `PlaylistOrchestrator` call this method.
This is a targeted cleanup of code the feature touches — the current 300-line method mixes canvas
construction with the single-shot trigger.

### 4.2 Rotation loop

```
state: currentIndex, currentItemStartTsUtc, running
for each tick while running:
    item = nextItem()                    // sequential, or shuffled order (§4.3)
    currentItemStartTsUtc = now
    await ApplyCrossScreenConfigAsync(item.Config)
    dwell = ResolveDwell(item)           // §5
    await Delay(dwell) (cancellable)
    advance; if end-of-list and !Loop -> stop; else continue
```

The orchestrator runs in the host process (which acts as the server). It exposes
`StartAsync(Playlist)`, `Stop()`, and a `CurrentItem` / `CurrentItemStartTsUtc` snapshot for
session-resume and UI.

### 4.3 Shuffle

Shuffle picks the **order** of items; the server is the single authority that issues each item, so
no per-machine determinism is required for shuffle. Randomness lives entirely in the orchestrator
(server side) and **never** touches the player's deterministic render path — the determinism
invariant (no `Random`/`DateTime.Now` in `MovementCalculator`/`ColorGrader`/`PatternLayout`) is
untouched. Shuffle reshuffles once per full playlist loop and guarantees every item appears once
per cycle (Fisher–Yates over the item list).

### 4.4 Reconnection / session resume

The orchestrator tracks `CurrentItem` + `CurrentItemStartTsUtc`. The existing session-resume
mechanism (server re-sends the current animation command to a rejoining client, back-dating the
epoch so a late joiner lands mid-animation) works unchanged — the "current command" is simply the
current playlist item's config with its recorded start timestamp. When the playlist is running,
the server's resume payload must be the current item, not a stale single-config command.

## 5. Rotation boundary — hard-cut + opt-in lap-snap

Every node switches when it receives the next item's command. Switch-time jitter here is
**between-items** (network latency on the switch), not within-animation drift, so a few ms is
acceptable and does not affect the ±50ms sync guarantee.

`ResolveDwell(item)`:
- **Hard-cut (default, `SnapToLap == false`):** `dwell = item.GetEffectiveDurationMs(default)`.
- **Lap-snap (`SnapToLap == true`):** round the dwell **up** to the next whole movement period so
  the animation completes a clean lap before switching:
  ```
  target = item.GetEffectiveDurationMs(default)
  lapMs  = ComputeLapMs(item.Config)          // see below
  dwell  = lapMs > 0 ? ceil(target / lapMs) * lapMs : target
  ```

**`ComputeLapMs` (v1 = Linear only):**
For `MovementType.Linear`, one full traversal across the virtual canvas is deterministic from the
canvas width and speed:
```
travelDistancePx = virtualCanvasWidth + contentWidthPx   // full off-screen-to-off-screen sweep
lapMs = travelDistancePx / speedPxPerSec * 1000
```
`virtualCanvasWidth` comes from `VirtualCanvasManager` (already built in the apply path);
`speedPxPerSec` from `MovementConfig.SpeedPixelsPerSecond`. For **Static, RandomWalk, Bounce,
Circular, SineWave** in v1, `ComputeLapMs` returns 0 → falls back to hard-cut, with a logged
`info` note that lap-snap is unsupported for that movement type. (SineWave is horizontal-Linear
underneath and *could* be added later; deferred to keep v1 tight.)

Rationale: this achieves clean traversal boundaries deterministically without client-side
completion reports (which would race across machines and fight the "no handoff, pure deterministic
canvas" invariant).

## 6. UI — dedicated Playlist / Party Mode dialog

Avalonia MVVM, following the existing `CrossScreenConfigDialog` / `CrossScreenConfigViewModel`
pattern: `PlaylistDialog.axaml(.cs)` + `PlaylistViewModel`. Opened from the tray menu and/or the
server control panel.

**Playlist-level controls:** Name, Loop (checkbox), Shuffle (checkbox), Default item duration.

**Item list** (each row): display name, duration field (blank = "default"), lap-snap checkbox,
and a summary line (animation file name + movement type + distribution mode).

**Item actions:**
- **Add** → opens the existing `CrossScreenConfigDialog`; on OK, `viewModel.BuildConfig()` is
  captured into a new `PlaylistItem` (default name = animation file name).
- **Edit** → re-opens `CrossScreenConfigDialog` seeded with that item's config
  (`LoadFromConfig`), writes the result back.
- **Reorder** (up/down), **Duplicate**, **Remove**.

**Show controls:** **Start Show** / **Stop Show** (bound to `PlaylistOrchestrator`), plus
**Save** / **Load** / **New** playlist (file dialogs over `%APPDATA%\WaBiBaBuSy\playlists\`).

**State reflection:** while a show runs, highlight the current item and show a "now playing" label.
Reuse the existing `ShowAnimationInfo` / `ActiveAnimationFileName` surface where practical.

## 7. Testing

Unit tests (`WaBiBaBuSy.Tests`):
- `ComputeLapMs` for Linear across representative speeds and canvas widths; verify non-Linear
  returns 0.
- `ResolveDwell`: ceil rounding for lap-snap; item-override vs. playlist-default resolution;
  hard-cut passthrough.
- Shuffle: every item appears exactly once per cycle; order actually varies across cycles given
  different shuffles.
- Persistence round-trip: `Playlist` → JSON → `Playlist` preserves all fields including nested
  `CrossScreenConfig`.

Determinism guard:
- Assert the per-item config broadcast by the orchestrator is byte-identical to the stored item
  config (shuffle changes order, never content) — protects the render-path determinism invariant.

Manual / E2E (fold into the pending multi-client E2E pass):
- 3+ item playlist with mixed durations and one lap-snap Linear item; confirm all machines switch
  together and the lap-snap item completes a full traversal before switching.
- Client disconnect/reconnect mid-show lands the rejoiner on the current item at the right position.

## 8. Files (anticipated)

**New:**
- `WaBiBaBuSy.Models/Wallpaper/Playlist.cs` — `Playlist`, `PlaylistItem`.
- `WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs`.
- `WaBiBaBuSy.Core/Services/Animation/PlaylistStore.cs` — JSON save/load helper.
- `WaBiBaBuSy.UI/Views/PlaylistDialog.axaml(.cs)`.
- `WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs`.
- Tests in `WaBiBaBuSy.Tests`.

**Modified:**
- `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` — extract `ApplyCrossScreenConfigAsync`;
  wire tray/control-panel entry point + orchestrator lifetime.
- `WaBiBaBuSy.Core/Services/WaBiBaBuSyService.cs` — own the `PlaylistOrchestrator` instance
  (alongside the existing `AnimationOrchestrator`).
- Session-resume path — resume payload reflects the current playlist item when a show is running.

## 9. Open questions for implementation (non-blocking)

- Whether Start Show should live only in the new dialog or also as a top-level tray action once a
  playlist exists (lean: dialog for v1, tray shortcut later).
- Optional refinement: pre-schedule the next switch at a future shared timestamp to remove
  between-item switch jitter entirely (v1 broadcasts at the boundary; refinement deferred).

# Show Reliability for 20 Nodes — Design

**Created:** 2026-09-17 · **Status:** §2 (Tier 0.4), §3 and §5 implemented 2026-09-17; §4 two-phase switch with standby runtime and §6 auto-start deferred · **Roadmap:** Tier 1.3 (+ 0.4)

> **Implementation note (2026-09-17):** with prefetch in place the item switch already reveals on the same
> frame on every node (future T0, Tier 0.4); what remains from §4 is the *gapless* switch (standby
> runtime + transitions), which needs the scene-layers player refactor. `PlaylistOrchestrator` exposes
> `NextItem` / `NextSwitchUtcMs` for the UI countdown as a first step.
**Solves:** F7 in [`2026-09-17-lan-party-roadmap.md`](2026-09-17-lan-party-roadmap.md)

---

## 1. Problem, as it will show up at the party

1. **Ragged starts.** `sharedStartTimestamp = UtcNow` is taken *before* the remote command is
   even sent (`MainWindowViewModel.ApplyCrossScreenConfigAsync`). Each remote then downloads the
   file (`WallpaperPlaybackService.cs:187`), spawns `Player.D2D`, decodes GIF frames to GPU, and
   back-dates. On 20 nodes with a 20 MB GIF the fish pops in mid-lap on some screens 3–8 s after
   the first. Every playlist item switch repeats this.
2. **Re-downloads.** The content cache is an in-memory dictionary (`_contentCache`); after a client
   restart every item is fetched again although the file is on disk. `contentId` is the bare file
   name, so `logo.gif` from two folders collide and a re-exported file with the same name is
   served stale.
3. **Missing assets on remotes.** Only `Animation.AnimationPath` is registered as content;
   background images and `AdditionalAnimationPaths` are dropped with a warning
   (`ApplyCrossScreenD2DFromRemoteAsync`). Multi-image presets silently degrade on 19 of 20 screens.
4. **No switch coordination.** Playlist items are broadcast at the boundary. Nodes switch when the
   command arrives (jitter ≈ RTT + apply time), and no transition is possible because a node does
   not know the next item.
5. **Server burst.** 20 clients pull the same file at the same instant over one gRPC server.

## 2. Future T0 (roadmap 0.4, S)

```
leadMs = clamp(3 × maxObservedRtt + applyEstimateMs, 800, 4000)      // default ≈ 1500
sharedStartTimestamp = UtcNow + leadMs
```
- `applyEstimateMs` = last measured local "load → READY" duration (the player already prints
  `READY`), so a heavy GIF widens the lead automatically.
- Player: `HandleStartAnimationCommand` already computes `_renderLoopStart` for any timestamp; add
  the rule **elapsed < 0 → draw background only (or first frame at start position)**. Check the
  pattern / endless branches for negative elapsed (`(long)(scrolledD / step)` truncates toward
  zero — fine — but `%` on negatives needs the same guard `ColorGrader` uses).
- Late joiners are unaffected (their elapsed is positive).

Result: all 20 nodes that have the content start on the same frame.

## 3. Content pipeline

### 3.1 Content identity
`contentId = $"{fileName}-{sha256(file)[..16]}"`. The hash is computed once per gallery item and
cached in the gallery JSON (`WallpaperGallery` already persists items). Same name, new bytes → new
id → clients fetch the new version; identical bytes under two names dedupe on the client if the
cache is keyed by hash.

### 3.2 Persistent client cache index
`ContentCacheManager` gets `index.json` (`contentId → path, size, lastUsed`) written on every
download; loaded on startup into `_contentCache`. LRU eviction already exists; it now survives
restarts.

### 3.3 All assets travel
`ApplyCrossScreenConfigAsync` registers one content id for **every** file the scene references:
`Animation.GetAllAnimationPaths()`, `Background.ImagePath`, and (scene layers) every layer's
sources. `SyncParameters` gains `repeated ContentRef assets = 23;` (`content_id`, `role`
(`primary|additional|background`), `layer_index`, `original_path`). The client downloads all,
then rewrites paths in the deserialized configs (`original_path → local path`) before apply — the
special-casing of `AnimationPath` in `ApplyCrossScreenD2DFromRemoteAsync` disappears.

### 3.4 Prefetch
- New `SyncCommand.Type = PREFETCH` with the asset list of the **whole playlist** (or of the next
  item). `PlaylistOrchestrator.Start` sends it before the first apply; the client fetches in the
  background with low priority and reports progress via heartbeat
  (`HeartbeatRequest.prefetch_ready_count / total`).
- Item switch waits for "all seats ready" up to `PrefetchTimeoutMs` (default 10 s), then goes
  anyway (a straggler back-dates as today).
- Topology node badge: ⬇ 3/7 while prefetching, ✓ when ready.

### 3.5 Server burst
Chunk size is already 1 MB; add a per-server download semaphore (e.g. 6 concurrent streams) so
20 simultaneous first-time fetches queue instead of thrashing. With prefetch this only matters on
the very first show of the evening.

## 4. Coordinated item switches (pre-announce)

`PlaylistOrchestrator` currently: apply → wait dwell → apply next. Change to a two-phase switch:

```
t_switch = T0_current + dwell
at t_switch − announceLeadMs (≈ 2000 ms):
    broadcast LOAD_NEXT(next item, T0 = t_switch)       // content already prefetched
    remotes + local players load the next item into a standby runtime, do not display
at t_switch (each node's own clock, offset-corrected):
    standby becomes active (Cut) or transition runs (effects design §6)
```

- Local server monitors get the same two-phase path through `D2DCompositionService`
  (`PlayerCommandLoadAnimation` with `StandBy = true` + `PlayerCommandSwitchAt(T0)`).
- Session resume: `_activeCrossScreenCommands` stores the *active* command with its original `T0`;
  a rejoining node lands mid-item as today. If a rejoin happens inside the announce window, send
  both active and next.
- `PlaylistOrchestrator` exposes `NextItem` + `NextSwitchUtc` for the UI countdown ("next: Logo
  Rain in 0:42").
- Lap-snap: `ContentWidthPx` is `0` today ("best-effort"); with the player reporting its computed
  `_animWidth` via `READY:{width}` the dwell rounds to real laps. With a Ring, lap = `P / speed`.

## 5. Show health (server UI)

A strip above the topology: **N/20 ready · M playing · drift worst ±Xms · k stale · versions
mismatched: 1**, plus per-node badges (prefetch, ready, playing, drift — drift exists). One
button **Resync all**: re-sends the active command with the original `T0` to every seat (cheap;
fixes any node that missed a command). One button **Restart show at next item**.

## 6. Startup of the room

Two small things that save the first 30 minutes of a party:
- **Client auto-connect + auto-follow**: `ClientConfiguration.AutoConnect` exists; the installer
  should offer "Start with Windows, auto-connect via mDNS, follow the server's show" as the default
  client profile so a rebooted machine rejoins with zero clicks.
- **Server: "Start last show on server start"** option (`ServerConfiguration.AutoStartPlaylist`).

## 7. Tests

- `ContentIdTests`: id changes with bytes, stable across renames; index roundtrip.
- `AssetListTests`: a 3-layer scene with background image → correct `ContentRef` list; path
  rewrite on the client side.
- `SwitchScheduleTests` (`PlaylistScheduler`): `t_switch`, announce time, behavior when dwell <
  announce lead (announce immediately), lap-snap with real content width and with Ring perimeter.
- `NegativeElapsedTests`: `MovementCalculator`/`PatternLayout`/`ColorGrader` at `elapsed < 0` do
  not throw and the player rule "draw nothing before 0" is honored (unit test on the helper that
  decides visibility).
- Manual: 3 nodes, 20 MB GIF, watch first frame land simultaneously; pull one node's cable across
  a switch; restart a client and confirm no re-download.

## 8. Rollout

1. 0.4 Future T0 + negative-elapsed guard (S).
2. Content ids + persistent cache index (S).
3. All-assets transfer (M) — unlocks multi-image presets and background images on remotes.
4. Prefetch + readiness badges (M).
5. Two-phase switch + `NextItem` UI (M) — unlocks transitions.
6. Show-health strip, Resync all, auto-start options (S).

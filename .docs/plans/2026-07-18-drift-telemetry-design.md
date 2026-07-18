# Drift Telemetry — Design (2026-07-18)

**Status:** Approved design, pre-implementation
**Replaces:** `TimingSynchronizer` (deleted) — resolves the "wire up or delete" decision in `.docs/2026.07_OPEN_ITEMS.md` §2 with "replace with telemetry"
**Related:** `.docs/2025.12_OpenIssues.md` (TimingSynchronizer audit item), Tier 1 clock-offset sync (`ClockOffsetEstimator`)

## Problem

The server has no visibility into per-client timing. Clients compute an NTP-style clock offset locally (`ClockOffsetEstimator`, fed from heartbeat round-trips) and use it to compensate incoming command timestamps, but never report it back. E2E validation of the ±50ms sync target currently requires reading client debug logs.

`TimingSynchronizer` was meant to address timing but is dead code: it broadcasts *expected positions to clients* (backwards for telemetry), and it reads `AnimationDistributor.GetAnimatingClients()`, which is only populated by the orchestrator path — a path with zero callers. On the live `d2d_crossscreen` path it can never fire.

## Decision summary

| Question | Decision |
|---|---|
| Drift metric | Clock offset + RTT (client already computes both; offset IS the sync error under deterministic math) |
| Fate of TimingSynchronizer | Delete class + broadcast machinery; new small `DriftMonitor` aggregator |
| Transport | Extend `HeartbeatRequest` (no new timers, heartbeat already updates `ConnectedClient`) |
| UI | Per-node "±Xms" label with color state on the topology canvas |
| Correction loop | None — display only. Deterministic-math invariant untouched. |

## Architecture

Passive, one-directional telemetry:

```
client ClockOffsetEstimator ──(heartbeat fields)──▶ WallpaperSyncService.Heartbeat
        ──▶ ConnectedClient (stored) + DriftMonitor (thresholds/staleness)
        ──▶ GetConnectedClients() ──▶ MainWindowViewModel.RefreshTopology (2s poll)
        ──▶ ClientNodeViewModel.DriftMs/DriftState ──▶ topology node label
```

## Changes

### Proto (`WaBiBaBuSy.Grpc/Protos/wabibabusy.proto`)
- `HeartbeatRequest` += `double clock_offset_ms = 4;`, `double rtt_ms = 5;`, `bool has_drift_report = 6;`
  (`has_drift_report` is needed because the first heartbeat fires before `ClockOffsetEstimator` has any sample — without it the server would record a bogus 0ms offset)
- `ConnectedClient` += `double clock_offset_ms = 9;`, `double rtt_ms = 10;`, `int64 last_drift_report_utc = 11;`
- Delete `AnimationTimingSync` message and `BroadcastAnimationTimingSync` RPC (dead broadcast path). Server+client versions travel in lockstep via the updater, so no reserved-field dance is needed.

### Client (`WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs`)
- `SendHeartbeatAsync`: populate `ClockOffsetMs`/`RttMs`/`HasDriftReport = true` on the request once `ClockOffsetEstimator` has at least one sample; before that, `HasDriftReport` stays false and the server ignores the fields.
- `ClockOffsetEstimator` (`WaBiBaBuSy.Models/Networking/`): add `RttMs` getter exposing the best (lowest-RTT) sample's round-trip time alongside `OffsetMs`.

### Server
- Delete `WaBiBaBuSy.Core/Services/Animation/TimingSynchronizer.cs` (`TimingSynchronizer`, `TimingSyncSession`, `SyncSessionState`) and the `BroadcastAnimationTimingSync` handler in `WallpaperSyncService`.
- New `WaBiBaBuSy.Core/Services/Animation/DriftMonitor.cs`:
  - `Record(clientId, offsetMs, rttMs, timestampUtc)`
  - `GetDrift(clientId)` → `(offsetMs, rttMs, lastReportUtc)?`
  - `IsStale(clientId, now)` → no report within 2× heartbeat interval
  - Logs a warning when `|offsetMs| > 50` (tolerance breach)
- `WallpaperSyncService.Heartbeat`: when `has_drift_report` is true, copy incoming fields onto the stored `ConnectedClient` (+ stamp `last_drift_report_utc`) and call `DriftMonitor.Record`; otherwise leave drift state untouched. Clients are removed from `DriftMonitor` in `RemoveClient` (dead-client sweep / stream teardown).

### Topology UI (`WaBiBaBuSy.UI`)
- `ClientNodeViewModel` += `DriftMs`, `RttMs`, `DriftState` enum: `None`, `Ok` (≤25ms), `Warn` (25–50ms), `Breach` (>50ms), `Stale`.
- `MainWindowViewModel.UpdateClientList`: map proto fields → VM; compute `Stale`/`None` at map time from `last_drift_report_utc`. The existing 2s `RefreshTopology` rebuild carries the values — no additional wiring.
- Node template: small "±Xms" label; green/yellow/red by state; grey "—" for `Stale`; nothing rendered for `None` and on server-local nodes (`SERVER_LOCALHOST_MONITOR_*` — the server is the reference clock).

## Error handling

- No report yet / old client without the fields: proto3 defaults are 0, but `last_drift_report_utc == 0` distinguishes "no report" (`None`) from a genuine 0ms offset — never display absent data as "perfectly synced".
- Missing/stale data never throws; worst case is a grey label.
- `DriftMonitor` is concurrent-safe (`ConcurrentDictionary`), mirroring `_connectedClients` usage.

## Testing (`WaBiBaBuSy.Tests`)

- `DriftMonitor`: record/overwrite, staleness boundary (exactly 2× interval), threshold logging classification.
- `ClockOffsetEstimator.RttMs`: returns RTT of the lowest-RTT sample; 0/empty-window behavior.
- `DriftState` mapping: boundaries at 25ms and 50ms, negative offsets (use `|offset|`), stale-beats-value precedence.
- Existing 19 tests stay green; movement determinism tests unaffected (no rendering-path changes).

## Non-goals

- No playback-position correction of any kind (would violate the deterministic-math invariant).
- No measured render-position drift from the D2D players (possible later; would need player→main IPC).
- No history/graphing — current value only.

## Side effects

Retires the timing-sync broadcast machinery from the "legacy dead code" cleanup list (`.docs/2026.07_OPEN_ITEMS.md` §2).

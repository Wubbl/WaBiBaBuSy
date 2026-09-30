# Automated Multi-Machine Test Mode — Design

**Created:** 2026-09-30 · **Status:** design approved, not implemented · **Scope:** spec 1 of 2 (harness + Sync & timing + Visual parity)
**Motivation:** [`2026-09-28-gui-and-e2e-test-checklist.md`](2026-09-28-gui-and-e2e-test-checklist.md) §3–§4 are slow,
subjective and not repeatable by hand ("no visible drift", "same frame on all nodes"). This design turns them
into a run that measures, captures and reports, so results can be analysed and optimised afterwards.

---

## 1. Goal and non-goals

**Goal.** Connect one or two remote clients, start a test run on the server, walk away. The run plays a set of
scenarios on every node, measures timing and position per node, captures screenshots from every monitor at
the same instant, collects all logs, and writes a results folder (`report.json` + `report.html` + PNGs + logs)
that a human reads in a browser and Claude reads from disk.

**Spec 1 (this document):** test harness, *Sync & timing*, *Visual parity*.
**Spec 2 (later, reuses the harness):** *Show reliability* (prefetch timing, item-switch gap, skip latency,
resync) and *Failure & recovery* (simulated disconnect, client restart, server restart, reconnect timing).

**Non-goals.**
- Checklist §1 (GUI interaction) stays manual.
- No change to the deterministic render math; test mode only adds side paths.
- Not a production feature: the whole thing is gated behind a setting and meant for development runs.
- No physical camera/Desktop-Duplication capture in v1 (see §9).

## 2. Prerequisite fixes (land first, the harness depends on them)

1. **"View logs" hangs** (checklist §1.2). `WallpaperSyncClient.SendLogsToServerAsync` (≈`:747`) reads the
   current day's log with `File.ReadAllLinesAsync`, while `FileLoggerProvider` (≈`:44`) holds it open with a
   `StreamWriter` → sharing violation, caught and only logged; the server never gets a reply and the UI stays at
   "Requesting logs…". Fix: open with `FileShare.ReadWrite | FileShare.Delete`; on any failure still call
   `SendClientLogs` with an error text; the server side gets a timeout (10 s) that shows "no reply from X".
   Also stop ignoring the `bool` from `SendCommandToClientAsync` in `RequestClientLogsAsync`.
2. **Remote thumbnails never sent.** `WallpaperSyncClient.ThumbnailCaptureService` (≈`:62`) is never assigned,
   so `SendThumbnailIfDueAsync` returns early on every remote. Wire it to the client's own player HWND
   (the same capture services `MainWindowViewModel` already creates per local monitor).

## 3. Architecture

| Layer | New piece | Responsibility |
|---|---|---|
| Models (pure, unit-tested) | `Testing/TestScenario`, `TestStep` (+ subtypes), `ProbeResult`, `DriftAnalyzer`, `ExpectedPosition`, `MarkerDetector` | Scenario JSON model; analysis math: drift spread, expected sprite position via `MovementCalculator`, marker centroid in a PNG, pass/warn/fail against thresholds |
| Core | `Testing/TestRunner` | Executes a scenario step by step through existing service/coordinator calls (play scene, stop, prefetch) plus the new probe calls; owns timeouts and cancellation |
| Core | `Testing/TestReportWriter` | Writes the results folder (`report.json`, `report.html`, PNGs, logs) |
| gRPC | `CommandType.TEST_MODE = 10`, `CommandType.TEST_PROBE = 11`, RPC `SubmitProbeResult` | Server → nodes: timecode on/off, simulated skew, "probe at server time T"; nodes → server: probe results with PNGs (chunked stream) |
| Client | `Testing/TestProbeHandler` | Converts T to local time with the clock offset, forwards the probe to every local player, merges replies, adds a perf sample, uploads |
| Player.D2D | `cmd_test_mode`, `cmd_probe` | Draws the timecode strip; answers probes (see §5); replies on stderr as `SIGNAL:PROBE:{json}` |
| UI | Developer tools → "Run test suite…", CLI `--test-run <file> [--exit]`, localhost HTTP API | Three entry points into the same `TestRunner`; toolbar progress text |

**Invariants.**
- The server's own monitors are probed exactly like remote nodes (same `TestProbeHandler` path, no shortcut).
- Player replies go over the **stderr signal channel** (like `SIGNAL:LAP_COMPLETE`). `D2DPlayerHost` pairs each
  stdout command with the next stdout line; asynchronous probe replies on stdout would break that pairing.
- All DXGI work (readback, frame statistics) stays in `Player.D2D` — nothing DXGI in the main process.
- The timecode strip is drawn after scene composition as an overlay; it never feeds back into positioning,
  pattern layout or color grading.

### 3.1 Entry points and gating

- **Setting** `Settings → Developer → Enable test mode` (server, default off). Off → no menu item, no API.
- **Client setting** `Allow test runs` (default on). Off → the client answers `TEST_MODE` / `TEST_PROBE` with a
  refusal, and preflight fails for that node with a clear message. Captures contain only the wallpaper
  back buffer (no windows, no desktop), so on-by-default is acceptable.
- **Dev tools menu:** "Run test suite…" → file picker (defaults to the bundled `TestScenarios/`), progress
  in the toolbar ("Test: step 4/12 · parity-matrix"), Cancel button, "Open results" when done.
- **CLI:** `WaBiBaBuSy.UI.exe --test-run <scenario.json> [--exit]` starts the server if needed, waits for
  `requires.minRemoteNodes` to connect (timeout 120 s), runs, and with `--exit` quits with exit code
  0 (all pass) / 1 (any fail) / 2 (aborted).
- **Localhost HTTP API:** a second Kestrel listener on `127.0.0.1:<port>` (HTTP/1.1, default port 50052,
  configurable) inside `WallpaperSyncServerHost`, only when test mode is enabled:
  - `POST /test/run` `{ "scenario": "<path>" }` → `{ "runId" }` (409 if a run is active)
  - `GET /test/status` → current step, progress, last result per step
  - `DELETE /test/run` → cancel (partial report is still written)
  - `GET /test/runs` → list of result folders

## 4. Scenario format

JSON files in `TestScenarios/` (in the repo, copied next to the app on build). Scenes are plain
`CrossScreenConfig` JSON; asset paths are relative to the scenario file and go through the existing content
registration / prefetch, so remotes receive them like any other scene.

```json
{
  "name": "sync-basic",
  "requires": { "minRemoteNodes": 1 },
  "thresholds": { "driftSpreadMs": 50, "driftWarnMs": 25, "positionErrorPx": 3, "pixelDiffPct": 0.5 },
  "steps": [
    { "type": "testMode", "timecode": true },
    { "type": "playScene", "scene": "scenes/linear-sequential.json", "targets": "all" },
    { "type": "probe", "label": "clean-start", "at": "start+150ms", "capture": true },
    { "type": "probeSeries", "label": "long-run", "everyMs": 10000, "forMs": 600000, "perf": true },
    { "type": "exactFrame", "label": "pattern-parity", "elapsedMs": 12345 },
    { "type": "stop" }
  ]
}
```

**Step types (v1).**

| Type | Parameters | Effect |
|---|---|---|
| `testMode` | `timecode` (bool), `simulatedClockSkewMs` (per node map, optional) | Sends `TEST_MODE` to all nodes |
| `playScene` | `scene`, `targets` (`all` / `server` / `remotes` / node names) | Same path as ▶ Play on all/selection; records the shared start timestamp and start lead |
| `probe` | `label`, `at` (`start+Nms` or `now+Nms`), `capture` | One `TEST_PROBE` at a server instant |
| `probeSeries` | `label`, `everyMs`, `forMs`, `capture` (default false), `perf` | Repeated probes; the drift-over-time source |
| `exactFrame` | `label`, `elapsedMs` | Offscreen render at an exact elapsed value on every node (§5.3) |
| `wait` | `ms` | Plain delay |
| `stop` | — | Clear wallpaper on all nodes |

`at` is always converted to an absolute **server UTC ms** by the runner, at least 500 ms in the future, so the
command reaches every node before the instant.

**Marker sprite.** Measurement scenes use a generated asset (`TestScenarios/assets/marker.png`): a 64×64
magenta square with a 4 px black border and a white dot offset toward the right edge, which `MarkerDetector`
finds reliably against the test backgrounds (solid dark grey or the default IconZone background). The
off-center dot makes the orientation detectable, so a missing or wrong horizontal flip ("face travel
direction", facing rows) shows up as `orientation: mirrored` instead of passing silently.

## 5. Measurements

### 5.1 Probe (timing)

`TEST_PROBE { probeId, atServerUtcMs, capture, maxCaptureWidth }` →
client computes `atLocalUtcMs = atServerUtcMs − clockOffsetMs` → every local player gets
`cmd_probe { probeId, atLocalUtcMs, capture }`.

The player, on the **first frame rendered at or after** `atLocalUtcMs`, records:

- `renderedElapsedMs` — the effective elapsed value the frame was rendered with (incl. node phase)
- `renderUtcMs` — local UTC when rendering started, `frameIndex`
- present time — from `IDXGISwapChain::GetFrameStatistics` (`SyncQPCTime` for the matching `PresentCount`,
  converted QPC → UTC); if statistics are unavailable, `presentUtcMs = null` and the report falls back to
  `renderUtcMs` with a note
- frame-interval stats over the last 120 frames: mean fps, p50 / p99 / max frame time, dropped-frame count
  (interval > 1.5 × refresh interval)
- optional capture: back buffer → staging texture → PNG in `%TEMP%\WaBiBaBuSy\probes\`, path in the reply

and replies `SIGNAL:PROBE:{json}` on stderr. The client adds `clockOffsetMs`, `rttMs`, a perf sample, and
uploads everything via `SubmitProbeResult` (client-streaming: header message, then PNG chunks of 256 KB).

**Metric.** Per node-monitor *i*, converted to server time with that node's offset:
`error_i = renderedElapsedMs_i − (presentServerUtcMs_i − sharedStartServerUtcMs) − phaseMs_i`,
where `phaseMs_i` is the configured wave / node delay. `driftSpread = max(error_i) − min(error_i)`.
Pass ≤ `driftWarnMs`, warn ≤ `driftSpreadMs`, fail above. `mean(error)` is reported separately as the
common latency (same on all nodes → invisible, but tells how far behind wall time the whole wall runs).

**Limit (stated in every report).** Local→server conversion uses the same clock-offset estimate the sync uses.
The probe therefore catches render / present / start-lead / config errors, but **not** an error in the clock
estimate itself. The report prints each node's RTT/2 as the bound on that error. The timecode strip (§5.4)
lets a phone photo of two screens verify physical sync as an optional manual step.

### 5.2 Position check (visual parity)

For probes with `capture: true` on marker scenes, the server runs `MovementCalculator` with **that node's**
parameters (canvas width, `MonitorOffsetX`, seat / ring mapping, facing, physical-unit scale) at the node's
`renderedElapsedMs`, and `MarkerDetector` finds the marker centroid in the PNG.
`positionErrorPx = |detected − expected|`, "marker expected off-screen" and "marker not found" are explicit
outcomes. This catches bezel jumps, wrong offsets, ring-seam teleports, facing-row mirroring and cm-scale
mistakes without any tolerance for timing (expected position is computed at the elapsed actually rendered).

For non-marker content scenes the captures are only placed side by side in seat order for visual review.

### 5.3 Exact frame (pixel parity)

`cmd_probe { exactElapsedMs }` renders **one offscreen frame at exactly that elapsed value** into a D2D bitmap
render target (not presented) and reads it back. Nodes whose frames must be identical (Simultaneous mode,
same resolution — Pattern, Traveling Colors, RandomWalk, multi-image) are compared pixel by pixel:
`pixelDiffPct` = share of pixels with any channel difference > 2. Mismatch → fail, with a diff image
in the report.

Requires a small refactor in `Player.D2D`: the frame-drawing function takes `elapsedMs` (and target) as
parameters instead of calling `ComputeEffectiveElapsedMs()` inside. The render loop passes the live value; the
math is unchanged.

### 5.4 Timecode strip

In test mode each player draws, bottom-left, a 32-bit binary strip of `renderedElapsedMs` (32 cells of
8×8 px, black/white, with a start/stop guard pattern) plus the same number as text. It makes every screenshot
self-describing and is designed to be decoded from a phone photo later.

### 5.5 Simulated clock skew

`TEST_MODE { simulatedClockSkewMs }` adds a constant to the client's clock source (the one feeding
`ClockOffsetEstimator` and the player start-time conversion) — introduced as a small `IClock` seam in the
client. This covers the checklist "skew ±2 s" test without changing Windows time; expected result: the
estimated offset shows ≈ the skew, drift spread stays within threshold.

### 5.6 Perf samples

On `probeSeries` ticks with `perf: true`: CPU % and working set of the client app and every player process
(`Process` API, delta over the interval), GPU utilisation from the PDH "GPU Engine" counters filtered by
player PID (optional — `null` when counters are unavailable). Thresholds from the MVP table: CPU < 15 %,
GPU < 10 %, memory < 200 MB per client.

## 6. Run flow

1. **Preflight.** Check `requires`, "Allow test runs" on every node, build version per node (mismatch = warning),
   record monitors, refresh rates, clock offset and RTT per node.
2. **Assets.** Register and prefetch every asset of every scene in the scenario; wait for "✓ cached" on all
   nodes (timeout 120 s); prefetch duration goes into the report.
3. **Steps.** Execute in order; every step has a timeout (default 30 s, `probeSeries` = `forMs` + 30 s).
   Toolbar shows progress; the running step is visible via `GET /test/status`.
4. **Collect.** Fetch logs from every node for the run's time window (fixed FETCH_LOGS path, §2 item 1, extended
   with `fromUtcMs` / `toUtcMs`).
5. **Report.** Write the results folder; send `TEST_MODE { timecode: false, simulatedClockSkewMs: 0 }` to all
   nodes; restore the scene that was playing before the run.

## 7. Results folder

`%LOCALAPPDATA%\WaBiBaBuSy\TestRuns\<yyyy-MM-dd_HHmm>_<scenario>\`

- `report.json` — environment (commit hash, app version, node list with seat order, monitors, resolutions,
  refresh rates, clock offsets, RTT), every raw probe result, every computed metric, verdict per step.
  This is the file for automated analysis.
- `report.html` — self-contained (inline CSS/JS/SVG, PNGs referenced relatively): summary with
  pass / warn / fail per step, drift-spread-over-time chart, per-node error lines, perf table,
  per-probe screenshot strip in seat order with expected-vs-detected marker overlay, exact-frame diff images.
- `nodes/<nodeName>/<step>-<probe>-mon<k>.png`
- `logs/<nodeName>.log`

## 8. Error handling

| Situation | Behaviour |
|---|---|
| Node does not answer a probe within 2 s after the instant | Marked `missing` for that probe; run continues |
| Node disconnects mid-run | Failure entry with timestamp; remaining steps run on the remaining nodes |
| Player cannot capture / read back | `SIGNAL:PROBE:{error}`; timing still reported without PNG |
| Frame statistics unavailable | `presentUtcMs = null`, fall back to render time, noted in the report |
| Step timeout | Step = fail, run continues with the next step |
| Cancel (UI / `DELETE /test/run`) or app shutdown | Partial report written, test mode switched off on all nodes |
| Scenario JSON invalid / scene file missing | Rejected before preflight with the path and error; nothing is played |

## 9. Deferred

- **Desktop Duplication "real screen" capture mode** — what is actually visible, incl. desktop icons, at the
  cost of covering windows, privacy and capture jitter.
- **Timecode decoding from a phone photo/video** — the strip is designed for it; the decoder is later work.
- **Spec 2:** Show reliability + Failure & recovery (needs a fault-injection command: drop the stream for N s,
  restart client app).

## 10. Testing

- **Unit (`WaBiBaBuSy.Tests`, Models only):** scenario JSON round-trip and validation; `DriftAnalyzer`
  (phase subtraction, spread, thresholds, missing nodes); `MarkerDetector` on generated PNGs (clean, partially
  off-screen, absent); `ExpectedPosition` for Sequential, Simultaneous, Ring seam, facing rows, physical units;
  timecode encode/decode round-trip.
- **Manual single machine:** `--test-run TestScenarios/sync-basic.json --exit` on the server alone → exit code 0,
  green report with the server's monitors as nodes.
- **Manual multi-machine:** first real run with 1–2 remotes replaces checklist §3 and the timing parts of §4.

## 11. Bundled scenarios (v1)

| File | Covers checklist |
|---|---|
| `sync-basic.json` | §3 clean start, §4 10-minute run with perf |
| `parity-matrix.json` | §3: Linear Sequential across machines, SineWave + Reversed, Simultaneous + Wave, Pattern + Traveling Colors (exact frame), IconZone global path, multi-image / background asset, Ring seam, facing rows, mixed resolutions (pixel mode), physical units, face travel direction |
| `clock-skew.json` | §4 skew ±2 s (simulated) |

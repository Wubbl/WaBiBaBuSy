# Automated Test Mode — Deferred Follow-ups

**Created:** 2026-09-30 · **Resolved:** 2026-10-02 · Minor findings from the per-task and final reviews of spec 1. On 2026-10-02 every item was re-checked against the code and fixed, found already fixed, or closed with a reason (commits `f5806dc` … `e0828aa`; 437 → 553 unit tests). What is still open is in **Remaining** at the bottom.

Legend: **F** fixed (commit) · **AF** already fixed before 2026-10-02 · **WF** won't fix (reason).

## Priority items

| Item | Status |
|---|---|
| Task 15 — control-API body binding (415/400) ran before the loopback filter → a LAN peer could detect test mode | **F** `f5806dc`: gate is middleware on the `/test` prefix, before routing's own 415/405 endpoints and binding (404 for any non-local connection) |
| Task 16 — second monitor often `PresentLocalUtcMs = null`; mixed fallback/measured samples distort spread | Spread: **AF** `cadca6c` (present-time samples only when ≥ 2). Root cause found 2026-10-02 (`d36c525` diagnostic): `GetFrameStatistics` returns **S_OK with all-zero statistics** in some desktop states — reproducible with both the pre- and post-fix builds at 21:02–21:04 while the same build resolved 16/16 at 20:34. Environmental, not a code regression; see Remaining |
| Task 12 — spec §8 "node disconnects mid-run → failure entry with timestamp" | Entry on `NoCommandStream`: **AF** `c7f8031`. Detection on silent/dropped nodes: **F** `27c18ef` (node list checked after every step and when a remote misses a probe) |

## Task 1 — client log fetch
- Waiter overwrite on concurrent fetches — **F** `e9f2938` (per-client gate, `WaitAsync`, no lingering delay)
- Windowed fetch read only today's file — **F** `e9f2938` (`ReadLogsForUploadAsync` reads every daily file the window touches, ≤ 7 days)
- UI hint hardcoded the log folder — **F** `59a90a0` ("configured log folder … (default …)")
- No tests for upload paths / waiter completion — **F** `e9f2938` (`ClientLogUploadTests`, `WallpaperSyncServiceTestModeTests`, `SyncServiceHarness`)
- Also: test-run log fetches popped open the main window's logs panel — **F** `f5949c4`

## Task 2 — thumbnail capture
- HWND never re-assigned after `ClearWallpaperHwnd` — **F** `59a90a0` (`ThumbnailCaptureService.ShouldAdopt`)
- Capture service null after reconnect / on the non-cross-screen path — **F** `59a90a0` (one app-wide capture service handed to every client)

## Task 3 — scenario / scene loading
- JSON nulls, missing scene file — **AF** `ff7e31e`; NUL in a path → `ScenarioException` **F** `3c2d691`
- Sync `File.ReadAllText` — **WF** (by design; small one-off reads before a run)
- Test gaps — **F** `3c2d691`
- (Task 15, Models part) UNC / device scene and asset paths hit `File.Exists` over SMB — **F** `3c2d691` (`ScenarioLoader.IsRemoteOrDevicePath`; scenarios on a UNC share are refused; mapped drives are not detected)

## Task 4 — probe models
- `RemoteProbeResult.Captures` STJ-only `[JsonIgnore]` — **WF** (correct: STJ on both ends, Newtonsoft only carries `PlayerProbeReply`); regression test added
- XML docs, `Reset()` test, `Worst` cases — **F** `619942b`

## Task 5 — expected positions
- Ring two-copy wrap / Y-edge Partial tests, `CanvasHeight <= 0` guard — **F** `619942b`
- `MarkerExpectation` mutable — **WF** (plan-mandated; built with object initializers)

## Task 6 — image checks
- XML docs, buffer-length guards (`BgraBuffer.Require`), Unknown-orientation / MinPixels tests — **F** `619942b`
- OffScreen + no capture → Pass vs Visible + no capture → Skipped — **F** `619942b` (both Skipped)

## Task 7 — player capture / IPC
- `ImmediateContext` AddRef, SavePng dims — **AF** `cadca6c`
- Malformed `cmd_test_mode` / `cmd_probe` broke stdout pairing — **F** `eb555f3` (error reply on stderr instead)
- Capture stall before Present — **AF** `cadca6c`; leftover long interval in a later probe's frame stats — **WF** (needs a tracker "skip next interval" API; small)
- Queued probes only flushed on the normal end-of-frame path — **F** `eb555f3` (`AnswerStrandedProbes`: error reply after 1 s, flush on loop stop)
- Start-command comment — **F** `eb555f3`

## Task 8 — exact frames
- Eligibility check form — **F** `eb555f3`
- One readback + GPU sync per exact probe — **WF** (rare, offscreen, test-only)
- Pushed layer + throw → swallowed `EndDraw` result — **F** `eb555f3` (logged); fault injection — **WF** (needs a live player)

## Task 9 — player host
- PROBE handler exception types — **AF** `cadca6c`; parsing moved to `ProbeFanOut.ParseProbeSignal` with 14 tests — **F** `4559c81`
- Uncancelled `Task.Delay`, cancelled ct → null — **F** `4559c81` (`ProbeFanOut.WaitForReplyAsync`: timeout → null, cancel → OCE)
- `FirstOrDefault` host only — **WF** (one host per composition service by construction)
- `SetTestModeAsync` swallows per-host failures — **WF** (logged; fire-and-forget caller; the next probe shows the error)

## Task 10 — perf / client state
- `PerfSampler` never forgets PIDs — **F** `0b3d383`
- Skew / TestModeState survive an aborted run — **AF** `c7f8031`
- TEST_MODE vs new-player registration race, `TestProbeTargets` only on the connect path — **F** `59a90a0`
- `ReadyTimestampUtc` real UTC — **F** `e9f2938` (sync clock incl. skew; no callers today)
- No test of skewed scheduling — **WF** for now (needs a clock seam in `WallpaperPlaybackService`; see Remaining)

## Task 11 — server test channel
- Multicast invoke, cancel → null + lingering delay, `_waiters` overwrite, XML docs — **F** `e9f2938`
- Capture size cap (128 MB, 16 monitors) and client_id must own an open command stream — **F** `e9f2938`; real client authentication — **WF** (no RPC in the protocol authenticates clients; LAN-only product)

## Task 12 — runner
- Node-name collisions, OCE filter, long `at` offsets, cancel-in-preflight warnings, scenes loaded before preflight probes, step-timeout test, run-level OCE check, cancelled step DurationMs / Verdict, ProbeReport.Error in step messages, `EvaluateParity` peak memory, concurrent-cancel test, unobserved tasks — **F** `0b3d383`
- Sequential-mode series stretched by a silent node — **WF** (ruling: one pending-probe slot per player)

## Task 13 — report
- DriftChart culture — **AF** `c7f8031` (de-DE test added); Error rendering / hostile-name tests, empty `<p>`, RenderHtml throw after report.json — **F** `0b3d383`

## Task 14 — UI
- Off-thread restore read, cached `IsTestModeEnabled` + refresh after Settings save, guarded OpenTestResults, double-click picker guard, port clamp — **F** `0f770cd` (build-verified only)
- Running show restores as "nothing playing" — **WF** for now (see Remaining)
- Manual Settings GUI check — open (2026-09-28 checklist)

## Task 15 — CLI / API / host
- API/CLI runs not shown in the menu — **F** `0f770cd` (`ObservedTestRunControl`)
- Port 50052 bind failure killed the gRPC start — **F** `0f770cd` (retries without the test listener; unit-tested)
- WinExe console output — **F** `0f770cd` (`CliConsole` attaches to the parent console per write; use `start /wait` / `Start-Process -Wait` for the exit code)
- Stray args — **F** `0f770cd` (warnings + usage)
- `\??\` paths — **F** `0f770cd` (explicit device-path rejection); VM sync start exceptions — **AF** `ff7e31e`; `FreePort()` collision — **F** `0f770cd`

## Task 16 — docs / stats
- Design §3.1 POST response — **AF** `a16b788`
- `FrameIntervalTracker.Snapshot` allocation — **F** `619942b` + `9a82b1b` (`MeanIntervalMs` on the render thread; timecode strip encodes into a `stackalloc` span)

## Final-review parked items
- Skew check false-fail before the first post-reset heartbeat — **F** `0b3d383` (deferred while offset and RTT are both 0)
- Staging texture leak if Present throws — **F** `eb555f3`
- Dispose stall ≤ 500 ms per player on the stdin lock — **WF** (bounded; shorter risks a torn JSON line)

---

## Remaining

1. **Zero frame statistics (Task 16 root cause).** In some desktop states `IDXGISwapChain::GetFrameStatistics` returns S_OK with `PresentCount = 0` / `SyncRefreshCount = 0` for the wallpaper windows, so every probe falls back to render time (the report says so; drift spread is then render-time based and misses the 6–17 ms present latency). Which state triggers it is unknown — it switched between 20:34 and 21:02 on 2026-10-02 on the dev machine with no code change. Next step: note what is on screen when it happens (maximized / fullscreen window over the desktop, monitor sleep, HDR, refresh-rate change), and consider a fallback source such as `DwmGetCompositionTimingInfo` (`qpcVBlank`, `cFrame`) for composed windows.
2. **Restore a running show after a test run** — needs the running `Playlist` from `PlaylistOrchestrator` and an `ITestHost` restore that is not limited to `CrossScreenConfig`.
3. **Skewed-scheduling unit test** — needs an injectable clock (or `InternalsVisibleTo`) in `WallpaperPlaybackService` / `WallpaperSyncClient`.
4. **Log fetch correlation id** — a reply that arrives after its fetch timed out can complete the next queued fetch for that client; fix = request id in `ClientLogData` / FETCH_LOGS (proto change).
5. **Manual checks** of the build-verified UI and player changes (menu status for CLI/API runs, CLI console line, Settings refresh, stranded-probe replies) — fold into the 2026-09-28 checklist run.
6. Not test mode, found in passing: `TrayViewModel.Connect` never wires the cross-screen apply/stop delegates, so a client connected from the tray without the main window cannot play cross-screen scenes. Tracked in `.docs/2025.12_OpenIssues.md`.

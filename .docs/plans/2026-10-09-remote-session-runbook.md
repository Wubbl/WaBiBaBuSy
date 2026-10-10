# Remote Session Runbook (2026-10-09 evening)

**Purpose:** the first real multi-machine run. This page is the *order* and the *commands*; the tick-boxes live in
[`2026-10-04-manual-test-checklist.md`](2026-10-04-manual-test-checklist.md) (§n below = that file). Tick there, note
report paths in its Results table, file failures in `.docs/2025.12_OpenIssues.md`.

**Skip tonight:** checklist §1–§3 (server alone, any day without the other machines) and §9 (optional).

---

## Pre-flight (done 2026-10-09 afternoon, commit `da7215e`, server machine only)

| Check | Result |
|---|---|
| `dotnet build` | 0 errors, 0 warnings |
| `dotnet test WaBiBaBuSy.Tests` | 867 / 867 green |
| `smoke-local.json` (CLI, `--exit`) | pass, exit 0 (`TestRuns\2026-10-09_1433_smoke-local`) |
| `parity-matrix.json` with `minRemoteNodes: 0` (temporary copy, 3 local monitors) | pass, 18/18 steps (`…_1434_parity-matrix-local`) |
| `showcase/events.json` | exit 0; every event visible in its exact frames (confetti, spotlight, traversal, whiteout full-screen, announce) (`…_1435_events`) |

So scenes, steps, exact frames and show events work on one machine. Tonight only checks what needs the network.

## 0. Setup (≈ 20 min) — checklist §0

1. **Same commit everywhere** (the updater refuses mismatched versions): `git pull` → `dotnet build -c Release` on
   every machine, or copy the server's `WaBiBaBuSy.UI\bin\Release\net9.0-windows\` folder to the clients. Note the commit.
2. **Firewall / network:** the network profile must be *Private* on every machine. Neither the dev build nor the installer adds a
   firewall rule, so the first server start asks Windows for one: allow *Private*. Needed: TCP 50051 inbound on the server,
   UDP 5353 (mDNS) for discovery. If a client cannot find the server, type the IP (§5.1 item 1) and check the rule.
3. Settings → Logging → **Log to file** on every machine.
4. Server: Settings → Server → Developer → **Enable test mode**, then restart the server.
5. Clients: "Answer automated test runs from the server" stays on (default).
6. At least one 1080p + 1440p pair. Note whether clocks are NTP-synced.

## 1. Connect (≈ 15 min) — checklist §5.1

Do the **tray-only client** item first (§5.1 items 3–4: connect one client from the tray, play a Sequential scene, *then*
open its window). Automated runs need every client's window open and **Connected**.
Then: tiles appear within ~5 s, arrange 2 rows / facing / Ring, gaps; restart a client → same seat; restart the server → topology intact.

## 2. Automated runs (≈ 45 min) — checklist §4

On the server, from the exe's folder (the build copies `TestScenarios\` next to the exe; PowerShell, `Start-Process -Wait` waits for the run and gives you the exit code):

```powershell
Start-Process -Wait -PassThru .\WaBiBaBuSy.UI.exe '--test-run','TestScenarios\sync-basic.json','--exit' | % ExitCode
```

Or Developer tools → Run test suite… in the main window. Exit code 0 = pass, 1 = a step failed, 2 = aborted / not enough clients (the CLI waits 120 s for them).

| Order | Scenario | Time | Note |
|---|---|---|---|
| 1 | `parity-matrix.json` (pixel mode) | ~2 min | first, because it is short and shows parity problems at once |
| 2 | `parity-matrix.json` with Room ⚙ → Physical units on | ~2 min | turn Physical units off afterwards |
| 3 | `clock-skew.json` | ~1.5 min | first replace `REMOTE-HOSTNAME-1/-2` in the exe folder's copy with the tile names (the repo file stays untouched) |
| 4 | `showcase/events.json` | ~15 s | now with remotes: confetti / spotlight / traversal / whiteout / announce in the remotes' frames too |
| 5 | `recovery.json` | ~3 min | fault injection: 10 s link loss (reconnect time, limit 5 s) + 45 s silence (sweep time); replace `REMOTE-HOSTNAME-1` in the exe folder's copy. Covers the timing of checklist §7 — the manual cable pull in §6 becomes a cross-check |
| 6 | `sync-basic.json` | ~11 min | do §5.2 eyes checks meanwhile; or run the **lost node** variant (§4: pull a cable after ~1 min) |

After each run: note verdict + worst spread, open `report.html` (results in `%LOCALAPPDATA%\WaBiBaBuSy\TestRuns\`).
Zip the whole `TestRuns` folder at the end of the session; it is the evidence for the docs update.

## 3. Eyes only (≈ 30 min) — checklist §5.2

Clean start, Ring seam, facing rows, mixed resolution bezel, physical cm (ruler), preview vs wall, IconZone, multi-image, Wave.
Skip anything a passing `parity-matrix` already covers if time is short (exact-frame parity = identical pixels).

## 4. Show events across machines (≈ 30 min) — checklist §8

Priority: Celebration across the bezel → Spotlight → Traversal on a client that never had the image → Reconnect / Resync during an event → hotkeys (tray only) → interrupts. "Old client" only if a previous build is at hand.

## 5. Show reliability & sync (≈ 40 min) — checklist §6

Build the 3-item "E2E" playlist first (different content incl. a large GIF / video). Prefetch (⬇ → ✓ cached), switches without a pause, ⏭ on all, client restart mid-show (no re-download), ping / offset tooltips, clock skew ±2 s, show + test run restore.

## 6. Failure & recovery (≈ 20 min, last — it is disruptive) — checklist §7

Cable pull ~10 s → **time the reconnect** (target < 5 s after the link is back) · client power-off → tile gone within ~40 s · server stop/start · log files present on every machine.

**Wallpaper restore (fix from 2026-10-09):** after ■ Stop, Clear all, a test run and app exit, every machine shows its own Windows wallpaper again, not the last animation frame (log: "Last player exited, user wallpaper repainted").

## If time is short

Do in this order and stop anywhere: **§2 rows 1, 3, 5, 6** → §8 Celebration + Reconnect → §7 cable pull timing → §6 prefetch. These cover the four claims nothing but a real network can verify: pixel parity, drift, event sync, reconnection.

## After the session

- Tick the checklist, fill its Results table, add report paths.
- Failures → `.docs/2025.12_OpenIssues.md`; then update `.docs/2026.07_OPEN_ITEMS.md` §1 (move passed items out) and `CLAUDE.md` "Current Work".

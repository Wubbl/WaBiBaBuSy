# Automated Multi-Machine Test Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A scenario-driven test run on the server that probes every monitor of every node at shared server instants (timing, position, exact-frame pixels, perf), collects screenshots and logs, and writes `report.json` + `report.html`.

**Architecture:** Pure analysis code (scenario model, drift / position / pixel math) lives in `WaBiBaBuSy.Models/Testing` and is unit-tested. `Player.D2D` answers two new stdin commands (`cmd_test_mode`, `cmd_probe`) with `SIGNAL:PROBE:{json}` on stderr. Clients receive `TEST_MODE` / `TEST_PROBE` over the existing `SyncStream` and upload results through a new client-streaming RPC. A `TestRunner` in Core drives the steps through two seams, `ITestHost` (implemented by the UI) and `ITestTransport` (implemented over gRPC), so it can be tested with fakes. Three entry points (Developer tools menu, `--test-run` CLI switch, localhost HTTP API) share one `TestRunCoordinator`.

**Tech Stack:** .NET 9, C# 12, xUnit 2.9, gRPC (Grpc.AspNetCore 2.83), System.Text.Json, Newtonsoft.Json (player IPC only), Vortice 3.8.3 (D3D11/DXGI/D2D), System.Drawing (PNG encode/decode), Avalonia 12 + CommunityToolkit.Mvvm.

**Spec:** [`2026-09-30-automated-test-mode-design.md`](2026-09-30-automated-test-mode-design.md)

## Global Constraints

- Target frameworks unchanged except `WaBiBaBuSy.Tests` → `net9.0-windows` (Task 12; needed to reference Core).
- C# 12+, nullable enabled, file-scoped namespaces, XML docs on public APIs, async/await for all I/O (CLAUDE.md).
- The deterministic render math is not changed. New player code runs only when `_testTimecode`, a pending probe, or a nonzero `_testClockSkewMs` is set. The only shared-path edits are (a) replacing `DateTime.UtcNow` with `NowUtc` in `ComputeEffectiveElapsedMs` and the start command (identical result when skew = 0), and (b) extracting the GIF frame draw into `DrawNativeGifScene(elapsedMs)` with the same calls in the same order.
- No DXGI / D3D work in the main process. Readback and frame statistics live in `Player.D2D`.
- New player commands never write to stdout (the host pairs commands with the next stdout line). Replies go to stderr as `SIGNAL:PROBE:{json}`.
- Probe instants are **server UTC ms**, always ≥ now + 500 ms (`TestRunnerOptions.MinLeadMs`). A node that does not reply within 2 s after the instant (`ProbeGraceMs`) is `missing`.
- Server setting `EnableTestMode` default **false**; client setting `AllowTestRuns` default **true**; control API port **50052**, bound to `127.0.0.1` only, HTTP/1.1, only when `EnableTestMode` is on.
- Threshold defaults: `driftSpreadMs` 50, `driftWarnMs` 25, `positionErrorPx` 3, `pixelDiffPct` 0.5, `maxCpuPercent` 15, `maxGpuPercent` 10, `maxMemoryMb` 200.
- Results folder: `%LOCALAPPDATA%\WaBiBaBuSy\TestRuns\<yyyy-MM-dd_HHmm>_<scenario>\` with `report.json`, `report.html`, `nodes/<node>/*.png`, `logs/<node>.log`.
- Proto field numbers: `CommandType.TEST_MODE = 10`, `TEST_PROBE = 11`; `SyncParameters` 27–34 as listed in Task 1 / Task 10; `ConnectedClient.app_version = 14`.
- Trunk-based: commit directly on `main` after each task (user preference). End every commit message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Build/test commands: `dotnet build WaBiBaBuSy.sln -c Debug` (0 errors) and `dotnet test WaBiBaBuSy.Tests`.

## Review Focus

1. **A probe instant before the shared start or before a node's wave phase** (negative effective elapsed): drift is still computed from the formula, and the position check is `Skipped` ("marker not yet shown"), not `Fail`. Pinned by `ExpectedPositionTests.NegativeElapsed_IsNotOnScreen` (Task 5) and `DriftAnalyzerTests.NegativeElapsed_StillComparable` (Task 4).
2. **A node that replies with an error or not at all**: it is left out of the spread, listed under `MissingNodes`, and the verdict is at least `Warn`. With zero valid samples the verdict is `Skipped`. Pinned by `DriftAnalyzerTests.MissingNode_*` (Task 4) and `TestRunnerTests.RemoteNeverReplies_ProbeMarkedMissing` (Task 12).
3. **A marker straddling a bezel or the Ring seam** gives a partial blob: the position check is `Skipped` ("marker straddles the edge"), never `Fail`. Pinned by `ExpectedPositionTests.StraddlingEdge_IsPartial` (Task 5) and `ImageCheckTests.PartialMarker_Skipped` (Task 6).
4. **A malformed scenario** (unknown step type, wrong-case type, missing scene file, unparsable `at`, `forMs < everyMs`) is rejected before anything plays, with the path and the reason. Pinned by `ScenarioLoaderTests.*Invalid*` (Task 3).
5. **Cancelling mid-series** (UI Cancel, `DELETE /test/run`, app exit) still writes a partial report and switches test mode off on every node. Pinned by `TestRunnerTests.Cancel_WritesPartialReport_AndTurnsTestModeOff` (Task 12).

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `WaBiBaBuSy.Common/IO/LogTail.cs` | Read the tail of a file another writer holds open; filter by local time window | 1 |
| `WaBiBaBuSy.Models/Testing/TestScenario.cs` | Scenario, requirements, thresholds, step types (JSON polymorphic) | 3 |
| `WaBiBaBuSy.Models/Testing/ScenarioLoader.cs` | Parse + validate scenario JSON, resolve paths, `ProbeAt` parsing | 3 |
| `WaBiBaBuSy.Models/Testing/SceneFile.cs` | Load a `CrossScreenConfig` JSON and make its asset paths absolute | 3 |
| `WaBiBaBuSy.Models/Testing/ProbeModels.cs` | Probe DTOs: requests, `PlayerProbeReply`, `NodeProbeSample`, `RemoteProbeResult`, `PerfSample`, `FrameIntervalStats`, `Verdict` | 4 |
| `WaBiBaBuSy.Models/Testing/FrameIntervalTracker.cs` | Ring buffer of frame intervals → fps / p50 / p99 / max / dropped | 4 |
| `WaBiBaBuSy.Models/Testing/DriftAnalyzer.cs` | Per-node timing error, spread, verdict | 4 |
| `WaBiBaBuSy.Models/Testing/ExpectedPosition.cs` | Where the marker must be on a node at a given elapsed | 5 |
| `WaBiBaBuSy.Models/Testing/MarkerDetector.cs` | Find the magenta marker + dot orientation in a BGRA buffer | 6 |
| `WaBiBaBuSy.Models/Testing/PixelDiff.cs` | Compare two BGRA frames, produce a diff image | 6 |
| `WaBiBaBuSy.Models/Testing/TimecodeStrip.cs` | 40-cell binary strip encode/decode | 6 |
| `WaBiBaBuSy.Models/Testing/PositionCheck.cs` | Combine expected/player/detected into a verdict | 6 |
| `WaBiBaBuSy.Player.Common/Messages/PlayerCommandTestMode.cs`, `PlayerCommandProbe.cs` | Player IPC commands | 7 |
| `WaBiBaBuSy.Player.D2D/Program.cs` | Clock skew, timecode strip, frame tracker, live probe, readback, present stats, exact frame | 7, 8 |
| `WaBiBaBuSy.Core/Services/Testing/ITestProbeTarget.cs`, `ProbeFanOut.cs` | One monitor's player as a probe target; fan-out helper | 9 |
| `WaBiBaBuSy.WallpaperEngine/Direct2D/D2DPlayerHost.cs`, `Composition/D2DCompositionService.cs` | Send test commands, await `SIGNAL:PROBE` | 9 |
| `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` | New command types, params, `SubmitProbeResult` RPC, `app_version` | 1, 10, 11 |
| `WaBiBaBuSy.Core/Services/Testing/PerfSampler.cs` | CPU / memory / GPU sample for app + player processes | 10 |
| `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs` | Handle TEST_MODE / TEST_PROBE, skewed clock, upload | 1, 2, 10 |
| `WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs` | Receive probe results, awaitable log fetch | 1, 11 |
| `WaBiBaBuSy.Core/Services/Testing/ServerTestChannel.cs` | `ITestTransport` over gRPC | 11 |
| `WaBiBaBuSy.Core/Services/Testing/ITestHost.cs`, `ITestTransport.cs`, `TestRunner.cs`, `TestRunnerOptions.cs`, `PngPixels.cs` | Scenario execution | 12 |
| `WaBiBaBuSy.Models/Testing/TestRunReport.cs` | Report model | 12 |
| `WaBiBaBuSy.Core/Services/Testing/TestReportWriter.cs` | `report.json` + `report.html` | 13 |
| `WaBiBaBuSy.UI/Services/Testing/TestHostAdapter.cs`, `ViewModels/MainWindowViewModel.Testing.cs` | UI side of `ITestHost`; menu / progress | 14 |
| `WaBiBaBuSy.Core/Services/Testing/TestRunCoordinator.cs`, `TestControlEndpoints.cs`; `WaBiBaBuSy.UI/Program.cs`, `StartupOptions.cs`, `App.axaml.cs` | One active run; HTTP API; CLI | 15 |
| `TestScenarios/**` | Bundled scenarios, scenes, marker asset | 16 |

---

### Task 1: Fix "View logs" hang + awaitable log fetch

Root cause (spec §2.1): `FileLoggerProvider` keeps today's log open with `new StreamWriter(path, append: true)` (share mode `Read`). The client reads it with `File.ReadAllLinesAsync`, which asks for share mode `Read` only, so the open fails while the writer holds write access. The exception is caught and only logged, nothing is sent back, and the UI stays at "Requesting logs…".

**Files:**
- Create: `WaBiBaBuSy.Common/IO/LogTail.cs`
- Create: `WaBiBaBuSy.Tests/LogTailTests.cs`
- Modify: `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` (`SyncParameters` fields 27–28)
- Modify: `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs` (`SendLogsToServerAsync` ≈ `:747`, FETCH_LOGS branch ≈ `:882`)
- Modify: `WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs` (`SendClientLogs` ≈ `:1240`, `RequestClientLogsAsync` ≈ `:1262`)
- Modify: `WaBiBaBuSy.Core/Services/WaBiBaBuSyService.cs` (`RequestClientLogsAsync` ≈ `:603`)
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (`FetchClientLogs` ≈ `:1958`)

**Interfaces:**
- Produces: `LogTail.ReadLastLinesAsync(string path, int maxLines, CancellationToken ct = default) → Task<IReadOnlyList<string>>`; `LogTail.FilterWindow(IReadOnlyList<string> lines, TimeSpan fromLocal, TimeSpan toLocal) → IReadOnlyList<string>`
- Produces: `WallpaperSyncService.RequestClientLogsAsync(string clientId, long fromUtcMs = 0, long toUtcMs = 0) → Task<bool>`; `WallpaperSyncService.FetchClientLogsAsync(string clientId, long fromUtcMs, long toUtcMs, TimeSpan timeout) → Task<ClientLogData?>`; `WaBiBaBuSyService.FetchClientLogsAsync(same) → Task<ClientLogData?>`. Task 11 uses `WallpaperSyncService.FetchClientLogsAsync`.
- Produces proto: `SyncParameters.log_from_utc_ms = 27`, `log_to_utc_ms = 28`.

- [ ] **Step 1: Write the failing tests**

`WaBiBaBuSy.Tests/LogTailTests.cs`:
```csharp
using WaBiBaBuSy.Common.IO;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// "View logs" hung because the client read its own log with File.ReadAllLinesAsync while
/// FileLoggerProvider held the file open for writing. LogTail must read through that lock.
/// </summary>
public class LogTailTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wbbs-logtail-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
    }

    [Fact]
    public void RootCause_ReadAllLines_FailsWhileLoggerHoldsTheFile()
    {
        using var writer = new StreamWriter(_path, append: true) { AutoFlush = true };   // FileLoggerProvider's open mode
        writer.WriteLine("[12:00:00.000 INF] [Test] hello");
        Assert.ThrowsAny<IOException>(() => File.ReadAllLines(_path));
    }

    [Fact]
    public async Task ReadLastLines_WhileLoggerHoldsTheFile_ReturnsTheTail()
    {
        using var writer = new StreamWriter(_path, append: true) { AutoFlush = true };
        for (int i = 0; i < 10; i++) writer.WriteLine($"[12:00:0{i}.000 INF] [Test] line {i}");

        var lines = await LogTail.ReadLastLinesAsync(_path, 3);

        Assert.Equal(new[]
        {
            "[12:00:07.000 INF] [Test] line 7",
            "[12:00:08.000 INF] [Test] line 8",
            "[12:00:09.000 INF] [Test] line 9",
        }, lines);
    }

    [Fact]
    public async Task ReadLastLines_FewerLinesThanMax_ReturnsAll()
    {
        await File.WriteAllLinesAsync(_path, new[] { "a", "b" });
        Assert.Equal(new[] { "a", "b" }, await LogTail.ReadLastLinesAsync(_path, 500));
    }

    [Fact]
    public void FilterWindow_KeepsStampedLinesInWindow_AndTheirContinuationLines()
    {
        var lines = new[]
        {
            "[11:59:59.999 INF] [A] before",
            "[12:00:00.000 ERR] [A] inside",
            "   at Some.Stack.Trace()",
            "[12:00:05.000 INF] [A] inside too",
            "[12:00:05.001 INF] [A] after",
        };

        var result = LogTail.FilterWindow(lines, new TimeSpan(12, 0, 0), new TimeSpan(0, 12, 0, 5, 0));

        Assert.Equal(new[] { lines[1], lines[2], lines[3] }, result);
    }

    [Fact]
    public void FilterWindow_WindowCrossingMidnight_ReturnsEverything()
    {
        var lines = new[] { "[23:59:59.000 INF] x", "[00:00:01.000 INF] y" };
        Assert.Equal(lines, LogTail.FilterWindow(lines, new TimeSpan(23, 59, 0), new TimeSpan(0, 1, 0)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~LogTailTests`
Expected: build error `The type or namespace name 'IO' does not exist in the namespace 'WaBiBaBuSy.Common'`.

- [ ] **Step 3: Implement `LogTail`**

`WaBiBaBuSy.Common/IO/LogTail.cs`:
```csharp
using System.Globalization;

namespace WaBiBaBuSy.Common.IO;

/// <summary>
/// Reads the tail of a log file that another writer (the app's own rolling file logger) keeps open.
/// </summary>
public static class LogTail
{
    /// <summary>
    /// The last <paramref name="maxLines"/> lines of <paramref name="path"/>. Opens with
    /// <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>, so a
    /// <see cref="StreamWriter"/> holding the file for appending does not block the read.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadLastLinesAsync(string path, int maxLines, CancellationToken ct = default)
    {
        var tail = new Queue<string>(Math.Max(1, maxLines));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (maxLines <= 0) continue;
            if (tail.Count == maxLines) tail.Dequeue();
            tail.Enqueue(line);
        }
        return tail.ToArray();
    }

    /// <summary>
    /// Lines whose leading <c>[HH:mm:ss.fff</c> local timestamp lies in [<paramref name="fromLocal"/>,
    /// <paramref name="toLocal"/>] (local time of day). Unstamped lines (stack traces) follow the
    /// preceding stamped line. A window that crosses midnight returns every line.
    /// </summary>
    public static IReadOnlyList<string> FilterWindow(IReadOnlyList<string> lines, TimeSpan fromLocal, TimeSpan toLocal)
    {
        if (toLocal < fromLocal) return lines;
        var result = new List<string>();
        bool inWindow = false;
        foreach (var line in lines)
        {
            if (TryParseTime(line, out var time)) inWindow = time >= fromLocal && time <= toLocal;
            if (inWindow) result.Add(line);
        }
        return result;
    }

    // FileLoggerProvider format: "[12:34:56.789 INF] [Category] message"
    private static bool TryParseTime(string line, out TimeSpan time)
    {
        time = default;
        return line.Length >= 13 && line[0] == '['
            && TimeSpan.TryParseExact(line.AsSpan(1, 12), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out time);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~LogTailTests`
Expected: 5 passed.

- [ ] **Step 5: Add the log window to the proto**

In `wabibabusy.proto`, `message SyncParameters`, after `repeated ContentRef assets = 26;`:
```proto
  int64 log_from_utc_ms = 27;          // FETCH_LOGS: only lines from this UTC instant (0 = last 500 lines)
  int64 log_to_utc_ms = 28;            // FETCH_LOGS: only lines up to this UTC instant
```

- [ ] **Step 6: Client — read through the lock, always answer**

In `WallpaperSyncClient.cs` add `using WaBiBaBuSy.Common.IO;`. Replace `SendLogsToServerAsync` with:
```csharp
    /// <summary>
    /// Upload the local log file to the server (FETCH_LOGS). Always answers: a read failure is
    /// sent back as text so the server never waits forever. A nonzero window returns only the
    /// lines between the two UTC instants (test runs).
    /// </summary>
    public async Task SendLogsToServerAsync(string logDirectory, long fromUtcMs = 0, long toUtcMs = 0)
    {
        if (_client == null || string.IsNullOrEmpty(_clientId))
        {
            _logger.LogWarning("Cannot send logs - not connected");
            return;
        }

        var logDate = DateTime.Today.ToString("yyyy-MM-dd");
        var logPath = Path.Combine(logDirectory, $"wabibabusy-{logDate}.log");
        string logContent;
        try
        {
            if (File.Exists(logPath))
            {
                bool windowed = fromUtcMs > 0 && toUtcMs >= fromUtcMs;
                var lines = await LogTail.ReadLastLinesAsync(logPath, windowed ? 20000 : 500);
                if (windowed)
                    lines = LogTail.FilterWindow(lines, ToLocalTimeOfDay(fromUtcMs), ToLocalTimeOfDay(toUtcMs));
                logContent = string.Join(Environment.NewLine, lines);
            }
            else
            {
                logContent = $"[No log file at {logPath} — enable Settings → Logging → Log to file on this machine]";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read log file {Path}", logPath);
            logContent = $"[Could not read {logPath}: {ex.Message}]";
        }

        try
        {
            var response = await _client.SendClientLogsAsync(new ClientLogData
            {
                ClientId = _clientId,
                LogContent = logContent,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LogDate = logDate
            });
            _logger.LogInformation("Logs sent to server: {Success}", response.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending logs to server");
        }
    }

    private static TimeSpan ToLocalTimeOfDay(long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToLocalTime().TimeOfDay;
```

In the SyncStream loop, replace the FETCH_LOGS branch so it uses the configured log directory and the window:
```csharp
                    if (command.Type == CommandType.FetchLogs)
                    {
                        _logger.LogInformation("Server requested logs - sending log file");
                        var logDir = ConfigurationManager.LoadLoggingConfiguration().LogDirectory;
                        long from = command.Params?.LogFromUtcMs ?? 0, to = command.Params?.LogToUtcMs ?? 0;
                        _ = Task.Run(() => SendLogsToServerAsync(logDir, from, to));
                        continue;
                    }
```
(`ConfigurationManager` lives in `WaBiBaBuSy.Models.Configuration`, already imported.)

- [ ] **Step 7: Server — report send failures, awaitable fetch**

In `WallpaperSyncService.cs` add a field next to `_clientLogs`:
```csharp
    // FetchClientLogsAsync waiters, completed by SendClientLogs (one pending fetch per client).
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClientLogData>> _logWaiters = new();
```
In `SendClientLogs`, after `_clientLogs[request.ClientId] = request;`:
```csharp
        if (_logWaiters.TryGetValue(request.ClientId, out var waiter))
            waiter.TrySetResult(request);
```
Replace `RequestClientLogsAsync` and add `FetchClientLogsAsync`:
```csharp
    /// <summary>
    /// Ask a client for its log (FETCH_LOGS). False when the client has no command stream.
    /// A nonzero window limits the reply to lines between the two UTC instants.
    /// </summary>
    public async Task<bool> RequestClientLogsAsync(string clientId, long fromUtcMs = 0, long toUtcMs = 0)
    {
        var command = new SyncCommand
        {
            Type = CommandType.FetchLogs,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ContentId = clientId,
            Params = new SyncParameters { LogFromUtcMs = fromUtcMs, LogToUtcMs = toUtcMs }
        };

        bool sent = await SendCommandToClientAsync(clientId, command);
        if (sent) _logger.LogInformation("Requested logs from client {ClientId}", clientId);
        else _logger.LogWarning("Could not request logs from client {ClientId}: no command stream", clientId);
        return sent;
    }

    /// <summary>Request a client's log and wait for the reply; null on send failure or timeout.</summary>
    public async Task<ClientLogData?> FetchClientLogsAsync(string clientId, long fromUtcMs, long toUtcMs, TimeSpan timeout)
    {
        var waiter = new TaskCompletionSource<ClientLogData>(TaskCreationOptions.RunContinuationsAsynchronously);
        _logWaiters[clientId] = waiter;
        try
        {
            if (!await RequestClientLogsAsync(clientId, fromUtcMs, toUtcMs)) return null;
            var finished = await Task.WhenAny(waiter.Task, Task.Delay(timeout));
            return finished == waiter.Task ? await waiter.Task : null;
        }
        finally
        {
            _logWaiters.TryRemove(new KeyValuePair<string, TaskCompletionSource<ClientLogData>>(clientId, waiter));
        }
    }
```

In `WaBiBaBuSyService.cs` replace `RequestClientLogsAsync` and add the pass-through:
```csharp
    public async Task<bool> RequestClientLogsAsync(string clientId)
    {
        if (_serverHost?.SyncService == null)
        {
            _logger.LogWarning("Cannot request logs - server not running");
            return false;
        }
        return await _serverHost.SyncService.RequestClientLogsAsync(clientId);
    }

    /// <summary>Request a client's log and wait for it (server mode); null when unavailable or timed out.</summary>
    public Task<WaBiBaBuSy.Grpc.ClientLogData?> FetchClientLogsAsync(string clientId, long fromUtcMs, long toUtcMs, TimeSpan timeout) =>
        _serverHost?.SyncService?.FetchClientLogsAsync(clientId, fromUtcMs, toUtcMs, timeout)
        ?? Task.FromResult<WaBiBaBuSy.Grpc.ClientLogData?>(null);
```

- [ ] **Step 8: UI — show a timeout instead of hanging**

In `MainWindowViewModel.FetchClientLogs`, replace the body of the `try` block:
```csharp
            RemoteClientLogs = $"Requesting logs from {selectedRemote.Hostname}...";
            IsClientLogsVisible = true;

            // The reply also arrives through OnClientLogsReceived; this only covers "no answer".
            var logs = await _service.FetchClientLogsAsync(selectedRemote.ClientId, 0, 0, TimeSpan.FromSeconds(10));
            if (logs == null)
                RemoteClientLogs = $"No reply from {selectedRemote.Hostname} within 10 s. Is it connected? Its logs are in %LOCALAPPDATA%\\WaBiBaBuSy\\Logs on that machine.";
```

- [ ] **Step 9: Build and run all tests**

Run: `dotnet build WaBiBaBuSy.sln -c Debug` → 0 errors. Run: `dotnet test WaBiBaBuSy.Tests` → all green.

- [ ] **Step 10: Commit**

```bash
git add WaBiBaBuSy.Common/IO/LogTail.cs WaBiBaBuSy.Tests/LogTailTests.cs WaBiBaBuSy.Grpc WaBiBaBuSy.Core WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs
git commit -m "fix: View logs hang - read log through the logger's file lock, always reply, 10 s timeout" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 2: Wire remote thumbnails

`WallpaperSyncClient.ThumbnailCaptureService` is never assigned, so `SendThumbnailIfDueAsync` returns early on every remote client. Assign it when the client starts a cross-screen player, and clear it on stop.

**Files:**
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (`ApplyCrossScreenD2DFromRemoteAsync` end ≈ `:2560`, `StopRemoteCrossScreenD2DAsync` ≈ `:2573`)

**Interfaces:**
- Consumes: `ThumbnailCaptureService.SetWallpaperHwnd(IntPtr hwnd, string? wallpaperName)`, `ClearWallpaperHwnd()`, `D2DCompositionService.PlayerHwnd`, `WaBiBaBuSyService.Client`.

- [ ] **Step 1: Assign the capture service after a remote player starts**

At the end of `ApplyCrossScreenD2DFromRemoteAsync`, directly after `_remoteD2DServices[monitorIndex] = d2dService;`:
```csharp
        // Remote thumbnails: the client uploads this player's window (the primary one when
        // several monitors play). Without this the upload loop returns early on every remote.
        var syncClient = _service.Client;
        if (syncClient != null && (monitorIndex == 0 || syncClient.ThumbnailCaptureService == null))
        {
            syncClient.ThumbnailCaptureService ??= new ThumbnailCaptureService(AppLogger.CreateLogger<ThumbnailCaptureService>());
            syncClient.ThumbnailCaptureService.SetWallpaperHwnd(d2dService.PlayerHwnd, Path.GetFileName(req.FilePath));
        }
```

- [ ] **Step 2: Clear it on stop**

In `StopRemoteCrossScreenD2DAsync`, after `_remoteD2DServices.Clear();`:
```csharp
        _service.Client?.ThumbnailCaptureService?.ClearWallpaperHwnd();
```

- [ ] **Step 3: Build**

Run: `dotnet build WaBiBaBuSy.sln -c Debug` → 0 errors.

- [ ] **Step 4: Manual check (one server + one client)**

Server: open the control panel. Client: open its window → Connect → server plays any scene on all. Expected: within ~6 s the client's tile shows a live thumbnail. Close the server window → the client log shows no further `Sent thumbnail` lines after one heartbeat.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs
git commit -m "fix: remote clients never uploaded thumbnails (capture service was never assigned)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 3: Scenario model, loader, scene files

**Files:**
- Create: `WaBiBaBuSy.Models/Testing/TestScenario.cs`
- Create: `WaBiBaBuSy.Models/Testing/ScenarioLoader.cs`
- Create: `WaBiBaBuSy.Models/Testing/SceneFile.cs`
- Create: `WaBiBaBuSy.Tests/ScenarioLoaderTests.cs`

**Interfaces:**
- Produces (namespace `WaBiBaBuSy.Models.Testing`):
  - `TestScenario { string Name; ScenarioRequirements Requires; ScenarioThresholds Thresholds; List<TestStep> Steps; [JsonIgnore] string BaseDirectory }`
  - `ScenarioRequirements { int MinRemoteNodes }`
  - `ScenarioThresholds { double DriftSpreadMs = 50, DriftWarnMs = 25, PositionErrorPx = 3, PixelDiffPct = 0.5, MaxCpuPercent = 15, MaxGpuPercent = 10, MaxMemoryMb = 200 }`
  - `abstract TestStep { string? Label; int? TimeoutMs; abstract string Kind }` and subclasses `TestModeStep { bool Timecode = true; Dictionary<string,int> SimulatedClockSkewMs }`, `PlaySceneStep { string Scene; string Targets = "all"; bool Marker }`, `ProbeStep { string At = "now+1000ms"; bool Capture = true }`, `ProbeSeriesStep { int EveryMs = 10000; int ForMs = 60000; bool Capture; bool Perf = true }`, `ExactFrameStep { long ElapsedMs; bool Capture = true }`, `WaitStep { int Ms }`, `StopStep`
  - `ScenarioLoader.Load(string path) → TestScenario`, `Parse(string json, string baseDirectory, string sourceName = "(scenario)") → TestScenario`, `Validate(TestScenario) → IReadOnlyList<string>`, `ResolvePath(TestScenario, string) → string`, `JsonOptions`
  - `ScenarioException : Exception`
  - `enum ProbeAnchor { Start, Now }`; `ProbeAt.TryParse(string? text, out ProbeAnchor anchor, out long offsetMs) → bool`; `ProbeAt.Resolve(ProbeAnchor anchor, long offsetMs, long sharedStartUtcMs, long nowUtcMs, long minLeadMs) → long`
  - `SceneFile.Load(string path) → CrossScreenConfig` (asset paths made absolute, missing assets throw `ScenarioException`)

- [ ] **Step 1: Write the failing tests**

`WaBiBaBuSy.Tests/ScenarioLoaderTests.cs`:
```csharp
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ScenarioLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-scenario-{Guid.NewGuid():N}");

    public ScenarioLoaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "scenes"));
        File.WriteAllBytes(Path.Combine(_dir, "scenes", "marker.png"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(_dir, "scenes", "linear.json"),
            """{ "Animation": { "AnimationPath": "marker.png", "TargetHeight": 64 }, "Movement": { "Type": "Linear", "SpeedPixelsPerSecond": 400 }, "DistributionMode": "Sequential" }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const string Valid = """
    {
      "name": "sync-basic",
      "requires": { "minRemoteNodes": 1 },
      "thresholds": { "driftSpreadMs": 40 },
      "steps": [
        { "type": "testMode", "timecode": true, "simulatedClockSkewMs": { "pc-02": 2000 } },
        { "type": "playScene", "scene": "scenes/linear.json", "targets": "all", "marker": true },
        { "label": "clean-start", "type": "probe", "at": "start+150ms", "capture": true },
        { "type": "probeSeries", "label": "long-run", "everyMs": 1000, "forMs": 5000 },
        { "type": "exactFrame", "label": "px", "elapsedMs": 12345 },
        { "type": "wait", "ms": 10 },
        { "type": "stop" }
      ]
    }
    """;

    [Fact]
    public void Parse_FullExample_ReadsEveryStepType()
    {
        var s = ScenarioLoader.Parse(Valid, _dir);

        Assert.Equal("sync-basic", s.Name);
        Assert.Equal(1, s.Requires.MinRemoteNodes);
        Assert.Equal(40, s.Thresholds.DriftSpreadMs);
        Assert.Equal(25, s.Thresholds.DriftWarnMs);   // default kept
        Assert.Collection(s.Steps,
            st => Assert.Equal(2000, Assert.IsType<TestModeStep>(st).SimulatedClockSkewMs["pc-02"]),
            st => Assert.True(Assert.IsType<PlaySceneStep>(st).Marker),
            st => Assert.Equal("start+150ms", Assert.IsType<ProbeStep>(st).At),   // "type" after "label" still works
            st => Assert.Equal(5000, Assert.IsType<ProbeSeriesStep>(st).ForMs),
            st => Assert.Equal(12345, Assert.IsType<ExactFrameStep>(st).ElapsedMs),
            st => Assert.Equal(10, Assert.IsType<WaitStep>(st).Ms),
            st => Assert.IsType<StopStep>(st));
    }

    [Theory]
    [InlineData("\"type\": \"teleport\"")]
    [InlineData("\"type\": \"playscene\", \"scene\": \"scenes/linear.json\"")]   // discriminators are case-sensitive
    [InlineData("\"scene\": \"scenes/linear.json\"")]                           // no type at all
    public void Parse_InvalidStepType_Throws(string stepBody)
    {
        var json = $$"""{ "name": "x", "steps": [ { {{stepBody}} } ] }""";
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, _dir));
        Assert.Contains("invalid scenario JSON", ex.Message);
    }

    [Fact]
    public void Parse_Invalid_MissingSceneFile_NamesThePath()
    {
        var json = """{ "name": "x", "steps": [ { "type": "playScene", "scene": "scenes/nope.json" } ] }""";
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, _dir));
        Assert.Contains("scene file not found", ex.Message);
        Assert.Contains("nope.json", ex.Message);
    }

    [Theory]
    [InlineData("""{ "type": "probe", "label": "p", "at": "later" }""", "is not start+Nms or now+Nms")]
    [InlineData("""{ "type": "probe", "at": "now+10ms" }""", "label is required")]
    [InlineData("""{ "type": "probeSeries", "label": "s", "everyMs": 1000, "forMs": 500 }""", "forMs must be >= everyMs")]
    [InlineData("""{ "type": "probeSeries", "label": "s", "everyMs": 0, "forMs": 500 }""", "everyMs must be > 0")]
    [InlineData("""{ "type": "exactFrame", "label": "e", "elapsedMs": -1 }""", "elapsedMs must be >= 0")]
    [InlineData("""{ "type": "wait", "ms": 10, "timeoutMs": 0 }""", "timeoutMs must be > 0")]
    public void Parse_Invalid_StepParameters(string step, string expected)
    {
        var json = $$"""{ "name": "x", "steps": [ { "type": "playScene", "scene": "scenes/linear.json" }, {{step}} ] }""";
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, _dir));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Parse_Invalid_ProbeBeforeAnyScene()
    {
        var json = """{ "name": "x", "steps": [ { "type": "probe", "label": "p", "at": "now+10ms" } ] }""";
        Assert.Contains("needs a playScene step before it",
            Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, _dir)).Message);
    }

    [Fact]
    public void Parse_Invalid_EmptyNameAndSteps()
    {
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse("""{ "name": "", "steps": [] }""", _dir));
        Assert.Contains("name is empty", ex.Message);
        Assert.Contains("steps is empty", ex.Message);
    }

    [Theory]
    [InlineData("start+150ms", ProbeAnchor.Start, 150)]
    [InlineData("now+2000ms", ProbeAnchor.Now, 2000)]
    [InlineData("START+5", ProbeAnchor.Start, 5)]
    [InlineData("now", ProbeAnchor.Now, 0)]
    public void ProbeAt_TryParse_Accepts(string text, ProbeAnchor anchor, long offset)
    {
        Assert.True(ProbeAt.TryParse(text, out var a, out var o));
        Assert.Equal(anchor, a);
        Assert.Equal(offset, o);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("start-100ms")]
    [InlineData("soon+1ms")]
    [InlineData("now+abc")]
    public void ProbeAt_TryParse_Rejects(string? text) => Assert.False(ProbeAt.TryParse(text, out _, out _));

    [Fact]
    public void ProbeAt_Resolve_NeverEarlierThanMinLead()
    {
        // start+150 lies in the past (start was 10 s ago) → pushed to now + lead
        Assert.Equal(100_500, ProbeAt.Resolve(ProbeAnchor.Start, 150, sharedStartUtcMs: 90_000, nowUtcMs: 100_000, minLeadMs: 500));
        Assert.Equal(103_000, ProbeAt.Resolve(ProbeAnchor.Now, 3000, sharedStartUtcMs: 0, nowUtcMs: 100_000, minLeadMs: 500));
    }

    [Fact]
    public void SceneFile_Load_MakesAssetPathsAbsolute_AndReadsEnumNames()
    {
        var scene = SceneFile.Load(Path.Combine(_dir, "scenes", "linear.json"));
        Assert.Equal(Path.Combine(_dir, "scenes", "marker.png"), scene.Animation.AnimationPath);
        Assert.Equal(MovementType.Linear, scene.Movement.Type);
        Assert.Equal(AnimationDistributionMode.Sequential, scene.DistributionMode);
    }

    [Fact]
    public void SceneFile_Load_MissingAsset_Throws()
    {
        var path = Path.Combine(_dir, "scenes", "broken.json");
        File.WriteAllText(path, """{ "Animation": { "AnimationPath": "gone.gif" } }""");
        Assert.Contains("asset not found", Assert.Throws<ScenarioException>(() => SceneFile.Load(path)).Message);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~ScenarioLoaderTests`
Expected: build error, namespace `WaBiBaBuSy.Models.Testing` not found.

- [ ] **Step 3: Implement the model**

`WaBiBaBuSy.Models/Testing/TestScenario.cs`:
```csharp
using System.Text.Json.Serialization;

namespace WaBiBaBuSy.Models.Testing;

/// <summary>
/// An automated test run: preflight requirements, pass/fail thresholds and the ordered steps.
/// Stored as JSON in <c>TestScenarios/</c> (automated test mode design §4).
/// </summary>
public sealed class TestScenario
{
    public string Name { get; set; } = string.Empty;
    public ScenarioRequirements Requires { get; set; } = new();
    public ScenarioThresholds Thresholds { get; set; } = new();
    public List<TestStep> Steps { get; set; } = new();

    /// <summary>Folder of the scenario file; relative scene paths resolve against it.</summary>
    [JsonIgnore] public string BaseDirectory { get; set; } = string.Empty;
}

/// <summary>What must be true before the run starts.</summary>
public sealed class ScenarioRequirements
{
    /// <summary>Remote clients that must be connected (the server's own monitors do not count).</summary>
    public int MinRemoteNodes { get; set; }
}

/// <summary>Verdict thresholds. Defaults are the MVP targets.</summary>
public sealed class ScenarioThresholds
{
    /// <summary>Drift spread above this fails.</summary>
    public double DriftSpreadMs { get; set; } = 50;
    /// <summary>Drift spread above this (and up to <see cref="DriftSpreadMs"/>) warns.</summary>
    public double DriftWarnMs { get; set; } = 25;
    public double PositionErrorPx { get; set; } = 3;
    /// <summary>Share of differing pixels (percent) above which an exact-frame comparison fails.</summary>
    public double PixelDiffPct { get; set; } = 0.5;
    public double MaxCpuPercent { get; set; } = 15;
    public double MaxGpuPercent { get; set; } = 10;
    public double MaxMemoryMb { get; set; } = 200;
}

/// <summary>One scenario step; the JSON <c>type</c> property selects the subclass (case-sensitive).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TestModeStep), "testMode")]
[JsonDerivedType(typeof(PlaySceneStep), "playScene")]
[JsonDerivedType(typeof(ProbeStep), "probe")]
[JsonDerivedType(typeof(ProbeSeriesStep), "probeSeries")]
[JsonDerivedType(typeof(ExactFrameStep), "exactFrame")]
[JsonDerivedType(typeof(WaitStep), "wait")]
[JsonDerivedType(typeof(StopStep), "stop")]
public abstract class TestStep
{
    /// <summary>Name in the report; required for probe, probeSeries and exactFrame.</summary>
    public string? Label { get; set; }
    /// <summary>Overrides the default step timeout (30 s; probeSeries: forMs + 30 s).</summary>
    public int? TimeoutMs { get; set; }
    /// <summary>The JSON type name, for messages.</summary>
    [JsonIgnore] public abstract string Kind { get; }
}

/// <summary>Timecode strip on/off on every node; optional simulated clock skew per remote node.</summary>
public sealed class TestModeStep : TestStep
{
    public bool Timecode { get; set; } = true;
    /// <summary>Node name or client id → skew in ms added to that client's clock. Set it before playScene.</summary>
    public Dictionary<string, int> SimulatedClockSkewMs { get; set; } = new();
    public override string Kind => "testMode";
}

/// <summary>Play a scene file on the targets (same path as ▶ Play).</summary>
public sealed class PlaySceneStep : TestStep
{
    /// <summary>CrossScreenConfig JSON, relative to the scenario file.</summary>
    public string Scene { get; set; } = string.Empty;
    /// <summary><c>all</c>, <c>server</c>, <c>remotes</c>, or comma-separated node names / ids.</summary>
    public string Targets { get; set; } = "all";
    /// <summary>The scene shows the marker sprite: captured probes run the position check.</summary>
    public bool Marker { get; set; }
    public override string Kind => "playScene";
}

/// <summary>One probe at a server instant.</summary>
public sealed class ProbeStep : TestStep
{
    /// <summary><c>start+Nms</c> (after the shared start) or <c>now+Nms</c>.</summary>
    public string At { get; set; } = "now+1000ms";
    public bool Capture { get; set; } = true;
    public override string Kind => "probe";
}

/// <summary>Repeated probes; the drift-over-time source.</summary>
public sealed class ProbeSeriesStep : TestStep
{
    public int EveryMs { get; set; } = 10000;
    public int ForMs { get; set; } = 60000;
    public bool Capture { get; set; }
    public bool Perf { get; set; } = true;
    public override string Kind => "probeSeries";
}

/// <summary>Every node renders one offscreen frame at exactly <see cref="ElapsedMs"/>.</summary>
public sealed class ExactFrameStep : TestStep
{
    public long ElapsedMs { get; set; }
    public bool Capture { get; set; } = true;
    public override string Kind => "exactFrame";
}

/// <summary>Plain delay.</summary>
public sealed class WaitStep : TestStep
{
    public int Ms { get; set; }
    public override string Kind => "wait";
}

/// <summary>Clear the wallpaper on every node.</summary>
public sealed class StopStep : TestStep
{
    public override string Kind => "stop";
}
```

`WaBiBaBuSy.Models/Testing/ScenarioLoader.cs`:
```csharp
using System.Globalization;
using System.Text.Json;

namespace WaBiBaBuSy.Models.Testing;

/// <summary>Parses and validates scenario JSON. Nothing is played when this throws.</summary>
public static class ScenarioLoader
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowOutOfOrderMetadataProperties = true,   // "type" does not have to be the first property
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>Load and validate a scenario file.</summary>
    public static TestScenario Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new ScenarioException($"Scenario file not found: {full}");
        return Parse(File.ReadAllText(full), Path.GetDirectoryName(full)!, full);
    }

    /// <summary>Parse and validate scenario JSON; relative paths resolve against <paramref name="baseDirectory"/>.</summary>
    public static TestScenario Parse(string json, string baseDirectory, string sourceName = "(scenario)")
    {
        TestScenario? scenario;
        try
        {
            scenario = JsonSerializer.Deserialize<TestScenario>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ScenarioException($"{sourceName}: invalid scenario JSON — {ex.Message}");
        }
        if (scenario == null) throw new ScenarioException($"{sourceName}: empty scenario");

        scenario.BaseDirectory = baseDirectory;
        var errors = Validate(scenario);
        if (errors.Count > 0)
            throw new ScenarioException($"{sourceName}:{Environment.NewLine}" + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));
        return scenario;
    }

    /// <summary>Every problem in the scenario, empty when it can run.</summary>
    public static IReadOnlyList<string> Validate(TestScenario scenario)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(scenario.Name)) errors.Add("name is empty");
        if (scenario.Steps.Count == 0) errors.Add("steps is empty");
        if (scenario.Requires.MinRemoteNodes < 0) errors.Add("requires.minRemoteNodes must be >= 0");

        bool scenePlaying = false;
        for (int i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];
            string at = $"step {i + 1} ({step.Kind})";
            if (step.TimeoutMs is <= 0) errors.Add($"{at}: timeoutMs must be > 0");

            switch (step)
            {
                case PlaySceneStep play:
                    if (string.IsNullOrWhiteSpace(play.Scene)) errors.Add($"{at}: scene is empty");
                    else if (!File.Exists(ResolvePath(scenario, play.Scene)))
                        errors.Add($"{at}: scene file not found: {ResolvePath(scenario, play.Scene)}");
                    scenePlaying = true;
                    break;
                case ProbeStep probe:
                    RequireLabel(probe, at, errors);
                    if (!ProbeAt.TryParse(probe.At, out _, out _)) errors.Add($"{at}: at \"{probe.At}\" is not start+Nms or now+Nms");
                    RequireScene(scenePlaying, at, errors);
                    break;
                case ProbeSeriesStep series:
                    RequireLabel(series, at, errors);
                    if (series.EveryMs <= 0) errors.Add($"{at}: everyMs must be > 0");
                    else if (series.ForMs < series.EveryMs) errors.Add($"{at}: forMs must be >= everyMs");
                    RequireScene(scenePlaying, at, errors);
                    break;
                case ExactFrameStep exact:
                    RequireLabel(exact, at, errors);
                    if (exact.ElapsedMs < 0) errors.Add($"{at}: elapsedMs must be >= 0");
                    RequireScene(scenePlaying, at, errors);
                    break;
                case WaitStep wait:
                    if (wait.Ms < 0) errors.Add($"{at}: ms must be >= 0");
                    break;
                case StopStep:
                    scenePlaying = false;
                    break;
            }
        }
        return errors;
    }

    /// <summary>Absolute path of a scenario-relative (or already absolute) path.</summary>
    public static string ResolvePath(TestScenario scenario, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(scenario.BaseDirectory, path));

    private static void RequireLabel(TestStep step, string at, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(step.Label)) errors.Add($"{at}: label is required");
    }

    private static void RequireScene(bool scenePlaying, string at, List<string> errors)
    {
        if (!scenePlaying) errors.Add($"{at}: needs a playScene step before it");
    }
}

/// <summary>A scenario or scene file that cannot run; the message names the file and every problem.</summary>
public sealed class ScenarioException : Exception
{
    public ScenarioException(string message) : base(message) { }
}

/// <summary>What a probe's <c>at</c> is relative to.</summary>
public enum ProbeAnchor { Start, Now }

/// <summary><c>start+150ms</c> / <c>now+2000ms</c> parsing and resolution to a server instant.</summary>
public static class ProbeAt
{
    /// <summary>Parse <c>start|now</c> optionally followed by <c>+N</c> or <c>+Nms</c> (case-insensitive).</summary>
    public static bool TryParse(string? text, out ProbeAnchor anchor, out long offsetMs)
    {
        anchor = ProbeAnchor.Now;
        offsetMs = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.Trim().ToLowerInvariant();
        string rest;
        if (t.StartsWith("start", StringComparison.Ordinal)) { anchor = ProbeAnchor.Start; rest = t[5..]; }
        else if (t.StartsWith("now", StringComparison.Ordinal)) { anchor = ProbeAnchor.Now; rest = t[3..]; }
        else return false;

        if (rest.Length == 0) return true;
        if (rest[0] != '+') return false;
        rest = rest[1..];
        if (rest.EndsWith("ms", StringComparison.Ordinal)) rest = rest[..^2];
        return long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out offsetMs);
    }

    /// <summary>Server UTC ms of the probe, never earlier than <paramref name="nowUtcMs"/> + <paramref name="minLeadMs"/>.</summary>
    public static long Resolve(ProbeAnchor anchor, long offsetMs, long sharedStartUtcMs, long nowUtcMs, long minLeadMs)
    {
        long target = (anchor == ProbeAnchor.Start ? sharedStartUtcMs : nowUtcMs) + offsetMs;
        return Math.Max(target, nowUtcMs + minLeadMs);
    }
}
```

`WaBiBaBuSy.Models/Testing/SceneFile.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Testing;

/// <summary>Loads a <see cref="CrossScreenConfig"/> JSON used by a scenario.</summary>
public static class SceneFile
{
    /// <summary>Same shape as the configs sent to clients (PascalCase), plus enum names and comments.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Load a scene; the animation, additional image and background image paths become absolute
    /// against the scene file's folder. Throws <see cref="ScenarioException"/> for bad JSON or a missing asset.
    /// </summary>
    public static CrossScreenConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        CrossScreenConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<CrossScreenConfig>(File.ReadAllText(full), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ScenarioException($"{full}: invalid scene JSON — {ex.Message}");
        }
        if (config == null) throw new ScenarioException($"{full}: empty scene");

        var dir = Path.GetDirectoryName(full)!;
        config.Animation.AnimationPath = Absolute(dir, config.Animation.AnimationPath);
        config.Animation.AdditionalAnimationPaths = config.Animation.AdditionalAnimationPaths.Select(p => Absolute(dir, p)).ToList();
        if (!string.IsNullOrEmpty(config.Background.ImagePath))
            config.Background.ImagePath = Absolute(dir, config.Background.ImagePath);

        foreach (var (asset, _) in SceneAssets.CollectPaths(config))
            if (!string.IsNullOrWhiteSpace(asset) && !File.Exists(asset))
                throw new ScenarioException($"{full}: asset not found: {asset}");
        return config;
    }

    private static string Absolute(string dir, string path) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(dir, path));
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~ScenarioLoaderTests`
Expected: all pass. If `Parse_InvalidStepType_Throws("\"scene\": …")` fails because System.Text.Json throws a different exception type for a missing discriminator on an abstract type, add that type to the `catch (… when …)` filter. The contract is "always `ScenarioException`".

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Testing WaBiBaBuSy.Tests/ScenarioLoaderTests.cs
git commit -m "feat(test-mode): scenario model, loader + validation, scene files" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 4: Probe DTOs, frame-interval stats, drift analysis

**Files:**
- Create: `WaBiBaBuSy.Models/Testing/ProbeModels.cs`
- Create: `WaBiBaBuSy.Models/Testing/FrameIntervalTracker.cs`
- Create: `WaBiBaBuSy.Models/Testing/DriftAnalyzer.cs`
- Create: `WaBiBaBuSy.Tests/FrameIntervalTrackerTests.cs`, `WaBiBaBuSy.Tests/DriftAnalyzerTests.cs`

**Interfaces:**
- Produces (all `WaBiBaBuSy.Models.Testing`, mutable POCOs with `{ get; set; }` so both System.Text.Json and Newtonsoft round-trip them):
  - `enum Verdict { Pass, Warn, Fail, Skipped }`; `Verdicts.Worst(Verdict a, Verdict b) → Verdict` (Fail > Warn > Pass > Skipped)
  - `ProbeRequest { string ProbeId; long AtServerUtcMs; bool Capture; long? ExactElapsedMs }`
  - `PlayerProbeRequest { string ProbeId; long AtLocalUtcMs; bool Capture; long? ExactElapsedMs; string CaptureDirectory }`
  - `PlayerProbeReply { string ProbeId; int MonitorIndex; string? Error; bool Exact; long RenderedElapsedMs; int PhaseMs; long RenderLocalUtcMs; long? PresentLocalUtcMs; bool PresentEstimated; long FrameIndex; FrameIntervalStats? Frames; float AnimX, AnimY, AnimWidth, AnimHeight; bool Flipped; int Width, Height; string? CapturePath }`
  - `FrameIntervalStats { double MeanFps, P50Ms, P99Ms, MaxMs; int Dropped, Count }`
  - `PerfSample { double? AppCpuPercent; double AppMemoryMb; double? PlayerCpuPercent; double PlayerMemoryMb; int PlayerProcesses; double? GpuPercent }`
  - `RemoteProbeResult { string ClientId; string ProbeId; double ClockOffsetMs; double RttMs; PerfSample? Perf; string? Error; List<PlayerProbeReply> Replies; [JsonIgnore] Dictionary<int, byte[]> Captures }`
  - `NodeProbeSample` (fields in code below) with `static FromReply(PlayerProbeReply, string nodeId, string nodeName, bool isLocal, double clockOffsetMs, double rttMs)` and `static MissingFor(string nodeId, string nodeName, int monitorIndex, bool isLocal, string reason)`
  - `FrameIntervalTracker(int capacity = 120, long? ticksPerSecond = null)`, `.Record(long timestamp)`, `.Snapshot(int refreshHz) → FrameIntervalStats?`, `.Reset()`
  - `NodeTimingError { string NodeId, NodeName; int MonitorIndex; double ErrorMs; double ClockBoundMs; bool UsedRenderTime }`
  - `DriftResult { List<NodeTimingError> Errors; double SpreadMs; double MeanErrorMs; Verdict Verdict; List<string> MissingNodes; string Message }`
  - `DriftAnalyzer.ErrorMs(NodeProbeSample s, long sharedStartServerUtcMs) → double`; `DriftAnalyzer.Analyze(IReadOnlyList<NodeProbeSample> samples, long sharedStartServerUtcMs, ScenarioThresholds thresholds) → DriftResult`

**Metric (exact definition; the spec's §5.1 sign for the phase term is corrected here):** the player reports `RenderedElapsedMs` = effective elapsed = raw − phase, and `PhaseMs` = raw − effective. The frame reached the screen at `presentServer = (PresentLocalUtcMs ?? RenderLocalUtcMs) + ClockOffsetMs`. Then `error = RenderedElapsedMs + PhaseMs − (presentServer − sharedStartServerUtcMs)`: content time minus wall time. It is negative by the render→present latency on a healthy node. Task 16 updates the spec line.

- [ ] **Step 1: Write the failing tests**

`WaBiBaBuSy.Tests/FrameIntervalTrackerTests.cs`:
```csharp
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class FrameIntervalTrackerTests
{
    // ticksPerSecond = 1000 → timestamps are plain milliseconds
    private static FrameIntervalTracker Tracker(int capacity = 120) => new(capacity, ticksPerSecond: 1000);

    [Fact]
    public void Empty_OrSingleTimestamp_HasNoStats()
    {
        var t = Tracker();
        Assert.Null(t.Snapshot(60));
        t.Record(1000);
        Assert.Null(t.Snapshot(60));
    }

    [Fact]
    public void Steady60Hz_NoDrops()
    {
        var t = Tracker();
        for (int i = 0; i <= 60; i++) t.Record(1000 + (long)Math.Round(i * 1000.0 / 60));
        var s = t.Snapshot(60)!;
        Assert.Equal(60, s.Count);
        Assert.InRange(s.MeanFps, 59.5, 60.5);
        Assert.Equal(0, s.Dropped);
        Assert.InRange(s.MaxMs, 16, 18);
    }

    [Fact]
    public void Hitch_CountsAsDroppedAndShowsInMaxAndP99()
    {
        var t = Tracker();
        long ts = 0;
        t.Record(ts);
        for (int i = 0; i < 99; i++) t.Record(ts += 16);
        t.Record(ts += 50);
        var s = t.Snapshot(60)!;
        Assert.Equal(1, s.Dropped);          // 50 ms > 1.5 × 16.7 ms
        Assert.Equal(50, s.MaxMs);
        Assert.Equal(16, s.P50Ms);
        Assert.Equal(50, s.P99Ms);           // nearest rank: ceil(0.99 × 100) = 99th of 100 sorted values
    }

    [Fact]
    public void Capacity_KeepsOnlyTheNewestIntervals()
    {
        var t = Tracker(capacity: 3);
        long ts = 0;
        t.Record(ts);
        t.Record(ts += 100);
        for (int i = 0; i < 3; i++) t.Record(ts += 10);
        var s = t.Snapshot(0)!;
        Assert.Equal(3, s.Count);
        Assert.Equal(10, s.MaxMs);
        Assert.Equal(0, s.Dropped);          // refreshHz 0 = unknown → no drop counting
    }
}
```

`WaBiBaBuSy.Tests/DriftAnalyzerTests.cs`:
```csharp
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class DriftAnalyzerTests
{
    private const long Start = 1_000_000;
    private static readonly ScenarioThresholds T = new();   // warn 25, fail 50

    /// <summary>A node whose clock is <paramref name="clientBehindMs"/> behind the server, showing content
    /// <paramref name="contentLagMs"/> behind wall time, with 16 ms render→present latency.</summary>
    private static NodeProbeSample Node(string name, double clientBehindMs = 0, long wallElapsed = 5000, long contentLagMs = 0, int phaseMs = 0)
    {
        long renderServer = Start + wallElapsed;
        long renderLocal = renderServer - (long)clientBehindMs;
        return new NodeProbeSample
        {
            NodeId = name, NodeName = name,
            RenderedElapsedMs = wallElapsed - contentLagMs - phaseMs,
            PhaseMs = phaseMs,
            RenderLocalUtcMs = renderLocal,
            PresentLocalUtcMs = renderLocal + 16,
            ClockOffsetMs = clientBehindMs,          // offset = server − client
            RttMs = 2,
        };
    }

    [Fact]
    public void ErrorMs_HealthyNode_IsMinusPresentLatency() =>
        Assert.Equal(-16, DriftAnalyzer.ErrorMs(Node("a"), Start));

    [Fact]
    public void ErrorMs_ClockSkewIsCancelledByTheOffset() =>
        Assert.Equal(-16, DriftAnalyzer.ErrorMs(Node("a", clientBehindMs: 2000), Start));

    [Fact]
    public void ErrorMs_WavePhaseIsAddedBack() =>
        Assert.Equal(-16, DriftAnalyzer.ErrorMs(Node("a", phaseMs: 750), Start));

    [Fact]
    public void ErrorMs_FallsBackToRenderTime_WhenNoPresentTime()
    {
        var s = Node("a");
        s.PresentLocalUtcMs = null;
        Assert.Equal(0, DriftAnalyzer.ErrorMs(s, Start));
        Assert.True(DriftAnalyzer.Analyze(new[] { s }, Start, T).Errors[0].UsedRenderTime);
    }

    [Fact]
    public void Analyze_IdenticalNodes_Pass()
    {
        var r = DriftAnalyzer.Analyze(new[] { Node("a"), Node("b", clientBehindMs: 1234) }, Start, T);
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.Equal(0, r.SpreadMs, 3);
        Assert.Equal(-16, r.MeanErrorMs, 3);
        Assert.Equal(1, r.Errors[1].ClockBoundMs);   // RTT/2
    }

    [Theory]
    [InlineData(25, Verdict.Pass)]
    [InlineData(30, Verdict.Warn)]
    [InlineData(50, Verdict.Warn)]
    [InlineData(80, Verdict.Fail)]
    public void Analyze_SpreadThresholds(long lag, Verdict expected)
    {
        var r = DriftAnalyzer.Analyze(new[] { Node("a"), Node("b", contentLagMs: lag) }, Start, T);
        Assert.Equal(lag, r.SpreadMs, 3);
        Assert.Equal(expected, r.Verdict);
    }

    [Fact]
    public void MissingNode_ExcludedFromSpread_AtLeastWarn()
    {
        var gone = NodeProbeSample.MissingFor("c", "pc-03", 0, isLocal: false, reason: "no reply within 2 s");
        var failed = Node("d");
        failed.Error = "readback failed";

        var r = DriftAnalyzer.Analyze(new[] { Node("a"), Node("b"), gone, failed }, Start, T);

        Assert.Equal(Verdict.Warn, r.Verdict);
        Assert.Equal(2, r.Errors.Count);
        Assert.Equal(new[] { "pc-03", "d" }, r.MissingNodes);
        Assert.Contains("missing", r.Message);
    }

    [Fact]
    public void MissingNode_AllMissing_Skipped()
    {
        var r = DriftAnalyzer.Analyze(new[] { NodeProbeSample.MissingFor("c", "pc-03", 0, false, "timeout") }, Start, T);
        Assert.Equal(Verdict.Skipped, r.Verdict);
    }

    [Fact]
    public void NegativeElapsed_StillComparable()
    {
        // Probe 500 ms before the shared start: both nodes show the background only; timing still agrees.
        var r = DriftAnalyzer.Analyze(new[] { Node("a", wallElapsed: -500), Node("b", wallElapsed: -500) }, Start, T);
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.Equal(-16, r.Errors[0].ErrorMs, 3);
    }

    [Theory]
    [InlineData(Verdict.Pass, Verdict.Fail, Verdict.Fail)]
    [InlineData(Verdict.Warn, Verdict.Pass, Verdict.Warn)]
    [InlineData(Verdict.Skipped, Verdict.Pass, Verdict.Pass)]
    [InlineData(Verdict.Skipped, Verdict.Skipped, Verdict.Skipped)]
    public void Verdicts_Worst(Verdict a, Verdict b, Verdict expected) => Assert.Equal(expected, Verdicts.Worst(a, b));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~FrameIntervalTrackerTests|FullyQualifiedName~DriftAnalyzerTests"`
Expected: build errors (types missing).

- [ ] **Step 3: Implement**

`WaBiBaBuSy.Models/Testing/ProbeModels.cs`:
```csharp
using System.Text.Json.Serialization;

namespace WaBiBaBuSy.Models.Testing;

/// <summary>Outcome of a check, a probe, a step or a run.</summary>
public enum Verdict { Pass, Warn, Fail, Skipped }

public static class Verdicts
{
    /// <summary>The more severe of two verdicts: Fail &gt; Warn &gt; Pass &gt; Skipped.</summary>
    public static Verdict Worst(Verdict a, Verdict b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(Verdict v) => v switch
    {
        Verdict.Fail => 3,
        Verdict.Warn => 2,
        Verdict.Pass => 1,
        _ => 0,
    };
}

/// <summary>Runner → node: probe at a server instant.</summary>
public sealed class ProbeRequest
{
    public string ProbeId { get; set; } = string.Empty;
    public long AtServerUtcMs { get; set; }
    public bool Capture { get; set; }
    /// <summary>Set for an exact-frame probe: render offscreen at exactly this effective elapsed.</summary>
    public long? ExactElapsedMs { get; set; }
}

/// <summary>Node → its player: the probe in the node's own (possibly skewed) clock.</summary>
public sealed class PlayerProbeRequest
{
    public string ProbeId { get; set; } = string.Empty;
    public long AtLocalUtcMs { get; set; }
    public bool Capture { get; set; }
    public long? ExactElapsedMs { get; set; }
    /// <summary>Folder the player writes the PNG into.</summary>
    public string CaptureDirectory { get; set; } = string.Empty;
}

/// <summary>Player → host (stderr <c>SIGNAL:PROBE:{json}</c>). Times are the player's local clock.</summary>
public sealed class PlayerProbeReply
{
    public string ProbeId { get; set; } = string.Empty;
    /// <summary>Filled in by the host / fan-out, not by the player.</summary>
    public int MonitorIndex { get; set; }
    public string? Error { get; set; }
    public bool Exact { get; set; }
    /// <summary>Effective elapsed the frame was rendered with (raw − phase).</summary>
    public long RenderedElapsedMs { get; set; }
    /// <summary>raw − effective: the Wave-mode phase this node applies.</summary>
    public int PhaseMs { get; set; }
    public long RenderLocalUtcMs { get; set; }
    /// <summary>When the frame reached the screen (DXGI frame statistics); null when unavailable.</summary>
    public long? PresentLocalUtcMs { get; set; }
    /// <summary>True when the present time was extrapolated from a later frame's statistics.</summary>
    public bool PresentEstimated { get; set; }
    public long FrameIndex { get; set; }
    public FrameIntervalStats? Frames { get; set; }
    /// <summary>The player's own sprite rectangle (canvas units, node-local, top-left).</summary>
    public float AnimX { get; set; }
    public float AnimY { get; set; }
    public float AnimWidth { get; set; }
    public float AnimHeight { get; set; }
    /// <summary>The sprite was drawn mirrored (face travel direction).</summary>
    public bool Flipped { get; set; }
    /// <summary>Player surface size, device px.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>PNG written by the player on its machine.</summary>
    public string? CapturePath { get; set; }
}

/// <summary>Frame pacing over the last N frames.</summary>
public sealed class FrameIntervalStats
{
    public double MeanFps { get; set; }
    public double P50Ms { get; set; }
    public double P99Ms { get; set; }
    public double MaxMs { get; set; }
    /// <summary>Intervals longer than 1.5 refresh periods.</summary>
    public int Dropped { get; set; }
    public int Count { get; set; }
}

/// <summary>Process load on one machine at one probe.</summary>
public sealed class PerfSample
{
    public double? AppCpuPercent { get; set; }
    public double AppMemoryMb { get; set; }
    public double? PlayerCpuPercent { get; set; }
    public double PlayerMemoryMb { get; set; }
    public int PlayerProcesses { get; set; }
    /// <summary>3D engine utilisation of the player processes; null when the counters are unavailable.</summary>
    public double? GpuPercent { get; set; }
}

/// <summary>Client → server: everything one client measured for one probe.</summary>
public sealed class RemoteProbeResult
{
    public string ClientId { get; set; } = string.Empty;
    public string ProbeId { get; set; } = string.Empty;
    /// <summary>server − client, ms, at the time of the probe.</summary>
    public double ClockOffsetMs { get; set; }
    public double RttMs { get; set; }
    public PerfSample? Perf { get; set; }
    public string? Error { get; set; }
    public List<PlayerProbeReply> Replies { get; set; } = new();
    /// <summary>PNG bytes per monitor index; travels as separate gRPC chunks.</summary>
    [JsonIgnore] public Dictionary<int, byte[]> Captures { get; set; } = new();
}

/// <summary>One node-monitor's measurement as the report sees it.</summary>
public sealed class NodeProbeSample
{
    public string NodeId { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public int MonitorIndex { get; set; }
    public bool IsLocal { get; set; }
    public bool Missing { get; set; }
    public string? Error { get; set; }
    public bool Exact { get; set; }
    public long RenderedElapsedMs { get; set; }
    public int PhaseMs { get; set; }
    public long RenderLocalUtcMs { get; set; }
    public long? PresentLocalUtcMs { get; set; }
    public bool PresentEstimated { get; set; }
    public long FrameIndex { get; set; }
    public double ClockOffsetMs { get; set; }
    public double RttMs { get; set; }
    public FrameIntervalStats? Frames { get; set; }
    public PerfSample? Perf { get; set; }
    public float PlayerAnimX { get; set; }
    public float PlayerAnimY { get; set; }
    public float PlayerAnimWidth { get; set; }
    public float PlayerAnimHeight { get; set; }
    public bool PlayerFlipped { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Capture PNG, relative to the results folder.</summary>
    public string? CapturePath { get; set; }

    public static NodeProbeSample FromReply(PlayerProbeReply r, string nodeId, string nodeName, bool isLocal, double clockOffsetMs, double rttMs) => new()
    {
        NodeId = nodeId,
        NodeName = nodeName,
        MonitorIndex = r.MonitorIndex,
        IsLocal = isLocal,
        Error = r.Error,
        Exact = r.Exact,
        RenderedElapsedMs = r.RenderedElapsedMs,
        PhaseMs = r.PhaseMs,
        RenderLocalUtcMs = r.RenderLocalUtcMs,
        PresentLocalUtcMs = r.PresentLocalUtcMs,
        PresentEstimated = r.PresentEstimated,
        FrameIndex = r.FrameIndex,
        ClockOffsetMs = clockOffsetMs,
        RttMs = rttMs,
        Frames = r.Frames,
        PlayerAnimX = r.AnimX,
        PlayerAnimY = r.AnimY,
        PlayerAnimWidth = r.AnimWidth,
        PlayerAnimHeight = r.AnimHeight,
        PlayerFlipped = r.Flipped,
        Width = r.Width,
        Height = r.Height,
    };

    public static NodeProbeSample MissingFor(string nodeId, string nodeName, int monitorIndex, bool isLocal, string reason) => new()
    {
        NodeId = nodeId,
        NodeName = nodeName,
        MonitorIndex = monitorIndex,
        IsLocal = isLocal,
        Missing = true,
        Error = reason,
    };
}
```

`WaBiBaBuSy.Models/Testing/FrameIntervalTracker.cs`:
```csharp
using System.Diagnostics;

namespace WaBiBaBuSy.Models.Testing;

/// <summary>
/// Ring buffer of the last N frame-to-frame intervals (player render loop, test mode). Not thread-safe:
/// record and snapshot from the render thread.
/// </summary>
public sealed class FrameIntervalTracker
{
    private readonly double[] _intervalsMs;
    private readonly double _ticksPerMs;
    private int _count;
    private int _next;
    private long _lastTimestamp;
    private bool _hasLast;

    /// <param name="capacity">Intervals kept.</param>
    /// <param name="ticksPerSecond">Timestamp resolution; default <see cref="Stopwatch.Frequency"/>.</param>
    public FrameIntervalTracker(int capacity = 120, long? ticksPerSecond = null)
    {
        _intervalsMs = new double[Math.Max(1, capacity)];
        _ticksPerMs = (ticksPerSecond ?? Stopwatch.Frequency) / 1000.0;
    }

    /// <summary>Record a frame at <paramref name="timestamp"/> (e.g. <see cref="Stopwatch.GetTimestamp"/> right after Present).</summary>
    public void Record(long timestamp)
    {
        if (_hasLast)
        {
            _intervalsMs[_next] = (timestamp - _lastTimestamp) / _ticksPerMs;
            _next = (_next + 1) % _intervalsMs.Length;
            if (_count < _intervalsMs.Length) _count++;
        }
        _lastTimestamp = timestamp;
        _hasLast = true;
    }

    /// <summary>Stats over the kept intervals; null before the second frame. <paramref name="refreshHz"/> 0 = no drop counting.</summary>
    public FrameIntervalStats? Snapshot(int refreshHz)
    {
        if (_count == 0) return null;
        var sorted = new double[_count];
        Array.Copy(_intervalsMs, sorted, _count);   // order does not matter for the stats
        Array.Sort(sorted);

        double mean = sorted.Average();
        double dropLimit = refreshHz > 0 ? 1.5 * 1000.0 / refreshHz : double.MaxValue;
        return new FrameIntervalStats
        {
            Count = _count,
            MeanFps = mean > 0 ? 1000.0 / mean : 0,
            P50Ms = NearestRank(sorted, 0.50),
            P99Ms = NearestRank(sorted, 0.99),
            MaxMs = sorted[^1],
            Dropped = sorted.Count(ms => ms > dropLimit),
        };
    }

    public void Reset()
    {
        _count = 0;
        _next = 0;
        _hasLast = false;
    }

    private static double NearestRank(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
}
```

`WaBiBaBuSy.Models/Testing/DriftAnalyzer.cs`:
```csharp
namespace WaBiBaBuSy.Models.Testing;

/// <summary>One node-monitor's timing error for one probe.</summary>
public sealed class NodeTimingError
{
    public string NodeId { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public int MonitorIndex { get; set; }
    /// <summary>Content time − wall time at present, ms (negative = behind).</summary>
    public double ErrorMs { get; set; }
    /// <summary>RTT/2: how wrong the clock-offset estimate itself may be. The probe cannot see that part.</summary>
    public double ClockBoundMs { get; set; }
    /// <summary>No present time was available; render time was used.</summary>
    public bool UsedRenderTime { get; set; }
}

/// <summary>Timing verdict for one probe across all nodes.</summary>
public sealed class DriftResult
{
    public List<NodeTimingError> Errors { get; set; } = new();
    /// <summary>max(error) − min(error): the drift people can see between screens.</summary>
    public double SpreadMs { get; set; }
    /// <summary>Common latency of the whole wall behind wall time (same on all nodes → invisible).</summary>
    public double MeanErrorMs { get; set; }
    public Verdict Verdict { get; set; }
    public List<string> MissingNodes { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

/// <summary>Pure drift math over probe samples (design §5.1).</summary>
public static class DriftAnalyzer
{
    /// <summary>
    /// <c>RenderedElapsedMs + PhaseMs − (presentServer − sharedStart)</c>, where
    /// <c>presentServer = (PresentLocalUtcMs ?? RenderLocalUtcMs) + ClockOffsetMs</c>.
    /// </summary>
    public static double ErrorMs(NodeProbeSample sample, long sharedStartServerUtcMs)
    {
        long presentLocal = sample.PresentLocalUtcMs ?? sample.RenderLocalUtcMs;
        double presentServer = presentLocal + sample.ClockOffsetMs;
        return sample.RenderedElapsedMs + sample.PhaseMs - (presentServer - sharedStartServerUtcMs);
    }

    /// <summary>Spread and verdict. Missing / failed samples are excluded and force at least Warn.</summary>
    public static DriftResult Analyze(IReadOnlyList<NodeProbeSample> samples, long sharedStartServerUtcMs, ScenarioThresholds thresholds)
    {
        var missing = samples.Where(s => s.Missing || s.Error != null).Select(DisplayName).ToList();
        var valid = samples.Where(s => !s.Missing && s.Error == null).ToList();
        if (valid.Count == 0)
            return new DriftResult { Verdict = Verdict.Skipped, MissingNodes = missing, Message = "no node answered" };

        var errors = valid.Select(s => new NodeTimingError
        {
            NodeId = s.NodeId,
            NodeName = s.NodeName,
            MonitorIndex = s.MonitorIndex,
            ErrorMs = ErrorMs(s, sharedStartServerUtcMs),
            ClockBoundMs = Math.Max(0, s.RttMs) / 2.0,
            UsedRenderTime = s.PresentLocalUtcMs == null,
        }).ToList();

        double spread = errors.Max(e => e.ErrorMs) - errors.Min(e => e.ErrorMs);
        double mean = errors.Average(e => e.ErrorMs);
        var verdict = spread <= thresholds.DriftWarnMs ? Verdict.Pass
            : spread <= thresholds.DriftSpreadMs ? Verdict.Warn
            : Verdict.Fail;
        if (missing.Count > 0) verdict = Verdicts.Worst(verdict, Verdict.Warn);

        var message = $"spread {spread:F1} ms (warn > {thresholds.DriftWarnMs}, fail > {thresholds.DriftSpreadMs}), common latency {mean:F1} ms";
        if (missing.Count > 0) message += $", missing: {string.Join(", ", missing)}";

        return new DriftResult { Errors = errors, SpreadMs = spread, MeanErrorMs = mean, Verdict = verdict, MissingNodes = missing, Message = message };
    }

    private static string DisplayName(NodeProbeSample s) => s.MonitorIndex > 0 ? $"{s.NodeName}#{s.MonitorIndex}" : s.NodeName;
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~FrameIntervalTrackerTests|FullyQualifiedName~DriftAnalyzerTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Testing WaBiBaBuSy.Tests/FrameIntervalTrackerTests.cs WaBiBaBuSy.Tests/DriftAnalyzerTests.cs
git commit -m "feat(test-mode): probe DTOs, frame-interval stats, drift analyzer" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 5: Expected marker position

**Files:**
- Create: `WaBiBaBuSy.Models/Testing/ExpectedPosition.cs`
- Create: `WaBiBaBuSy.Tests/ExpectedPositionTests.cs`

**Interfaces:**
- Consumes: `MovementCalculator.Calculate(MovementConfig, long, int, int, int, int, float tileAlignStepX, bool canvasWraps)`, `NodeMapping.ToLocalX/ToLocalY/WrapCopies`, `SyncTiming.ShouldDrawAnimation`, `NodeLayout` (`WaBiBaBuSy.Models.Topology`)
- Produces: `enum MarkerVisibility { Visible, Partial, OffScreen, NotStarted }`; `MarkerExpectation { MarkerVisibility Visibility; float X, Y, Width, Height; float CenterX; float CenterY }` (device px, top-left, node surface); `ExpectedPosition.Compute(MovementConfig movement, long effectiveElapsedMs, float spriteWidth, float spriteHeight, NodeLayout layout) → MarkerExpectation`; `ExpectedPosition.PerMonitorLayout(string nodeId, int width, int height) → NodeLayout`

The player computes `(vx, vy)` with `MovementCalculator` in canvas units and maps them with `NodeMapping.ToLocalX` and `vy − OffsetY`. It draws under `layout.Scale`. `Compute` repeats exactly that and scales to device px. The sprite size comes from the player's own reply (`PlayerAnimWidth/Height`), so FitMode / TargetHeight need no second implementation.

- [ ] **Step 1: Write the failing tests**

`WaBiBaBuSy.Tests/ExpectedPositionTests.cs`:
```csharp
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ExpectedPositionTests
{
    private static readonly MovementConfig Static = new() { Type = MovementType.Static };

    private static NodeLayout Slice(int offsetX, int canvasWidth, int width = 1920, int height = 1080, bool mirrored = false, float scale = 1f) => new()
    {
        Id = $"n{offsetX}", OffsetX = offsetX, OffsetY = 0, Width = width, Height = height,
        CanvasWidth = canvasWidth, CanvasHeight = height, Mirrored = mirrored, Scale = scale,
    };

    [Fact]
    public void SingleNode_StaticMarker_IsCentered()
    {
        var e = ExpectedPosition.Compute(Static, 1000, 64, 64, ExpectedPosition.PerMonitorLayout("a", 1920, 1080));
        Assert.Equal(MarkerVisibility.Visible, e.Visibility);
        Assert.Equal(928, e.X);
        Assert.Equal(508, e.Y);
        Assert.Equal(960, e.CenterX);
    }

    [Fact]
    public void NegativeElapsed_IsNotOnScreen()
    {
        var e = ExpectedPosition.Compute(Static, -1, 64, 64, ExpectedPosition.PerMonitorLayout("a", 1920, 1080));
        Assert.Equal(MarkerVisibility.NotStarted, e.Visibility);
    }

    [Fact]
    public void StraddlingEdge_IsPartial()
    {
        // Two 1920 nodes, static sprite centered on the 3840 canvas → sits on the bezel.
        Assert.Equal(MarkerVisibility.Partial, ExpectedPosition.Compute(Static, 1000, 64, 64, Slice(0, 3840)).Visibility);
        Assert.Equal(MarkerVisibility.Partial, ExpectedPosition.Compute(Static, 1000, 64, 64, Slice(1920, 3840)).Visibility);
    }

    [Fact]
    public void OtherNodesSlice_IsOffScreen()
    {
        // Three nodes, center of the 5760 canvas is on node 2; node 3 sees nothing.
        Assert.Equal(MarkerVisibility.OffScreen, ExpectedPosition.Compute(Static, 1000, 64, 64, Slice(3840, 5760)).Visibility);
        Assert.Equal(MarkerVisibility.Visible, ExpectedPosition.Compute(Static, 1000, 64, 64, Slice(1920, 5760)).Visibility);
    }

    [Fact]
    public void MirroredSlice_FlipsXInsideTheSlice()
    {
        var linear = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 300 };
        for (long t = 0; t < 20_000; t += 700)
        {
            var normal = ExpectedPosition.Compute(linear, t, 64, 64, Slice(1920, 5760));
            var mirrored = ExpectedPosition.Compute(linear, t, 64, 64, Slice(1920, 5760, mirrored: true));
            Assert.Equal(normal.Visibility, mirrored.Visibility);
            if (normal.Visibility == MarkerVisibility.Visible)
                Assert.Equal(1920 - normal.X - 64, mirrored.X, 3);
        }
    }

    [Fact]
    public void PhysicalScale_ScalesToDevicePixels()
    {
        var one = ExpectedPosition.Compute(Static, 1000, 64, 64, Slice(0, 1920, scale: 1f));
        var two = ExpectedPosition.Compute(Static, 1000, 64, 64, Slice(0, 1920, scale: 2f));
        Assert.Equal(one.X * 2, two.X, 3);
        Assert.Equal(one.Y * 2, two.Y, 3);
        Assert.Equal(128, two.Width);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~ExpectedPositionTests` → build error.

- [ ] **Step 3: Implement**

`WaBiBaBuSy.Models/Testing/ExpectedPosition.cs`:
```csharp
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Testing;

public enum MarkerVisibility { Visible, Partial, OffScreen, NotStarted }

/// <summary>Where the marker must be on one node, device px, top-left of the node's surface.</summary>
public sealed class MarkerExpectation
{
    public MarkerVisibility Visibility { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float CenterX => X + Width / 2f;
    public float CenterY => Y + Height / 2f;
}

/// <summary>
/// Server-side replica of the player's single-sprite placement: <see cref="MovementCalculator"/> in
/// canvas units → <see cref="NodeMapping"/> → × <see cref="NodeLayout.Scale"/>.
/// </summary>
public static class ExpectedPosition
{
    public static MarkerExpectation Compute(MovementConfig movement, long effectiveElapsedMs, float spriteWidth, float spriteHeight, NodeLayout layout)
    {
        if (!SyncTiming.ShouldDrawAnimation(effectiveElapsedMs))
            return new MarkerExpectation { Visibility = MarkerVisibility.NotStarted };

        var (vx, vy) = MovementCalculator.Calculate(
            movement, effectiveElapsedMs,
            (int)MathF.Round(spriteWidth), (int)MathF.Round(spriteHeight),
            layout.CanvasWidth, layout.CanvasHeight,
            tileAlignStepX: 0f, canvasWraps: layout.Wraps);

        float scale = layout.Scale > 0f ? layout.Scale : 1f;
        float localY = NodeMapping.ToLocalY(vy, layout);
        bool yInside = localY >= 0 && localY + spriteHeight <= layout.Height;

        Span<float> copies = stackalloc float[2];
        int count = NodeMapping.WrapCopies(vx, spriteWidth, layout, copies);
        MarkerExpectation? partial = null;
        for (int i = 0; i < count; i++)
        {
            float localX = NodeMapping.ToLocalX(copies[i], spriteWidth, layout);
            bool fullyInside = localX >= 0 && localX + spriteWidth <= layout.Width && yInside;
            bool overlaps = localX + spriteWidth > 0 && localX < layout.Width && localY + spriteHeight > 0 && localY < layout.Height;
            if (fullyInside) return Make(MarkerVisibility.Visible, localX, localY, spriteWidth, spriteHeight, scale);
            if (overlaps) partial ??= Make(MarkerVisibility.Partial, localX, localY, spriteWidth, spriteHeight, scale);
        }
        return partial ?? new MarkerExpectation { Visibility = MarkerVisibility.OffScreen };
    }

    /// <summary>Layout a Simultaneous-mode player uses: its own monitor is the whole canvas.</summary>
    public static NodeLayout PerMonitorLayout(string nodeId, int width, int height) => new()
    {
        Id = nodeId, OffsetX = 0, OffsetY = 0, Width = width, Height = height,
        CanvasWidth = width, CanvasHeight = height, Scale = 1f,
    };

    private static MarkerExpectation Make(MarkerVisibility v, float x, float y, float w, float h, float scale) => new()
    {
        Visibility = v, X = x * scale, Y = y * scale, Width = w * scale, Height = h * scale,
    };
}
```

- [ ] **Step 4: Run the tests** → all pass. If `SingleNode_StaticMarker_IsCentered` is off by rounding, check `MovementCalculator.CalculateStatic` and adjust the expected value to its exact centering formula. The test documents the formula; it does not invent one.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Testing/ExpectedPosition.cs WaBiBaBuSy.Tests/ExpectedPositionTests.cs
git commit -m "feat(test-mode): expected marker position per node" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 6: Image checks — marker detection, pixel diff, timecode strip, position verdict

**Files:**
- Create: `WaBiBaBuSy.Models/Testing/MarkerDetector.cs`, `PixelDiff.cs`, `TimecodeStrip.cs`, `PositionCheck.cs`
- Create: `WaBiBaBuSy.Tests/ImageCheckTests.cs`

**Interfaces:**
- All buffers are BGRA8 (`B, G, R, A` per pixel), with `stride` bytes per row. That is what the player's readback produces, and what `PngPixels.Decode` (Task 12) returns.
- Produces:
  - `enum MarkerOrientation { Unknown, Normal, Mirrored }`; `MarkerDetection { bool Found; float X, Y, Width, Height; float CenterX, CenterY; MarkerOrientation Orientation; int PixelCount; bool TouchesEdge }`; `MarkerDetector.Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride) → MarkerDetection`; `MarkerDetector.IsMagenta(byte r, byte g, byte b) → bool`
  - `PixelDiffResult { bool SizeMismatch; int DiffPixels; double DiffPct; byte[]? DiffImage }`; `PixelDiff.Compare(ReadOnlySpan<byte> a, int aw, int ah, int aStride, ReadOnlySpan<byte> b, int bw, int bh, int bStride, int channelTolerance = 2) → PixelDiffResult`
  - `TimecodeStrip.CellPx = 8`, `Cells = 40`, `MarginPx = 8`; `Encode(uint value) → bool[]`; `ToCode(long elapsedMs) → uint`; `Origin(int surfaceHeight) → (int X, int Y)`; `Decode(ReadOnlySpan<byte> bgra, int width, int height, int stride) → uint?`
  - `PositionCheckResult { string NodeId, NodeName; int MonitorIndex; Verdict Verdict; string Message; MarkerExpectation Expected; MarkerDetection? Detected; float PlayerCenterX, PlayerCenterY; double ErrorPx; double PlayerErrorPx }`; `PositionCheck.Evaluate(NodeProbeSample sample, MarkerExpectation expected, MarkerDetection? detected, float scale, double tolerancePx) → PositionCheckResult`

Marker asset (made in Task 16): 64×64 px, a 4 px black border, a magenta interior `#FF00FF` (56×56), and a white 12×12 dot at x 40–51, y 26–37. The magenta bounding box has the same center as the sprite. A dot right of that center means `Normal`.

- [ ] **Step 1: Write the failing tests**

`WaBiBaBuSy.Tests/ImageCheckTests.cs`:
```csharp
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ImageCheckTests
{
    private const int W = 400, H = 300, Stride = W * 4;

    private static byte[] Surface(byte r = 30, byte g = 30, byte b = 30)
    {
        var px = new byte[Stride * H];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        return px;
    }

    private static void Fill(byte[] px, int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(H, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(W, x0 + w); x++)
            {
                int i = y * Stride + x * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
            }
    }

    /// <summary>The 64×64 marker asset, top-left at (x, y); mirrored draws the dot on the left.</summary>
    private static void Marker(byte[] px, int x, int y, bool mirrored = false)
    {
        Fill(px, x, y, 64, 64, 0, 0, 0);
        Fill(px, x + 4, y + 4, 56, 56, 255, 0, 255);
        Fill(px, mirrored ? x + 12 : x + 40, y + 26, 12, 12, 255, 255, 255);
    }

    [Fact]
    public void Detect_FindsCenterAndOrientation()
    {
        var px = Surface();
        Marker(px, 100, 50);
        var d = MarkerDetector.Detect(px, W, H, Stride);
        Assert.True(d.Found);
        Assert.Equal(132f, d.CenterX, 2);   // magenta box x 104..159 (56 px) → center 132 = center of the 100..163 sprite
        Assert.Equal(82f, d.CenterY, 2);    // y 54..109
        Assert.Equal(MarkerOrientation.Normal, d.Orientation);
        Assert.False(d.TouchesEdge);
    }

    [Fact]
    public void Detect_Mirrored() =>
        Assert.Equal(MarkerOrientation.Mirrored, MarkerDetector.Detect(Marker2(mirrored: true), W, H, Stride).Orientation);

    private static byte[] Marker2(bool mirrored) { var px = Surface(); Marker(px, 100, 50, mirrored); return px; }

    [Fact]
    public void Detect_NoMarker_NotFound() => Assert.False(MarkerDetector.Detect(Surface(), W, H, Stride).Found);

    [Fact]
    public void Detect_CutAtTheEdge_TouchesEdge()
    {
        var px = Surface();
        Marker(px, W - 30, 50);
        var d = MarkerDetector.Detect(px, W, H, Stride);
        Assert.True(d.Found);
        Assert.True(d.TouchesEdge);
    }

    [Fact]
    public void PixelDiff_Identical_IsZero()
    {
        var r = PixelDiff.Compare(Surface(), W, H, Stride, Surface(), W, H, Stride);
        Assert.Equal(0, r.DiffPixels);
        Assert.Equal(0, r.DiffPct);
        Assert.Equal(Stride * H, r.DiffImage!.Length);
    }

    [Fact]
    public void PixelDiff_CountsBeyondToleranceOnly_IgnoresAlpha()
    {
        var a = Surface();
        var b = Surface();
        b[0] = (byte)(b[0] + 3);          // pixel 0: blue +3 → differs
        b[4 + 1] = (byte)(b[4 + 1] + 2);  // pixel 1: green +2 → within tolerance
        b[8 + 3] = 0;                     // pixel 2: alpha only → ignored (swap chain alpha is "Ignore")
        var r = PixelDiff.Compare(a, W, H, Stride, b, W, H, Stride);
        Assert.Equal(1, r.DiffPixels);
        Assert.Equal(100.0 / (W * H), r.DiffPct, 6);
    }

    [Fact]
    public void PixelDiff_SizeMismatch()
    {
        var r = PixelDiff.Compare(Surface(), W, H, Stride, new byte[8], 2, 1, 8);
        Assert.True(r.SizeMismatch);
        Assert.Equal(100, r.DiffPct);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(123456u)]
    [InlineData(uint.MaxValue)]
    public void Timecode_RoundTrips(uint value)
    {
        var px = Surface();
        var cells = TimecodeStrip.Encode(value);
        var (ox, oy) = TimecodeStrip.Origin(H);
        for (int i = 0; i < cells.Length; i++)
        {
            byte c = cells[i] ? (byte)255 : (byte)0;
            Fill(px, ox + i * TimecodeStrip.CellPx, oy, TimecodeStrip.CellPx, TimecodeStrip.CellPx, c, c, c);
        }
        Assert.Equal(value, TimecodeStrip.Decode(px, W, H, Stride));
    }

    [Fact]
    public void Timecode_NoStrip_DecodesToNull() => Assert.Null(TimecodeStrip.Decode(Surface(), W, H, Stride));

    [Fact]
    public void Timecode_NegativeElapsed_WrapsInsteadOfThrowing() => Assert.Equal(uint.MaxValue, TimecodeStrip.ToCode(-1));

    // ── PositionCheck ──────────────────────────────────────────────────────

    private static NodeProbeSample Sample(float animX = 928, float animY = 508, bool flipped = false) => new()
    {
        NodeId = "a", NodeName = "pc-01", PlayerAnimX = animX, PlayerAnimY = animY,
        PlayerAnimWidth = 64, PlayerAnimHeight = 64, PlayerFlipped = flipped,
    };

    private static MarkerExpectation Expect(MarkerVisibility v = MarkerVisibility.Visible) =>
        new() { Visibility = v, X = 928, Y = 508, Width = 64, Height = 64 };

    private static MarkerDetection Detected(float cx, float cy, MarkerOrientation o = MarkerOrientation.Normal) =>
        new() { Found = true, X = cx - 28, Y = cy - 28, Width = 56, Height = 56, Orientation = o };

    [Fact]
    public void Position_OnTarget_Pass()
    {
        var r = PositionCheck.Evaluate(Sample(), Expect(), Detected(960, 540), 1f, 3);
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.Equal(0, r.ErrorPx, 3);
    }

    [Fact]
    public void Position_TenPixelsOff_Fail()
    {
        var r = PositionCheck.Evaluate(Sample(), Expect(), Detected(970, 540), 1f, 3);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Equal(10, r.ErrorPx, 3);
        Assert.Equal(0, r.PlayerErrorPx, 3);   // player math agreed → a drawing problem, not a math problem
    }

    [Fact]
    public void Position_NotFound_Fail() =>
        Assert.Equal(Verdict.Fail, PositionCheck.Evaluate(Sample(), Expect(), new MarkerDetection { Found = false }, 1f, 3).Verdict);

    [Fact]
    public void PartialMarker_Skipped() =>
        Assert.Equal(Verdict.Skipped, PositionCheck.Evaluate(Sample(), Expect(MarkerVisibility.Partial), Detected(10, 10), 1f, 3).Verdict);

    [Fact]
    public void OffScreenButFound_Fail() =>
        Assert.Equal(Verdict.Fail, PositionCheck.Evaluate(Sample(), Expect(MarkerVisibility.OffScreen), Detected(960, 540), 1f, 3).Verdict);

    [Fact]
    public void NotStarted_NothingVisible_Skipped() =>
        Assert.Equal(Verdict.Skipped, PositionCheck.Evaluate(Sample(), Expect(MarkerVisibility.NotStarted), new MarkerDetection(), 1f, 3).Verdict);

    [Fact]
    public void WrongOrientation_Fail()
    {
        var r = PositionCheck.Evaluate(Sample(flipped: false), Expect(), Detected(960, 540, MarkerOrientation.Mirrored), 1f, 3);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains("mirrored", r.Message);
    }
}
```

- [ ] **Step 2: Run to verify failure** → build errors.

- [ ] **Step 3: Implement**

`WaBiBaBuSy.Models/Testing/MarkerDetector.cs`:
```csharp
namespace WaBiBaBuSy.Models.Testing;

public enum MarkerOrientation { Unknown, Normal, Mirrored }

/// <summary>Where the marker's magenta area was found (device px) and which way its dot points.</summary>
public sealed class MarkerDetection
{
    public bool Found { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float CenterX => X + Width / 2f;
    public float CenterY => Y + Height / 2f;
    public MarkerOrientation Orientation { get; set; }
    public int PixelCount { get; set; }
    /// <summary>The magenta box touches the surface edge (marker partly off-screen).</summary>
    public bool TouchesEdge { get; set; }
}

/// <summary>Finds the test marker (magenta square with an off-center white dot) in a BGRA buffer.</summary>
public static class MarkerDetector
{
    /// <summary>Fewer magenta pixels than this = not found (noise, anti-aliasing).</summary>
    public const int MinPixels = 64;

    public static bool IsMagenta(byte r, byte g, byte b) => r >= 200 && b >= 200 && g <= 80;
    private static bool IsWhite(byte r, byte g, byte b) => r >= 220 && g >= 220 && b >= 220;

    public static MarkerDetection Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                if (!IsMagenta(bgra[i + 2], bgra[i + 1], bgra[i])) continue;
                count++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (count < MinPixels) return new MarkerDetection { Found = false, PixelCount = count };

        long sumX = 0;
        int dots = 0;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                int i = y * stride + x * 4;
                if (IsWhite(bgra[i + 2], bgra[i + 1], bgra[i])) { sumX += x; dots++; }
            }

        var orientation = MarkerOrientation.Unknown;
        if (dots >= 4)
            orientation = (double)sumX / dots > (minX + maxX) / 2.0 ? MarkerOrientation.Normal : MarkerOrientation.Mirrored;

        return new MarkerDetection
        {
            Found = true,
            X = minX,
            Y = minY,
            Width = maxX - minX + 1,
            Height = maxY - minY + 1,
            Orientation = orientation,
            PixelCount = count,
            TouchesEdge = minX == 0 || minY == 0 || maxX == width - 1 || maxY == height - 1,
        };
    }
}
```

`WaBiBaBuSy.Models/Testing/PixelDiff.cs`:
```csharp
namespace WaBiBaBuSy.Models.Testing;

public sealed class PixelDiffResult
{
    public bool SizeMismatch { get; set; }
    public int DiffPixels { get; set; }
    public double DiffPct { get; set; }
    /// <summary>BGRA, same size as the inputs: differing pixels red, others a dimmed grey of the first image.</summary>
    public byte[]? DiffImage { get; set; }
}

/// <summary>Exact-frame parity: compares B, G, R per pixel (alpha ignored — the swap chain alpha mode is Ignore).</summary>
public static class PixelDiff
{
    public static PixelDiffResult Compare(
        ReadOnlySpan<byte> a, int aw, int ah, int aStride,
        ReadOnlySpan<byte> b, int bw, int bh, int bStride,
        int channelTolerance = 2)
    {
        if (aw != bw || ah != bh) return new PixelDiffResult { SizeMismatch = true, DiffPct = 100 };

        var diff = new byte[aw * ah * 4];
        int count = 0;
        for (int y = 0; y < ah; y++)
            for (int x = 0; x < aw; x++)
            {
                int ia = y * aStride + x * 4, ib = y * bStride + x * 4, id = (y * aw + x) * 4;
                bool differs = Math.Abs(a[ia] - b[ib]) > channelTolerance
                    || Math.Abs(a[ia + 1] - b[ib + 1]) > channelTolerance
                    || Math.Abs(a[ia + 2] - b[ib + 2]) > channelTolerance;
                if (differs)
                {
                    count++;
                    diff[id + 2] = 255;   // red
                }
                else
                {
                    byte grey = (byte)((a[ia] + a[ia + 1] + a[ia + 2]) / 9);
                    diff[id] = grey; diff[id + 1] = grey; diff[id + 2] = grey;
                }
                diff[id + 3] = 255;
            }
        return new PixelDiffResult { DiffPixels = count, DiffPct = count * 100.0 / (aw * ah), DiffImage = diff };
    }
}
```

`WaBiBaBuSy.Models/Testing/TimecodeStrip.cs`:
```csharp
namespace WaBiBaBuSy.Models.Testing;

/// <summary>
/// Test-mode timecode: 40 square cells bottom-left — start guard 1010, 32 data bits (MSB first) of
/// the rendered elapsed ms, stop guard 0101. White = 1. Drawn in device px so a screenshot (or later
/// a phone photo) says which elapsed value the frame showed.
/// </summary>
public static class TimecodeStrip
{
    public const int CellPx = 8;
    public const int Cells = 40;
    public const int MarginPx = 8;

    private static readonly bool[] StartGuard = { true, false, true, false };
    private static readonly bool[] StopGuard = { false, true, false, true };

    /// <summary>Elapsed ms as the 32-bit code (wraps every ~49.7 days; negative values wrap too).</summary>
    public static uint ToCode(long elapsedMs) => unchecked((uint)elapsedMs);

    public static bool[] Encode(uint value)
    {
        var cells = new bool[Cells];
        StartGuard.CopyTo(cells, 0);
        for (int bit = 0; bit < 32; bit++)
            cells[4 + bit] = ((value >> (31 - bit)) & 1) != 0;
        StopGuard.CopyTo(cells, 36);
        return cells;
    }

    /// <summary>Top-left of the strip on a surface of <paramref name="surfaceHeight"/> device px.</summary>
    public static (int X, int Y) Origin(int surfaceHeight) => (MarginPx, surfaceHeight - MarginPx - CellPx);

    /// <summary>The code in a BGRA buffer, or null when there is no valid strip.</summary>
    public static uint? Decode(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        var (ox, oy) = Origin(height);
        if (oy < 0 || ox + Cells * CellPx > width) return null;

        var cells = new bool[Cells];
        int cy = oy + CellPx / 2;
        for (int c = 0; c < Cells; c++)
        {
            int i = cy * stride + (ox + c * CellPx + CellPx / 2) * 4;
            cells[c] = (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3 > 127;
        }
        for (int g = 0; g < 4; g++)
            if (cells[g] != StartGuard[g] || cells[36 + g] != StopGuard[g]) return null;

        uint value = 0;
        for (int bit = 0; bit < 32; bit++)
            if (cells[4 + bit]) value |= 1u << (31 - bit);
        return value;
    }
}
```

`WaBiBaBuSy.Models/Testing/PositionCheck.cs`:
```csharp
namespace WaBiBaBuSy.Models.Testing;

public sealed class PositionCheckResult
{
    public string NodeId { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public int MonitorIndex { get; set; }
    public Verdict Verdict { get; set; }
    public string Message { get; set; } = string.Empty;
    public MarkerExpectation Expected { get; set; } = new();
    public MarkerDetection? Detected { get; set; }
    /// <summary>Where the player itself believed the marker was (device px).</summary>
    public float PlayerCenterX { get; set; }
    public float PlayerCenterY { get; set; }
    /// <summary>Detected vs expected center: what people see.</summary>
    public double ErrorPx { get; set; }
    /// <summary>Player belief vs expected center: a math / config mismatch when large.</summary>
    public double PlayerErrorPx { get; set; }
}

/// <summary>Turns expected / player-believed / detected marker positions into a verdict (design §5.2).</summary>
public static class PositionCheck
{
    public static PositionCheckResult Evaluate(NodeProbeSample sample, MarkerExpectation expected, MarkerDetection? detected, float scale, double tolerancePx)
    {
        float s = scale > 0 ? scale : 1f;
        var r = new PositionCheckResult
        {
            NodeId = sample.NodeId,
            NodeName = sample.NodeName,
            MonitorIndex = sample.MonitorIndex,
            Expected = expected,
            Detected = detected,
            PlayerCenterX = (sample.PlayerAnimX + sample.PlayerAnimWidth / 2f) * s,
            PlayerCenterY = (sample.PlayerAnimY + sample.PlayerAnimHeight / 2f) * s,
        };
        bool found = detected?.Found == true;

        switch (expected.Visibility)
        {
            case MarkerVisibility.NotStarted:
                return Set(r, found ? Verdict.Fail : Verdict.Skipped, found ? "marker visible before the start" : "marker not shown yet");
            case MarkerVisibility.OffScreen:
                return Set(r, found ? Verdict.Fail : Verdict.Pass, found ? "marker on screen, expected on another node" : "off-screen as expected");
            case MarkerVisibility.Partial:
                return Set(r, Verdict.Skipped, "marker straddles the edge");
        }

        r.PlayerErrorPx = Distance(r.PlayerCenterX, r.PlayerCenterY, expected.CenterX, expected.CenterY);
        if (detected == null) return Set(r, Verdict.Skipped, "no capture");
        if (!found) return Set(r, Verdict.Fail, "marker not found in the capture");

        r.ErrorPx = Distance(detected!.CenterX, detected.CenterY, expected.CenterX, expected.CenterY);
        var expectedOrientation = sample.PlayerFlipped ? MarkerOrientation.Mirrored : MarkerOrientation.Normal;
        if (detected.Orientation != MarkerOrientation.Unknown && detected.Orientation != expectedOrientation)
            return Set(r, Verdict.Fail, $"sprite drawn {detected.Orientation.ToString().ToLowerInvariant()}, player says flipped={sample.PlayerFlipped}");

        return r.ErrorPx <= tolerancePx
            ? Set(r, Verdict.Pass, $"within {r.ErrorPx:F1} px")
            : Set(r, Verdict.Fail, $"off by {r.ErrorPx:F1} px (player math off by {r.PlayerErrorPx:F1} px)");
    }

    private static PositionCheckResult Set(PositionCheckResult r, Verdict v, string message)
    {
        r.Verdict = v;
        r.Message = message;
        return r;
    }

    private static double Distance(float x1, float y1, float x2, float y2) =>
        Math.Sqrt((x1 - x2) * (double)(x1 - x2) + (y1 - y2) * (double)(y1 - y2));
}
```

- [ ] **Step 4: Run the tests** → `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~ImageCheckTests` → all pass.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Testing WaBiBaBuSy.Tests/ImageCheckTests.cs
git commit -m "feat(test-mode): marker detection, pixel diff, timecode strip, position verdict" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 7: Player — test mode, timecode strip, live probe (readback + present time)

Read `.docs/2025.12_DIRECT2D_RENDERING_ARCHITECTURE.md` before this task. Every edit is in `WaBiBaBuSy.Player.D2D/Program.cs` (one static class) unless noted. Line numbers are from commit `9152877` and drift, so find each spot by the quoted code.

**Files:**
- Create: `WaBiBaBuSy.Player.Common/Messages/PlayerCommandTestMode.cs`, `PlayerCommandProbe.cs`
- Modify: `WaBiBaBuSy.Player.D2D/Program.cs`

**Interfaces:**
- Consumes: `PlayerProbeRequest`, `PlayerProbeReply`, `FrameIntervalTracker`, `TimecodeStrip` (Tasks 4, 6).
- Produces stdin commands (no stdout reply): `{"MessageType":"cmd_test_mode","Timecode":bool,"ClockSkewMs":int}` and `{"MessageType":"cmd_probe","ProbeId":…,"AtLocalUtcMs":…,"Capture":bool,"ExactElapsedMs":long|null,"CaptureDirectory":…}`.
- Produces stderr line: `SIGNAL:PROBE:<PlayerProbeReply JSON (Newtonsoft, PascalCase)>`, sent once per probe (on success, error or timeout of the present statistics).
- Produces for Task 8: `DrawTimecodeStrip`, `EmitProbeReplyAsync(PlayerProbeReply, Task<string?>)`, `SavePng(byte[], int, int, int, string)`, `NowUtcMs`, `_probeLock`, `_pendingProbe`.

- [ ] **Step 1: Add the IPC messages**

`WaBiBaBuSy.Player.Common/Messages/PlayerCommandTestMode.cs`:
```csharp
namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Automated test mode: timecode strip on/off and a simulated clock skew (ms added to the player's
/// clock). The player sends no stdout reply.
/// </summary>
public class PlayerCommandTestMode : PlayerMessageBase
{
    public PlayerCommandTestMode()
    {
        MessageType = "cmd_test_mode";
    }

    public bool Timecode { get; set; }
    public int ClockSkewMs { get; set; }
}
```

`WaBiBaBuSy.Player.Common/Messages/PlayerCommandProbe.cs`:
```csharp
namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Automated test mode: report the first frame rendered at or after <see cref="AtLocalUtcMs"/>
/// (or, with <see cref="ExactElapsedMs"/>, one offscreen frame at exactly that elapsed). The answer
/// arrives on stderr as <c>SIGNAL:PROBE:{json}</c>; there is no stdout reply.
/// </summary>
public class PlayerCommandProbe : PlayerMessageBase
{
    public PlayerCommandProbe()
    {
        MessageType = "cmd_probe";
    }

    public string ProbeId { get; set; } = string.Empty;
    public long AtLocalUtcMs { get; set; }
    public bool Capture { get; set; }
    public long? ExactElapsedMs { get; set; }
    public string CaptureDirectory { get; set; } = string.Empty;
}
```

- [ ] **Step 2: State and the skewed clock**

Add `using System.Diagnostics;` and `using WaBiBaBuSy.Models.Testing;` to the usings at the top of `Program.cs`. After the `_lastColorToggle` field (≈ line 570) add:
```csharp
    // ── Automated test mode (2026-09-30 design §5) ─────────────────────────
    // Nothing below changes a frame unless the host sent cmd_test_mode / cmd_probe.
    private static volatile bool _testTimecode;
    private static volatile int _testClockSkewMs;
    private static readonly FrameIntervalTracker _frameTracker = new(120);
    private static readonly object _probeLock = new();
    private static PlayerProbeRequest? _pendingProbe;                              // guarded by _probeLock
    private static readonly List<PendingPresentProbe> _awaitingPresent = new();   // render thread only
    private static long _lastFrameElapsedMs = long.MinValue;                       // MinValue = not playing
    private static int _lastFramePhaseMs;
    private static long _lastFrameRenderUtcMs;
    private static bool _lastFlipX;
    private static int _refreshHz;
    private static ID2D1SolidColorBrush? _timecodeWhite, _timecodeBlack;

    private sealed class PendingPresentProbe
    {
        public required PlayerProbeReply Reply { get; init; }
        public required uint PresentCount { get; init; }
        public required long DeadlineTick { get; init; }
        public required Task<string?> Capture { get; init; }
    }

    /// <summary>UTC now as this node sees it: the real clock plus the simulated test skew (0 outside tests).</summary>
    private static DateTime NowUtc => _testClockSkewMs == 0 ? DateTime.UtcNow : DateTime.UtcNow.AddMilliseconds(_testClockSkewMs);
    private static long NowUtcMs => new DateTimeOffset(NowUtc).ToUnixTimeMilliseconds();
```

Replace `ComputeEffectiveElapsedMs` (≈ line 1474):
```csharp
    private static long ComputeEffectiveElapsedMs()
    {
        long raw = (long)(NowUtc - _renderLoopStart).TotalMilliseconds;
        long effective = SyncTiming.ApplyNodePhase(raw, _nodeOrder, _movementConfig?.NodePhaseDelayMs ?? 0, _perMonitorMode);
        _lastFramePhaseMs = (int)(raw - effective);   // reported by probes
        return effective;
    }
```

In `RenderLoop()` replace `_renderLoopStart = DateTime.UtcNow;` with `_renderLoopStart = NowUtc;`.

In the start-animation handler (≈ line 3433), replace
```csharp
                    _renderLoopStart = DateTime.UtcNow.AddMilliseconds(
                        -(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cmd.StartTimestampMs));
                    _logger?.LogInformation("[START-CMD] Using shared timestamp: {Ts}ms, offset from now: {Offset}ms",
                        cmd.StartTimestampMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cmd.StartTimestampMs);
```
with
```csharp
                    // The shared start as an instant; elapsed = NowUtc − start (same result as before when no test skew is set).
                    _renderLoopStart = DateTimeOffset.FromUnixTimeMilliseconds(cmd.StartTimestampMs).UtcDateTime;
                    _logger?.LogInformation("[START-CMD] Using shared timestamp: {Ts}ms, offset from now: {Offset}ms",
                        cmd.StartTimestampMs, NowUtcMs - cmd.StartTimestampMs);
```
and in its `else` branch replace `_renderLoopStart = DateTime.UtcNow;` with `_renderLoopStart = NowUtc;`.

In `CreateSwapChain`, after `int refreshHz = QueryDisplayRefreshHz(od.DeviceName);` add `_refreshHz = refreshHz;`.

- [ ] **Step 3: Record per-frame timing in both native paths**

In the GIF branch (`if (shouldComposeNative && _d2dGifFrames != null)`), directly after `var elapsedMs = ComputeEffectiveElapsedMs();` add:
```csharp
                            _lastFrameElapsedMs = elapsedMs;
                            _lastFrameRenderUtcMs = NowUtcMs;
```
Add the same two lines after `var elapsedMs = ComputeEffectiveElapsedMs();` in the video branch. In the final `else` (solid-color fallback) add `_lastFrameElapsedMs = long.MinValue;` before `Color4 color;`.

In `DrawAnimationLayer`, single-source path, after the `if (_animationConfig.FaceTravelDirection) { … }` block add `_lastFlipX = flipX;`.

- [ ] **Step 4: Handle the two commands**

In `HandleJsonCommand`'s `switch (wrapper.MessageType)`, before `default:`:
```csharp
                case "cmd_test_mode":
                    // No stdout reply: the host does not wait for one (would break command/response pairing).
                    var testCmd = JsonConvert.DeserializeObject<PlayerCommandTestMode>(json);
                    if (testCmd != null)
                    {
                        _testTimecode = testCmd.Timecode;
                        _testClockSkewMs = testCmd.ClockSkewMs;
                        _logger?.LogInformation("[Test] Timecode={Timecode} ClockSkew={Skew}ms", testCmd.Timecode, testCmd.ClockSkewMs);
                    }
                    break;

                case "cmd_probe":
                    var probeCmd = JsonConvert.DeserializeObject<PlayerCommandProbe>(json);
                    if (probeCmd != null)
                    {
                        lock (_probeLock)
                        {
                            _pendingProbe = new PlayerProbeRequest
                            {
                                ProbeId = probeCmd.ProbeId,
                                AtLocalUtcMs = probeCmd.AtLocalUtcMs,
                                Capture = probeCmd.Capture,
                                ExactElapsedMs = probeCmd.ExactElapsedMs,
                                CaptureDirectory = probeCmd.CaptureDirectory,
                            };
                        }
                        _logger?.LogInformation("[Test] Probe {Id} at {At} (exact={Exact})", probeCmd.ProbeId, probeCmd.AtLocalUtcMs, probeCmd.ExactElapsedMs);
                    }
                    break;
```

- [ ] **Step 5: Timecode strip, probe capture, present statistics**

Add these methods to `Program` (near `DrawDebugOverlay`):
```csharp
    // ================================
    // Automated test mode
    // ================================

    /// <summary>Bottom-left binary strip of the rendered elapsed ms (device px, ignores the physical-canvas scale).</summary>
    private static void DrawTimecodeStrip(long elapsedMs)
    {
        if (_d2dContext == null || elapsedMs == long.MinValue) return;
        _timecodeWhite ??= _d2dContext.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        _timecodeBlack ??= _d2dContext.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 1f));

        var cells = TimecodeStrip.Encode(TimecodeStrip.ToCode(elapsedMs));
        var (ox, oy) = TimecodeStrip.Origin(_height);
        var savedTransform = _d2dContext.Transform;
        _d2dContext.Transform = Matrix3x2.Identity;
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = new RectangleF(ox + i * TimecodeStrip.CellPx, oy, TimecodeStrip.CellPx, TimecodeStrip.CellPx);
            _d2dContext.FillRectangle(cell, cells[i] ? _timecodeWhite : _timecodeBlack);
        }
        _d2dContext.Transform = savedTransform;
    }

    /// <summary>The pending live probe once its instant has arrived, filled with this frame's timing.</summary>
    private static PlayerProbeReply? TakeDueLiveProbe()
    {
        PlayerProbeRequest? req;
        lock (_probeLock)
        {
            req = _pendingProbe;
            if (req == null || req.ExactElapsedMs != null) return null;
            bool playing = _lastFrameElapsedMs != long.MinValue;
            bool due = playing ? _lastFrameRenderUtcMs >= req.AtLocalUtcMs : NowUtcMs >= req.AtLocalUtcMs;
            if (!due) return null;
            _pendingProbe = null;
        }

        bool notPlaying = _lastFrameElapsedMs == long.MinValue;
        return new PlayerProbeReply
        {
            ProbeId = req.ProbeId,
            Error = notPlaying ? "player is not playing an animation" : null,
            RenderedElapsedMs = notPlaying ? 0 : _lastFrameElapsedMs,
            PhaseMs = _lastFramePhaseMs,
            RenderLocalUtcMs = notPlaying ? NowUtcMs : _lastFrameRenderUtcMs,
            FrameIndex = _frameCount,
            Frames = _frameTracker.Snapshot(_refreshHz),
            AnimX = _animX,
            AnimY = _animY,
            AnimWidth = _animWidth,
            AnimHeight = _animHeight,
            Flipped = _lastFlipX,
            Width = _width,
            Height = _height,
            CapturePath = req.Capture ? Path.Combine(req.CaptureDirectory, $"probe-{req.ProbeId}-{Environment.ProcessId}.png") : null,
        };
    }

    /// <summary>Copy the back buffer to CPU memory now (render thread, a few ms); encode the PNG on the thread pool.</summary>
    private static Task<string?> StartCapture(IDXGISurface backBuffer, PlayerProbeReply reply)
    {
        if (reply.CapturePath == null) return Task.FromResult<string?>(null);
        try
        {
            var (pixels, stride) = ReadSurfaceBgra(backBuffer);
            int w = _width, h = _height;
            string path = reply.CapturePath;
            return Task.Run<string?>(() => { SavePng(pixels, w, h, stride, path); return path; });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Probe] Back-buffer readback failed");
            reply.Error = $"readback failed: {ex.Message}";
            reply.CapturePath = null;
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>Back buffer → staging texture → tightly packed BGRA bytes.</summary>
    private static (byte[] Pixels, int Stride) ReadSurfaceBgra(IDXGISurface surface)
    {
        var context = _d3dDevice!.ImmediateContext;
        using var texture = surface.QueryInterface<ID3D11Texture2D>();
        var desc = texture.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;
        using var staging = _d3dDevice.CreateTexture2D(desc);
        context.CopyResource(staging, texture);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int width = (int)desc.Width, height = (int)desc.Height, stride = width * 4;
            var pixels = new byte[stride * height];
            for (int y = 0; y < height; y++)
                Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, pixels, y * stride, stride);
            return (pixels, stride);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static void SavePng(byte[] bgra, int width, int height, int stride, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var bmp = new Bitmap(width, height, stride, System.Drawing.Imaging.PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
            bmp.Save(path, ImageFormat.Png);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void QueueForPresentStats(PlayerProbeReply reply, Task<string?> capture)
    {
        uint presentCount = 0;
        try { presentCount = _swapChain!.LastPresentCount; } catch { /* statistics unsupported → fallback below */ }
        _awaitingPresent.Add(new PendingPresentProbe
        {
            Reply = reply,
            PresentCount = presentCount,
            DeadlineTick = Environment.TickCount64 + 500,
            Capture = capture,
        });
    }

    /// <summary>
    /// Resolve queued probes once DXGI reports when their frame reached the screen; after 500 ms
    /// without statistics the probe is sent with PresentLocalUtcMs = null (the report then uses render time).
    /// </summary>
    private static void PollPresentStats()
    {
        if (_awaitingPresent.Count == 0 || _swapChain == null) return;
        FrameStatistics stats = default;
        bool haveStats;
        try { haveStats = _swapChain.GetFrameStatistics(out stats).Success && stats.PresentCount != 0; }
        catch { haveStats = false; }

        for (int i = _awaitingPresent.Count - 1; i >= 0; i--)
        {
            var p = _awaitingPresent[i];
            bool resolved = haveStats && p.PresentCount != 0 && stats.PresentCount >= p.PresentCount;
            if (!resolved && Environment.TickCount64 < p.DeadlineTick) continue;

            if (resolved)
            {
                long syncQpc = stats.SyncQPCTime;
                uint behind = stats.PresentCount - p.PresentCount;
                if (behind > 0 && _refreshHz > 0)
                    syncQpc -= (long)(behind * (double)Stopwatch.Frequency / _refreshHz);   // earlier frame: step back whole refreshes
                double ageMs = (Stopwatch.GetTimestamp() - syncQpc) * 1000.0 / Stopwatch.Frequency;
                p.Reply.PresentLocalUtcMs = NowUtcMs - (long)Math.Round(ageMs);
                p.Reply.PresentEstimated = behind > 0;
            }
            _awaitingPresent.RemoveAt(i);
            _ = EmitProbeReplyAsync(p.Reply, p.Capture);
        }
    }

    /// <summary>Wait for the PNG (if any), then send the reply on stderr.</summary>
    private static async Task EmitProbeReplyAsync(PlayerProbeReply reply, Task<string?> capture)
    {
        try { reply.CapturePath = await capture; }
        catch (Exception ex)
        {
            reply.CapturePath = null;
            reply.Error ??= $"PNG encode failed: {ex.Message}";
        }
        Console.Error.WriteLine("SIGNAL:PROBE:" + JsonConvert.SerializeObject(reply));
        Console.Error.Flush();
    }
```
Vortice 3.8.3 member names used above: `ID3D11Texture2D.Description`, `ResourceUsage.Staging`, `CpuAccessFlags.Read`, `ID3D11Device.CreateTexture2D(Texture2DDescription)`, `ID3D11DeviceContext.Map(resource, 0, MapMode.Read, MapFlags.None) → MappedSubresource { DataPointer, RowPitch }`, `IDXGISwapChain.LastPresentCount`, `IDXGISwapChain.GetFrameStatistics(out FrameStatistics) → Result`. If the compiler reports a different member name or overload, use the one IntelliSense offers for that type. The logic stays the same.

- [ ] **Step 6: Hook them into the frame**

At the end of the per-frame block, replace
```csharp
                    RecordMovementTrailSample();
                    DrawDebugOverlay();

                    _d2dContext.EndDraw();
                    _d2dContext.Target = null;
                    _swapChain.Present(1, PresentFlags.None);
```
with
```csharp
                    RecordMovementTrailSample();
                    DrawDebugOverlay();
                    if (_testTimecode) DrawTimecodeStrip(_lastFrameElapsedMs);

                    _d2dContext.EndDraw();
                    // Test mode: a due probe reads this exact frame back before it is presented.
                    var probe = TakeDueLiveProbe();
                    var capture = probe != null ? StartCapture(backBuffer, probe) : null;
                    _d2dContext.Target = null;
                    _swapChain.Present(1, PresentFlags.None);
                    _frameTracker.Record(Stopwatch.GetTimestamp());
                    if (probe != null) QueueForPresentStats(probe, capture!);
                    PollPresentStats();
```
`backBuffer` is the `using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);` declared at the top of the same block.

- [ ] **Step 7: Build and smoke-test the player alone**

Run: `dotnet build WaBiBaBuSy.Player.D2D -c Debug` → 0 errors.

Smoke test (the player must not crash on the new commands; no host needed):
```bash
dotnet run --project WaBiBaBuSy.Player.D2D -- --bounds 0,0,640,360
```
Type on stdin (each followed by Enter):
`{"MessageType":"cmd_test_mode","Timecode":true,"ClockSkewMs":0}` then
`{"MessageType":"cmd_probe","ProbeId":"t1","AtLocalUtcMs":0,"Capture":true,"CaptureDirectory":"C:\\Temp\\wbbs-probe"}`.
Expected: stderr prints `SIGNAL:PROBE:{"ProbeId":"t1",…,"Error":"player is not playing an animation",…}` and `C:\Temp\wbbs-probe\probe-t1-<pid>.png` exists (a black 640×360 image). Then type `EXIT`.

- [ ] **Step 8: Commit**

```bash
git add WaBiBaBuSy.Player.Common/Messages WaBiBaBuSy.Player.D2D/Program.cs
git commit -m "feat(test-mode): player timecode strip, live probe with back-buffer readback and present time" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 8: Player — exact frame (offscreen render at an exact elapsed)

**Files:**
- Modify: `WaBiBaBuSy.Player.D2D/Program.cs`

**Interfaces:**
- Consumes: Task 7 helpers (`_probeLock`, `_pendingProbe`, `EmitProbeReplyAsync`, `SavePng`, `NowUtcMs`, `_lastFlipX`).
- Produces: `cmd_probe` with `ExactElapsedMs` set → reply with `Exact = true`, `RenderedElapsedMs = ExactElapsedMs`, and a capture of a frame that was never presented. The live frame that follows is unchanged (animation state is saved and restored).

- [ ] **Step 1: Extract the GIF frame draw (render math unchanged)**

Add:
```csharp
    /// <summary>One native-D2D GIF/image frame at <paramref name="elapsedMs"/>: background, then movement + animation once started.</summary>
    private static void DrawNativeGifScene(long elapsedMs)
    {
        DrawBackground();

        // Before the shared start (future T0) or before this node's wave phase arrives, only the
        // background is shown — every node then reveals the sprite on the same frame.
        if (SyncTiming.ShouldDrawAnimation(elapsedMs))
        {
            UpdateAnimationPosition(elapsedMs);
            DrawAnimationLayer(elapsedMs);
        }
    }
```
In the GIF branch replace the block from `// Draw background (Stage 3)` through the closing brace of `if (SyncTiming.ShouldDrawAnimation(elapsedMs)) { … }` with `DrawNativeGifScene(elapsedMs);`. The calls and their order are identical.

- [ ] **Step 2: Save / restore the animation state an extra frame would touch**

```csharp
    private readonly record struct AnimationStateSnapshot(
        float AnimX, float AnimY, float AnimVirtualX, float RotationRad, int EndlessCellOffsetI,
        bool FacingLeft, float PrevFacingX, bool HasPrevFacingX, int TraverseCount, bool LastFlipX);

    private static AnimationStateSnapshot SaveAnimationState() => new(
        _animX, _animY, _animVirtualX, _animRotationRad, _endlessCellOffsetI,
        _facingLeft, _prevFacingX, _hasPrevFacingX, _traverseCount, _lastFlipX);

    private static void RestoreAnimationState(in AnimationStateSnapshot s)
    {
        _animX = s.AnimX; _animY = s.AnimY; _animVirtualX = s.AnimVirtualX; _animRotationRad = s.RotationRad;
        _endlessCellOffsetI = s.EndlessCellOffsetI; _facingLeft = s.FacingLeft; _prevFacingX = s.PrevFacingX;
        _hasPrevFacingX = s.HasPrevFacingX; _traverseCount = s.TraverseCount; _lastFlipX = s.LastFlipX;
    }
```

- [ ] **Step 3: Render the exact frame when due**

```csharp
    /// <summary>
    /// Test mode: render one offscreen frame at exactly the requested elapsed and read it back. It is
    /// never presented; the live animation state is restored afterwards. GIF/image scenes only, not IconZone
    /// (its lap logic rebuilds paths). Face-travel flip has no frame history here and is drawn unflipped.
    /// </summary>
    private static void RenderDueExactFrame()
    {
        PlayerProbeRequest? req;
        lock (_probeLock)
        {
            req = _pendingProbe;
            if (req?.ExactElapsedMs == null || NowUtcMs < req.AtLocalUtcMs) return;
            _pendingProbe = null;
        }

        long elapsed = req.ExactElapsedMs.Value;
        var reply = new PlayerProbeReply
        {
            ProbeId = req.ProbeId, Exact = true, RenderedElapsedMs = elapsed, RenderLocalUtcMs = NowUtcMs,
            FrameIndex = _frameCount, Width = _width, Height = _height,
        };

        bool gifPlaying;
        lock (_compositionLock) gifPlaying = _useNativeD2DComposition && _isPlaying && _d2dGifFrames != null;
        if (!gifPlaying || _backgroundMode == BackgroundMode.IconZone || _d2dContext == null)
        {
            reply.Error = _backgroundMode == BackgroundMode.IconZone
                ? "exact frames are not supported on IconZone backgrounds"
                : "exact frames need a playing GIF/image animation (video is not supported)";
            _ = EmitProbeReplyAsync(reply, Task.FromResult<string?>(null));
            return;
        }

        var saved = SaveAnimationState();
        ID2D1Bitmap1? target = null, readback = null;
        try
        {
            var size = new SizeI(_width, _height);
            var format = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore);
            target = _d2dContext.CreateBitmap(size, IntPtr.Zero, 0,
                new BitmapProperties1 { PixelFormat = format, DpiX = 96f, DpiY = 96f, BitmapOptions = BitmapOptions.Target });
            _d2dContext.Target = target;
            _d2dContext.BeginDraw();
            _hasPrevFacingX = false;
            _facingLeft = false;
            DrawNativeGifScene(elapsed);
            reply.AnimX = _animX; reply.AnimY = _animY; reply.AnimWidth = _animWidth; reply.AnimHeight = _animHeight;
            reply.Flipped = _lastFlipX;
            reply.PhaseMs = 0;   // exact frames are rendered at the given effective elapsed; no phase applies
            _d2dContext.EndDraw();
            _d2dContext.Target = null;

            readback = _d2dContext.CreateBitmap(size, IntPtr.Zero, 0,
                new BitmapProperties1 { PixelFormat = format, DpiX = 96f, DpiY = 96f, BitmapOptions = BitmapOptions.CpuRead | BitmapOptions.CannotDraw });
            readback.CopyFromBitmap(target);
            int stride = _width * 4;
            var pixels = new byte[stride * _height];
            var mapped = readback.Map(MapOptions.Read);
            try
            {
                for (int y = 0; y < _height; y++)
                    Marshal.Copy(mapped.Bits + y * (int)mapped.Pitch, pixels, y * stride, stride);
            }
            finally
            {
                readback.Unmap();
            }

            Task<string?> capture = Task.FromResult<string?>(null);
            if (req.Capture)
            {
                string path = Path.Combine(req.CaptureDirectory, $"exact-{req.ProbeId}-{Environment.ProcessId}.png");
                int w = _width, h = _height;
                capture = Task.Run<string?>(() => { SavePng(pixels, w, h, stride, path); return path; });
            }
            _ = EmitProbeReplyAsync(reply, capture);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Probe] Exact frame failed");
            try { _d2dContext.Target = null; } catch { /* already reset */ }
            reply.Error = $"exact frame failed: {ex.Message}";
            _ = EmitProbeReplyAsync(reply, Task.FromResult<string?>(null));
        }
        finally
        {
            RestoreAnimationState(saved);
            readback?.Dispose();
            target?.Dispose();
        }
    }
```
Call it at the start of the per-frame block, before the back buffer is bound:
```csharp
                if (_d2dContext != null && _swapChain != null)
                {
                    RenderDueExactFrame();   // test mode only: offscreen, never presented

                    // Stage 1: Per-frame pattern - bind DeviceContext to current back buffer
                    using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
```
(Vortice: `ID2D1DeviceContext.CreateBitmap(SizeI, IntPtr, uint, BitmapProperties1) → ID2D1Bitmap1`, `ID2D1Bitmap1.CopyFromBitmap`, `Map(MapOptions) → MappedRectangle { Pitch, Bits }`, `Unmap()`.)

- [ ] **Step 4: Build**

Run: `dotnet build WaBiBaBuSy.Player.D2D -c Debug` → 0 errors. The end-to-end check comes in Task 16.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Player.D2D/Program.cs
git commit -m "feat(test-mode): player exact-frame offscreen render for pixel parity" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 9: Host side — probe targets, fan-out, test project on Core

**Files:**
- Create: `WaBiBaBuSy.Core/Services/Testing/ITestProbeTarget.cs`, `ProbeFanOut.cs`
- Modify: `WaBiBaBuSy.WallpaperEngine/Direct2D/D2DPlayerHost.cs`, `WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs`
- Modify: `WaBiBaBuSy.Tests/WaBiBaBuSy.Tests.csproj` (→ `net9.0-windows`, reference Core)
- Create: `WaBiBaBuSy.Tests/ProbeFanOutTests.cs`

**Interfaces:**
- Produces: `interface ITestProbeTarget { int MonitorIndex { get; } Task SetTestModeAsync(bool timecode, int clockSkewMs); Task<PlayerProbeReply?> ProbeAsync(PlayerProbeRequest request, TimeSpan timeout, CancellationToken ct); }` (namespace `WaBiBaBuSy.Core.Services.Testing`)
- Produces: `ProbeFanOut.ProbeAllAsync(IReadOnlyList<ITestProbeTarget> targets, PlayerProbeRequest request, TimeSpan timeout, CancellationToken ct) → Task<IReadOnlyList<PlayerProbeReply>>` (never null entries; `MonitorIndex` set from the target); `ProbeFanOut.SetTestModeAllAsync(IReadOnlyList<ITestProbeTarget>, bool timecode, int clockSkewMs) → Task`
- Produces: `D2DPlayerHost.SendTestModeAsync(bool, int)`, `D2DPlayerHost.ProbeAsync(PlayerProbeRequest, TimeSpan, CancellationToken) → Task<PlayerProbeReply?>`; `D2DCompositionService : ITestProbeTarget`

- [ ] **Step 1: Move the test project to `net9.0-windows` and reference Core**

In `WaBiBaBuSy.Tests/WaBiBaBuSy.Tests.csproj` change `<TargetFramework>net9.0</TargetFramework>` to `<TargetFramework>net9.0-windows</TargetFramework>` and add `<ProjectReference Include="..\WaBiBaBuSy.Core\WaBiBaBuSy.Core.csproj" />` to the project-reference item group. Run `dotnet test WaBiBaBuSy.Tests` → the existing suite is still green.

- [ ] **Step 2: Write the failing test**

`WaBiBaBuSy.Tests/ProbeFanOutTests.cs`:
```csharp
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ProbeFanOutTests
{
    private sealed class FakeTarget : ITestProbeTarget
    {
        public int MonitorIndex { get; init; }
        public Func<PlayerProbeRequest, PlayerProbeReply?> Answer { get; init; } = r => new PlayerProbeReply { ProbeId = r.ProbeId };
        public (bool Timecode, int Skew)? LastMode { get; private set; }
        public Task SetTestModeAsync(bool timecode, int clockSkewMs) { LastMode = (timecode, clockSkewMs); return Task.CompletedTask; }
        public Task<PlayerProbeReply?> ProbeAsync(PlayerProbeRequest request, TimeSpan timeout, CancellationToken ct) => Task.FromResult(Answer(request));
    }

    [Fact]
    public async Task ProbeAll_SetsMonitorIndex_AndTurnsSilenceAndExceptionsIntoErrors()
    {
        var targets = new ITestProbeTarget[]
        {
            new FakeTarget { MonitorIndex = 0 },
            new FakeTarget { MonitorIndex = 1, Answer = _ => null },
            new FakeTarget { MonitorIndex = 2, Answer = _ => throw new InvalidOperationException("pipe closed") },
        };

        var replies = await ProbeFanOut.ProbeAllAsync(targets, new PlayerProbeRequest { ProbeId = "p1" }, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(new[] { 0, 1, 2 }, replies.Select(r => r.MonitorIndex));
        Assert.All(replies, r => Assert.Equal("p1", r.ProbeId));
        Assert.Null(replies[0].Error);
        Assert.Equal("no reply from player", replies[1].Error);
        Assert.Equal("pipe closed", replies[2].Error);
    }

    [Fact]
    public async Task SetTestModeAll_ReachesEveryTarget()
    {
        var a = new FakeTarget();
        var b = new FakeTarget { MonitorIndex = 1 };
        await ProbeFanOut.SetTestModeAllAsync(new ITestProbeTarget[] { a, b }, timecode: true, clockSkewMs: 2000);
        Assert.Equal((true, 2000), a.LastMode);
        Assert.Equal((true, 2000), b.LastMode);
    }
}
```

- [ ] **Step 3: Run to verify failure** → build error (`WaBiBaBuSy.Core.Services.Testing` missing).

- [ ] **Step 4: Implement the Core pieces**

`WaBiBaBuSy.Core/Services/Testing/ITestProbeTarget.cs`:
```csharp
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>One monitor's D2D player as seen by the automated test mode.</summary>
public interface ITestProbeTarget
{
    /// <summary>Monitor index on this machine.</summary>
    int MonitorIndex { get; }

    /// <summary>Timecode strip on/off and simulated clock skew. Apply before the scene's start command.</summary>
    Task SetTestModeAsync(bool timecode, int clockSkewMs);

    /// <summary>Probe the player; null when it does not answer within <paramref name="timeout"/>.</summary>
    Task<PlayerProbeReply?> ProbeAsync(PlayerProbeRequest request, TimeSpan timeout, CancellationToken ct);
}
```

`WaBiBaBuSy.Core/Services/Testing/ProbeFanOut.cs`:
```csharp
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>Probe or configure all local players of a machine at once.</summary>
public static class ProbeFanOut
{
    /// <summary>One reply per target, in target order; silence or an exception becomes an error reply.</summary>
    public static async Task<IReadOnlyList<PlayerProbeReply>> ProbeAllAsync(
        IReadOnlyList<ITestProbeTarget> targets, PlayerProbeRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var tasks = targets.Select(async target =>
        {
            PlayerProbeReply? reply = null;
            string? error = null;
            try
            {
                reply = await target.ProbeAsync(request, timeout, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                error = ex.Message;
            }
            reply ??= new PlayerProbeReply { ProbeId = request.ProbeId, Error = error ?? "no reply from player" };
            reply.MonitorIndex = target.MonitorIndex;
            return reply;
        });
        return await Task.WhenAll(tasks);
    }

    public static Task SetTestModeAllAsync(IReadOnlyList<ITestProbeTarget> targets, bool timecode, int clockSkewMs) =>
        Task.WhenAll(targets.Select(t => t.SetTestModeAsync(timecode, clockSkewMs)));
}
```

- [ ] **Step 5: Run the tests** → `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~ProbeFanOutTests` → pass.

- [ ] **Step 6: `D2DPlayerHost`: send, await `SIGNAL:PROBE`**

Add usings `System.Collections.Concurrent` and `WaBiBaBuSy.Models.Testing`. Add the field and the methods:
```csharp
    // Automated test mode: probes waiting for their SIGNAL:PROBE line on stderr, by probe id.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PlayerProbeReply>> _pendingProbes = new();

    /// <summary>Test mode on/off (timecode strip, simulated clock skew). Fire-and-forget: the player sends no stdout reply.</summary>
    public async Task SendTestModeAsync(bool timecode, int clockSkewMs)
    {
        if (!IsRunning) return;
        await SendCommandAsync(JsonConvert.SerializeObject(new PlayerCommandTestMode { Timecode = timecode, ClockSkewMs = clockSkewMs }));
    }

    /// <summary>Probe the player; completes when its SIGNAL:PROBE arrives on stderr, null on timeout.</summary>
    public async Task<PlayerProbeReply?> ProbeAsync(PlayerProbeRequest request, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsRunning) return null;
        var waiter = new TaskCompletionSource<PlayerProbeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingProbes[request.ProbeId] = waiter;
        try
        {
            await SendCommandAsync(JsonConvert.SerializeObject(new PlayerCommandProbe
            {
                ProbeId = request.ProbeId,
                AtLocalUtcMs = request.AtLocalUtcMs,
                Capture = request.Capture,
                ExactElapsedMs = request.ExactElapsedMs,
                CaptureDirectory = request.CaptureDirectory,
            }));
            var finished = await Task.WhenAny(waiter.Task, Task.Delay(timeout, ct));
            return finished == waiter.Task ? await waiter.Task : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _pendingProbes.TryRemove(request.ProbeId, out _);
        }
    }
```
In `OnPlayerError`, before the `SIGNAL:LAP_COMPLETE:` check:
```csharp
            if (line.StartsWith("SIGNAL:PROBE:", StringComparison.Ordinal))
            {
                try
                {
                    var reply = JsonConvert.DeserializeObject<PlayerProbeReply>(line["SIGNAL:PROBE:".Length..]);
                    if (reply != null && _pendingProbes.TryRemove(reply.ProbeId, out var waiter))
                        waiter.TrySetResult(reply);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "[Player] Malformed PROBE signal");
                }
                return;
            }
```

- [ ] **Step 7: `D2DCompositionService : ITestProbeTarget`**

Add `using WaBiBaBuSy.Core.Services.Testing;` and `using WaBiBaBuSy.Models.Testing;`, change the declaration to `public class D2DCompositionService : IDisposable, ITestProbeTarget`, and add:
```csharp
    /// <inheritdoc />
    public int MonitorIndex => _monitorIndex;

    /// <inheritdoc />
    public async Task SetTestModeAsync(bool timecode, int clockSkewMs)
    {
        foreach (var host in _playerHosts.Values)
        {
            if (!host.IsRunning) continue;
            try { await host.SendTestModeAsync(timecode, clockSkewMs); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to send test mode to a player host"); }
        }
    }

    /// <inheritdoc />
    public async Task<PlayerProbeReply?> ProbeAsync(PlayerProbeRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var host = _playerHosts.Values.FirstOrDefault(h => h.IsRunning);
        return host == null ? null : await host.ProbeAsync(request, timeout, ct);
    }
```

- [ ] **Step 8: Build + tests** → `dotnet build WaBiBaBuSy.sln -c Debug` 0 errors; `dotnet test WaBiBaBuSy.Tests` green.

- [ ] **Step 9: Commit**

```bash
git add WaBiBaBuSy.Core/Services/Testing WaBiBaBuSy.WallpaperEngine WaBiBaBuSy.Tests
git commit -m "feat(test-mode): probe targets, fan-out, player host awaits SIGNAL:PROBE" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 10: Proto + client — TEST_MODE / TEST_PROBE, simulated skew, result upload, perf sampling

**Files:**
- Modify: `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto`
- Modify: `WaBiBaBuSy.Models/Configuration/ClientConfiguration.cs` (`AllowTestRuns`)
- Create: `WaBiBaBuSy.Models/Testing/TestModeErrors.cs`
- Create: `WaBiBaBuSy.Core/Services/Testing/PerfSampler.cs`
- Modify: `WaBiBaBuSy.Core/WaBiBaBuSy.Core.csproj` (package `System.Diagnostics.PerformanceCounter`)
- Modify: `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (client-side wiring: probe targets + test mode for new remote players)
- Create: `WaBiBaBuSy.Tests/PerfSamplerTests.cs`

**Interfaces:**
- Produces proto: `CommandType.TEST_MODE = 10`, `TEST_PROBE = 11`; `SyncParameters` `test_timecode = 29`, `test_clock_skew_ms = 30`, `test_probe_id = 31`, `test_capture = 32`, `test_has_exact_elapsed = 33`, `test_exact_elapsed_ms = 34`; RPC `SubmitProbeResult(stream ProbeResultChunk) returns (ProbeResultAck)`; messages `ProbeResultChunk { oneof part { ProbeResultHeader header = 1; ProbeCaptureChunk capture = 2; } }`, `ProbeResultHeader { client_id = 1; probe_id = 2; result_json = 3; }`, `ProbeCaptureChunk { monitor_index = 1; data = 2; }`, `ProbeResultAck { success = 1; message = 2; }`
- Produces: `TestModeErrors.Disabled`, `TestModeErrors.NoPlayer` (string constants, used by the runner's preflight in Task 12)
- Produces: `PerfSampler.Sample() → PerfSample`; `PerfSampler.PlayerProcessName`
- Produces on `WallpaperSyncClient`: `Func<IReadOnlyList<ITestProbeTarget>>? TestProbeTargets { get; set; }`; `(bool Timecode, int ClockSkewMs) TestModeState { get; }`
- Wire semantics: `TEST_PROBE.timestamp_utc` is the probe instant in **server** time. The client's existing conversion (`TimestampUtc -= clockOffset`) turns it into local time before the handler sees it. The result JSON is `RemoteProbeResult` (System.Text.Json defaults). PNGs follow as 256 KB `ProbeCaptureChunk`s per monitor.

- [ ] **Step 1: Proto**

In `enum CommandType` after `PREFETCH = 9;`:
```proto
  TEST_MODE = 10;         // Automated test run: timecode strip on/off, simulated clock skew (params.test_*)
  TEST_PROBE = 11;        // Automated test run: report the frame shown at timestamp_utc (params.test_*)
```
In `message SyncParameters` after `log_to_utc_ms = 28;`:
```proto
  bool test_timecode = 29;             // TEST_MODE: draw the timecode strip
  int32 test_clock_skew_ms = 30;       // TEST_MODE: simulated skew added to this client's clock (0 = off)
  string test_probe_id = 31;           // TEST_PROBE
  bool test_capture = 32;              // TEST_PROBE: read the frame back as PNG
  bool test_has_exact_elapsed = 33;    // TEST_PROBE: exact-frame probe (offscreen render)
  int64 test_exact_elapsed_ms = 34;    // TEST_PROBE: the exact effective elapsed to render
```
In `service WallpaperSync` after `SendClientLogs`:
```proto
  // Automated test run: client uploads one probe result (header, then PNG chunks per monitor)
  rpc SubmitProbeResult(stream ProbeResultChunk) returns (ProbeResultAck);
```
At the end of the file:
```proto
// ============================================================================
// Automated test mode (2026-09-30)
// ============================================================================

message ProbeResultChunk {
  oneof part {
    ProbeResultHeader header = 1;
    ProbeCaptureChunk capture = 2;
  }
}

message ProbeResultHeader {
  string client_id = 1;
  string probe_id = 2;
  string result_json = 3;   // RemoteProbeResult (System.Text.Json); captures travel as chunks
}

message ProbeCaptureChunk {
  int32 monitor_index = 1;
  bytes data = 2;           // up to 256 KB of PNG bytes, in order
}

message ProbeResultAck {
  bool success = 1;
  string message = 2;
}
```

- [ ] **Step 2: Settings flag and error texts**

In `ClientConfiguration` (after `PauseOnFullscreen`):
```csharp
    /// <summary>
    /// Answer automated test runs from the server (timecode strip, probes with wallpaper screenshots).
    /// Captures contain only the wallpaper back buffer, never other windows.
    /// </summary>
    public bool AllowTestRuns { get; set; } = true;
```

`WaBiBaBuSy.Models/Testing/TestModeErrors.cs`:
```csharp
namespace WaBiBaBuSy.Models.Testing;

/// <summary>Error texts a client sends back; the runner's preflight matches on them.</summary>
public static class TestModeErrors
{
    public const string Disabled = "test runs are disabled on this client (Settings → Client → Allow test runs)";
    public const string NoPlayer = "no player running on this client";
}
```

- [ ] **Step 3: PerfSampler (failing test first)**

Run: `dotnet add WaBiBaBuSy.Core package System.Diagnostics.PerformanceCounter`

`WaBiBaBuSy.Tests/PerfSamplerTests.cs`:
```csharp
using WaBiBaBuSy.Core.Services.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class PerfSamplerTests
{
    [Fact]
    public void FirstSample_HasMemoryButNoCpu_SecondHasCpu()
    {
        var sampler = new PerfSampler();
        var first = sampler.Sample();
        Assert.True(first.AppMemoryMb > 0);
        Assert.Null(first.AppCpuPercent);   // CPU% needs two samples

        var spin = DateTime.UtcNow.AddMilliseconds(50);
        while (DateTime.UtcNow < spin) { }  // burn a little CPU

        var second = sampler.Sample();
        Assert.NotNull(second.AppCpuPercent);
        Assert.InRange(second.AppCpuPercent!.Value, 0, 100);
        Assert.Equal(0, second.PlayerProcesses);   // no player runs during unit tests
    }
}
```
Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~PerfSamplerTests` → build error.

`WaBiBaBuSy.Core/Services/Testing/PerfSampler.cs`:
```csharp
using System.Diagnostics;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>
/// CPU / memory of this app and its D2D player processes, plus the players' 3D-engine GPU
/// utilisation from the "GPU Engine" performance counters. CPU% and GPU% need two samples;
/// the first sample reports null for them.
/// </summary>
public sealed class PerfSampler
{
    public const string PlayerProcessName = "WaBiBaBuSy.Player.D2D";

    private readonly Dictionary<int, (TimeSpan Cpu, long Tick)> _lastCpu = new();
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new();

    public PerfSample Sample()
    {
        using var app = Process.GetCurrentProcess();
        var players = Process.GetProcessesByName(PlayerProcessName);
        try
        {
            double? playerCpu = null;
            double playerMemory = 0;
            foreach (var p in players)
            {
                var cpu = CpuPercent(p);
                if (cpu.HasValue) playerCpu = (playerCpu ?? 0) + cpu.Value;
                playerMemory += p.WorkingSet64 / (1024.0 * 1024.0);
            }
            return new PerfSample
            {
                AppCpuPercent = CpuPercent(app),
                AppMemoryMb = app.WorkingSet64 / (1024.0 * 1024.0),
                PlayerCpuPercent = playerCpu,
                PlayerMemoryMb = playerMemory,
                PlayerProcesses = players.Length,
                GpuPercent = GpuPercent(players.Select(p => p.Id).ToList()),
            };
        }
        finally
        {
            foreach (var p in players) p.Dispose();
        }
    }

    private double? CpuPercent(Process process)
    {
        try
        {
            process.Refresh();
            long now = Environment.TickCount64;
            var cpu = process.TotalProcessorTime;
            double? result = null;
            if (_lastCpu.TryGetValue(process.Id, out var previous) && now > previous.Tick)
                result = (cpu - previous.Cpu).TotalMilliseconds / (now - previous.Tick) / Environment.ProcessorCount * 100.0;
            _lastCpu[process.Id] = (cpu, now);
            return result;
        }
        catch (Exception)
        {
            return null;   // process exited between listing and reading
        }
    }

    private double? GpuPercent(IReadOnlyList<int> playerPids)
    {
        if (playerPids.Count == 0) return null;
        try
        {
            var prefixes = playerPids.Select(pid => $"pid_{pid}_").ToList();
            var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                .Where(n => n.Contains("engtype_3D", StringComparison.Ordinal) && prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
                .ToList();

            double total = 0;
            bool anyWarm = false;
            foreach (var name in names)
            {
                if (!_gpuCounters.TryGetValue(name, out var counter))
                {
                    counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true);
                    counter.NextValue();   // first read of a rate counter is always 0
                    _gpuCounters[name] = counter;
                    continue;
                }
                total += counter.NextValue();
                anyWarm = true;
            }
            return anyWarm ? total : null;
        }
        catch (Exception)
        {
            return null;   // counters unavailable (no WDDM 2.x driver, locked-down machine)
        }
    }
}
```
Run the test → pass.

- [ ] **Step 4: Client — state, skewed clock, command handling**

In `WallpaperSyncClient.cs` add usings `System.Text.Json`, `WaBiBaBuSy.Core.Services.Testing`, `WaBiBaBuSy.Models.Testing`. Add fields and properties next to `_clockOffset`:
```csharp
    // Automated test mode (2026-09-30): simulated skew of this machine's clock and the players to probe.
    private volatile int _clockSkewMs;
    private readonly PerfSampler _perfSampler = new();

    /// <summary>Set by the UI: this client's running D2D players (one per monitor that plays).</summary>
    public Func<IReadOnlyList<ITestProbeTarget>>? TestProbeTargets { get; set; }

    /// <summary>Last TEST_MODE from the server; applied to players created later (before their start).</summary>
    public (bool Timecode, int ClockSkewMs) TestModeState { get; private set; }

    /// <summary>This machine's clock as the sync sees it: real UTC plus the simulated test skew (0 outside tests).</summary>
    private long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _clockSkewMs;
```
In `SendHeartbeatAsync`, replace both `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` calls (`sendMs`, `receiveMs`) with `NowMs()`.

In the SyncStream loop, directly after the FETCH_LOGS branch:
```csharp
                    // Automated test mode: handled here, never passed to the playback service (no scheduling wait).
                    if (command.Type == CommandType.TestMode)
                    {
                        ApplyTestMode(command.Params ?? new SyncParameters());
                        continue;
                    }
                    if (command.Type == CommandType.TestProbe)
                    {
                        var probeCommand = command;
                        _ = Task.Run(() => HandleTestProbeAsync(probeCommand));
                        continue;
                    }
```
Add the handlers (e.g. after `SendLogsToServerAsync`):
```csharp
    /// <summary>
    /// TEST_MODE: remember the state (for players created later), switch the simulated skew and
    /// re-measure the clock offset right away, then forward to the running players.
    /// </summary>
    private void ApplyTestMode(SyncParameters p)
    {
        if (!_configuration.AllowTestRuns)
        {
            _logger.LogWarning("Ignoring TEST_MODE: test runs are disabled on this client");
            return;
        }

        TestModeState = (p.TestTimecode, p.TestClockSkewMs);
        if (_clockSkewMs != p.TestClockSkewMs)
        {
            _clockSkewMs = p.TestClockSkewMs;
            _clockOffset.Reset();   // old samples were taken with the old clock
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    try { await SendHeartbeatAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Re-sync heartbeat failed"); }
                    await Task.Delay(300);
                }
            });
        }
        _logger.LogInformation("[Test] Timecode={Timecode} SimulatedSkew={Skew}ms", p.TestTimecode, p.TestClockSkewMs);

        var targets = TestProbeTargets?.Invoke() ?? Array.Empty<ITestProbeTarget>();
        _ = ProbeFanOut.SetTestModeAllAsync(targets, p.TestTimecode, p.TestClockSkewMs);
    }

    /// <summary>TEST_PROBE: probe every local player at the (already local) instant and upload the result.</summary>
    private async Task HandleTestProbeAsync(SyncCommand command)
    {
        var p = command.Params ?? new SyncParameters();
        var result = new RemoteProbeResult
        {
            ClientId = _clientId ?? string.Empty,
            ProbeId = p.TestProbeId,
            ClockOffsetMs = _clockOffset.OffsetMs,
            RttMs = _clockOffset.RttMs,
        };
        try
        {
            var targets = TestProbeTargets?.Invoke() ?? Array.Empty<ITestProbeTarget>();
            if (!_configuration.AllowTestRuns) result.Error = TestModeErrors.Disabled;
            else if (targets.Count == 0) result.Error = TestModeErrors.NoPlayer;
            else
            {
                var request = new PlayerProbeRequest
                {
                    ProbeId = p.TestProbeId,
                    AtLocalUtcMs = command.TimestampUtc,   // converted to this machine's clock on receipt
                    Capture = p.TestCapture,
                    ExactElapsedMs = p.TestHasExactElapsed ? p.TestExactElapsedMs : null,
                    CaptureDirectory = Path.Combine(Path.GetTempPath(), "WaBiBaBuSy", "probes"),
                };
                var wait = TimeSpan.FromMilliseconds(Math.Max(0, command.TimestampUtc - NowMs()) + 2000);
                result.Replies = (await ProbeFanOut.ProbeAllAsync(targets, request, wait, CancellationToken.None)).ToList();
                foreach (var reply in result.Replies)
                {
                    if (reply.CapturePath == null || !File.Exists(reply.CapturePath)) continue;
                    result.Captures[reply.MonitorIndex] = await File.ReadAllBytesAsync(reply.CapturePath);
                    try { File.Delete(reply.CapturePath); } catch { /* temp file */ }
                }
            }
            result.Perf = _perfSampler.Sample();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test probe {ProbeId} failed", p.TestProbeId);
            result.Error ??= ex.Message;
        }
        await SubmitProbeResultAsync(result);
    }

    private async Task SubmitProbeResultAsync(RemoteProbeResult result)
    {
        if (_client == null) return;
        const int chunkSize = 256 * 1024;
        try
        {
            using var call = _client.SubmitProbeResult();
            await call.RequestStream.WriteAsync(new ProbeResultChunk
            {
                Header = new ProbeResultHeader { ClientId = result.ClientId, ProbeId = result.ProbeId, ResultJson = JsonSerializer.Serialize(result) }
            });
            foreach (var (monitor, png) in result.Captures)
                for (int offset = 0; offset < png.Length; offset += chunkSize)
                    await call.RequestStream.WriteAsync(new ProbeResultChunk
                    {
                        Capture = new ProbeCaptureChunk { MonitorIndex = monitor, Data = ByteString.CopyFrom(png, offset, Math.Min(chunkSize, png.Length - offset)) }
                    });
            await call.RequestStream.CompleteAsync();
            await call.ResponseAsync;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not upload probe result {ProbeId}", result.ProbeId);
        }
    }
```

- [ ] **Step 5: Client-side UI wiring**

In `MainWindowViewModel.cs`, in the connect path where `_service.SetD2DCrossScreenApplyDelegate(ApplyCrossScreenD2DFromRemoteAsync);` is called (inside `if (connected)`), add:
```csharp
                if (_service.Client != null)
                    _service.Client.TestProbeTargets = () => _remoteD2DServices.Values.Cast<WaBiBaBuSy.Core.Services.Testing.ITestProbeTarget>().ToList();
```
In `ApplyCrossScreenD2DFromRemoteAsync`, between `await Task.Delay(100);` (after `InitializeAsync`) and `await d2dService.StartAsync(…)`:
```csharp
        // Test mode (timecode / simulated skew) must reach a new player before its start command:
        // the skew shifts the player's clock, and the start is converted with the skewed offset.
        var testMode = _service.Client?.TestModeState ?? default;
        if (testMode.Timecode || testMode.ClockSkewMs != 0)
            await d2dService.SetTestModeAsync(testMode.Timecode, testMode.ClockSkewMs);
```

- [ ] **Step 6: Build + tests**

Run: `dotnet build WaBiBaBuSy.sln -c Debug` → 0 errors (`WallpaperSyncService` does not override `SubmitProbeResult` yet; the generated base returns `Unimplemented`, which is fine until Task 11). Run: `dotnet test WaBiBaBuSy.Tests` → green.

- [ ] **Step 7: Commit**

```bash
git add WaBiBaBuSy.Grpc/Protos WaBiBaBuSy.Models WaBiBaBuSy.Core WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs WaBiBaBuSy.Tests/PerfSamplerTests.cs
git commit -m "feat(test-mode): TEST_MODE/TEST_PROBE on the client, simulated clock skew, probe upload, perf sampling" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 11: Server — receive probe results, app version, `ServerTestChannel`

**Files:**
- Modify: `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` (`ConnectedClient.app_version = 14`)
- Modify: `WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs` (override `SubmitProbeResult`, event, `AppVersion` on register)
- Create: `WaBiBaBuSy.Core/Services/Testing/ITestTransport.cs`, `ServerTestChannel.cs`

**Interfaces:**
- Consumes: `WallpaperSyncService.SendCommandToClientAsync(string, SyncCommand) → Task<bool>`, `FetchClientLogsAsync` (Task 1).
- Produces: `WallpaperSyncService.ProbeResultReceived` event with `ProbeResultReceivedEventArgs { string ClientId; string ProbeId; string ResultJson; IReadOnlyDictionary<int, byte[]> Captures }` (namespace `WaBiBaBuSy.Grpc.Services`)
- Produces: `interface ITestTransport { Task<bool> SendTestModeAsync(string clientId, bool timecode, int clockSkewMs); Task<RemoteProbeResult?> ProbeAsync(string clientId, ProbeRequest request, TimeSpan timeout, CancellationToken ct); Task<string?> FetchLogsAsync(string clientId, long fromUtcMs, long toUtcMs, TimeSpan timeout); }`
- Produces: `ServerTestChannel(WallpaperSyncService sync) : ITestTransport, IDisposable`
- Produces: `ConnectedClient.AppVersion` (used by `TestHostAdapter` in Task 14).

- [ ] **Step 1: Proto + registration**

In `message ConnectedClient` after `prefetch_total = 13;`:
```proto
  string app_version = 14;          // from ClientInfo at registration (test-run preflight compares builds)
```
In `WallpaperSyncService.RegisterClient`, in the `new ConnectedClient { … }` initializer, add `AppVersion = request.AppVersion,`.

- [ ] **Step 2: Receive probe results**

In `WallpaperSyncService.cs` (inside the class, e.g. after the Remote Log Fetch region):
```csharp
    #region Automated test mode

    /// <summary>A client uploaded one probe result (header JSON + PNG bytes per monitor).</summary>
    public event EventHandler<ProbeResultReceivedEventArgs>? ProbeResultReceived;

    public override async Task<ProbeResultAck> SubmitProbeResult(
        IAsyncStreamReader<ProbeResultChunk> requestStream,
        ServerCallContext context)
    {
        ProbeResultHeader? header = null;
        var captures = new Dictionary<int, MemoryStream>();
        try
        {
            await foreach (var chunk in requestStream.ReadAllAsync(context.CancellationToken))
            {
                switch (chunk.PartCase)
                {
                    case ProbeResultChunk.PartOneofCase.Header:
                        header = chunk.Header;
                        break;
                    case ProbeResultChunk.PartOneofCase.Capture:
                        if (!captures.TryGetValue(chunk.Capture.MonitorIndex, out var buffer))
                            captures[chunk.Capture.MonitorIndex] = buffer = new MemoryStream();
                        chunk.Capture.Data.WriteTo(buffer);
                        break;
                }
            }
            if (header == null) return new ProbeResultAck { Success = false, Message = "missing header" };

            ProbeResultReceived?.Invoke(this, new ProbeResultReceivedEventArgs(
                header.ClientId, header.ProbeId, header.ResultJson,
                captures.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray())));
            return new ProbeResultAck { Success = true };
        }
        finally
        {
            foreach (var buffer in captures.Values) buffer.Dispose();
        }
    }

    #endregion
```
Next to `ClientLogsReceivedEventArgs` at the bottom of the file:
```csharp
public class ProbeResultReceivedEventArgs : EventArgs
{
    public string ClientId { get; }
    public string ProbeId { get; }
    public string ResultJson { get; }
    public IReadOnlyDictionary<int, byte[]> Captures { get; }

    public ProbeResultReceivedEventArgs(string clientId, string probeId, string resultJson, IReadOnlyDictionary<int, byte[]> captures)
    {
        ClientId = clientId;
        ProbeId = probeId;
        ResultJson = resultJson;
        Captures = captures;
    }
}
```

- [ ] **Step 3: Transport interface and the gRPC implementation**

`WaBiBaBuSy.Core/Services/Testing/ITestTransport.cs`:
```csharp
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>How the test runner reaches remote clients (gRPC in the app, a fake in tests).</summary>
public interface ITestTransport
{
    /// <summary>False when the client has no command stream.</summary>
    Task<bool> SendTestModeAsync(string clientId, bool timecode, int clockSkewMs);

    /// <summary>The client's result, or null when none arrived within <paramref name="timeout"/>.</summary>
    Task<RemoteProbeResult?> ProbeAsync(string clientId, ProbeRequest request, TimeSpan timeout, CancellationToken ct);

    /// <summary>Log lines between the two UTC instants, or null on timeout.</summary>
    Task<string?> FetchLogsAsync(string clientId, long fromUtcMs, long toUtcMs, TimeSpan timeout);
}
```

`WaBiBaBuSy.Core/Services/Testing/ServerTestChannel.cs`:
```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Grpc.Services;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary><see cref="ITestTransport"/> over the server's SyncStream + SubmitProbeResult RPC.</summary>
public sealed class ServerTestChannel : ITestTransport, IDisposable
{
    private readonly WallpaperSyncService _sync;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteProbeResult>> _waiters = new();

    public ServerTestChannel(WallpaperSyncService sync)
    {
        _sync = sync;
        _sync.ProbeResultReceived += OnProbeResultReceived;
    }

    public Task<bool> SendTestModeAsync(string clientId, bool timecode, int clockSkewMs) =>
        _sync.SendCommandToClientAsync(clientId, new SyncCommand
        {
            Type = CommandType.TestMode,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Params = new SyncParameters { TestTimecode = timecode, TestClockSkewMs = clockSkewMs },
        });

    public async Task<RemoteProbeResult?> ProbeAsync(string clientId, ProbeRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var key = Key(clientId, request.ProbeId);
        var waiter = new TaskCompletionSource<RemoteProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[key] = waiter;
        try
        {
            bool sent = await _sync.SendCommandToClientAsync(clientId, new SyncCommand
            {
                Type = CommandType.TestProbe,
                TimestampUtc = request.AtServerUtcMs,
                Params = new SyncParameters
                {
                    TestProbeId = request.ProbeId,
                    TestCapture = request.Capture,
                    TestHasExactElapsed = request.ExactElapsedMs.HasValue,
                    TestExactElapsedMs = request.ExactElapsedMs ?? 0,
                },
            });
            if (!sent)
                return new RemoteProbeResult { ClientId = clientId, ProbeId = request.ProbeId, Error = "client has no command stream" };

            var finished = await Task.WhenAny(waiter.Task, Task.Delay(timeout, ct));
            return finished == waiter.Task ? await waiter.Task : null;
        }
        finally
        {
            _waiters.TryRemove(key, out _);
        }
    }

    public async Task<string?> FetchLogsAsync(string clientId, long fromUtcMs, long toUtcMs, TimeSpan timeout) =>
        (await _sync.FetchClientLogsAsync(clientId, fromUtcMs, toUtcMs, timeout))?.LogContent;

    private void OnProbeResultReceived(object? sender, ProbeResultReceivedEventArgs e)
    {
        RemoteProbeResult result;
        try
        {
            result = JsonSerializer.Deserialize<RemoteProbeResult>(e.ResultJson) ?? new RemoteProbeResult();
        }
        catch (JsonException ex)
        {
            result = new RemoteProbeResult { Error = $"malformed probe result: {ex.Message}" };
        }
        result.ClientId = e.ClientId;
        result.ProbeId = e.ProbeId;
        result.Captures = new Dictionary<int, byte[]>(e.Captures);
        if (_waiters.TryGetValue(Key(e.ClientId, e.ProbeId), out var waiter))
            waiter.TrySetResult(result);
    }

    private static string Key(string clientId, string probeId) => $"{clientId}|{probeId}";

    public void Dispose() => _sync.ProbeResultReceived -= OnProbeResultReceived;
}
```

- [ ] **Step 4: Build + tests** → `dotnet build WaBiBaBuSy.sln -c Debug` 0 errors; `dotnet test WaBiBaBuSy.Tests` green.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Grpc WaBiBaBuSy.Core/Services/Testing
git commit -m "feat(test-mode): server receives probe results; ServerTestChannel transport; client app version in topology" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 12: TestRunner, report model, JSON report

**Files:**
- Create: `WaBiBaBuSy.Models/Testing/TestRunReport.cs`
- Create: `WaBiBaBuSy.Core/Services/Testing/ITestHost.cs`, `TestRunnerOptions.cs`, `TestRunner.cs`, `PngPixels.cs`, `TestReportWriter.cs` (JSON part; HTML in Task 13)
- Create: `WaBiBaBuSy.Tests/TestRunnerTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 3–6, 9 (`ITestProbeTarget`, `ProbeFanOut`), 10 (`PerfSampler`, `TestModeErrors`), 11 (`ITestTransport`); `LogTail` (Task 1); `VersionInfo.AppVersion` (`WaBiBaBuSy.Common.Version`); `SeatMapLayoutResult.Get(string)`.
- Produces (Models, `WaBiBaBuSy.Models.Testing`): `TestRunReport`, `RunEnvironment`, `TestNodeInfo`, `StepReport`, `ProbeReport`, `PixelParityResult` (fields in code below).
- Produces (Core, `WaBiBaBuSy.Core.Services.Testing`):
  - `interface ITestHost { Task<IReadOnlyList<TestNodeInfo>> GetNodesAsync(); Task<SceneStartInfo> PlaySceneAsync(CrossScreenConfig scene, IReadOnlyList<string> targetNodeIds, CancellationToken ct); Task StopAllAsync(); CrossScreenConfig? CurrentScene { get; } Task RestoreSceneAsync(CrossScreenConfig? scene); Task<bool> PrefetchAsync(IReadOnlyList<CrossScreenConfig> scenes, TimeSpan timeout, CancellationToken ct); IReadOnlyList<ITestProbeTarget> LocalProbeTargets(); void ReportProgress(string text); }`
  - `SceneStartInfo { long SharedStartServerUtcMs; int StartLeadMs; SeatMapLayoutResult? Layout; bool PerMonitor; MovementConfig EffectiveMovement }`
  - `TestRunnerOptions { int MinLeadMs = 500; int ProbeGraceMs = 2000; int UploadGraceMs = 3000; TimeSpan PrefetchTimeout = 120 s; TimeSpan LogFetchTimeout = 10 s; int DefaultStepTimeoutMs = 30000; string ResultsRoot; string? LocalLogDirectory; Func<long> NowUtcMs }`
  - `TestRunStatus { bool Running; string Scenario; int StepIndex; int StepCount; string StepLabel; string? ResultsDirectory; Verdict? LastVerdict }` (record)
  - `TestRunner(ITestHost host, ITestTransport transport, ILogger<TestRunner> logger, TestRunnerOptions? options = null)`, `.Status`, `.RunAsync(TestScenario scenario, string scenarioPath, CancellationToken ct) → Task<TestRunReport>`
  - `PngPixels.Decode(string path) → PngPixels.Image(byte[] Pixels, int Width, int Height, int Stride)`; `PngPixels.Encode(byte[] bgra, int width, int height, int stride, string path)`
  - `TestReportWriter.JsonOptions`; `TestReportWriter.Write(TestRunReport report)` (writes `report.json`; Task 13 adds `report.html` to the same method)

**Runner flow (spec §6):** preflight → load scenes → prefetch → steps → finally { test mode off everywhere, restore the previous scene, collect logs, write the report }. The `finally` also runs on cancellation and on an aborted preflight.

- [ ] **Step 1: Report model**

`WaBiBaBuSy.Models/Testing/TestRunReport.cs`:
```csharp
namespace WaBiBaBuSy.Models.Testing;

/// <summary>Everything one test run measured; serialized as report.json.</summary>
public sealed class TestRunReport
{
    public string Scenario { get; set; } = string.Empty;
    public string ScenarioPath { get; set; } = string.Empty;
    public string ResultsDirectory { get; set; } = string.Empty;
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }
    public bool Aborted { get; set; }
    public string? AbortReason { get; set; }
    public Verdict Verdict { get; set; } = Verdict.Skipped;
    public long? PrefetchMs { get; set; }
    public RunEnvironment Environment { get; set; } = new();
    public ScenarioThresholds Thresholds { get; set; } = new();
    public List<StepReport> Steps { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    /// <summary>Collected logs, relative to the results folder.</summary>
    public List<string> LogFiles { get; set; } = new();
    public string ClockNote { get; set; } =
        "Probe times are converted with each client's clock-offset estimate, so an error in that estimate itself is invisible here; it is bounded by RTT/2 per node (clockBoundMs).";
}

public sealed class RunEnvironment
{
    public string AppVersion { get; set; } = string.Empty;
    public string? Commit { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public List<TestNodeInfo> Nodes { get; set; } = new();
}

/// <summary>A node as the run saw it at preflight (seat order).</summary>
public sealed class TestNodeInfo
{
    public string NodeId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsLocal { get; set; }
    public bool Connected { get; set; } = true;
    public int MonitorIndex { get; set; }
    public int SeatOrder { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshHz { get; set; }
    public double ClockOffsetMs { get; set; }
    public double RttMs { get; set; }
    public string AppVersion { get; set; } = string.Empty;
}

public sealed class StepReport
{
    public int Index { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Scene { get; set; }
    public Verdict Verdict { get; set; } = Verdict.Skipped;
    public string Message { get; set; } = string.Empty;
    public long StartedUtcMs { get; set; }
    public long DurationMs { get; set; }
    public List<ProbeReport> Probes { get; set; } = new();
}

public sealed class ProbeReport
{
    public string ProbeId { get; set; } = string.Empty;
    public long AtServerUtcMs { get; set; }
    public bool Exact { get; set; }
    public Verdict Verdict { get; set; } = Verdict.Skipped;
    public List<NodeProbeSample> Samples { get; set; } = new();
    public DriftResult? Drift { get; set; }
    public List<PositionCheckResult> Positions { get; set; } = new();
    public List<PixelParityResult> Parity { get; set; } = new();
    public List<string> PerfViolations { get; set; } = new();
}

public sealed class PixelParityResult
{
    public string NodeId { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public string ReferenceNodeName { get; set; } = string.Empty;
    public Verdict Verdict { get; set; }
    public double DiffPct { get; set; }
    /// <summary>Relative to the results folder; null when identical.</summary>
    public string? DiffImagePath { get; set; }
    public string Message { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Host seam, options, PNG helper, JSON writer**

`WaBiBaBuSy.Core/Services/Testing/ITestHost.cs`:
```csharp
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>What the runner needs from the server app (implemented by the UI; faked in tests).</summary>
public interface ITestHost
{
    /// <summary>Every node in seat order: the server's monitors and the remote clients, connected or not.</summary>
    Task<IReadOnlyList<TestNodeInfo>> GetNodesAsync();

    /// <summary>Play <paramref name="scene"/> on the node ids (empty = all), like ▶ Play; returns once local players started.</summary>
    Task<SceneStartInfo> PlaySceneAsync(CrossScreenConfig scene, IReadOnlyList<string> targetNodeIds, CancellationToken ct);

    Task StopAllAsync();

    /// <summary>What was playing before the run (restored afterwards); null = nothing.</summary>
    CrossScreenConfig? CurrentScene { get; }

    /// <summary>Play <paramref name="scene"/> on all nodes again, or stop everything when null.</summary>
    Task RestoreSceneAsync(CrossScreenConfig? scene);

    /// <summary>Cache every asset of the scenes on all remote nodes; true when all report cached in time.</summary>
    Task<bool> PrefetchAsync(IReadOnlyList<CrossScreenConfig> scenes, TimeSpan timeout, CancellationToken ct);

    /// <summary>The server's own running players (one per local monitor that plays).</summary>
    IReadOnlyList<ITestProbeTarget> LocalProbeTargets();

    /// <summary>One-line progress for the toolbar.</summary>
    void ReportProgress(string text);
}

/// <summary>What a started scene means for the checks.</summary>
public sealed class SceneStartInfo
{
    public long SharedStartServerUtcMs { get; set; }
    public int StartLeadMs { get; set; }
    /// <summary>Seat layout of the run (Sequential); null when unknown.</summary>
    public SeatMapLayoutResult? Layout { get; set; }
    /// <summary>Simultaneous mode: every node's own monitor is its canvas.</summary>
    public bool PerMonitor { get; set; }
    /// <summary>Movement after cm→px resolution (what the players received).</summary>
    public MovementConfig EffectiveMovement { get; set; } = new();
}
```

`WaBiBaBuSy.Core/Services/Testing/TestRunnerOptions.cs`:
```csharp
using WaBiBaBuSy.Models.Configuration;

namespace WaBiBaBuSy.Core.Services.Testing;

public sealed class TestRunnerOptions
{
    /// <summary>A probe instant is never closer than this to "now" (command must reach every node first).</summary>
    public int MinLeadMs { get; set; } = 500;
    /// <summary>A node that has not answered this long after the instant is missing.</summary>
    public int ProbeGraceMs { get; set; } = 2000;
    /// <summary>Extra time for a remote's PNG upload.</summary>
    public int UploadGraceMs { get; set; } = 3000;
    public TimeSpan PrefetchTimeout { get; set; } = TimeSpan.FromSeconds(120);
    public TimeSpan LogFetchTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int DefaultStepTimeoutMs { get; set; } = 30000;
    public string ResultsRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaBiBaBuSy", "TestRuns");
    /// <summary>This machine's log folder; null = from the logging configuration.</summary>
    public string? LocalLogDirectory { get; set; }
    public Func<long> NowUtcMs { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal string ResolveLocalLogDirectory() =>
        LocalLogDirectory ?? ConfigurationManager.LoadLoggingConfiguration().LogDirectory;
}
```

`WaBiBaBuSy.Core/Services/Testing/PngPixels.cs`:
```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>PNG ↔ BGRA8 buffers for the image checks (System.Drawing, Windows only).</summary>
public static class PngPixels
{
    public sealed record Image(byte[] Pixels, int Width, int Height, int Stride);

    public static Image Decode(string path)
    {
        using var bmp = new Bitmap(path);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = bmp.Width * 4;
            var pixels = new byte[stride * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
            return new Image(pixels, bmp.Width, bmp.Height, stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public static void Encode(byte[] bgra, int width, int height, int stride, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var bmp = new Bitmap(width, height, stride, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            bmp.Save(path, ImageFormat.Png);
        }
        finally
        {
            handle.Free();
        }
    }
}
```

`WaBiBaBuSy.Core/Services/Testing/TestReportWriter.cs` (JSON now, HTML in Task 13):
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>Writes report.json (and, from Task 13, report.html) into the run's results folder.</summary>
public static class TestReportWriter
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Write(TestRunReport report)
    {
        Directory.CreateDirectory(report.ResultsDirectory);
        File.WriteAllText(Path.Combine(report.ResultsDirectory, "report.json"), JsonSerializer.Serialize(report, JsonOptions));
    }
}
```

- [ ] **Step 3: Write the failing runner tests**

`WaBiBaBuSy.Tests/TestRunnerTests.cs`:
```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class TestRunnerTests : IDisposable
{
    private const int W = 400, H = 300;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-runner-{Guid.NewGuid():N}");
    private readonly FakeHost _host = new();
    private readonly FakeTransport _transport = new();

    public TestRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "scenes"));
        File.WriteAllBytes(Path.Combine(_dir, "scenes", "marker.png"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(_dir, "scenes", "static.json"),
            """{ "Animation": { "AnimationPath": "marker.png", "TargetHeight": 64 }, "Movement": { "Type": "Static" }, "DistributionMode": "Simultaneous" }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private TestRunner Runner() => new(_host, _transport, NullLogger<TestRunner>.Instance, new TestRunnerOptions
    {
        MinLeadMs = 20, ProbeGraceMs = 500, UploadGraceMs = 200,
        ResultsRoot = Path.Combine(_dir, "results"), LocalLogDirectory = Path.Combine(_dir, "no-logs"),
    });

    private TestScenario Scenario(string steps, int minRemotes = 1) => ScenarioLoader.Parse(
        $$"""{ "name": "unit", "requires": { "minRemoteNodes": {{minRemotes}} }, "steps": [ {{steps}} ] }""", _dir);

    private const string PlayMarker = """{ "type": "playScene", "scene": "scenes/static.json", "marker": true }""";

    [Fact]
    public async Task HealthyWall_Passes_AndWritesEverything()
    {
        var report = await Runner().RunAsync(Scenario($$"""
            { "type": "testMode", "timecode": true, "simulatedClockSkewMs": { "pc-02": 2000 } },
            {{PlayMarker}},
            { "type": "probe", "label": "first", "at": "now+30ms", "capture": true },
            { "type": "probeSeries", "label": "series", "everyMs": 40, "forMs": 120, "perf": false },
            { "type": "stop" }
            """), "unit.json", CancellationToken.None);

        Assert.False(report.Aborted, report.AbortReason);
        Assert.Equal(Verdict.Pass, report.Verdict);
        Assert.Equal(5, report.Steps.Count);
        Assert.Equal(3, report.Steps[3].Probes.Count);

        var first = report.Steps[2].Probes[0];
        Assert.Equal(2, first.Samples.Count);                       // server monitor + pc-02
        Assert.Equal(Verdict.Pass, first.Drift!.Verdict);
        var local = first.Positions.Single(p => p.NodeName == "server #0");
        Assert.Equal(Verdict.Pass, local.Verdict);                  // marker drawn where the math says
        Assert.True(File.Exists(Path.Combine(report.ResultsDirectory, first.Samples[0].CapturePath!)));

        Assert.True(File.Exists(Path.Combine(report.ResultsDirectory, "report.json")));
        Assert.True(File.Exists(Path.Combine(report.ResultsDirectory, "logs", "pc-02.log")));
        Assert.Equal((true, 2000), _transport.Modes[0]);            // skew only for the named node
        Assert.Equal((false, 0), _transport.Modes[^1]);             // switched off at the end
        Assert.True(_host.Restored);

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(report.ResultsDirectory, "report.json")));
        Assert.Equal("pass", json.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task LaggingRemote_FailsDrift()
    {
        _transport.ContentLagMs = 80;
        var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "probe", "label": "p", "at": "now+30ms", "capture": false }"""),
            "unit.json", CancellationToken.None);
        Assert.Equal(Verdict.Fail, report.Steps[1].Verdict);
        Assert.Equal(80, report.Steps[1].Probes[0].Drift!.SpreadMs, 0);
        Assert.Equal(Verdict.Fail, report.Verdict);
    }

    [Fact]
    public async Task RemoteNeverReplies_ProbeMarkedMissing()
    {
        _transport.Silent = true;
        var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "probe", "label": "p", "at": "now+30ms", "capture": false }"""),
            "unit.json", CancellationToken.None);
        var probe = report.Steps[1].Probes[0];
        Assert.True(probe.Samples.Single(s => s.NodeName == "pc-02").Missing);
        Assert.Equal(Verdict.Warn, probe.Verdict);
    }

    [Fact]
    public async Task TooFewRemotes_AbortsBeforePlaying_ButWritesReport()
    {
        var report = await Runner().RunAsync(Scenario(PlayMarker, minRemotes: 2), "unit.json", CancellationToken.None);
        Assert.True(report.Aborted);
        Assert.Contains("needs 2 connected remote node(s), found 1", report.AbortReason);
        Assert.Equal(0, _host.PlayCount);
        Assert.True(File.Exists(Path.Combine(report.ResultsDirectory, "report.json")));
    }

    [Fact]
    public async Task ClientWithTestRunsDisabled_AbortsAtPreflight()
    {
        _transport.Error = TestModeErrors.Disabled;
        var report = await Runner().RunAsync(Scenario(PlayMarker), "unit.json", CancellationToken.None);
        Assert.True(report.Aborted);
        Assert.Contains("pc-02", report.AbortReason);
        Assert.Equal(0, _host.PlayCount);
    }

    [Fact]
    public async Task Cancel_WritesPartialReport_AndTurnsTestModeOff()
    {
        using var cts = new CancellationTokenSource();
        var run = Runner().RunAsync(Scenario($$"""
            { "type": "testMode", "timecode": true },
            {{PlayMarker}},
            { "type": "wait", "ms": 10000 }
            """), "unit.json", cts.Token);
        await Task.Delay(300);
        cts.Cancel();
        var report = await run;

        Assert.True(report.Aborted);
        Assert.Equal((false, 0), _transport.Modes[^1]);
        Assert.Equal<(bool, int)?>((false, 0), _host.Target(0).LastMode);
        Assert.True(_host.Restored);
        Assert.True(File.Exists(Path.Combine(report.ResultsDirectory, "report.json")));
    }

    [Fact]
    public async Task MarkerMisplaced_FailsPositionCheck()
    {
        _host.Target(0).MarkerOffsetX = 20;
        var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "probe", "label": "p", "at": "now+30ms", "capture": true }"""),
            "unit.json", CancellationToken.None);
        var pos = report.Steps[1].Probes[0].Positions.Single(p => p.NodeName == "server #0");
        Assert.Equal(Verdict.Fail, pos.Verdict);
        Assert.Equal(20, pos.ErrorPx, 0);
    }

    [Fact]
    public async Task ExactFrame_ComparesSameResolutionNodes()
    {
        _host.AddLocalMonitor(1);
        var passing = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "exactFrame", "label": "px", "elapsedMs": 5000 }"""),
            "unit.json", CancellationToken.None);
        Assert.Equal(Verdict.Pass, passing.Steps[1].Verdict);
        Assert.NotEmpty(passing.Steps[1].Probes[0].Parity);

        _host.Target(1).MarkerOffsetX = 5;   // monitor 1 draws a different frame
        var failing = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "exactFrame", "label": "px", "elapsedMs": 5000 }"""),
            "unit.json", CancellationToken.None);
        var parity = failing.Steps[1].Probes[0].Parity.Single();
        Assert.Equal(Verdict.Fail, parity.Verdict);
        Assert.NotNull(parity.DiffImagePath);
    }

    // ── fakes ──────────────────────────────────────────────────────────────

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class FakeTarget : ITestProbeTarget
    {
        public int MonitorIndex { get; init; }
        public long Start { get; set; }
        public int MarkerOffsetX { get; set; }
        public (bool, int)? LastMode { get; private set; }

        public Task SetTestModeAsync(bool timecode, int clockSkewMs) { LastMode = (timecode, clockSkewMs); return Task.CompletedTask; }

        public async Task<PlayerProbeReply?> ProbeAsync(PlayerProbeRequest request, TimeSpan timeout, CancellationToken ct)
        {
            var wait = request.AtLocalUtcMs - Now();
            if (wait > 0) await Task.Delay((int)wait, ct);
            long render = Now();
            long elapsed = request.ExactElapsedMs ?? render - Start;
            string? path = null;
            if (request.Capture)
            {
                path = Path.Combine(request.CaptureDirectory, $"{request.ProbeId}-{MonitorIndex}.png");
                PngPixels.Encode(DrawMarker(168 + MarkerOffsetX, 118), W, H, W * 4, path);   // static marker, centered
            }
            return new PlayerProbeReply
            {
                ProbeId = request.ProbeId, Exact = request.ExactElapsedMs != null,
                RenderedElapsedMs = elapsed, RenderLocalUtcMs = render, PresentLocalUtcMs = render + 16,
                AnimX = 168, AnimY = 118, AnimWidth = 64, AnimHeight = 64, Width = W, Height = H, CapturePath = path,
            };
        }
    }

    private static byte[] DrawMarker(int x0, int y0)
    {
        var px = new byte[W * H * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 30; px[i + 1] = 30; px[i + 2] = 30; px[i + 3] = 255; }
        void Fill(int x, int y, int w, int h, byte r, byte g, byte b)
        {
            for (int yy = y; yy < y + h; yy++)
                for (int xx = x; xx < x + w; xx++)
                {
                    int i = (yy * W + xx) * 4;
                    px[i] = b; px[i + 1] = g; px[i + 2] = r;
                }
        }
        Fill(x0, y0, 64, 64, 0, 0, 0);
        Fill(x0 + 4, y0 + 4, 56, 56, 255, 0, 255);
        Fill(x0 + 40, y0 + 26, 12, 12, 255, 255, 255);
        return px;
    }

    private sealed class FakeHost : ITestHost
    {
        private readonly List<FakeTarget> _targets = new() { new FakeTarget { MonitorIndex = 0 } };
        private readonly List<TestNodeInfo> _nodes = new()
        {
            new() { NodeId = "SERVER_LOCALHOST_MONITOR_0", Name = "server #0", IsLocal = true, MonitorIndex = 0, Width = W, Height = H, SeatOrder = 0 },
            new() { NodeId = "client-1", Name = "pc-02", IsLocal = false, Width = W, Height = H, SeatOrder = 1 },
        };
        public int PlayCount { get; private set; }
        public bool Restored { get; private set; }
        public FakeTarget Target(int monitor) => _targets.Single(t => t.MonitorIndex == monitor);

        public void AddLocalMonitor(int index)
        {
            _targets.Add(new FakeTarget { MonitorIndex = index });
            _nodes.Insert(index, new TestNodeInfo { NodeId = $"SERVER_LOCALHOST_MONITOR_{index}", Name = $"server #{index}", IsLocal = true, MonitorIndex = index, Width = W, Height = H });
        }

        public Task<IReadOnlyList<TestNodeInfo>> GetNodesAsync() => Task.FromResult<IReadOnlyList<TestNodeInfo>>(_nodes);

        public Task<SceneStartInfo> PlaySceneAsync(CrossScreenConfig scene, IReadOnlyList<string> targetNodeIds, CancellationToken ct)
        {
            PlayCount++;
            long start = Now() + 10;
            foreach (var t in _targets) t.Start = start;
            FakeTransport.SharedStart = start;
            return Task.FromResult(new SceneStartInfo
            {
                SharedStartServerUtcMs = start,
                PerMonitor = scene.DistributionMode == AnimationDistributionMode.Simultaneous,
                EffectiveMovement = scene.Movement,
            });
        }

        public Task StopAllAsync() => Task.CompletedTask;
        public CrossScreenConfig? CurrentScene => null;
        public Task RestoreSceneAsync(CrossScreenConfig? scene) { Restored = true; return Task.CompletedTask; }
        public Task<bool> PrefetchAsync(IReadOnlyList<CrossScreenConfig> scenes, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public IReadOnlyList<ITestProbeTarget> LocalProbeTargets() => _targets;
        public void ReportProgress(string text) { }
    }

    private sealed class FakeTransport : ITestTransport
    {
        public static long SharedStart;
        public List<(bool, int)> Modes { get; } = new();
        public long ContentLagMs { get; set; }
        public bool Silent { get; set; }
        public string? Error { get; set; }

        public Task<bool> SendTestModeAsync(string clientId, bool timecode, int clockSkewMs)
        {
            Modes.Add((timecode, clockSkewMs));
            return Task.FromResult(true);
        }

        public Task<RemoteProbeResult?> ProbeAsync(string clientId, ProbeRequest request, TimeSpan timeout, CancellationToken ct)
        {
            if (Silent) return Task.FromResult<RemoteProbeResult?>(null);
            if (Error != null) return Task.FromResult<RemoteProbeResult?>(new RemoteProbeResult { ClientId = clientId, ProbeId = request.ProbeId, Error = Error });
            // Client clock 1000 ms behind the server; the offset (server − client) is +1000.
            long renderLocal = request.AtServerUtcMs - 1000;
            return Task.FromResult<RemoteProbeResult?>(new RemoteProbeResult
            {
                ClientId = clientId, ProbeId = request.ProbeId, ClockOffsetMs = 1000, RttMs = 2,
                Replies =
                {
                    new PlayerProbeReply
                    {
                        ProbeId = request.ProbeId, MonitorIndex = 0,
                        RenderedElapsedMs = request.ExactElapsedMs ?? request.AtServerUtcMs - SharedStart - ContentLagMs,
                        RenderLocalUtcMs = renderLocal, PresentLocalUtcMs = renderLocal + 16,
                        AnimX = 168, AnimY = 118, AnimWidth = 64, AnimHeight = 64, Width = W, Height = H,
                    },
                },
            });
        }

        public Task<string?> FetchLogsAsync(string clientId, long fromUtcMs, long toUtcMs, TimeSpan timeout) =>
            Task.FromResult<string?>("[12:00:00.000 INF] remote log");
    }
}
```
Perf is off in the healthy-wall test on purpose: the test host's own CPU/memory would trip the MVP thresholds. `PerfSamplerTests` covers sampling and `EvaluatePerf` is plain comparisons. Notes on the fakes: the remote reply is `RenderedElapsedMs = at − start` rendered at `at` (server time), and the local fake renders at `max(now, at)` with `elapsed = now − start`. Both have error −16 → spread ≈ 0 (± the scheduler's wake-up jitter, far below 25 ms). `ClientWithTestRunsDisabled` returns the error on the preflight probe. `ExactFrame` compares server #0 against server #1 (same resolution); pc-02 sends no capture, so it is left out of parity.

- [ ] **Step 4: Run to verify failure** → `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~TestRunnerTests` → build error (`TestRunner` missing).

- [ ] **Step 5: Implement `TestRunner`**

`WaBiBaBuSy.Core/Services/Testing/TestRunner.cs`:
```csharp
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Common.IO;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using AppVersionInfo = WaBiBaBuSy.Common.Version.VersionInfo;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>Progress of the active (or last) run; read by the toolbar and GET /test/status.</summary>
public sealed record TestRunStatus(bool Running, string Scenario, int StepIndex, int StepCount, string StepLabel, string? ResultsDirectory, Verdict? LastVerdict)
{
    public static TestRunStatus Idle { get; } = new(false, string.Empty, 0, 0, string.Empty, null, null);
}

/// <summary>Executes a <see cref="TestScenario"/> (automated test mode design §6).</summary>
public sealed class TestRunner
{
    private readonly ITestHost _host;
    private readonly ITestTransport _transport;
    private readonly ILogger<TestRunner> _logger;
    private readonly TestRunnerOptions _o;
    private readonly PerfSampler _perf = new();
    private volatile TestRunStatus _status = TestRunStatus.Idle;

    public TestRunner(ITestHost host, ITestTransport transport, ILogger<TestRunner> logger, TestRunnerOptions? options = null)
    {
        _host = host;
        _transport = transport;
        _logger = logger;
        _o = options ?? new TestRunnerOptions();
    }

    public TestRunStatus Status => _status;

    private sealed class RunContext
    {
        public required TestScenario Scenario { get; init; }
        public required TestRunReport Report { get; init; }
        public string Dir => Report.ResultsDirectory;
        public List<TestNodeInfo> Nodes { get; set; } = new();
        public List<TestNodeInfo> Remotes { get; set; } = new();
        public Dictionary<int, CrossScreenConfig> Scenes { get; } = new();   // step index → scene
        public bool Timecode { get; set; }
        public TestModeStep? Mode { get; set; }
        public SceneStartInfo? Active { get; set; }
        public CrossScreenConfig? ActiveScene { get; set; }
        public HashSet<string>? ActiveTargets { get; set; }                  // null = all nodes
        public bool Marker { get; set; }
        public bool ScenePlayed { get; set; }
        public int ProbeSeq { get; set; }
        public bool IsTarget(string nodeId) => ActiveTargets == null || ActiveTargets.Contains(nodeId);
    }

    /// <summary>Run the scenario; always returns a report (partial on cancel / abort) and writes it to disk.</summary>
    public async Task<TestRunReport> RunAsync(TestScenario scenario, string scenarioPath, CancellationToken ct)
    {
        long startedMs = _o.NowUtcMs();
        var report = new TestRunReport
        {
            Scenario = scenario.Name,
            ScenarioPath = scenarioPath,
            ResultsDirectory = CreateResultsDirectory(scenario.Name),
            StartedUtc = DateTimeOffset.FromUnixTimeMilliseconds(startedMs),
            Thresholds = scenario.Thresholds,
        };
        var ctx = new RunContext { Scenario = scenario, Report = report };
        var previousScene = _host.CurrentScene;
        SetStatus(scenario, 0, "preflight", report);

        try
        {
            if (await PreflightAsync(ctx, ct))
            {
                LoadScenes(ctx);
                await PrefetchAsync(ctx, ct);
                for (int i = 0; i < scenario.Steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = scenario.Steps[i];
                    var sr = new StepReport { Index = i + 1, Kind = step.Kind, Label = step.Label, StartedUtcMs = _o.NowUtcMs() };
                    report.Steps.Add(sr);
                    SetStatus(scenario, i + 1, step.Label ?? step.Kind, report);
                    _host.ReportProgress($"Test: step {i + 1}/{scenario.Steps.Count} · {step.Label ?? step.Kind}");

                    int timeoutMs = StepTimeoutMs(step);
                    using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stepCts.CancelAfter(timeoutMs);
                    try
                    {
                        await RunStepAsync(ctx, i, step, sr, stepCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        sr.Verdict = Verdict.Fail;
                        sr.Message = $"timed out after {timeoutMs} ms";
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Test step {Index} ({Kind}) failed", i + 1, step.Kind);
                        sr.Verdict = Verdict.Fail;
                        sr.Message = ex.Message;
                    }
                    sr.DurationMs = _o.NowUtcMs() - sr.StartedUtcMs;
                }
            }
        }
        catch (OperationCanceledException)
        {
            report.Aborted = true;
            report.AbortReason = "cancelled";
        }
        catch (ScenarioException ex)
        {
            report.Aborted = true;
            report.AbortReason = ex.Message;
        }
        finally
        {
            await CleanupAsync(ctx, previousScene);
            await CollectLogsAsync(ctx, startedMs);
            report.FinishedUtc = DateTimeOffset.FromUnixTimeMilliseconds(_o.NowUtcMs());
            report.Verdict = report.Steps.Aggregate(Verdict.Skipped, (v, s) => Verdicts.Worst(v, s.Verdict));
            if (report.Aborted) report.Verdict = Verdicts.Worst(report.Verdict, Verdict.Warn);
            try { TestReportWriter.Write(report); }
            catch (Exception ex) { _logger.LogError(ex, "Could not write the test report to {Dir}", report.ResultsDirectory); }
            try { Directory.Delete(Path.Combine(report.ResultsDirectory, "tmp"), recursive: true); } catch { /* may not exist */ }
            _status = _status with { Running = false, ResultsDirectory = report.ResultsDirectory, LastVerdict = report.Verdict };
            _host.ReportProgress($"Test: {report.Verdict.ToString().ToLowerInvariant()}{(report.Aborted ? " (aborted)" : "")} · {report.ResultsDirectory}");
        }
        return report;
    }

    // ── preflight, scenes, prefetch ─────────────────────────────────────────

    private async Task<bool> PreflightAsync(RunContext ctx, CancellationToken ct)
    {
        var report = ctx.Report;
        ctx.Nodes = (await _host.GetNodesAsync()).ToList();
        ctx.Remotes = ctx.Nodes.Where(n => !n.IsLocal && n.Connected).ToList();
        report.Environment = new RunEnvironment
        {
            AppVersion = AppVersionInfo.AppVersion,
            Commit = CommitHash(),
            MachineName = Environment.MachineName,
            Nodes = ctx.Nodes,
        };

        foreach (var n in ctx.Nodes.Where(n => !n.IsLocal && !n.Connected))
            report.Warnings.Add($"{n.Name} is not connected and is left out");
        foreach (var n in ctx.Remotes.Where(n => n.AppVersion.Length > 0 && n.AppVersion != AppVersionInfo.AppVersion))
            report.Warnings.Add($"{n.Name} runs {n.AppVersion}, the server runs {AppVersionInfo.AppVersion}");

        int required = ctx.Scenario.Requires.MinRemoteNodes;
        if (ctx.Remotes.Count < required)
        {
            report.Aborted = true;
            report.AbortReason = $"needs {required} connected remote node(s), found {ctx.Remotes.Count}";
            return false;
        }

        // A probe with no scene playing: proves the round trip and reveals "Allow test runs" = off.
        var probe = new ProbeRequest { ProbeId = "preflight", AtServerUtcMs = _o.NowUtcMs() + _o.MinLeadMs };
        var timeout = TimeSpan.FromMilliseconds(_o.MinLeadMs + _o.ProbeGraceMs);
        var results = await Task.WhenAll(ctx.Remotes.Select(n => _transport.ProbeAsync(n.NodeId, probe, timeout, ct)));
        var disabled = ctx.Remotes.Where((n, i) => results[i]?.Error == TestModeErrors.Disabled).Select(n => n.Name).ToList();
        if (disabled.Count > 0)
        {
            report.Aborted = true;
            report.AbortReason = $"test runs are disabled on: {string.Join(", ", disabled)} (Settings → Client → Allow test runs)";
            return false;
        }
        for (int i = 0; i < ctx.Remotes.Count; i++)
            if (results[i] == null)
                report.Warnings.Add($"{ctx.Remotes[i].Name} did not answer the preflight probe (older build or no command stream?)");
        return true;
    }

    private static void LoadScenes(RunContext ctx)
    {
        for (int i = 0; i < ctx.Scenario.Steps.Count; i++)
            if (ctx.Scenario.Steps[i] is PlaySceneStep play)
                ctx.Scenes[i] = SceneFile.Load(ScenarioLoader.ResolvePath(ctx.Scenario, play.Scene));
    }

    private async Task PrefetchAsync(RunContext ctx, CancellationToken ct)
    {
        if (ctx.Remotes.Count == 0 || ctx.Scenes.Count == 0) return;
        _host.ReportProgress("Test: caching scene assets on all nodes…");
        var sw = Stopwatch.StartNew();
        bool ok = await _host.PrefetchAsync(ctx.Scenes.Values.ToList(), _o.PrefetchTimeout, ct);
        ctx.Report.PrefetchMs = sw.ElapsedMilliseconds;
        if (!ok) ctx.Report.Warnings.Add($"prefetch did not complete within {_o.PrefetchTimeout.TotalSeconds:F0} s; first scenes may start late on some nodes");
    }

    // ── steps ───────────────────────────────────────────────────────────────

    private async Task RunStepAsync(RunContext ctx, int index, TestStep step, StepReport sr, CancellationToken ct)
    {
        var t = ctx.Scenario.Thresholds;
        switch (step)
        {
            case TestModeStep mode:
                ctx.Mode = mode;
                ctx.Timecode = mode.Timecode;
                var failed = new List<string>();
                foreach (var n in ctx.Remotes)
                    if (!await _transport.SendTestModeAsync(n.NodeId, mode.Timecode, SkewFor(mode, n)))
                        failed.Add(n.Name);
                await ProbeFanOut.SetTestModeAllAsync(_host.LocalProbeTargets(), mode.Timecode, 0);
                var skews = ctx.Remotes.Where(n => SkewFor(mode, n) != 0).Select(n => $"{n.Name} {SkewFor(mode, n):+#;-#} ms");
                sr.Verdict = failed.Count == 0 ? Verdict.Pass : Verdict.Warn;
                sr.Message = $"timecode {(mode.Timecode ? "on" : "off")}"
                    + (skews.Any() ? $", simulated skew: {string.Join(", ", skews)}" : "")
                    + (failed.Count > 0 ? $"; not reached: {string.Join(", ", failed)}" : "");
                break;

            case PlaySceneStep play:
                var scene = ctx.Scenes[index];
                var targets = ResolveTargets(play.Targets, ctx.Nodes);
                if (targets is { Count: 0 })
                {
                    sr.Verdict = Verdict.Fail;
                    sr.Message = $"no node matches targets \"{play.Targets}\"";
                    break;
                }
                ctx.Active = await _host.PlaySceneAsync(scene, targets ?? new List<string>(), ct);
                ctx.ActiveScene = scene;
                ctx.ActiveTargets = targets?.ToHashSet();
                ctx.Marker = play.Marker;
                ctx.ScenePlayed = true;
                // New local players start without test mode (remote clients apply it before their start).
                await ProbeFanOut.SetTestModeAllAsync(_host.LocalProbeTargets(), ctx.Timecode, 0);
                sr.Scene = play.Scene;
                sr.Verdict = Verdict.Pass;
                sr.Message = $"started {(ctx.Active.PerMonitor ? "Simultaneous" : "Sequential")}, lead {ctx.Active.StartLeadMs} ms";
                break;

            case ProbeStep probe:
                ProbeAt.TryParse(probe.At, out var anchor, out var offset);
                long at = ProbeAt.Resolve(anchor, offset, ctx.Active!.SharedStartServerUtcMs, _o.NowUtcMs(), _o.MinLeadMs);
                var single = await ProbeOnceAsync(ctx, sr, at, probe.Capture, exactElapsedMs: null, perf: false, ct);
                sr.Probes.Add(single);
                sr.Verdict = single.Verdict;
                sr.Message = single.Drift?.Message ?? string.Empty;
                break;

            case ProbeSeriesStep series:
                long seriesStart = _o.NowUtcMs() + _o.MinLeadMs;
                for (long offsetMs = 0; offsetMs < series.ForMs; offsetMs += series.EveryMs)
                {
                    long due = Math.Max(seriesStart + offsetMs, _o.NowUtcMs() + _o.MinLeadMs);
                    sr.Probes.Add(await ProbeOnceAsync(ctx, sr, due, series.Capture, exactElapsedMs: null, perf: series.Perf, ct));
                }
                sr.Verdict = sr.Probes.Aggregate(Verdict.Skipped, (v, p) => Verdicts.Worst(v, p.Verdict));
                var spreads = sr.Probes.Where(p => p.Drift is { Verdict: not Verdict.Skipped }).Select(p => p.Drift!.SpreadMs).ToList();
                sr.Message = spreads.Count == 0
                    ? "no timing data"
                    : $"{sr.Probes.Count} probes, spread max {spreads.Max():F1} ms / mean {spreads.Average():F1} ms";
                var perfIssues = sr.Probes.SelectMany(p => p.PerfViolations).Distinct().ToList();
                if (perfIssues.Count > 0) sr.Message += $"; perf: {string.Join("; ", perfIssues)}";
                break;

            case ExactFrameStep exact:
                if (ctx.ActiveScene!.Background.Mode == BackgroundMode.IconZone)
                {
                    sr.Verdict = Verdict.Skipped;
                    sr.Message = "exact frames are not supported on IconZone backgrounds";
                    break;
                }
                var frame = await ProbeOnceAsync(ctx, sr, _o.NowUtcMs() + _o.MinLeadMs, exact.Capture, exact.ElapsedMs, perf: false, ct);
                sr.Probes.Add(frame);
                sr.Verdict = frame.Verdict;
                sr.Message = ctx.Active!.PerMonitor
                    ? $"{frame.Parity.Count} comparison(s)"
                    : "Sequential: every node shows a different slice; frames saved for review";
                break;

            case WaitStep wait:
                await Task.Delay(wait.Ms, ct);
                sr.Verdict = Verdict.Pass;
                break;

            case StopStep:
                await _host.StopAllAsync();
                ctx.Active = null;
                ctx.ActiveScene = null;
                sr.Verdict = Verdict.Pass;
                break;
        }
    }

    // ── one probe across all nodes ──────────────────────────────────────────

    private async Task<ProbeReport> ProbeOnceAsync(RunContext ctx, StepReport sr, long atServerUtcMs, bool capture, long? exactElapsedMs, bool perf, CancellationToken ct)
    {
        string probeId = $"{sr.Index:D2}-{++ctx.ProbeSeq:D3}";
        var pr = new ProbeReport { ProbeId = probeId, AtServerUtcMs = atServerUtcMs, Exact = exactElapsedMs != null };
        var timeout = TimeSpan.FromMilliseconds(Math.Max(0, atServerUtcMs - _o.NowUtcMs()) + _o.ProbeGraceMs);

        var localTask = ProbeFanOut.ProbeAllAsync(_host.LocalProbeTargets(), new PlayerProbeRequest
        {
            ProbeId = probeId,
            AtLocalUtcMs = atServerUtcMs,   // the server's own clock is the reference
            Capture = capture,
            ExactElapsedMs = exactElapsedMs,
            CaptureDirectory = Path.Combine(ctx.Dir, "tmp"),
        }, timeout, ct);

        var remotes = ctx.Remotes.Where(n => ctx.IsTarget(n.NodeId)).ToList();
        var request = new ProbeRequest { ProbeId = probeId, AtServerUtcMs = atServerUtcMs, Capture = capture, ExactElapsedMs = exactElapsedMs };
        var remoteTasks = remotes.Select(n => _transport.ProbeAsync(n.NodeId, request, timeout + TimeSpan.FromMilliseconds(_o.UploadGraceMs), ct)).ToList();

        var localReplies = await localTask;
        var remoteResults = await Task.WhenAll(remoteTasks);
        var localPerf = perf ? _perf.Sample() : null;

        foreach (var reply in localReplies)
        {
            var node = ctx.Nodes.FirstOrDefault(n => n.IsLocal && n.MonitorIndex == reply.MonitorIndex);
            var sample = NodeProbeSample.FromReply(reply, node?.NodeId ?? $"local-{reply.MonitorIndex}", node?.Name ?? $"server #{reply.MonitorIndex}", true, 0, 0);
            sample.Perf = localPerf;
            if (reply.CapturePath != null && File.Exists(reply.CapturePath))
            {
                sample.CapturePath = SaveCapture(ctx, sr, probeId, sample, await File.ReadAllBytesAsync(reply.CapturePath, ct));
                TryDelete(reply.CapturePath);
            }
            pr.Samples.Add(sample);
        }

        for (int i = 0; i < remotes.Count; i++)
        {
            var node = remotes[i];
            var result = remoteResults[i];
            if (result == null)
            {
                pr.Samples.Add(NodeProbeSample.MissingFor(node.NodeId, node.Name, 0, false, $"no reply within {timeout.TotalSeconds:F1} s"));
                continue;
            }
            if (result.Replies.Count == 0)
            {
                pr.Samples.Add(NodeProbeSample.MissingFor(node.NodeId, node.Name, 0, false, result.Error ?? "no players"));
                continue;
            }
            foreach (var reply in result.Replies)
            {
                var sample = NodeProbeSample.FromReply(reply, node.NodeId, node.Name, false, result.ClockOffsetMs, result.RttMs);
                sample.Perf = result.Perf;
                sample.Error ??= result.Error;
                if (result.Captures.TryGetValue(reply.MonitorIndex, out var png))
                    sample.CapturePath = SaveCapture(ctx, sr, probeId, sample, png);
                pr.Samples.Add(sample);
            }
        }

        var t = ctx.Scenario.Thresholds;
        if (exactElapsedMs == null && ctx.Active != null)
        {
            pr.Drift = DriftAnalyzer.Analyze(pr.Samples, ctx.Active.SharedStartServerUtcMs, t);
            pr.Verdict = pr.Drift.Verdict;
            if (capture && ctx.Marker) EvaluatePositions(ctx, pr);
        }
        if (exactElapsedMs != null) EvaluateParity(ctx, sr, pr);
        if (perf) EvaluatePerf(pr, t);
        return pr;
    }

    private void EvaluatePositions(RunContext ctx, ProbeReport pr)
    {
        var active = ctx.Active!;
        foreach (var s in pr.Samples.Where(s => !s.Missing && s.Error == null))
        {
            var layout = active.PerMonitor ? ExpectedPosition.PerMonitorLayout(s.NodeId, s.Width, s.Height) : active.Layout?.Get(s.NodeId);
            if (layout == null)
            {
                pr.Positions.Add(new PositionCheckResult { NodeId = s.NodeId, NodeName = s.NodeName, MonitorIndex = s.MonitorIndex, Verdict = Verdict.Skipped, Message = "no seat layout for this node" });
                continue;
            }
            var expected = ExpectedPosition.Compute(active.EffectiveMovement, s.RenderedElapsedMs, s.PlayerAnimWidth, s.PlayerAnimHeight, layout);
            MarkerDetection? detected = null;
            if (s.CapturePath != null)
            {
                var img = PngPixels.Decode(Path.Combine(ctx.Dir, s.CapturePath));
                detected = MarkerDetector.Detect(img.Pixels, img.Width, img.Height, img.Stride);
            }
            var check = PositionCheck.Evaluate(s, expected, detected, layout.Scale, ctx.Scenario.Thresholds.PositionErrorPx);
            pr.Positions.Add(check);
            pr.Verdict = Verdicts.Worst(pr.Verdict, check.Verdict);
        }
    }

    private void EvaluateParity(RunContext ctx, StepReport sr, ProbeReport pr)
    {
        pr.Verdict = pr.Samples.Any(s => s.Missing || s.Error != null) ? Verdict.Warn : Verdict.Skipped;
        if (!ctx.Active!.PerMonitor) return;

        var withFrames = pr.Samples.Where(s => !s.Missing && s.Error == null && s.CapturePath != null).ToList();
        foreach (var group in withFrames.GroupBy(s => (s.Width, s.Height)))
        {
            var members = group.ToList();
            if (members.Count < 2) continue;
            var reference = members[0];
            var refImg = PngPixels.Decode(Path.Combine(ctx.Dir, reference.CapturePath!));
            foreach (var other in members.Skip(1))
            {
                var img = PngPixels.Decode(Path.Combine(ctx.Dir, other.CapturePath!));
                var diff = PixelDiff.Compare(refImg.Pixels, refImg.Width, refImg.Height, refImg.Stride, img.Pixels, img.Width, img.Height, img.Stride);
                var result = new PixelParityResult
                {
                    NodeId = other.NodeId,
                    NodeName = other.NodeName,
                    ReferenceNodeName = reference.NodeName,
                    DiffPct = diff.DiffPct,
                    Verdict = !diff.SizeMismatch && diff.DiffPct <= ctx.Scenario.Thresholds.PixelDiffPct ? Verdict.Pass : Verdict.Fail,
                };
                result.Message = diff.SizeMismatch ? "different frame size" : $"{diff.DiffPct:F3} % of pixels differ";
                if (diff.DiffPixels > 0 && diff.DiffImage != null)
                {
                    var rel = RelativeCapturePath(sr, pr.ProbeId, other, "diff");
                    PngPixels.Encode(diff.DiffImage, img.Width, img.Height, img.Width * 4, Path.Combine(ctx.Dir, rel));
                    result.DiffImagePath = rel.Replace('\\', '/');
                }
                pr.Parity.Add(result);
                pr.Verdict = Verdicts.Worst(pr.Verdict, result.Verdict);
            }
        }
    }

    private static void EvaluatePerf(ProbeReport pr, ScenarioThresholds t)
    {
        // Local monitors share one machine sample; report each machine once.
        foreach (var s in pr.Samples.Where(s => s.Perf != null).GroupBy(s => s.IsLocal ? "server" : s.NodeId).Select(g => g.First()))
        {
            var p = s.Perf!;
            string who = s.IsLocal ? "server" : s.NodeName;
            if (p.AppCpuPercent > t.MaxCpuPercent) pr.PerfViolations.Add($"{who} app CPU {p.AppCpuPercent:F1} % > {t.MaxCpuPercent} %");
            if (p.PlayerCpuPercent > t.MaxCpuPercent) pr.PerfViolations.Add($"{who} player CPU {p.PlayerCpuPercent:F1} % > {t.MaxCpuPercent} %");
            if (p.GpuPercent > t.MaxGpuPercent) pr.PerfViolations.Add($"{who} GPU {p.GpuPercent:F1} % > {t.MaxGpuPercent} %");
            double memory = p.AppMemoryMb + p.PlayerMemoryMb;
            if (memory > t.MaxMemoryMb) pr.PerfViolations.Add($"{who} memory {memory:F0} MB > {t.MaxMemoryMb} MB");
        }
        if (pr.PerfViolations.Count > 0) pr.Verdict = Verdicts.Worst(pr.Verdict, Verdict.Warn);
    }

    // ── cleanup, logs ───────────────────────────────────────────────────────

    private async Task CleanupAsync(RunContext ctx, CrossScreenConfig? previousScene)
    {
        try
        {
            foreach (var n in ctx.Remotes)
                await _transport.SendTestModeAsync(n.NodeId, false, 0);
            await ProbeFanOut.SetTestModeAllAsync(_host.LocalProbeTargets(), false, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not switch test mode off on every node");
        }
        try
        {
            if (ctx.ScenePlayed) await _host.RestoreSceneAsync(previousScene);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restore the scene that played before the test run");
        }
    }

    private async Task CollectLogsAsync(RunContext ctx, long startedMs)
    {
        long toMs = _o.NowUtcMs();
        var logDir = Path.Combine(ctx.Dir, "logs");
        Directory.CreateDirectory(logDir);
        foreach (var n in ctx.Remotes)
        {
            string text;
            try { text = await _transport.FetchLogsAsync(n.NodeId, startedMs - 5000, toMs, _o.LogFetchTimeout) ?? "[no reply within the timeout]"; }
            catch (Exception ex) { text = $"[log fetch failed: {ex.Message}]"; }
            var file = $"{Sanitize(n.Name)}.log";
            await File.WriteAllTextAsync(Path.Combine(logDir, file), text);
            ctx.Report.LogFiles.Add($"logs/{file}");
        }

        try
        {
            var path = Path.Combine(_o.ResolveLocalLogDirectory(), $"wabibabusy-{DateTime.Today:yyyy-MM-dd}.log");
            if (File.Exists(path))
            {
                var lines = await LogTail.ReadLastLinesAsync(path, 20000);
                var window = LogTail.FilterWindow(lines,
                    DateTimeOffset.FromUnixTimeMilliseconds(startedMs - 5000).ToLocalTime().TimeOfDay,
                    DateTimeOffset.FromUnixTimeMilliseconds(toMs).ToLocalTime().TimeOfDay);
                await File.WriteAllLinesAsync(Path.Combine(logDir, "server.log"), window);
                ctx.Report.LogFiles.Add("logs/server.log");
            }
        }
        catch (Exception ex)
        {
            ctx.Report.Warnings.Add($"server log not collected: {ex.Message}");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private int StepTimeoutMs(TestStep step) => step.TimeoutMs ?? step switch
    {
        ProbeSeriesStep s => s.ForMs + 30000,
        WaitStep w => w.Ms + 30000,
        PlaySceneStep => 120000,   // includes downloads on remotes
        _ => _o.DefaultStepTimeoutMs,
    };

    private static int SkewFor(TestModeStep mode, TestNodeInfo node) =>
        mode.SimulatedClockSkewMs.TryGetValue(node.Name, out var byName) ? byName
        : mode.SimulatedClockSkewMs.TryGetValue(node.NodeId, out var byId) ? byId
        : 0;

    /// <summary>null = all nodes; otherwise the matching node ids (possibly empty = no match).</summary>
    private static List<string>? ResolveTargets(string targets, IReadOnlyList<TestNodeInfo> nodes)
    {
        var t = targets.Trim();
        if (t.Length == 0 || t.Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
        if (t.Equals("server", StringComparison.OrdinalIgnoreCase)) return nodes.Where(n => n.IsLocal).Select(n => n.NodeId).ToList();
        if (t.Equals("remotes", StringComparison.OrdinalIgnoreCase)) return nodes.Where(n => !n.IsLocal).Select(n => n.NodeId).ToList();
        var wanted = t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return nodes.Where(n => wanted.Any(w => w.Equals(n.Name, StringComparison.OrdinalIgnoreCase) || w == n.NodeId))
            .Select(n => n.NodeId).ToList();
    }

    private string CreateResultsDirectory(string scenarioName)
    {
        var baseName = $"{DateTime.Now:yyyy-MM-dd_HHmm}_{Sanitize(scenarioName)}";
        var dir = Path.Combine(_o.ResultsRoot, baseName);
        for (int n = 2; Directory.Exists(dir); n++) dir = Path.Combine(_o.ResultsRoot, $"{baseName}-{n}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SaveCapture(RunContext ctx, StepReport sr, string probeId, NodeProbeSample sample, byte[] png)
    {
        var rel = RelativeCapturePath(sr, probeId, sample, "mon" + sample.MonitorIndex);
        var full = Path.Combine(ctx.Dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, png);
        return rel.Replace('\\', '/');
    }

    private static string RelativeCapturePath(StepReport sr, string probeId, NodeProbeSample sample, string suffix) =>
        Path.Combine("nodes", Sanitize(sample.NodeName), $"{sr.Index:D2}-{Sanitize(sr.Label ?? sr.Kind)}-{probeId}-{suffix}.png");

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) || c == ' ' || c == '#' ? '_' : c).ToArray());
        return clean.Length == 0 ? "node" : clean;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* temp file */ }
    }

    private static string? CommitHash()
    {
        var info = typeof(TestRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int plus = info?.IndexOf('+') ?? -1;
        return plus >= 0 ? info![(plus + 1)..] : null;
    }

    private void SetStatus(TestScenario scenario, int stepIndex, string label, TestRunReport report) =>
        _status = new TestRunStatus(true, scenario.Name, stepIndex, scenario.Steps.Count, label, report.ResultsDirectory, null);
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~TestRunnerTests`
Expected: 8 passed. If `HealthyWall_Passes_AndWritesEverything` reports a small nonzero spread, that is the timer wake-up jitter of `Task.Delay`. It must stay below 25 ms. A larger value means the runner's time conversion is wrong, not the test.

- [ ] **Step 7: Full suite + commit**

Run: `dotnet test WaBiBaBuSy.Tests` → green.
```bash
git add WaBiBaBuSy.Models/Testing/TestRunReport.cs WaBiBaBuSy.Core/Services/Testing WaBiBaBuSy.Tests/TestRunnerTests.cs
git commit -m "feat(test-mode): TestRunner with preflight, probes, position/parity/perf checks, cleanup, report.json" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 13: HTML report

**Files:**
- Modify: `WaBiBaBuSy.Core/Services/Testing/TestReportWriter.cs`
- Create: `WaBiBaBuSy.Tests/TestReportWriterTests.cs`

**Interfaces:**
- Consumes: `TestRunReport` and children (Task 12).
- Produces: `TestReportWriter.Write(TestRunReport)` now writes both `report.json` and `report.html`; `TestReportWriter.RenderHtml(TestRunReport) → string` (public for tests).

The page is self-contained: inline CSS, inline SVG, and PNGs referenced by relative path. It has a light/dark theme (`prefers-color-scheme`) and needs no scripts. Contents:
- A summary: verdict, duration, nodes table, warnings, the clock note.
- One card per step, holding:
  - a drift chart for a `probeSeries`
  - a screenshot strip in seat order, each image overlaid with the expected box (green, dashed), the detected box (magenta) and the player's own center (cross)
  - parity rows with a diff-image link
  - perf violations

- [ ] **Step 1: Write the failing test**

`WaBiBaBuSy.Tests/TestReportWriterTests.cs`:
```csharp
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class TestReportWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-report-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private TestRunReport Report()
    {
        var sample = new NodeProbeSample { NodeId = "a", NodeName = "pc-01", Width = 400, Height = 300, CapturePath = "nodes/pc-01/02-p-02-001-mon0.png" };
        return new TestRunReport
        {
            Scenario = "sync <basic>", ResultsDirectory = _dir, Verdict = Verdict.Warn,
            StartedUtc = DateTimeOffset.UnixEpoch, FinishedUtc = DateTimeOffset.UnixEpoch.AddSeconds(65),
            Environment = new RunEnvironment { AppVersion = "2.6.3", Nodes = { new TestNodeInfo { NodeId = "a", Name = "pc-01", Width = 400, Height = 300 } } },
            Warnings = { "pc-02 runs 2.6.2" },
            Steps =
            {
                new StepReport
                {
                    Index = 2, Kind = "probeSeries", Label = "long-run", Verdict = Verdict.Warn, Message = "3 probes",
                    Probes =
                    {
                        new ProbeReport { ProbeId = "02-001", Verdict = Verdict.Pass, Samples = { sample },
                            Drift = new DriftResult { SpreadMs = 4, Verdict = Verdict.Pass },
                            Positions = { new PositionCheckResult { NodeId = "a", NodeName = "pc-01", Verdict = Verdict.Pass,
                                Expected = new MarkerExpectation { Visibility = MarkerVisibility.Visible, X = 10, Y = 10, Width = 64, Height = 64 } } } },
                        new ProbeReport { ProbeId = "02-002", Verdict = Verdict.Warn, Drift = new DriftResult { SpreadMs = 30, Verdict = Verdict.Warn } },
                        new ProbeReport { ProbeId = "02-003", Verdict = Verdict.Pass, Drift = new DriftResult { SpreadMs = 6, Verdict = Verdict.Pass } },
                    },
                },
            },
        };
    }

    [Fact]
    public void Write_CreatesJsonAndHtml()
    {
        TestReportWriter.Write(Report());
        Assert.True(File.Exists(Path.Combine(_dir, "report.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "report.html")));
    }

    [Fact]
    public void Html_EscapesText_ShowsVerdicts_ChartAndScreenshots()
    {
        var html = TestReportWriter.RenderHtml(Report());
        Assert.Contains("sync &lt;basic&gt;", html);                  // escaped, never raw markup
        Assert.DoesNotContain("sync <basic>", html);
        Assert.Contains("long-run", html);
        Assert.Contains("class=\"verdict warn\"", html);
        Assert.Contains("<polyline", html);                           // drift chart for the series
        Assert.Contains("href=\"nodes/pc-01/02-p-02-001-mon0.png\"", html);
        Assert.Contains("pc-02 runs 2.6.2", html);
        Assert.Contains("prefers-color-scheme: dark", html);
    }
}
```
Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~TestReportWriterTests` → fails (`RenderHtml` missing).

- [ ] **Step 2: Implement**

Replace `TestReportWriter.cs` with:
```csharp
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>Writes report.json (for tools) and report.html (for people) into the run's results folder.</summary>
public static class TestReportWriter
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Write(TestRunReport report)
    {
        Directory.CreateDirectory(report.ResultsDirectory);
        File.WriteAllText(Path.Combine(report.ResultsDirectory, "report.json"), JsonSerializer.Serialize(report, JsonOptions));
        File.WriteAllText(Path.Combine(report.ResultsDirectory, "report.html"), RenderHtml(report));
    }

    public static string RenderHtml(TestRunReport r)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(E(r.Scenario)).Append(" · test run</title><style>").Append(Css).Append("</style></head><body><main>");

        sb.Append("<header><h1>").Append(E(r.Scenario)).Append("</h1>").Append(Badge(r.Verdict));
        if (r.Aborted) sb.Append(" <span class=\"aborted\">aborted: ").Append(E(r.AbortReason ?? "")).Append("</span>");
        sb.Append("<p class=\"meta\">").Append(E(r.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
          .Append(" · ").Append(E(FormatDuration(r.FinishedUtc - r.StartedUtc)))
          .Append(" · v").Append(E(r.Environment.AppVersion));
        if (r.Environment.Commit != null) sb.Append(" · ").Append(E(r.Environment.Commit));
        if (r.PrefetchMs != null) sb.Append(" · prefetch ").Append(r.PrefetchMs).Append(" ms");
        sb.Append("</p></header>");

        sb.Append("<section><h2>Nodes</h2><table><tr><th>Seat</th><th>Name</th><th>Resolution</th><th>Hz</th><th>Clock offset</th><th>RTT</th><th>Version</th></tr>");
        foreach (var n in r.Environment.Nodes.OrderBy(n => n.SeatOrder))
            sb.Append("<tr><td>").Append(n.SeatOrder + 1).Append("</td><td>").Append(E(n.Name)).Append(n.IsLocal ? " <small>(server)</small>" : "")
              .Append("</td><td>").Append(n.Width).Append('×').Append(n.Height).Append("</td><td>").Append(n.RefreshHz)
              .Append("</td><td>").Append(n.ClockOffsetMs.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms</td><td>")
              .Append(n.RttMs.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms</td><td>").Append(E(n.AppVersion)).Append("</td></tr>");
        sb.Append("</table>");
        if (r.Warnings.Count > 0)
        {
            sb.Append("<ul class=\"warnings\">");
            foreach (var w in r.Warnings) sb.Append("<li>").Append(E(w)).Append("</li>");
            sb.Append("</ul>");
        }
        sb.Append("<p class=\"note\">").Append(E(r.ClockNote)).Append("</p></section>");

        foreach (var step in r.Steps) RenderStep(sb, step);

        if (r.LogFiles.Count > 0)
        {
            sb.Append("<section><h2>Logs</h2><ul>");
            foreach (var f in r.LogFiles) sb.Append("<li><a href=\"").Append(E(f)).Append("\">").Append(E(f)).Append("</a></li>");
            sb.Append("</ul></section>");
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void RenderStep(StringBuilder sb, StepReport s)
    {
        sb.Append("<section class=\"step\"><h2>").Append(s.Index).Append(". ").Append(E(s.Label ?? s.Kind))
          .Append(" <small>").Append(E(s.Kind)).Append("</small> ").Append(Badge(s.Verdict)).Append("</h2>");
        if (s.Message.Length > 0) sb.Append("<p>").Append(E(s.Message)).Append("</p>");

        var timed = s.Probes.Where(p => p.Drift is { Verdict: not Verdict.Skipped }).ToList();
        if (timed.Count >= 2) sb.Append(DriftChart(timed));

        foreach (var p in s.Probes)
        {
            bool hasImages = p.Samples.Any(x => x.CapturePath != null);
            bool interesting = hasImages || p.Parity.Count > 0 || p.PerfViolations.Count > 0 || s.Probes.Count == 1 || p.Verdict is Verdict.Fail or Verdict.Warn;
            if (!interesting) continue;

            sb.Append("<div class=\"probe\"><h3>Probe ").Append(E(p.ProbeId)).Append(' ').Append(Badge(p.Verdict)).Append("</h3>");
            if (p.Drift != null) sb.Append("<p>").Append(E(p.Drift.Message)).Append("</p>");

            if (p.Drift != null && p.Drift.Errors.Count > 0)
            {
                sb.Append("<table><tr><th>Node</th><th>Error</th><th>Clock bound</th><th>Note</th></tr>");
                foreach (var e in p.Drift.Errors)
                    sb.Append("<tr><td>").Append(E(e.NodeName)).Append("</td><td>").Append(e.ErrorMs.ToString("F1", CultureInfo.InvariantCulture))
                      .Append(" ms</td><td>±").Append(e.ClockBoundMs.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms</td><td>")
                      .Append(e.UsedRenderTime ? "render time (no present stats)" : "").Append("</td></tr>");
                sb.Append("</table>");
            }

            if (hasImages)
            {
                sb.Append("<div class=\"strip\">");
                foreach (var sample in p.Samples.Where(x => x.CapturePath != null))
                    sb.Append(Screenshot(sample, p.Positions.FirstOrDefault(c => c.NodeId == sample.NodeId && c.MonitorIndex == sample.MonitorIndex)));
                sb.Append("</div>");
            }

            foreach (var par in p.Parity)
            {
                sb.Append("<p>").Append(Badge(par.Verdict)).Append(' ').Append(E(par.NodeName)).Append(" vs ").Append(E(par.ReferenceNodeName))
                  .Append(": ").Append(E(par.Message));
                if (par.DiffImagePath != null) sb.Append(" · <a href=\"").Append(E(par.DiffImagePath)).Append("\">diff image</a>");
                sb.Append("</p>");
            }

            foreach (var missing in p.Samples.Where(x => x.Missing || x.Error != null))
                sb.Append("<p class=\"missing\">").Append(E(missing.NodeName)).Append(": ").Append(E(missing.Error ?? "missing")).Append("</p>");
            foreach (var v in p.PerfViolations) sb.Append("<p class=\"perf\">").Append(E(v)).Append("</p>");
            sb.Append("</div>");
        }
        sb.Append("</section>");
    }

    private static string Screenshot(NodeProbeSample s, PositionCheckResult? check)
    {
        var sb = new StringBuilder("<figure>");
        sb.Append("<a href=\"").Append(E(s.CapturePath!)).Append("\"><svg viewBox=\"0 0 ").Append(s.Width).Append(' ').Append(s.Height)
          .Append("\" role=\"img\" aria-label=\"").Append(E(s.NodeName)).Append(" capture\"><image href=\"").Append(E(s.CapturePath!))
          .Append("\" width=\"").Append(s.Width).Append("\" height=\"").Append(s.Height).Append("\"/>");
        if (check != null && check.Expected.Visibility is MarkerVisibility.Visible or MarkerVisibility.Partial)
            sb.Append(Rect(check.Expected.X, check.Expected.Y, check.Expected.Width, check.Expected.Height, "expected"));
        if (check?.Detected is { Found: true } d)
            sb.Append(Rect(d.X, d.Y, d.Width, d.Height, "detected"));
        if (check != null)
            sb.Append("<path class=\"player\" d=\"M").Append(F(check.PlayerCenterX - 10)).Append(' ').Append(F(check.PlayerCenterY))
              .Append("h20M").Append(F(check.PlayerCenterX)).Append(' ').Append(F(check.PlayerCenterY - 10)).Append("v20\"/>");
        sb.Append("</svg></a><figcaption>").Append(E(s.NodeName));
        if (s.MonitorIndex > 0) sb.Append(" #").Append(s.MonitorIndex);
        sb.Append(" · ").Append(s.RenderedElapsedMs).Append(" ms");
        if (check != null) sb.Append(" · ").Append(Badge(check.Verdict)).Append(' ').Append(E(check.Message));
        sb.Append("</figcaption></figure>");
        return sb.ToString();
    }

    private static string DriftChart(IReadOnlyList<ProbeReport> probes)
    {
        const int w = 600, h = 160, pad = 28;
        double max = Math.Max(60, probes.Max(p => p.Drift!.SpreadMs) * 1.1);
        string X(int i) => F(pad + i * (double)(w - 2 * pad) / Math.Max(1, probes.Count - 1));
        string Y(double v) => F(h - pad - v / max * (h - 2 * pad));
        var points = string.Join(' ', probes.Select((p, i) => $"{X(i)},{Y(p.Drift!.SpreadMs)}"));
        var sb = new StringBuilder("<figure class=\"chart\"><svg viewBox=\"0 0 ").Append(w).Append(' ').Append(h).Append("\" role=\"img\" aria-label=\"drift spread per probe\">");
        sb.Append("<line class=\"axis\" x1=\"").Append(pad).Append("\" y1=\"").Append(h - pad).Append("\" x2=\"").Append(w - pad).Append("\" y2=\"").Append(h - pad).Append("\"/>");
        foreach (var (v, cls) in new[] { (25.0, "warn"), (50.0, "fail") })
            sb.Append("<line class=\"limit ").Append(cls).Append("\" x1=\"").Append(pad).Append("\" y1=\"").Append(Y(v)).Append("\" x2=\"").Append(w - pad)
              .Append("\" y2=\"").Append(Y(v)).Append("\"/><text x=\"").Append(w - pad + 2).Append("\" y=\"").Append(Y(v)).Append("\">").Append(v).Append("</text>");
        sb.Append("<polyline points=\"").Append(points).Append("\"/>");
        sb.Append("</svg><figcaption>Drift spread (ms) per probe</figcaption></figure>");
        return sb.ToString();
    }

    private static string Rect(float x, float y, float w, float h, string cls) =>
        $"<rect class=\"{cls}\" x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\"/>";

    private static string Badge(Verdict v) => $"<span class=\"verdict {v.ToString().ToLowerInvariant()}\">{v.ToString().ToLowerInvariant()}</span>";
    private static string E(string text) => WebUtility.HtmlEncode(text);
    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static string FormatDuration(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.TotalSeconds:F0} s";

    private const string Css = """
        :root{--bg:#f7f7f5;--fg:#1d1d1b;--muted:#6b6b66;--card:#fff;--line:#deded8;--pass:#1f7a3f;--warn:#a15c00;--fail:#b3261e;--skip:#6b6b66;--accent:#c026d3}
        @media (prefers-color-scheme: dark){:root{--bg:#161615;--fg:#ececea;--muted:#9a9a94;--card:#20201f;--line:#34342f;--pass:#5cc27f;--warn:#e0a13a;--fail:#ef6b62;--skip:#9a9a94;--accent:#e879f9}}
        body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,sans-serif}
        main{max-width:1100px;margin:0 auto;padding:24px 16px}
        h1{display:inline;margin:0 8px 0 0;font-size:24px}h2{font-size:17px;margin:0 0 8px}h3{font-size:14px;margin:12px 0 4px}
        section{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px;margin:16px 0}
        .meta,.note,small,figcaption{color:var(--muted)}
        table{border-collapse:collapse;width:100%;margin:8px 0}th,td{text-align:left;padding:4px 8px;border-bottom:1px solid var(--line)}
        .verdict{display:inline-block;padding:1px 8px;border-radius:999px;font-size:12px;font-weight:600;color:#fff}
        .verdict.pass{background:var(--pass)}.verdict.warn{background:var(--warn)}.verdict.fail{background:var(--fail)}.verdict.skipped{background:var(--skip)}
        .aborted,.missing{color:var(--fail)}.perf,.warnings{color:var(--warn)}
        .strip{display:flex;gap:8px;overflow-x:auto;padding-bottom:4px}
        figure{margin:0;flex:0 0 auto;width:280px}figure.chart{width:100%}svg{width:100%;height:auto;display:block;border-radius:6px;background:#000}
        figure.chart svg{background:transparent}
        polyline{fill:none;stroke:var(--accent);stroke-width:2}.axis{stroke:var(--line)}
        .limit{stroke-dasharray:4 4}.limit.warn{stroke:var(--warn)}.limit.fail{stroke:var(--fail)}text{fill:var(--muted);font-size:10px}
        rect.expected{fill:none;stroke:#22c55e;stroke-width:3;stroke-dasharray:8 6}rect.detected{fill:none;stroke:#ff00ff;stroke-width:3}
        path.player{stroke:#facc15;stroke-width:3}
        a{color:inherit}
        """;
}
```

- [ ] **Step 3: Run the tests** → `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~TestReportWriterTests|FullyQualifiedName~TestRunnerTests"` → all pass.

- [ ] **Step 4: Commit**

```bash
git add WaBiBaBuSy.Core/Services/Testing/TestReportWriter.cs WaBiBaBuSy.Tests/TestReportWriterTests.cs
git commit -m "feat(test-mode): self-contained HTML report with drift chart and annotated screenshots" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 14: UI — settings, test host adapter, Developer tools menu

**Files:**
- Modify: `WaBiBaBuSy.Models/Configuration/ServerConfiguration.cs` (`EnableTestMode`, `TestControlPort`)
- Modify: `WaBiBaBuSy.UI/ViewModels/SettingsViewModel.cs`, `WaBiBaBuSy.UI/Views/SettingsWindow.axaml`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (`ActiveStartLeadMs`, `ActiveEffectiveMovement`)
- Create: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.Testing.cs`
- Create: `WaBiBaBuSy.UI/Services/Testing/TestHostAdapter.cs`
- Modify: `WaBiBaBuSy.UI/Views/MainWindow.axaml` (Developer tools expander ≈ `:106`; toolbar status text)
- Modify: `WaBiBaBuSy.Core/Services/WaBiBaBuSyService.cs` (`ServerSyncService`)

**Interfaces:**
- Consumes: `ITestHost`, `SceneStartInfo`, `TestNodeInfo`, `ITestProbeTarget`, `ServerTestChannel`, `TestRunner`; VM privates `StartCrossScreen()`, `StopCrossScreen()`, `StopPlaylistAsync()`, `ClearNodesAsync(…)`, `PrefetchShowAssetsAsync(…)`, `WaitUntilRemotesPrefetchedAsync(TimeSpan)`, `IsLocalMonitor(string)`, `_d2dCompositionServices`, `_storageProvider`.
- Produces: `ServerConfiguration.EnableTestMode` (default false), `ServerConfiguration.TestControlPort` (default 50052); `WaBiBaBuSyService.ServerSyncService → WallpaperSyncService?`
- Produces on `MainWindowViewModel`: `internal` test hooks `TestNodesAsync()`, `PlaySceneForTestAsync(CrossScreenConfig, IReadOnlyList<string>)`, `StopAllForTestAsync()`, `SceneForTestRestore`, `PrefetchForTestAsync(IReadOnlyList<CrossScreenConfig>, TimeSpan)`, `LocalProbeTargetsForTest()`; observable `TestRunStatusText`, `IsTestRunning`, `IsTestModeEnabled`; commands `RunTestSuiteCommand`, `CancelTestRunCommand`, `OpenTestResultsCommand`; property `TestCoordinator` (type `TestRunCoordinator`, set in Task 15; until then the commands use a runner directly, see Step 5).
- Produces: `TestHostAdapter(MainWindowViewModel vm) : ITestHost`

- [ ] **Step 1: Configuration + settings UI**

`ServerConfiguration` (after `UploadLimitMBps`):
```csharp
    /// <summary>
    /// Developer: enables automated test runs (Developer tools → Run test suite, --test-run and the
    /// local control API on <see cref="TestControlPort"/>, bound to 127.0.0.1 only).
    /// </summary>
    public bool EnableTestMode { get; set; } = false;

    /// <summary>Port of the local test control API (HTTP/1.1, 127.0.0.1 only).</summary>
    public int TestControlPort { get; set; } = 50052;
```

`SettingsViewModel`: add next to `_uploadLimitMBps`:
```csharp
    [ObservableProperty]
    private bool _enableTestMode;

    [ObservableProperty]
    private int _testControlPort;
```
and next to `_pauseOnFullscreen`:
```csharp
    [ObservableProperty]
    private bool _allowTestRuns;
```
In the load method, next to `UploadLimitMBps = …`: `EnableTestMode = serverConfig.EnableTestMode; TestControlPort = serverConfig.TestControlPort;`. Next to `PauseOnFullscreen = clientConfig.PauseOnFullscreen;`: `AllowTestRuns = clientConfig.AllowTestRuns;`. In the save method, next to `s.UploadLimitMBps = …`: `s.EnableTestMode = EnableTestMode; s.TestControlPort = Math.Clamp(TestControlPort, 1024, 65535);`. Next to `c.PauseOnFullscreen = …`: `c.AllowTestRuns = AllowTestRuns;`.

`SettingsWindow.axaml`: directly after the Server section's `<Expander Header="Advanced">…</Expander>`:
```xml
                    <Expander Header="Developer">
                        <StackPanel Spacing="8">
                            <CheckBox Content="Enable test mode (Developer tools → Run test suite, --test-run, local control API)"
                                      IsChecked="{Binding EnableTestMode}" Foreground="White"/>
                            <StackPanel Orientation="Horizontal" Spacing="8" IsEnabled="{Binding EnableTestMode}">
                                <TextBlock Classes="label" Text="Control API port (127.0.0.1 only):" VerticalAlignment="Center"/>
                                <NumericUpDown Value="{Binding TestControlPort}" Minimum="1024" Maximum="65535" Width="140"/>
                            </StackPanel>
                            <TextBlock Foreground="#888888" FontSize="11" TextWrapping="Wrap"
                                       Text="Takes effect when the server starts. Results go to %LOCALAPPDATA%\WaBiBaBuSy\TestRuns."/>
                        </StackPanel>
                    </Expander>
```
In the Client section, after its `Advanced` expander (the one with `MaxCacheSizeMB`):
```xml
                    <CheckBox Content="Answer automated test runs from the server (screenshots show only the wallpaper)"
                              IsChecked="{Binding AllowTestRuns}" Foreground="White" Margin="0,8,0,0"/>
```

- [ ] **Step 2: Expose the server sync service; record lead + effective movement**

`WaBiBaBuSyService`: next to `SyncCoordinator`:
```csharp
    /// <summary>The running server's gRPC service (test runs); null when the server is stopped.</summary>
    public WaBiBaBuSy.Grpc.Services.WallpaperSyncService? ServerSyncService => _serverHost?.SyncService;
```
`MainWindowViewModel.cs`: next to `[ObservableProperty] private long _activeSharedStartMs;`:
```csharp
    [ObservableProperty] private int _activeStartLeadMs;
    /// <summary>Movement the players received (after cm→px); test runs compute expected positions from it.</summary>
    [ObservableProperty] private MovementConfig? _activeEffectiveMovement;
```
In `ApplyCrossScreenConfigAsync`, next to `ActiveSharedStartMs = sharedStartTimestamp;`:
```csharp
        ActiveStartLeadMs = startLeadMs;
        ActiveEffectiveMovement = effMovement;
```

- [ ] **Step 3: VM test hooks**

`WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.Testing.cs`:
```csharp
using System.Diagnostics;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using AppVersionInfo = WaBiBaBuSy.Common.Version.VersionInfo;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>Automated test mode (2026-09-30 design): hooks for <see cref="Services.Testing.TestHostAdapter"/> and the menu.</summary>
public partial class MainWindowViewModel
{
    [ObservableProperty] private string? _testRunStatusText;
    [ObservableProperty] private bool _isTestRunning;

    /// <summary>Server setting "Enable test mode"; hides the menu item when off.</summary>
    public bool IsTestModeEnabled => WaBiBaBuSy.Models.Configuration.ConfigurationManager.LoadServerConfiguration().EnableTestMode;

    internal Task<IReadOnlyList<TestNodeInfo>> TestNodesAsync() => Dispatcher.UIThread.InvokeAsync<IReadOnlyList<TestNodeInfo>>(() =>
    {
        var versions = _service.GetConnectedClients().GroupBy(c => c.ClientId).ToDictionary(g => g.Key, g => g.First().AppVersion);
        return Clients.GroupBy(c => c.ClientId).Select(g => g.First()).OrderBy(c => c.Order).Select((c, seat) =>
        {
            bool local = IsLocalMonitor(c.ClientId);
            return new TestNodeInfo
            {
                NodeId = c.ClientId,
                Name = local ? $"server #{Math.Max(0, c.MonitorIndex)}" : c.Hostname,
                IsLocal = local,
                Connected = local || c.IsConnected,
                MonitorIndex = Math.Max(0, c.MonitorIndex),
                SeatOrder = seat,
                Width = c.MonitorWidth,
                Height = c.MonitorHeight,
                RefreshHz = c.MonitorRefreshHz,
                ClockOffsetMs = c.DriftMs,
                RttMs = c.RttMs,
                AppVersion = local ? AppVersionInfo.AppVersion : versions.GetValueOrDefault(c.ClientId, string.Empty),
            };
        }).ToList();
    });

    /// <summary>Same teardown + start as ▶ Play (PlayDraftAsync), without touching the editor draft.</summary>
    internal Task<SceneStartInfo> PlaySceneForTestAsync(CrossScreenConfig config, IReadOnlyList<string> targetNodeIds) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            config.SelectedMonitorIds = targetNodeIds.ToList();
            if (_playlistOrchestrator?.IsRunning == true)
            {
                await StopPlaylistAsync();
                await ClearNodesAsync(Clients.Select(c => c.ClientId).Distinct().ToList());
            }
            if (IsCrossScreenRunning) await StopCrossScreen();

            _crossScreenConfig = config;
            HasAnimationConfig = true;
            await StartCrossScreen();
            if (!ReferenceEquals(ActiveScene, config))
                throw new InvalidOperationException("the scene did not start (see the server log)");

            return new SceneStartInfo
            {
                SharedStartServerUtcMs = ActiveSharedStartMs,
                StartLeadMs = ActiveStartLeadMs,
                Layout = ActiveLayout,
                PerMonitor = config.DistributionMode == AnimationDistributionMode.Simultaneous,
                EffectiveMovement = ActiveEffectiveMovement ?? config.Movement,
            };
        });

    internal Task StopAllForTestAsync() => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        if (IsCrossScreenRunning) await StopCrossScreen();
        await ClearNodesAsync(Clients.Select(c => c.ClientId).Distinct().ToList());
    });

    /// <summary>What to restore after a run: the running scene, or null when nothing plays.</summary>
    internal CrossScreenConfig? SceneForTestRestore => IsCrossScreenRunning ? ActiveScene : null;

    internal async Task<bool> PrefetchForTestAsync(IReadOnlyList<CrossScreenConfig> scenes, TimeSpan timeout)
    {
        await PrefetchShowAssetsAsync(scenes);
        return await WaitUntilRemotesPrefetchedAsync(timeout);
    }

    internal IReadOnlyList<ITestProbeTarget> LocalProbeTargetsForTest() =>
        _d2dCompositionServices.Values.Where(s => s.IsRunning).Cast<ITestProbeTarget>().ToList();

    [RelayCommand]
    private async Task RunTestSuite()
    {
        if (_storageProvider == null || IsTestRunning) return;
        var bundled = Path.Combine(AppContext.BaseDirectory, "TestScenarios");
        var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Run test scenario",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Test scenario") { Patterns = new[] { "*.json" } } },
            SuggestedStartLocation = Directory.Exists(bundled) ? await _storageProvider.TryGetFolderFromPathAsync(bundled) : null,
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) await StartTestRunAsync(path);
    }

    [RelayCommand]
    private void CancelTestRun() => TestCoordinator?.Cancel();

    [RelayCommand]
    private void OpenTestResults()
    {
        var dir = TestCoordinator?.Status.ResultsDirectory;
        var html = dir != null ? Path.Combine(dir, "report.html") : null;
        if (html != null && File.Exists(html))
            Process.Start(new ProcessStartInfo { FileName = html, UseShellExecute = true });
    }
}
```

- [ ] **Step 4: `TestHostAdapter`**

`WaBiBaBuSy.UI/Services/Testing/TestHostAdapter.cs`:
```csharp
using Avalonia.Threading;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Services.Testing;

/// <summary><see cref="ITestHost"/> over the server control panel's view model.</summary>
public sealed class TestHostAdapter : ITestHost
{
    private readonly MainWindowViewModel _vm;

    public TestHostAdapter(MainWindowViewModel vm) => _vm = vm;

    public Task<IReadOnlyList<TestNodeInfo>> GetNodesAsync() => _vm.TestNodesAsync();

    public Task<SceneStartInfo> PlaySceneAsync(CrossScreenConfig scene, IReadOnlyList<string> targetNodeIds, CancellationToken ct) =>
        _vm.PlaySceneForTestAsync(scene, targetNodeIds);

    public Task StopAllAsync() => _vm.StopAllForTestAsync();

    public CrossScreenConfig? CurrentScene => _vm.SceneForTestRestore;

    public Task RestoreSceneAsync(CrossScreenConfig? scene) =>
        scene == null ? _vm.StopAllForTestAsync() : _vm.PlaySceneForTestAsync(scene, scene.SelectedMonitorIds.ToList());

    public Task<bool> PrefetchAsync(IReadOnlyList<CrossScreenConfig> scenes, TimeSpan timeout, CancellationToken ct) =>
        _vm.PrefetchForTestAsync(scenes, timeout);

    public IReadOnlyList<ITestProbeTarget> LocalProbeTargets() => _vm.LocalProbeTargetsForTest();

    public void ReportProgress(string text) => Dispatcher.UIThread.Post(() => _vm.TestRunStatusText = text);
}
```

- [ ] **Step 5: Menu + toolbar text**

In `MainWindow.axaml`, inside the Developer tools expander's `StackPanel`, after the debug-overlay `WrapPanel`:
```xml
                                        <WrapPanel Orientation="Horizontal" IsVisible="{Binding IsTestModeEnabled}">
                                            <TextBlock Text="Test mode" VerticalAlignment="Center" FontSize="11" Foreground="#AAAAAA" Margin="0,0,8,0"/>
                                            <Button Content="Run test suite…" Command="{Binding RunTestSuiteCommand}" FontSize="11" Margin="0,0,6,0"/>
                                            <Button Content="Cancel" Command="{Binding CancelTestRunCommand}" IsVisible="{Binding IsTestRunning}" FontSize="11" Margin="0,0,6,0"/>
                                            <Button Content="Open last results" Command="{Binding OpenTestResultsCommand}" FontSize="11"/>
                                        </WrapPanel>
```
In the toolbar row, next to the show "Now: … · next: …" label, add:
```xml
                <TextBlock Text="{Binding TestRunStatusText}" IsVisible="{Binding TestRunStatusText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
                           VerticalAlignment="Center" FontSize="11" Foreground="#E0A13A" Margin="8,0" TextTrimming="CharacterEllipsis" MaxWidth="360"/>
```
`StartTestRunAsync(string path)` and the `TestCoordinator` property come in Task 15. So that this task builds on its own, add a temporary private stub in `MainWindowViewModel.Testing.cs` now, and Task 15 replaces it:
```csharp
    /// <summary>Set by the tray (Task 15). Null until then.</summary>
    public TestRunCoordinator? TestCoordinator { get; set; }

    private Task StartTestRunAsync(string path)
    {
        TestRunStatusText = "Test runner not wired yet";
        return Task.CompletedTask;
    }
```
Because this stub references `TestRunCoordinator`, create an empty `public sealed class TestRunCoordinator { public TestRunStatus Status => TestRunStatus.Idle; public bool Cancel() => false; }` in `WaBiBaBuSy.Core/Services/Testing/TestRunCoordinator.cs`. Task 15 replaces the whole file.

- [ ] **Step 6: Build + manual check**

Run: `dotnet build WaBiBaBuSy.sln -c Debug` → 0 errors; `dotnet test WaBiBaBuSy.Tests` green.
Manual: Settings → Server → Developer → Enable test mode → Save → reopen the window: `⋯` → Developer tools shows the "Test mode" row. Client settings show "Answer automated test runs…" checked.

- [ ] **Step 7: Commit**

```bash
git add WaBiBaBuSy.Models/Configuration WaBiBaBuSy.UI WaBiBaBuSy.Core/Services/WaBiBaBuSyService.cs WaBiBaBuSy.Core/Services/Testing/TestRunCoordinator.cs
git commit -m "feat(test-mode): settings, UI test host adapter, Developer tools entry" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 15: One coordinator for menu, CLI and localhost API

**Files:**
- Replace: `WaBiBaBuSy.Core/Services/Testing/TestRunCoordinator.cs`
- Create: `WaBiBaBuSy.Core/Services/Testing/TestControlEndpoints.cs`
- Modify: `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncServerHost.cs`, `WaBiBaBuSy.Core/Services/WaBiBaBuSyService.cs`
- Create: `WaBiBaBuSy.UI/StartupOptions.cs`
- Modify: `WaBiBaBuSy.UI/Program.cs`, `WaBiBaBuSy.UI/App.axaml.cs`, `WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs`, `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.Testing.cs`
- Create: `WaBiBaBuSy.Tests/TestRunCoordinatorTests.cs`

**Interfaces:**
- Produces: `interface ITestRunControl { TestRunStatus Status { get; } bool IsRunning { get; } Task<TestRunReport> StartAsync(string scenarioPath); bool Cancel(); IReadOnlyList<string> ListRuns(); }`
- Produces: `TestRunCoordinator(Func<ITestHost?> hostFactory, Func<ITestTransport?> transportFactory, ILoggerFactory loggerFactory, TestRunnerOptions? options = null) : ITestRunControl`, `event EventHandler<TestRunReport>? RunFinished`. `StartAsync` validates synchronously: it throws `ScenarioException` for a bad file and `InvalidOperationException` when a run is active or no host/transport is available. It then runs on the thread pool.
- Produces: `TestControlEndpoints.Map(WebApplication app, ITestRunControl control, int port)`; `WallpaperSyncServerHost.TestControl { get; set; }`, `TestControlPort { get; set; }`; `WaBiBaBuSyService.TestRunControl { get; set; }`
- Produces: `StartupOptions.Parse(string[] args) → StartupOptions { string? TestRunPath; bool ExitWhenDone }`, `StartupOptions.Current`
- Exit codes for `--test-run … --exit`: 0 = no step failed, 1 = a step failed, 2 = aborted / could not run.

- [ ] **Step 1: Failing tests (coordinator + CLI parsing)**

`WaBiBaBuSy.Tests/TestRunCoordinatorTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class TestRunCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-coord-{Guid.NewGuid():N}");

    public TestRunCoordinatorTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "wait.json"), """{ "name": "wait", "steps": [ { "type": "wait", "ms": 2000 } ] }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class EmptyHost : ITestHost
    {
        public Task<IReadOnlyList<TestNodeInfo>> GetNodesAsync() => Task.FromResult<IReadOnlyList<TestNodeInfo>>(Array.Empty<TestNodeInfo>());
        public Task<SceneStartInfo> PlaySceneAsync(CrossScreenConfig s, IReadOnlyList<string> t, CancellationToken ct) => Task.FromResult(new SceneStartInfo());
        public Task StopAllAsync() => Task.CompletedTask;
        public CrossScreenConfig? CurrentScene => null;
        public Task RestoreSceneAsync(CrossScreenConfig? scene) => Task.CompletedTask;
        public Task<bool> PrefetchAsync(IReadOnlyList<CrossScreenConfig> s, TimeSpan t, CancellationToken ct) => Task.FromResult(true);
        public IReadOnlyList<ITestProbeTarget> LocalProbeTargets() => Array.Empty<ITestProbeTarget>();
        public void ReportProgress(string text) { }
    }

    private sealed class NoTransport : ITestTransport
    {
        public Task<bool> SendTestModeAsync(string c, bool t, int s) => Task.FromResult(true);
        public Task<RemoteProbeResult?> ProbeAsync(string c, ProbeRequest r, TimeSpan t, CancellationToken ct) => Task.FromResult<RemoteProbeResult?>(null);
        public Task<string?> FetchLogsAsync(string c, long f, long t, TimeSpan to) => Task.FromResult<string?>(null);
    }

    private TestRunCoordinator Coordinator(bool withHost = true) => new(
        () => withHost ? new EmptyHost() : null, () => new NoTransport(), NullLoggerFactory.Instance,
        new TestRunnerOptions { ResultsRoot = Path.Combine(_dir, "results"), LocalLogDirectory = _dir, MinLeadMs = 20 });

    [Fact]
    public async Task SecondStart_WhileRunning_IsRejected_CancelEndsTheRun()
    {
        var c = Coordinator();
        var run = c.StartAsync(Path.Combine(_dir, "wait.json"));
        Assert.True(c.IsRunning);
        Assert.Throws<InvalidOperationException>(() => { _ = c.StartAsync(Path.Combine(_dir, "wait.json")); });

        Assert.True(c.Cancel());
        var report = await run;
        Assert.True(report.Aborted);
        Assert.False(c.IsRunning);
        Assert.Single(c.ListRuns());
    }

    [Fact]
    public void BadScenario_ThrowsBeforeStarting() =>
        Assert.Throws<ScenarioException>(() => { _ = Coordinator().StartAsync(Path.Combine(_dir, "missing.json")); });

    [Fact]
    public void NoHost_Throws() =>
        Assert.Throws<InvalidOperationException>(() => { _ = Coordinator(withHost: false).StartAsync(Path.Combine(_dir, "wait.json")); });
}
```
Also add to `WaBiBaBuSy.Tests.csproj` a reference to the UI project? **No.** `StartupOptions` is tiny and lives in UI (WinExe). Test it by hand in Step 7 instead of coupling the test project to Avalonia.

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~TestRunCoordinatorTests` → build errors (constructor missing).

- [ ] **Step 2: Coordinator**

Replace `WaBiBaBuSy.Core/Services/Testing/TestRunCoordinator.cs`:
```csharp
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>Start / cancel / inspect test runs; shared by the menu, the CLI switch and the control API.</summary>
public interface ITestRunControl
{
    TestRunStatus Status { get; }
    bool IsRunning { get; }
    /// <summary>Validates and starts a run. Throws <see cref="ScenarioException"/> or <see cref="InvalidOperationException"/> before anything plays.</summary>
    Task<TestRunReport> StartAsync(string scenarioPath);
    bool Cancel();
    /// <summary>Result folders, newest first.</summary>
    IReadOnlyList<string> ListRuns();
}

/// <summary>At most one run at a time.</summary>
public sealed class TestRunCoordinator : ITestRunControl
{
    private readonly Func<ITestHost?> _hostFactory;
    private readonly Func<ITestTransport?> _transportFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TestRunnerOptions _options;
    private readonly object _lock = new();
    private TestRunner? _runner;
    private CancellationTokenSource? _cts;
    private Task<TestRunReport>? _run;

    public TestRunCoordinator(Func<ITestHost?> hostFactory, Func<ITestTransport?> transportFactory, ILoggerFactory loggerFactory, TestRunnerOptions? options = null)
    {
        _hostFactory = hostFactory;
        _transportFactory = transportFactory;
        _loggerFactory = loggerFactory;
        _options = options ?? new TestRunnerOptions();
    }

    public event EventHandler<TestRunReport>? RunFinished;

    public TestRunStatus Status => _runner?.Status ?? TestRunStatus.Idle;

    public bool IsRunning
    {
        get { lock (_lock) return _run is { IsCompleted: false }; }
    }

    public Task<TestRunReport> StartAsync(string scenarioPath)
    {
        var scenario = ScenarioLoader.Load(scenarioPath);   // throws ScenarioException
        lock (_lock)
        {
            if (_run is { IsCompleted: false }) throw new InvalidOperationException("a test run is already active");
            var host = _hostFactory() ?? throw new InvalidOperationException("open the server control panel first");
            var transport = _transportFactory() ?? throw new InvalidOperationException("start the server first");

            _cts = new CancellationTokenSource();
            _runner = new TestRunner(host, transport, _loggerFactory.CreateLogger<TestRunner>(), _options);
            var runner = _runner;
            var token = _cts.Token;
            _run = Task.Run(async () =>
            {
                try
                {
                    var report = await runner.RunAsync(scenario, Path.GetFullPath(scenarioPath), token);
                    RunFinished?.Invoke(this, report);
                    return report;
                }
                finally
                {
                    (transport as IDisposable)?.Dispose();
                }
            });
            return _run;
        }
    }

    public bool Cancel()
    {
        lock (_lock)
        {
            if (_run is not { IsCompleted: false } || _cts == null) return false;
            _cts.Cancel();
            return true;
        }
    }

    public IReadOnlyList<string> ListRuns() =>
        Directory.Exists(_options.ResultsRoot)
            ? new DirectoryInfo(_options.ResultsRoot).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Select(d => d.FullName).ToList()
            : Array.Empty<string>();
}
```
Run the coordinator tests → pass.

- [ ] **Step 3: Localhost control API**

`WaBiBaBuSy.Core/Services/Testing/TestControlEndpoints.cs`:
```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>
/// HTTP/1.1 endpoints on 127.0.0.1:&lt;port&gt; (never the gRPC port): POST /test/run {"scenario": path},
/// GET /test/status, DELETE /test/run, GET /test/runs.
/// </summary>
public static class TestControlEndpoints
{
    private sealed record RunRequest(string? Scenario);

    public static void Map(WebApplication app, ITestRunControl control, int port)
    {
        var hosts = new[] { $"127.0.0.1:{port}", $"localhost:{port}" };
        var json = TestReportWriter.JsonOptions;

        app.MapPost("/test/run", async (HttpRequest request) =>
        {
            RunRequest? body;
            try { body = await JsonSerializer.DeserializeAsync<RunRequest>(request.Body, json); }
            catch (JsonException ex) { return Results.BadRequest(new { error = $"invalid JSON: {ex.Message}" }); }
            if (string.IsNullOrWhiteSpace(body?.Scenario)) return Results.BadRequest(new { error = "scenario is required" });
            try
            {
                _ = control.StartAsync(body.Scenario);
                return Results.Json(new { started = true, scenario = body.Scenario }, json, statusCode: StatusCodes.Status202Accepted);
            }
            catch (ScenarioException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireHost(hosts);

        app.MapGet("/test/status", () => Results.Json(control.Status, json)).RequireHost(hosts);
        app.MapDelete("/test/run", () => control.Cancel()
            ? Results.Json(new { cancelled = true }, json)
            : Results.NotFound(new { error = "no active run" })).RequireHost(hosts);
        app.MapGet("/test/runs", () => Results.Json(control.ListRuns(), json)).RequireHost(hosts);
    }
}
```
In `WallpaperSyncServerHost`: add `using System.Net;` and `using WaBiBaBuSy.Core.Services.Testing;`, then
```csharp
    /// <summary>Test control API; mapped only when set and <see cref="TestControlPort"/> &gt; 0.</summary>
    public ITestRunControl? TestControl { get; set; }
    public int TestControlPort { get; set; }
```
In `StartAsync`, inside `ConfigureKestrel(options => { … })` after the gRPC `ListenAnyIP`:
```csharp
                if (TestControl != null && TestControlPort > 0)
                    options.Listen(IPAddress.Loopback, TestControlPort, lo => lo.Protocols = HttpProtocols.Http1);
```
and after `app.MapGrpcService<WallpaperSyncService>();`:
```csharp
            if (TestControl != null && TestControlPort > 0)
            {
                TestControlEndpoints.Map(app, TestControl, TestControlPort);
                _logger.LogInformation("Test control API on http://127.0.0.1:{Port}", TestControlPort);
            }
```
In `WaBiBaBuSyService`:
```csharp
    /// <summary>Set by the UI; exposed as the localhost control API when EnableTestMode is on.</summary>
    public ITestRunControl? TestRunControl { get; set; }
```
and in `StartServerAsync`, after `_serverHost = new WallpaperSyncServerHost(…);` and before `await _serverHost.StartAsync();`:
```csharp
            if (_serverConfig.EnableTestMode)
            {
                _serverHost.TestControl = TestRunControl;
                _serverHost.TestControlPort = _serverConfig.TestControlPort;
            }
```

- [ ] **Step 4: Wire the coordinator in the tray (app lifetime)**

`TrayViewModel`: add a field and create it at the end of the constructor:
```csharp
    private readonly TestRunCoordinator _testCoordinator;
```
```csharp
        _testCoordinator = new TestRunCoordinator(
            () => _mainWindow?.DataContext is MainWindowViewModel vm ? new TestHostAdapter(vm) : null,
            () => _service.ServerSyncService is { } sync ? new ServerTestChannel(sync) : null,
            AppLogger.Factory);
        _service.TestRunControl = _testCoordinator;
```
In `ShowWindow()`, where the `MainWindowViewModel` is created, set `TestCoordinator = _testCoordinator` in the initializer:
```csharp
                DataContext = new MainWindowViewModel(_service) { TestCoordinator = _testCoordinator }
```
Refactor `Exit()` into a shared method so the CLI can pass an exit code:
```csharp
    [RelayCommand]
    private Task Exit() => ExitAsync(0);

    private async Task ExitAsync(int exitCode)
    {
        _testCoordinator.Cancel();
        if (_mainWindow?.DataContext is MainWindowViewModel mainViewModel)
            mainViewModel.Cleanup();
        await _service.StopServerAsync();
        await _service.DisconnectFromServerAsync();
        _mainWindow?.Close();
        _desktop.Shutdown(exitCode);
    }
```
Add the command-line entry:
```csharp
    /// <summary>
    /// --test-run: open the control panel, start the server, wait for the scenario's remote nodes
    /// (120 s), run, print the verdict; with --exit quit with 0 (no failure), 1 (a step failed), 2 (aborted).
    /// </summary>
    public async Task RunTestFromCommandLineAsync(string scenarioPath, bool exitWhenDone)
    {
        int exitCode = 2;
        try
        {
            var scenario = ScenarioLoader.Load(scenarioPath);
            ShowWindow();
            if (!_service.IsServerRunning) await _service.StartServerAsync();
            var vm = (MainWindowViewModel)_mainWindow!.DataContext!;

            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (true)
            {
                var nodes = await vm.TestNodesAsync();
                int remotes = nodes.Count(n => !n.IsLocal && n.Connected);
                if (remotes >= scenario.Requires.MinRemoteNodes && nodes.Any(n => n.IsLocal)) break;
                if (DateTime.UtcNow > deadline)
                    throw new InvalidOperationException($"only {remotes} of {scenario.Requires.MinRemoteNodes} remote node(s) connected after 120 s");
                await Task.Delay(1000);
            }

            var report = await _testCoordinator.StartAsync(scenarioPath);
            exitCode = report.Aborted ? 2 : report.Verdict == Verdict.Fail ? 1 : 0;
            Console.WriteLine($"Test run {report.Verdict.ToString().ToLowerInvariant()}: {Path.Combine(report.ResultsDirectory, "report.html")}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Test run could not run: {ex.Message}");
        }
        if (exitWhenDone) await ExitAsync(exitCode);
    }
```
(usings: `WaBiBaBuSy.Core.Services.Testing`, `WaBiBaBuSy.Models.Testing`, `WaBiBaBuSy.UI.Services.Testing`.)

- [ ] **Step 5: CLI parsing + app start**

`WaBiBaBuSy.UI/StartupOptions.cs`:
```csharp
namespace WaBiBaBuSy.UI;

/// <summary>Command-line switches: <c>--test-run &lt;scenario.json&gt; [--exit]</c>.</summary>
public sealed class StartupOptions
{
    public string? TestRunPath { get; init; }
    public bool ExitWhenDone { get; init; }

    public static StartupOptions Current { get; private set; } = new();

    public static StartupOptions Parse(string[] args)
    {
        string? path = null;
        bool exit = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--test-run", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) path = args[++i];
            else if (args[i].Equals("--exit", StringComparison.OrdinalIgnoreCase)) exit = true;
        }
        Current = new StartupOptions { TestRunPath = path, ExitWhenDone = exit };
        return Current;
    }
}
```
`Program.Main`: `public static void Main(string[] args) { StartupOptions.Parse(args); BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }`. Change the expression body to a block and keep `[STAThread]`.

`App.OnFrameworkInitializationCompleted`, replace `DataContext = new TrayViewModel(desktop);` with:
```csharp
            var tray = new TrayViewModel(desktop);
            DataContext = tray;
            if (StartupOptions.Current.TestRunPath is { } testRun)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = tray.RunTestFromCommandLineAsync(testRun, StartupOptions.Current.ExitWhenDone));
```

In `MainWindowViewModel.Testing.cs` replace the Task 14 stub `StartTestRunAsync` with:
```csharp
    private async Task StartTestRunAsync(string path)
    {
        if (TestCoordinator == null) return;
        try
        {
            IsTestRunning = true;
            var report = await TestCoordinator.StartAsync(path);
            TestRunStatusText = $"Test: {report.Verdict.ToString().ToLowerInvariant()}{(report.Aborted ? " (aborted)" : "")} — Open last results";
        }
        catch (Exception ex) when (ex is ScenarioException or InvalidOperationException)
        {
            TestRunStatusText = $"Test not started: {ex.Message.Split('\n')[0]}";
        }
        finally
        {
            IsTestRunning = false;
        }
    }
```
and change `CancelTestRun` to `private void CancelTestRun() => TestCoordinator?.Cancel();` (unchanged signature, now the real coordinator).

- [ ] **Step 6: Build + tests**

Run: `dotnet build WaBiBaBuSy.sln -c Debug` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → green.

- [ ] **Step 7: Manual check of the three entry points (server alone)**

With Settings → Server → Developer → Enable test mode on, and the Task 16 scenarios in place (or any scenario with `minRemoteNodes: 0`):
```bash
curl -s -X POST http://127.0.0.1:50052/test/run -H "Content-Type: application/json" -d "{\"scenario\":\"C:/path/to/TestScenarios/smoke-local.json\"}"
```
→ `202 {"started":true,…}`. `curl -s http://127.0.0.1:50052/test/status` → `running: true, stepIndex …`. A second POST → `409`. `curl -s -X DELETE http://127.0.0.1:50052/test/run` → `{"cancelled":true}`. From another machine, `http://<server-ip>:50052/test/status` must **not** connect (loopback only), and `http://<server-ip>:50051/test/status` must return 404.

- [ ] **Step 8: Commit**

```bash
git add WaBiBaBuSy.Core WaBiBaBuSy.UI WaBiBaBuSy.Tests/TestRunCoordinatorTests.cs
git commit -m "feat(test-mode): run coordinator, --test-run CLI, localhost control API" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 16: Bundled scenarios, marker asset, docs, first real runs

**Files:**
- Create: `tools/make-test-marker.ps1`, `TestScenarios/assets/marker.png` (generated), `TestScenarios/README.md`
- Create: `TestScenarios/smoke-local.json`, `sync-basic.json`, `parity-matrix.json`, `clock-skew.json`
- Create: `TestScenarios/scenes/*.json` (listed below)
- Modify: `WaBiBaBuSy.UI/WaBiBaBuSy.UI.csproj` (copy `TestScenarios/**` to the output)
- Modify: `.docs/plans/2026-09-30-automated-test-mode-design.md` (status, §5.1 metric sign, §5.4 no text in the strip), `.docs/plans/2026-09-28-gui-and-e2e-test-checklist.md` (pointer), `.docs/RECENT_UPDATES.md`, `CLAUDE.md` (status line of the design link), `graphify-out/GRAPH_REPORT.md` (via `./tools/graphify-update.ps1`)

- [ ] **Step 1: Generate the marker**

`tools/make-test-marker.ps1`:
```powershell
# Generates TestScenarios/assets/marker.png: 64x64, 4 px black border, magenta #FF00FF interior,
# white 12x12 dot at x 40-51 / y 26-37 (right of center -> "Normal" orientation for MarkerDetector).
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\TestScenarios\assets\marker.png'
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$bmp = New-Object System.Drawing.Bitmap 64, 64
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::Black)
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 0, 255))), 4, 4, 56, 56)
$g.FillRectangle([System.Drawing.Brushes]::White, 40, 26, 12, 12)
$g.Dispose()
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "Wrote $out"
```
Run: `pwsh ./tools/make-test-marker.ps1` → `TestScenarios/assets/marker.png` exists.

- [ ] **Step 2: Scenes**

All scenes: solid `#202020` background, the marker at native size (`TargetHeight` 64, `FitMode` `Center`), no color grading unless stated. Write these files:

`TestScenarios/scenes/linear-sequential.json`:
```json
{
  "Background": { "Mode": "SolidColor", "ColorHex": "#202020" },
  "Animation": { "AnimationPath": "../assets/marker.png", "TargetHeight": 64, "FitMode": "Center", "Loop": true },
  "Movement": { "Type": "Linear", "SpeedPixelsPerSecond": 400 },
  "DistributionMode": "Sequential"
}
```
`TestScenarios/scenes/static-simultaneous.json`: same, with `"Movement": { "Type": "Static" }` and `"DistributionMode": "Simultaneous"`.
`TestScenarios/scenes/sine-reversed.json`: same as linear, with `"Movement": { "Type": "SineWave", "SpeedPixelsPerSecond": 300, "WaveAmplitudePixels": 150, "WaveFrequencyHz": 0.5, "Reversed": true }`.
`TestScenarios/scenes/simultaneous-wave.json`: `"Movement": { "Type": "Bounce", "SpeedPixelsPerSecond": 500, "NodePhaseDelayMs": 250 }`, `"DistributionMode": "Simultaneous"`.
`TestScenarios/scenes/pattern-traveling.json`:
```json
{
  "Background": { "Mode": "SolidColor", "ColorHex": "#202020" },
  "Animation": {
    "AnimationPath": "../assets/marker.png", "TargetHeight": 64, "FitMode": "Center", "Loop": true,
    "Pattern": { "Sizing": "Fill", "SpacingX": 40, "SpacingY": 40, "RandomOffsetMaxPx": 12, "RandomRotationMaxDeg": 20, "Seed": 7 },
    "ColorGrading": { "Mode": "TravelingRainbow", "CyclesPerSecond": 0.2, "Seed": 3 }
  },
  "Movement": { "Type": "Linear", "SpeedPixelsPerSecond": 200 },
  "DistributionMode": "Simultaneous"
}
```
`TestScenarios/scenes/iconzone-linear.json`: linear-sequential with `"Background": { "Mode": "IconZone" }`.
`TestScenarios/scenes/face-direction-bounce.json`: linear-sequential with `"Movement": { "Type": "Bounce", "SpeedPixelsPerSecond": 600 }` and `"FaceTravelDirection": true` inside `Animation`.

- [ ] **Step 3: Scenarios**

`TestScenarios/smoke-local.json` (server alone; used for the single-machine check):
```json
{
  "name": "smoke-local",
  "requires": { "minRemoteNodes": 0 },
  "steps": [
    { "type": "testMode", "timecode": true },
    { "type": "playScene", "scene": "scenes/linear-sequential.json", "marker": true },
    { "type": "probe", "label": "clean-start", "at": "start+150ms", "capture": true },
    { "type": "probe", "label": "moving", "at": "start+3000ms", "capture": true },
    { "type": "probeSeries", "label": "30s", "everyMs": 5000, "forMs": 30000, "perf": true },
    { "type": "playScene", "scene": "scenes/pattern-traveling.json" },
    { "type": "exactFrame", "label": "pattern-parity", "elapsedMs": 12345 },
    { "type": "stop" }
  ]
}
```
`TestScenarios/sync-basic.json`:
```json
{
  "name": "sync-basic",
  "requires": { "minRemoteNodes": 1 },
  "steps": [
    { "type": "testMode", "timecode": true },
    { "type": "playScene", "scene": "scenes/linear-sequential.json", "marker": true },
    { "type": "probe", "label": "clean-start", "at": "start+150ms", "capture": true },
    { "type": "probe", "label": "crossing", "at": "start+5000ms", "capture": true },
    { "type": "probeSeries", "label": "10-minute run", "everyMs": 10000, "forMs": 600000, "perf": true },
    { "type": "stop" }
  ]
}
```
`TestScenarios/parity-matrix.json`:
```json
{
  "name": "parity-matrix",
  "requires": { "minRemoteNodes": 1 },
  "steps": [
    { "type": "testMode", "timecode": true },
    { "type": "playScene", "label": "linear sequential", "scene": "scenes/linear-sequential.json", "marker": true },
    { "type": "probeSeries", "label": "linear positions", "everyMs": 1500, "forMs": 15000, "capture": true, "perf": false },
    { "type": "playScene", "label": "sine reversed", "scene": "scenes/sine-reversed.json", "marker": true },
    { "type": "probeSeries", "label": "sine positions", "everyMs": 1500, "forMs": 15000, "capture": true, "perf": false },
    { "type": "playScene", "label": "static simultaneous", "scene": "scenes/static-simultaneous.json", "marker": true },
    { "type": "probe", "label": "static centered", "at": "start+1000ms", "capture": true },
    { "type": "exactFrame", "label": "static parity", "elapsedMs": 4000 },
    { "type": "playScene", "label": "wave", "scene": "scenes/simultaneous-wave.json", "marker": true },
    { "type": "probeSeries", "label": "wave positions", "everyMs": 1000, "forMs": 8000, "capture": true, "perf": false },
    { "type": "playScene", "label": "pattern + traveling colors", "scene": "scenes/pattern-traveling.json" },
    { "type": "exactFrame", "label": "pattern parity", "elapsedMs": 12345 },
    { "type": "exactFrame", "label": "pattern parity later", "elapsedMs": 98765 },
    { "type": "playScene", "label": "face direction", "scene": "scenes/face-direction-bounce.json", "marker": true },
    { "type": "probeSeries", "label": "facing", "everyMs": 1000, "forMs": 10000, "capture": true, "perf": false },
    { "type": "playScene", "label": "icon zone", "scene": "scenes/iconzone-linear.json" },
    { "type": "probeSeries", "label": "icon zone timing", "everyMs": 2000, "forMs": 10000, "capture": true, "perf": false },
    { "type": "stop" }
  ]
}
```
`TestScenarios/clock-skew.json`:
```json
{
  "name": "clock-skew",
  "requires": { "minRemoteNodes": 1 },
  "steps": [
    { "type": "testMode", "timecode": true, "simulatedClockSkewMs": { "REMOTE-HOSTNAME-1": 2000, "REMOTE-HOSTNAME-2": -2000 } },
    { "type": "wait", "label": "re-measure clock offset", "ms": 12000 },
    { "type": "playScene", "scene": "scenes/linear-sequential.json", "marker": true },
    { "type": "probe", "label": "clean-start", "at": "start+150ms", "capture": true },
    { "type": "probeSeries", "label": "skewed run", "everyMs": 5000, "forMs": 60000, "capture": false, "perf": false },
    { "type": "testMode", "label": "skew off", "timecode": false },
    { "type": "stop" }
  ]
}
```
`TestScenarios/README.md`: explain in ~25 lines:
- How to run: Developer tools / `--test-run <file> --exit` / `curl` examples.
- Clients must open their window and Connect, and have "Allow test runs" on.
- Room settings (Ring / Snake, facing rows, Physical units) come from the room, not from the scene. Set them before `parity-matrix`, and run it twice to cover both pixel mode and Physical units.
- Replace `REMOTE-HOSTNAME-n` in `clock-skew.json` with the client names shown on the tiles.
- Results location, and what `report.html` shows.

- [ ] **Step 4: Ship the scenarios with the app**

In `WaBiBaBuSy.UI/WaBiBaBuSy.UI.csproj` add:
```xml
  <ItemGroup>
    <None Include="..\TestScenarios\**\*" LinkBase="TestScenarios" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```
Run: `dotnet build WaBiBaBuSy.UI -c Debug` → `WaBiBaBuSy.UI\bin\Debug\net9.0-windows\TestScenarios\sync-basic.json` exists.

- [ ] **Step 5: Unit check that every bundled scenario parses**

Append to `WaBiBaBuSy.Tests/ScenarioLoaderTests.cs`:
```csharp
    [Theory]
    [InlineData("smoke-local.json")]
    [InlineData("sync-basic.json")]
    [InlineData("parity-matrix.json")]
    [InlineData("clock-skew.json")]
    public void BundledScenario_IsValid(string file)
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "TestScenarios"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var scenario = ScenarioLoader.Load(Path.Combine(root!, "TestScenarios", file));
        foreach (var play in scenario.Steps.OfType<PlaySceneStep>())
            SceneFile.Load(ScenarioLoader.ResolvePath(scenario, play.Scene));   // every scene + asset resolves
    }
```
Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~BundledScenario_IsValid` → 4 passed.

- [ ] **Step 6: Single-machine end-to-end (server alone)**

Enable test mode in Settings, then:
```bash
dotnet run --project WaBiBaBuSy.UI -- --test-run TestScenarios/smoke-local.json --exit
```
Expected:
- The console prints `Test run pass: …\report.html` and the process exits with code 0 (`echo $LASTEXITCODE` in PowerShell).
- The report shows every server monitor as a node, the timecode strip visible in the captures, marker boxes where expected, a drift spread < 25 ms, and pattern parity Pass (only if the server has two monitors with the same resolution; otherwise Skipped).

If anything is red, **stop**. Treat it with superpowers:systematic-debugging before any multi-machine run.

- [ ] **Step 7: Docs**

- **Design doc:** set the status to "implemented 2026-MM-DD (spec 1)".
- **Design doc §5.1:** replace the error formula with `error_i = renderedElapsedMs_i + phaseMs_i − (presentServerUtcMs_i − sharedStartServerUtcMs)` (phase is added back; `renderedElapsedMs` is already phase-shifted).
- **Design doc §5.4:** "binary strip only; the number as text needs DirectWrite, which the player does not use yet (deferred)".
- **Checklist (`2026-09-28-gui-and-e2e-test-checklist.md`):** add a line under "How to use": "§3–§4 timing and parity items: run `TestScenarios/sync-basic.json`, `parity-matrix.json`, `clock-skew.json` (see `TestScenarios/README.md`) and attach `report.html`."
- **`.docs/RECENT_UPDATES.md`:** a new entry dated today covering the test mode plus the two fixes (View logs, remote thumbnails).
- **`CLAUDE.md`:** change the design link's "(spec 1 of 2, design approved)" to "(spec 1 implemented; spec 2 = show reliability + failure injection)".
- **Graph:** run `./tools/graphify-update.ps1`.

- [ ] **Step 8: Commit**

```bash
git add tools/make-test-marker.ps1 TestScenarios WaBiBaBuSy.UI/WaBiBaBuSy.UI.csproj WaBiBaBuSy.Tests/ScenarioLoaderTests.cs .docs CLAUDE.md graphify-out/GRAPH_REPORT.md
git commit -m "feat(test-mode): bundled scenarios + marker asset; docs" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 9: First multi-machine run (with the user)**

This needs the user's machines. Hand over the steps instead of doing them:
1. Clients: same build, window open → Connect, "Allow test runs" on.
2. Server: `--test-run TestScenarios/sync-basic.json` (about 11 min), then `parity-matrix.json`, then `clock-skew.json` with the host names filled in.
3. The results folders are the input for the optimization round.

---

## Self-Review Notes (plan author)

- **Spec coverage:**
  - §2 → Tasks 1–2.
  - §3 architecture → Tasks 4, 7–12, 14–15.
  - §3.1 gating / entry points → Tasks 10 (client flag), 14 (server flag, menu), 15 (CLI, API).
  - §4 scenario format → Task 3.
  - §5.1 → Tasks 4, 7.
  - §5.2 → Tasks 5, 6, 12.
  - §5.3 → Tasks 6, 8, 12.
  - §5.4 → Tasks 6, 7.
  - §5.5 → Tasks 7, 10.
  - §5.6 → Tasks 10, 12.
  - §6 run flow → Task 12.
  - §7 results → Tasks 12, 13.
  - §8 error handling → Task 12 (timeouts, missing, cancel), Task 9 (player errors).
  - §10 testing → the unit tests across tasks, plus Task 16 Steps 5–6.
  - §11 scenarios → Task 16.
- **Deviations from the spec, all documented in Task 16 Step 7:**
  - The §5.1 phase sign is corrected.
  - The timecode strip has no text.
  - Room-level settings (Ring, facing, physical units) are configured in the room, not per scenario.
  - The spec's "restore the scene" restores only a cross-screen scene, not a running playlist.


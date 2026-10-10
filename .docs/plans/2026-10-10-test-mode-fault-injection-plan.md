# Test Mode Spec 2a — Fault Injection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A scenario step `fault` that makes a remote client lose its link (`drop`) or go quiet (`silent`) and measures reconnect / sweep time, plus the two session bugs those faults expose.

**Architecture:** The server sends a new `TEST_FAULT` command; the client simulates the fault inside `WallpaperSyncClient` (real teardown + real backoff for `drop`, suppressed heartbeats / responses / thumbnails for `silent`). The runner polls the server's own client registry through `ITestTransport.IsRegistered` (no UI refresh lag), evaluates the timeline with a pure function (`FaultEvaluation`) and reports it per step and in a "Faults" table.

**Tech Stack:** .NET 9, C# 12, gRPC (Grpc.AspNetCore / Grpc.Net.Client), xUnit.

**Spec:** `.docs/plans/2026-09-30-automated-test-mode-design.md` §12 (Spec 2a). One refinement over the spec: the runner measures `lostAt` / `backAt` with `ITestTransport.IsRegistered` (the server's registry) instead of `ITestHost.GetNodesAsync` (the room, refreshed by the UI); at the end of the step it waits up to 10 s for the room to agree so the post-step connection check does not record the node as lost. Task 4 updates §12 accordingly.

## Global Constraints

- Proto: `CommandType.TEST_FAULT = 13`; `SyncParameters.test_fault_kind = 36` (string), `test_fault_ms = 37` (int32). Never renumber existing fields.
- Fault kinds are exactly `"drop"` and `"silent"` (lowercase); anything else is refused (client: logged + ignored; scenario: rejected when it loads).
- Client clamps the fault length to 1 000 … 120 000 ms. The runner does not clamp (unit tests use short faults).
- A client with `ClientConfiguration.AllowTestRuns == false` ignores `TEST_FAULT`.
- Verdicts — drop: `reconnectMs = backAt − (sentAt + ms)`; Pass ≤ `maxReconnectMs` (default 5000), Warn ≤ 2×, Fail later / never back / never lost / back before the fault ended. Silent: `sweepMs = lostAt − sentAt`; Pass ≤ `maxSweepMs` (default 45000) and back, Warn swept late but back, Fail never swept / never back.
- Deterministic render paths (`MovementCalculator`, `ColorGrader`, `PatternLayout`, players) are not touched.
- Code style: file-scoped namespaces, nullable on, XML docs on public members, match the surrounding comment density.
- Build: `dotnet build` 0 errors; tests: `dotnet test WaBiBaBuSy.Tests`.
- Commits go directly on `main` (trunk-based), each ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Stale stream ends after a reconnect** — the old `SyncStream` of a client that already re-registered must not remove the new session (real cable pulls hit this: the server notices the dead TCP late). → Task 1 test `OldStreamEndingAfterReconnect_KeepsTheClient`.
2. **Swept but alive client** (sleep, Wi-Fi hiccup > 30 s) must re-register by itself on its next heartbeat. → Task 2 test `SweptClient_ReRegistersOnItsNextHeartbeat`.
3. **Fault on a client with test runs disabled** must do nothing (no surprise disconnect at a party). → Task 2 test `TestRunsDisabled_FaultIsIgnored`.
4. **Unedited placeholder node name** (`REMOTE-HOSTNAME-1`) must fail the step loudly, not silently test nothing. → Task 3 test `FaultOnUnknownNode_FailsTheStep`.
5. **Room lagging behind the server after the node is back** must not turn an expected fault into a "Node failures" entry, and a node that really stays away must still be reported. → Task 3 tests `DropFault_MeasuresReconnect_AndNodeIsNotCountedAsLost` and `NodeNeverBack_FailsTheStep_AndIsRecordedAsLost`.

---

## File Map

| File | Change |
|---|---|
| `WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs` | `SyncStream` `finally`: remove the client only if the ending stream is still the registered one |
| `WaBiBaBuSy.Tests/SyncServiceHarness.cs` | `Connect` returns its `CancellationTokenSource`; new `RegisterAsync` |
| `WaBiBaBuSy.Tests/SyncStreamLifecycleTests.cs` | **new** — stream guard tests |
| `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` | `TEST_FAULT = 13`, fields 36 / 37 |
| `WaBiBaBuSy.Models/Testing/TestFaults.cs` | **new** — kind constants, clamp, `FaultReport`, `FaultEvaluation` |
| `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs` | handle `TEST_FAULT`; connect gate; silence; unacknowledged heartbeat → reconnect |
| `WaBiBaBuSy.Tests/ClientFaultTests.cs` | **new** — loopback integration tests (real server host + real client) |
| `WaBiBaBuSy.Tests/FaultEvaluationTests.cs` | **new** — pure verdict tests |
| `WaBiBaBuSy.Models/Testing/TestScenario.cs` | `FaultStep` |
| `WaBiBaBuSy.Models/Testing/TestRunReport.cs` | `StepReport.Fault` |
| `WaBiBaBuSy.Core/Services/Testing/ITestTransport.cs` | `SendFaultAsync`, `IsRegistered` |
| `WaBiBaBuSy.Core/Services/Testing/ServerTestChannel.cs` | implement both |
| `WaBiBaBuSy.Core/Services/Testing/TestRunnerOptions.cs` | `FaultPollMs`, `FaultHostSyncMs` |
| `WaBiBaBuSy.Core/Services/Testing/TestRunner.cs` | `case FaultStep`, step timeout |
| `WaBiBaBuSy.Tests/TestRunnerTests.cs`, `TestRunCoordinatorTests.cs` | fakes implement the new transport members; fault step tests |
| `WaBiBaBuSy.Core/Services/Testing/TestReportWriter.cs` | "Faults" table |
| `WaBiBaBuSy.Tests/TestReportWriterTests.cs` | Faults table test |
| `TestScenarios/recovery.json`, `TestScenarios/README.md` | new scenario + docs |
| `.docs/…` | design §12 refinement, runbook, changelog, open items |

---

### Task 1: Server keeps a re-registered client when its old stream ends

**Files:**
- Modify: `WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs` (the `finally` of `SyncStream`, ≈ line 376)
- Modify: `WaBiBaBuSy.Tests/SyncServiceHarness.cs`
- Create: `WaBiBaBuSy.Tests/SyncStreamLifecycleTests.cs`

**Interfaces:**
- Produces: `SyncServiceHarness.Connect(string clientId, Action<SyncCommand> onCommand)` now returns `CancellationTokenSource` (existing callers ignore it); `SyncServiceHarness.RegisterAsync(string clientId)` → `Task<RegistrationResponse>`.

- [ ] **Step 1: Extend the harness**

In `SyncServiceHarness.cs` change `Connect` to return its token source and add `RegisterAsync`:

```csharp
    /// <summary>Register a command stream for <paramref name="clientId"/>; every command the server sends goes to <paramref name="onCommand"/>. Cancel the returned source to end that stream.</summary>
    public CancellationTokenSource Connect(string clientId, Action<SyncCommand> onCommand)
    {
        var cts = new CancellationTokenSource();
        _streams.Add(cts);
        // SyncStream registers the stream before its first await, so the client is reachable on return.
        _ = Service.SyncStream(new BlockingReader<SyncResponse>(),
            new CallbackWriter<SyncCommand>(onCommand),
            new FakeServerCallContext(new Metadata { { "client-id", clientId } }, cts.Token));
        return cts;
    }

    /// <summary>The RegisterClient RPC, as a client with a persistent id sends it.</summary>
    public Task<RegistrationResponse> RegisterAsync(string clientId) =>
        Service.RegisterClient(new ClientInfo { ClientId = clientId, Hostname = clientId, ScreenConfig = new ScreenConfiguration() },
            new FakeServerCallContext(new Metadata(), CancellationToken.None));
```

- [ ] **Step 2: Write the failing tests**

Create `WaBiBaBuSy.Tests/SyncStreamLifecycleTests.cs`:

```csharp
using WaBiBaBuSy.Grpc;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Which stream's end removes a client: only the one still registered for it.</summary>
public class SyncStreamLifecycleTests : IDisposable
{
    private readonly SyncServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private bool Listed(string id) => _h.Service.GetConnectedClients().Any(c => c.ClientId == id);

    private static async Task<bool> Eventually(Func<bool> condition, int ms = 2000)
    {
        for (int waited = 0; waited < ms; waited += 20)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task OldStreamEndingAfterReconnect_KeepsTheClient()
    {
        await _h.RegisterAsync("c1");
        var oldStream = _h.Connect("c1", _ => { });
        await _h.RegisterAsync("c1");               // the client reconnected
        _h.Connect("c1", _ => { });                  // with a new stream

        oldStream.Cancel();                          // the dead link is noticed only now
        await Task.Delay(300);

        Assert.True(Listed("c1"));
    }

    [Fact]
    public async Task CurrentStreamEnding_RemovesTheClient()
    {
        await _h.RegisterAsync("c1");
        var stream = _h.Connect("c1", _ => { });

        stream.Cancel();

        Assert.True(await Eventually(() => !Listed("c1")));
    }
}
```

- [ ] **Step 3: Run them to verify the first fails**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SyncStreamLifecycleTests"`
Expected: `OldStreamEndingAfterReconnect_KeepsTheClient` FAILS (the client is removed); `CurrentStreamEnding_RemovesTheClient` passes.

- [ ] **Step 4: Guard the removal**

In `WallpaperSyncService.SyncStream`, replace the `finally` block:

```csharp
        finally
        {
            // Only the stream still registered for this client ends its session. An old stream that dies after
            // the client already reconnected (a pulled cable is noticed late), or after the sweep removed the
            // client, must not remove the new session.
            if (_clientCommandStreams.TryGetValue(clientId, out var current) && ReferenceEquals(current, responseStream))
            {
                RemoveClient(clientId);
                _logger.LogInformation("Client {ClientId} removed from topology after disconnect", clientId);
            }
            else
            {
                _logger.LogInformation("Stale sync stream of client {ClientId} ended; its current session stays", clientId);
            }
        }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SyncStreamLifecycleTests|FullyQualifiedName~WallpaperSyncServiceTestModeTests|FullyQualifiedName~ClientLogUploadTests"`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs WaBiBaBuSy.Tests/SyncServiceHarness.cs WaBiBaBuSy.Tests/SyncStreamLifecycleTests.cs
git commit -m "fix(server): an old sync stream ending after a reconnect no longer removes the new session"
```

---

### Task 2: TEST_FAULT on the client + swept clients re-register

**Files:**
- Modify: `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto`
- Create: `WaBiBaBuSy.Models/Testing/TestFaults.cs` (only the `TestFaults` static class in this task)
- Modify: `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs`
- Create: `WaBiBaBuSy.Tests/ClientFaultTests.cs`

**Interfaces:**
- Produces: `CommandType.TestFault`; `SyncParameters.TestFaultKind` (string), `SyncParameters.TestFaultMs` (int); `TestFaults.Drop = "drop"`, `TestFaults.Silent = "silent"`, `TestFaults.IsKnown(string?) → bool`, `TestFaults.ClampMs(int) → int`.

- [ ] **Step 1: Proto**

In `wabibabusy.proto`, `enum CommandType`, after `EVENT = 12;`:

```proto
  TEST_FAULT = 13;        // Automated test run: simulate a fault on this client (params.test_fault_*). Old clients log "unknown" and ignore it.
```

In `message SyncParameters`, after `log_request_id = 35;`:

```proto
  string test_fault_kind = 36;         // TEST_FAULT: "drop" (link lost for test_fault_ms, then the real reconnect) or "silent" (no heartbeats / responses / thumbnails, connection kept)
  int32 test_fault_ms = 37;            // TEST_FAULT: fault length in ms (the client clamps to 1 000 … 120 000)
```

- [ ] **Step 2: Fault constants**

Create `WaBiBaBuSy.Models/Testing/TestFaults.cs`:

```csharp
namespace WaBiBaBuSy.Models.Testing;

/// <summary>Faults a test run can inject on a remote client (TEST_FAULT, automated test mode design §12).</summary>
public static class TestFaults
{
    /// <summary>The link is lost for the fault length; the client then reconnects through its real backoff.</summary>
    public const string Drop = "drop";
    /// <summary>No heartbeats, sync responses or thumbnails for the fault length; the connection stays open (a powered-off client as the server sees it).</summary>
    public const string Silent = "silent";

    public const int MinMs = 1000;
    public const int MaxMs = 120000;

    public static bool IsKnown(string? kind) => kind is Drop or Silent;

    /// <summary>The fault length a client applies.</summary>
    public static int ClampMs(int ms) => Math.Clamp(ms, MinMs, MaxMs);
}
```

- [ ] **Step 3: Write the failing integration tests**

Create `WaBiBaBuSy.Tests/ClientFaultTests.cs`. They start the real gRPC server on a loopback port and connect a real client (same pattern as `WallpaperSyncServerHostTests`):

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>TEST_FAULT and session recovery, end to end over loopback gRPC (real server host, real client).</summary>
public class ClientFaultTests : IAsyncLifetime
{
    private readonly int _port = FreePort();
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"wbbs-fault-{Guid.NewGuid():N}");
    private WallpaperSyncServerHost _host = null!;
    private WallpaperSyncClient _client = null!;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public async Task InitializeAsync()
    {
        _host = new WallpaperSyncServerHost(NullLogger<WallpaperSyncServerHost>.Instance,
            new ServerConfiguration { Port = _port, EnableAutoDiscovery = false, ContentDirectory = Path.Combine(_cacheDir, "server") })
        { LoopbackOnly = true };
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        try { await _client.DisconnectAsync(); } catch { /* best effort */ }
        await _host.StopAsync();
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> ConnectAsync(bool allowTestRuns = true)
    {
        _client = new WallpaperSyncClient(NullLogger<WallpaperSyncClient>.Instance,
            new ClientConfiguration { CacheDirectory = Path.Combine(_cacheDir, "client"), HeartbeatIntervalSeconds = 1, AllowTestRuns = allowTestRuns });
        Assert.True(await _client.ConnectAsync("127.0.0.1", _port));
        Assert.True(await Eventually(() => Registered(_client.ClientId!), 3000));
        return _client.ClientId!;
    }

    private bool Registered(string id) => _host.SyncService!.GetConnectedClients().Any(c => c.ClientId == id);

    private long LastHeartbeat(string id) => _host.SyncService!.GetConnectedClients().First(c => c.ClientId == id).LastHeartbeat;

    private Task<bool> SendFaultAsync(string id, string kind, int ms) =>
        _host.SyncService!.SendCommandToClientAsync(id, new SyncCommand
        {
            Type = CommandType.TestFault,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Params = new SyncParameters { TestFaultKind = kind, TestFaultMs = ms },
        });

    private static async Task<bool> Eventually(Func<bool> condition, int ms)
    {
        for (int waited = 0; waited < ms; waited += 50)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task SweptClient_ReRegistersOnItsNextHeartbeat()
    {
        var id = await ConnectAsync();

        _host.SyncService!.RemoveClient(id);   // what the dead-client sweep does; the client's connection stays open

        Assert.True(await Eventually(() => Registered(id), 8000), "the swept client never re-registered");
    }

    [Fact]
    public async Task Drop_LosesTheLink_ThenReconnectsAfterTheFault()
    {
        var id = await ConnectAsync();
        var sent = DateTimeOffset.UtcNow;

        Assert.True(await SendFaultAsync(id, TestFaults.Drop, 1500));

        Assert.True(await Eventually(() => !Registered(id), 3000), "the server never lost the client");
        Assert.True(await Eventually(() => Registered(id), 10000), "the client never came back");
        Assert.True((DateTimeOffset.UtcNow - sent).TotalMilliseconds >= 1400, "the client came back before the fault ended");
    }

    [Fact]
    public async Task Silent_StopsHeartbeats_ThenResumes()
    {
        var id = await ConnectAsync();
        Assert.True(await SendFaultAsync(id, TestFaults.Silent, 2500));
        await Task.Delay(300);
        long during = LastHeartbeat(id);

        await Task.Delay(1800);                    // ≥ one heartbeat interval inside the silence
        Assert.Equal(during, LastHeartbeat(id));

        Assert.True(await Eventually(() => LastHeartbeat(id) != during, 4000), "heartbeats never resumed");
        Assert.True(Registered(id));
    }

    [Fact]
    public async Task TestRunsDisabled_FaultIsIgnored()
    {
        var id = await ConnectAsync(allowTestRuns: false);

        Assert.True(await SendFaultAsync(id, TestFaults.Drop, 3000));
        await Task.Delay(2000);

        Assert.True(Registered(id));
        Assert.True(_client.IsConnected);
    }
}
```

- [ ] **Step 4: Run them to verify they fail**

Run: `dotnet build && dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ClientFaultTests"`
Expected: `SweptClient_ReRegistersOnItsNextHeartbeat`, `Drop_…` and `Silent_…` FAIL (no fault handling, unacknowledged heartbeats ignored); `TestRunsDisabled_FaultIsIgnored` passes.

- [ ] **Step 5: Client fields and helpers**

In `WallpaperSyncClient.cs`, next to `_backoff` (≈ line 71):

```csharp
    // TEST_FAULT (automated test mode §12): real UTC ms until which the server is "unreachable" (drop) or this
    // client stays quiet (silent). 0 = no fault. Real time, not NowMs(): the simulated clock skew must not shift it.
    private long _faultUntilUtcMs;
    private long _silentUntilUtcMs;

    private long RealNowMs() => TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    private bool InSilence() => RealNowMs() < Interlocked.Read(ref _silentUntilUtcMs);
```

- [ ] **Step 6: Connect gate**

In `ConnectAsync`, right after the `if (IsConnected) { … return true; }` block:

```csharp
        if (RealNowMs() < Interlocked.Read(ref _faultUntilUtcMs))
        {
            _logger.LogInformation("[Test] Server unreachable (simulated link loss)");
            return false;
        }
```

- [ ] **Step 7: Silence**

At the top of `SendHeartbeatAsync()`, `SendSyncResponseAsync(...)` and `SendThumbnailIfDueAsync()` add:

```csharp
        if (InSilence()) return;   // TEST_FAULT silent: what the server sees from a powered-off client
```

- [ ] **Step 8: Unacknowledged heartbeat → reconnect**

In `SendHeartbeatAsync()`, directly after `var response = await _client.HeartbeatAsync(request);`:

```csharp
        if (!response.Acknowledged)
        {
            // The server no longer lists us (dead-client sweep after a sleep / Wi-Fi hiccup, or a server restart)
            // while our connection survived: without a new registration we would stay invisible until restarted.
            _logger.LogWarning("Server no longer lists this client — re-registering");
            TriggerReconnect("server no longer lists this client");
            return;
        }
```

- [ ] **Step 9: Handle TEST_FAULT**

In the sync stream loop, after the `CommandType.TestProbe` block (≈ line 1105):

```csharp
                    if (command.Type == CommandType.TestFault)
                    {
                        ApplyTestFault(command.Params ?? new SyncParameters());
                        continue;
                    }
```

and next to `ApplyTestMode`:

```csharp
    /// <summary>
    /// TEST_FAULT: drop = tear the link down as if the cable came out and refuse to reach the server for the fault
    /// length, so the reconnect runs through the real backoff and session resume; silent = no heartbeats, sync
    /// responses or thumbnails for the fault length while the connection stays open.
    /// </summary>
    private void ApplyTestFault(SyncParameters p)
    {
        if (!_configuration.AllowTestRuns)
        {
            _logger.LogWarning("Ignoring TEST_FAULT: test runs are disabled on this client");
            return;
        }
        if (!TestFaults.IsKnown(p.TestFaultKind))
        {
            _logger.LogWarning("Ignoring TEST_FAULT: unknown kind '{Kind}'", p.TestFaultKind);
            return;
        }

        int ms = TestFaults.ClampMs(p.TestFaultMs);
        long until = RealNowMs() + ms;
        if (p.TestFaultKind == TestFaults.Drop)
        {
            Interlocked.Exchange(ref _faultUntilUtcMs, until);
            _logger.LogWarning("[Test] Simulated link loss for {Ms} ms", ms);
            // Off the stream loop: the teardown cancels and waits for this very loop.
            _ = Task.Run(async () =>
            {
                await TeardownChannelAsync();
                TriggerReconnect("simulated link loss");
            });
        }
        else
        {
            Interlocked.Exchange(ref _silentUntilUtcMs, until);
            _logger.LogWarning("[Test] Simulated silence for {Ms} ms (no heartbeats, responses or thumbnails)", ms);
        }
    }
```

Add `using WaBiBaBuSy.Models.Testing;` if the file does not have it yet (it already uses `TestModeErrors`, so it should).

- [ ] **Step 10: Run the tests**

Run: `dotnet build && dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ClientFaultTests"`
Expected: all 4 PASS. If `Drop_…` is flaky on timing, read the client log via a `TestOutputHelper` logger before loosening any bound.

- [ ] **Step 11: Full test run**

Run: `dotnet test WaBiBaBuSy.Tests`
Expected: all PASS (867 before this plan + the new ones).

- [ ] **Step 12: Commit**

```bash
git add WaBiBaBuSy.Grpc/Protos/wabibabusy.proto WaBiBaBuSy.Models/Testing/TestFaults.cs WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs WaBiBaBuSy.Tests/ClientFaultTests.cs
git commit -m "feat(test-mode): TEST_FAULT drop / silent on the client; swept clients re-register"
```

---

### Task 3: Runner `fault` step

**Files:**
- Modify: `WaBiBaBuSy.Models/Testing/TestFaults.cs` (add `FaultReport`, `FaultEvaluation`)
- Modify: `WaBiBaBuSy.Models/Testing/TestScenario.cs` (`FaultStep`), `WaBiBaBuSy.Models/Testing/ScenarioLoader.cs` (validation)
- Modify: `WaBiBaBuSy.Models/Testing/TestRunReport.cs` (`StepReport.Fault`)
- Modify: `WaBiBaBuSy.Core/Services/Testing/ITestTransport.cs`, `ServerTestChannel.cs`, `TestRunnerOptions.cs`, `TestRunner.cs`
- Modify: `WaBiBaBuSy.Tests/TestRunnerTests.cs`, `WaBiBaBuSy.Tests/TestRunCoordinatorTests.cs`
- Create: `WaBiBaBuSy.Tests/FaultEvaluationTests.cs`

**Interfaces:**
- Consumes: `TestFaults.Drop/Silent/IsKnown`, `CommandType.TestFault`, `SyncParameters.TestFaultKind/TestFaultMs` (Task 2).
- Produces: `FaultReport` (fields below), `FaultEvaluation.Evaluate(FaultReport r, int maxReconnectMs, int maxSweepMs) → void` (fills `ReconnectMs`, `SweepMs`, `Verdict`, `Message`), `StepReport.Fault` (`FaultReport?`), `ITestTransport.SendFaultAsync(string clientId, string kind, int ms) → Task<bool>`, `ITestTransport.IsRegistered(string clientId) → bool`.

- [ ] **Step 1: Write the failing evaluation tests**

Create `WaBiBaBuSy.Tests/FaultEvaluationTests.cs`:

```csharp
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class FaultEvaluationTests
{
    private static FaultReport Drop(long? lost, long? back) =>
        new() { Kind = TestFaults.Drop, Ms = 10000, SentAtUtcMs = 0, LostAtUtcMs = lost, BackAtUtcMs = back };

    private static FaultReport Silent(long? lost, long? back) =>
        new() { Kind = TestFaults.Silent, Ms = 45000, SentAtUtcMs = 0, LostAtUtcMs = lost, BackAtUtcMs = back };

    [Theory]
    [InlineData(14000L, Verdict.Pass, 4000L)]
    [InlineData(15000L, Verdict.Pass, 5000L)]
    [InlineData(19000L, Verdict.Warn, 9000L)]
    [InlineData(21000L, Verdict.Fail, 11000L)]
    public void Drop_ReconnectTimeAfterTheFault(long back, Verdict verdict, long reconnectMs)
    {
        var r = Drop(lost: 50, back: back);
        FaultEvaluation.Evaluate(r, maxReconnectMs: 5000, maxSweepMs: 45000);
        Assert.Equal(verdict, r.Verdict);
        Assert.Equal(reconnectMs, r.ReconnectMs);
    }

    [Fact]
    public void Drop_NeverLost_Fails()
    {
        var r = Drop(lost: null, back: null);
        FaultEvaluation.Evaluate(r, 5000, 45000);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains("never lost", r.Message);
    }

    [Fact]
    public void Drop_NeverBack_Fails()
    {
        var r = Drop(lost: 50, back: null);
        FaultEvaluation.Evaluate(r, 5000, 45000);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains("never came back", r.Message);
    }

    [Fact]
    public void Drop_BackBeforeTheFaultEnded_Fails()
    {
        var r = Drop(lost: 50, back: 3000);
        FaultEvaluation.Evaluate(r, 5000, 45000);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains("before the fault ended", r.Message);
    }

    [Theory]
    [InlineData(38000L, Verdict.Pass)]
    [InlineData(52000L, Verdict.Warn)]
    public void Silent_SweepTime(long lost, Verdict verdict)
    {
        var r = Silent(lost: lost, back: 47000 + lost);
        FaultEvaluation.Evaluate(r, 5000, maxSweepMs: 45000);
        Assert.Equal(verdict, r.Verdict);
        Assert.Equal(lost, r.SweepMs);
    }

    [Fact]
    public void Silent_NeverSwept_Fails()
    {
        var r = Silent(lost: null, back: null);
        FaultEvaluation.Evaluate(r, 5000, 45000);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains("never dropped", r.Message);
    }

    [Fact]
    public void Silent_SweptButNeverBack_Fails()
    {
        var r = Silent(lost: 38000, back: null);
        FaultEvaluation.Evaluate(r, 5000, 45000);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains("never came back", r.Message);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~FaultEvaluationTests"`
Expected: build error (`FaultReport` / `FaultEvaluation` not defined).

- [ ] **Step 3: Implement `FaultReport` and `FaultEvaluation`**

Append to `WaBiBaBuSy.Models/Testing/TestFaults.cs`:

```csharp
/// <summary>One injected fault as the runner saw it from the server (UTC ms on the server clock).</summary>
public sealed class FaultReport
{
    public string NodeId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Ms { get; set; }
    public long SentAtUtcMs { get; set; }
    /// <summary>First poll at which the server no longer listed the client.</summary>
    public long? LostAtUtcMs { get; set; }
    /// <summary>First poll after <see cref="LostAtUtcMs"/> at which the server listed the client again.</summary>
    public long? BackAtUtcMs { get; set; }
    /// <summary>drop: back − (sent + fault length).</summary>
    public long? ReconnectMs { get; set; }
    /// <summary>silent: lost − sent.</summary>
    public long? SweepMs { get; set; }
    public Verdict Verdict { get; set; } = Verdict.Skipped;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Verdict of a fault from its timeline (design §12): pure, so the thresholds are unit-tested.</summary>
public static class FaultEvaluation
{
    public static void Evaluate(FaultReport r, int maxReconnectMs, int maxSweepMs)
    {
        if (r.Kind == TestFaults.Drop)
        {
            if (r.LostAtUtcMs == null) { Set(r, Verdict.Fail, "the server never lost the node (fault not applied?)"); return; }
            if (r.BackAtUtcMs == null) { Set(r, Verdict.Fail, $"lost after {r.LostAtUtcMs - r.SentAtUtcMs} ms, never came back"); return; }
            long reconnect = r.BackAtUtcMs.Value - (r.SentAtUtcMs + r.Ms);
            if (reconnect < 0) { Set(r, Verdict.Fail, $"back {-reconnect} ms before the fault ended (fault not applied?)"); return; }
            r.ReconnectMs = reconnect;
            var v = reconnect <= maxReconnectMs ? Verdict.Pass : reconnect <= 2L * maxReconnectMs ? Verdict.Warn : Verdict.Fail;
            Set(r, v, $"reconnected {reconnect} ms after the link came back (limit {maxReconnectMs} ms)");
        }
        else
        {
            if (r.LostAtUtcMs == null) { Set(r, Verdict.Fail, "the server never dropped the silent node"); return; }
            long sweep = r.LostAtUtcMs.Value - r.SentAtUtcMs;
            r.SweepMs = sweep;
            if (r.BackAtUtcMs == null) { Set(r, Verdict.Fail, $"dropped after {sweep} ms, never came back after the silence"); return; }
            var v = sweep <= maxSweepMs ? Verdict.Pass : Verdict.Warn;
            Set(r, v, $"dropped after {sweep} ms (limit {maxSweepMs} ms), back {r.BackAtUtcMs - r.SentAtUtcMs} ms after the fault started");
        }
    }

    private static void Set(FaultReport r, Verdict v, string message)
    {
        r.Verdict = v;
        r.Message = message;
    }
}
```

- [ ] **Step 4: Run the evaluation tests**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~FaultEvaluationTests"`
Expected: all PASS.

- [ ] **Step 5: Step model, report field, transport members, options**

`TestScenario.cs` — add `[JsonDerivedType(typeof(FaultStep), "fault")]` to the attribute list on `TestStep`, and the class after `ShowEventStep`. The fault kind is the property `Fault` (JSON `"fault": "drop"`), because `TestStep.Kind` is already the step's type name:

```csharp
/// <summary>Inject a fault on one remote and measure the recovery (design §12).</summary>
public sealed class FaultStep : TestStep
{
    /// <summary>Tile name (case-insensitive) or client id of a remote node.</summary>
    public string Node { get; set; } = string.Empty;
    /// <summary><c>drop</c> or <c>silent</c> (<see cref="TestFaults"/>).</summary>
    public string Fault { get; set; } = TestFaults.Drop;
    public int Ms { get; set; } = 10000;
    public int MaxReconnectMs { get; set; } = 5000;
    public int MaxSweepMs { get; set; } = 45000;
    /// <summary>How long the runner watches the node before giving up.</summary>
    [JsonIgnore] public int WatchMs => Fault == TestFaults.Silent ? Math.Max(Ms, 2 * MaxSweepMs) + 10000 : Ms + 2 * MaxReconnectMs + 10000;
    public override string Kind => "fault";
}
```

The scenario JSON is therefore `{ "type": "fault", "node": "…", "fault": "drop", "ms": 10000, "maxReconnectMs": 5000 }`. Task 4 updates design §12 and the README to this spelling.

`TestRunReport.cs` — in `StepReport`, after `Probes`:

```csharp
    /// <summary>The fault a <c>fault</c> step injected and what the server saw.</summary>
    public FaultReport? Fault { get; set; }
```

`ITestTransport.cs` — add:

```csharp
    /// <summary>Send TEST_FAULT; false when the client has no command stream.</summary>
    Task<bool> SendFaultAsync(string clientId, string kind, int ms);

    /// <summary>Whether the server's registry lists the client right now (no UI refresh in between).</summary>
    bool IsRegistered(string clientId);
```

`ServerTestChannel.cs` — after `SendTestModeAsync`:

```csharp
    public Task<bool> SendFaultAsync(string clientId, string kind, int ms) =>
        _sync.SendCommandToClientAsync(clientId, new SyncCommand
        {
            Type = CommandType.TestFault,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Params = new SyncParameters { TestFaultKind = kind, TestFaultMs = ms },
        });

    public bool IsRegistered(string clientId) => _sync.GetConnectedClients().Any(c => c.ClientId == clientId);
```

`TestRunnerOptions.cs` — add:

```csharp
    /// <summary>How often a fault step checks whether the server lists the node.</summary>
    public int FaultPollMs { get; set; } = 250;
    /// <summary>After a fault, how long to wait for the room (the node list) to show the node again.</summary>
    public int FaultHostSyncMs { get; set; } = 10000;
```

`TestRunCoordinatorTests.cs` — `NoTransport` gets:

```csharp
        public Task<bool> SendFaultAsync(string c, string k, int ms) => Task.FromResult(true);
        public bool IsRegistered(string c) => true;
```

- [ ] **Step 6: Fakes for the runner tests**

In `TestRunnerTests.cs`:

`FakeHost` — add:

```csharp
        public void SetConnected(string nodeId, bool connected) => _nodes.Single(n => n.NodeId == nodeId).Connected = connected;
```

`FakeTransport` — add:

```csharp
        public List<(string ClientId, string Kind, int Ms)> Faults { get; } = new();
        /// <summary>Called on SendFaultAsync (the test simulates the client's reaction).</summary>
        public Action<string, string, int>? OnFault { get; set; }
        public HashSet<string> Unregistered { get; } = new();

        public Task<bool> SendFaultAsync(string clientId, string kind, int ms)
        {
            Faults.Add((clientId, kind, ms));
            OnFault?.Invoke(clientId, kind, ms);
            return Task.FromResult(true);
        }

        public bool IsRegistered(string clientId)
        {
            lock (Unregistered) return !Unregistered.Contains(clientId);
        }
```

and in the test class a helper that simulates a client being gone from `goneAfterMs` to `backAfterMs` (null = never back), in both the server registry and the room:

```csharp
    private void SimulateOutage(int goneAfterMs, int? backAfterMs, int roomLagMs = 0)
    {
        _transport.OnFault = (id, _, _) => _ = Task.Run(async () =>
        {
            await Task.Delay(goneAfterMs);
            lock (_transport.Unregistered) _transport.Unregistered.Add(id);
            _host.SetConnected(id, false);
            if (backAfterMs == null) return;
            await Task.Delay(backAfterMs.Value - goneAfterMs);
            lock (_transport.Unregistered) _transport.Unregistered.Remove(id);
            await Task.Delay(roomLagMs);
            _host.SetConnected(id, true);
        });
    }
```

- [ ] **Step 7: Write the failing runner tests**

Add to `TestRunnerTests.cs`:

```csharp
    private TestRunner FaultRunner() => Runner(o => { o.FaultPollMs = 10; o.FaultHostSyncMs = 1000; });

    [Fact]
    public async Task DropFault_MeasuresReconnect_AndNodeIsNotCountedAsLost()
    {
        SimulateOutage(goneAfterMs: 20, backAfterMs: 250, roomLagMs: 100);   // fault 200 ms → back ≈ 50 ms after it
        var report = await FaultRunner().RunAsync(Scenario($$"""
            { "type": "testMode", "timecode": true },
            {{PlayMarker}},
            { "type": "fault", "label": "drop", "node": "PC-02", "fault": "drop", "ms": 200, "maxReconnectMs": 500 },
            { "type": "probe", "label": "after", "at": "now+30ms", "capture": false },
            { "type": "stop" }
            """), "unit.json", CancellationToken.None);

        var step = report.Steps[2];
        Assert.Equal(Verdict.Pass, step.Verdict);
        Assert.NotNull(step.Fault);
        Assert.Equal("client-1", step.Fault!.NodeId);
        Assert.InRange(step.Fault.ReconnectMs!.Value, 0, 500);
        Assert.Equal(("client-1", "drop", 200), _transport.Faults.Single());
        Assert.Empty(report.NodeFailures);                                   // expected outage, not a node failure
        Assert.Equal(2, _transport.Modes.Count(m => m.Item1));               // test mode re-sent after the reconnect
        Assert.Contains(report.Steps[3].Probes[0].Samples, s => s.NodeId == "client-1");   // probed again afterwards
    }

    [Fact]
    public async Task SilentFault_MeasuresTheSweep()
    {
        SimulateOutage(goneAfterMs: 150, backAfterMs: 300);
        var report = await FaultRunner().RunAsync(Scenario($$"""
            {{PlayMarker}},
            { "type": "fault", "label": "silent", "node": "client-1", "fault": "silent", "ms": 200, "maxSweepMs": 400 },
            { "type": "stop" }
            """), "unit.json", CancellationToken.None);

        var fault = report.Steps[1].Fault!;
        Assert.Equal(Verdict.Pass, fault.Verdict);
        Assert.InRange(fault.SweepMs!.Value, 100, 400);
        Assert.Empty(report.NodeFailures);
    }

    [Fact]
    public async Task NodeNeverBack_FailsTheStep_AndIsRecordedAsLost()
    {
        SimulateOutage(goneAfterMs: 20, backAfterMs: null);
        var report = await FaultRunner().RunAsync(Scenario($$"""
            {{PlayMarker}},
            { "type": "fault", "label": "drop", "node": "pc-02", "fault": "drop", "ms": 100, "maxReconnectMs": 100 },
            { "type": "stop" }
            """), "unit.json", CancellationToken.None);

        Assert.Equal(Verdict.Fail, report.Steps[1].Verdict);
        Assert.Contains("never came back", report.Steps[1].Message);
        Assert.Single(report.NodeFailures);
    }

    [Fact]
    public async Task FaultOnUnknownNode_FailsTheStep()
    {
        var report = await FaultRunner().RunAsync(Scenario("""
            { "type": "fault", "node": "REMOTE-HOSTNAME-1", "fault": "drop", "ms": 100 }
            """), "unit.json", CancellationToken.None);

        Assert.Equal(Verdict.Fail, report.Steps[0].Verdict);
        Assert.Contains("REMOTE-HOSTNAME-1", report.Steps[0].Message);
        Assert.Empty(_transport.Faults);
    }

    [Theory]
    [InlineData("""{ "type": "fault", "node": "pc-02", "fault": "unplug", "ms": 100 }""", "unplug")]
    [InlineData("""{ "type": "fault", "node": "", "fault": "drop", "ms": 100 }""", "node")]
    [InlineData("""{ "type": "fault", "node": "pc-02", "fault": "drop", "ms": 0 }""", "ms")]
    [InlineData("""{ "type": "fault", "node": "pc-02", "fault": "drop", "ms": 100, "maxReconnectMs": 0 }""", "maxReconnectMs")]
    public void BadFaultStep_IsRejectedWhenTheScenarioLoads(string step, string mentions)
    {
        var ex = Assert.Throws<ScenarioException>(() => Scenario(step));
        Assert.Contains(mentions, ex.Message);
    }
```

- [ ] **Step 8: Run to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~TestRunnerTests"`
Expected: the four new `[Fact]`s FAIL (the runner ignores `FaultStep`: the switch has no case) and the `BadFaultStep_…` cases FAIL (the loader accepts them); all existing tests PASS.

- [ ] **Step 9: Implement the step**

In `ScenarioLoader` (the validation `switch`, after `case ShowEventStep`), reject bad fault steps when the scenario loads (the node name can only be checked at run time):

```csharp
                case FaultStep fault:
                    if (string.IsNullOrWhiteSpace(fault.Node)) errors.Add($"{at}: node is empty");
                    if (!TestFaults.IsKnown(fault.Fault)) errors.Add($"{at}: fault \"{fault.Fault}\" is not {TestFaults.Drop} or {TestFaults.Silent}");
                    if (fault.Ms <= 0) errors.Add($"{at}: ms must be > 0");
                    if (fault.MaxReconnectMs <= 0) errors.Add($"{at}: maxReconnectMs must be > 0");
                    if (fault.MaxSweepMs <= 0) errors.Add($"{at}: maxSweepMs must be > 0");
                    break;
```

In `TestRunner.StepTimeoutMs`, add a line before `_ =>`:

```csharp
        FaultStep f => f.WatchMs + _o.FaultHostSyncMs + _o.DefaultStepTimeoutMs,
```

In `RunStepAsync`, add a case before `case WaitStep wait:`:

```csharp
            case FaultStep fault:
                await RunFaultAsync(ctx, sr, fault, ct);
                break;
```

and the method after `RunSeriesAsync`:

```csharp
    /// <summary>
    /// Design §12: send the fault, then watch the server's registry until the node was lost and is back (or the
    /// watch time is up). The node's outage is expected: it is not a node failure, and its test mode is re-sent
    /// (a reconnect clears it on the client). Ends once the room shows the node again, so the post-step check agrees.
    /// </summary>
    private async Task RunFaultAsync(RunContext ctx, StepReport sr, FaultStep fault, CancellationToken ct)
    {
        var node = ctx.Remotes.FirstOrDefault(n => SkewKeyMatches(fault.Node, n));
        if (node == null)
        {
            sr.Verdict = Verdict.Fail;
            sr.Message = $"fault node '{fault.Node}' matches no connected remote";
            return;
        }
        var r = new FaultReport { NodeId = node.NodeId, Name = node.Name, Kind = fault.Fault, Ms = fault.Ms, SentAtUtcMs = _o.NowUtcMs() };
        sr.Fault = r;
        if (!await _transport.SendFaultAsync(node.NodeId, fault.Fault, fault.Ms))
        {
            sr.Verdict = Verdict.Fail;
            sr.Message = $"fault not delivered: {TestModeErrors.NoCommandStream}";
            RecordNodeFailure(ctx, node, sr.Message);
            return;
        }

        long deadline = r.SentAtUtcMs + fault.WatchMs;
        while (_o.NowUtcMs() < deadline)
        {
            bool registered = _transport.IsRegistered(node.NodeId);
            long now = _o.NowUtcMs();
            if (!registered && r.LostAtUtcMs == null) r.LostAtUtcMs = now;
            if (registered && r.LostAtUtcMs != null) { r.BackAtUtcMs = now; break; }
            await Task.Delay(_o.FaultPollMs, ct);
        }

        FaultEvaluation.Evaluate(r, fault.MaxReconnectMs, fault.MaxSweepMs);
        sr.Verdict = r.Verdict;
        sr.Message = $"{node.Name} {fault.Fault} {fault.Ms} ms: {r.Message}";

        if (r.BackAtUtcMs == null) return;   // the post-step connection check records it as lost

        ctx.Lost.TryRemove(node.NodeId, out _);
        if (ctx.Mode != null && !await _transport.SendTestModeAsync(node.NodeId, ctx.Mode.Timecode, SkewFor(ctx.Mode, node)))
            sr.Message += "; test mode not re-sent (no command stream yet)";

        // Wait for the room to list the node again, else CheckConnectionsAsync would count it as lost.
        long hostDeadline = _o.NowUtcMs() + _o.FaultHostSyncMs;
        while (_o.NowUtcMs() < hostDeadline)
        {
            var nodes = await _host.GetNodesAsync();
            if (nodes.Any(n => n.NodeId == node.NodeId && n.Connected)) return;
            await Task.Delay(_o.FaultPollMs, ct);
        }
        sr.Message += $"; the room still showed {node.Name} as offline {_o.FaultHostSyncMs} ms later";
    }
```

- [ ] **Step 10: Run the runner tests**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~TestRunnerTests|FullyQualifiedName~FaultEvaluationTests|FullyQualifiedName~TestRunCoordinatorTests|FullyQualifiedName~ScenarioLoader"`
Expected: all PASS.

- [ ] **Step 11: Commit**

```bash
git add WaBiBaBuSy.Models/Testing WaBiBaBuSy.Core/Services/Testing WaBiBaBuSy.Tests/TestRunnerTests.cs WaBiBaBuSy.Tests/TestRunCoordinatorTests.cs WaBiBaBuSy.Tests/FaultEvaluationTests.cs
git commit -m "feat(test-mode): fault step — drop / silent with reconnect and sweep timing"
```

---

### Task 4: Report table, scenario, docs

**Files:**
- Modify: `WaBiBaBuSy.Core/Services/Testing/TestReportWriter.cs` (after the "Node failures" block, ≈ line 83)
- Modify: `WaBiBaBuSy.Tests/TestReportWriterTests.cs`
- Create: `TestScenarios/recovery.json`
- Modify: `TestScenarios/README.md`, `.docs/plans/2026-09-30-automated-test-mode-design.md` §12, `.docs/plans/2026-10-09-remote-session-runbook.md`, `.docs/RECENT_UPDATES.md`, `.docs/2026.07_OPEN_ITEMS.md`

**Interfaces:**
- Consumes: `StepReport.Fault` / `FaultReport` (Task 3).

- [ ] **Step 1: Write the failing report test**

In `TestReportWriterTests.cs` (uses the existing `Report()` helper):

```csharp
    [Fact]
    public void Html_ListsFaults()
    {
        var report = Report();
        report.Steps.Add(new StepReport
        {
            Index = 9, Kind = "fault", Label = "drop", Verdict = Verdict.Warn,
            Fault = new FaultReport
            {
                Name = "pc-02", Kind = TestFaults.Drop, Ms = 10000, SentAtUtcMs = 1000, LostAtUtcMs = 1200, BackAtUtcMs = 18000,
                ReconnectMs = 7000, Verdict = Verdict.Warn, Message = "reconnected 7000 ms after the link came back (limit 5000 ms)",
            },
        });

        var html = TestReportWriter.RenderHtml(report);

        Assert.Contains("<h3>Faults</h3>", html);
        Assert.Contains("pc-02", html);
        Assert.Contains("reconnected 7000 ms after the link came back", html);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~TestReportWriterTests"`
Expected: `Html_ListsFaults` FAILS.

- [ ] **Step 3: Render the table**

In `TestReportWriter.RenderHtml`, after the `NodeFailures` block:

```csharp
        var faults = r.Steps.Where(s => s.Fault != null).Select(s => s.Fault!).ToList();
        if (faults.Count > 0)
        {
            sb.Append("<h3>Faults</h3><table><tr><th>Node</th><th>Fault</th><th>Server lost it after</th><th>Back after</th><th>Result</th><th></th></tr>");
            foreach (var f in faults)
                sb.Append("<tr><td>").Append(E(f.Name)).Append("</td><td>").Append(E(f.Kind)).Append(' ').Append(f.Ms).Append(" ms</td><td>")
                  .Append(f.LostAtUtcMs is long lost ? $"{lost - f.SentAtUtcMs} ms" : "—").Append("</td><td>")
                  .Append(f.BackAtUtcMs is long back ? $"{back - f.SentAtUtcMs} ms" : "—").Append("</td><td>")
                  .Append(E(f.Message)).Append("</td><td>").Append(Badge(f.Verdict)).Append("</td></tr>");
            sb.Append("</table>");
        }
```

- [ ] **Step 4: Run the report tests**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~TestReportWriterTests"`
Expected: all PASS.

- [ ] **Step 5: Scenario**

Create `TestScenarios/recovery.json`:

```json
{
  "name": "recovery",
  "requires": { "minRemoteNodes": 1 },
  "steps": [
    { "type": "testMode", "timecode": true },
    { "type": "playScene", "scene": "scenes/linear-sequential.json", "marker": true },
    { "type": "probe", "label": "before", "at": "start+1000ms", "capture": true },
    { "type": "fault", "label": "10 s link loss", "node": "REMOTE-HOSTNAME-1", "fault": "drop", "ms": 10000, "maxReconnectMs": 5000 },
    { "type": "wait", "label": "scene resumes on the node", "ms": 3000 },
    { "type": "probe", "label": "after link loss", "at": "now+1000ms", "capture": true },
    { "type": "fault", "label": "power-off stand-in", "node": "REMOTE-HOSTNAME-1", "fault": "silent", "ms": 45000, "maxSweepMs": 45000 },
    { "type": "wait", "label": "scene resumes on the node", "ms": 3000 },
    { "type": "probe", "label": "after silence", "at": "now+1000ms", "capture": true },
    { "type": "testMode", "label": "test mode off", "timecode": false },
    { "type": "stop" }
  ]
}
```

Check it is copied next to the exe like the others: `ls WaBiBaBuSy.UI/bin/Debug/net9.0-windows/TestScenarios/recovery.json` after `dotnet build` (the csproj copies the folder by wildcard; if not, add it the way `clock-skew.json` is included).

- [ ] **Step 6: Docs**

- `TestScenarios/README.md`: add a row to the scenarios table — `recovery.json` | 1+ client | 10 s link loss (reconnect time after the link is back, limit 5 s) and a 45 s silence (time until the server drops the node, limit 45 s), the node rejoining at the right position after each; replace `REMOTE-HOSTNAME-1` in the exe folder's copy with a tile name — and a `## Step fault` section with the JSON example and the verdict table from design §12 (with the `"fault"` property name).
- Design §12: rename the JSON property `kind` → `fault` in both examples; replace "polls `GetNodesAsync`" with "polls the server's client registry (`ITestTransport.IsRegistered`) and, once the node is back, waits up to 10 s for the room to show it"; add the stale-stream guard (Task 1) to "Findings".
- Runbook §2 table: add row 6 `recovery.json` (~3 min; replace the placeholder; covers checklist §7 cable pull + power-off timing — the manual cable pull in §6 becomes a cross-check).
- `.docs/RECENT_UPDATES.md`: entry "2026-10-10 — Test mode spec 2a: fault injection" (what, the two session fixes, verification).
- `.docs/2026.07_OPEN_ITEMS.md` §2: "Test mode spec 2" → first slice done (drop / silent); open: client restart, server-side faults, show-reliability scenarios.

- [ ] **Step 7: Full verification**

Run: `dotnet build` → 0 errors. `dotnet test WaBiBaBuSy.Tests` → all PASS. Then a single-machine smoke: `smoke-local` via CLI (`--exit`) still exit 0 (the fault step is not in it; this checks nothing else regressed).

- [ ] **Step 8: Commit**

```bash
git add WaBiBaBuSy.Core/Services/Testing/TestReportWriter.cs WaBiBaBuSy.Tests/TestReportWriterTests.cs TestScenarios .docs
git commit -m "feat(test-mode): Faults table in the report, recovery.json, docs"
```

Then run `./tools/graphify-update.ps1` (CLAUDE.md rule after code changes).

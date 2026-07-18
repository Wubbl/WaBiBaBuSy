# Drift Telemetry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clients report their clock offset + RTT to the server via heartbeat; the topology UI shows a color-coded "±Xms" drift label per node. The dead `TimingSynchronizer` broadcast machinery is deleted.

**Architecture:** Passive one-way telemetry: `ClockOffsetEstimator` (client) → new heartbeat fields → stored on proto `ConnectedClient` (server) + `DriftMonitor` (breach logging) → existing 2s topology poll → `ClientNodeViewModel.DriftState` → code-behind node label. No correction loop; the deterministic-math invariant is untouched.

**Tech Stack:** .NET 9, gRPC (proto3, C# codegen via Grpc.Tools on build), Avalonia (MVVM, CommunityToolkit `[ObservableProperty]`), xUnit.

**Spec:** `.docs/plans/2026-07-18-drift-telemetry-design.md`

**Key layout constraint:** `WaBiBaBuSy.Grpc` and `WaBiBaBuSy.Tests` reference only `WaBiBaBuSy.Models` (+Common), NOT `WaBiBaBuSy.Core`. Therefore `DriftMonitor` and `DriftState` live in `WaBiBaBuSy.Models/Networking/` (same home as `ClockOffsetEstimator`), not in Core as the spec's path sketch suggested.

**Scope guard:** There are TWO types named `AnimationTimingSync`: the proto message (deleted here) and a POCO `WaBiBaBuSy.Models/Animation/AnimationTimingSync.cs` used by the dead client-side orchestrator path (`ClientAnimationRenderer.OnReceiveTimingSync`, `AnimationService.ReceiveTimingSyncAsync`). The POCO and its consumers are NOT touched — they belong to the separate "legacy dead code" cleanup item.

**Verify before starting:** `dotnet test WaBiBaBuSy.Tests` → 19 tests pass.

---

### Task 1: `ClockOffsetEstimator.RttMs` getter

**Files:**
- Modify: `WaBiBaBuSy.Models\Networking\ClockOffsetEstimator.cs`
- Test: `WaBiBaBuSy.Tests\ClockOffsetEstimatorTests.cs`

- [ ] **Step 1.1: Write the failing tests**

Append inside the existing `ClockOffsetEstimatorTests` class:

```csharp
    [Fact]
    public void NoSamples_RttIsZero()
    {
        var estimator = new ClockOffsetEstimator();
        Assert.Equal(0, estimator.RttMs);
    }

    [Fact]
    public void RttMs_ReturnsLowestRttInWindow()
    {
        var estimator = new ClockOffsetEstimator();
        // RTT = receive - send: 200ms then 50ms
        estimator.AddSample(clientSendMs: 1000, serverTimestampMs: 1600, clientReceiveMs: 1200);
        estimator.AddSample(clientSendMs: 2000, serverTimestampMs: 2500, clientReceiveMs: 2050);
        Assert.Equal(50, estimator.RttMs);
    }
```

- [ ] **Step 1.2: Run tests to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ClockOffsetEstimatorTests"`
Expected: compile error `'ClockOffsetEstimator' does not contain a definition for 'RttMs'`

- [ ] **Step 1.3: Implement the getter**

In `ClockOffsetEstimator.cs`, insert after the `OffsetMs` property (after line 44):

```csharp
    /// <summary>
    /// Round-trip time (ms) of the best (lowest-RTT) sample in the window —
    /// the sample <see cref="OffsetMs"/> is derived from. 0 until samples exist.
    /// </summary>
    public long RttMs
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count == 0) return 0;
                long best = long.MaxValue;
                foreach (var (rtt, _) in _samples)
                {
                    if (rtt < best) best = rtt;
                }
                return best;
            }
        }
    }
```

- [ ] **Step 1.4: Run tests to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ClockOffsetEstimatorTests"`
Expected: PASS (all, including the 2 new)

- [ ] **Step 1.5: Commit**

```bash
git add WaBiBaBuSy.Models/Networking/ClockOffsetEstimator.cs WaBiBaBuSy.Tests/ClockOffsetEstimatorTests.cs
git commit -m "feat: expose best-sample RTT from ClockOffsetEstimator"
```

---

### Task 2: `DriftMonitor` + `DriftState` in Models

**Files:**
- Create: `WaBiBaBuSy.Models\Networking\DriftMonitor.cs`
- Test: `WaBiBaBuSy.Tests\DriftMonitorTests.cs` (new file)

- [ ] **Step 2.1: Write the failing tests**

Create `WaBiBaBuSy.Tests\DriftMonitorTests.cs`:

```csharp
using WaBiBaBuSy.Models.Networking;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class DriftMonitorTests
{
    private const long Now = 1_000_000_000L; // arbitrary UTC ms reference

    // --- Classify: report presence ---

    [Fact]
    public void Classify_NoReportTimestamp_IsNone()
    {
        // proto3 default 0 must never render as "perfectly synced"
        Assert.Equal(DriftState.None, DriftMonitor.Classify(0, lastReportUtcMs: 0, nowUtcMs: Now));
    }

    // --- Classify: thresholds (|offset|, boundaries inclusive on the lower state) ---

    [Theory]
    [InlineData(0.0, DriftState.Ok)]
    [InlineData(25.0, DriftState.Ok)]      // boundary: ≤25 is Ok
    [InlineData(25.1, DriftState.Warn)]
    [InlineData(50.0, DriftState.Warn)]    // boundary: ≤50 is Warn
    [InlineData(50.1, DriftState.Breach)]
    [InlineData(-60.0, DriftState.Breach)] // negative offsets use absolute value
    [InlineData(-25.0, DriftState.Ok)]
    public void Classify_FreshReport_ByOffset(double offsetMs, DriftState expected)
    {
        Assert.Equal(expected, DriftMonitor.Classify(offsetMs, lastReportUtcMs: Now, nowUtcMs: Now));
    }

    // --- Classify: staleness (2 × heartbeat interval; default interval 5s → 10 000ms) ---

    [Fact]
    public void Classify_ReportOlderThanTwoIntervals_IsStale_EvenIfOffsetSmall()
    {
        Assert.Equal(DriftState.Stale,
            DriftMonitor.Classify(10.0, lastReportUtcMs: Now - 10_001, nowUtcMs: Now));
    }

    [Fact]
    public void Classify_ExactlyTwoIntervalsOld_IsNotStale()
    {
        Assert.Equal(DriftState.Ok,
            DriftMonitor.Classify(10.0, lastReportUtcMs: Now - 10_000, nowUtcMs: Now));
    }

    [Fact]
    public void Classify_CustomHeartbeatInterval_ChangesStalenessWindow()
    {
        // 2s interval → stale after 4 000ms
        Assert.Equal(DriftState.Stale,
            DriftMonitor.Classify(10.0, lastReportUtcMs: Now - 4_001, nowUtcMs: Now, heartbeatIntervalSeconds: 2));
        Assert.Equal(DriftState.Ok,
            DriftMonitor.Classify(10.0, lastReportUtcMs: Now - 4_000, nowUtcMs: Now, heartbeatIntervalSeconds: 2));
    }

    // --- Record: breach-transition signal (for log-once-per-breach behavior) ---

    [Fact]
    public void Record_FirstBreach_ReturnsTrue_RepeatBreach_ReturnsFalse()
    {
        var monitor = new DriftMonitor();
        Assert.True(monitor.Record("c1", offsetMs: 60, rttMs: 5, reportUtcMs: Now));   // enters breach
        Assert.False(monitor.Record("c1", offsetMs: 70, rttMs: 5, reportUtcMs: Now));  // still breached
        Assert.False(monitor.Record("c1", offsetMs: 10, rttMs: 5, reportUtcMs: Now));  // recovers
        Assert.True(monitor.Record("c1", offsetMs: 60, rttMs: 5, reportUtcMs: Now));   // re-enters breach
    }

    [Fact]
    public void Record_NonBreach_ReturnsFalse()
    {
        var monitor = new DriftMonitor();
        Assert.False(monitor.Record("c1", offsetMs: 10, rttMs: 5, reportUtcMs: Now));
        Assert.False(monitor.Record("c1", offsetMs: 50, rttMs: 5, reportUtcMs: Now)); // 50 is Warn, not Breach
    }

    // --- Record/GetDrift/Remove: storage ---

    [Fact]
    public void GetDrift_ReturnsLatestReport()
    {
        var monitor = new DriftMonitor();
        monitor.Record("c1", offsetMs: 12.5, rttMs: 3.5, reportUtcMs: Now);
        var report = monitor.GetDrift("c1");
        Assert.NotNull(report);
        Assert.Equal(12.5, report!.OffsetMs);
        Assert.Equal(3.5, report.RttMs);
        Assert.Equal(Now, report.ReportUtcMs);
    }

    [Fact]
    public void GetDrift_UnknownClient_ReturnsNull()
    {
        Assert.Null(new DriftMonitor().GetDrift("nobody"));
    }

    [Fact]
    public void Remove_ClearsReport()
    {
        var monitor = new DriftMonitor();
        monitor.Record("c1", offsetMs: 12, rttMs: 3, reportUtcMs: Now);
        monitor.Remove("c1");
        Assert.Null(monitor.GetDrift("c1"));
    }
}
```

- [ ] **Step 2.2: Run tests to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~DriftMonitorTests"`
Expected: compile error `The type or namespace name 'DriftMonitor' could not be found`

- [ ] **Step 2.3: Implement `DriftMonitor`**

Create `WaBiBaBuSy.Models\Networking\DriftMonitor.cs`:

```csharp
using System.Collections.Concurrent;

namespace WaBiBaBuSy.Models.Networking;

/// <summary>
/// Sync-quality classification for a client's reported clock offset.
/// </summary>
public enum DriftState
{
    /// <summary>No drift report received yet (or a pre-telemetry client).</summary>
    None = 0,

    /// <summary>|offset| ≤ 25ms — comfortably inside the ±50ms sync tolerance.</summary>
    Ok = 1,

    /// <summary>25ms &lt; |offset| ≤ 50ms — approaching tolerance.</summary>
    Warn = 2,

    /// <summary>|offset| &gt; 50ms — sync tolerance breached.</summary>
    Breach = 3,

    /// <summary>Last report is older than 2× the heartbeat interval — value untrustworthy.</summary>
    Stale = 4,
}

/// <summary>
/// Server-side drift-telemetry aggregator. Stores the latest clock-offset/RTT
/// report per client (fed from heartbeats) and classifies sync quality against
/// the ±50ms tolerance. Passive telemetry only — never corrects playback;
/// the d2d_crossscreen path stays purely deterministic.
/// Thread-safe: heartbeat handlers write concurrently.
/// </summary>
public class DriftMonitor
{
    /// <summary>|offset| above this is <see cref="DriftState.Warn"/>.</summary>
    public const double WarnThresholdMs = 25.0;

    /// <summary>|offset| above this is <see cref="DriftState.Breach"/> (the ±50ms sync tolerance).</summary>
    public const double BreachThresholdMs = 50.0;

    /// <summary>Mirrors ClientConfiguration.HeartbeatIntervalSeconds' default; the server does not know each client's actual setting.</summary>
    public const int DefaultHeartbeatIntervalSeconds = 5;

    /// <summary>A report older than this many heartbeat intervals is <see cref="DriftState.Stale"/>.</summary>
    public const int StalenessMultiplier = 2;

    /// <summary>One client's latest drift report.</summary>
    public sealed record DriftReport(double OffsetMs, double RttMs, long ReportUtcMs);

    private readonly ConcurrentDictionary<string, DriftReport> _reports = new();

    /// <summary>
    /// Store a client's latest report. Returns true only when this report newly
    /// crosses into <see cref="DriftState.Breach"/> — callers use it to log the
    /// breach once instead of every heartbeat.
    /// </summary>
    public bool Record(string clientId, double offsetMs, double rttMs, long reportUtcMs)
    {
        _reports.TryGetValue(clientId, out var previous);
        _reports[clientId] = new DriftReport(offsetMs, rttMs, reportUtcMs);

        var wasBreached = previous != null && Math.Abs(previous.OffsetMs) > BreachThresholdMs;
        var isBreached = Math.Abs(offsetMs) > BreachThresholdMs;
        return isBreached && !wasBreached;
    }

    /// <summary>Latest report for a client, or null if none received.</summary>
    public DriftReport? GetDrift(string clientId)
        => _reports.TryGetValue(clientId, out var report) ? report : null;

    /// <summary>Forget a client (disconnect / dead-client sweep).</summary>
    public void Remove(string clientId) => _reports.TryRemove(clientId, out _);

    /// <summary>
    /// Classify a reported offset. <paramref name="lastReportUtcMs"/> == 0 means
    /// "no report ever" (proto3 default) and yields <see cref="DriftState.None"/> —
    /// absent data must never display as perfectly synced.
    /// </summary>
    public static DriftState Classify(
        double offsetMs,
        long lastReportUtcMs,
        long nowUtcMs,
        int heartbeatIntervalSeconds = DefaultHeartbeatIntervalSeconds)
    {
        if (lastReportUtcMs <= 0)
            return DriftState.None;

        if (nowUtcMs - lastReportUtcMs > heartbeatIntervalSeconds * StalenessMultiplier * 1000L)
            return DriftState.Stale;

        var abs = Math.Abs(offsetMs);
        if (abs <= WarnThresholdMs) return DriftState.Ok;
        if (abs <= BreachThresholdMs) return DriftState.Warn;
        return DriftState.Breach;
    }
}
```

- [ ] **Step 2.4: Run tests to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~DriftMonitorTests"`
Expected: PASS (16 test cases — the Theory expands to 7)

- [ ] **Step 2.5: Commit**

```bash
git add WaBiBaBuSy.Models/Networking/DriftMonitor.cs WaBiBaBuSy.Tests/DriftMonitorTests.cs
git commit -m "feat: DriftMonitor aggregator + DriftState classification"
```

---

### Task 3: Proto changes + delete dead broadcast RPC

The proto and its only-remaining RPC consumer must change in one commit or the build breaks (regen removes the base method the handler overrides).

**Files:**
- Modify: `WaBiBaBuSy.Grpc\Protos\wabibabusy.proto` (lines 53, 118-128, 218-227, 356-361)
- Modify: `WaBiBaBuSy.Grpc\Services\WallpaperSyncService.cs` (delete lines 1325-1350)

- [ ] **Step 3.1: Extend `HeartbeatRequest`**

Replace lines 117-122:

```proto
// Heartbeat request
message HeartbeatRequest {
  string client_id = 1;
  int64 timestamp = 2;
  ClientStatusEnum status = 3;
  // Drift telemetry (client's NTP-style estimate from prior heartbeat round-trips).
  // has_drift_report=false on the first heartbeat: the estimator has no sample yet,
  // so the server must not read the (default-0) values as "perfectly synced".
  double clock_offset_ms = 4;   // estimated (server_clock - client_clock)
  double rtt_ms = 5;            // round-trip time of the best sample
  bool has_drift_report = 6;
}
```

- [ ] **Step 3.2: Extend `ConnectedClient`**

Replace lines 217-227:

```proto
// Connected client information
message ConnectedClient {
  string client_id = 1;
  string hostname = 2;
  string ip_address = 3;
  int32 order_position = 4;
  ClientStatusEnum status = 5;
  int64 last_heartbeat = 6;
  ScreenConfiguration screen_config = 7;
  int32 physical_distance_cm = 8; // Distance to previous client in chain (cm)
  // Drift telemetry, last reported via heartbeat. last_drift_report_utc == 0
  // means no report ever (pre-telemetry client or first heartbeat pending).
  double clock_offset_ms = 9;       // (server_clock - client_clock), ms
  double rtt_ms = 10;               // heartbeat round-trip time, ms
  int64 last_drift_report_utc = 11; // server receive time of last report (UTC ms)
}
```

- [ ] **Step 3.3: Delete the dead broadcast RPC + message**

- Delete line 53: `rpc BroadcastAnimationTimingSync(AnimationTimingSync) returns (Empty);`
- Delete lines 356-361 (the comment line `// Timing synchronization message (server → all animating clients)` and the whole `message AnimationTimingSync { ... }` block).

- [ ] **Step 3.4: Delete the RPC handler**

In `WaBiBaBuSy.Grpc\Services\WallpaperSyncService.cs`, delete the whole method incl. its doc comment (lines 1325-1350):

```csharp
    /// <summary>
    /// Server broadcasts timing sync to all animating clients
    /// </summary>
    public override async Task<Empty> BroadcastAnimationTimingSync(
        AnimationTimingSync request,
        ServerCallContext context)
    { ... }
```

(The `CommandType.SYNC_FRAME` enum value stays — removing enum values from live protos is riskier than leaving one unused. No client-side code consumes SyncFrame commands.)

- [ ] **Step 3.5: Build to verify codegen + no dangling references**

Run: `dotnet build`
Expected: Build succeeded, 0 errors. (Grpc.Tools regenerates `Wabibabusy.cs`/`WabibabusyGrpc.cs` from the proto during build.)

- [ ] **Step 3.6: Run full test suite**

Run: `dotnet test WaBiBaBuSy.Tests`
Expected: all tests pass (19 pre-existing + Task 1/2 additions)

- [ ] **Step 3.7: Commit**

```bash
git add WaBiBaBuSy.Grpc/Protos/wabibabusy.proto WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs
git commit -m "feat: drift-telemetry proto fields; drop dead BroadcastAnimationTimingSync RPC"
```

---

### Task 4: Client reports offset + RTT in heartbeat

**Files:**
- Modify: `WaBiBaBuSy.Core\Services\Networking\WallpaperSyncClient.cs:398-404`

No unit test: this is a 6-line gRPC-facing change in a class that needs a live channel; it's covered by the E2E validation pass (top of the open-items list). The estimator math it forwards is unit-tested in Task 1.

- [ ] **Step 4.1: Populate the new fields**

In `SendHeartbeatAsync`, replace lines 398-404:

```csharp
        var sendMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var request = new HeartbeatRequest
        {
            ClientId = _clientId,
            Timestamp = sendMs,
            Status = ClientStatusEnum.ClientConnected
        };

        // Drift telemetry: report the current estimate (from prior round-trips).
        // First heartbeat has no sample yet — HasDriftReport stays false so the
        // server doesn't mistake default-0 for a perfect sync.
        if (_clockOffset.HasSamples)
        {
            request.ClockOffsetMs = _clockOffset.OffsetMs;
            request.RttMs = _clockOffset.RttMs;
            request.HasDriftReport = true;
        }
```

- [ ] **Step 4.2: Build**

Run: `dotnet build`
Expected: Build succeeded, 0 errors

- [ ] **Step 4.3: Commit**

```bash
git add WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs
git commit -m "feat: client reports clock offset + RTT via heartbeat"
```

---

### Task 5: Server stores reports + breach logging

**Files:**
- Modify: `WaBiBaBuSy.Grpc\Services\WallpaperSyncService.cs` (usings ~line 6, fields ~line 24, `Heartbeat` at 198-222, `RemoveClient` at ~590)

- [ ] **Step 5.1: Add using + field**

Add to the usings block (after line 6 `using WaBiBaBuSy.Models.Configuration;`):

```csharp
using WaBiBaBuSy.Models.Networking;
```

Add a field next to `_writeLocks` (~line 24):

```csharp
    private readonly DriftMonitor _driftMonitor = new(); // per-client drift telemetry from heartbeats
```

- [ ] **Step 5.2: Extend the `Heartbeat` handler**

Replace the method body's success branch (lines 202-214) with:

```csharp
        if (_connectedClients.TryGetValue(request.ClientId, out var client))
        {
            client.LastHeartbeat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            client.Status = request.Status;

            if (request.HasDriftReport)
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                client.ClockOffsetMs = request.ClockOffsetMs;
                client.RttMs = request.RttMs;
                client.LastDriftReportUtc = nowMs;

                // Record returns true only on a NEW breach — log once, not every heartbeat.
                if (_driftMonitor.Record(request.ClientId, request.ClockOffsetMs, request.RttMs, nowMs))
                {
                    _logger.LogWarning(
                        "Client {ClientId} clock offset {OffsetMs:F1}ms exceeds the ±{ToleranceMs:F0}ms sync tolerance (RTT {RttMs:F1}ms)",
                        request.ClientId, request.ClockOffsetMs, DriftMonitor.BreachThresholdMs, request.RttMs);
                }
            }

            _logger.LogDebug("Heartbeat received from client {ClientId}", request.ClientId);

            return Task.FromResult(new HeartbeatResponse
            {
                Acknowledged = true,
                ServerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
```

(The unknown-client fallthrough at lines 216-221 stays unchanged.)

- [ ] **Step 5.3: Clean up on client removal**

In `RemoveClient` (~line 590), add after `_writeLocks.TryRemove(clientId, out _);`:

```csharp
        _driftMonitor.Remove(clientId);
```

- [ ] **Step 5.4: Build + full tests**

Run: `dotnet build && dotnet test WaBiBaBuSy.Tests`
Expected: 0 errors, all tests pass

- [ ] **Step 5.5: Commit**

```bash
git add WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs
git commit -m "feat: server stores per-client drift reports, warns on tolerance breach"
```

---

### Task 6: Delete `TimingSynchronizer`

**Files:**
- Delete: `WaBiBaBuSy.Core\Services\Animation\TimingSynchronizer.cs`
- Modify: `WaBiBaBuSy.Models\Configuration\LoggingConfiguration.cs:31` (comment references the deleted class)

- [ ] **Step 6.1: Verify zero code references first**

Run: `grep -rn "TimingSynchronizer\|TimingSyncSession\|SyncSessionState" --include="*.cs" WaBiBaBuSy.Core WaBiBaBuSy.Grpc WaBiBaBuSy.UI WaBiBaBuSy.Models WaBiBaBuSy.Common WaBiBaBuSy.Tests`
Expected: hits ONLY in `WaBiBaBuSy.Core\Services\Animation\TimingSynchronizer.cs` itself and the `LoggingConfiguration.cs:31` comment. If anything else appears, STOP and investigate before deleting.

- [ ] **Step 6.2: Delete the file**

```bash
git rm WaBiBaBuSy.Core/Services/Animation/TimingSynchronizer.cs
```

- [ ] **Step 6.3: Fix the stale comment**

In `LoggingConfiguration.cs` line 31, change:

```csharp
    /// <summary>Animation system (AnimationDistributor, TimingSynchronizer, Orchestrator)</summary>
```

to:

```csharp
    /// <summary>Animation system (AnimationDistributor, Orchestrator)</summary>
```

- [ ] **Step 6.4: Build + full tests**

Run: `dotnet build && dotnet test WaBiBaBuSy.Tests`
Expected: 0 errors, all tests pass

- [ ] **Step 6.5: Commit**

```bash
git add -A
git commit -m "refactor: delete dead TimingSynchronizer (superseded by drift telemetry)"
```

---

### Task 7: Topology UI drift label

**Files:**
- Modify: `WaBiBaBuSy.UI\ViewModels\ClientNodeViewModel.cs` (add 3 properties)
- Modify: `WaBiBaBuSy.UI\ViewModels\MainWindowViewModel.cs:2514-2534` (`UpdateClientList` mapping)
- Modify: `WaBiBaBuSy.UI\Views\MainWindow.axaml.cs` (node construction ~line 381, after `statusPanel`; new helper method)

UI is code-behind-constructed (no XAML template for topology nodes), so this is manual-verify: run server + one client and watch the label. No unit test — classification logic is already covered by `DriftMonitorTests`.

- [ ] **Step 7.1: Add VM properties**

In `ClientNodeViewModel.cs`, add `using WaBiBaBuSy.Models.Networking;` at the top (after line 1), and insert after the `_activeAnimationName` field (line 89):

```csharp
    /// <summary>Last reported clock offset (server − client), ms. Only meaningful when DriftState is not None.</summary>
    [ObservableProperty]
    private double _driftMs;

    /// <summary>Heartbeat round-trip time of the client's best clock sample, ms.</summary>
    [ObservableProperty]
    private double _rttMs;

    /// <summary>Sync-quality classification of the last drift report.</summary>
    [ObservableProperty]
    private DriftState _driftState = DriftState.None;
```

- [ ] **Step 7.2: Map proto → VM in `UpdateClientList`**

In `MainWindowViewModel.cs`, add `using WaBiBaBuSy.Models.Networking;` to the usings if not present. Then in the `new ClientNodeViewModel { ... }` initializer (lines 2514-2534), add after `PixelsPerCm = monitorPixelsPerCm`:

```csharp
                        PixelsPerCm = monitorPixelsPerCm,
                        // Drift telemetry (server-local SERVER_LOCALHOST_MONITOR_*/LOCAL_MACHINE_MONITOR_*
                        // nodes never report → LastDriftReportUtc stays 0 → DriftState.None → label hidden)
                        DriftMs = grpcClient.ClockOffsetMs,
                        RttMs = grpcClient.RttMs,
                        DriftState = DriftMonitor.Classify(
                            grpcClient.ClockOffsetMs,
                            grpcClient.LastDriftReportUtc,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
```

- [ ] **Step 7.3: Render the label on the node**

In `MainWindow.axaml.cs`, add `using WaBiBaBuSy.Models.Networking;` to the usings if not present.

Insert after `stackPanel.Children.Add(statusPanel);` (line 381), before the `animNameText` block:

```csharp
        // Drift telemetry label ("±Xms", color-coded; hidden until a client reports)
        var driftText = new TextBlock
        {
            FontSize = 9,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        UpdateDriftLabel(driftText, client);
        stackPanel.Children.Add(driftText);
```

In the `client.PropertyChanged` handler just below (lines 397-412), add a branch:

```csharp
            else if (e.PropertyName == nameof(client.DriftState) || e.PropertyName == nameof(client.DriftMs))
            {
                UpdateDriftLabel(driftText, client);
            }
```

Add this private helper to the `MainWindow` class (next to the other node-construction helpers):

```csharp
    /// <summary>
    /// Style the per-node drift label: hidden until a report exists, grey em-dash
    /// when stale, otherwise "±Xms" colored by DriftState (Ok/Warn/Breach).
    /// </summary>
    private static void UpdateDriftLabel(TextBlock label, ClientNodeViewModel client)
    {
        switch (client.DriftState)
        {
            case DriftState.None:
                label.IsVisible = false;
                break;
            case DriftState.Stale:
                label.IsVisible = true;
                label.Text = "sync: —";
                label.Foreground = new SolidColorBrush(Color.Parse("#888888"));
                break;
            default:
                label.IsVisible = true;
                label.Text = $"±{Math.Abs(client.DriftMs):F0}ms";
                label.Foreground = new SolidColorBrush(Color.Parse(client.DriftState switch
                {
                    DriftState.Ok => "#00CC66",   // matches the animation-name green
                    DriftState.Warn => "#FFC800",
                    _ => "#FF4444",               // matches the disconnected red
                }));
                break;
        }
    }
```

- [ ] **Step 7.4: Build + full tests**

Run: `dotnet build && dotnet test WaBiBaBuSy.Tests`
Expected: 0 errors, all tests pass

- [ ] **Step 7.5: Manual smoke test**

Run: `dotnet run --project WaBiBaBuSy.UI` → tray icon → Start Server → Open Server Control Panel.
Expected: server-local monitor nodes show NO drift label. If a second machine/instance connects as a client: its node shows a green "±Xms" label within ~2 heartbeat intervals (≤10s); killing the client turns the label grey ("sync: —") before the node is swept.

- [ ] **Step 7.6: Commit**

```bash
git add WaBiBaBuSy.UI/ViewModels/ClientNodeViewModel.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs WaBiBaBuSy.UI/Views/MainWindow.axaml.cs
git commit -m "feat: per-client drift label on topology nodes"
```

---

### Task 8: Documentation + graph refresh

**Files:**
- Modify: `.docs\2026.07_OPEN_ITEMS.md` (§2 decision + §6 pointer)
- Modify: `.docs\2025.12_OpenIssues.md` (close the TimingSynchronizer audit item)
- Modify: `.docs\RECENT_UPDATES.md` (changelog entry)
- Modify: `CLAUDE.md` ("Current Work" item 5)

- [ ] **Step 8.1: Update `.docs/2026.07_OPEN_ITEMS.md`**

In §2 "Open decisions", replace the TimingSynchronizer bullet with:

```markdown
- ~~**TimingSynchronizer: wire up or delete.**~~ **Resolved 2026-07-18:** deleted; replaced by passive drift telemetry (heartbeat-reported clock offset + RTT, per-node "±Xms" label in the topology UI). See `.docs/plans/2026-07-18-drift-telemetry-design.md`.
```

In §6 "Where things live", update the bugs line to:

```markdown
- Bugs → `.docs/2025.12_OpenIssues.md` (TimingSynchronizer audit item resolved 2026-07-18)
```

Also update §1 Validation: extend the "Clock-offset sync" row's check to mention the new surface, e.g. append "; topology node shows the offset as a color-coded '±Xms' label".

- [ ] **Step 8.2: Update `.docs/2025.12_OpenIssues.md`**

Find the TimingSynchronizer audit item and mark it resolved with the same one-line summary + date (follow the file's existing resolved-item style).

- [ ] **Step 8.3: Add `.docs/RECENT_UPDATES.md` entry**

Prepend an entry (follow the file's existing format):

```markdown
## 2026-07-18 — Drift telemetry (replaces TimingSynchronizer)
- Clients report clock offset + RTT via heartbeat (`has_drift_report` guards the sample-less first beat)
- Server stores reports on `ConnectedClient`, `DriftMonitor` logs once per new ±50ms breach
- Topology nodes show color-coded "±Xms" (green ≤25 / yellow ≤50 / red >50 / grey stale)
- Deleted dead `TimingSynchronizer` + `BroadcastAnimationTimingSync` RPC + proto `AnimationTimingSync`
```

- [ ] **Step 8.4: Update `CLAUDE.md`**

Replace "Current Work" item 5 (`**TimingSynchronizer decision** — ...`) with:

```markdown
5. **Drift telemetry E2E check** — implemented 2026-07-18 (heartbeat-reported clock offset + topology drift labels); verify labels during multi-client testing
```

- [ ] **Step 8.5: Refresh the knowledge graph**

Run: `graphify update .`
Expected: completes without error (AST-only)

- [ ] **Step 8.6: Commit**

```bash
git add .docs CLAUDE.md graphify-out
git commit -m "docs: drift telemetry shipped; TimingSynchronizer decision resolved"
```

---

## Spec coverage self-check

| Spec section | Task |
|---|---|
| Proto: heartbeat fields + has_drift_report | 3.1 |
| Proto: ConnectedClient fields | 3.2 |
| Proto: delete AnimationTimingSync message + RPC | 3.3, 3.4 |
| Client: heartbeat reporting, estimator RttMs | 1, 4 |
| Server: delete TimingSynchronizer | 6 |
| Server: DriftMonitor + Heartbeat handler + RemoveClient | 2, 5 |
| UI: VM props, UpdateClientList mapping, node label, staleness | 7 |
| Error handling: None-vs-0 semantics, stale, concurrency | 2 (tests), 3.1, 5 |
| Testing: DriftMonitor, RttMs, DriftState boundaries, 19 green | 1, 2, every task's test step |
| Non-goals (no correction, no render-drift, no history) | not implemented anywhere — by design |

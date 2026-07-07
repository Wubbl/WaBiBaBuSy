# Tier 1 Reliability Implementation Plan

> **STATUS: COMPLETE (2026-07-07).** All 10 tasks implemented and committed (`0e8d681`..`673b4c8` + docs). 12 unit tests green, full solution builds with 0 errors. Remaining follow-ups are tracked in `.docs/2025.12_OpenIssues.md` (findings 5/6/11/12) and the Tier 2 roadmap.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make multi-machine animation traversal trustworthy: full config parity for remote clients, correct Simultaneous mode on remotes, clock-offset compensation, auto-reconnection with session resume, and server-side robustness (bind failure, write races, dead-client sweep).

**Architecture:** All remote-bound configs travel as JSON strings in `SyncParameters` (like the existing `pattern_json`). Clock offset is estimated NTP-style from the existing heartbeat and applied at the single point where commands enter the client (`StartSyncStream`), converting server timestamps to client-clock terms. Reconnection lives entirely inside `WallpaperSyncClient`; session resume lives in `WallpaperSyncCoordinator`, which remembers the last cross-screen command per client and re-sends it on re-registration (deterministic epoch back-dating makes the rejoin land mid-animation correctly).

**Tech Stack:** C# 12 / .NET 9, gRPC + Protobuf, System.Text.Json, xunit (new test project)

**Source facts (verified 2026-07-07):**
- `SyncParameters` proto fields end at 17 (`color_grading_json`) — `wabibabusy.proto:152-171`
- `MonitorInfo` proto fields end at 8 (`refresh_rate`) — `wabibabusy.proto:92-101`
- Coordinator send: `WallpaperSyncCoordinator.StartCrossScreenD2DOnClientAsync` (`WallpaperSyncCoordinator.cs:377-435`)
- Client receive: `WallpaperPlaybackService.cs:225-251` → `D2DCrossScreenApplyDelegate` (12-arg `Func`, `WallpaperPlaybackService.cs:39`) → `MainWindowViewModel.ApplyCrossScreenD2DFromRemoteAsync` (`MainWindowViewModel.cs:1913-2013`)
- Send loop: `MainWindowViewModel.cs:3033-3054`; shared timestamp `:3011`; local per-monitor init `:2984-2993`
- Heartbeat: client `WallpaperSyncClient.cs:240-297`, server `WallpaperSyncService.cs:155-179` (`server_timestamp` already in response, client discards it)
- Sync stream client: `WallpaperSyncClient.cs:690-758`; server `WallpaperSyncService.cs:185-240` (`RemoveClient` in `finally`)
- Server write: `WallpaperSyncService.SendCommandToClientAsync` `:562-581` (no lock), `BroadcastCommandAsync` `:586+`
- Host start bug: `WallpaperSyncServerHost.cs:93-99` (`_ = _host.RunAsync()` + `Task.Delay(500)`)
- `AnimationLayerConfig`/`BackgroundLayerConfig`: `WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs:137,204`
- `LastHeartbeat` unit is **seconds** (`ToUnixTimeSeconds`)
- No unit-test project exists (only `WaBiBaBuSy.D2DTest` diagnostic app)

---

### Task 1: Create xunit test project

**Files:**
- Create: `WaBiBaBuSy.Tests/WaBiBaBuSy.Tests.csproj`

- [ ] **Step 1: Create project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\WaBiBaBuSy.Models\WaBiBaBuSy.Models.csproj" />
  </ItemGroup>
</Project>
```

Note: reference Models only (netstandard-safe pure logic). The estimator/backoff classes go into **Models** (`WaBiBaBuSy.Models/Networking/`) so Core, which references Models, can use them and the test project avoids Core's Windows-only dependency chain.

- [ ] **Step 2: Add to solution:** `dotnet sln add WaBiBaBuSy.Tests/WaBiBaBuSy.Tests.csproj`
- [ ] **Step 3: `dotnet build WaBiBaBuSy.Tests`** — expect success
- [ ] **Step 4: Commit** `test: add WaBiBaBuSy.Tests xunit project`

### Task 2: ClockOffsetEstimator (TDD)

**Files:**
- Create: `WaBiBaBuSy.Models/Networking/ClockOffsetEstimator.cs`
- Test: `WaBiBaBuSy.Tests/ClockOffsetEstimatorTests.cs`

- [ ] **Step 1: Failing tests** — offset for symmetric RTT `(t0=1000, server=1600, t3=1200)` → `server - (t0+t3)/2 = 500`; min-RTT sample wins among mixed samples; window keeps last 16; `HasSamples` false initially.
- [ ] **Step 2: Run tests → FAIL (type missing)**
- [ ] **Step 3: Implement**

```csharp
namespace WaBiBaBuSy.Models.Networking;

/// <summary>
/// NTP-style clock offset estimator. Feed it heartbeat round-trips
/// (client send time t0, server timestamp ts, client receive time t3);
/// OffsetMs estimates (server_clock - client_clock) using the sample
/// with the lowest RTT in a sliding window (lowest RTT = least asymmetry error).
/// </summary>
public class ClockOffsetEstimator
{
    private const int WindowSize = 16;
    private readonly object _lock = new();
    private readonly Queue<(long rtt, long offset)> _samples = new();

    public bool HasSamples { get { lock (_lock) return _samples.Count > 0; } }

    /// <summary>Estimated (server - client) clock offset in ms. 0 until samples exist.</summary>
    public long OffsetMs
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count == 0) return 0;
                long bestRtt = long.MaxValue; long best = 0;
                foreach (var (rtt, offset) in _samples)
                    if (rtt < bestRtt) { bestRtt = rtt; best = offset; }
                return best;
            }
        }
    }

    public void AddSample(long clientSendMs, long serverTimestampMs, long clientReceiveMs)
    {
        var rtt = clientReceiveMs - clientSendMs;
        if (rtt < 0) return; // clock went backwards mid-flight; discard
        var offset = serverTimestampMs - (clientSendMs + clientReceiveMs) / 2;
        lock (_lock)
        {
            _samples.Enqueue((rtt, offset));
            while (_samples.Count > WindowSize) _samples.Dequeue();
        }
    }

    public void Reset() { lock (_lock) _samples.Clear(); }
}
```

- [ ] **Step 4: Run tests → PASS**
- [ ] **Step 5: Commit** `feat: NTP-style ClockOffsetEstimator with min-RTT window`

### Task 3: ReconnectBackoff (TDD)

**Files:**
- Create: `WaBiBaBuSy.Models/Networking/ReconnectBackoff.cs`
- Test: `WaBiBaBuSy.Tests/ReconnectBackoffTests.cs`

- [ ] **Step 1: Failing tests** — sequence `NextDelay()` → 1s,2s,4s,8s,16s,30s,30s; `Reset()` → back to 1s.
- [ ] **Step 2: Implement**

```csharp
namespace WaBiBaBuSy.Models.Networking;

/// <summary>Exponential backoff: 1s, 2s, 4s, 8s, 16s, then capped at 30s.</summary>
public class ReconnectBackoff
{
    private int _attempt;
    public TimeSpan NextDelay()
    {
        var seconds = Math.Min(30, 1 << Math.Min(_attempt, 5));
        _attempt++;
        return TimeSpan.FromSeconds(seconds);
    }
    public void Reset() => _attempt = 0;
}
```

- [ ] **Step 3: Tests PASS, Commit** `feat: ReconnectBackoff (1s..30s exponential)`

### Task 4: Clock-offset wiring in WallpaperSyncClient

**Files:**
- Modify: `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs`

- [ ] **Step 1:** Add field `private readonly ClockOffsetEstimator _clockOffset = new();` + `using WaBiBaBuSy.Models.Networking;` + `public long ClockOffsetMs => _clockOffset.OffsetMs;`
- [ ] **Step 2:** In `SendHeartbeatAsync` (line ~281): capture `t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` (reuse as `request.Timestamp`), after `HeartbeatAsync` capture `t3`, then `if (response.Acknowledged) _clockOffset.AddSample(t0, response.ServerTimestamp, t3);`
- [ ] **Step 3:** In `StartSyncStream`'s `await foreach` loop (line ~694), immediately after receiving `command`, convert server-clock timestamps to client-clock terms:

```csharp
// Convert server-clock timestamps into this machine's clock terms so
// deterministic playback math is unaffected by wall-clock skew.
var offset = _clockOffset.OffsetMs;
if (offset != 0)
{
    if (command.TimestampUtc != 0) command.TimestampUtc -= offset;
    if (command.Params != null && command.Params.SharedStartTimestampMs != 0)
        command.Params.SharedStartTimestampMs -= offset;
}
```

- [ ] **Step 4:** Build Core → 0 errors. Commit `feat: clock-offset compensation from heartbeat (NTP-style)`

### Task 5: Auto-reconnection in WallpaperSyncClient

**Files:**
- Modify: `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs`

- [ ] **Step 1:** Add fields: `_serverAddress`, `_serverPort`, `volatile bool _userDisconnected`, `int _reconnecting` (Interlocked flag), `int _heartbeatFailures`, `private readonly ReconnectBackoff _backoff = new();`
- [ ] **Step 2:** `ConnectAsync`: store address/port, set `_userDisconnected = false`, `_heartbeatFailures = 0`, `_backoff.Reset()` on success.
- [ ] **Step 3:** `DisconnectAsync`: first line `_userDisconnected = true;`
- [ ] **Step 4:** Heartbeat loop catch (line ~258): count consecutive failures, ≥3 → `TriggerReconnect("heartbeat failures")` and break. Success path resets `_heartbeatFailures = 0`.
- [ ] **Step 5:** Sync stream generic catch (line ~751): call `TriggerReconnect("sync stream lost")`.
- [ ] **Step 6:** Implement:

```csharp
private void TriggerReconnect(string reason)
{
    if (_userDisconnected) return;
    if (Interlocked.Exchange(ref _reconnecting, 1) == 1) return;
    _logger.LogWarning("Connection lost ({Reason}) — starting auto-reconnect", reason);
    IsConnected = false;
    ConnectionStatusChanged?.Invoke(this,
        new ConnectionStatusChangedEventArgs(false, _serverAddress ?? string.Empty, _serverPort));
    _ = Task.Run(ReconnectLoopAsync);
}

private async Task ReconnectLoopAsync()
{
    try
    {
        while (!_userDisconnected)
        {
            var delay = _backoff.NextDelay();
            _logger.LogInformation("Reconnecting in {Delay}s...", delay.TotalSeconds);
            await Task.Delay(delay);
            if (_userDisconnected) return;

            await TeardownChannelAsync();   // keeps _clientId so the server resumes our session
            try
            {
                if (await ConnectAsync(_serverAddress!, _serverPort))
                {
                    _logger.LogInformation("Reconnected to server after connection loss");
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Reconnect attempt failed: {Message}", ex.Message);
            }
        }
    }
    finally { Interlocked.Exchange(ref _reconnecting, 0); }
}

/// <summary>Tear down streams + channel WITHOUT clearing _clientId or firing user-facing disconnect.</summary>
private async Task TeardownChannelAsync()
{
    StopHeartbeat(); StopSyncStream(); StopCrossScreenFrameStream(); StopThumbnailSending();
    if (_channel != null)
    {
        try { await _channel.ShutdownAsync(); } catch { }
        _channel.Dispose(); _channel = null;
    }
    _client = null;
}
```

Note `ConnectAsync` early-returns when `IsConnected` — `TriggerReconnect` already set it false. Backoff reset on success happens in `ConnectAsync` (Step 2).

- [ ] **Step 7:** Fix `Dispose()` deadlock: `public void Dispose() { _userDisconnected = true; Task.Run(() => DisconnectAsync()).Wait(TimeSpan.FromSeconds(5)); }`
- [ ] **Step 8:** Build Core → 0 errors. Commit `feat: auto-reconnection with exponential backoff`

### Task 6: Session resume (server side)

**Files:**
- Modify: `WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs`
- Modify: `WaBiBaBuSy.Core/Services/WallpaperSyncCoordinator.cs`

- [ ] **Step 1:** `WallpaperSyncService`: add `public event EventHandler<string>? ClientRegistered;` raised at the end of successful `RegisterClient` (after client added to `_connectedClients`).
- [ ] **Step 2:** Coordinator: add `private readonly ConcurrentDictionary<string, SyncCommand> _activeCrossScreenCommands = new();`. In `StartCrossScreenD2DOnClientAsync` store the built command: `_activeCrossScreenCommands[clientId] = command;`. In `StopCrossScreenOnClientAsync` and any stop-all path: `_activeCrossScreenCommands.TryRemove(clientId, out _)` / `Clear()`.
- [ ] **Step 3:** Where the coordinator gets `_syncService` (its `Initialize`/setter), subscribe `ClientRegistered` → handler:

```csharp
private async void OnClientRegistered(object? sender, string clientId)
{
    if (!_activeCrossScreenCommands.TryGetValue(clientId, out var command)) return;
    // The SyncStream opens shortly after registration; retry until the stream exists.
    for (int i = 0; i < 10; i++)
    {
        await Task.Delay(500);
        if (_syncService != null && await _syncService.SendCommandToClientAsync(clientId, command))
        {
            _logger.LogInformation("Resumed active cross-screen animation on reconnected client {ClientId}", clientId);
            return;
        }
    }
    _logger.LogWarning("Could not resume animation on reconnected client {ClientId} (stream never came up)", clientId);
}
```

Original `shared_start_timestamp_ms` is re-sent unchanged — epoch back-dating in the player makes the rejoining client land at the current animation position.

- [ ] **Step 4:** Build → 0 errors. Commit `feat: session resume — re-send active animation on client re-register`

### Task 7: Server hardening

**Files:**
- Modify: `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncServerHost.cs`
- Modify: `WaBiBaBuSy.Grpc/Services/WallpaperSyncService.cs`

- [ ] **Step 1 (bind failure):** Replace `_ = _host.RunAsync(); await Task.Delay(500);` with `await app.StartAsync();` (exceptions like port-in-use now propagate to the existing catch). `StopAsync` keeps `_host.StopAsync(...)`. Fix `Dispose()`: `Task.Run(() => StopAsync()).Wait(TimeSpan.FromSeconds(10));`
- [ ] **Step 2 (write serialization):** In `WallpaperSyncService` add `private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();` and a helper:

```csharp
private async Task WriteToClientStreamAsync(string clientId, IServerStreamWriter<SyncCommand> stream, SyncCommand command)
{
    var gate = _writeLocks.GetOrAdd(clientId, _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync();
    try { await stream.WriteAsync(command); }
    finally { gate.Release(); }
}
```

Use it in `SendCommandToClientAsync` and in `BroadcastCommandAsync`'s per-client writes. Remove the lock entry in `RemoveClient`.

- [ ] **Step 3 (dead-client sweep):** In the service constructor start `private readonly Timer _heartbeatSweepTimer;` firing every 10s:

```csharp
private void SweepDeadClients(object? state)
{
    var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30; // LastHeartbeat is in SECONDS
    foreach (var kvp in _connectedClients)
    {
        if (kvp.Value.LastHeartbeat < cutoff)
        {
            _logger.LogWarning("Client {ClientId} ({Hostname}) heartbeat timed out — removing", kvp.Key, kvp.Value.Hostname);
            RemoveClient(kvp.Key);
        }
    }
}
```

Guard: skip sweep for entries younger than 30s since registration (fresh clients whose heartbeat hasn't started). `RemoveClient` must be idempotent (verify: it already TryRemoves from dictionaries).

- [ ] **Step 4:** Build → 0 errors. Commit `fix: server bind-failure detection, per-client write locks, dead-client sweep`

### Task 8: Remote parameter parity + Simultaneous fix

**Files:**
- Modify: `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` (SyncParameters)
- Modify: `WaBiBaBuSy.Core/Services/WallpaperSyncCoordinator.cs:377-435`
- Modify: `WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs:39,225-251`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (`ApplyCrossScreenD2DFromRemoteAsync`, send loop ~3033, delegate registration sites)
- Test: `WaBiBaBuSy.Tests/ConfigJsonRoundtripTests.cs`

- [ ] **Step 1 (proto):** Append to `SyncParameters`:

```proto
  string movement_json = 18;        // JSON-serialized MovementConfig (empty = derive from movement_type)
  string animation_json = 19;       // JSON-serialized AnimationLayerConfig (AnimationPath overridden client-side)
  string background_json = 20;      // JSON-serialized BackgroundLayerConfig (empty = solid background_color)
  int32 target_monitor_index = 21;  // Remote monitor to render on (default 0 = primary)
```

- [ ] **Step 2 (roundtrip test):** Serialize a `MovementConfig` with all-non-default values (SineWave, Reversed=true, Endless=true, amplitude 123.5f, custom seed) + an `AnimationLayerConfig` (SpeedMultiplier, VerticalAlign, MultiImageSpread) through `JsonSerializer` and assert field equality after deserialize. Run → PASS (guards against non-serializable members like `PrecomputedPath`; if it fails on a member, mark that member `[JsonIgnore]`-exempt or adjust — PrecomputedPath must roundtrip since it ships the global IconZone path).
- [ ] **Step 3 (coordinator):** Change `StartCrossScreenD2DOnClientAsync` signature: add `MovementConfig? movement = null, AnimationLayerConfig? animation = null, BackgroundLayerConfig? background = null, int targetMonitorIndex = 0` (keep existing params for compat). Populate `MovementJson`, `AnimationJson`, `BackgroundJson`, `TargetMonitorIndex` in `SyncParameters` via `JsonSerializer.Serialize` when non-null.
- [ ] **Step 4 (UI send, ~line 3040):** Pass `movement: _crossScreenConfig.Movement`, `animation: animationConfig` (the IconZone-cloned one, so the precomputed global path now reaches remotes too), `background: _crossScreenConfig.Background`, `targetMonitorIndex: remoteClient.MonitorIndex`.
- [ ] **Step 5 (playback service):** Replace the 12-arg delegate with a DTO — Create `WaBiBaBuSy.Models/Wallpaper/CrossScreenApplyRequest.cs`:

```csharp
namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>Everything a client needs to start a cross-screen D2D animation, as received from the server.</summary>
public class CrossScreenApplyRequest
{
    public required string FilePath { get; init; }        // locally-cached content path
    public int MonitorIndex { get; init; }
    public string BackgroundColor { get; init; } = "#000000";
    public int FitMode { get; init; }
    public int VirtualCanvasWidth { get; init; }
    public int MonitorOffsetX { get; init; }
    public long SharedStartTimestampMs { get; init; }
    public int PixelsPerSecond { get; init; }
    public bool PerMonitorMode { get; init; }
    public int MovementType { get; init; }
    public string PatternJson { get; init; } = "";
    public string ColorGradingJson { get; init; } = "";
    public string MovementJson { get; init; } = "";
    public string AnimationJson { get; init; } = "";
    public string BackgroundJson { get; init; } = "";
}
```

Change `D2DCrossScreenApplyDelegate` to `Func<CrossScreenApplyRequest, Task>?`; populate all fields from `command.Params` in `HandleLoadCommandAsync` (`MonitorIndex = command.Params?.TargetMonitorIndex ?? 0`).

- [ ] **Step 6 (UI receive):** `ApplyCrossScreenD2DFromRemoteAsync(CrossScreenApplyRequest req)`:
  - Movement: `req.MovementJson` non-empty → deserialize full `MovementConfig` (and set `SpeedPixelsPerSecond` from it, not the int param); else legacy int fallback.
  - Animation: `req.AnimationJson` non-empty → deserialize `AnimationLayerConfig`, then override `AnimationPath = req.FilePath` and clear `AdditionalAnimationPaths` entries that don't exist locally (multi-image files aren't transferred yet — log a warning listing dropped paths); else legacy construction.
  - Background: `req.BackgroundJson` non-empty → deserialize; if mode is Stretched/TiledImage and `ImagePath` missing on disk → fall back to `SolidColor` + `req.BackgroundColor`, log warning; else legacy solid color.
  - **Simultaneous fix:** `explicitVirtualCanvasWidth: req.PerMonitorMode ? null : req.VirtualCanvasWidth`, `explicitMonitorOffsetX: req.PerMonitorMode ? null : req.MonitorOffsetX` — mirrors the local path (`MainWindowViewModel.cs:2992-2993`).
  - Speed for `StartAsync`: from deserialized movement when present (Static → 0).
- [ ] **Step 7:** Update every `D2DCrossScreenApplyDelegate` assignment site (search: `MainWindowViewModel`, `TrayViewModel`) to the new signature.
- [ ] **Step 8:** Build full solution → 0 errors. Run tests → PASS. Commit `feat: full remote parameter parity (movement/animation/background JSON) + fix Simultaneous on remotes`

### Task 9: Real remote monitor geometry (PixelsPerCm)

**Files:**
- Modify: `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` (MonitorInfo)
- Modify: `WaBiBaBuSy.Core/Services/Networking/WallpaperSyncClient.cs` (`GetScreenConfiguration`, ~line 812)
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (topology → `ClientNodeViewModel` mapping; screenConfigs at 2842-2855)
- Modify: `WaBiBaBuSy.UI/ViewModels/ClientNodeViewModel.cs`

- [ ] **Step 1 (proto):** `MonitorInfo` += `float pixels_per_cm = 9;`
- [ ] **Step 2 (client):** In `GetScreenConfiguration`, populate `PixelsPerCm` per monitor. Core cannot reference WallpaperEngine, so use a local physical-DPI helper (P/Invoke `GetDpiForMonitor(MonitorFromPoint(...), MDT_RAW_DPI)`, fall back to `MDT_EFFECTIVE_DPI`, then 0 = unknown; px/cm = dpi / 2.54).
- [ ] **Step 3 (server/UI):** Add `PixelsPerCm` to `ClientNodeViewModel`; populate from topology `ClientInfo.ScreenConfig.Monitors[MonitorIndex].PixelsPerCm` where the topology is mapped (find via grep `MonitorWidth =` in MainWindowViewModel). In `screenConfigs` (line 2852-2854) use: remote → `c.PixelsPerCm > 0 ? c.PixelsPerCm : fallbackPixelsPerCm`.
- [ ] **Step 4:** Build → 0 errors. Commit `feat: transmit real per-monitor pixels-per-cm; use it for remote gap math`

### Task 10: Final verification & docs

- [ ] `dotnet build` full solution → 0 errors; `dotnet test` → all green.
- [ ] Update `.docs/2025.12_OpenIssues.md` (mark items 1-4, 7-10 of the audit fixed), `.docs/2025.12_MissingFeatures.md` (reconnection complete), `CLAUDE.md` Current Work + MVP criteria (6/6), `.docs/RECENT_UPDATES.md` new entry.
- [ ] `graphify update .`
- [ ] Commit `docs: tier 1 reliability completion`

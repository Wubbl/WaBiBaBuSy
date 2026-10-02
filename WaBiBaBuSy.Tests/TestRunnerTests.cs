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

    private TestRunner Runner(Action<TestRunnerOptions>? configure = null)
    {
        var options = new TestRunnerOptions
        {
            MinLeadMs = 20, ProbeGraceMs = 500, UploadGraceMs = 200,
            ResultsRoot = Path.Combine(_dir, "results"), LocalLogDirectory = Path.Combine(_dir, "no-logs"),
        };
        configure?.Invoke(options);
        return new(_host, _transport, NullLogger<TestRunner>.Instance, options);
    }

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
    public async Task MemoryThreshold_AppliesToRemoteNodes_NotToTheServer()
    {
        // A 1 MB threshold: the real local sample (this test process) is far above it, so only the server exemption keeps it out.
        var scenario = ScenarioLoader.Parse($$"""
            { "name": "unit", "requires": { "minRemoteNodes": 1 }, "thresholds": { "maxMemoryMb": 1 },
              "steps": [ {{PlayMarker}}, { "type": "probeSeries", "label": "perf", "everyMs": 40, "forMs": 40, "capture": false, "perf": true } ] }
            """, _dir);
        _transport.RemoteMemoryMb = 500;
        var report = await Runner().RunAsync(scenario, "unit.json", CancellationToken.None);
        var violations = report.Steps[1].Probes.SelectMany(p => p.PerfViolations).ToList();
        Assert.Contains(violations, v => v.StartsWith("pc-02 memory 500 MB > 1 MB"));
        Assert.DoesNotContain(violations, v => v.StartsWith("server memory"));   // the server also hosts the UI and every local player
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

    [Fact]
    public async Task ExactFrame_UnreadableFrame_FailsOnItsOwn_NextFrameIsTheReference()
    {
        _host.AddLocalMonitor(1);
        _host.AddLocalMonitor(2);
        _host.Target(0).CorruptCapture = true;
        var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "exactFrame", "label": "px", "elapsedMs": 5000 }"""),
            "unit.json", CancellationToken.None);
        var parity = report.Steps[1].Probes[0].Parity;
        var bad = parity.Single(p => p.NodeName == "server #0");
        Assert.Equal(Verdict.Fail, bad.Verdict);
        Assert.StartsWith("frame unreadable", bad.Message);
        Assert.Equal("server #1", bad.ReferenceNodeName);
        var good = parity.Single(p => p.NodeName == "server #2");
        Assert.Equal(Verdict.Pass, good.Verdict);
        Assert.Equal("server #1", good.ReferenceNodeName);
    }

    [Fact]
    public async Task SilentRemote_SeriesStaysOnSchedule()
    {
        _transport.SilentUntilTimeout = true;
        var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "probeSeries", "label": "s", "everyMs": 150, "forMs": 750, "perf": false, "capture": false }"""),
            "unit.json", CancellationToken.None);
        var step = report.Steps[1];
        Assert.DoesNotContain("timed out", step.Message);
        Assert.Equal(5, step.Probes.Count);
        Assert.All(step.Probes, p => Assert.True(p.Samples.Single(s => s.NodeName == "pc-02").Missing));
        // Awaiting each probe would take 5 x (grace 500 + upload 200 ms) = 3500 ms; on schedule it is about 500 ms + one grace.
        Assert.True(step.DurationMs < 2500, $"series took {step.DurationMs} ms");
    }

    [Fact]
    public async Task PlaySceneThrows_PreviousSceneStillRestored()
    {
        _host.ThrowOnPlay = true;
        var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "probe", "label": "p", "at": "now+30ms", "capture": false }"""),
            "unit.json", CancellationToken.None);
        Assert.Equal(Verdict.Fail, report.Steps[0].Verdict);
        Assert.Equal(Verdict.Fail, report.Steps[1].Verdict);             // no scene is playing
        Assert.Equal("no scene is playing", report.Steps[1].Message);
        Assert.True(_host.Restored);
        Assert.True(File.Exists(Path.Combine(report.ResultsDirectory, "report.json")));
    }

    [Fact]
    public async Task UnexpectedHostException_ReportsAborted()
    {
        _host.ThrowOnGetNodes = true;
        var report = await Runner().RunAsync(Scenario(PlayMarker), "unit.json", CancellationToken.None);
        Assert.True(report.Aborted);
        Assert.Contains("node list unavailable", report.AbortReason);
        Assert.True(File.Exists(Path.Combine(report.ResultsDirectory, "report.json")));
    }

    [Fact]
    public async Task CancelledSeries_KeepsCompletedProbes()
    {
        using var cts = new CancellationTokenSource();
        var run = Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "probeSeries", "label": "s", "everyMs": 100, "forMs": 5000, "perf": false, "capture": false }"""),
            "unit.json", cts.Token);
        await Task.Delay(700);
        cts.Cancel();
        var report = await run;

        Assert.True(report.Aborted);
        var step = report.Steps[1];
        Assert.True(step.Probes.Count >= 3, $"kept {step.Probes.Count} probes");
        Assert.True(step.Probes.Count < 50);
        Assert.Equal(step.Probes.Select(p => p.ProbeId).OrderBy(id => id), step.Probes.Select(p => p.ProbeId));   // schedule order
        Assert.NotEqual(Verdict.Skipped, step.Verdict);
    }

    [Fact]
    public async Task FaultingProbe_BecomesFailedProbe_SeriesContinues()
    {
        _transport.ThrowOnProbeId = "02-002";
        var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "probeSeries", "label": "s", "everyMs": 150, "forMs": 600, "perf": false, "capture": false }"""),
            "unit.json", CancellationToken.None);
        var step = report.Steps[1];
        Assert.False(report.Aborted, report.AbortReason);
        Assert.Equal(4, step.Probes.Count);
        Assert.Equal(Verdict.Fail, step.Probes[1].Verdict);
        Assert.Contains("probe exploded", step.Probes[1].Error);
        Assert.All(step.Probes.Where(p => p.ProbeId != "02-002"), p => Assert.Null(p.Error));
        Assert.Equal(Verdict.Fail, step.Verdict);
        Assert.Contains("1 probe(s) failed", step.Message);
    }

    [Fact]
    public async Task UnmatchedSkewKey_FailsTheTestModeStep()
    {
        var report = await Runner().RunAsync(Scenario("""{ "type": "testMode", "timecode": true, "simulatedClockSkewMs": { "REMOTE-HOSTNAME-1": 2000 } }"""),
            "unit.json", CancellationToken.None);
        Assert.Equal(Verdict.Fail, report.Steps[0].Verdict);
        Assert.Contains("simulated skew key 'REMOTE-HOSTNAME-1' matches no connected node", report.Steps[0].Message);
        Assert.Equal((true, 0), _transport.Modes[0]);   // nothing applied to pc-02
    }

    [Fact]
    public async Task SkewKey_MatchesNodeNameCaseInsensitively()
    {
        var report = await Runner().RunAsync(Scenario("""{ "type": "testMode", "timecode": false, "simulatedClockSkewMs": { "PC-02": -1500 } }"""),
            "unit.json", CancellationToken.None);
        Assert.Equal(Verdict.Pass, report.Steps[0].Verdict);
        Assert.Equal((false, -1500), _transport.Modes[0]);
    }

    [Fact]
    public async Task SkewNotSeenInTheClockOffset_FailsTheFirstProbe()
    {
        _transport.IgnoreSkew = true;   // the client acknowledges TEST_MODE but its clock offset never moves
        var report = await Runner().RunAsync(Scenario($$"""
            { "type": "testMode", "timecode": false, "simulatedClockSkewMs": { "pc-02": 2000 } },
            {{PlayMarker}},
            { "type": "probe", "label": "p", "at": "now+30ms", "capture": false }
            """), "unit.json", CancellationToken.None);
        var probe = report.Steps[2].Probes[0];
        Assert.Equal(Verdict.Fail, probe.Verdict);
        Assert.Contains(probe.Messages, m => m.Contains("simulated skew on pc-02 not seen"));
        Assert.Contains("simulated skew on pc-02 not seen", report.Steps[2].Message);
    }

    [Fact]
    public async Task RemoteLosesItsCommandStream_RecordsOneNodeFailure_RunContinues()
    {
        _transport.NoStreamFromProbeId = "02-002";
        var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "probeSeries", "label": "s", "everyMs": 150, "forMs": 600, "perf": false, "capture": false }, { "type": "stop" }"""),
            "unit.json", CancellationToken.None);

        Assert.False(report.Aborted, report.AbortReason);
        var failure = Assert.Single(report.NodeFailures);
        Assert.Equal("client-1", failure.NodeId);
        Assert.Equal("pc-02", failure.Name);
        Assert.True(failure.AtUtcMs > 0);
        Assert.Contains(TestModeErrors.NoCommandStream, failure.Reason);
        Assert.Equal(4, report.Steps[1].Probes.Count);                                        // the series kept going
        Assert.Equal(Verdict.Pass, report.Steps[2].Verdict);                                  // and so did the run
        Assert.Equal(Verdict.Warn, report.Verdict);
        Assert.Contains("Node failures", File.ReadAllText(Path.Combine(report.ResultsDirectory, "report.html")));
    }

    [Fact]
    public async Task RemoteDisconnectsDuringAStep_RecordsNodeFailureAtTheStepBoundary_AndIsSkippedAfterwards()
    {
        _host.DisconnectOnPlay = "client-1";   // gone right after the scene starts, before any probe
        _transport.SilentUntilTimeout = true;   // and it would never answer one
        var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "probe", "label": "p", "at": "now+30ms", "capture": false }, { "type": "stop" }"""),
            "unit.json", CancellationToken.None);

        Assert.False(report.Aborted, report.AbortReason);
        var failure = Assert.Single(report.NodeFailures);
        Assert.Equal("client-1", failure.NodeId);
        Assert.Contains("disconnected", failure.Reason);
        Assert.True(failure.AtUtcMs <= report.Steps[1].StartedUtcMs);                         // noticed when the play step ended
        Assert.DoesNotContain(report.Steps[1].Probes[0].Samples, s => s.NodeId == "client-1"); // not probed (and not "missing") afterwards
        Assert.Equal(Verdict.Warn, report.Verdict);
    }

    [Fact]
    public async Task DuplicateNodeNames_GetSeparateLogsAndCaptures()
    {
        _host.AddRemote("client-2", "pc-02");    // same hostname as client-1
        _host.AddRemote("client-3", "Server");   // would collide with the server's own logs/server.log
        _transport.RemoteCaptures = true;
        var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "probe", "label": "p", "at": "now+30ms", "capture": true }"""),
            "unit.json", CancellationToken.None);

        var captures = report.Steps[1].Probes[0].Samples.Where(s => !s.IsLocal).Select(s => s.CapturePath).ToList();
        Assert.Equal(3, captures.Count);
        Assert.All(captures, c => Assert.True(File.Exists(Path.Combine(report.ResultsDirectory, c!)), c));
        Assert.Equal(3, captures.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.Equal(3, report.LogFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("logs/server.log", report.LogFiles, StringComparer.OrdinalIgnoreCase);
        Assert.All(report.LogFiles, f => Assert.True(File.Exists(Path.Combine(report.ResultsDirectory, f)), f));
    }

    [Fact]
    public async Task StepTimeout_FailsTheStep_RunContinues()
    {
        var report = await Runner().RunAsync(Scenario("""{ "type": "wait", "ms": 5000, "timeoutMs": 100 }, { "type": "stop" }"""),
            "unit.json", CancellationToken.None);
        Assert.False(report.Aborted, report.AbortReason);
        Assert.Equal(Verdict.Fail, report.Steps[0].Verdict);
        Assert.Equal("timed out after 100 ms", report.Steps[0].Message);
        Assert.InRange(report.Steps[0].DurationMs, 50, 3000);
        Assert.Equal(Verdict.Pass, report.Steps[1].Verdict);
    }

    [Fact]
    public async Task CancellationNobodyAskedFor_InAStep_IsAFailure_NotATimeout()
    {
        _host.PlayException = new OperationCanceledException("player host gave up");
        var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "stop" }"""), "unit.json", CancellationToken.None);
        Assert.False(report.Aborted, report.AbortReason);
        Assert.Equal(Verdict.Fail, report.Steps[0].Verdict);
        Assert.Equal("player host gave up", report.Steps[0].Message);
        Assert.Equal(Verdict.Pass, report.Steps[1].Verdict);
    }

    [Fact]
    public async Task CancellationNobodyAskedFor_AtRunLevel_IsNotReportedAsCancelled()
    {
        _transport.ThrowOnProbeId = "preflight";
        _transport.ProbeException = new TaskCanceledException("transport gave up");
        var report = await Runner().RunAsync(Scenario(PlayMarker), "unit.json", CancellationToken.None);
        Assert.True(report.Aborted);
        Assert.Equal("transport gave up", report.AbortReason);
    }

    [Fact]
    public async Task ProbeAtALongOffset_ExtendsTheStepTimeout()
    {
        var report = await Runner(o => o.DefaultStepTimeoutMs = 200).RunAsync(
            Scenario($$"""{{PlayMarker}}, { "type": "probe", "label": "p", "at": "now+400ms", "capture": false }"""),
            "unit.json", CancellationToken.None);
        Assert.Equal(Verdict.Pass, report.Steps[1].Verdict);
        Assert.DoesNotContain("timed out", report.Steps[1].Message);
    }

    [Fact]
    public async Task CancelDuringPreflight_AbortsWithoutSpuriousWarnings()
    {
        _transport.HoldPreflightUntilCancelled = true;
        using var cts = new CancellationTokenSource(200);
        var report = await Runner().RunAsync(Scenario(PlayMarker), "unit.json", cts.Token);
        Assert.True(report.Aborted);
        Assert.Equal("cancelled", report.AbortReason);
        Assert.Empty(report.Warnings);
        Assert.Equal(0, _host.PlayCount);
    }

    [Fact]
    public async Task BadSceneFile_IsRejectedBeforePreflightProbesTheRemotes()
    {
        File.WriteAllText(Path.Combine(_dir, "scenes", "bad.json"), """{ "Animation": { "AnimationPath": "missing.png" } }""");
        var report = await Runner().RunAsync(Scenario("""{ "type": "playScene", "scene": "scenes/bad.json" }"""), "unit.json", CancellationToken.None);
        Assert.True(report.Aborted);
        Assert.Contains("asset not found", report.AbortReason);
        Assert.Equal(0, _transport.ProbeCount);
        Assert.Equal(0, _host.PlayCount);
    }

    [Fact]
    public async Task CancelledMidStep_StepGetsDurationAndVerdict()
    {
        using var cts = new CancellationTokenSource();
        var run = Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "wait", "ms": 10000 }"""), "unit.json", cts.Token);
        await Task.Delay(300);
        cts.Cancel();
        var report = await run;

        Assert.True(report.Aborted);
        var step = report.Steps[1];
        Assert.Equal(Verdict.Warn, step.Verdict);
        Assert.Contains("cancelled", step.Message);
        Assert.InRange(step.DurationMs, 100, 5000);
    }

    [Fact]
    public async Task CancelledConcurrentSeries_KeepsCompletedProbes_InOrder()
    {
        // everyMs >= MinLeadMs + 100: probes overlap (each waits out grace + upload for the silent remote).
        _transport.SilentUntilTimeout = true;
        using var cts = new CancellationTokenSource();
        var run = Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "probeSeries", "label": "s", "everyMs": 150, "forMs": 10000, "perf": false, "capture": false }"""),
            "unit.json", cts.Token);
        // Preflight waits out the silent remote too (~0.5 s); the first probe answers ~1.3 s in.
        await Task.Delay(2500);
        cts.Cancel();
        var report = await run;

        Assert.True(report.Aborted);
        Assert.Equal("cancelled", report.AbortReason);
        var step = report.Steps[1];
        Assert.InRange(step.Probes.Count, 1, 20);   // ~13 started, ~5 still in flight at the cancel: only answered ones are kept
        Assert.Equal(step.Probes.Select(p => p.ProbeId).OrderBy(id => id, StringComparer.Ordinal), step.Probes.Select(p => p.ProbeId));
        Assert.All(step.Probes, p => Assert.Null(p.Error));
        Assert.True(step.DurationMs > 0);
        Assert.True(File.Exists(Path.Combine(report.ResultsDirectory, "report.json")));
    }

    [Theory]
    [InlineData("""{ "type": "probe", "label": "p", "at": "now+30ms", "capture": false }""")]
    [InlineData("""{ "type": "exactFrame", "label": "px", "elapsedMs": 5000 }""")]
    public async Task FaultingSingleProbe_ErrorShowsInTheStepMessage(string step)
    {
        _transport.ThrowOnProbeId = "02-001";
        var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, {{step}}"""), "unit.json", CancellationToken.None);
        Assert.Equal(Verdict.Fail, report.Steps[1].Verdict);
        Assert.Contains("probe exploded", report.Steps[1].Message);
    }

    [Fact]
    public async Task PartiallyFaultingProbe_LeavesNoUnobservedTask()
    {
        string marker = $"late remote fault {Guid.NewGuid():N}";
        var unobserved = new List<string>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
                unobserved.AddRange(e.Exception.Flatten().InnerExceptions.Select(x => x.Message));
        }

        _host.AddRemote("client-2", "pc-03");
        _transport.ProbeOverride = (clientId, request, ct) =>
        {
            if (request.ProbeId == "preflight") return Task.FromResult<RemoteProbeResult?>(new RemoteProbeResult { ClientId = clientId, ProbeId = request.ProbeId });
            if (clientId == "client-2") throw new InvalidOperationException("synchronous fault");
            return FaultLater(marker);
        };
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var report = await Runner().RunAsync(Scenario($$"""{{PlayMarker}}, { "type": "probe", "label": "p", "at": "now+30ms", "capture": false }"""),
                "unit.json", CancellationToken.None);
            Assert.Equal(Verdict.Fail, report.Steps[1].Verdict);
            await Task.Delay(300);   // let the late fault happen
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
        lock (unobserved) Assert.DoesNotContain(marker, unobserved);

        static async Task<RemoteProbeResult?> FaultLater(string message)
        {
            await Task.Delay(100);
            throw new InvalidOperationException(message);
        }
    }

    [Fact]
    public async Task ProbeBeforeTheFirstPostResetHeartbeat_DefersTheSkewCheck()
    {
        _transport.NoEstimateProbeIds.Add("03-001");   // the client reset its estimate for the skew; no heartbeat yet
        var report = await Runner().RunAsync(Scenario($$"""
            { "type": "testMode", "timecode": false, "simulatedClockSkewMs": { "pc-02": 2000 } },
            {{PlayMarker}},
            { "type": "probe", "label": "early", "at": "now+30ms", "capture": false },
            { "type": "probe", "label": "later", "at": "now+30ms", "capture": false }
            """), "unit.json", CancellationToken.None);
        Assert.All(report.Steps.Skip(2), s => Assert.DoesNotContain("not seen", s.Message));
        Assert.Equal(Verdict.Pass, report.Steps[2].Verdict);
        Assert.Equal(Verdict.Pass, report.Steps[3].Verdict);
    }

    [Fact]
    public async Task SkewCheck_StillRunsOnTheFirstProbeWithAnEstimate()
    {
        _transport.IgnoreSkew = true;
        _transport.NoEstimateProbeIds.Add("03-001");
        var report = await Runner().RunAsync(Scenario($$"""
            { "type": "testMode", "timecode": false, "simulatedClockSkewMs": { "pc-02": 2000 } },
            {{PlayMarker}},
            { "type": "probe", "label": "early", "at": "now+30ms", "capture": false },
            { "type": "probe", "label": "later", "at": "now+30ms", "capture": false }
            """), "unit.json", CancellationToken.None);
        Assert.DoesNotContain("not seen", report.Steps[2].Message);
        Assert.Contains("simulated skew on pc-02 not seen", report.Steps[3].Message);
    }

    // ── fakes ──────────────────────────────────────────────────────────────

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class FakeTarget : ITestProbeTarget
    {
        public int MonitorIndex { get; init; }
        public long Start { get; set; }
        public int MarkerOffsetX { get; set; }
        /// <summary>Writes a capture file that is not a PNG.</summary>
        public bool CorruptCapture { get; set; }
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
                if (CorruptCapture)
                {
                    Directory.CreateDirectory(request.CaptureDirectory);
                    File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                }
                else PngPixels.Encode(DrawMarker(168 + MarkerOffsetX, 118), W, H, W * 4, path);   // static marker, centered
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
        public bool ThrowOnPlay { get; set; }
        /// <summary>PlaySceneAsync throws this (e.g. an OperationCanceledException nobody asked for).</summary>
        public Exception? PlayException { get; set; }
        public bool ThrowOnGetNodes { get; set; }
        /// <summary>This remote reports Connected = false from the first scene start on (lost mid-run).</summary>
        public string? DisconnectOnPlay { get; set; }
        public int PlayCount { get; private set; }
        public bool Restored { get; private set; }
        public FakeTarget Target(int monitor) => _targets.Single(t => t.MonitorIndex == monitor);

        public void AddLocalMonitor(int index)
        {
            _targets.Add(new FakeTarget { MonitorIndex = index });
            _nodes.Insert(index, new TestNodeInfo { NodeId = $"SERVER_LOCALHOST_MONITOR_{index}", Name = $"server #{index}", IsLocal = true, MonitorIndex = index, Width = W, Height = H });
        }

        public void AddRemote(string nodeId, string name) =>
            _nodes.Add(new TestNodeInfo { NodeId = nodeId, Name = name, IsLocal = false, Width = W, Height = H, SeatOrder = _nodes.Count });

        public Task<IReadOnlyList<TestNodeInfo>> GetNodesAsync() =>
            ThrowOnGetNodes ? throw new InvalidOperationException("node list unavailable") : Task.FromResult<IReadOnlyList<TestNodeInfo>>(_nodes);

        public Task<SceneStartInfo> PlaySceneAsync(CrossScreenConfig scene, IReadOnlyList<string> targetNodeIds, CancellationToken ct)
        {
            PlayCount++;
            if (ThrowOnPlay) throw new InvalidOperationException("play failed");
            if (PlayException != null) throw PlayException;
            if (DisconnectOnPlay != null) _nodes.Single(n => n.NodeId == DisconnectOnPlay).Connected = false;
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
        /// <summary>Like a node that never answers: each probe waits out its whole timeout, then returns null.</summary>
        public bool SilentUntilTimeout { get; set; }
        /// <summary>The transport call itself throws for this probe id (an error that really faults the runner's probe).</summary>
        public string? ThrowOnProbeId { get; set; }
        public string? Error { get; set; }
        /// <summary>When set, remote probe results carry a perf sample with this much player memory.</summary>
        public double? RemoteMemoryMb { get; set; }
        /// <summary>The simulated skew is acknowledged but never reaches the reported clock offset.</summary>
        public bool IgnoreSkew { get; set; }
        /// <summary>From this probe id on (ordinal), the client has no command stream (disconnected).</summary>
        public string? NoStreamFromProbeId { get; set; }
        /// <summary>What <see cref="ThrowOnProbeId"/> throws (default: InvalidOperationException "probe exploded").</summary>
        public Exception? ProbeException { get; set; }
        /// <summary>The preflight probe hangs until the run is cancelled, then answers null (like ServerTestChannel).</summary>
        public bool HoldPreflightUntilCancelled { get; set; }
        /// <summary>Probes answered before the client's first post-reset heartbeat: no clock estimate yet (offset 0, RTT 0).</summary>
        public HashSet<string> NoEstimateProbeIds { get; } = new();
        /// <summary>Each remote reply carries a capture PNG for monitor 0.</summary>
        public bool RemoteCaptures { get; set; }
        /// <summary>Replaces the whole probe behaviour for a client (null result = fall through to the default).</summary>
        public Func<string, ProbeRequest, CancellationToken, Task<RemoteProbeResult?>>? ProbeOverride { get; set; }
        public int ProbeCount;
        private int _skewMs;

        public Task<bool> SendTestModeAsync(string clientId, bool timecode, int clockSkewMs)
        {
            Modes.Add((timecode, clockSkewMs));
            if (!IgnoreSkew) _skewMs = clockSkewMs;
            return Task.FromResult(true);
        }

        public Task<RemoteProbeResult?> ProbeAsync(string clientId, ProbeRequest request, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref ProbeCount);
            if (ProbeOverride != null) return ProbeOverride(clientId, request, ct);
            if (HoldPreflightUntilCancelled && request.ProbeId == "preflight") return HeldUntilCancelled(ct);
            if (SilentUntilTimeout) return NeverAnswers(timeout, ct);
            if (request.ProbeId == ThrowOnProbeId) throw ProbeException ?? new InvalidOperationException("probe exploded");
            if (Silent) return Task.FromResult<RemoteProbeResult?>(null);
            if (Error != null) return Task.FromResult<RemoteProbeResult?>(new RemoteProbeResult { ClientId = clientId, ProbeId = request.ProbeId, Error = Error });
            if (NoStreamFromProbeId != null && request.ProbeId != "preflight" && string.CompareOrdinal(request.ProbeId, NoStreamFromProbeId) >= 0)
                return Task.FromResult<RemoteProbeResult?>(new RemoteProbeResult { ClientId = clientId, ProbeId = request.ProbeId, Error = TestModeErrors.NoCommandStream });
            // Client clock 1000 ms behind the server (plus any simulated skew); the offset (server − client) is 1000 − skew.
            bool noEstimate = NoEstimateProbeIds.Contains(request.ProbeId);
            long offset = noEstimate ? 0 : 1000 - _skewMs;
            long renderLocal = request.AtServerUtcMs - offset;
            var result = new RemoteProbeResult
            {
                ClientId = clientId, ProbeId = request.ProbeId, ClockOffsetMs = offset, RttMs = noEstimate ? 0 : 2,
                Perf = RemoteMemoryMb is { } mb ? new PerfSample { PlayerMemoryMb = mb } : null,
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
            };
            if (RemoteCaptures && request.Capture)
            {
                var png = Path.Combine(Path.GetTempPath(), $"wbbs-remote-{Guid.NewGuid():N}.png");
                PngPixels.Encode(DrawMarker(168, 118), W, H, W * 4, png);
                result.Captures[0] = File.ReadAllBytes(png);
                File.Delete(png);
            }
            return Task.FromResult<RemoteProbeResult?>(result);
        }

        private static async Task<RemoteProbeResult?> NeverAnswers(TimeSpan timeout, CancellationToken ct)
        {
            await Task.Delay(timeout, ct);
            return null;
        }

        private static async Task<RemoteProbeResult?> HeldUntilCancelled(CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { /* the real channel answers null on cancel */ }
            return null;
        }

        public Task<string?> FetchLogsAsync(string clientId, long fromUtcMs, long toUtcMs, TimeSpan timeout) =>
            Task.FromResult<string?>("[12:00:00.000 INF] remote log");
    }
}

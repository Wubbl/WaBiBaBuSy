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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test run \"{Scenario}\" aborted by an unexpected error", scenario.Name);
            report.Aborted = true;
            report.AbortReason = ex.Message;
        }
        finally
        {
            try
            {
                await CleanupAsync(ctx, previousScene);
                await CollectLogsAsync(ctx, startedMs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Test run cleanup failed");
                report.Warnings.Add($"cleanup failed: {ex.Message}");
            }
            finally
            {
                report.FinishedUtc = DateTimeOffset.FromUnixTimeMilliseconds(_o.NowUtcMs());
                report.Verdict = report.Steps.Aggregate(Verdict.Skipped, (v, s) => Verdicts.Worst(v, s.Verdict));
                if (report.Aborted) report.Verdict = Verdicts.Worst(report.Verdict, Verdict.Warn);
                try { TestReportWriter.Write(report); }
                catch (Exception ex) { _logger.LogError(ex, "Could not write the test report to {Dir}", report.ResultsDirectory); }
                try { Directory.Delete(Path.Combine(report.ResultsDirectory, "tmp"), recursive: true); } catch { /* may not exist */ }
                _status = _status with { Running = false, ResultsDirectory = report.ResultsDirectory, LastVerdict = report.Verdict };
                try { _host.ReportProgress($"Test: {report.Verdict.ToString().ToLowerInvariant()}{(report.Aborted ? " (aborted)" : "")} · {report.ResultsDirectory}"); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not report test progress"); }
            }
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
                ctx.ScenePlayed = true;   // before the await: a failed / timed-out play still gets the previous scene restored
                ctx.Active = null;
                ctx.ActiveScene = null;
                ctx.Active = await _host.PlaySceneAsync(scene, targets ?? new List<string>(), ct);
                ctx.ActiveScene = scene;
                ctx.ActiveTargets = targets?.ToHashSet();
                ctx.Marker = play.Marker;
                // New local players start without test mode (remote clients apply it before their start).
                await ProbeFanOut.SetTestModeAllAsync(_host.LocalProbeTargets(), ctx.Timecode, 0);
                sr.Scene = play.Scene;
                sr.Verdict = Verdict.Pass;
                sr.Message = $"started {(ctx.Active.PerMonitor ? "Simultaneous" : "Sequential")}, lead {ctx.Active.StartLeadMs} ms";
                break;

            case ProbeStep probe:
                if (ctx.Active == null)
                {
                    sr.Verdict = Verdict.Fail;
                    sr.Message = "no scene is playing";
                    break;
                }
                ProbeAt.TryParse(probe.At, out var anchor, out var offset);
                long at = ProbeAt.Resolve(anchor, offset, ctx.Active!.SharedStartServerUtcMs, _o.NowUtcMs(), _o.MinLeadMs);
                var single = await ProbeOnceAsync(ctx, sr, at, probe.Capture, exactElapsedMs: null, perf: false, ct);
                sr.Probes.Add(single);
                sr.Verdict = single.Verdict;
                sr.Message = single.Drift?.Message ?? string.Empty;
                break;

            case ProbeSeriesStep series:
                try
                {
                    await RunSeriesAsync(ctx, sr, series, ct);
                }
                finally
                {
                    // Also on cancel / timeout: summarise the probes that completed (RunAsync may still override with Fail).
                    sr.Verdict = sr.Probes.Aggregate(Verdict.Skipped, (v, p) => Verdicts.Worst(v, p.Verdict));
                    var spreads = sr.Probes.Where(p => p.Drift is { Verdict: not Verdict.Skipped }).Select(p => p.Drift!.SpreadMs).ToList();
                    sr.Message = spreads.Count == 0
                        ? "no timing data"
                        : $"{sr.Probes.Count} probes, spread max {spreads.Max():F1} ms / mean {spreads.Average():F1} ms";
                    var perfIssues = sr.Probes.SelectMany(p => p.PerfViolations).Distinct().ToList();
                    if (perfIssues.Count > 0) sr.Message += $"; perf: {string.Join("; ", perfIssues)}";
                    var faults = sr.Probes.Where(p => p.Error != null).Select(p => $"{p.ProbeId}: {p.Error}").ToList();
                    if (faults.Count > 0) sr.Message += $"; {faults.Count} probe(s) failed: {string.Join("; ", faults.Take(3))}";
                }
                break;

            case ExactFrameStep exact:
                if (ctx.Active == null || ctx.ActiveScene == null)
                {
                    sr.Verdict = Verdict.Fail;
                    sr.Message = "no scene is playing";
                    break;
                }
                if (ctx.ActiveScene.Background.Mode == BackgroundMode.IconZone)
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

    /// <summary>
    /// Probes on schedule: each probe is started MinLeadMs before its instant without waiting for the previous
    /// one's replies, so a silent node (each probe waits out its grace) cannot stretch the series.
    /// Players hold one pending-probe slot, so when the spacing is below the lead each probe is awaited first.
    /// </summary>
    private async Task RunSeriesAsync(RunContext ctx, StepReport sr, ProbeSeriesStep series, CancellationToken ct)
    {
        long seriesStart = _o.NowUtcMs() + _o.MinLeadMs;
        // A player's single pending-probe slot frees only once a frame at/after the instant rendered: keep a margin.
        bool sequential = series.EveryMs < _o.MinLeadMs + 100;
        var tasks = new List<Task<ProbeReport>>();
        try
        {
            for (long offsetMs = 0; offsetMs < series.ForMs; offsetMs += series.EveryMs)
            {
                ct.ThrowIfCancellationRequested();
                long now = _o.NowUtcMs();
                long due = Math.Max(seriesStart + offsetMs, now + _o.MinLeadMs);
                long waitMs = due - _o.MinLeadMs - now;
                if (waitMs > 0) await Task.Delay((int)Math.Min(waitMs, int.MaxValue), ct);
                // ProbeOnceAsync assigns its probe id before its first await, so ids stay ordered.
                var task = ProbeOnceAsync(ctx, sr, due, series.Capture, exactElapsedMs: null, perf: series.Perf, ct);
                tasks.Add(task);
                if (sequential) await task;
            }
        }
        finally
        {
            // On success, cancel or timeout alike: keep every probe that completed, in schedule order.
            try { await Task.WhenAll(tasks); } catch { /* cancelled probes are simply not kept */ }
            sr.Probes.AddRange(tasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result));
        }
    }

    // ── one probe across all nodes ──────────────────────────────────────────

    private async Task<ProbeReport> ProbeOnceAsync(RunContext ctx, StepReport sr, long atServerUtcMs, bool capture, long? exactElapsedMs, bool perf, CancellationToken ct)
    {
        string probeId = $"{sr.Index:D2}-{++ctx.ProbeSeq:D3}";
        var pr = new ProbeReport { ProbeId = probeId, AtServerUtcMs = atServerUtcMs, Exact = exactElapsedMs != null };
        try
        {
            await CollectProbeAsync(ctx, sr, pr, capture, exactElapsedMs, perf, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // One broken probe (IO error, bad frame, ...) must not take the series or the run down with it.
            _logger.LogWarning(ex, "Probe {ProbeId} failed", probeId);
            pr.Verdict = Verdict.Fail;
            pr.Error = ex.Message;
        }
        return pr;
    }

    private async Task CollectProbeAsync(RunContext ctx, StepReport sr, ProbeReport pr, bool capture, long? exactElapsedMs, bool perf, CancellationToken ct)
    {
        string probeId = pr.ProbeId;
        long atServerUtcMs = pr.AtServerUtcMs;
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
                try
                {
                    var img = PngPixels.Decode(Path.Combine(ctx.Dir, s.CapturePath));
                    detected = MarkerDetector.Detect(img.Pixels, img.Width, img.Height, img.Stride);
                }
                catch (Exception ex)
                {
                    pr.Positions.Add(new PositionCheckResult { NodeId = s.NodeId, NodeName = s.NodeName, MonitorIndex = s.MonitorIndex, Verdict = Verdict.Fail, Message = $"frame unreadable: {ex.Message}" });
                    pr.Verdict = Verdicts.Worst(pr.Verdict, Verdict.Fail);
                    continue;
                }
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
            // The first frame that decodes is the reference; every unreadable frame fails on its own.
            var decoded = new List<(NodeProbeSample Node, PngPixels.Image? Image, string? Error)>();
            foreach (var m in members)
            {
                try { decoded.Add((m, PngPixels.Decode(Path.Combine(ctx.Dir, m.CapturePath!)), null)); }
                catch (Exception ex) { decoded.Add((m, null, ex.Message)); }
            }
            var first = decoded.FirstOrDefault(d => d.Image != null);
            foreach (var bad in decoded.Where(d => d.Image == null))
                AddParityFailure(pr, bad.Node, first.Node?.NodeName ?? string.Empty, $"frame unreadable: {bad.Error}");
            if (first.Node == null) continue;
            var reference = first.Node;
            var refImg = first.Image!;
            foreach (var entry in decoded.Where(d => d.Image != null && d.Node != reference))
            {
                var other = entry.Node;
                var img = entry.Image!;
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
                    var rel = RelativeCapturePath(sr, pr.ProbeId, other, "diff-mon" + other.MonitorIndex);
                    PngPixels.Encode(diff.DiffImage, img.Width, img.Height, img.Width * 4, Path.Combine(ctx.Dir, rel));
                    result.DiffImagePath = rel.Replace('\\', '/');
                }
                pr.Parity.Add(result);
                pr.Verdict = Verdicts.Worst(pr.Verdict, result.Verdict);
            }
        }
    }

    private static void AddParityFailure(ProbeReport pr, NodeProbeSample node, string referenceNodeName, string message)
    {
        pr.Parity.Add(new PixelParityResult
        {
            NodeId = node.NodeId,
            NodeName = node.NodeName,
            ReferenceNodeName = referenceNodeName,
            Verdict = Verdict.Fail,
            Message = message,
        });
        pr.Verdict = Verdicts.Worst(pr.Verdict, Verdict.Fail);
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
            // The memory target is per client; the server also hosts the UI and every local player, so it is reported only.
            double memory = p.AppMemoryMb + p.PlayerMemoryMb;
            if (!s.IsLocal && memory > t.MaxMemoryMb) pr.PerfViolations.Add($"{who} memory {memory:F0} MB > {t.MaxMemoryMb} MB");
        }
        if (pr.PerfViolations.Count > 0) pr.Verdict = Verdicts.Worst(pr.Verdict, Verdict.Warn);
    }

    // ── cleanup, logs ───────────────────────────────────────────────────────

    private async Task CleanupAsync(RunContext ctx, CrossScreenConfig? previousScene)
    {
        try
        {
            foreach (var n in ctx.Remotes)
            {
                try { await _transport.SendTestModeAsync(n.NodeId, false, 0); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not switch test mode off on {Node}", n.Name); }
            }
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
        try
        {
            Directory.CreateDirectory(logDir);
        }
        catch (Exception ex)
        {
            ctx.Report.Warnings.Add($"logs not collected: {ex.Message}");
            return;
        }
        foreach (var n in ctx.Remotes)
        {
            string text;
            try { text = await _transport.FetchLogsAsync(n.NodeId, startedMs - 5000, toMs, _o.LogFetchTimeout) ?? "[no reply within the timeout]"; }
            catch (Exception ex) { text = $"[log fetch failed: {ex.Message}]"; }
            var file = $"{Sanitize(n.Name)}.log";
            try
            {
                await File.WriteAllTextAsync(Path.Combine(logDir, file), text);
                ctx.Report.LogFiles.Add($"logs/{file}");
            }
            catch (Exception ex)
            {
                ctx.Report.Warnings.Add($"log of {n.Name} not saved: {ex.Message}");
            }
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

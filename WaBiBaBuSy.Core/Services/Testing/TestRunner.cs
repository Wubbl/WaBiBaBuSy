using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Common.IO;
using WaBiBaBuSy.Core.Services.Show;
using WaBiBaBuSy.Models.Show;
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
    /// <summary>The last BiBaBo game fired in this run (a later BombOutcome step ends it).</summary>
    private ShowEvent? _lastBomb;

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
        /// <summary>Clock offset per remote at preflight (before any simulated skew).</summary>
        public Dictionary<string, double> PreflightOffsetMs { get; } = new();
        /// <summary>Nonzero skews sent by the last testMode step, checked by the next probe that reaches the node.</summary>
        public ConcurrentDictionary<string, (int SkewMs, double BaselineOffsetMs)> PendingSkewChecks { get; } = new();
        /// <summary>Remotes lost mid-run (see <see cref="TestRunReport.NodeFailures"/>); later steps skip them.</summary>
        public ConcurrentDictionary<string, bool> Lost { get; } = new();
        public IEnumerable<TestNodeInfo> LiveRemotes => Remotes.Where(n => !Lost.ContainsKey(n.NodeId));

        private readonly Dictionary<string, string> _fileNames = new();
        // Case-insensitive like the file system; "server" is the server's own logs/server.log.
        private readonly HashSet<string> _usedFileNames = new(StringComparer.OrdinalIgnoreCase) { "server" };

        /// <summary>
        /// The node's capture folder / log file name: sanitized, and unique within the run (two clients can
        /// share a hostname, two names can sanitize alike); later claimants get "-2", "-3", ...
        /// </summary>
        public string FileNameFor(string nodeId, string name)
        {
            lock (_fileNames)
            {
                if (_fileNames.TryGetValue(nodeId, out var known)) return known;
                var baseName = Sanitize(name);
                var unique = baseName;
                for (int n = 2; _usedFileNames.Contains(unique); n++) unique = $"{baseName}-{n}";
                _usedFileNames.Add(unique);
                _fileNames[nodeId] = unique;
                return unique;
            }
        }
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
        TestRestorePoint restorePoint;
        try
        {
            restorePoint = _host.CaptureRestorePoint();
        }
        catch (Exception ex)
        {
            // Not worth failing the run for: afterwards everything is stopped, as if nothing had played.
            _logger.LogWarning(ex, "Could not capture what was playing before the test run");
            report.Warnings.Add($"could not capture what was playing before the run: {ex.Message}");
            restorePoint = TestRestorePoint.Nothing;
        }
        SetStatus(scenario, 0, "preflight", report);

        try
        {
            if (await PreflightAsync(ctx, ct))
            {
                await PrefetchAsync(ctx, ct);
                for (int i = 0; i < scenario.Steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = scenario.Steps[i];
                    var sr = new StepReport { Index = i + 1, Kind = step.Kind, Label = step.Label, StartedUtcMs = _o.NowUtcMs() };
                    report.Steps.Add(sr);
                    SetStatus(scenario, i + 1, step.Label ?? step.Kind, report);
                    _host.ReportProgress($"Test: step {i + 1}/{scenario.Steps.Count} · {step.Label ?? step.Kind}");

                    int timeoutMs = StepTimeoutMs(ctx, step);
                    using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stepCts.CancelAfter(timeoutMs);
                    try
                    {
                        await RunStepAsync(ctx, i, step, sr, stepCts.Token);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // The run is cancelled mid-step: keep what the step measured, mark it incomplete.
                        if (sr.Verdict != Verdict.Fail) sr.Verdict = Verdict.Warn;
                        sr.Message = sr.Message.Length > 0 ? $"{sr.Message}; cancelled" : "cancelled";
                        throw;
                    }
                    catch (OperationCanceledException) when (stepCts.IsCancellationRequested)
                    {
                        sr.Verdict = Verdict.Fail;
                        sr.Message = $"timed out after {timeoutMs} ms";
                    }
                    catch (Exception ex)
                    {
                        // Includes an OperationCanceledException nobody asked for (e.g. an internal timeout).
                        _logger.LogError(ex, "Test step {Index} ({Kind}) failed", i + 1, step.Kind);
                        sr.Verdict = Verdict.Fail;
                        sr.Message = ex.Message;
                    }
                    finally
                    {
                        sr.DurationMs = _o.NowUtcMs() - sr.StartedUtcMs;
                    }
                    if (!ct.IsCancellationRequested) await CheckConnectionsAsync(ctx);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
                await CleanupAsync(ctx, restorePoint);
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
                if (report.Aborted || report.NodeFailures.Count > 0) report.Verdict = Verdicts.Worst(report.Verdict, Verdict.Warn);
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
        foreach (var n in ctx.Nodes) ctx.FileNameFor(n.NodeId, n.Name);   // seat order decides who gets "-2"

        // A bad scene file aborts here (ScenarioException), before anything reaches the remotes.
        LoadScenes(ctx);

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
        var results = await Task.WhenAll(ctx.Remotes.Select(n => RemoteProbeAsync(n.NodeId, probe, timeout, ct)));
        ct.ThrowIfCancellationRequested();   // a cancelled probe answers null: that is not "did not answer"
        var disabled = ctx.Remotes.Where((n, i) => results[i]?.Error == TestModeErrors.Disabled).Select(n => n.Name).ToList();
        if (disabled.Count > 0)
        {
            report.Aborted = true;
            report.AbortReason = $"test runs are disabled on: {string.Join(", ", disabled)} (Settings → Client → Allow test runs)";
            return false;
        }
        for (int i = 0; i < ctx.Remotes.Count; i++)
        {
            if (results[i] == null)
                report.Warnings.Add($"{ctx.Remotes[i].Name} did not answer the preflight probe (older build or no command stream?)");
            ctx.PreflightOffsetMs[ctx.Remotes[i].NodeId] = results[i] is { Error: not TestModeErrors.NoCommandStream } r ? r.ClockOffsetMs : ctx.Remotes[i].ClockOffsetMs;
        }
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
        ct.ThrowIfCancellationRequested();
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
                var live = ctx.LiveRemotes.ToList();
                // A key that matches no node would silently test nothing (e.g. an unedited placeholder hostname).
                var unmatched = mode.SimulatedClockSkewMs.Keys.Where(k => !live.Any(n => SkewKeyMatches(k, n))).ToList();
                var failed = new List<string>();
                ctx.PendingSkewChecks.Clear();
                foreach (var n in live)
                {
                    int skew = SkewFor(mode, n);
                    if (!await _transport.SendTestModeAsync(n.NodeId, mode.Timecode, skew))
                    {
                        failed.Add(n.Name);
                        RecordNodeFailure(ctx, n, $"test mode not delivered: {TestModeErrors.NoCommandStream}");
                    }
                    else if (skew != 0)
                    {
                        ctx.PendingSkewChecks[n.NodeId] = (skew, ctx.PreflightOffsetMs.TryGetValue(n.NodeId, out var baseline) ? baseline : n.ClockOffsetMs);
                    }
                }
                await ProbeFanOut.SetTestModeAllAsync(_host.LocalProbeTargets(), mode.Timecode, 0);
                var skews = live.Where(n => SkewFor(mode, n) != 0).Select(n => $"{n.Name} {SkewFor(mode, n):+#;-#} ms");
                sr.Verdict = unmatched.Count > 0 ? Verdict.Fail : failed.Count == 0 ? Verdict.Pass : Verdict.Warn;
                sr.Message = $"timecode {(mode.Timecode ? "on" : "off")}"
                    + (skews.Any() ? $", simulated skew: {string.Join(", ", skews)}" : "")
                    + (failed.Count > 0 ? $"; not reached: {string.Join(", ", failed)}" : "")
                    + string.Concat(unmatched.Select(k => $"; simulated skew key '{k}' matches no connected node"));
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
                ctx.Active = play.Switch
                    ? await _host.SwitchSceneAsync(scene, targets ?? new List<string>(), ct, play.Transition)
                    : await _host.PlaySceneAsync(scene, targets ?? new List<string>(), ct);
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
                sr.Message = string.Join("; ", new[] { single.Error ?? string.Empty, single.Drift?.Message ?? string.Empty }.Concat(single.Messages).Where(m => m.Length > 0));
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
                    var unmeasured = series.Perf ? UnmeasuredPerf(sr.Probes) : new List<string>();
                    if (unmeasured.Count > 0)
                    {
                        // A null counter passes every threshold: say the CPU / GPU target was not tested at all.
                        sr.Message += $"; not measured: {string.Join(", ", unmeasured)}";
                        sr.Verdict = Verdicts.Worst(sr.Verdict, Verdict.Warn);
                    }
                    var faults = sr.Probes.Where(p => p.Error != null).Select(p => $"{p.ProbeId}: {p.Error}").ToList();
                    if (faults.Count > 0) sr.Message += $"; {faults.Count} probe(s) failed: {string.Join("; ", faults.Take(3))}";
                    var notes = sr.Probes.SelectMany(p => p.Messages).Distinct().ToList();
                    if (notes.Count > 0) sr.Message += $"; {string.Join("; ", notes)}";
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
                sr.Message = (frame.Error != null ? $"{frame.Error}; " : string.Empty) + (ctx.Active!.PerMonitor
                    ? $"{frame.Parity.Count} comparison(s)"
                    : "Sequential: every node shows a different slice; frames saved for review");
                break;

            case ShowEventStep ev:
                if (ctx.Active == null)
                {
                    sr.Verdict = Verdict.Fail;
                    sr.Message = "no scene is playing";
                    break;
                }
                var evt = ev.Event.Clone();
                evt.EventId = $"test-{Guid.NewGuid():N}";   // players dedupe by id: a rerun must not be ignored
                evt.T0Utc = ctx.Active.SharedStartServerUtcMs + ev.AtElapsedMs;
                evt.DurationMs = ShowEventMath.ClampDuration(evt.DurationMs);
                if (!string.IsNullOrEmpty(evt.ImagePath)) evt.ImagePath = ScenarioLoader.ResolvePath(ctx.Scenario, evt.ImagePath);
                if (evt.Type == ShowEventType.Spotlight && ctx.Active.Layout?.Nodes.FirstOrDefault(n => n.Order == evt.TargetOrder) is { } target)
                    evt.TargetCenterX = target.OffsetX + target.Width / 2f;
                if (evt.Type == ShowEventType.Bomb && evt.Bomb == null)
                {
                    if (ctx.Active.Layout == null) { sr.Verdict = Verdict.Fail; sr.Message = "BiBaBo needs the scene's seat-map layout"; break; }
                    var (game, _) = BombGameFactory.Create(new BombGameRequest(ctx.Active.Layout, null, _ => false, evt.Seed, evt.EventId, GroupByMachine: false,
                        ForceMode: ev.ForceMode, ForceKinds: ev.ForceKinds, ForceJunction: false, ForceRuleCard: false), out var error);
                    if (game == null) { sr.Verdict = Verdict.Fail; sr.Message = error; break; }
                    evt.Bomb = game;
                    if (string.IsNullOrEmpty(evt.ImagePath)) (evt.ImagePath, game.LogoTimerBox) = BombLogo.Resolve();
                    if (evt.DurationMs <= 0 || evt.DurationMs == ShowEventMath.ClampDuration(0))
                        evt.DurationMs = ShowEventMath.ClampDuration(game.IntroMs + game.TimerMs + 15_000);
                    _lastBomb = evt;
                }
                if (evt.Type == ShowEventType.BombOutcome)
                {
                    if (_lastBomb?.Bomb is not { } g || ev.BombOutcome is not { } result) { sr.Verdict = Verdict.Fail; sr.Message = "a BombOutcome needs bombOutcome and an earlier Bomb"; break; }
                    evt.Bomb = g.WithOutcome(result, result is BombResult.Defused or BombResult.WrongCable ? 0 : -1,
                        BombMath.RemainingMs(g, evt.T0Utc - _lastBomb.T0Utc));
                    evt.DurationMs = result == BombResult.Aborted ? BombMath.AbortDurationMs : BombMath.OutcomeDurationMs;
                    (evt.ImagePath, evt.Bomb.OutcomeImageScale) = BombLogo.OutcomeImage(result, _lastBomb.ImagePath, shippedLogo: g.LogoTimerBox.Length == 4);
                }
                await _host.FireShowEventAsync(evt, ct);
                sr.Verdict = Verdict.Pass;
                sr.Message = $"{evt.Type} at elapsed {ev.AtElapsedMs} ms for {evt.DurationMs} ms";
                break;

            case FaultStep fault:
                await RunFaultAsync(ctx, sr, fault, ct);
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

    /// <summary>
    /// Design §12: send the fault, then watch the server's registry until the node was lost and is back (or the
    /// watch time is up). The node's outage is expected: it is not a node failure, and its test mode is re-sent
    /// (a reconnect clears it on the client). Ends once the room shows the node again, so the post-step check agrees.
    /// </summary>
    private async Task RunFaultAsync(RunContext ctx, StepReport sr, FaultStep fault, CancellationToken ct)
    {
        // A client id matches exactly; a tile name must be unique (two machines can share a hostname).
        var live = ctx.LiveRemotes.ToList();
        var matches = live.Where(n => n.NodeId == fault.Node).ToList();
        if (matches.Count == 0) matches = live.Where(n => SkewKeyMatches(fault.Node, n)).ToList();
        if (matches.Count != 1)
        {
            sr.Verdict = Verdict.Fail;
            sr.Message = matches.Count == 0
                ? $"fault node '{fault.Node}' matches no connected remote"
                : $"fault node '{fault.Node}' matches {matches.Count} remotes ({string.Join(", ", matches.Select(n => n.NodeId))}); use the client id";
            return;
        }
        var node = matches[0];
        var r = new FaultReport { NodeId = node.NodeId, Name = node.Name, Kind = fault.Fault, Ms = fault.Ms, SentAtUtcMs = _o.NowUtcMs() };
        sr.Fault = r;
        if (!await _transport.SendFaultAsync(node.NodeId, fault.Fault, fault.Ms))
        {
            sr.Verdict = Verdict.Fail;
            sr.Message = $"fault not delivered: {TestModeErrors.NoCommandStream}";
            RecordNodeFailure(ctx, node, sr.Message);
            return;
        }

        long deadline = r.SentAtUtcMs + fault.WatchMs(_o.FaultWatchSlackMs);
        // A silent node still listed this long after its silence ended will not be dropped any more.
        long silentGiveUp = r.SentAtUtcMs + fault.Ms + _o.FaultSilenceGraceMs;
        while (_o.NowUtcMs() < deadline)
        {
            bool registered = _transport.IsRegistered(node.NodeId);
            long now = _o.NowUtcMs();
            if (!registered && r.LostAtUtcMs == null) r.LostAtUtcMs = now;
            if (registered && r.LostAtUtcMs != null) { r.BackAtUtcMs = now; break; }
            if (r.LostAtUtcMs == null && fault.Fault == TestFaults.Silent && now > silentGiveUp) break;
            await Task.Delay(_o.FaultPollMs, ct);
        }

        FaultEvaluation.Evaluate(r, fault.MaxReconnectMs, fault.MaxSweepMs);
        sr.Verdict = r.Verdict;
        sr.Message = $"{node.Name} {fault.Fault} {fault.Ms} ms: {r.Message}";

        if (r.BackAtUtcMs == null) return;   // the post-step connection check records it as lost

        long syncDeadline = _o.NowUtcMs() + _o.FaultHostSyncMs;

        // Wait for the room to list the node again, else CheckConnectionsAsync would count it as lost.
        bool inRoom = false;
        while (!inRoom && _o.NowUtcMs() < syncDeadline)
        {
            inRoom = (await _host.GetNodesAsync()).Any(n => n.NodeId == node.NodeId && n.Connected);
            if (!inRoom) await Task.Delay(_o.FaultPollMs, ct);
        }
        if (!inRoom) sr.Message += $"; the room still showed {node.Name} as offline {_o.FaultHostSyncMs} ms later";

        // A reconnect clears test mode on the client. Its command stream opens just after it re-registered,
        // so retry: a node left without its simulated skew would make later probes pass for the wrong reason.
        if (ctx.Mode == null) return;
        while (!await _transport.SendTestModeAsync(node.NodeId, ctx.Mode.Timecode, SkewFor(ctx.Mode, node)))
        {
            if (_o.NowUtcMs() >= syncDeadline)
            {
                sr.Verdict = Verdicts.Worst(sr.Verdict, Verdict.Warn);
                sr.Message += "; test mode not re-sent (no command stream)";
                return;
            }
            await Task.Delay(_o.FaultPollMs, ct);
        }
    }

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

        var remotes = ctx.LiveRemotes.Where(n => ctx.IsTarget(n.NodeId)).ToList();
        var request = new ProbeRequest { ProbeId = probeId, AtServerUtcMs = atServerUtcMs, Capture = capture, ExactElapsedMs = exactElapsedMs };
        var remoteAll = Task.WhenAll(remotes.Select(n => RemoteProbeAsync(n.NodeId, request, timeout + TimeSpan.FromMilliseconds(_o.UploadGraceMs), ct)));

        // Wait for both sides before reading either: a fault on one side must not leave the other unobserved.
        await Task.WhenAll(localTask, remoteAll);
        var localReplies = await localTask;
        var remoteResults = await remoteAll;
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
        // Only running players answer: a local monitor the scene targets whose player died or hung
        // would otherwise vanish from the probe instead of counting as missing.
        foreach (var node in ctx.Nodes.Where(n => n.IsLocal && ctx.IsTarget(n.NodeId)))
            if (!localReplies.Any(r => r.MonitorIndex == node.MonitorIndex))
                pr.Samples.Add(NodeProbeSample.MissingFor(node.NodeId, node.Name, node.MonitorIndex, true, $"no reply from the local player within {timeout.TotalSeconds:F1} s"));

        // A remote that did not answer may have dropped: record that now, not after a long series.
        if (remoteResults.Any(r => r == null)) await CheckConnectionsAsync(ctx);

        var skewIssues = new List<string>();
        for (int i = 0; i < remotes.Count; i++)
        {
            var node = remotes[i];
            var result = remoteResults[i];
            if (result == null)
            {
                pr.Samples.Add(NodeProbeSample.MissingFor(node.NodeId, node.Name, 0, false, $"no reply within {timeout.TotalSeconds:F1} s"));
                continue;
            }
            // Offset and RTT both 0 = the client reset its estimate for the new skew and no heartbeat has
            // answered yet: there is nothing to check, so the next probe that reaches the node checks instead.
            bool hasClockEstimate = result.RttMs != 0 || result.ClockOffsetMs != 0;
            if (result.Error == TestModeErrors.NoCommandStream)
                RecordNodeFailure(ctx, node, $"probe {probeId}: {result.Error}");
            else if (hasClockEstimate && ctx.PendingSkewChecks.TryRemove(node.NodeId, out var check))
            {
                // The skew shifts the client clock by +skew, so its offset (server - client) must move by -skew.
                double moved = result.ClockOffsetMs - check.BaselineOffsetMs;
                double tolerance = Math.Max(50, result.RttMs);
                if (Math.Abs(moved + check.SkewMs) > tolerance)
                    skewIssues.Add($"simulated skew on {node.Name} not seen: clock offset moved {moved:+0;-0;0} ms since preflight, expected ≈ {-check.SkewMs:+0;-0} ms (±{tolerance:F0})");
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
        if (capture)
        {
            // A frame that never arrived (upload dropped, player wrote none) would otherwise just be skipped by
            // the position and parity checks: the evidence was asked for, so its absence fails the probe.
            var noFrame = pr.Samples.Where(s => !s.Missing && s.Error == null && s.CapturePath == null)
                .Select(s => s.MonitorIndex > 0 && !s.IsLocal ? $"{s.NodeName}#{s.MonitorIndex}" : s.NodeName).ToList();
            if (noFrame.Count > 0)
            {
                pr.Messages.Add($"capture requested but no frame arrived from: {string.Join(", ", noFrame)}");
                pr.Verdict = Verdicts.Worst(pr.Verdict, Verdict.Fail);
            }
        }
        if (perf) EvaluatePerf(pr, t);
        if (skewIssues.Count > 0)
        {
            pr.Messages.AddRange(skewIssues);
            pr.Verdict = Verdicts.Worst(pr.Verdict, Verdict.Fail);
        }
    }

    /// <summary>The transport call as a task: a synchronous throw becomes a faulted task, observed with its siblings.</summary>
    private async Task<RemoteProbeResult?> RemoteProbeAsync(string nodeId, ProbeRequest request, TimeSpan timeout, CancellationToken ct) =>
        await _transport.ProbeAsync(nodeId, request, timeout, ct);

    /// <summary>Spec §8: a remote connected at preflight went missing. Recorded once per node; later steps skip it.</summary>
    private void RecordNodeFailure(RunContext ctx, TestNodeInfo node, string reason)
    {
        if (!ctx.Lost.TryAdd(node.NodeId, true)) return;
        _logger.LogWarning("Test run lost node {Node}: {Reason}", node.Name, reason);
        lock (ctx.Report.NodeFailures)
            ctx.Report.NodeFailures.Add(new NodeFailure { NodeId = node.NodeId, Name = node.Name, AtUtcMs = _o.NowUtcMs(), Reason = reason });
    }

    /// <summary>
    /// Spec §8: record remotes the server no longer lists as connected (stream ended, or the dead-client
    /// sweep dropped them). Runs after every step and when a remote misses a probe, so the timestamp is
    /// at most one step or one probe after the loss.
    /// </summary>
    private async Task CheckConnectionsAsync(RunContext ctx)
    {
        if (!ctx.LiveRemotes.Any()) return;
        IReadOnlyList<TestNodeInfo> nodes;
        try { nodes = await _host.GetNodesAsync(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Test run: node list unavailable for the connection check");
            return;
        }
        var connected = nodes.Where(n => n.Connected).Select(n => n.NodeId).ToHashSet();
        foreach (var node in ctx.LiveRemotes.ToList())
            if (!connected.Contains(node.NodeId))
                RecordNodeFailure(ctx, node, "disconnected from the server");
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
            // Only the reference and one other frame are decoded at a time (a 1080p frame is ~8 MB).
            NodeProbeSample? reference = null;
            PngPixels.Image? refImg = null;
            var unreadableBeforeReference = new List<(NodeProbeSample Node, string Error)>();
            foreach (var other in members)
            {
                PngPixels.Image img;
                try { img = PngPixels.Decode(Path.Combine(ctx.Dir, other.CapturePath!)); }
                catch (Exception ex)
                {
                    if (reference == null) unreadableBeforeReference.Add((other, ex.Message));
                    else AddParityFailure(pr, other, reference.NodeName, $"frame unreadable: {ex.Message}");
                    continue;
                }
                if (reference == null)
                {
                    reference = other;
                    refImg = img;
                    foreach (var bad in unreadableBeforeReference)
                        AddParityFailure(pr, bad.Node, reference.NodeName, $"frame unreadable: {bad.Error}");
                    continue;
                }
                var diff = PixelDiff.Compare(refImg!.Pixels, refImg.Width, refImg.Height, refImg.Stride, img.Pixels, img.Width, img.Height, img.Stride);
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
                    var rel = RelativeCapturePath(ctx, sr, pr.ProbeId, other, "diff-mon" + other.MonitorIndex);
                    PngPixels.Encode(diff.DiffImage, img.Width, img.Height, img.Width * 4, Path.Combine(ctx.Dir, rel));
                    result.DiffImagePath = rel.Replace('\\', '/');
                }
                pr.Parity.Add(result);
                pr.Verdict = Verdicts.Worst(pr.Verdict, result.Verdict);
            }
            if (reference == null)
                foreach (var bad in unreadableBeforeReference)
                    AddParityFailure(pr, bad.Node, string.Empty, $"frame unreadable: {bad.Error}");
        }
        // Mixed resolutions leave every group with one member: say so instead of a silent "0 comparisons".
        if (pr.Parity.Count == 0 && withFrames.Count >= 2)
        {
            pr.Messages.Add($"parity not compared: no two of {withFrames.Count} frames share a resolution");
            pr.Verdict = Verdicts.Worst(pr.Verdict, Verdict.Warn);
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

    /// <summary>"pc-02 GPU" for every machine whose counter stayed null over the whole series.</summary>
    private static List<string> UnmeasuredPerf(IEnumerable<ProbeReport> probes)
    {
        var result = new List<string>();
        foreach (var machine in probes.SelectMany(p => p.Samples).Where(s => s.Perf != null).GroupBy(s => s.IsLocal ? "server" : s.NodeName))
        {
            var perf = machine.Select(s => s.Perf!).ToList();
            if (perf.All(p => p.PlayerCpuPercent == null)) result.Add($"{machine.Key} player CPU");
            if (perf.All(p => p.GpuPercent == null)) result.Add($"{machine.Key} GPU");
        }
        return result;
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

    private async Task CleanupAsync(RunContext ctx, TestRestorePoint restorePoint)
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
            if (ctx.ScenePlayed) await _host.RestoreAsync(restorePoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restore the scene or show that played before the test run");
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
            var file = $"{ctx.FileNameFor(n.NodeId, n.Name)}.log";
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

    private int StepTimeoutMs(RunContext ctx, TestStep step) => step.TimeoutMs ?? step switch
    {
        ProbeSeriesStep s => s.ForMs + 30000,
        WaitStep w => w.Ms + 30000,
        PlaySceneStep => 120000,   // includes downloads on remotes
        ProbeStep p => ProbeLeadMs(ctx, p) + _o.DefaultStepTimeoutMs,   // the default budget starts at the probe instant
        FaultStep f => f.WatchMs(_o.FaultWatchSlackMs) + _o.FaultHostSyncMs + _o.DefaultStepTimeoutMs,
        _ => _o.DefaultStepTimeoutMs,
    };

    /// <summary>How far ahead a probe step's instant lies ("start+60000ms" can be a minute away); 0 when unknown.</summary>
    private int ProbeLeadMs(RunContext ctx, ProbeStep probe)
    {
        if (ctx.Active == null || !ProbeAt.TryParse(probe.At, out var anchor, out var offset)) return 0;
        long now = _o.NowUtcMs();
        long lead = ProbeAt.Resolve(anchor, offset, ctx.Active.SharedStartServerUtcMs, now, _o.MinLeadMs) - now;
        return (int)Math.Clamp(lead, 0, int.MaxValue - _o.DefaultStepTimeoutMs);
    }

    /// <summary>Skew for a node: key = its NodeId (exact) or its name (case-insensitive, hostnames); NodeId wins.</summary>
    private static int SkewFor(TestModeStep mode, TestNodeInfo node)
    {
        if (mode.SimulatedClockSkewMs.TryGetValue(node.NodeId, out var byId)) return byId;
        foreach (var (key, ms) in mode.SimulatedClockSkewMs)
            if (key.Equals(node.Name, StringComparison.OrdinalIgnoreCase)) return ms;
        return 0;
    }

    private static bool SkewKeyMatches(string key, TestNodeInfo node) =>
        key == node.NodeId || key.Equals(node.Name, StringComparison.OrdinalIgnoreCase);

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
        var rel = RelativeCapturePath(ctx, sr, probeId, sample, "mon" + sample.MonitorIndex);
        var full = Path.Combine(ctx.Dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, png);
        return rel.Replace('\\', '/');
    }

    private static string RelativeCapturePath(RunContext ctx, StepReport sr, string probeId, NodeProbeSample sample, string suffix) =>
        Path.Combine("nodes", ctx.FileNameFor(sample.NodeId, sample.NodeName), $"{sr.Index:D2}-{Sanitize(sr.Label ?? sr.Kind)}-{probeId}-{suffix}.png");

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

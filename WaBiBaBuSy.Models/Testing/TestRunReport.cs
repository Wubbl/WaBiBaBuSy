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
    /// <summary>Remotes connected at preflight that went missing mid-run (spec §8), once per node; the run verdict is then at least Warn.</summary>
    public List<NodeFailure> NodeFailures { get; set; } = new();
    /// <summary>Collected logs, relative to the results folder.</summary>
    public List<string> LogFiles { get; set; } = new();
    public string ClockNote { get; set; } =
        "Probe times are converted with each client's clock-offset estimate, so an error in that estimate itself is invisible here; it is bounded by RTT/2 per node (clockBoundMs).";

    /// <summary>
    /// CLI exit code: 2 aborted, 1 when a step failed <b>or</b> the run cannot vouch for every node
    /// (a node lost, a probe a node did not answer, nothing measured at all), else 0. Stricter than
    /// <see cref="Verdict"/>, where those cases are only Warn / Skipped: a script must never read them as a pass.
    /// </summary>
    public (int Code, string? Reason) ExitCode()
    {
        if (Aborted) return (2, AbortReason ?? "aborted");
        if (Verdict == Verdict.Fail) return (1, "a step failed");
        if (NodeFailures.Count > 0) return (1, $"node(s) lost: {string.Join(", ", NodeFailures.Select(f => f.Name))}");
        var unanswered = Steps.SelectMany(s => s.Probes).SelectMany(p => p.Samples)
            .Where(s => s.Missing || s.Error != null).Select(s => s.NodeName).Distinct().ToList();
        if (unanswered.Count > 0) return (1, $"probe(s) not answered by: {string.Join(", ", unanswered)}");
        if (Verdict == Verdict.Skipped) return (1, "nothing was measured");
        return (0, null);
    }
}

/// <summary>A remote node lost during the run: when and why. Later steps run without it.</summary>
public sealed class NodeFailure
{
    public string NodeId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long AtUtcMs { get; set; }
    public string Reason { get; set; } = string.Empty;
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
    /// <summary>The fault a <c>fault</c> step injected and what the server saw.</summary>
    public FaultReport? Fault { get; set; }
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
    /// <summary>Further findings of this probe (e.g. a simulated clock skew that never showed up in the clock offset).</summary>
    public List<string> Messages { get; set; } = new();
    /// <summary>Set when the probe itself broke (IO error, unreadable frame, ...); the verdict is then Fail.</summary>
    public string? Error { get; set; }
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

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

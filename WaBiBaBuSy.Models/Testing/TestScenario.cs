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

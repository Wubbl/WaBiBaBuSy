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

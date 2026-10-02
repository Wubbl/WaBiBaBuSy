using System.Text.Json.Serialization;

namespace WaBiBaBuSy.Models.Testing;

/// <summary>Outcome of a check, a probe, a step or a run.</summary>
public enum Verdict
{
    /// <summary>Checked and within the thresholds.</summary>
    Pass,
    /// <summary>Checked; beyond the warn threshold, or something is missing (the run still passes).</summary>
    Warn,
    /// <summary>Checked and beyond the fail threshold, or the check could not run as required.</summary>
    Fail,
    /// <summary>Nothing to check (no capture, marker on an edge, …); never worsens a combined verdict.</summary>
    Skipped,
}

/// <summary>Combining verdicts of checks into probe / step / run verdicts.</summary>
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
    /// <summary>Unique per probe in the run; pairs replies and capture chunks with the request.</summary>
    public string ProbeId { get; set; } = string.Empty;
    /// <summary>When to sample, server UTC ms (the node converts it with its clock offset).</summary>
    public long AtServerUtcMs { get; set; }
    /// <summary>Also write a PNG of the wallpaper back buffer.</summary>
    public bool Capture { get; set; }
    /// <summary>Set for an exact-frame probe: render offscreen at exactly this effective elapsed.</summary>
    public long? ExactElapsedMs { get; set; }
}

/// <summary>Node → its player: the probe in the node's own (possibly skewed) clock.</summary>
public sealed class PlayerProbeRequest
{
    /// <summary>Same id as the <see cref="ProbeRequest"/>.</summary>
    public string ProbeId { get; set; } = string.Empty;
    /// <summary>When to sample, in the node's local UTC ms (skew applied).</summary>
    public long AtLocalUtcMs { get; set; }
    /// <summary>Write a PNG of the frame into <see cref="CaptureDirectory"/>.</summary>
    public bool Capture { get; set; }
    /// <summary>Exact-frame probe: render offscreen at exactly this effective elapsed instead of sampling the live frame.</summary>
    public long? ExactElapsedMs { get; set; }
    /// <summary>Folder the player writes the PNG into.</summary>
    public string CaptureDirectory { get; set; } = string.Empty;
}

/// <summary>Player → host (stderr <c>SIGNAL:PROBE:{json}</c>). Times are the player's local clock.</summary>
public sealed class PlayerProbeReply
{
    /// <summary>Same id as the request.</summary>
    public string ProbeId { get; set; } = string.Empty;
    /// <summary>Filled in by the host / fan-out, not by the player.</summary>
    public int MonitorIndex { get; set; }
    /// <summary>Why this monitor has no measurement; null on success.</summary>
    public string? Error { get; set; }
    /// <summary>The frame was an exact-frame offscreen render, not the live frame.</summary>
    public bool Exact { get; set; }
    /// <summary>Effective elapsed the frame was rendered with (raw − phase).</summary>
    public long RenderedElapsedMs { get; set; }
    /// <summary>raw − effective: the Wave-mode phase this node applies.</summary>
    public int PhaseMs { get; set; }
    /// <summary>When the frame was rendered (just before Present), player-local UTC ms.</summary>
    public long RenderLocalUtcMs { get; set; }
    /// <summary>When the frame reached the screen (DXGI frame statistics); null when unavailable.</summary>
    public long? PresentLocalUtcMs { get; set; }
    /// <summary>True when the present time was extrapolated from a later frame's statistics.</summary>
    public bool PresentEstimated { get; set; }
    /// <summary>The player's frame counter for the sampled frame.</summary>
    public long FrameIndex { get; set; }
    /// <summary>Frame pacing over the frames before the probe; null before the second frame.</summary>
    public FrameIntervalStats? Frames { get; set; }
    /// <summary>The player's own sprite rectangle (canvas units, node-local, top-left).</summary>
    public float AnimX { get; set; }
    /// <inheritdoc cref="AnimX"/>
    public float AnimY { get; set; }
    /// <inheritdoc cref="AnimX"/>
    public float AnimWidth { get; set; }
    /// <inheritdoc cref="AnimX"/>
    public float AnimHeight { get; set; }
    /// <summary>The sprite was drawn mirrored (face travel direction).</summary>
    public bool Flipped { get; set; }
    /// <summary>Player surface size, device px.</summary>
    public int Width { get; set; }
    /// <inheritdoc cref="Width"/>
    public int Height { get; set; }
    /// <summary>PNG written by the player on its machine.</summary>
    public string? CapturePath { get; set; }
}

/// <summary>Frame pacing over the last N frames.</summary>
public sealed class FrameIntervalStats
{
    /// <summary>1000 / mean interval.</summary>
    public double MeanFps { get; set; }
    /// <summary>Median frame interval, ms (nearest rank).</summary>
    public double P50Ms { get; set; }
    /// <summary>99th-percentile frame interval, ms (nearest rank).</summary>
    public double P99Ms { get; set; }
    /// <summary>Longest frame interval, ms.</summary>
    public double MaxMs { get; set; }
    /// <summary>Intervals longer than 1.5 refresh periods.</summary>
    public int Dropped { get; set; }
    /// <summary>Intervals the stats are computed over.</summary>
    public int Count { get; set; }
}

/// <summary>Process load on one machine at one probe.</summary>
public sealed class PerfSample
{
    /// <summary>CPU of the WaBiBaBuSy app process, % of all cores; null on the first sample (no baseline yet).</summary>
    public double? AppCpuPercent { get; set; }
    /// <summary>Working set of the app process, MB.</summary>
    public double AppMemoryMb { get; set; }
    /// <summary>CPU of all player processes together, % of all cores; null without a baseline.</summary>
    public double? PlayerCpuPercent { get; set; }
    /// <summary>Working set of all player processes together, MB.</summary>
    public double PlayerMemoryMb { get; set; }
    /// <summary>Running player processes.</summary>
    public int PlayerProcesses { get; set; }
    /// <summary>3D engine utilisation of the player processes; null when the counters are unavailable.</summary>
    public double? GpuPercent { get; set; }
}

/// <summary>Client → server: everything one client measured for one probe.</summary>
public sealed class RemoteProbeResult
{
    /// <summary>The reporting client.</summary>
    public string ClientId { get; set; } = string.Empty;
    /// <summary>Same id as the <see cref="ProbeRequest"/>.</summary>
    public string ProbeId { get; set; } = string.Empty;
    /// <summary>server − client, ms, at the time of the probe.</summary>
    public double ClockOffsetMs { get; set; }
    /// <summary>Round trip of the clock-offset estimate, ms; RTT/2 bounds its error.</summary>
    public double RttMs { get; set; }
    /// <summary>Process load at the probe; null when it was not sampled (the probe failed first).</summary>
    public PerfSample? Perf { get; set; }
    /// <summary>Why the client could not probe at all (e.g. <see cref="TestModeErrors.NoPlayer"/>); null otherwise.</summary>
    public string? Error { get; set; }
    /// <summary>One reply per monitor (player) of the client.</summary>
    public List<PlayerProbeReply> Replies { get; set; } = new();
    /// <summary>
    /// PNG bytes per monitor index; travels as separate gRPC chunks. Ignored by System.Text.Json, which
    /// carries this class in the result header — Newtonsoft only ever carries <see cref="PlayerProbeReply"/>.
    /// </summary>
    [JsonIgnore] public Dictionary<int, byte[]> Captures { get; set; } = new();
}

/// <summary>One node-monitor's measurement as the report sees it.</summary>
public sealed class NodeProbeSample
{
    /// <summary>Client id, or the server's own node id for local monitors.</summary>
    public string NodeId { get; set; } = string.Empty;
    /// <summary>Display name of the node.</summary>
    public string NodeName { get; set; } = string.Empty;
    /// <summary>Monitor index on that node.</summary>
    public int MonitorIndex { get; set; }
    /// <summary>A monitor of the server machine itself (clock offset 0).</summary>
    public bool IsLocal { get; set; }
    /// <summary>No reply arrived (timeout, node lost); only <see cref="Error"/> is meaningful.</summary>
    public bool Missing { get; set; }
    /// <summary>Why there is no measurement; null on success.</summary>
    public string? Error { get; set; }
    /// <inheritdoc cref="PlayerProbeReply.Exact"/>
    public bool Exact { get; set; }
    /// <inheritdoc cref="PlayerProbeReply.RenderedElapsedMs"/>
    public long RenderedElapsedMs { get; set; }
    /// <inheritdoc cref="PlayerProbeReply.PhaseMs"/>
    public int PhaseMs { get; set; }
    /// <inheritdoc cref="PlayerProbeReply.RenderLocalUtcMs"/>
    public long RenderLocalUtcMs { get; set; }
    /// <inheritdoc cref="PlayerProbeReply.PresentLocalUtcMs"/>
    public long? PresentLocalUtcMs { get; set; }
    /// <inheritdoc cref="PlayerProbeReply.PresentEstimated"/>
    public bool PresentEstimated { get; set; }
    /// <inheritdoc cref="PlayerProbeReply.FrameIndex"/>
    public long FrameIndex { get; set; }
    /// <inheritdoc cref="RemoteProbeResult.ClockOffsetMs"/>
    public double ClockOffsetMs { get; set; }
    /// <inheritdoc cref="RemoteProbeResult.RttMs"/>
    public double RttMs { get; set; }
    /// <inheritdoc cref="PlayerProbeReply.Frames"/>
    public FrameIntervalStats? Frames { get; set; }
    /// <inheritdoc cref="RemoteProbeResult.Perf"/>
    public PerfSample? Perf { get; set; }
    /// <summary>The player's own sprite rectangle (canvas units, node-local, top-left).</summary>
    public float PlayerAnimX { get; set; }
    /// <inheritdoc cref="PlayerAnimX"/>
    public float PlayerAnimY { get; set; }
    /// <inheritdoc cref="PlayerAnimX"/>
    public float PlayerAnimWidth { get; set; }
    /// <inheritdoc cref="PlayerAnimX"/>
    public float PlayerAnimHeight { get; set; }
    /// <inheritdoc cref="PlayerProbeReply.Flipped"/>
    public bool PlayerFlipped { get; set; }
    /// <summary>Player surface size, device px.</summary>
    public int Width { get; set; }
    /// <inheritdoc cref="Width"/>
    public int Height { get; set; }
    /// <summary>Capture PNG, relative to the results folder.</summary>
    public string? CapturePath { get; set; }

    /// <summary>Sample from a player's reply plus the node's clock estimate (capture path is set by the caller).</summary>
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

    /// <summary>Placeholder for a node-monitor that did not answer; <paramref name="reason"/> becomes <see cref="Error"/>.</summary>
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

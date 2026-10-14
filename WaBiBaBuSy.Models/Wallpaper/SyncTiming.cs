using System;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Pure timing helpers shared by the server apply path and the D2D player.
/// Everything here is a function of its inputs — no wall clock, no per-machine state —
/// so every node derives identical values from the same broadcast numbers.
/// </summary>
public static class SyncTiming
{
    /// <summary>Smallest lead the server puts between "now" and the shared start timestamp.</summary>
    public const int MinStartLeadMs = 800;

    /// <summary>Largest lead — beyond this a show start feels unresponsive.</summary>
    public const int MaxStartLeadMs = 4000;

    /// <summary>
    /// How far in the future the shared start timestamp is placed so that every node has
    /// received the command, spawned its player and decoded the content before the first frame.
    /// Three round trips cover command delivery + jitter; the apply estimate covers local
    /// load time (the player's load→READY duration observed on the server).
    /// </summary>
    public static int ComputeStartLeadMs(double maxObservedRttMs, int applyEstimateMs)
    {
        double rtt = double.IsFinite(maxObservedRttMs) ? Math.Max(0.0, maxObservedRttMs) : 0.0;
        double lead = 3.0 * rtt + Math.Max(0, applyEstimateMs);
        return (int)Math.Clamp(Math.Round(lead), MinStartLeadMs, MaxStartLeadMs);
    }

    /// <summary>
    /// Wave mode: in Simultaneous (per-monitor) distribution each node runs the same animation
    /// shifted by <c>nodeOrder × nodePhaseDelayMs</c>, so a bounce or pulse visibly travels down
    /// the row. In Sequential (spanning) mode the phase is ignored — the canvas is one shared
    /// world state and shifting a node would tear the sprite at the bezel.
    /// </summary>
    public static long ApplyNodePhase(long elapsedMs, int nodeOrder, int nodePhaseDelayMs, bool perMonitorMode)
    {
        if (!perMonitorMode || nodePhaseDelayMs <= 0 || nodeOrder <= 0)
            return elapsedMs;
        return elapsedMs - (long)nodeOrder * nodePhaseDelayMs;
    }

    /// <summary>
    /// True once the shared start (or this node's phase-shifted start) has been reached.
    /// Before that the player draws the background only, so a future start timestamp yields a
    /// clean simultaneous first frame instead of a sprite already mid-canvas on early nodes.
    /// </summary>
    public static bool ShouldDrawAnimation(long effectiveElapsedMs) => effectiveElapsedMs >= 0;

    /// <summary>
    /// A scene layer's clock on a node: the shared elapsed shifted by the layer's Wave-mode node phase (Simultaneous
    /// layers only), null before that reaches 0 (nothing drawn yet), then advanced by the layer's own
    /// <see cref="SceneLayer.PhaseOffsetMs"/>. Players and the room preview both position and animate a layer at this.
    /// </summary>
    public static long? LayerElapsed(long sceneElapsedMs, int nodeOrder, SceneLayer layer)
    {
        long phased = ApplyNodePhase(sceneElapsedMs, nodeOrder, layer.Movement.NodePhaseDelayMs,
            layer.Distribution == AnimationDistributionMode.Simultaneous);
        return ShouldDrawAnimation(phased) ? phased + layer.PhaseOffsetMs : null;
    }

    /// <summary>Modulo that maps negative values into [0, period). Period must be &gt; 0.</summary>
    public static long PositiveModulo(long value, long period)
    {
        if (period <= 0) return 0;
        long m = value % period;
        return m < 0 ? m + period : m;
    }

    // ── video on the shared clock ───────────────────────────────────────────

    /// <summary>Drift beyond this is corrected with a seek; below it the playback rate is nudged instead.</summary>
    public const int VideoSeekThresholdMs = 200;

    /// <summary>Drift within this is left alone (LibVLC's reported time is only frame-accurate).</summary>
    public const int VideoRateDeadbandMs = 20;

    /// <summary>
    /// Video position (ms) the shared clock asks for: the elapsed time folded into the clip when it loops,
    /// held on the last frame when it does not. 0 before the start or for an unknown duration.
    /// </summary>
    public static long VideoTargetMs(long elapsedMs, long durationMs, bool loop)
    {
        if (durationMs <= 0 || elapsedMs <= 0) return 0;
        return loop ? PositiveModulo(elapsedMs, durationMs) : Math.Min(elapsedMs, durationMs);
    }

    /// <summary>
    /// Signed drift of the video against the clock (positive = video ahead). A looping clip takes the shorter
    /// way around, so 9.9 s against 0.1 s in a 10 s loop is −200 ms, not +9.8 s.
    /// </summary>
    public static long VideoDriftMs(long videoMs, long targetMs, long durationMs, bool loop)
    {
        long drift = videoMs - targetMs;
        if (!loop || durationMs <= 0) return drift;
        long half = durationMs / 2;
        return PositiveModulo(drift + half, durationMs) - half;
    }

    /// <summary>
    /// Playback rate that closes a small drift within about a second: behind → faster, ahead → slower,
    /// clamped to ±10 % so the change stays invisible. 1 inside the deadband.
    /// </summary>
    public static float VideoCorrectionRate(long driftMs) =>
        Math.Abs(driftMs) <= VideoRateDeadbandMs ? 1f : Math.Clamp(1f - driftMs / 1000f, 0.9f, 1.1f);

    /// <summary>Screen-space horizontal motion below this magnitude does not change facing.</summary>
    public const float FacingHysteresisPx = 0.5f;

    /// <summary>
    /// Whether the sprite should be drawn mirrored (facing left) given its screen-space
    /// horizontal delta since the previous frame. Small deltas keep the previous facing so a
    /// bounce apex or a paused sprite never flickers.
    /// </summary>
    public static bool ResolveFacingLeft(float screenDx, bool previousFacingLeft)
    {
        if (screenDx <= -FacingHysteresisPx) return true;
        if (screenDx >= FacingHysteresisPx) return false;
        return previousFacingLeft;
    }
}

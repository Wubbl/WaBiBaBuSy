using System;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Pure scheduling helpers for playlist rotation. No I/O, no wall-clock, no shared render state.
/// Shuffle randomness is caller-seeded and server-authority-only — it decides item ORDER, never
/// render output, so it does not violate the deterministic-render invariant.
/// </summary>
public static class PlaylistScheduler
{
    /// <summary>
    /// One full movement-lap duration in ms for the given config, or 0 if lap-snap is unsupported.
    /// v1 supports Linear movement only (no Pattern). For horizontal Linear travel (Y unset) this
    /// equals MovementCalculator.CalculateLinear's loop period exactly (distance = canvasWidth +
    /// contentWidth). When StartX/StartY/EndX/EndY are set explicitly the vertical delta is taken
    /// from those values; note MovementCalculator centers Y by default, so a config that sets only
    /// one of StartY/EndY will get an approximate (still safe — over/under-rounds the dwell) lap here.
    /// </summary>
    public static int ComputeLapMs(MovementConfig m, int canvasWidth, int contentWidthPx)
        => ComputeLapMs(m, canvasWidth, contentWidthPx, speedPxOverride: 0f);

    /// <param name="speedPxOverride">
    /// Effective canvas speed in px/s when the config's speed is authored in cm/s (see
    /// <see cref="PhysicalUnits.EffectiveSpeedPx"/>); 0 = use <c>m.SpeedPixelsPerSecond</c>.
    /// </param>
    public static int ComputeLapMs(MovementConfig m, int canvasWidth, int contentWidthPx, float speedPxOverride)
    {
        if (m.Type != MovementType.Linear) return 0;
        if (!m.Loop) return 0;
        float speed = speedPxOverride > 0f ? speedPxOverride : m.SpeedPixelsPerSecond;
        if (speed <= 0f) return 0;

        float animWidth = Math.Max(0, contentWidthPx);
        float startX = m.StartX ?? (m.Reversed ? canvasWidth : -animWidth);
        float endX   = m.EndX   ?? (m.Reversed ? -animWidth  : canvasWidth);
        float startY = m.StartY ?? 0f;
        float endY   = m.EndY   ?? startY;

        double dx = endX - startX;
        double dy = endY - startY;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < 1.0) return 0;

        return (int)Math.Round(distance / speed * 1000.0);
    }

    /// <summary>
    /// The dwell time (ms) to wait before switching to the next item.
    /// Hard-cut = effective duration. Lap-snap rounds UP to the next whole lap; if lapMs is 0
    /// (unsupported movement) it falls back to hard-cut.
    /// </summary>
    public static int ResolveDwellMs(PlaylistItem item, int playlistDefaultMs, int lapMs)
    {
        int target = item.GetEffectiveDurationMs(playlistDefaultMs);
        if (!item.SnapToLap || lapMs <= 0) return target;

        int laps = Math.Max(1, (int)Math.Ceiling(target / (double)lapMs));
        return laps * lapMs;
    }

    /// <summary>
    /// The item-index order for one cycle. Natural order when shuffle is off; a seeded
    /// Fisher-Yates permutation (every index exactly once) when on. Seed-driven so the caller
    /// controls randomness and tests are deterministic. Server-authority-only — never a render input.
    /// </summary>
    public static int[] BuildCycleOrder(int count, bool shuffle, int seed)
    {
        var order = new int[Math.Max(0, count)];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        if (!shuffle || order.Length < 2) return order;

        var rng = new Random(seed);
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }
}

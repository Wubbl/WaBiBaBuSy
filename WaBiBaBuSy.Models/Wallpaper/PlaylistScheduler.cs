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
    /// v1 supports Linear movement only (no Pattern), matching MovementCalculator.CalculateLinear's
    /// loop period: distance = sqrt(dx^2 + dy^2) with default off-screen start/end.
    /// </summary>
    public static int ComputeLapMs(MovementConfig m, int canvasWidth, int contentWidthPx)
    {
        if (m.Type != MovementType.Linear) return 0;
        if (!m.Loop) return 0;
        if (m.SpeedPixelsPerSecond <= 0f) return 0;

        float animWidth = Math.Max(0, contentWidthPx);
        float startX = m.StartX ?? (m.Reversed ? canvasWidth : -animWidth);
        float endX   = m.EndX   ?? (m.Reversed ? -animWidth  : canvasWidth);
        float startY = m.StartY ?? 0f;
        float endY   = m.EndY   ?? startY;

        double dx = endX - startX;
        double dy = endY - startY;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < 1.0) return 0;

        return (int)Math.Round(distance / m.SpeedPixelsPerSecond * 1000.0);
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
}

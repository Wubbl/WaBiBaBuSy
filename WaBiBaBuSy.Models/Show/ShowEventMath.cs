using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Show;

/// <summary>
/// Pure timing of one-shot show events, shared by the D2D players and the room preview. An event's local time
/// is derived from the scene clock, so a live frame and a test-mode exact frame at the same elapsed agree.
/// </summary>
public static class ShowEventMath
{
    public const int MinDurationMs = 250;
    public const int MaxDurationMs = 600_000;
    public const int FadeMs = 250;

    /// <summary><paramref name="durationMs"/> clamped to 250 ms … 10 min.</summary>
    public static int ClampDuration(int durationMs) => Math.Clamp(durationMs, MinDurationMs, MaxDurationMs);

    /// <summary>
    /// Milliseconds since the event's T0 at a frame of the scene clock: <paramref name="sceneRawElapsedMs"/> is the
    /// un-phased scene elapsed (live: now − scene start; exact frame: the probe's elapsed). Wave-mode phase does not apply.
    /// </summary>
    public static long LocalMs(ShowEvent evt, long sceneStartUtcMs, long sceneRawElapsedMs) =>
        sceneRawElapsedMs + sceneStartUtcMs - evt.T0Utc;

    /// <summary>True inside [T0, T0 + duration).</summary>
    public static bool IsActive(ShowEvent evt, long localMs) => localMs >= 0 && localMs < ClampDuration(evt.DurationMs);

    /// <summary>True once the event is over at wall time <paramref name="nowUtcMs"/> (a late joiner does not draw it).</summary>
    public static bool Ended(ShowEvent evt, long nowUtcMs) => nowUtcMs >= evt.T0Utc + ClampDuration(evt.DurationMs);

    /// <summary>0 outside the event, 0→1 over the first <paramref name="fadeMs"/>, 1→0 over the last (at most half the duration each).</summary>
    public static float Envelope(long localMs, int durationMs, int fadeMs = FadeMs)
    {
        int d = ClampDuration(durationMs);
        if (localMs < 0 || localMs >= d) return 0f;
        int f = Math.Min(fadeMs, d / 2);
        if (f <= 0) return 1f;
        float rise = Math.Min(1f, localMs / (float)f);
        float fall = Math.Min(1f, (d - localMs) / (float)f);
        return Math.Min(rise, fall);
    }

    /// <summary>Blackout fill opacity.</summary>
    public static float BlackoutOpacity(ShowEvent evt, long localMs) => Envelope(localMs, evt.DurationMs);

    /// <summary>"#RRGGBB" as RGB; white when invalid. Never throws (parsed on every frame).</summary>
    public static (byte R, byte G, byte B) ParseColor(string? hex) => SpriteEffects.ParseBezelColor(hex);

    /// <summary>sin(2π·hz·t), with t folded into one period in double first (exact after days of uptime).</summary>
    public static double Sin(long ms, double hz)
    {
        if (hz <= 0) return 0;
        double period = 1000.0 / hz;
        double t = ms % period;
        if (t < 0) t += period;
        return Math.Sin(2.0 * Math.PI * t / period);
    }
}

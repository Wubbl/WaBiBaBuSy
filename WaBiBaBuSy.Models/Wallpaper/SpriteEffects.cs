namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>One trail ghost: how far back in time it samples the sprite, its opacity and its size factor.</summary>
public readonly record struct TrailGhost(long OffsetMs, float Opacity, float Scale);

/// <summary>
/// Pure math of the sprite effects (<see cref="SpriteEffectsConfig"/>), shared by the D2D players and the room
/// preview so both draw the same thing. Every function depends only on its arguments.
/// </summary>
public static class SpriteEffects
{
    /// <summary>Upper bound for <see cref="TrailConfig.Count"/> (draw cost per frame).</summary>
    public const int MaxTrailCount = 32;

    /// <summary>True when <paramref name="fx"/> asks for at least one effect.</summary>
    public static bool HasAny(SpriteEffectsConfig? fx) =>
        fx != null && (TrailCount(fx.Trail) > 0
                       || (fx.Pulse != null && fx.Pulse.Amplitude > 0f && fx.Pulse.FrequencyHz > 0f)
                       || fx.SpinDegPerSecond != 0f
                       || fx.Entry != BezelEffect.None || fx.Exit != BezelEffect.None);

    /// <summary>
    /// Effects are drawn for a single image moved by <see cref="MovementCalculator"/>: not with a pattern (cells
    /// cross bezels continuously), not with extra images, not on an IconZone path (its position is not a pure
    /// function of time, so ghosts and crossings cannot be sampled).
    /// </summary>
    public static bool Supported(AnimationLayerConfig animation, MovementConfig? movement, BackgroundMode backgroundMode) =>
        HasAny(animation.Effects)
        && animation.Pattern == null
        && !animation.AdditionalAnimationPaths.Any(p => !string.IsNullOrWhiteSpace(p))
        && movement != null
        && !MovementCalculator.FollowsIconPath(backgroundMode, movement);

    /// <summary>Scale factor at <paramref name="elapsedMs"/>: 1 + A·sin(2πft), A clamped to 0..0.9; 1 without a pulse.</summary>
    public static float PulseScale(PulseConfig? pulse, long elapsedMs)
    {
        if (pulse == null || pulse.FrequencyHz <= 0f || pulse.Amplitude <= 0f) return 1f;
        float amplitude = Math.Min(pulse.Amplitude, 0.9f);
        double periodMs = 1000.0 / pulse.FrequencyHz;
        double t = elapsedMs % periodMs;            // fold in double first: exact after days of uptime
        if (t < 0) t += periodMs;
        return 1f + amplitude * (float)Math.Sin(2.0 * Math.PI * t / periodMs);
    }

    /// <summary>Rotation in degrees, in [0, 360), after <paramref name="elapsedMs"/> at <paramref name="degPerSecond"/>.</summary>
    public static float SpinDegrees(float degPerSecond, long elapsedMs)
    {
        if (degPerSecond == 0f) return 0f;
        double deg = elapsedMs / 1000.0 * degPerSecond % 360.0;
        if (deg < 0) deg += 360.0;
        return deg >= 360.0 ? 0f : (float)deg;
    }

    /// <summary>Number of ghosts to draw (0..<see cref="MaxTrailCount"/>).</summary>
    public static int TrailCount(TrailConfig? trail) => trail == null ? 0 : Math.Clamp(trail.Count, 0, MaxTrailCount);

    /// <summary>
    /// Ghost <paramref name="k"/> (1 = nearest): sampled k × spacing ago, opacity StartOpacity × (1 − (k−1)/Count),
    /// size ScaleFalloff^k.
    /// </summary>
    public static TrailGhost Ghost(TrailConfig trail, int k)
    {
        int count = Math.Max(1, TrailCount(trail));
        long offset = (long)k * Math.Max(1, trail.SpacingMs);
        float opacity = Math.Clamp(trail.StartOpacity, 0f, 1f) * (1f - (k - 1) / (float)count);
        float scale = MathF.Pow(Math.Clamp(trail.ScaleFalloff, 0.1f, 1f), k);
        return new TrailGhost(offset, Math.Clamp(opacity, 0f, 1f), scale);
    }
}

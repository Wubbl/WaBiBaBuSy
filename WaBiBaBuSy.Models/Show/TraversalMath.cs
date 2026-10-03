namespace WaBiBaBuSy.Models.Show;

/// <summary>Where the extra sprite of a Traversal / Announce is, in virtual canvas units.</summary>
public readonly record struct TraversalPose(float VirtualX, float VirtualY, float Width, float Height, float Opacity);

/// <summary>
/// Traversal and Announce (design §3.4, §3.6): one temporary sprite that runs exactly one lap from T0 and despawns.
/// The size comes from the event (height fraction × canvas height, × the server-measured aspect), never from the
/// decoded bitmap, so every node computes the same box.
/// </summary>
public static class TraversalMath
{
    public const int MinDurationMs = 2000;
    public const int MaxDurationMs = 120_000;
    public const float DefaultSpeedPxPerSecond = 600f;

    public static (float W, float H) SpriteSize(ShowEvent evt, int canvasH)
    {
        float fraction = float.IsFinite(evt.HeightFraction) ? Math.Clamp(evt.HeightFraction, 0.05f, 1f) : 0.5f;
        float aspect = float.IsFinite(evt.SpriteAspect) && evt.SpriteAspect > 0.01f ? Math.Min(evt.SpriteAspect, 20f) : 1f;
        float h = fraction * Math.Max(1, canvasH);
        return (h * aspect, h);
    }

    /// <summary>Duration of one lap at <paramref name="pxPerSecond"/> (canvas units/s; ≤ 0 → 600), clamped to 2 s … 120 s.</summary>
    public static int DurationMs(int canvasW, float spriteW, bool wraps, float pxPerSecond)
    {
        if (!float.IsFinite(pxPerSecond) || pxPerSecond <= 0f) pxPerSecond = DefaultSpeedPxPerSecond;
        double distance = wraps ? canvasW : canvasW + Math.Max(0f, spriteW);
        return (int)Math.Clamp(Math.Round(distance * 1000.0 / pxPerSecond), MinDurationMs, MaxDurationMs);
    }

    /// <summary>
    /// Pose at <paramref name="localMs"/>; null outside the event. Non-wrapping: enters fully off the left edge and
    /// leaves fully off the right. Ring: starts just before the seam and travels one canvas width. Reverse mirrors it.
    /// </summary>
    public static TraversalPose? Pose(ShowEvent evt, int canvasW, int canvasH, bool wraps, long localMs)
    {
        if (!ShowEventMath.IsActive(evt, localMs)) return null;
        var (w, h) = SpriteSize(evt, canvasH);
        double p = localMs / (double)ShowEventMath.ClampDuration(evt.DurationMs);
        double x = wraps ? -w + p * canvasW : -w + p * (canvasW + w);
        if (evt.Reverse) x = canvasW - w - x;
        return new TraversalPose((float)x, (Math.Max(1, canvasH) - h) / 2f, w, h, ShowEventMath.Envelope(localMs, evt.DurationMs));
    }
}

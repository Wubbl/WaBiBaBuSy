using System;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Pure scheduling helpers for playlist rotation. No I/O, no wall-clock, no shared render state.
/// Shuffle randomness is caller-seeded and server-authority-only — it decides item ORDER, never
/// render output, so it does not violate the deterministic-render invariant.
/// </summary>
public static class PlaylistScheduler
{
    /// <summary>What a movement's period depends on, in canvas px: canvas, the sprite as drawn, speed after cm→px, Ring wrap.</summary>
    public readonly record struct LapGeometry(int CanvasWidth, int CanvasHeight, int ContentWidth, int ContentHeight, float SpeedPx = 0f, bool Wraps = false);

    /// <summary>Linear lap from canvas width + sprite width (kept for callers without the full geometry).</summary>
    public static int ComputeLapMs(MovementConfig m, int canvasWidth, int contentWidthPx)
        => ComputeLapMs(m, canvasWidth, contentWidthPx, speedPxOverride: 0f);

    /// <param name="speedPxOverride">
    /// Effective canvas speed in px/s when the config's speed is authored in cm/s (see
    /// <see cref="PhysicalUnits.EffectiveSpeedPx"/>); 0 = use <c>m.SpeedPixelsPerSecond</c>.
    /// </param>
    /// <param name="canvasWraps">
    /// Ring seat map: the sprite wraps from the last seat to the first, so a lap is exactly the canvas width
    /// (no run-in), as in <see cref="MovementCalculator"/> (review 2026-10-13 E7: the lap was one sprite too long).
    /// </param>
    public static int ComputeLapMs(MovementConfig m, int canvasWidth, int contentWidthPx, float speedPxOverride, bool canvasWraps = false)
        => ComputeLapMs(m, new LapGeometry(canvasWidth, 0, contentWidthPx, 0, speedPxOverride, canvasWraps));

    /// <summary>
    /// The movement's natural period in ms — after it the sprite is back where (and moving how) it started — or 0 when
    /// it has none. Mirrors <see cref="MovementCalculator"/> per type:
    /// <list type="bullet">
    /// <item>Linear (Loop): start → end distance / speed; default path = canvas + sprite width (Ring: canvas only).</item>
    /// <item>SineWave (Loop): the horizontal lap, as Linear; the wave's own phase is cosmetic.</item>
    /// <item>Bounce: the horizontal round trip <c>2 × (canvasW − spriteW) / |vx|</c>; vertical when it does not move
    /// sideways. Both axes rarely line up, so "back at its start x, same direction" is the rest point.</item>
    /// <item>Circular: one orbit, <c>2π × radius / speed</c>.</item>
    /// <item>Static, RandomWalk, an IconZone path: none.</item>
    /// </list>
    /// </summary>
    public static int ComputeLapMs(MovementConfig m, LapGeometry g, BackgroundMode background = BackgroundMode.SolidColor)
    {
        if (MovementCalculator.FollowsIconPath(background, m)) return 0;
        float speed = g.SpeedPx > 0f ? g.SpeedPx : m.SpeedPixelsPerSecond;
        if (speed <= 0f) return 0;

        double distance;
        switch (m.Type)
        {
            case MovementType.Linear:
            {
                if (!m.Loop) return 0;
                float runIn = g.Wraps ? 0f : Math.Max(0, g.ContentWidth);
                float startX = m.StartX ?? (m.Reversed ? g.CanvasWidth : -runIn);
                float endX   = m.EndX   ?? (m.Reversed ? -runIn       : g.CanvasWidth);
                float startY = m.StartY ?? 0f;
                float endY   = m.EndY   ?? startY;
                double dx = endX - startX, dy = endY - startY;
                distance = Math.Sqrt(dx * dx + dy * dy);
                break;
            }
            case MovementType.SineWave:
                if (!m.Loop) return 0;
                distance = g.CanvasWidth + (g.Wraps ? 0 : Math.Max(0, g.ContentWidth));
                break;
            case MovementType.Bounce:
            {
                // Same float decomposition as MovementCalculator.CalculateBounce.
                float angleRad = m.DirectionAngleDegrees * MathF.PI / 180f;
                double vx = Math.Abs(MathF.Cos(angleRad) * speed), vy = Math.Abs(MathF.Sin(angleRad) * speed);
                double rangeX = g.CanvasWidth - g.ContentWidth, rangeY = g.CanvasHeight - g.ContentHeight;
                double periodSec = vx > 1e-3 && rangeX > 0 ? 2 * rangeX / vx
                                 : vy > 1e-3 && rangeY > 0 ? 2 * rangeY / vy
                                 : 0;
                return periodSec > 0 ? (int)Math.Round(periodSec * 1000.0) : 0;
            }
            case MovementType.Circular:
                if (m.OrbitRadiusPixels < 1f) return 0;
                distance = 2 * Math.PI * m.OrbitRadiusPixels;
                break;
            default:
                return 0;
        }
        return distance < 1.0 ? 0 : (int)Math.Round(distance / speed * 1000.0);
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

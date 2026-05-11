using System;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Pure deterministic math for computing animation position from elapsed time.
/// All Player.D2D processes compute the same (virtualX, virtualY) independently,
/// ensuring multi-monitor sync without per-frame IPC.
/// </summary>
public static class MovementCalculator
{
    /// <summary>
    /// Calculate animation position in virtual canvas coordinates.
    /// </summary>
    /// <param name="config">Movement configuration</param>
    /// <param name="elapsedMs">Elapsed time since animation start in milliseconds</param>
    /// <param name="animWidth">Animation width in pixels (after FitMode scaling)</param>
    /// <param name="animHeight">Animation height in pixels (after FitMode scaling)</param>
    /// <param name="canvasWidth">Total virtual canvas width in pixels (all monitors)</param>
    /// <param name="canvasHeight">Virtual canvas height in pixels</param>
    /// <returns>Position (X, Y) in virtual canvas coordinates</returns>
    public static (float X, float Y) Calculate(
        MovementConfig config,
        long elapsedMs,
        int animWidth, int animHeight,
        int canvasWidth, int canvasHeight,
        float tileAlignStepX = 0f)
    {
        return config.Type switch
        {
            MovementType.Static => CalculateStatic(animWidth, animHeight, canvasWidth, canvasHeight),
            MovementType.Linear => CalculateLinear(config, elapsedMs, animWidth, animHeight, canvasWidth, canvasHeight, tileAlignStepX),
            MovementType.Bounce => CalculateBounce(config, elapsedMs, animWidth, animHeight, canvasWidth, canvasHeight),
            MovementType.SineWave => CalculateSineWave(config, elapsedMs, animWidth, animHeight, canvasWidth, canvasHeight),
            MovementType.Circular => CalculateCircular(config, elapsedMs, animWidth, animHeight, canvasWidth, canvasHeight),
            MovementType.RandomWalk => CalculateRandomWalk(config, elapsedMs, animWidth, animHeight, canvasWidth, canvasHeight),
            _ => CalculateStatic(animWidth, animHeight, canvasWidth, canvasHeight)
        };
    }

    private static (float X, float Y) CalculateStatic(
        int animWidth, int animHeight, int canvasWidth, int canvasHeight)
    {
        float x = (canvasWidth - animWidth) / 2f;
        float y = (canvasHeight - animHeight) / 2f;
        return (x, y);
    }

    private static (float X, float Y) CalculateLinear(
        MovementConfig config, long elapsedMs,
        int animWidth, int animHeight, int canvasWidth, int canvasHeight,
        float tileAlignStepX = 0f)
    {
        // Reversed=true → cells travel left-to-right visually (first node → last node)
        float startX = config.StartX ?? (config.Reversed ? canvasWidth  : -animWidth);
        float startY = config.StartY ?? (canvasHeight - animHeight) / 2f;
        float endX   = config.EndX   ?? (config.Reversed ? -animWidth   : canvasWidth);
        float endY   = config.EndY   ?? (canvasHeight - animHeight) / 2f;

        float dx = endX - startX;
        float dy = endY - startY;
        float totalDistance = MathF.Sqrt(dx * dx + dy * dy);

        if (totalDistance < 1f)
            return (startX, startY);

        float elapsedSec = elapsedMs / 1000f;
        float traveledDistance = elapsedSec * config.SpeedPixelsPerSecond;

        if (config.Loop && totalDistance > 0)
        {
            // Snap the loop period to the nearest multiple of the tile step so visible cell
            // indices (LogicalI) are identical at the wrap boundary → no abrupt color jump.
            float period = totalDistance;
            if (tileAlignStepX > 0f)
            {
                float snapped = MathF.Round(totalDistance / tileAlignStepX) * tileAlignStepX;
                if (snapped > 0f) period = snapped;
            }
            traveledDistance %= period;
        }
        else
        {
            traveledDistance = MathF.Min(traveledDistance, totalDistance);
        }

        float t = traveledDistance / totalDistance;
        float x = startX + dx * t;
        float y = startY + dy * t;
        return (x, y);
    }

    private static (float X, float Y) CalculateBounce(
        MovementConfig config, long elapsedMs,
        int animWidth, int animHeight, int canvasWidth, int canvasHeight)
    {
        // Decompose speed into X and Y components based on angle
        float angleRad = config.DirectionAngleDegrees * MathF.PI / 180f;
        float vx = MathF.Cos(angleRad) * config.SpeedPixelsPerSecond;
        float vy = MathF.Sin(angleRad) * config.SpeedPixelsPerSecond;

        float elapsedSec = elapsedMs / 1000f;

        // Available range for each axis (animation must stay within canvas)
        float rangeX = canvasWidth - animWidth;
        float rangeY = canvasHeight - animHeight;

        // Start from center of canvas
        float startX = config.StartX ?? rangeX / 2f;
        float startY = config.StartY ?? rangeY / 2f;

        float x = BounceAxis(startX, vx * elapsedSec, rangeX);
        float y = BounceAxis(startY, vy * elapsedSec, rangeY);

        return (x, y);
    }

    /// <summary>
    /// Compute bouncing position along one axis using the "unfold and fold" method.
    /// Maps any distance traveled to a position within [0, range] with reflections at boundaries.
    /// </summary>
    private static float BounceAxis(float start, float distance, float range)
    {
        if (range <= 0)
            return 0;

        // Current position (may be out of bounds)
        float pos = start + distance;

        // Normalize to [0, 2*range] period using modular arithmetic
        float period = 2f * range;

        // Handle negative positions
        pos %= period;
        if (pos < 0) pos += period;

        // Fold: if past the midpoint, reflect back
        if (pos > range)
            pos = period - pos;

        return MathF.Max(0, MathF.Min(range, pos));
    }

    private static (float X, float Y) CalculateSineWave(
        MovementConfig config, long elapsedMs,
        int animWidth, int animHeight, int canvasWidth, int canvasHeight)
    {
        float elapsedSec = elapsedMs / 1000f;

        // X: horizontal travel across the full canvas width (wrapping).
        // Reversed=true → right-to-left (starts at canvasWidth, moves left).
        float totalXDistance = canvasWidth + animWidth;
        float xTraveled = elapsedSec * config.SpeedPixelsPerSecond;

        float x;
        if (config.Loop)
        {
            float phase = xTraveled % totalXDistance;
            x = config.Reversed ? (canvasWidth - phase) : (-animWidth + phase);
        }
        else
        {
            float phase = MathF.Min(xTraveled, totalXDistance);
            x = config.Reversed ? (canvasWidth - phase) : (-animWidth + phase);
        }

        // Y: sine wave oscillation centered vertically
        float centerY = (canvasHeight - animHeight) / 2f;
        float y = centerY + config.WaveAmplitudePixels * MathF.Sin(2f * MathF.PI * config.WaveFrequencyHz * elapsedSec);

        // Clamp Y to canvas bounds
        y = MathF.Max(0, MathF.Min(canvasHeight - animHeight, y));

        return (x, y);
    }

    private static (float X, float Y) CalculateCircular(
        MovementConfig config, long elapsedMs,
        int animWidth, int animHeight, int canvasWidth, int canvasHeight)
    {
        float centerX = config.OrbitCenterX ?? canvasWidth / 2f;
        float centerY = config.OrbitCenterY ?? canvasHeight / 2f;
        float radius = config.OrbitRadiusPixels;

        if (radius < 1f)
            return (centerX - animWidth / 2f, centerY - animHeight / 2f);

        float elapsedSec = elapsedMs / 1000f;

        // Angular velocity: omega = speed / radius (radians per second).
        // Reversed=true → counter-clockwise (negate angle).
        float omega = config.SpeedPixelsPerSecond / radius;
        float angle = (config.Reversed ? -1f : 1f) * omega * elapsedSec;

        // Position is the top-left corner of the animation bounding box
        float x = centerX + radius * MathF.Cos(angle) - animWidth / 2f;
        float y = centerY + radius * MathF.Sin(angle) - animHeight / 2f;

        return (x, y);
    }

    private static (float X, float Y) CalculateRandomWalk(
        MovementConfig config, long elapsedMs,
        int animWidth, int animHeight, int canvasWidth, int canvasHeight)
    {
        float stepIntervalMs = MathF.Max(100f, config.RandomStepIntervalMs);
        int currentStepIndex = (int)(elapsedMs / stepIntervalMs);
        float withinStep = (elapsedMs % stepIntervalMs) / stepIntervalMs; // 0..1 interpolation factor

        // Rolling seed: every IterationStepCount steps, rotate to a new seed so the walk
        // never settles into a visible repeating pattern. All monitors advance together
        // (same elapsedMs → same iterationIndex → same seed change).
        int iterationIndex = (config.IterationStepCount > 0)
            ? currentStepIndex / config.IterationStepCount
            : 0;
        int effectiveSeed = config.RandomSeed ^ (int)((uint)iterationIndex * 2246822519u);

        // Generate waypoint for the current step and the next step
        var (x0, y0) = GenerateWaypoint(effectiveSeed, currentStepIndex, animWidth, animHeight, canvasWidth, canvasHeight);
        var (x1, y1) = GenerateWaypoint(effectiveSeed, currentStepIndex + 1, animWidth, animHeight, canvasWidth, canvasHeight);

        // Smooth interpolation using cubic ease-in-out
        float t = SmoothStep(withinStep);
        float x = x0 + (x1 - x0) * t;
        float y = y0 + (y1 - y0) * t;

        return (x, y);
    }

    /// <summary>
    /// Generate a deterministic waypoint for a given step index.
    /// Same seed + stepIndex always produces the same point on all monitors.
    /// </summary>
    private static (float X, float Y) GenerateWaypoint(
        int seed, int stepIndex,
        int animWidth, int animHeight,
        int canvasWidth, int canvasHeight)
    {
        // Combine seed and step index for deterministic randomness
        var rng = new Random(seed ^ (int)((uint)stepIndex * 2654435761u)); // Knuth multiplicative hash

        float rangeX = MathF.Max(0, canvasWidth - animWidth);
        float rangeY = MathF.Max(0, canvasHeight - animHeight);

        float x = (float)(rng.NextDouble() * rangeX);
        float y = (float)(rng.NextDouble() * rangeY);

        return (x, y);
    }

    /// <summary>
    /// Smooth-step interpolation (cubic ease-in-out) for smooth random walk transitions
    /// </summary>
    private static float SmoothStep(float t)
    {
        t = MathF.Max(0, MathF.Min(1, t));
        return t * t * (3f - 2f * t);
    }
}

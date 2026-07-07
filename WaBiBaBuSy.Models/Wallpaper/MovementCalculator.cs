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

        // Fold in DOUBLE before converting to float: after days of uptime, float ulp
        // on the unbounded traveled distance reaches multiple pixels (visible stutter).
        double traveledDistance = elapsedMs / 1000.0 * config.SpeedPixelsPerSecond;

        if (config.Loop && totalDistance > 0)
        {
            // Snap the loop period to the nearest multiple of the tile step so visible cell
            // indices (LogicalI) are identical at the wrap boundary → no abrupt color jump.
            double period = totalDistance;
            if (tileAlignStepX > 0f)
            {
                double snapped = Math.Round(totalDistance / (double)tileAlignStepX) * tileAlignStepX;
                if (snapped > 0) period = snapped;
            }
            traveledDistance %= period;
        }
        else
        {
            traveledDistance = Math.Min(traveledDistance, totalDistance);
        }

        float t = (float)(traveledDistance / totalDistance);
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
        double vx = MathF.Cos(angleRad) * config.SpeedPixelsPerSecond;
        double vy = MathF.Sin(angleRad) * config.SpeedPixelsPerSecond;

        double elapsedSec = elapsedMs / 1000.0;

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
    /// Distance is folded in double so long-uptime float error never reaches the pixel position.
    /// </summary>
    private static float BounceAxis(float start, double distance, float range)
    {
        if (range <= 0)
            return 0;

        // Current position (may be out of bounds)
        double pos = start + distance;

        // Normalize to [0, 2*range] period using modular arithmetic
        double period = 2.0 * range;

        // Handle negative positions
        pos %= period;
        if (pos < 0) pos += period;

        // Fold: if past the midpoint, reflect back
        if (pos > range)
            pos = period - pos;

        return MathF.Max(0, MathF.Min(range, (float)pos));
    }

    private static (float X, float Y) CalculateSineWave(
        MovementConfig config, long elapsedMs,
        int animWidth, int animHeight, int canvasWidth, int canvasHeight)
    {
        double elapsedSec = elapsedMs / 1000.0;

        // X: horizontal travel across the full canvas width (wrapping).
        // Reversed=true → right-to-left (starts at canvasWidth, moves left).
        // Phase is folded in double so long uptimes keep sub-pixel accuracy.
        float totalXDistance = canvasWidth + animWidth;
        double xTraveled = elapsedSec * config.SpeedPixelsPerSecond;

        float phase = config.Loop
            ? (float)(xTraveled % totalXDistance)
            : (float)Math.Min(xTraveled, totalXDistance);
        float x = config.Reversed ? (canvasWidth - phase) : (-animWidth + phase);

        // Y: sine wave oscillation centered vertically. Fold elapsed into one wave
        // period in double first — Sin of a huge argument loses all accuracy.
        float centerY = (canvasHeight - animHeight) / 2f;
        float y = centerY;
        if (config.WaveFrequencyHz > 0f)
        {
            double wavePeriodSec = 1.0 / config.WaveFrequencyHz;
            double tInPeriod = elapsedSec % wavePeriodSec;
            y += config.WaveAmplitudePixels * (float)Math.Sin(2.0 * Math.PI * config.WaveFrequencyHz * tInPeriod);
        }

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

        // Angular velocity: omega = speed / radius (radians per second).
        // Reversed=true → counter-clockwise (negate angle).
        // Fold the angle into [0, 2π) in double before trig — Cos/Sin of an
        // unbounded float angle degrades after hours of uptime.
        double omega = config.SpeedPixelsPerSecond / (double)radius;
        double angle = (config.Reversed ? -1.0 : 1.0) * omega * (elapsedMs / 1000.0);
        angle %= 2.0 * Math.PI;

        // Position is the top-left corner of the animation bounding box
        float x = centerX + radius * (float)Math.Cos(angle) - animWidth / 2f;
        float y = centerY + radius * (float)Math.Sin(angle) - animHeight / 2f;

        return (x, y);
    }

    private static (float X, float Y) CalculateRandomWalk(
        MovementConfig config, long elapsedMs,
        int animWidth, int animHeight, int canvasWidth, int canvasHeight)
    {
        double stepIntervalMs = Math.Max(100f, config.RandomStepIntervalMs);
        // Double math: elapsedMs is exact in double, so step index and interpolation
        // factor stay accurate no matter how long the wallpaper has been running.
        double stepPosition = elapsedMs / stepIntervalMs;
        int currentStepIndex = (int)stepPosition;
        float withinStep = (float)(stepPosition - currentStepIndex); // 0..1 interpolation factor

        // Generate waypoint for the current step and the next step. Seed rotation is
        // resolved per-waypoint (see WaypointIteration) so the boundary waypoint is
        // shared by the segments before and after it — no teleport on rotation.
        var (x0, y0) = GenerateWaypoint(config, currentStepIndex, animWidth, animHeight, canvasWidth, canvasHeight);
        var (x1, y1) = GenerateWaypoint(config, currentStepIndex + 1, animWidth, animHeight, canvasWidth, canvasHeight);

        // Smooth interpolation using cubic ease-in-out
        float t = SmoothStep(withinStep);
        float x = x0 + (x1 - x0) * t;
        float y = y0 + (y1 - y0) * t;

        return (x, y);
    }

    /// <summary>
    /// Which seed iteration a waypoint belongs to. Rolling seed: every
    /// IterationStepCount steps the walk rotates to a new seed so it never visibly
    /// repeats. The waypoint at an exact rotation boundary (stepIndex % count == 0)
    /// is pinned to the PREVIOUS iteration's seed: the segment approaching the
    /// boundary and the segment leaving it then share that waypoint, keeping the
    /// path continuous. All monitors advance together (same elapsedMs → same seed).
    /// </summary>
    private static int WaypointIteration(int stepIndex, int iterationStepCount)
    {
        if (iterationStepCount <= 0) return 0;
        if (stepIndex > 0 && stepIndex % iterationStepCount == 0)
            return stepIndex / iterationStepCount - 1;
        return stepIndex / iterationStepCount;
    }

    /// <summary>
    /// Generate a deterministic waypoint for a given step index.
    /// Same config + stepIndex always produces the same point on all monitors.
    /// </summary>
    private static (float X, float Y) GenerateWaypoint(
        MovementConfig config, int stepIndex,
        int animWidth, int animHeight,
        int canvasWidth, int canvasHeight)
    {
        int iteration = WaypointIteration(stepIndex, config.IterationStepCount);
        int effectiveSeed = config.RandomSeed ^ (int)((uint)iteration * 2246822519u);

        // Combine seed and step index for deterministic randomness
        var rng = new Random(effectiveSeed ^ (int)((uint)stepIndex * 2654435761u)); // Knuth multiplicative hash

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

using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Wallpapers run for days. Elapsed time must be folded into the movement period
/// in DOUBLE precision before any float math, otherwise float ulp at large elapsed
/// (~0.25s at 40 days) quantizes positions into visible multi-pixel jumps.
/// These tests compare the position at a huge elapsed time against the position at
/// the equivalent small phase time — they must agree to sub-pixel accuracy.
/// </summary>
public class MovementCalculatorLongRunTests
{
    private const long FortyDaysMs = 40L * 24 * 3600 * 1000; // 3,456,000,000 ms
    private const int AnimW = 200, AnimH = 200, CanvasW = 3840, CanvasH = 1080;

    [Fact]
    public void Linear_FortyDaysUptime_MatchesEquivalentPhase()
    {
        var config = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 500f, Loop = true };

        // Default path: (-animW, centerY) → (canvasW, centerY); period = canvasW + animW.
        double periodPx = CanvasW + AnimW;
        double periodMs = periodPx / 500.0 * 1000.0;
        long equivalentMs = (long)(FortyDaysMs % periodMs);

        var big = MovementCalculator.Calculate(config, FortyDaysMs, AnimW, AnimH, CanvasW, CanvasH);
        var small = MovementCalculator.Calculate(config, equivalentMs, AnimW, AnimH, CanvasW, CanvasH);

        Assert.True(Math.Abs(big.X - small.X) < 1f, $"X drifted: {big.X} vs {small.X}");
        Assert.True(Math.Abs(big.Y - small.Y) < 1f, $"Y drifted: {big.Y} vs {small.Y}");
    }

    [Fact]
    public void SineWave_FortyDaysUptime_YMatchesEquivalentPhase()
    {
        var config = new MovementConfig
        {
            Type = MovementType.SineWave,
            SpeedPixelsPerSecond = 500f,
            WaveFrequencyHz = 0.5f,
            WaveAmplitudePixels = 200f,
            Loop = true
        };

        // Wave period = 2s. Pick a big elapsed that is an exact multiple of the wave
        // period plus 500ms, so Y must equal Y at 500ms.
        long bigMs = FortyDaysMs - (FortyDaysMs % 2000) + 500;

        var big = MovementCalculator.Calculate(config, bigMs, AnimW, AnimH, CanvasW, CanvasH);
        var small = MovementCalculator.Calculate(config, 500, AnimW, AnimH, CanvasW, CanvasH);

        Assert.True(Math.Abs(big.Y - small.Y) < 1f, $"Y drifted: {big.Y} vs {small.Y}");
    }

    [Fact]
    public void Circular_FortyDaysUptime_MatchesEquivalentAngle()
    {
        var config = new MovementConfig
        {
            Type = MovementType.Circular,
            SpeedPixelsPerSecond = 500f,
            OrbitRadiusPixels = 500f
        };

        // omega = 1 rad/s → angular period = 2π s.
        double angularPeriodMs = 2.0 * Math.PI * 1000.0;
        long equivalentMs = (long)(FortyDaysMs % angularPeriodMs);

        var big = MovementCalculator.Calculate(config, FortyDaysMs, AnimW, AnimH, CanvasW, CanvasH);
        var small = MovementCalculator.Calculate(config, equivalentMs, AnimW, AnimH, CanvasW, CanvasH);

        Assert.True(Math.Abs(big.X - small.X) < 1.5f, $"X drifted: {big.X} vs {small.X}");
        Assert.True(Math.Abs(big.Y - small.Y) < 1.5f, $"Y drifted: {big.Y} vs {small.Y}");
    }

    [Fact]
    public void Bounce_FortyDaysUptime_MatchesEquivalentPhase()
    {
        var config = new MovementConfig
        {
            Type = MovementType.Bounce,
            SpeedPixelsPerSecond = 500f,
            DirectionAngleDegrees = 0f // pure horizontal: vx=500, vy=0
        };

        // Horizontal bounce period = 2 * rangeX px.
        double rangeX = CanvasW - AnimW;
        double periodMs = 2.0 * rangeX / 500.0 * 1000.0;
        long equivalentMs = (long)(FortyDaysMs % periodMs);

        var big = MovementCalculator.Calculate(config, FortyDaysMs, AnimW, AnimH, CanvasW, CanvasH);
        var small = MovementCalculator.Calculate(config, equivalentMs, AnimW, AnimH, CanvasW, CanvasH);

        Assert.True(Math.Abs(big.X - small.X) < 1f, $"X drifted: {big.X} vs {small.X}");
    }
}

/// <summary>
/// RandomWalk rotates its seed every IterationStepCount steps so the walk never
/// visibly repeats. The waypoint at the exact rotation boundary must be shared by
/// the segment before and after it, otherwise the sprite teleports on every rotation.
/// </summary>
public class MovementCalculatorRandomWalkContinuityTests
{
    private const int AnimW = 200, AnimH = 200, CanvasW = 3840, CanvasH = 1080;

    [Fact]
    public void RandomWalk_NoTeleportAtSeedRotationBoundary()
    {
        var config = new MovementConfig
        {
            Type = MovementType.RandomWalk,
            RandomSeed = 42,
            RandomStepIntervalMs = 1000f,
            IterationStepCount = 20
        };

        // Check several rotation boundaries: elapsed = k * 20 * 1000ms.
        for (int k = 1; k <= 5; k++)
        {
            long boundaryMs = k * 20L * 1000L;
            var before = MovementCalculator.Calculate(config, boundaryMs - 1, AnimW, AnimH, CanvasW, CanvasH);
            var after = MovementCalculator.Calculate(config, boundaryMs + 1, AnimW, AnimH, CanvasW, CanvasH);

            // SmoothStep has zero velocity at segment ends, so 2ms apart must be sub-pixel.
            var dx = Math.Abs(after.X - before.X);
            var dy = Math.Abs(after.Y - before.Y);
            Assert.True(dx < 1f && dy < 1f,
                $"Teleport at boundary {k}: ({before.X:F1},{before.Y:F1}) → ({after.X:F1},{after.Y:F1})");
        }
    }

    [Fact]
    public void RandomWalk_StillVariesAcrossIterations()
    {
        var config = new MovementConfig
        {
            Type = MovementType.RandomWalk,
            RandomSeed = 42,
            RandomStepIntervalMs = 1000f,
            IterationStepCount = 20
        };

        // Same step-within-iteration in two different iterations should differ
        // (seed rotation still effective — the fix must not freeze the seed).
        var iter0 = MovementCalculator.Calculate(config, 5_500, AnimW, AnimH, CanvasW, CanvasH);
        var iter1 = MovementCalculator.Calculate(config, 25_500, AnimW, AnimH, CanvasW, CanvasH);
        Assert.True(Math.Abs(iter0.X - iter1.X) > 1f || Math.Abs(iter0.Y - iter1.Y) > 1f,
            "Seed rotation appears ineffective — different iterations produced identical positions");
    }

    [Fact]
    public void RandomWalk_DeterministicAcrossCalls()
    {
        var config = new MovementConfig
        {
            Type = MovementType.RandomWalk,
            RandomSeed = 7,
            RandomStepIntervalMs = 800f,
            IterationStepCount = 10
        };

        var a = MovementCalculator.Calculate(config, 123_456, AnimW, AnimH, CanvasW, CanvasH);
        var b = MovementCalculator.Calculate(config, 123_456, AnimW, AnimH, CanvasW, CanvasH);
        Assert.Equal(a.X, b.X);
        Assert.Equal(a.Y, b.Y);
    }
}

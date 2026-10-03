using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// RandomWalk is sampled many times per frame by the sprite effects (trail ghosts, bezel scan). Its waypoints must
/// stay exactly what they were (every machine computes the same path) and repeated samples must not allocate.
/// </summary>
public class RandomWalkMemoTests
{
    private static readonly MovementConfig Walk = new()
    {
        Type = MovementType.RandomWalk, RandomSeed = 1234, RandomStepIntervalMs = 800, IterationStepCount = 7,
    };

    /// <summary>The waypoint algorithm as it was before the memo (verbatim), as the reference.</summary>
    private static (float X, float Y) ReferenceWaypoint(MovementConfig config, int stepIndex, int animW, int animH, int canvasW, int canvasH)
    {
        int iterationStepCount = config.IterationStepCount;
        int iteration = iterationStepCount <= 0 ? 0
            : stepIndex > 0 && stepIndex % iterationStepCount == 0 ? stepIndex / iterationStepCount - 1
            : stepIndex / iterationStepCount;
        int effectiveSeed = config.RandomSeed ^ (int)((uint)iteration * 2246822519u);
        var rng = new Random(effectiveSeed ^ (int)((uint)stepIndex * 2654435761u));
        float rangeX = MathF.Max(0, canvasW - animW);
        float rangeY = MathF.Max(0, canvasH - animH);
        return ((float)(rng.NextDouble() * rangeX), (float)(rng.NextDouble() * rangeY));
    }

    private static (float X, float Y) Reference(long elapsedMs)
    {
        double stepPosition = elapsedMs / (double)Math.Max(100f, Walk.RandomStepIntervalMs);
        int i = (int)stepPosition;
        float within = (float)(stepPosition - i);
        var (x0, y0) = ReferenceWaypoint(Walk, i, 100, 80, 3000, 1000);
        var (x1, y1) = ReferenceWaypoint(Walk, i + 1, 100, 80, 3000, 1000);
        within = MathF.Max(0, MathF.Min(1, within));
        float t = within * within * (3f - 2f * within);
        return (x0 + (x1 - x0) * t, y0 + (y1 - y0) * t);
    }

    [Fact]
    public void Positions_AreBitIdenticalToTheReference()
    {
        for (long t = 0; t < 60_000; t += 137)
            Assert.Equal(Reference(t), MovementCalculator.Calculate(Walk, t, 100, 80, 3000, 1000));
        // Days of uptime too.
        long late = 40L * 24 * 3600 * 1000 + 55;
        Assert.Equal(Reference(late), MovementCalculator.Calculate(Walk, late, 100, 80, 3000, 1000));
    }

    [Fact]
    public void RepeatedSamples_DoNotAllocate()
    {
        // A frame's worth of samples (bezel scan + trail): warm up once, then the same window must allocate nothing.
        void Frame(long now)
        {
            for (long t = now; t > now - 400; t -= 16)
                MovementCalculator.Calculate(Walk, t, 100, 80, 3000, 1000);
        }
        Frame(10_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) Frame(10_000 + i);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

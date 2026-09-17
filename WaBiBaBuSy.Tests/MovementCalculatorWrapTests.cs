using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Ring canvas: Linear/SineWave laps are exactly one perimeter with no off-screen run-in.</summary>
public class MovementCalculatorWrapTests
{
    private const int P = 44400;   // 20 × 1920 + 2 × 3000
    private const int W = 400, H = 300;

    [Fact]
    public void Linear_Wrapping_StartsAtZero_AndReturnsAfterOnePerimeter()
    {
        var cfg = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 1000f, Loop = true };

        var (x0, _) = MovementCalculator.Calculate(cfg, 0, W, H, P, 1080, canvasWraps: true);
        Assert.Equal(0f, x0, 3);

        long lapMs = (long)(P / 1000.0 * 1000);
        var (xLap, _) = MovementCalculator.Calculate(cfg, lapMs, W, H, P, 1080, canvasWraps: true);
        Assert.Equal(0f, xLap, 1);

        var (xHalf, _) = MovementCalculator.Calculate(cfg, lapMs / 2, W, H, P, 1080, canvasWraps: true);
        Assert.Equal(P / 2f, xHalf, 1);
    }

    [Fact]
    public void Linear_Wrapping_StaysInsidePerimeter()
    {
        var cfg = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 777f, Loop = true };
        for (long t = 0; t < 200_000; t += 1234)
        {
            var (x, _) = MovementCalculator.Calculate(cfg, t, W, H, P, 1080, canvasWraps: true);
            Assert.InRange(x, 0f, P);
        }
    }

    [Fact]
    public void Linear_Wrapping_Reversed_RunsFromPerimeterDown()
    {
        var cfg = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 1000f, Loop = true, Reversed = true };
        var (x, _) = MovementCalculator.Calculate(cfg, 1000, W, H, P, 1080, canvasWraps: true);
        Assert.Equal(P - 1000f, x, 1);
    }

    [Fact]
    public void Linear_NonWrapping_IsUnchanged()
    {
        var cfg = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 1000f, Loop = true };
        var (x0, _) = MovementCalculator.Calculate(cfg, 0, W, H, P, 1080);
        Assert.Equal(-W, x0, 3);                       // classic off-screen run-in
        var (xDefault, _) = MovementCalculator.Calculate(cfg, 5000, W, H, P, 1080);
        var (xExplicit, _) = MovementCalculator.Calculate(cfg, 5000, W, H, P, 1080, canvasWraps: false);
        Assert.Equal(xDefault, xExplicit);
    }

    [Fact]
    public void SineWave_Wrapping_LapIsOnePerimeter()
    {
        var cfg = new MovementConfig { Type = MovementType.SineWave, SpeedPixelsPerSecond = 1000f, Loop = true, WaveFrequencyHz = 0f };
        var (x0, _) = MovementCalculator.Calculate(cfg, 0, W, H, P, 1080, canvasWraps: true);
        Assert.Equal(0f, x0, 3);
        var (xLap, _) = MovementCalculator.Calculate(cfg, P, W, H, P, 1080, canvasWraps: true); // 1000 px/s → P ms
        Assert.Equal(0f, xLap, 1);
    }

    [Fact]
    public void Wrap_IsIgnoredByBounceCircularRandomWalk()
    {
        foreach (var type in new[] { MovementType.Bounce, MovementType.Circular, MovementType.RandomWalk })
        {
            var cfg = new MovementConfig { Type = type, SpeedPixelsPerSecond = 500f };
            var a = MovementCalculator.Calculate(cfg, 4321, W, H, P, 1080);
            var b = MovementCalculator.Calculate(cfg, 4321, W, H, P, 1080, canvasWraps: true);
            Assert.Equal(a, b);
        }
    }
}

using System.Linq;
using System.Text.Json;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class PlaylistTests
{
    [Fact]
    public void Playlist_RoundtripsThroughJson_PreservingNestedConfig()
    {
        var original = new Playlist
        {
            Name = "Party",
            Loop = true,
            Shuffle = true,
            DefaultItemDurationMs = 45_000,
            Items =
            {
                new PlaylistItem
                {
                    Name = "Fish",
                    DurationMs = 12_000,
                    SnapToLap = true,
                    Config = new CrossScreenConfig
                    {
                        Layers = { new SceneLayer {
                            Distribution = AnimationDistributionMode.Sequential,
                            Movement = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 640f },
                            Animation = new AnimationLayerConfig { AnimationPath = "fish.gif", TargetHeight = 300 } } },

                    },
                },
                new PlaylistItem { Name = "Idle", DurationMs = null, SnapToLap = false },
            },
        };

        var restored = JsonSerializer.Deserialize<Playlist>(JsonSerializer.Serialize(original))!;

        Assert.Equal("Party", restored.Name);
        Assert.True(restored.Shuffle);
        Assert.Equal(45_000, restored.DefaultItemDurationMs);
        Assert.Equal(2, restored.Items.Count);
        Assert.Equal("Fish", restored.Items[0].Name);
        Assert.Equal(12_000, restored.Items[0].DurationMs);
        Assert.True(restored.Items[0].SnapToLap);
        Assert.Equal(MovementType.Linear, restored.Items[0].Config.Layers[0].Movement.Type);
        Assert.Equal("fish.gif", restored.Items[0].Config.Layers[0].Animation.AnimationPath);
        Assert.Null(restored.Items[1].DurationMs);
    }

    [Theory]
    [InlineData(12_000, 30_000, 12_000)] // explicit override wins
    [InlineData(null, 30_000, 30_000)]   // null falls back to playlist default
    public void GetEffectiveDurationMs_ResolvesOverrideOrDefault(int? itemMs, int defaultMs, int expected)
    {
        var item = new PlaylistItem { DurationMs = itemMs };
        Assert.Equal(expected, item.GetEffectiveDurationMs(defaultMs));
    }

    // --- ComputeLapMs -------------------------------------------------------

    [Fact]
    public void ComputeLapMs_Linear_MatchesCanvasPlusContentOverSpeed()
    {
        // Default Linear: startX=-animWidth, endX=canvasWidth => distance = canvasWidth + animWidth.
        var mv = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 500f, Loop = true };
        // distance = 4000 + 200 = 4200 px; /500 px/s = 8.4 s = 8400 ms.
        Assert.Equal(8400, PlaylistScheduler.ComputeLapMs(mv, canvasWidth: 4000, contentWidthPx: 200));
    }

    [Fact]
    public void ComputeLapMs_Ring_IsCanvasOverSpeed_NoRunIn()
    {
        // Review 2026-10-13 E7: on a Ring the sprite wraps (MovementCalculator runIn = 0), so a lap is 4000 px, not 4200.
        var mv = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 500f, Loop = true };
        Assert.Equal(8000, PlaylistScheduler.ComputeLapMs(mv, 4000, 200, speedPxOverride: 0f, canvasWraps: true));
    }

    [Fact]
    public void ComputeLapMs_NoPeriod_ForStaticRandomWalkAndTheIconZonePath()
    {
        foreach (var t in new[] { MovementType.Static, MovementType.RandomWalk })
        {
            var mv = new MovementConfig { Type = t, SpeedPixelsPerSecond = 500f, Loop = true };
            Assert.Equal(0, PlaylistScheduler.ComputeLapMs(mv, 4000, 200));
        }
        var linear = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 500f, Loop = true };
        Assert.Equal(0, PlaylistScheduler.ComputeLapMs(linear, Geo(), BackgroundMode.IconZone));
    }

    private static PlaylistScheduler.LapGeometry Geo(bool wraps = false) => new(4000, 1080, 200, 150, 0f, wraps);

    /// <summary>The period is real when the movement is back at the same position one period later (MovementCalculator).</summary>
    private static void AssertPeriodic(MovementConfig m, int periodMs, bool checkY, bool wraps = false)
    {
        Assert.True(periodMs > 0);
        foreach (long t0 in new long[] { 0, 1234, 98_765 })
        {
            var a = MovementCalculator.Calculate(m, t0, 200, 150, 4000, 1080, canvasWraps: wraps);
            var b = MovementCalculator.Calculate(m, t0 + periodMs, 200, 150, 4000, 1080, canvasWraps: wraps);
            Assert.True(Math.Abs(a.X - b.X) < 1.5f, $"{m.Type} x {a.X} vs {b.X} at t={t0}");
            if (checkY) Assert.True(Math.Abs(a.Y - b.Y) < 1.5f, $"{m.Type} y {a.Y} vs {b.Y} at t={t0}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComputeLapMs_SineWave_IsTheHorizontalLap(bool wraps)
    {
        var m = new MovementConfig { Type = MovementType.SineWave, SpeedPixelsPerSecond = 500f, Loop = true, WaveFrequencyHz = 0.3f, WaveAmplitudePixels = 100 };
        int lap = PlaylistScheduler.ComputeLapMs(m, Geo(wraps));
        Assert.Equal(wraps ? 8000 : 8400, lap);
        AssertPeriodic(m, lap, checkY: false, wraps);
    }

    [Theory]
    [InlineData(33f)]
    [InlineData(90f)]   // straight up and down: the vertical round trip
    public void ComputeLapMs_Bounce_IsTheRoundTrip(float angle)
    {
        var m = new MovementConfig { Type = MovementType.Bounce, SpeedPixelsPerSecond = 420f, DirectionAngleDegrees = angle };
        int lap = PlaylistScheduler.ComputeLapMs(m, Geo());
        AssertPeriodic(m, lap, checkY: angle == 90f);
    }

    [Fact]
    public void ComputeLapMs_Circular_IsOneOrbit()
    {
        var m = new MovementConfig { Type = MovementType.Circular, SpeedPixelsPerSecond = 300f, OrbitRadiusPixels = 250f };
        int lap = PlaylistScheduler.ComputeLapMs(m, Geo());
        Assert.Equal((int)Math.Round(2 * Math.PI * 250 / 300 * 1000), lap);
        AssertPeriodic(m, lap, checkY: true);
    }

    [Fact]
    public void ComputeLapMs_ZeroSpeedOrNoLoop_ReturnsZero()
    {
        Assert.Equal(0, PlaylistScheduler.ComputeLapMs(
            new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 0f, Loop = true }, 4000, 200));
        Assert.Equal(0, PlaylistScheduler.ComputeLapMs(
            new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 500f, Loop = false }, 4000, 200));
    }

    // --- ResolveDwellMs -----------------------------------------------------

    [Fact]
    public void ResolveDwellMs_HardCut_ReturnsEffectiveDuration()
    {
        var item = new PlaylistItem { DurationMs = 5_000, SnapToLap = false };
        Assert.Equal(5_000, PlaylistScheduler.ResolveDwellMs(item, playlistDefaultMs: 30_000, lapMs: 8_400));
    }

    [Fact]
    public void ResolveDwellMs_LapSnap_RoundsUpToWholeLap()
    {
        var item = new PlaylistItem { DurationMs = 10_000, SnapToLap = true };
        // ceil(10000 / 8400) = 2 laps => 16800 ms.
        Assert.Equal(16_800, PlaylistScheduler.ResolveDwellMs(item, playlistDefaultMs: 30_000, lapMs: 8_400));
    }

    [Fact]
    public void ResolveDwellMs_LapSnap_WithZeroLap_FallsBackToHardCut()
    {
        var item = new PlaylistItem { DurationMs = 10_000, SnapToLap = true };
        Assert.Equal(10_000, PlaylistScheduler.ResolveDwellMs(item, playlistDefaultMs: 30_000, lapMs: 0));
    }

    [Fact]
    public void ResolveDwellMs_LapSnap_ExactMultiple_DoesNotRoundUp()
    {
        var item = new PlaylistItem { DurationMs = 8_400, SnapToLap = true };
        // target == lapMs => exactly 1 lap, no extra rounding.
        Assert.Equal(8_400, PlaylistScheduler.ResolveDwellMs(item, playlistDefaultMs: 30_000, lapMs: 8_400));
    }

    [Fact]
    public void ComputeLapMs_DegenerateZeroDistance_ReturnsZero()
    {
        // start == end (StartX == EndX, no Y delta) => distance < 1 => 0.
        var mv = new MovementConfig
        {
            Type = MovementType.Linear, SpeedPixelsPerSecond = 500f, Loop = true,
            StartX = 100f, EndX = 100f,
        };
        Assert.Equal(0, PlaylistScheduler.ComputeLapMs(mv, canvasWidth: 4000, contentWidthPx: 200));
    }

    // --- BuildCycleOrder ----------------------------------------------------

    [Fact]
    public void BuildCycleOrder_NoShuffle_ReturnsNaturalOrder()
    {
        Assert.Equal(new[] { 0, 1, 2, 3 }, PlaylistScheduler.BuildCycleOrder(4, shuffle: false, seed: 123));
    }

    [Fact]
    public void BuildCycleOrder_Shuffle_IsPermutationCoveringEveryItemOnce()
    {
        var order = PlaylistScheduler.BuildCycleOrder(6, shuffle: true, seed: 999);
        Assert.Equal(6, order.Length);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, order.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void BuildCycleOrder_Shuffle_IsDeterministicForSameSeed()
    {
        Assert.Equal(
            PlaylistScheduler.BuildCycleOrder(8, shuffle: true, seed: 42),
            PlaylistScheduler.BuildCycleOrder(8, shuffle: true, seed: 42));
    }

    [Fact]
    public void BuildCycleOrder_Shuffle_DiffersForDifferentSeeds()
    {
        // With 8 items the chance two different seeds collide on the exact permutation is ~1/40320.
        Assert.NotEqual(
            PlaylistScheduler.BuildCycleOrder(8, shuffle: true, seed: 1),
            PlaylistScheduler.BuildCycleOrder(8, shuffle: true, seed: 2));
    }

    [Fact]
    public void BuildCycleOrder_Empty_ReturnsEmpty()
    {
        Assert.Empty(PlaylistScheduler.BuildCycleOrder(0, shuffle: true, seed: 1));
    }
}

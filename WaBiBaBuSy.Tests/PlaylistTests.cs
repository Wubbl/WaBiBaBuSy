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
                        AnimationSpeedPxPerSecond = 640,
                        DistributionMode = AnimationDistributionMode.Sequential,
                        Movement = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 640f },
                        Animation = new AnimationLayerConfig { AnimationPath = "fish.gif", TargetHeight = 300 },
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
        Assert.Equal(MovementType.Linear, restored.Items[0].Config.Movement.Type);
        Assert.Equal("fish.gif", restored.Items[0].Config.Animation.AnimationPath);
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
    public void ComputeLapMs_NonLinear_ReturnsZero()
    {
        foreach (var t in new[] { MovementType.Static, MovementType.Bounce, MovementType.SineWave,
                                  MovementType.Circular, MovementType.RandomWalk })
        {
            var mv = new MovementConfig { Type = t, SpeedPixelsPerSecond = 500f, Loop = true };
            Assert.Equal(0, PlaylistScheduler.ComputeLapMs(mv, 4000, 200));
        }
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
}

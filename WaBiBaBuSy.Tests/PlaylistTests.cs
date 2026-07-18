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
}

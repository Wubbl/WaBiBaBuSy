using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Playlist tab helpers: durations typed in seconds, drag-reorder targets, duplicating items.</summary>
public class PlaylistEditingTests
{
    [Theory]
    [InlineData("30", 30_000)]
    [InlineData(" 12.5 ", 12_500)]
    [InlineData("12,5", 12_500)]
    [InlineData("0.25", 250)]
    [InlineData("999999", 86_400_000)]
    public void ParseSecondsToMs_Valid(string text, int expectedMs)
        => Assert.Equal(expectedMs, PlaylistEditing.ParseSecondsToMs(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("NaN")]
    public void ParseSecondsToMs_BlankOrInvalid_IsNullMeaningDefault(string? text)
        => Assert.Null(PlaylistEditing.ParseSecondsToMs(text));

    [Theory]
    [InlineData(30_000, "30")]
    [InlineData(12_500, "12.5")]
    [InlineData(250, "0.25")]
    [InlineData(null, "")]
    [InlineData(0, "")]
    public void FormatMsAsSeconds(int? ms, string expected)
        => Assert.Equal(expected, PlaylistEditing.FormatMsAsSeconds(ms));

    [Theory]
    [InlineData(5, 0, 5, 4)]    // first row dropped after the last row
    [InlineData(5, 4, 0, 0)]    // last row dropped before the first
    [InlineData(5, 1, 4, 3)]    // down: the gap before row 4 becomes index 3 once row 1 is removed
    [InlineData(5, 3, 1, 1)]    // up
    [InlineData(5, 0, 99, 4)]   // gap index clamps to the end
    [InlineData(5, 2, -3, 0)]   // and to the start
    public void MoveTarget_Moves(int count, int from, int insertBefore, int expected)
        => Assert.Equal(expected, PlaylistEditing.MoveTarget(count, from, insertBefore));

    [Theory]
    [InlineData(5, 2, 2)]       // gap right before itself
    [InlineData(5, 2, 3)]       // gap right after itself
    [InlineData(5, -1, 0)]      // from out of range
    [InlineData(5, 5, 0)]
    [InlineData(0, 0, 0)]
    public void MoveTarget_NoMove_IsMinusOne(int count, int from, int insertBefore)
        => Assert.Equal(-1, PlaylistEditing.MoveTarget(count, from, insertBefore));

    [Fact]
    public void Duplicate_DeepCopiesConfig_AndSuffixesName()
    {
        var original = new PlaylistItem
        {
            Name = "Fish",
            DurationMs = 12_000,
            SnapToLap = true,
            Config = new CrossScreenConfig { SelectedMonitorIds = { "a" } },
        };
        original.Config.Animation.AnimationPath = "fish.gif";

        var copy = PlaylistEditing.Duplicate(original);
        copy.Config.Animation.AnimationPath = "shark.gif";
        copy.Config.SelectedMonitorIds.Add("b");

        Assert.Equal("Fish copy", copy.Name);
        Assert.Equal(12_000, copy.DurationMs);
        Assert.True(copy.SnapToLap);
        Assert.Equal("fish.gif", original.Config.Animation.AnimationPath);
        Assert.Equal(new[] { "a" }, original.Config.SelectedMonitorIds);
    }
}

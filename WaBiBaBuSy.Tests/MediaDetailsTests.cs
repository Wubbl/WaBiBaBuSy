using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Gallery thumbnail captions and tooltips: resolution, video length, GIF frames.
/// </summary>
public class MediaDetailsTests
{
    [Theory]
    [InlineData(0, "")]
    [InlineData(2400, "2.4 s")]
    [InlineData(12_900, "12 s")]
    [InlineData(75_000, "1:15")]
    [InlineData(3_725_000, "1:02:05")]
    public void FormatDuration(long ms, string expected) =>
        Assert.Equal(expected, MediaDetails.FormatDuration(ms));

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(40, 40)]
    [InlineData(5000, 1000)]
    public void GifDelayMatchesPlayer(int raw, int expected) =>
        Assert.Equal(expected, MediaDetails.NormalizeGifDelayMs(raw));

    [Fact]
    public void CaptionShowsResolutionOnlyForImages() =>
        Assert.Equal("2560×1440", MediaDetails.CaptionLine("Image", "2560x1440", 0, 0));

    [Fact]
    public void CaptionShowsFramesAndLengthForGifs() =>
        Assert.Equal("480×270 · 48 fr · 2.4 s", MediaDetails.CaptionLine("Gif", "480x270", 2400, 48));

    [Fact]
    public void CaptionShowsLengthForVideos() =>
        Assert.Equal("1920×1080 · 1:15", MediaDetails.CaptionLine("Video", "1920x1080", 75_000, 0));

    [Fact]
    public void CaptionDropsUnknownResolution() =>
        Assert.Equal("12 s", MediaDetails.CaptionLine("Video", "Unknown", 12_000, 0));

    [Fact]
    public void ToolTipForGifListsFramesLoopAndRate()
    {
        var tip = MediaDetails.ToolTip("cat.gif", "Gif", "480x270", "1.2 MB", 2400, 48, 0, @"C:\a\cat.gif");
        var lines = tip.Split(System.Environment.NewLine);
        Assert.Equal(["cat.gif", "GIF · 480×270 · 1.2 MB", "48 frames · 2.4 s loop · ≈20 fps", @"C:\a\cat.gif"], lines);
    }

    [Fact]
    public void ToolTipForVideoListsLengthRateAndFrames()
    {
        var tip = MediaDetails.ToolTip("clip.mp4", "Video", "1920x1080", "40.0 MB", 75_000, 2250, 30, "");
        var lines = tip.Split(System.Environment.NewLine);
        Assert.Equal(["clip.mp4", "Video · 1920×1080 · 40.0 MB", "1:15 · 30 fps · 2,250 frames"], lines);
    }
}

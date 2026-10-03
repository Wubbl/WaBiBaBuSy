using System.Text.Json;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ShowEventModelTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wbbs-events-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
    }

    private static ShowEvent Full() => new()
    {
        EventId = "e1", Type = ShowEventType.Traversal, T0Utc = 1_700_000_000_123, DurationMs = 9000, Seed = -42,
        ColorHex = "#7FDBFF", TargetOrder = 3, TargetCenterX = 5760.5f, ImagePath = @"C:\logo.png",
        SpriteAspect = 2.5f, HeightFraction = 0.4f, Reverse = true, Text = "Round 3",
    };

    private static void AssertSame(ShowEvent a, ShowEvent b)
    {
        Assert.Equal(a.EventId, b.EventId); Assert.Equal(a.Type, b.Type); Assert.Equal(a.T0Utc, b.T0Utc);
        Assert.Equal(a.DurationMs, b.DurationMs); Assert.Equal(a.Seed, b.Seed); Assert.Equal(a.ColorHex, b.ColorHex);
        Assert.Equal(a.TargetOrder, b.TargetOrder); Assert.Equal(a.TargetCenterX, b.TargetCenterX);
        Assert.Equal(a.ImagePath, b.ImagePath); Assert.Equal(a.SpriteAspect, b.SpriteAspect);
        Assert.Equal(a.HeightFraction, b.HeightFraction); Assert.Equal(a.Reverse, b.Reverse); Assert.Equal(a.Text, b.Text);
    }

    [Fact]
    public void SystemTextJson_RoundTrip_KeepsEveryField() =>
        AssertSame(Full(), JsonSerializer.Deserialize<ShowEvent>(JsonSerializer.Serialize(Full()))!);

    [Fact]
    public void SystemTextJson_WritesTheTypeAsAString() =>
        Assert.Contains("\"Traversal\"", JsonSerializer.Serialize(Full()));

    [Fact]
    public void Newtonsoft_RoundTrip_KeepsEveryField() =>   // the player IPC serializes with Newtonsoft
        AssertSame(Full(), Newtonsoft.Json.JsonConvert.DeserializeObject<ShowEvent>(Newtonsoft.Json.JsonConvert.SerializeObject(Full()))!);

    [Fact]
    public void Clone_IsIndependent()
    {
        var a = Full();
        var b = a.Clone();
        b.T0Utc = 1; b.ImagePath = null;
        Assert.Equal(1_700_000_000_123, a.T0Utc);
        Assert.Equal(@"C:\logo.png", a.ImagePath);
    }

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var s = new ShowEventSettings();
        Assert.Equal(8000, s.SpotlightDurationMs);
        Assert.Equal(5000, s.CelebrationDurationMs);
        Assert.Equal(3000, s.BlackoutDurationMs);
        Assert.Equal("#000000", s.BlackoutColorHex);
        Assert.Equal(600f, s.TraversalSpeedPxPerSecond);
        Assert.Equal(0.5f, s.TraversalHeightFraction);
        Assert.Equal(400f, s.AnnounceSpeedPxPerSecond);
        Assert.True(s.HotkeysEnabled);
    }

    [Fact]
    public void Store_MissingFile_GivesDefaults() =>
        Assert.Equal(8000, ShowEventSettingsStore.Load(_path).SpotlightDurationMs);

    [Fact]
    public void Store_CorruptFile_GivesDefaults()
    {
        File.WriteAllText(_path, "{ not json");
        Assert.Equal(5000, ShowEventSettingsStore.Load(_path).CelebrationDurationMs);
    }

    [Fact]
    public void Store_SaveThenLoad_RoundTrips()
    {
        ShowEventSettingsStore.Save(new ShowEventSettings { TraversalImagePath = @"C:\club.png", LastTargetSeatId = "pc-07", SpotlightDurationMs = 12000 }, _path);
        var back = ShowEventSettingsStore.Load(_path);
        Assert.Equal(@"C:\club.png", back.TraversalImagePath);
        Assert.Equal("pc-07", back.LastTargetSeatId);
        Assert.Equal(12000, back.SpotlightDurationMs);
    }
}

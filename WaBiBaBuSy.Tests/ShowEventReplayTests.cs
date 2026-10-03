using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Core.Services.Show;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Resume / Resync replay of the server's running events to one client (final-review F1).</summary>
public class ShowEventReplayTests
{
    private static ShowEvent E(string id, long t0, ShowEventType type = ShowEventType.Spotlight) =>
        new() { EventId = id, Type = type, T0Utc = t0, DurationMs = 5000, Seed = 7 };

    [Fact]
    public void BuildReplayCommands_OneEventCommandPerEvent_T0InTheTimestamp()
    {
        int seq = 100;
        var commands = WallpaperSyncCoordinator.BuildReplayCommands(
            new (ShowEvent, ContentAssetRef?)[] { (E("a", 1_000), null), (E("b", 2_000), null) }, () => ++seq);

        Assert.Equal(2, commands.Count);
        Assert.All(commands, c => Assert.Equal(CommandType.Event, c.Type));
        Assert.Equal(new long[] { 1_000, 2_000 }, commands.Select(c => c.TimestampUtc));
        Assert.Equal(new[] { 101, 102 }, commands.Select(c => c.SequenceNumber));
        Assert.True(ShowEventCodec.TryFromCommand(commands[1], out var back));
        Assert.Equal("b", back.EventId);
    }

    [Fact]
    public void BuildReplayCommands_CarriesTheTraversalImage()
    {
        var asset = new ContentAssetRef { ContentId = "img_abc", Role = AssetRole.Event, OriginalPath = @"C:\x\img.png" };
        var commands = WallpaperSyncCoordinator.BuildReplayCommands(
            new (ShowEvent, ContentAssetRef?)[] { (E("t", 5, ShowEventType.Traversal), asset) }, () => 1);

        var cmd = Assert.Single(commands);
        Assert.Equal("img_abc", cmd.ContentId);
        var reference = Assert.Single(cmd.Params.Assets);
        Assert.Equal("img_abc", reference.ContentId);
        Assert.Equal(AssetRole.Event, reference.Role);
        Assert.Equal(@"C:\x\img.png", reference.OriginalPath);
    }

    [Fact]
    public void BuildReplayCommands_NoEvents_NoCommands() =>
        Assert.Empty(WallpaperSyncCoordinator.BuildReplayCommands(Array.Empty<(ShowEvent, ContentAssetRef?)>(), () => 1));
}

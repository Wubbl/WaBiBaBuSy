using Google.Protobuf;
using WaBiBaBuSy.Core.Services.Show;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ShowEventCodecTests
{
    private static ShowEvent Spot() => new()
    {
        EventId = "abc", Type = ShowEventType.Spotlight, T0Utc = 5_000_000, DurationMs = 8000, Seed = 99,
        TargetOrder = 4, TargetCenterX = 4321.5f, ColorHex = "#FF00FF",
    };

    [Fact]
    public void ProtoBytes_RoundTrip_KeepsTheEvent()
    {
        var bytes = ShowEventCodec.ToCommand(Spot(), sequenceNumber: 17).ToByteArray();
        var cmd = SyncCommand.Parser.ParseFrom(bytes);
        Assert.Equal(CommandType.Event, cmd.Type);
        Assert.Equal(17, cmd.SequenceNumber);
        Assert.Equal(5_000_000, cmd.TimestampUtc);
        Assert.True(ShowEventCodec.TryFromCommand(cmd, out var back));
        Assert.Equal("abc", back.EventId);
        Assert.Equal(ShowEventType.Spotlight, back.Type);
        Assert.Equal(4, back.TargetOrder);
        Assert.Equal(4321.5f, back.TargetCenterX);
        Assert.Equal(99, back.Seed);
        Assert.Equal("#FF00FF", back.ColorHex);
    }

    [Fact]
    public void CommandType_Event_IsTwelve() => Assert.Equal(12, (int)CommandType.Event);

    [Fact]
    public void TryFromCommand_TakesT0FromTheTimestamp_NotTheJson()
    {
        var cmd = ShowEventCodec.ToCommand(Spot(), 1);
        cmd.TimestampUtc -= 2500;            // what the client's clock-offset conversion does
        Assert.True(ShowEventCodec.TryFromCommand(cmd, out var back));
        Assert.Equal(5_000_000 - 2500, back.T0Utc);
    }

    [Fact]
    public void UnknownCommandType_StillParses()  // a newer server's command reaching an older client
    {
        var bytes = new SyncCommand { Type = (CommandType)99, TimestampUtc = 1 }.ToByteArray();
        Assert.Equal(99, (int)SyncCommand.Parser.ParseFrom(bytes).Type);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("""{ "EventId": "", "Type": "Spotlight" }""")]
    [InlineData("""{ "EventId": "x", "Type": "Fireworks" }""")]   // a type this build does not know
    public void TryFromCommand_RejectsBadPayloads(string json) =>
        Assert.False(ShowEventCodec.TryFromCommand(new SyncCommand { Type = CommandType.Event, EventJson = json }, out _));

    [Fact]
    public void TryFromCommand_RejectsOtherCommandTypes()
    {
        var cmd = ShowEventCodec.ToCommand(Spot(), 1);
        cmd.Type = CommandType.Play;
        Assert.False(ShowEventCodec.TryFromCommand(cmd, out _));
    }

    [Fact]
    public void ToCommand_CarriesTheAsset()
    {
        var asset = new ContentAssetRef { ContentId = "logo.png-0123456789abcdef", Role = AssetRole.Event, OriginalPath = @"C:\logo.png" };
        var cmd = ShowEventCodec.ToCommand(Spot(), 1, asset);
        Assert.Equal(asset.ContentId, cmd.ContentId);
        var refd = Assert.Single(cmd.Params.Assets);
        Assert.Equal("event", refd.Role);
        Assert.Equal(@"C:\logo.png", refd.OriginalPath);
    }
}

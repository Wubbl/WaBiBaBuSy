using System.Text.Json;
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ProbeModelsTests
{
    [Fact]
    public void RemoteProbeResult_Json_CarriesRepliesButNotCaptures()
    {
        // The result header travels as System.Text.Json; the PNG bytes go as separate gRPC chunks.
        var result = new RemoteProbeResult
        {
            ClientId = "c1",
            ProbeId = "p1",
            Replies = { new PlayerProbeReply { ProbeId = "p1", MonitorIndex = 1, CapturePath = "a.png" } },
            Captures = { [1] = new byte[] { 1, 2, 3 } },
        };

        var json = JsonSerializer.Serialize(result);
        var back = JsonSerializer.Deserialize<RemoteProbeResult>(json)!;

        Assert.DoesNotContain("Captures", json);
        Assert.Empty(back.Captures);
        Assert.Equal(1, Assert.Single(back.Replies).MonitorIndex);
    }
}

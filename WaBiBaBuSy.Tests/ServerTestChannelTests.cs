using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary><see cref="ServerTestChannel"/>: TEST_PROBE out over SyncStream, the result back over SubmitProbeResult.</summary>
public class ServerTestChannelTests : IDisposable
{
    private readonly SyncServiceHarness _h = new();
    private readonly ServerTestChannel _channel;

    public ServerTestChannelTests() => _channel = new ServerTestChannel(_h.Service);

    public void Dispose()
    {
        _channel.Dispose();
        _h.Dispose();
    }

    private static ProbeRequest Probe(string id) => new() { ProbeId = id, AtServerUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

    [Fact]
    public async Task Probe_ReturnsTheClientsUploadedResult()
    {
        _h.Connect("c1", command =>
        {
            if (command.Type == CommandType.TestProbe)
                _ = Task.Run(() => _h.SubmitProbeAsync("c1", command.Params.TestProbeId, "{\"RttMs\":7}"));
        });

        var result = await _channel.ProbeAsync("c1", Probe("p1"), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("c1", result!.ClientId);
        Assert.Equal("p1", result.ProbeId);
        Assert.Equal(7.0, result.RttMs);
    }

    [Fact]
    public async Task Probe_NoReply_ReturnsNull()
    {
        _h.Connect("c1", _ => { });
        Assert.Null(await _channel.ProbeAsync("c1", Probe("p1"), TimeSpan.FromMilliseconds(100), CancellationToken.None));
    }

    [Fact]
    public async Task Probe_Cancelled_ThrowsInsteadOfLookingLikeATimeout()
    {
        _h.Connect("c1", _ => { });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _channel.ProbeAsync("c1", Probe("p1"), TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task Probe_DuplicatePendingProbeId_IsRefused_AndTheFirstWaiterKeepsItsResult()
    {
        _h.Connect("c1", _ => { });

        var first = _channel.ProbeAsync("c1", Probe("p1"), TimeSpan.FromSeconds(3), CancellationToken.None);
        var second = await _channel.ProbeAsync("c1", Probe("p1"), TimeSpan.FromSeconds(3), CancellationToken.None);
        await _h.SubmitProbeAsync("c1", "p1");

        Assert.NotNull(second?.Error);
        var result = await first;
        Assert.NotNull(result);
        Assert.Null(result!.Error);
    }
}

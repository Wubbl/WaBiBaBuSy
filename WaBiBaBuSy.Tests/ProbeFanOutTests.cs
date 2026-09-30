using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ProbeFanOutTests
{
    private sealed class FakeTarget : ITestProbeTarget
    {
        public int MonitorIndex { get; init; }
        public Func<PlayerProbeRequest, PlayerProbeReply?> Answer { get; init; } = r => new PlayerProbeReply { ProbeId = r.ProbeId };
        public (bool Timecode, int Skew)? LastMode { get; private set; }
        public Task SetTestModeAsync(bool timecode, int clockSkewMs) { LastMode = (timecode, clockSkewMs); return Task.CompletedTask; }
        public Task<PlayerProbeReply?> ProbeAsync(PlayerProbeRequest request, TimeSpan timeout, CancellationToken ct) => Task.FromResult(Answer(request));
    }

    [Fact]
    public async Task ProbeAll_SetsMonitorIndex_AndTurnsSilenceAndExceptionsIntoErrors()
    {
        var targets = new ITestProbeTarget[]
        {
            new FakeTarget { MonitorIndex = 0 },
            new FakeTarget { MonitorIndex = 1, Answer = _ => null },
            new FakeTarget { MonitorIndex = 2, Answer = _ => throw new InvalidOperationException("pipe closed") },
        };

        var replies = await ProbeFanOut.ProbeAllAsync(targets, new PlayerProbeRequest { ProbeId = "p1" }, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(new[] { 0, 1, 2 }, replies.Select(r => r.MonitorIndex));
        Assert.All(replies, r => Assert.Equal("p1", r.ProbeId));
        Assert.Null(replies[0].Error);
        Assert.Equal("no reply from player", replies[1].Error);
        Assert.Equal("pipe closed", replies[2].Error);
    }

    [Fact]
    public async Task SetTestModeAll_ReachesEveryTarget()
    {
        var a = new FakeTarget();
        var b = new FakeTarget { MonitorIndex = 1 };
        await ProbeFanOut.SetTestModeAllAsync(new ITestProbeTarget[] { a, b }, timecode: true, clockSkewMs: 2000);
        Assert.Equal((true, 2000), a.LastMode);
        Assert.Equal((true, 2000), b.LastMode);
    }
}

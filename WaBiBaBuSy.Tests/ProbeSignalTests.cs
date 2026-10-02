using System.Text.Json;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>The host side of the player probe protocol: SIGNAL:PROBE parsing and waiting for the reply.</summary>
public class ProbeSignalTests
{
    // The host passes Newtonsoft; the parsing rules under test do not depend on the deserializer.
    private static PlayerProbeReply? Deserialize(string json) => JsonSerializer.Deserialize<PlayerProbeReply>(json);

    [Fact]
    public void ParseProbeSignal_WellFormedLine_ReturnsReply()
    {
        var reply = ProbeFanOut.ParseProbeSignal("SIGNAL:PROBE:{\"ProbeId\":\"p7\",\"RenderedElapsedMs\":1234}", Deserialize, out var problem);

        Assert.NotNull(reply);
        Assert.Equal("p7", reply!.ProbeId);
        Assert.Equal(1234, reply.RenderedElapsedMs);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("SIGNAL:PROBE:{\"ProbeId\":null}")]
    [InlineData("SIGNAL:PROBE:{\"ProbeId\":\"\"}")]
    [InlineData("SIGNAL:PROBE:{}")]
    [InlineData("SIGNAL:PROBE:null")]
    public void ParseProbeSignal_NoProbeId_ReturnsNullWithProblem(string line)
    {
        var reply = ProbeFanOut.ParseProbeSignal(line, Deserialize, out var problem);

        Assert.Null(reply);
        Assert.Contains("probe id", problem);
    }

    [Theory]
    [InlineData("SIGNAL:PROBE:{not json")]
    [InlineData("SIGNAL:PROBE:")]
    [InlineData("SIGNAL:PROBE:{\"ProbeId\":\"p1\",\"FrameIndex\":\"abc\"}")]
    public void ParseProbeSignal_MalformedPayload_NeverThrows(string line)
    {
        var reply = ProbeFanOut.ParseProbeSignal(line, Deserialize, out var problem);

        Assert.Null(reply);
        Assert.StartsWith("malformed", problem);
    }

    [Fact]
    public void ParseProbeSignal_AnyDeserializerException_IsCaught()
    {
        // Runs on the stderr event thread: not only JSON errors must stay inside.
        var reply = ProbeFanOut.ParseProbeSignal("SIGNAL:PROBE:{}", _ => throw new ArgumentNullException("key"), out var problem);

        Assert.Null(reply);
        Assert.StartsWith("malformed", problem);
    }

    [Fact]
    public void ParseProbeSignal_OtherLine_IsNotAProbe()
    {
        var reply = ProbeFanOut.ParseProbeSignal("SIGNAL:LAP_COMPLETE:3", Deserialize, out var problem);

        Assert.Null(reply);
        Assert.Equal("not a SIGNAL:PROBE line", problem);
    }

    [Fact]
    public async Task WaitForReply_ReplyInTime_ReturnsIt()
    {
        var tcs = new TaskCompletionSource<PlayerProbeReply>();
        var wait = ProbeFanOut.WaitForReplyAsync(tcs.Task, TimeSpan.FromSeconds(30), CancellationToken.None);
        tcs.SetResult(new PlayerProbeReply { ProbeId = "p1" });

        var reply = await wait;

        Assert.Equal("p1", reply!.ProbeId);
    }

    [Fact]
    public async Task WaitForReply_Timeout_ReturnsNull()
    {
        var tcs = new TaskCompletionSource<PlayerProbeReply>();

        var reply = await ProbeFanOut.WaitForReplyAsync(tcs.Task, TimeSpan.FromMilliseconds(20), CancellationToken.None);

        Assert.Null(reply);
    }

    [Fact]
    public async Task WaitForReply_Cancelled_ThrowsInsteadOfReturningNull()
    {
        var tcs = new TaskCompletionSource<PlayerProbeReply>();
        using var cts = new CancellationTokenSource();
        var wait = ProbeFanOut.WaitForReplyAsync(tcs.Task, TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task WaitForReply_AlreadyCancelled_Throws()
    {
        var tcs = new TaskCompletionSource<PlayerProbeReply>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProbeFanOut.WaitForReplyAsync(tcs.Task, TimeSpan.FromSeconds(30), new CancellationToken(canceled: true)));
    }
}

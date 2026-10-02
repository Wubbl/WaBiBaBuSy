using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Grpc.Services;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Server side of the automated test mode in <see cref="WallpaperSyncService"/>: remote log fetches
/// (FETCH_LOGS → SendClientLogs) and probe-result uploads (SubmitProbeResult).
/// </summary>
public class WallpaperSyncServiceTestModeTests : IDisposable
{
    private readonly SyncServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>A client that answers every FETCH_LOGS with its window ("from-to") after <paramref name="delayMs"/>.</summary>
    private void ConnectEchoingClient(string clientId, int delayMs = 50) =>
        _h.Connect(clientId, command =>
        {
            if (command.Type != CommandType.FetchLogs) return;
            var window = $"{command.Params.LogFromUtcMs}-{command.Params.LogToUtcMs}";
            _ = Task.Run(async () =>
            {
                await Task.Delay(delayMs);
                await _h.ReplyLogsAsync(clientId, window);
            });
        });

    [Fact]
    public async Task FetchClientLogs_ReturnsTheClientsReply()
    {
        ConnectEchoingClient("c1");

        var logs = await _h.Service.FetchClientLogsAsync("c1", 1, 2, TimeSpan.FromSeconds(5));

        Assert.Equal("1-2", logs?.LogContent);
    }

    [Fact]
    public async Task FetchClientLogs_NoCommandStream_ReturnsNullAtOnce()
    {
        var started = DateTime.UtcNow;
        Assert.Null(await _h.Service.FetchClientLogsAsync("nobody", 0, 0, TimeSpan.FromSeconds(10)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FetchClientLogs_NoReply_ReturnsNull_AndALaterFetchStillWorks()
    {
        bool answer = false;
        _h.Connect("c1", command =>
        {
            if (command.Type == CommandType.FetchLogs && answer)
                _ = Task.Run(() => _h.ReplyLogsAsync("c1", "late"));
        });

        Assert.Null(await _h.Service.FetchClientLogsAsync("c1", 0, 0, TimeSpan.FromMilliseconds(200)));
        answer = true;
        Assert.Equal("late", (await _h.Service.FetchClientLogsAsync("c1", 0, 0, TimeSpan.FromSeconds(5)))?.LogContent);
    }

    [Fact]
    public async Task FetchClientLogs_ConcurrentFetchesForOneClient_EachGetTheirOwnReply()
    {
        // "View logs" while a test run collects its windowed logs: neither caller may lose its reply.
        ConnectEchoingClient("c1");

        var viewLogs = _h.Service.FetchClientLogsAsync("c1", 0, 0, TimeSpan.FromSeconds(3));
        var testRun = _h.Service.FetchClientLogsAsync("c1", 1000, 2000, TimeSpan.FromSeconds(3));

        Assert.Equal("0-0", (await viewLogs)?.LogContent);
        Assert.Equal("1000-2000", (await testRun)?.LogContent);
    }

    [Fact]
    public async Task SubmitProbeResult_ThrowingSubscriber_DoesNotFailTheUpload_OrStarveOtherSubscribers()
    {
        _h.Connect("c1", _ => { });
        ProbeResultReceivedEventArgs? received = null;
        _h.Service.ProbeResultReceived += (_, _) => throw new InvalidOperationException("broken subscriber");
        _h.Service.ProbeResultReceived += (_, e) => received = e;

        var ack = await _h.SubmitProbeAsync("c1", "p1");

        Assert.True(ack.Success);
        Assert.Equal("p1", received?.ProbeId);
    }

    [Fact]
    public async Task SubmitProbeResult_FromAClientWithoutCommandStream_IsRejected()
    {
        bool raised = false;
        _h.Service.ProbeResultReceived += (_, _) => raised = true;

        var ack = await _h.SubmitProbeAsync("not-connected", "p1");

        Assert.False(ack.Success);
        Assert.False(raised);
    }

    [Fact]
    public async Task SubmitProbeResult_CapturesAboveTheCap_AreDropped_ButTheResultIsDelivered()
    {
        _h.Connect("c1", _ => { });
        _h.Service.MaxProbeCaptureBytes = 1000;
        ProbeResultReceivedEventArgs? received = null;
        _h.Service.ProbeResultReceived += (_, e) => received = e;

        var ack = await _h.SubmitProbeAsync("c1", "p1", "{\"x\":1}",
            new Dictionary<int, byte[]> { [0] = new byte[600], [1] = new byte[600] }, chunkSize: 100);

        Assert.False(ack.Success);
        Assert.NotNull(received);
        Assert.Equal("{\"x\":1}", received!.ResultJson);
        Assert.Empty(received.Captures);
    }

    [Fact]
    public async Task SubmitProbeResult_CapturesWithinTheCap_AreDelivered()
    {
        _h.Connect("c1", _ => { });
        ProbeResultReceivedEventArgs? received = null;
        _h.Service.ProbeResultReceived += (_, e) => received = e;

        var png = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var ack = await _h.SubmitProbeAsync("c1", "p1", captures: new Dictionary<int, byte[]> { [1] = png }, chunkSize: 300);

        Assert.True(ack.Success);
        Assert.Equal(png, received!.Captures[1]);
    }
}

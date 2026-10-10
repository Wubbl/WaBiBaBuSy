using WaBiBaBuSy.Grpc;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Which stream's end removes a client: only the one still registered for it.</summary>
public class SyncStreamLifecycleTests : IDisposable
{
    private readonly SyncServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private bool Listed(string id) => _h.Service.GetConnectedClients().Any(c => c.ClientId == id);

    private static async Task<bool> Eventually(Func<bool> condition, int ms = 2000)
    {
        for (int waited = 0; waited < ms; waited += 20)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task OldStreamEndingAfterReconnect_KeepsTheClient()
    {
        Assert.True((await _h.RegisterAsync("c1")).Success);
        var oldStream = _h.Connect("c1", _ => { });
        await _h.RegisterAsync("c1");               // the client reconnected
        _h.Connect("c1", _ => { });                  // with a new stream

        oldStream.Cancel();                          // the dead link is noticed only now
        await Task.Delay(300);

        Assert.True(Listed("c1"));
    }

    [Fact]
    public async Task CurrentStreamEnding_RemovesTheClient()
    {
        Assert.True((await _h.RegisterAsync("c1")).Success);
        var stream = _h.Connect("c1", _ => { });
        Assert.True(Listed("c1"));

        stream.Cancel();

        Assert.True(await Eventually(() => !Listed("c1")));
    }
}

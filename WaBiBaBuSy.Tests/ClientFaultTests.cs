using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>TEST_FAULT and session recovery, end to end over loopback gRPC (real server host, real client).</summary>
public class ClientFaultTests : IAsyncLifetime
{
    private readonly int _port = FreePort();
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"wbbs-fault-{Guid.NewGuid():N}");
    private WallpaperSyncServerHost _host = null!;
    private WallpaperSyncClient? _client;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public async Task InitializeAsync()
    {
        _host = new WallpaperSyncServerHost(NullLogger<WallpaperSyncServerHost>.Instance,
            new ServerConfiguration { Port = _port, EnableAutoDiscovery = false, ContentDirectory = Path.Combine(_cacheDir, "server") })
        { LoopbackOnly = true };
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        try { if (_client != null) await _client.DisconnectAsync(); } catch { /* best effort */ }
        await _host.StopAsync();
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> ConnectAsync(bool allowTestRuns = true)
    {
        _client = new WallpaperSyncClient(NullLogger<WallpaperSyncClient>.Instance,
            new ClientConfiguration { CacheDirectory = Path.Combine(_cacheDir, "client"), HeartbeatIntervalSeconds = 1, AllowTestRuns = allowTestRuns });
        Assert.True(await _client.ConnectAsync("127.0.0.1", _port));
        Assert.True(await Eventually(() => Registered(_client.ClientId!), 3000));
        return _client.ClientId!;
    }

    private bool Registered(string id) => _host.SyncService!.GetConnectedClients().Any(c => c.ClientId == id);

    private long LastHeartbeat(string id) => _host.SyncService!.GetConnectedClients().First(c => c.ClientId == id).LastHeartbeat;

    /// <summary>Retries while the client's command stream is not open yet (it starts right after registration).</summary>
    private async Task<bool> SendFaultAsync(string id, string kind, int ms)
    {
        for (int waited = 0; waited < 3000; waited += 50)
        {
            if (await _host.SyncService!.SendCommandToClientAsync(id, new SyncCommand
            {
                Type = CommandType.TestFault,
                TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Params = new SyncParameters { TestFaultKind = kind, TestFaultMs = ms },
            })) return true;
            await Task.Delay(50);
        }
        return false;
    }

    private static async Task<bool> Eventually(Func<bool> condition, int ms)
    {
        for (int waited = 0; waited < ms; waited += 50)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task SweptClient_ReRegistersOnItsNextHeartbeat()
    {
        var id = await ConnectAsync();

        _host.SyncService!.RemoveClient(id);   // what the dead-client sweep does; the client's connection stays open

        Assert.True(await Eventually(() => Registered(id), 8000), "the swept client never re-registered");
    }

    [Fact]
    public async Task Drop_LosesTheLink_ThenReconnectsAfterTheFault()
    {
        var id = await ConnectAsync();
        var sent = DateTimeOffset.UtcNow;

        Assert.True(await SendFaultAsync(id, TestFaults.Drop, 1500));

        Assert.True(await Eventually(() => !Registered(id), 3000), "the server never lost the client");
        Assert.True(await Eventually(() => Registered(id), 10000), "the client never came back");
        Assert.True((DateTimeOffset.UtcNow - sent).TotalMilliseconds >= 1400, "the client came back before the fault ended");
    }

    [Fact]
    public async Task Silent_StopsHeartbeats_ThenResumes()
    {
        var id = await ConnectAsync();
        Assert.True(await SendFaultAsync(id, TestFaults.Silent, 2500));
        await Task.Delay(300);
        long during = LastHeartbeat(id);

        await Task.Delay(1800);                    // ≥ one heartbeat interval inside the silence
        Assert.Equal(during, LastHeartbeat(id));

        Assert.True(await Eventually(() => LastHeartbeat(id) != during, 4000), "heartbeats never resumed");
        Assert.True(Registered(id));
    }

    [Fact]
    public async Task TestRunsDisabled_FaultIsIgnored()
    {
        var id = await ConnectAsync(allowTestRuns: false);

        Assert.True(await SendFaultAsync(id, TestFaults.Drop, 3000));
        await Task.Delay(2000);

        Assert.True(Registered(id));
        Assert.True(_client!.IsConnected);
    }
}

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>The test control listener is optional: it must never keep the gRPC server from starting.</summary>
public class WallpaperSyncServerHostTests
{
    private sealed class FakeControl : ITestRunControl
    {
        public TestRunStatus Status => TestRunStatus.Idle;
        public bool IsRunning => false;
        public Task<TestRunReport> StartAsync(string scenarioPath) => Task.FromResult(new TestRunReport());
        public bool Cancel() => false;
        public IReadOnlyList<string> ListRuns() => Array.Empty<string>();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    private static WallpaperSyncServerHost Host(int testPort) =>
        new(NullLogger<WallpaperSyncServerHost>.Instance, new ServerConfiguration { Port = FreePort(), EnableAutoDiscovery = false })
        {
            LoopbackOnly = true,
            TestControl = new FakeControl(),
            TestControlPort = testPort,
        };

    [Fact]
    public async Task Start_WhenTheTestControlPortIsTaken_StartsTheServerWithoutIt()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var host = Host(((IPEndPoint)blocker.LocalEndpoint).Port);
        try
        {
            await host.StartAsync();
            Assert.True(host.IsRunning);
            Assert.NotNull(host.SyncService);
            Assert.False(host.IsTestControlListening);
        }
        finally
        {
            await host.StopAsync();
            blocker.Stop();
        }
    }

    [Theory]
    [InlineData(70000)]
    [InlineData(-1)]
    public async Task Start_WithAnOutOfRangeTestControlPort_StartsTheServerWithoutIt(int port)
    {
        var host = Host(port);
        try
        {
            await host.StartAsync();
            Assert.True(host.IsRunning);
            Assert.False(host.IsTestControlListening);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Start_WithAFreeTestControlPort_ServesTheControlApi()
    {
        int testPort = FreePort();
        var host = Host(testPort);
        try
        {
            await host.StartAsync();
            Assert.True(host.IsTestControlListening);
            using var http = new HttpClient();
            var response = await http.GetAsync($"http://127.0.0.1:{testPort}/test/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}

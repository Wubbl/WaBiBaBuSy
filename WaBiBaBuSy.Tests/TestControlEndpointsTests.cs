using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class TestControlEndpointsTests : IAsyncLifetime
{
    private sealed class FakeControl : ITestRunControl
    {
        public int Starts;
        public TestRunStatus Status => TestRunStatus.Idle;
        public bool IsRunning => false;
        public Task<TestRunReport> StartAsync(string scenarioPath)
        {
            Starts++;
            return Task.FromResult(new TestRunReport());
        }
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

    private readonly FakeControl _control = new();
    private readonly int _grpcLikePort = FreePort();
    private readonly int _testPort = FreePort();
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, _grpcLikePort, lo => lo.Protocols = HttpProtocols.Http2);   // stand-in for the gRPC listener
            o.Listen(IPAddress.Loopback, _testPort, lo => lo.Protocols = HttpProtocols.Http1);
        });
        _app = builder.Build();
        TestControlEndpoints.Map(_app, _control, _testPort);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync();
    }

    [Fact]
    public async Task Status_OnTheTestPort_Returns200()
    {
        using var http = new HttpClient();
        var response = await http.GetAsync($"http://127.0.0.1:{_testPort}/test/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Status_OnTheOtherListener_WithForgedHost_Returns404()
    {
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_grpcLikePort}/test/status")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Host = $"127.0.0.1:{_testPort}";
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Status_WithForeignHost_Returns404()
    {
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_testPort}/test/status");
        request.Headers.Host = $"evil.example:{_testPort}";
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Run_WithTextPlainBody_Returns415_AndDoesNotStart()
    {
        using var http = new HttpClient();
        var response = await http.PostAsync($"http://127.0.0.1:{_testPort}/test/run",
            new StringContent("""{"scenario":"C:/x.json"}""", System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, _control.Starts);
    }

    [Fact]
    public async Task Run_WithUncPath_Returns400_AndDoesNotStart()
    {
        using var http = new HttpClient();
        var response = await http.PostAsJsonAsync($"http://127.0.0.1:{_testPort}/test/run", new { scenario = new string('\\', 2) + "attacker" + '\\' + "share" + '\\' + "x.json" });
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _control.Starts);
    }

    [Fact]
    public async Task Run_WithLocalPath_Returns202()
    {
        using var http = new HttpClient();
        var response = await http.PostAsJsonAsync($"http://127.0.0.1:{_testPort}/test/run", new { scenario = @"C:\scenarios\x.json" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, _control.Starts);
    }
}

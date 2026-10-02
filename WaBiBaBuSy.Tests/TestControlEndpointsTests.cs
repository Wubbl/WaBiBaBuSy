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

    /// <summary>Two distinct free ports: both probes are held open together, so they cannot return the same port.</summary>
    private static (int, int) FreePorts()
    {
        var a = new TcpListener(IPAddress.Loopback, 0);
        var b = new TcpListener(IPAddress.Loopback, 0);
        a.Start();
        b.Start();
        try { return (((IPEndPoint)a.LocalEndpoint).Port, ((IPEndPoint)b.LocalEndpoint).Port); }
        finally { a.Stop(); b.Stop(); }
    }

    private readonly FakeControl _control = new();
    private int _grpcLikePort;
    private int _testPort;
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        // Another process may grab a probed port before Kestrel binds it: retry with fresh ports.
        for (int attempt = 1; ; attempt++)
        {
            (_grpcLikePort, _testPort) = FreePorts();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Loopback, _grpcLikePort, lo => lo.Protocols = HttpProtocols.Http2);   // stand-in for the gRPC listener
                o.Listen(IPAddress.Loopback, _testPort, lo => lo.Protocols = HttpProtocols.Http1);
            });
            var app = builder.Build();
            TestControlEndpoints.Map(app, _control, _testPort);
            try
            {
                await app.StartAsync();
                _app = app;
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await app.DisposeAsync();
            }
        }
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

    [Theory]
    [InlineData("POST", "text/plain", """{"scenario":"C:/x.json"}""")]   // routing's 415 endpoint
    [InlineData("POST", "application/json", "{not json")]                 // 400 from body binding
    [InlineData("PUT", "application/json", "{}")]                         // routing's 405 endpoint
    public async Task Run_OnTheOtherListener_WithForgedHost_Returns404_BeforeBinding(string method, string contentType, string body)
    {
        using var http = new HttpClient();
        var request = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{_grpcLikePort}/test/run")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
        };
        request.Headers.Host = $"127.0.0.1:{_testPort}";
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _control.Starts);
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

    [Theory]
    [InlineData(@"\??\C:\scenarios\x.json")]
    [InlineData(@"\\?\C:\scenarios\x.json")]
    [InlineData(@"\\.\C:\scenarios\x.json")]
    [InlineData("//?/C:/scenarios/x.json")]
    [InlineData("//./C:/scenarios/x.json")]
    public async Task Run_WithDevicePath_Returns400_AsNotALocalFile(string path)
    {
        using var http = new HttpClient();
        var response = await http.PostAsJsonAsync($"http://127.0.0.1:{_testPort}/test/run", new { scenario = path });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        Assert.Contains("must be a local file", text);
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

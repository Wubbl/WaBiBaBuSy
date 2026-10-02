using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Grpc.Services;
using WaBiBaBuSy.Models.Configuration;

namespace WaBiBaBuSy.Core.Services.Networking;

/// <summary>
/// Host for the WaBiBaBuSy gRPC server using ASP.NET Core
/// </summary>
public class WallpaperSyncServerHost : IDisposable
{
    private readonly ILogger<WallpaperSyncServerHost> _logger;
    private readonly ServerConfiguration _configuration;
    private readonly MdnsServerService? _mdnsService;
    private IHost? _host;
    private bool _isRunning;

    public bool IsRunning => _isRunning;
    public WallpaperSyncService? SyncService { get; private set; }

    /// <summary>
    /// Test control API; mapped only when set and <see cref="TestControlPort"/> is a valid port. Optional:
    /// when its port cannot be bound the gRPC server starts without it (see <see cref="IsTestControlListening"/>).
    /// </summary>
    public ITestRunControl? TestControl { get; set; }
    public int TestControlPort { get; set; }

    /// <summary>True while the server runs with the test control API bound.</summary>
    public bool IsTestControlListening { get; private set; }

    /// <summary>Bind the gRPC listener to 127.0.0.1 instead of every interface (tests; default false).</summary>
    public bool LoopbackOnly { get; init; }

    public event EventHandler<ServerStatusChangedEventArgs>? ServerStatusChanged;

    public WallpaperSyncServerHost(
        ILogger<WallpaperSyncServerHost> logger,
        ServerConfiguration configuration,
        MdnsServerService? mdnsService = null)
    {
        _logger = logger;
        _configuration = configuration;
        _mdnsService = mdnsService;
    }

    /// <summary>
    /// Start the gRPC server
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning)
        {
            _logger.LogWarning("Server is already running");
            return;
        }

        try
        {
            _logger.LogInformation("Starting WaBiBaBuSy gRPC server on port {Port}", _configuration.Port);

            bool withTestControl = TestControl != null && TestControlPort != 0;
            if (withTestControl && TestControlPort is < 1 or > IPEndPoint.MaxPort)
            {
                _logger.LogWarning("Test control port {Port} is out of range; starting without the test control API", TestControlPort);
                withTestControl = false;
            }

            // Start the host and await the bind — a port-in-use or firewall failure
            // must surface here instead of vanishing into an unobserved task.
            var app = BuildApp(withTestControl);
            _host = app;
            try
            {
                await app.StartAsync();
            }
            catch (IOException ex) when (withTestControl)
            {
                // The test control listener is optional (usually its port is taken). Retry without it:
                // if the gRPC port was the problem, this start throws and the server start fails as before.
                _host = null;
                await app.DisposeAsync();
                app = BuildApp(withTestControl: false);
                _host = app;
                await app.StartAsync();
                withTestControl = false;
                _logger.LogWarning("Test control API could not bind 127.0.0.1:{Port} ({Error}); the server runs without it",
                    TestControlPort, ex.Message);
            }
            IsTestControlListening = withTestControl;
            if (withTestControl)
                _logger.LogInformation("Test control API on http://127.0.0.1:{Port}", TestControlPort);

            _isRunning = true;

            // Start mDNS advertising if enabled and available
            if (_configuration.EnableAutoDiscovery && _mdnsService != null)
            {
                _logger.LogInformation("Starting mDNS advertising");
                _mdnsService.StartAdvertising();
            }

            _logger.LogInformation("WaBiBaBuSy gRPC server started successfully on port {Port}", _configuration.Port);
            ServerStatusChanged?.Invoke(this, new ServerStatusChangedEventArgs(true, _configuration.Port));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start gRPC server");
            _isRunning = false;
            IsTestControlListening = false;
            ServerStatusChanged?.Invoke(this, new ServerStatusChangedEventArgs(false, _configuration.Port));
            throw;
        }
    }

    private WebApplication BuildApp(bool withTestControl)
    {
        var builder = WebApplication.CreateBuilder();

        // Configure Kestrel to use HTTP/2 (required for gRPC)
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (LoopbackOnly)
                options.Listen(IPAddress.Loopback, _configuration.Port, lo => lo.Protocols = HttpProtocols.Http2);
            else
                options.ListenAnyIP(_configuration.Port, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                });
            if (withTestControl)
                options.Listen(IPAddress.Loopback, TestControlPort, lo => lo.Protocols = HttpProtocols.Http1);
        });

        // Add services
        builder.Services.AddGrpc();

        // Register server configuration as singleton
        builder.Services.AddSingleton(_configuration);

        // Register our gRPC service as singleton so we can access it
        builder.Services.AddSingleton<WallpaperSyncService>();

        // Add logging
        builder.Services.AddLogging(logging =>
        {
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Information);
        });

        var app = builder.Build();

        // Get the service instance
        SyncService = app.Services.GetRequiredService<WallpaperSyncService>();

        // Map gRPC service
        app.MapGrpcService<WallpaperSyncService>();

        if (withTestControl)
            TestControlEndpoints.Map(app, TestControl!, TestControlPort);

        // Add health check endpoint
        app.MapGet("/", () => "WaBiBaBuSy gRPC Server is running. Use a gRPC client to connect.");

        // The caller awaits StartAsync so a port-in-use or firewall failure surfaces there
        // instead of vanishing into an unobserved task.
        return app;
    }

    /// <summary>
    /// Stop the gRPC server
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Stopping WaBiBaBuSy gRPC server");

            // Stop mDNS advertising
            if (_configuration.EnableAutoDiscovery && _mdnsService != null)
            {
                _logger.LogInformation("Stopping mDNS advertising");
                _mdnsService.StopAdvertising();
            }

            // Stop the host
            if (_host != null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
                _host = null;
            }

            _isRunning = false;
            IsTestControlListening = false;
            SyncService = null;

            _logger.LogInformation("WaBiBaBuSy gRPC server stopped");
            ServerStatusChanged?.Invoke(this, new ServerStatusChangedEventArgs(false, _configuration.Port));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping gRPC server");
        }
    }

    public void Dispose()
    {
        // Run on the thread pool so blocking here cannot deadlock a UI
        // SynchronizationContext waiting on its own continuations.
        Task.Run(() => StopAsync()).Wait(TimeSpan.FromSeconds(10));
    }
}

public class ServerStatusChangedEventArgs : EventArgs
{
    public bool IsRunning { get; }
    public int Port { get; }

    public ServerStatusChangedEventArgs(bool isRunning, int port)
    {
        IsRunning = isRunning;
        Port = port;
    }
}

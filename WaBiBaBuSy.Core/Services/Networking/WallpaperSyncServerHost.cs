using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

            var builder = WebApplication.CreateBuilder();

            // Configure Kestrel to use HTTP/2 (required for gRPC)
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(_configuration.Port, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                });
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

            // Add health check endpoint
            app.MapGet("/", () => "WaBiBaBuSy gRPC Server is running. Use a gRPC client to connect.");

            // Start the host in background
            _host = app;
            _ = _host.RunAsync();

            // Wait a moment to ensure server started
            await Task.Delay(500);

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
            ServerStatusChanged?.Invoke(this, new ServerStatusChangedEventArgs(false, _configuration.Port));
            throw;
        }
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
        StopAsync().Wait();
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

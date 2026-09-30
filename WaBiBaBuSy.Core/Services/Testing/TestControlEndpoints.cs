using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>
/// HTTP/1.1 endpoints on 127.0.0.1:&lt;port&gt; (never the gRPC port): POST /test/run {"scenario": path},
/// GET /test/status, DELETE /test/run, GET /test/runs.
/// </summary>
/// <remarks>
/// The app also serves the AnyIP gRPC listener, so the routes are gated on the real connection
/// (local port + loopback peer), not on the client-supplied Host header. <c>RequireHost</c> stays as a
/// DNS-rebinding defence for browsers.
/// </remarks>
public static class TestControlEndpoints
{
    /// <summary>Body of POST /test/run; binding it as a typed parameter makes ASP.NET require application/json (415 otherwise).</summary>
    internal sealed record RunRequest(string? Scenario);

    public static void Map(WebApplication app, ITestRunControl control, int port)
    {
        var hosts = new[] { $"127.0.0.1:{port}", $"localhost:{port}" };
        var json = TestReportWriter.JsonOptions;

        var group = app.MapGroup("/test").RequireHost(hosts);
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var connection = ctx.HttpContext.Connection;
            if (connection.LocalPort != port || connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip))
                return Results.NotFound();
            return await next(ctx);
        });

        group.MapPost("/run", (RunRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Scenario)) return Results.BadRequest(new { error = "scenario is required" });
            try
            {
                var full = Path.GetFullPath(body.Scenario);
                // A UNC path would make this machine authenticate to a remote SMB share.
                if (full.StartsWith(@"\\", StringComparison.Ordinal) || new Uri(full).IsUnc)
                    return Results.BadRequest(new { error = "scenario must be a local file" });
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
            {
                return Results.BadRequest(new { error = $"invalid scenario path: {ex.Message}" });
            }
            try
            {
                _ = control.StartAsync(body.Scenario);
                return Results.Json(new { started = true, scenario = body.Scenario }, json, statusCode: StatusCodes.Status202Accepted);
            }
            catch (ScenarioException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        group.MapGet("/status", () => Results.Json(control.Status, json));
        group.MapDelete("/run", () => control.Cancel()
            ? Results.Json(new { cancelled = true }, json)
            : Results.NotFound(new { error = "no active run" }));
        group.MapGet("/runs", () => Results.Json(control.ListRuns(), json));
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>
/// HTTP/1.1 endpoints on 127.0.0.1:&lt;port&gt; (never the gRPC port): POST /test/run {"scenario": path},
/// GET /test/status, DELETE /test/run, GET /test/runs.
/// </summary>
public static class TestControlEndpoints
{
    private sealed record RunRequest(string? Scenario);

    public static void Map(WebApplication app, ITestRunControl control, int port)
    {
        var hosts = new[] { $"127.0.0.1:{port}", $"localhost:{port}" };
        var json = TestReportWriter.JsonOptions;

        app.MapPost("/test/run", async (HttpRequest request) =>
        {
            RunRequest? body;
            try { body = await JsonSerializer.DeserializeAsync<RunRequest>(request.Body, json); }
            catch (JsonException ex) { return Results.BadRequest(new { error = $"invalid JSON: {ex.Message}" }); }
            if (string.IsNullOrWhiteSpace(body?.Scenario)) return Results.BadRequest(new { error = "scenario is required" });
            try
            {
                _ = control.StartAsync(body.Scenario);
                return Results.Json(new { started = true, scenario = body.Scenario }, json, statusCode: StatusCodes.Status202Accepted);
            }
            catch (ScenarioException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireHost(hosts);

        app.MapGet("/test/status", () => Results.Json(control.Status, json)).RequireHost(hosts);
        app.MapDelete("/test/run", () => control.Cancel()
            ? Results.Json(new { cancelled = true }, json)
            : Results.NotFound(new { error = "no active run" })).RequireHost(hosts);
        app.MapGet("/test/runs", () => Results.Json(control.ListRuns(), json)).RequireHost(hosts);
    }
}

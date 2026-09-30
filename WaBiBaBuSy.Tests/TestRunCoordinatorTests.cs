using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class TestRunCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-coord-{Guid.NewGuid():N}");

    public TestRunCoordinatorTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "wait.json"), """{ "name": "wait", "steps": [ { "type": "wait", "ms": 2000 } ] }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class EmptyHost : ITestHost
    {
        public Task<IReadOnlyList<TestNodeInfo>> GetNodesAsync() => Task.FromResult<IReadOnlyList<TestNodeInfo>>(Array.Empty<TestNodeInfo>());
        public Task<SceneStartInfo> PlaySceneAsync(CrossScreenConfig s, IReadOnlyList<string> t, CancellationToken ct) => Task.FromResult(new SceneStartInfo());
        public Task StopAllAsync() => Task.CompletedTask;
        public CrossScreenConfig? CurrentScene => null;
        public Task RestoreSceneAsync(CrossScreenConfig? scene) => Task.CompletedTask;
        public Task<bool> PrefetchAsync(IReadOnlyList<CrossScreenConfig> s, TimeSpan t, CancellationToken ct) => Task.FromResult(true);
        public IReadOnlyList<ITestProbeTarget> LocalProbeTargets() => Array.Empty<ITestProbeTarget>();
        public void ReportProgress(string text) { }
    }

    private sealed class NoTransport : ITestTransport
    {
        public Task<bool> SendTestModeAsync(string c, bool t, int s) => Task.FromResult(true);
        public Task<RemoteProbeResult?> ProbeAsync(string c, ProbeRequest r, TimeSpan t, CancellationToken ct) => Task.FromResult<RemoteProbeResult?>(null);
        public Task<string?> FetchLogsAsync(string c, long f, long t, TimeSpan to) => Task.FromResult<string?>(null);
    }

    private TestRunCoordinator Coordinator(bool withHost = true) => new(
        () => withHost ? new EmptyHost() : null, () => new NoTransport(), NullLoggerFactory.Instance,
        new TestRunnerOptions { ResultsRoot = Path.Combine(_dir, "results"), LocalLogDirectory = _dir, MinLeadMs = 20 });

    [Fact]
    public async Task SecondStart_WhileRunning_IsRejected_CancelEndsTheRun()
    {
        var c = Coordinator();
        var run = c.StartAsync(Path.Combine(_dir, "wait.json"));
        Assert.True(c.IsRunning);
        Assert.Throws<InvalidOperationException>(() => { _ = c.StartAsync(Path.Combine(_dir, "wait.json")); });

        Assert.True(c.Cancel());
        var report = await run;
        Assert.True(report.Aborted);
        Assert.False(c.IsRunning);
        Assert.Single(c.ListRuns());
    }

    [Fact]
    public async Task WaitForIdle_AfterCancel_ReturnsOnceThePartialReportIsWritten()
    {
        var c = Coordinator();
        Assert.True(await c.WaitForIdleAsync(TimeSpan.FromSeconds(1)));   // nothing running

        _ = c.StartAsync(Path.Combine(_dir, "wait.json"));
        c.Cancel();
        Assert.True(await c.WaitForIdleAsync(TimeSpan.FromSeconds(10)));
        Assert.False(c.IsRunning);
        Assert.True(File.Exists(Path.Combine(Assert.Single(c.ListRuns()), "report.json")));
    }

    [Fact]
    public async Task WaitForIdle_TimesOut_WhileTheRunContinues()
    {
        var c = Coordinator();
        _ = c.StartAsync(Path.Combine(_dir, "wait.json"));
        Assert.False(await c.WaitForIdleAsync(TimeSpan.FromMilliseconds(50)));
        c.Cancel();
        Assert.True(await c.WaitForIdleAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void BadScenario_ThrowsBeforeStarting() =>
        Assert.Throws<ScenarioException>(() => { _ = Coordinator().StartAsync(Path.Combine(_dir, "missing.json")); });

    [Fact]
    public void NoHost_Throws() =>
        Assert.Throws<InvalidOperationException>(() => { _ = Coordinator(withHost: false).StartAsync(Path.Combine(_dir, "wait.json")); });
}

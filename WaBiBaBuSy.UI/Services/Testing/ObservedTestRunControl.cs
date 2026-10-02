using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.UI.Services.Testing;

/// <summary>
/// <see cref="ITestRunControl"/> that reports every run it starts, whichever path started it (menu, CLI,
/// control API), so the control panel can show it. <paramref name="started"/> may be called on any thread.
/// </summary>
public sealed class ObservedTestRunControl(ITestRunControl inner, Action<Task<TestRunReport>> started) : ITestRunControl
{
    private volatile Task<TestRunReport>? _lastRun;

    /// <summary>The run started last, while it is still going; null otherwise.</summary>
    public Task<TestRunReport>? ActiveRun => _lastRun is { IsCompleted: false } run ? run : null;

    public TestRunStatus Status => inner.Status;
    public bool IsRunning => inner.IsRunning;

    public Task<TestRunReport> StartAsync(string scenarioPath)
    {
        var run = inner.StartAsync(scenarioPath);   // throws before anything plays; nothing to report then
        _lastRun = run;
        try { started(run); }
        catch (Exception ex) { Console.Error.WriteLine($"[Test] Could not report the started run: {ex.Message}"); }
        return run;
    }

    public bool Cancel() => inner.Cancel();
    public IReadOnlyList<string> ListRuns() => inner.ListRuns();
}

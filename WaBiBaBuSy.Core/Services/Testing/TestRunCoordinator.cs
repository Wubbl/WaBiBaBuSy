using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>Start / cancel / inspect test runs; shared by the menu, the CLI switch and the control API.</summary>
public interface ITestRunControl
{
    TestRunStatus Status { get; }
    bool IsRunning { get; }
    /// <summary>Validates and starts a run. Throws <see cref="ScenarioException"/> or <see cref="InvalidOperationException"/> before anything plays.</summary>
    Task<TestRunReport> StartAsync(string scenarioPath);
    bool Cancel();
    /// <summary>Result folders, newest first.</summary>
    IReadOnlyList<string> ListRuns();
}

/// <summary>At most one run at a time.</summary>
public sealed class TestRunCoordinator : ITestRunControl
{
    private readonly Func<ITestHost?> _hostFactory;
    private readonly Func<ITestTransport?> _transportFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TestRunnerOptions _options;
    private readonly object _lock = new();
    private TestRunner? _runner;
    private CancellationTokenSource? _cts;
    private Task<TestRunReport>? _run;

    public TestRunCoordinator(Func<ITestHost?> hostFactory, Func<ITestTransport?> transportFactory, ILoggerFactory loggerFactory, TestRunnerOptions? options = null)
    {
        _hostFactory = hostFactory;
        _transportFactory = transportFactory;
        _loggerFactory = loggerFactory;
        _options = options ?? new TestRunnerOptions();
    }

    public event EventHandler<TestRunReport>? RunFinished;

    public TestRunStatus Status => _runner?.Status ?? TestRunStatus.Idle;

    public bool IsRunning
    {
        get { lock (_lock) return _run is { IsCompleted: false }; }
    }

    public Task<TestRunReport> StartAsync(string scenarioPath)
    {
        var scenario = ScenarioLoader.Load(scenarioPath);   // throws ScenarioException
        lock (_lock)
        {
            if (_run is { IsCompleted: false }) throw new InvalidOperationException("a test run is already active");
            var host = _hostFactory() ?? throw new InvalidOperationException("open the server control panel first");
            var transport = _transportFactory() ?? throw new InvalidOperationException("start the server first");

            _cts = new CancellationTokenSource();
            _runner = new TestRunner(host, transport, _loggerFactory.CreateLogger<TestRunner>(), _options);
            var runner = _runner;
            var token = _cts.Token;
            _run = Task.Run(async () =>
            {
                try
                {
                    var report = await runner.RunAsync(scenario, Path.GetFullPath(scenarioPath), token);
                    RunFinished?.Invoke(this, report);
                    return report;
                }
                finally
                {
                    (transport as IDisposable)?.Dispose();
                }
            });
            return _run;
        }
    }

    public bool Cancel()
    {
        lock (_lock)
        {
            if (_run is not { IsCompleted: false } || _cts == null) return false;
            _cts.Cancel();
            return true;
        }
    }

    public IReadOnlyList<string> ListRuns() =>
        Directory.Exists(_options.ResultsRoot)
            ? new DirectoryInfo(_options.ResultsRoot).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Select(d => d.FullName).ToList()
            : Array.Empty<string>();
}

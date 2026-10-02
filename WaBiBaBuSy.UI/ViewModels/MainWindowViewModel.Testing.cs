using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using AppVersionInfo = WaBiBaBuSy.Common.Version.VersionInfo;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>Automated test mode (2026-09-30 design): hooks for <see cref="Services.Testing.TestHostAdapter"/> and the menu.</summary>
public partial class MainWindowViewModel
{
    [ObservableProperty] private string? _testRunStatusText;
    [ObservableProperty] private bool _isTestRunning;

    private bool? _isTestModeEnabled;

    /// <summary>Server setting "Enable test mode"; hides the menu item when off. Read once, refreshed by <see cref="RefreshTestModeEnabled"/>.</summary>
    public bool IsTestModeEnabled => _isTestModeEnabled ??= WaBiBaBuSy.Models.Configuration.ConfigurationManager.LoadServerConfiguration().EnableTestMode;

    /// <summary>Re-read the setting after Settings was saved (UI thread).</summary>
    internal void RefreshTestModeEnabled()
    {
        bool enabled = WaBiBaBuSy.Models.Configuration.ConfigurationManager.LoadServerConfiguration().EnableTestMode;
        if (_isTestModeEnabled == enabled) return;
        _isTestModeEnabled = enabled;
        OnPropertyChanged(nameof(IsTestModeEnabled));
    }

    internal Task<IReadOnlyList<TestNodeInfo>> TestNodesAsync() => Dispatcher.UIThread.InvokeAsync<IReadOnlyList<TestNodeInfo>>(() =>
    {
        var versions = _service.GetConnectedClients().GroupBy(c => c.ClientId).ToDictionary(g => g.Key, g => g.First().AppVersion);
        return Clients.GroupBy(c => c.ClientId).Select(g => g.First()).OrderBy(c => c.Order).Select((c, seat) =>
        {
            bool local = IsLocalMonitor(c.ClientId);
            return new TestNodeInfo
            {
                NodeId = c.ClientId,
                Name = local ? $"server #{Math.Max(0, c.MonitorIndex)}" : c.Hostname,
                IsLocal = local,
                Connected = local || c.IsConnected,
                MonitorIndex = Math.Max(0, c.MonitorIndex),
                SeatOrder = seat,
                Width = c.MonitorWidth,
                Height = c.MonitorHeight,
                RefreshHz = c.MonitorRefreshHz,
                ClockOffsetMs = c.DriftMs,
                RttMs = c.RttMs,
                AppVersion = local ? AppVersionInfo.AppVersion : versions.GetValueOrDefault(c.ClientId, string.Empty),
            };
        }).ToList();
    }).GetTask();

    /// <summary>
    /// Same teardown + start as ▶ Play (PlayDraftAsync), without touching the editor draft. Holds the
    /// <see cref="IsStartingScene"/> guard so a Play / show click cannot interleave across the awaits.
    /// </summary>
    internal Task<SceneStartInfo> PlaySceneForTestAsync(CrossScreenConfig config, IReadOnlyList<string> targetNodeIds) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (IsStartingScene)
                throw new InvalidOperationException("a scene is already starting — try again when the current start has finished");
            IsStartingScene = true;
            try
            {
                config.SelectedMonitorIds = targetNodeIds.ToList();
                var previousStart = ActiveSharedStartMs;
                if (_playlistOrchestrator?.IsRunning == true)
                {
                    await StopPlaylistAsync();
                    await ClearNodesAsync(Clients.Select(c => c.ClientId).Distinct().ToList());
                }
                if (IsCrossScreenRunning) await StopCrossScreen();

                _crossScreenConfig = config;
                HasAnimationConfig = true;
                await StartCrossScreen();
                // A fresh start always moves the shared epoch; the reference check alone would pass when
                // the very same config object was left running after a failed stop.
                if (!ReferenceEquals(ActiveScene, config) || ActiveSharedStartMs == previousStart)
                    throw new InvalidOperationException("the scene did not start (see the server log)");

                return new SceneStartInfo
                {
                    SharedStartServerUtcMs = ActiveSharedStartMs,
                    StartLeadMs = ActiveStartLeadMs,
                    Layout = ActiveLayout,
                    PerMonitor = config.DistributionMode == AnimationDistributionMode.Simultaneous,
                    EffectiveMovement = ActiveEffectiveMovement ?? config.Movement,
                };
            }
            finally
            {
                IsStartingScene = false;
            }
        });

    internal Task StopAllForTestAsync() => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        if (IsStartingScene)
            throw new InvalidOperationException("a scene is already starting — try again when the current start has finished");
        IsStartingScene = true;
        try
        {
            // Unconditional: it stops a running show first (which never sets IsCrossScreenRunning) and
            // returns early when no scene runs.
            await StopCrossScreen();
            await ClearNodesAsync(Clients.Select(c => c.ClientId).Distinct().ToList());
        }
        finally
        {
            IsStartingScene = false;
        }
    });

    /// <summary>
    /// What to restore after a run: the running scene, or null when nothing plays. Read by the runner off the
    /// UI thread, so both properties are read together on it. A running show is not restored (see the design doc).
    /// </summary>
    internal CrossScreenConfig? SceneForTestRestore => Dispatcher.UIThread.Invoke(() => IsCrossScreenRunning ? ActiveScene : null);

    internal async Task<bool> PrefetchForTestAsync(IReadOnlyList<CrossScreenConfig> scenes, TimeSpan timeout)
    {
        await PrefetchShowAssetsAsync(scenes);
        return await WaitUntilRemotesPrefetchedAsync(timeout);
    }

    internal IReadOnlyList<ITestProbeTarget> LocalProbeTargetsForTest() =>
        _d2dCompositionServices.Values.Where(s => s.IsRunning).Cast<ITestProbeTarget>().ToList();

    // Set before the picker opens: IsTestRunning only turns true once a run started, so a double-click
    // would otherwise open a second picker.
    private bool _isPickingTestScenario;

    [RelayCommand]
    private async Task RunTestSuite()
    {
        if (_storageProvider == null || IsTestRunning || _isPickingTestScenario) return;
        _isPickingTestScenario = true;
        string? path;
        try
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "TestScenarios");
            var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Run test scenario",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Test scenario") { Patterns = new[] { "*.json" } } },
                SuggestedStartLocation = Directory.Exists(bundled) ? await _storageProvider.TryGetFolderFromPathAsync(bundled) : null,
            });
            path = files.FirstOrDefault()?.TryGetLocalPath();
        }
        finally
        {
            _isPickingTestScenario = false;
        }
        if (path != null) await StartTestRunAsync(path);
    }

    [RelayCommand]
    private void CancelTestRun() => TestCoordinator?.Cancel();

    [RelayCommand]
    private void OpenTestResults()
    {
        var dir = TestCoordinator?.Status.ResultsDirectory;
        var html = dir != null ? Path.Combine(dir, "report.html") : null;
        if (html == null || !File.Exists(html)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = html, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No browser associated with .html, or the shell refused: say so instead of crashing the command.
            Console.Error.WriteLine($"Could not open {html}: {ex.Message}");
            TestRunStatusText = $"Could not open the report: {ex.Message.Split('\n')[0]} ({html})";
        }
    }

    /// <summary>Set by the tray; null only when the window is built without one.</summary>
    public ITestRunControl? TestCoordinator { get; set; }

    private async Task StartTestRunAsync(string path)
    {
        if (TestCoordinator == null) return;
        Task<TestRunReport> run;
        try
        {
            run = TestCoordinator.StartAsync(path);   // validates synchronously
        }
        catch (Exception ex)
        {
            // ScenarioException / InvalidOperationException are expected; anything else (IO, a bug) must not
            // escape the menu command and crash the app either.
            if (ex is not (ScenarioException or InvalidOperationException)) Console.Error.WriteLine($"Test run could not start: {ex}");
            TestRunStatusText = $"Test not started: {ex.Message.Split('\n')[0]}";
            return;
        }
        await TrackTestRunAsync(run);
    }

    private Task<TestRunReport>? _trackedTestRun;

    /// <summary>
    /// Show a run in the menu (Cancel button, status line) until it ends, whichever path started it: the
    /// menu, the CLI or the control API (via the tray's <see cref="Services.Testing.ObservedTestRunControl"/>).
    /// UI thread; the same run reported twice is tracked once.
    /// </summary>
    internal Task TrackTestRunAsync(Task<TestRunReport> run)
    {
        if (ReferenceEquals(_trackedTestRun, run)) return Task.CompletedTask;
        _trackedTestRun = run;
        return TrackAsync();

        async Task TrackAsync()
        {
            IsTestRunning = true;
            string text;
            try
            {
                var report = await run;
                text = $"Test: {report.Verdict.ToString().ToLowerInvariant()}{(report.Aborted ? " (aborted)" : "")} \u2014 Open last results";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Test run failed: {ex}");
                text = $"Test run failed: {ex.Message.Split('\n')[0]}";
            }
            // A newer run may already be tracked (only possible once this one has finished): leave it alone.
            if (!ReferenceEquals(_trackedTestRun, run)) return;
            TestRunStatusText = text;
            IsTestRunning = false;
        }
    }
}

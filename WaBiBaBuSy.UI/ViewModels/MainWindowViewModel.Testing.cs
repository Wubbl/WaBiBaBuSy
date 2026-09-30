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

    /// <summary>Server setting "Enable test mode"; hides the menu item when off.</summary>
    public bool IsTestModeEnabled => WaBiBaBuSy.Models.Configuration.ConfigurationManager.LoadServerConfiguration().EnableTestMode;

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

    /// <summary>What to restore after a run: the running scene, or null when nothing plays.</summary>
    internal CrossScreenConfig? SceneForTestRestore => IsCrossScreenRunning ? ActiveScene : null;

    internal async Task<bool> PrefetchForTestAsync(IReadOnlyList<CrossScreenConfig> scenes, TimeSpan timeout)
    {
        await PrefetchShowAssetsAsync(scenes);
        return await WaitUntilRemotesPrefetchedAsync(timeout);
    }

    internal IReadOnlyList<ITestProbeTarget> LocalProbeTargetsForTest() =>
        _d2dCompositionServices.Values.Where(s => s.IsRunning).Cast<ITestProbeTarget>().ToList();

    [RelayCommand]
    private async Task RunTestSuite()
    {
        if (_storageProvider == null || IsTestRunning) return;
        var bundled = Path.Combine(AppContext.BaseDirectory, "TestScenarios");
        var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Run test scenario",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Test scenario") { Patterns = new[] { "*.json" } } },
            SuggestedStartLocation = Directory.Exists(bundled) ? await _storageProvider.TryGetFolderFromPathAsync(bundled) : null,
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) await StartTestRunAsync(path);
    }

    [RelayCommand]
    private void CancelTestRun() => TestCoordinator?.Cancel();

    [RelayCommand]
    private void OpenTestResults()
    {
        var dir = TestCoordinator?.Status.ResultsDirectory;
        var html = dir != null ? Path.Combine(dir, "report.html") : null;
        if (html != null && File.Exists(html))
            Process.Start(new ProcessStartInfo { FileName = html, UseShellExecute = true });
    }

    /// <summary>Set by the tray; null only when the window is built without one.</summary>
    public TestRunCoordinator? TestCoordinator { get; set; }

    private async Task StartTestRunAsync(string path)
    {
        if (TestCoordinator == null) return;
        try
        {
            IsTestRunning = true;
            var report = await TestCoordinator.StartAsync(path);
            TestRunStatusText = $"Test: {report.Verdict.ToString().ToLowerInvariant()}{(report.Aborted ? " (aborted)" : "")} — Open last results";
        }
        catch (Exception ex) when (ex is ScenarioException or InvalidOperationException)
        {
            TestRunStatusText = $"Test not started: {ex.Message.Split('\n')[0]}";
        }
        finally
        {
            IsTestRunning = false;
        }
    }
}

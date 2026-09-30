using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Services.Testing;

/// <summary><see cref="ITestHost"/> over the server control panel's view model.</summary>
public sealed class TestHostAdapter : ITestHost
{
    private readonly MainWindowViewModel _vm;

    public TestHostAdapter(MainWindowViewModel vm) => _vm = vm;

    public Task<IReadOnlyList<TestNodeInfo>> GetNodesAsync() => _vm.TestNodesAsync();

    public Task<SceneStartInfo> PlaySceneAsync(CrossScreenConfig scene, IReadOnlyList<string> targetNodeIds, CancellationToken ct) =>
        _vm.PlaySceneForTestAsync(scene, targetNodeIds);

    public Task StopAllAsync() => _vm.StopAllForTestAsync();

    public CrossScreenConfig? CurrentScene => _vm.SceneForTestRestore;

    public Task RestoreSceneAsync(CrossScreenConfig? scene) =>
        scene == null ? _vm.StopAllForTestAsync() : _vm.PlaySceneForTestAsync(scene, scene.SelectedMonitorIds.ToList());

    public Task<bool> PrefetchAsync(IReadOnlyList<CrossScreenConfig> scenes, TimeSpan timeout, CancellationToken ct) =>
        _vm.PrefetchForTestAsync(scenes, timeout);

    public IReadOnlyList<ITestProbeTarget> LocalProbeTargets() => _vm.LocalProbeTargetsForTest();

    public void ReportProgress(string text) => Dispatcher.UIThread.Post(() => _vm.TestRunStatusText = text);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.Services.Testing;

/// <summary>
/// <see cref="ITestHost"/> over the server's app-lifetime playback host, so a run keeps working when the
/// control panel is closed (and reopened) while it runs.
/// </summary>
public sealed class TestHostAdapter : ITestHost
{
    private readonly ServerPlaybackHost _host;

    public TestHostAdapter(ServerPlaybackHost host) => _host = host;

    public Task<IReadOnlyList<TestNodeInfo>> GetNodesAsync() => _host.TestNodesAsync();

    public Task<SceneStartInfo> PlaySceneAsync(CrossScreenConfig scene, IReadOnlyList<string> targetNodeIds, CancellationToken ct) =>
        _host.PlaySceneForTestAsync(scene, targetNodeIds);

    public Task<SceneStartInfo> SwitchSceneAsync(CrossScreenConfig scene, IReadOnlyList<string> targetNodeIds, CancellationToken ct,
        string? transition = null) =>
        _host.PlaySceneForTestAsync(scene, targetNodeIds, switchScene: true, SceneTransition.Parse(transition));

    public Task StopAllAsync() => _host.StopAllForTestAsync();

    public TestRestorePoint CaptureRestorePoint() => _host.CaptureTestRestorePoint();

    public Task RestoreAsync(TestRestorePoint restorePoint) =>
        restorePoint.Show is { } show ? _host.ResumeShowForTestAsync(show)
        : restorePoint.Scene is { } scene ? _host.PlaySceneForTestAsync(scene, scene.SelectedMonitorIds.ToList())
        : _host.StopAllForTestAsync();

    public Task<bool> PrefetchAsync(IReadOnlyList<CrossScreenConfig> scenes, TimeSpan timeout, CancellationToken ct) =>
        _host.PrefetchForTestAsync(scenes, timeout);

    public IReadOnlyList<ITestProbeTarget> LocalProbeTargets() => _host.LocalProbeTargets();

    public Task FireShowEventAsync(ShowEvent evt, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() => _host.FireEventForTestAsync(evt));

    public void ReportProgress(string text) => _host.ReportTestProgress(text);
}

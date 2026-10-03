using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>What the runner needs from the server app (implemented by the UI; faked in tests).</summary>
public interface ITestHost
{
    /// <summary>Every node in seat order: the server's monitors and the remote clients, connected or not.</summary>
    Task<IReadOnlyList<TestNodeInfo>> GetNodesAsync();

    /// <summary>Play <paramref name="scene"/> on the node ids (empty = all), like ▶ Play; returns once local players started.</summary>
    Task<SceneStartInfo> PlaySceneAsync(CrossScreenConfig scene, IReadOnlyList<string> targetNodeIds, CancellationToken ct);

    Task StopAllAsync();

    /// <summary>What plays now (a scene, a running show, or nothing); captured before the run, restored after it.</summary>
    TestRestorePoint CaptureRestorePoint();

    /// <summary>
    /// Bring back what <paramref name="restorePoint"/> captured: the show at its item (like ▶ on that playlist),
    /// else the scene on its nodes, else stop everything.
    /// </summary>
    Task RestoreAsync(TestRestorePoint restorePoint);

    /// <summary>Cache every asset of the scenes on all remote nodes; true when all report cached in time.</summary>
    Task<bool> PrefetchAsync(IReadOnlyList<CrossScreenConfig> scenes, TimeSpan timeout, CancellationToken ct);

    /// <summary>The server's own running players (one per local monitor that plays).</summary>
    IReadOnlyList<ITestProbeTarget> LocalProbeTargets();

    /// <summary>Fire a prepared show event (T0 set) on every node, like the Events strip.</summary>
    Task FireShowEventAsync(ShowEvent evt, CancellationToken ct);

    /// <summary>One-line progress for the toolbar.</summary>
    void ReportProgress(string text);
}

/// <summary>What played before a test run. At most one of <see cref="Show"/> / <see cref="Scene"/> is set in practice; a show wins.</summary>
public sealed record TestRestorePoint
{
    /// <summary>Nothing was playing: restoring stops everything.</summary>
    public static TestRestorePoint Nothing { get; } = new();

    /// <summary>The manual scene that was running, or null.</summary>
    public CrossScreenConfig? Scene { get; init; }

    /// <summary>The playlist show that was running and where it was, or null.</summary>
    public PlaylistResumePoint? Show { get; init; }
}

/// <summary>What a started scene means for the checks.</summary>
public sealed class SceneStartInfo
{
    public long SharedStartServerUtcMs { get; set; }
    public int StartLeadMs { get; set; }
    /// <summary>Seat layout of the run (Sequential); null when unknown.</summary>
    public SeatMapLayoutResult? Layout { get; set; }
    /// <summary>Simultaneous mode: every node's own monitor is its canvas.</summary>
    public bool PerMonitor { get; set; }
    /// <summary>Movement after cm→px resolution (what the players received).</summary>
    public MovementConfig EffectiveMovement { get; set; } = new();
}

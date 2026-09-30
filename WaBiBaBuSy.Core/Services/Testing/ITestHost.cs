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

    /// <summary>What was playing before the run (restored afterwards); null = nothing.</summary>
    CrossScreenConfig? CurrentScene { get; }

    /// <summary>Play <paramref name="scene"/> on all nodes again, or stop everything when null.</summary>
    Task RestoreSceneAsync(CrossScreenConfig? scene);

    /// <summary>Cache every asset of the scenes on all remote nodes; true when all report cached in time.</summary>
    Task<bool> PrefetchAsync(IReadOnlyList<CrossScreenConfig> scenes, TimeSpan timeout, CancellationToken ct);

    /// <summary>The server's own running players (one per local monitor that plays).</summary>
    IReadOnlyList<ITestProbeTarget> LocalProbeTargets();

    /// <summary>One-line progress for the toolbar.</summary>
    void ReportProgress(string text);
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

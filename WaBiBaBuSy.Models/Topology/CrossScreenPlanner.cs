using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Topology;

/// <summary>A node a scene plays on, with the size and DPI the apply path uses for it.</summary>
public sealed record PlannedNode(RoomNode Node, int WidthPx, int HeightPx, float PixelsPerCm);

/// <summary>What a cross-screen scene plays on and with which canvas, before any player starts.</summary>
public sealed class CrossScreenPlan
{
    /// <summary>This machine's monitors, in start order (selection order, else chain order).</summary>
    public IReadOnlyList<PlannedNode> Local { get; init; } = Array.Empty<PlannedNode>();
    /// <summary>Remote nodes, in the order the room lists them.</summary>
    public IReadOnlyList<PlannedNode> Remote { get; init; } = Array.Empty<PlannedNode>();
    /// <summary>Seat-map layout of every target (chain order).</summary>
    public SeatMapLayoutResult Layout { get; init; } = new();
    /// <summary>Movement after cm→px resolution (what the players receive).</summary>
    public MovementConfig EffectiveMovement { get; init; } = new();
    /// <summary>Animation after cm→px resolution.</summary>
    public AnimationLayerConfig EffectiveAnimation { get; init; } = new();
    /// <summary>Simultaneous mode: every monitor is its own canvas.</summary>
    public bool PerMonitor { get; init; }
    /// <summary>Worst round trip of the remote targets (0 without remotes); sizes the start lead.</summary>
    public double MaxRemoteRttMs { get; init; }
    /// <summary>No node matched: nothing to play.</summary>
    public bool IsEmpty => Local.Count == 0 && Remote.Count == 0;
}

/// <summary>
/// The pure first half of applying a cross-screen scene: targets (the scene's selection, else every node),
/// local/remote split, local start order, per-node size and DPI, the seat-map layout and the cm→px resolution.
/// </summary>
public static class CrossScreenPlanner
{
    /// <param name="nodes">The room's nodes.</param>
    /// <param name="referencePixelsPerCm">Pixels per cm of the server's primary monitor (physical canvas).</param>
    /// <param name="localMonitorPixelsPerCm">Pixels per cm of this machine's monitors, by monitor index.</param>
    public static CrossScreenPlan Plan(CrossScreenConfig config, IReadOnlyList<RoomNode> nodes, SeatMap seatMap,
        float referencePixelsPerCm, IReadOnlyList<float> localMonitorPixelsPerCm)
    {
        var selected = new HashSet<string>(config.SelectedMonitorIds);
        var targets = selected.Count > 0 ? nodes.Where(n => selected.Contains(n.Id)).ToList() : nodes.ToList();

        // Selection: start order = position in SelectedMonitorIds (the user's order); otherwise chain order.
        var localNodes = targets.Where(n => n.IsLocal);
        if (selected.Count > 0)
        {
            var rank = new Dictionary<string, int>();
            for (int i = 0; i < config.SelectedMonitorIds.Count; i++) rank.TryAdd(config.SelectedMonitorIds[i], i);
            localNodes = localNodes.OrderBy(n => rank.GetValueOrDefault(n.Id, int.MaxValue));
        }
        else
        {
            localNodes = localNodes.OrderBy(n => n.Order);
        }

        // Remotes assume the first local monitor's DPI when they reported none.
        float fallbackPpcm = localMonitorPixelsPerCm.Count > 0 ? localMonitorPixelsPerCm[0] : 0f;
        PlannedNode Planned(RoomNode n) => new(
            n,
            n.WidthPx > 0 ? n.WidthPx : 1920,
            n.HeightPx > 0 ? n.HeightPx : 1080,
            n.Id.StartsWith(NodeIds.LocalMachinePrefix, StringComparison.Ordinal)
                ? (n.MonitorIndex >= 0 && n.MonitorIndex < localMonitorPixelsPerCm.Count ? localMonitorPixelsPerCm[n.MonitorIndex] : fallbackPpcm)
                : (n.PixelsPerCm > 0f ? n.PixelsPerCm : fallbackPpcm));

        var local = localNodes.Select(Planned).ToList();
        var remote = targets.Where(n => !n.IsLocal).Select(Planned).ToList();
        if (local.Count == 0 && remote.Count == 0)
            return new CrossScreenPlan { EffectiveMovement = config.Movement, EffectiveAnimation = config.Animation };

        var layoutInputs = local.Concat(remote).OrderBy(p => p.Node.Order).Select(p => new LayoutNodeInput
        {
            Id = p.Node.Id,
            WidthPx = p.WidthPx,
            HeightPx = p.HeightPx,
            GapBeforeCm = p.Node.GapBeforeCm,
            PixelsPerCm = p.PixelsPerCm
        }).ToList();
        var layout = SeatMapLayoutBuilder.Build(seatMap, layoutInputs, referencePixelsPerCm);

        // Physical canvas: cm sizes/speeds become canvas px once, here; players only ever see pixels.
        var (movement, animation) = PhysicalUnits.ResolveForCanvas(config, layout.RefPixelsPerCm);

        return new CrossScreenPlan
        {
            Local = local,
            Remote = remote,
            Layout = layout,
            EffectiveMovement = movement,
            EffectiveAnimation = animation,
            PerMonitor = config.DistributionMode == AnimationDistributionMode.Simultaneous,
            MaxRemoteRttMs = remote.Count > 0 ? remote.Max(p => p.Node.RttMs) : 0.0
        };
    }
}

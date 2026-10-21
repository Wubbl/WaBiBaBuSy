using System.Text.RegularExpressions;
using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.Models.Show;

/// <summary>BiBaBo seats are machines: a dual-monitor desk is one seat (design §12).</summary>
public static partial class BombSeats
{
    /// <summary>The machine of the server's (or a standalone machine's) own monitors.</summary>
    public const string ThisMachine = "this-machine";

    [GeneratedRegex("_MONITOR_\\d+$")]
    private static partial Regex MonitorSuffix();

    /// <summary>"c1_MONITOR_2" → "c1"; local monitors → <see cref="ThisMachine"/>.</summary>
    public static string MachineOf(string nodeId) =>
        NodeIds.IsLocalMonitor(nodeId) ? ThisMachine : MonitorSuffix().Replace(nodeId, "");

    /// <summary>
    /// One node per free target machine (its first in traversal order), in traversal order.
    /// <paramref name="groupByMachine"/> false: every monitor is a seat (test runs on one machine).
    /// </summary>
    public static IReadOnlyList<NodeLayout> Candidates(SeatMapLayoutResult layout, IReadOnlyCollection<string>? targetNodeIds, Func<string, bool> isBusy,
        bool groupByMachine = true) =>
        layout.Nodes
            .Where(n => (targetNodeIds == null || targetNodeIds.Contains(n.Id)) && !isBusy(n.Id))
            .GroupBy(n => groupByMachine ? MachineOf(n.Id) : n.Id)
            .Select(g => g.OrderBy(n => n.Order).First())
            .OrderBy(n => n.Order)
            .ToList();
}

using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>The node list a test run sees (<see cref="ITestHost.GetNodesAsync"/>), built from the room's nodes.</summary>
public static class TestNodeList
{
    /// <param name="nodes">The room's nodes; an id listed twice counts once (its first entry).</param>
    /// <param name="remoteVersions">App version per remote client id.</param>
    /// <param name="localAppVersion">This server's app version (its own monitors).</param>
    public static IReadOnlyList<TestNodeInfo> Build(IReadOnlyList<RoomNode> nodes, IReadOnlyDictionary<string, string> remoteVersions, string localAppVersion) =>
        nodes.GroupBy(n => n.Id).Select(g => g.First()).OrderBy(n => n.Order).Select((n, seat) =>
        {
            bool local = n.IsLocal;
            return new TestNodeInfo
            {
                NodeId = n.Id,
                Name = local ? $"server #{Math.Max(0, n.MonitorIndex)}" : n.Hostname,
                IsLocal = local,
                Connected = local || n.IsConnected,
                MonitorIndex = Math.Max(0, n.MonitorIndex),
                SeatOrder = seat,
                Width = n.WidthPx,
                Height = n.HeightPx,
                RefreshHz = n.RefreshHz,
                ClockOffsetMs = n.ClockOffsetMs,
                RttMs = n.RttMs,
                AppVersion = local ? localAppVersion : remoteVersions.GetValueOrDefault(NodeIds.MachineOf(n.Id), string.Empty),
            };
        }).ToList();
}

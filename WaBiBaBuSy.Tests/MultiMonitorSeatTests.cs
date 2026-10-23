using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Remote multi-monitor clients: their seats ({client}_MONITOR_n) reach the client's stream, a client may share only one
/// monitor, and the server may switch seats off.
/// </summary>
public class MultiMonitorSeatTests
{
    [Theory]
    [InlineData("c1_MONITOR_1", "c1")]
    [InlineData("c1", "c1")]
    [InlineData("SERVER_LOCALHOST_MONITOR_0", "SERVER_LOCALHOST_MONITOR_0")]
    [InlineData("LOCAL_MACHINE_MONITOR_2", "LOCAL_MACHINE_MONITOR_2")]
    [InlineData("odd_MONITOR_x", "odd_MONITOR_x")]
    public void MachineOf_StripsTheSeatSuffixOfRemoteClientsOnly(string nodeId, string machine) =>
        Assert.Equal(machine, NodeIds.MachineOf(nodeId));

    private static ScreenConfiguration TwoMonitors()
    {
        var all = new ScreenConfiguration { MonitorCount = 2, TotalWidth = 4480, TotalHeight = 1440 };
        all.Monitors.Add(new MonitorInfo { Index = 0, X = 0, Y = 0, Width = 2560, Height = 1440 });
        all.Monitors.Add(new MonitorInfo { Index = 1, X = 2560, Y = 0, Width = 1920, Height = 1080 });
        return all;
    }

    [Fact]
    public void SharedMonitorsOnly_KeepsTheOneMonitorWithItsOwnIndex()
    {
        var only2nd = WallpaperSyncClient.SharedMonitorsOnly(TwoMonitors(), sharedMonitorIndex: 1);
        Assert.Equal(1, only2nd.MonitorCount);
        Assert.Equal(1, Assert.Single(only2nd.Monitors).Index);
        Assert.Equal(1920, only2nd.TotalWidth);

        Assert.Equal(2, WallpaperSyncClient.SharedMonitorsOnly(TwoMonitors(), -1).MonitorCount);
        Assert.Equal(2, WallpaperSyncClient.SharedMonitorsOnly(TwoMonitors(), 5).MonitorCount);   // unplugged: share all
    }

    [Fact]
    public void SingleMonitorSeat_RendersOnTheMonitorTheClientShares()
    {
        var client = new ConnectedClient { ClientId = "c1", ScreenConfig = WallpaperSyncClient.SharedMonitorsOnly(TwoMonitors(), 1) };
        Assert.Equal(1, RoomNodeSources.ToRoomNode(client).MonitorIndex);
    }

    private static RoomNode Node(string id, int order, bool ghost = false) =>
        new() { Id = id, Order = order, WidthPx = 1920, HeightPx = 1080, IsConnected = !ghost, IsGhost = ghost };

    [Fact]
    public void DisabledSeat_KeepsItsPlace_ButIsNeitherSentToNorParked()
    {
        var scene = new CrossScreenConfig
        {
            Layers = { new SceneLayer { Animation = new AnimationLayerConfig { AnimationPath = "fish.gif" } } }
        };
        var nodes = new[] { Node("a", 0), Node("b_MONITOR_0", 1), Node("b_MONITOR_1", 2), Node("c", 3, ghost: true) };
        var map = SeatMap.SingleRow();
        map.DisabledNodeIds = new() { "b_MONITOR_0", "c" };

        var plan = CrossScreenPlanner.Plan(scene, nodes, map, 0f, Array.Empty<float>());

        Assert.Equal(new[] { "a", "b_MONITOR_1" }, plan.Remote.Select(p => p.Node.Id));
        Assert.Empty(plan.Ghosts);
        Assert.Equal(new[] { "b_MONITOR_0", "c" }, plan.Disabled.Select(p => p.Node.Id));
        Assert.Equal(1920 * 2, plan.Layout.Get("b_MONITOR_1")!.OffsetX);   // b_MONITOR_0 still holds its place
        Assert.Equal(1920 * 4, plan.Layout.CanvasWidth);
    }

    [Fact]
    public void Agenda_NoStandInOnAnOffMonitor_AndARunningOneStops()
    {
        var (start, stop) = AgendaStandIns.Plan(agendaOn: true, monitorCount: 2,
            withScene: new HashSet<int>(), withStandIn: new HashSet<int> { 1 }, off: new HashSet<int> { 1 });
        Assert.Equal(new[] { 0 }, start);
        Assert.Equal(new[] { 1 }, stop);

        var agenda = new Agenda { HiddenNodeIds = { "b_MONITOR_1" } };
        Assert.True(agenda.IsHidden(new[] { "b", "b_MONITOR_1" }));
        Assert.False(agenda.IsHidden(new[] { "b", "b_MONITOR_0" }));
    }
}

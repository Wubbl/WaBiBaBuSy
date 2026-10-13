using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class RoomNodeSourcesTests
{
    private static readonly LocalScreens TwoMonitors = new(new[]
    {
        new LocalMonitor(0, 1920, 1080, 0, 0, true, @"\\.\DISPLAY1", 144),
        new LocalMonitor(1, 2560, 1440, 1920, 0, false, @"\\.\DISPLAY2", 60),
    }, 4480, 1440, "PC");

    private static readonly Dictionary<string, int> NoGaps = new();

    private static ConnectedClient Remote(string id, int order, int monitors, int gap = 0)
    {
        var screen = new ScreenConfiguration { MonitorCount = monitors };
        for (int m = 0; m < monitors; m++)
            screen.Monitors.Add(new MonitorInfo { Index = m, Width = 1280 + m, Height = 720, RefreshRate = 75, PixelsPerCm = 38f });
        return new ConnectedClient
        {
            ClientId = id, Hostname = id.ToLowerInvariant(), IpAddress = "10.0.0.5", Status = ClientStatusEnum.ClientPlaying,
            OrderPosition = order, PhysicalDistanceCm = gap, ScreenConfig = screen, RttMs = 4, ClockOffsetMs = -2,
        };
    }

    [Fact]
    public void StandaloneNodes_OnePerMonitor()
    {
        var nodes = RoomNodeSources.StandaloneNodes(TwoMonitors, new Dictionary<string, int> { ["LOCAL_MACHINE_MONITOR_1"] = 12 });

        Assert.Equal(new[] { "LOCAL_MACHINE_MONITOR_0", "LOCAL_MACHINE_MONITOR_1" }, nodes.Select(n => n.ClientId));
        Assert.Equal(new[] { 0, 1 }, nodes.Select(n => n.OrderPosition));
        Assert.Equal("PC - Monitor 1", nodes[0].Hostname);
        Assert.Equal("Primary Monitor", nodes[0].IpAddress);
        Assert.Equal("Monitor 2", nodes[1].IpAddress);
        Assert.Equal(new[] { 0, 12 }, nodes.Select(n => n.PhysicalDistanceCm));
        Assert.Equal(ClientStatusEnum.ClientConnected, nodes[0].Status);
        Assert.Equal(2, nodes[0].ScreenConfig.MonitorCount);
        Assert.Equal(4480, nodes[0].ScreenConfig.TotalWidth);
        Assert.Equal(144, nodes[0].ScreenConfig.Monitors[0].RefreshRate);
    }

    [Fact]
    public void ServerNodes_LocalsThenClients_OrderAndGaps()
    {
        var persistedOrder = new Dictionary<string, int> { ["SERVER_LOCALHOST_MONITOR_1"] = 5, ["MULTI_MONITOR_1"] = 9 };
        var persistedGap = new Dictionary<string, int> { ["SERVER_LOCALHOST_MONITOR_0"] = 3, ["MULTI_MONITOR_0"] = 7 };
        var session = new Dictionary<string, int> { ["SERVER_LOCALHOST_MONITOR_1"] = 11, ["SERVER_LOCALHOST_MONITOR_0"] = 99 };

        var nodes = RoomNodeSources.ServerNodes(TwoMonitors,
            new[] { Remote("SINGLE", 4, 1, gap: 6), Remote("MULTI", 1, 2, gap: 8) },
            id => persistedOrder.TryGetValue(id, out var o) ? o : null,
            id => persistedGap.TryGetValue(id, out var g) ? g : null,
            session);

        Assert.Equal(new[] { "SERVER_LOCALHOST_MONITOR_0", "SERVER_LOCALHOST_MONITOR_1", "SINGLE", "MULTI_MONITOR_0", "MULTI_MONITOR_1" },
            nodes.Select(n => n.ClientId));
        // Locals: persisted order, else index. Gap: persisted, else session.
        Assert.Equal(0, nodes[0].OrderPosition);
        Assert.Equal(5, nodes[1].OrderPosition);
        Assert.Equal(3, nodes[0].PhysicalDistanceCm);
        Assert.Equal(11, nodes[1].PhysicalDistanceCm);
        Assert.Equal("PC - Monitor 2 (Server)", nodes[1].Hostname);
        Assert.Equal("Primary Monitor (Server)", nodes[0].IpAddress);
        // Single-monitor client: the server's order and gap as reported.
        Assert.Equal(4, nodes[2].OrderPosition);
        Assert.Equal(6, nodes[2].PhysicalDistanceCm);
        // Expanded client: persisted order, else a counter that starts after the locals (2) and advances once per
        // expanded node only (single-monitor clients keep the server's order and do not advance it).
        Assert.Equal(2, nodes[3].OrderPosition);
        Assert.Equal(9, nodes[4].OrderPosition);
        Assert.Equal(7, nodes[3].PhysicalDistanceCm);   // persisted
        Assert.Equal(8, nodes[4].PhysicalDistanceCm);   // client's own gap
        Assert.Equal("multi - Monitor 2", nodes[4].Hostname);
        Assert.Equal(ClientStatusEnum.ClientPlaying, nodes[4].Status);
        Assert.Equal(4, nodes[4].RttMs);
    }

    [Fact]
    public void ToRoomNode_LocalMonitor()
    {
        var node = RoomNodeSources.ToRoomNode(RoomNodeSources.StandaloneNodes(TwoMonitors, NoGaps)[1]);

        Assert.Equal("LOCAL_MACHINE_MONITOR_1", node.Id);
        Assert.Equal(1, node.MonitorIndex);
        Assert.Equal(2560, node.WidthPx);
        Assert.Equal(1440, node.HeightPx);
        Assert.Equal(60, node.RefreshHz);
        Assert.True(node.IsConnected);
        Assert.True(node.IsLocal);
    }

    [Fact]
    public void ToRoomNode_ExpandedAndSingleClients()
    {
        var expanded = Remote("MULTI", 0, 2);
        expanded.ClientId = "MULTI_MONITOR_1";
        var e = RoomNodeSources.ToRoomNode(expanded);
        Assert.Equal(1, e.MonitorIndex);
        Assert.Equal(1281, e.WidthPx);
        Assert.Equal(38f, e.PixelsPerCm);
        Assert.True(e.IsConnected);   // Playing counts as connected
        Assert.Equal(-2, e.ClockOffsetMs);

        var single = RoomNodeSources.ToRoomNode(Remote("SINGLE", 3, 1, gap: 6));
        Assert.Equal(0, single.MonitorIndex);
        Assert.Equal(1280, single.WidthPx);
        Assert.Equal(3, single.Order);
        Assert.Equal(6, single.GapBeforeCm);
    }

    [Fact]
    public void MonitorFor_IndexOutOfRange_KeepsIndexWithoutInfo()
    {
        var c = Remote("X", 0, 1);
        c.ClientId = "X_MONITOR_4";
        var (index, monitor) = RoomNodeSources.MonitorFor(c);
        Assert.Equal(4, index);
        Assert.Null(monitor);
        Assert.Equal(0, RoomNodeSources.ToRoomNode(c).WidthPx);
    }

    [Fact]
    public void ToRoomNode_SyncingCountsAsConnected()
    {
        // Review 2026-10-13 C11: every command ack set Syncing, and the node dropped out of the next start for up to 5 s.
        var c = Remote("X", 0, 1);
        c.Status = ClientStatusEnum.ClientSyncing;
        Assert.True(RoomNodeSources.ToRoomNode(c).IsConnected);
    }

    [Fact]
    public void ToRoomNode_DisconnectedStatus()
    {
        var c = Remote("X", 0, 1);
        c.Status = ClientStatusEnum.ClientDisconnected;
        Assert.False(RoomNodeSources.ToRoomNode(c).IsConnected);
    }
}

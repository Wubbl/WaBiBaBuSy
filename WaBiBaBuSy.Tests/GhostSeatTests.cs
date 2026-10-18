using System.Text.Json;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Ghost seats (post-party plans §1): an offline machine keeps its seat, so the layout of every other seat is the
/// same whether it is connected or not, and nothing is sent to it.
/// </summary>
public class GhostSeatTests
{
    private static readonly LocalScreens Server = new(new[] { new LocalMonitor(0, 1920, 1080, 0, 0, true, @"\.\DISPLAY1", 60) },
        1920, 1080, "SRV");

    private static ConnectedClient Client(string id, int order, int gap, params (int W, int H)[] monitors)
    {
        var screen = new ScreenConfiguration { MonitorCount = monitors.Length };
        for (int m = 0; m < monitors.Length; m++)
            screen.Monitors.Add(new MonitorInfo { Index = m, Width = monitors[m].W, Height = monitors[m].H, PixelsPerCm = 37f + m });
        return new ConnectedClient
        {
            ClientId = id, Hostname = id.ToLowerInvariant(), Status = ClientStatusEnum.ClientPlaying,
            OrderPosition = order, PhysicalDistanceCm = gap, ScreenConfig = screen
        };
    }

    private static CrossScreenConfig Scene() => new()
    {
        Layers = { new SceneLayer {
            Animation = new AnimationLayerConfig { AnimationPath = "fish.gif", TargetHeight = 200 },
            Movement = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 300 } } }
    };

    /// <summary>The store as the server fills it when each client registers.</summary>
    private static TopologyStore Store(IEnumerable<ConnectedClient> clients, DateTime seen)
    {
        var store = TopologyStore.Load(Path.Combine(Path.GetTempPath(), $"ghost-{Guid.NewGuid():N}.json"));
        foreach (var c in clients)
        {
            store.Upsert(new TopologyEntry { ClientId = c.ClientId, Hostname = c.Hostname, OrderPosition = c.OrderPosition, PhysicalDistanceCm = c.PhysicalDistanceCm });
            var monitors = c.ScreenConfig.Monitors.Select(m => (m.Width, m.Height, m.PixelsPerCm)).ToList();
            store.RecordMonitors(c.ClientId, monitors, expanded: monitors.Count > 1, seen);
        }
        return store;
    }

    private static CrossScreenPlan Plan(TopologyStore store, IEnumerable<ConnectedClient> connected, DateTime now)
    {
        var sources = RoomNodeSources.ServerNodes(Server, connected,
            id => store.Entries.FirstOrDefault(e => e.ClientId == id)?.OrderPosition,
            id => store.Entries.FirstOrDefault(e => e.ClientId == id)?.PhysicalDistanceCm,
            new Dictionary<string, int>(), store.Entries, now);
        var nodes = sources.Select(RoomNodeSources.ToRoomNode).OrderBy(n => n.Order).ToList();
        return CrossScreenPlanner.Plan(Scene(), nodes, SeatMap.SingleRow(), 0f, new[] { 40f });
    }

    private static Dictionary<string, string> Layouts(CrossScreenPlan plan) =>
        plan.Layout.Nodes.ToDictionary(n => n.Id, n => JsonSerializer.Serialize(n));

    [Fact]
    public void OfflineSeats_KeepEveryNodeLayout_AndGetNoCommand()
    {
        var now = new DateTime(2026, 10, 18, 20, 0, 0, DateTimeKind.Utc);
        var a = Client("A", 1, 0, (1920, 1080));
        var b = Client("B", 2, 12, (2560, 1440));
        var multi = Client("M", 3, 5, (1280, 1024), (1920, 1200));
        var c = Client("C", 6, 8, (1920, 1080));
        var store = Store(new[] { a, b, multi, c }, now.AddHours(-1));

        var all = Plan(store, new[] { a, b, multi, c }, now);
        var withoutBAndM = Plan(store, new[] { a, c }, now);

        Assert.Equal(Layouts(all), Layouts(withoutBAndM));   // every seat incl. the ghosts: same canvas, same offsets
        Assert.Equal(all.Layout.CanvasWidth, withoutBAndM.Layout.CanvasWidth);
        Assert.Equal(new[] { "A", "C" }, withoutBAndM.Remote.Select(p => p.Node.Id));
        Assert.Equal(new[] { "B", "M_MONITOR_0", "M_MONITOR_1" }, withoutBAndM.Ghosts.Select(p => p.Node.Id).OrderBy(x => x));
        Assert.All(withoutBAndM.Ghosts, g => Assert.False(g.Node.IsConnected));
        Assert.Empty(all.Ghosts);
    }

    [Fact]
    public void SeatsNotSeenFor15Days_AreNotLoaded()
    {
        var now = new DateTime(2026, 10, 18, 20, 0, 0, DateTimeKind.Utc);
        var a = Client("A", 1, 0, (1920, 1080));

        Assert.Empty(Plan(Store(new[] { a }, now.AddDays(-15)), Array.Empty<ConnectedClient>(), now).Ghosts);
        Assert.Single(Plan(Store(new[] { a }, now.AddDays(-13)), Array.Empty<ConnectedClient>(), now).Ghosts);
    }

    [Fact]
    public void ForgetSeat_RemovesTheMachineAndItsMonitors()
    {
        var now = DateTime.UtcNow;
        var store = Store(new[] { Client("M", 3, 0, (1280, 1024), (1920, 1200)), Client("A", 1, 0, (1920, 1080)) }, now);

        Assert.Equal(3, store.Remove("M"));
        Assert.Equal(new[] { "A" }, store.Entries.Select(e => e.ClientId));
    }
}

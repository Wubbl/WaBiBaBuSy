using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Pure pieces of the server's app-lifetime playback host (2026-10-03): node ids, the cross-screen plan
/// (targets, local order, DPI fallbacks, layout), the test node list, the show status line and prefetch readiness.
/// </summary>
public class ServerPlaybackLogicTests
{
    private static RoomNode Node(string id, int order, int monitorIndex = -1, float ppcm = 0f, double rtt = 0,
        int w = 1920, int h = 1080, bool connected = true, string host = "") => new()
    {
        Id = id, Order = order, MonitorIndex = monitorIndex, PixelsPerCm = ppcm, RttMs = rtt,
        WidthPx = w, HeightPx = h, IsConnected = connected, Hostname = host
    };

    private static CrossScreenConfig Scene(params string[] selected) => new()
    {
        Animation = new AnimationLayerConfig { AnimationPath = "fish.gif", TargetHeight = 200 },
        Movement = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 300 },
        SelectedMonitorIds = selected.ToList()
    };

    // ── NodeIds ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("LOCAL_MACHINE_MONITOR_0", true)]
    [InlineData("SERVER_LOCALHOST_MONITOR_2", true)]
    [InlineData("abc-123", false)]
    [InlineData("abc-123_MONITOR_1", false)]
    public void NodeIds_IsLocalMonitor(string id, bool expected) => Assert.Equal(expected, NodeIds.IsLocalMonitor(id));

    [Theory]
    [InlineData("LOCAL_MACHINE_MONITOR_0", 0)]
    [InlineData("SERVER_LOCALHOST_MONITOR_2", 2)]
    [InlineData("abc-123", -1)]
    [InlineData("SERVER_LOCALHOST_MONITOR_x", -1)]
    public void NodeIds_MonitorIndex(string id, int expected) => Assert.Equal(expected, NodeIds.MonitorIndex(id));

    // ── CrossScreenPlanner ──────────────────────────────────────────────────

    [Fact]
    public void Plan_EmptySelection_UsesEveryNode_LocalsInChainOrder()
    {
        var nodes = new[]
        {
            Node("SERVER_LOCALHOST_MONITOR_1", order: 2, monitorIndex: 1),
            Node("remote-a", order: 1),
            Node("SERVER_LOCALHOST_MONITOR_0", order: 0, monitorIndex: 0),
        };

        var plan = CrossScreenPlanner.Plan(Scene(), nodes, SeatMap.SingleRow(), 0f, new[] { 40f, 40f });

        Assert.Equal(new[] { "SERVER_LOCALHOST_MONITOR_0", "SERVER_LOCALHOST_MONITOR_1" }, plan.Local.Select(n => n.Node.Id));
        Assert.Equal(new[] { "remote-a" }, plan.Remote.Select(n => n.Node.Id));
        Assert.False(plan.IsEmpty);
        // Layout follows the chain order (Order), local and remote alike.
        Assert.Equal(new[] { "SERVER_LOCALHOST_MONITOR_0", "remote-a", "SERVER_LOCALHOST_MONITOR_1" },
            plan.Layout.Nodes.OrderBy(n => n.OffsetX).Select(n => n.Id));
    }

    [Fact]
    public void Plan_Selection_FiltersNodes_AndOrdersLocalsBySelectionOrder()
    {
        var nodes = new[]
        {
            Node("SERVER_LOCALHOST_MONITOR_0", order: 0, monitorIndex: 0),
            Node("SERVER_LOCALHOST_MONITOR_1", order: 1, monitorIndex: 1),
            Node("remote-a", order: 2),
            Node("remote-b", order: 3),
        };

        var plan = CrossScreenPlanner.Plan(Scene("SERVER_LOCALHOST_MONITOR_1", "remote-b", "SERVER_LOCALHOST_MONITOR_0"),
            nodes, SeatMap.SingleRow(), 0f, new[] { 40f, 40f });

        Assert.Equal(new[] { "SERVER_LOCALHOST_MONITOR_1", "SERVER_LOCALHOST_MONITOR_0" }, plan.Local.Select(n => n.Node.Id));
        Assert.Equal(new[] { "remote-b" }, plan.Remote.Select(n => n.Node.Id));
        Assert.Null(plan.Layout.Get("remote-a"));
    }

    [Fact]
    public void Plan_NoMatchingNode_IsEmpty()
    {
        var plan = CrossScreenPlanner.Plan(Scene("gone"), new[] { Node("remote-a", 0) }, SeatMap.SingleRow(), 0f, Array.Empty<float>());
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Plan_UnknownSize_FallsBackTo1080p()
    {
        var plan = CrossScreenPlanner.Plan(Scene(), new[] { Node("remote-a", 0, w: 0, h: 0) }, SeatMap.SingleRow(), 0f, Array.Empty<float>());
        Assert.Equal((1920, 1080), (plan.Remote[0].WidthPx, plan.Remote[0].HeightPx));
    }

    [Fact]
    public void Plan_PixelsPerCm_LocalMachineByMonitor_RemoteReported_ElseFirstLocalMonitor()
    {
        var nodes = new[]
        {
            Node("LOCAL_MACHINE_MONITOR_1", order: 0, monitorIndex: 1, ppcm: 99f),   // local: the native monitor's DPI wins
            Node("remote-a", order: 1, ppcm: 50f),                                   // remote: what it reported
            Node("remote-b", order: 2),                                              // remote, unknown: first local monitor
            Node("SERVER_LOCALHOST_MONITOR_0", order: 3, monitorIndex: 0),           // server node: same rule as remotes (unchanged)
        };

        var plan = CrossScreenPlanner.Plan(Scene(), nodes, SeatMap.SingleRow(), 0f, new[] { 37f, 42f });
        var ppcm = plan.Local.Concat(plan.Remote).ToDictionary(n => n.Node.Id, n => n.PixelsPerCm);

        Assert.Equal(42f, ppcm["LOCAL_MACHINE_MONITOR_1"]);
        Assert.Equal(50f, ppcm["remote-a"]);
        Assert.Equal(37f, ppcm["remote-b"]);
        Assert.Equal(37f, ppcm["SERVER_LOCALHOST_MONITOR_0"]);
    }

    [Fact]
    public void Plan_MaxRemoteRtt_AndPerMonitorFlag()
    {
        var nodes = new[] { Node("SERVER_LOCALHOST_MONITOR_0", 0, 0, rtt: 500), Node("remote-a", 1, rtt: 12), Node("remote-b", 2, rtt: 30) };
        var scene = Scene();
        scene.DistributionMode = AnimationDistributionMode.Simultaneous;

        var plan = CrossScreenPlanner.Plan(scene, nodes, SeatMap.SingleRow(), 0f, new[] { 40f });

        Assert.Equal(30, plan.MaxRemoteRttMs);
        Assert.True(plan.PerMonitor);
    }

    [Fact]
    public void Plan_PhysicalCanvas_ResolvesCmSpeedToCanvasPixels()
    {
        var map = SeatMap.SingleRow();
        map.CanvasMode = CanvasMode.Physical;
        var scene = Scene();
        scene.Movement.SpeedUnit = SpeedUnit.CentimetersPerSecond;
        scene.Movement.SpeedCmPerSecond = 10f;

        var plan = CrossScreenPlanner.Plan(scene, new[] { Node("remote-a", 0, ppcm: 40f) }, map, 40f, Array.Empty<float>());

        Assert.Equal(400f, plan.EffectiveMovement.SpeedPixelsPerSecond);
        Assert.Equal(SpeedUnit.PixelsPerSecond, plan.EffectiveMovement.SpeedUnit);
        Assert.Equal(SpeedUnit.CentimetersPerSecond, scene.Movement.SpeedUnit);   // the scene itself is untouched
    }

    // ── TestNodeList ────────────────────────────────────────────────────────

    [Fact]
    public void TestNodeList_SeatOrder_LocalNames_Versions_DuplicatesOnce()
    {
        var nodes = new[]
        {
            Node("remote-a", order: 1, host: "PC-A", connected: false) with { RefreshHz = 60, ClockOffsetMs = 3 },
            Node("SERVER_LOCALHOST_MONITOR_1", order: 0, monitorIndex: 1, connected: false),
            Node("remote-a", order: 5, host: "dup"),
        };

        var list = TestNodeList.Build(nodes, new Dictionary<string, string> { ["remote-a"] = "2.6.2" }, "2.6.3");

        Assert.Equal(2, list.Count);
        Assert.Equal(("SERVER_LOCALHOST_MONITOR_1", "server #1", true, true, 0, "2.6.3"),
            (list[0].NodeId, list[0].Name, list[0].IsLocal, list[0].Connected, list[0].SeatOrder, list[0].AppVersion));
        Assert.Equal(("remote-a", "PC-A", false, false, 1, "2.6.2", 60, 3.0),
            (list[1].NodeId, list[1].Name, list[1].IsLocal, list[1].Connected, list[1].SeatOrder, list[1].AppVersion, list[1].RefreshHz, list[1].ClockOffsetMs));
        Assert.Equal(0, list[1].MonitorIndex);   // whole-machine node (-1) → 0
    }

    // ── ShowStatus ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Fish", "Logo Rain", 42_000, "Now: Fish · next: Logo Rain in 0:42")]
    [InlineData("Fish", null, 65_000, "Now: Fish · last item · ends in 1:05")]
    [InlineData(" ", "Logo Rain", 1_000, "next: Logo Rain in 0:01")]
    [InlineData("Fish", "Logo Rain", -500, "Now: Fish · next: Logo Rain in 0:00")]
    [InlineData("Fish", "Logo Rain", 3_725_000, "Now: Fish · next: Logo Rain in 62:05")]
    public void ShowStatus_Format(string? now, string? next, long remainingMs, string expected) =>
        Assert.Equal(expected, ShowStatus.Format(now, next, remainingMs));

    [Fact]
    public void ShowStatus_RemotesReady()
    {
        Assert.True(ShowStatus.RemotesReady(Array.Empty<(bool, int, int)>()));
        Assert.True(ShowStatus.RemotesReady(new[] { (true, 3, 3), (false, 0, 0) }));     // a disconnected node is not waited for
        Assert.False(ShowStatus.RemotesReady(new[] { (true, 3, 3), (true, 1, 3) }));
        Assert.False(ShowStatus.RemotesReady(new[] { (true, 0, 0) }));                   // nothing reported yet
    }
}

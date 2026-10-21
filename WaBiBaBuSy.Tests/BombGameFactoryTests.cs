using System.Text.Json;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>BiBaBo game generation: seats, roulette, cable routes, exactly one live cable (server-only).</summary>
public class BombGameFactoryTests
{
    private const int W = 1920, H = 1080;

    /// <summary>One row of <paramref name="machines"/> × <paramref name="monitors"/> 1920×1080 nodes.</summary>
    private static SeatMapLayoutResult Layout(int machines, int monitors = 1, bool wraps = false)
    {
        var nodes = new List<NodeLayout>();
        int n = machines * monitors;
        for (int m = 0; m < machines; m++)
            for (int k = 0; k < monitors; k++)
            {
                int i = nodes.Count;
                nodes.Add(new NodeLayout
                {
                    Id = monitors == 1 ? $"c{m}" : $"c{m}_MONITOR_{k}",
                    OffsetX = i * W, Width = W, Height = H, CanvasWidth = n * W, CanvasHeight = H, Order = i, Wraps = wraps,
                });
            }
        return new SeatMapLayoutResult { Nodes = nodes, CanvasWidth = n * W, CanvasHeight = H, Wraps = wraps, Traversal = wraps ? TraversalMode.Ring : TraversalMode.Snake };
    }

    private static (BombGame Game, int Live) Make(SeatMapLayoutResult layout, int seed = 7, Func<string, bool>? busy = null, IReadOnlyCollection<string>? targets = null)
    {
        var (game, live) = BombGameFactory.Create(new BombGameRequest(layout, targets, busy ?? (_ => false), seed, "g1"), out var error);
        Assert.True(game != null, error);
        return (game!, live);
    }

    [Fact]
    public void Factory_SameSeed_SameGame()
    {
        var a = Make(Layout(8)).Game;
        var b = Make(Layout(8)).Game;
        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
        Assert.NotEqual(JsonSerializer.Serialize(a), JsonSerializer.Serialize(Make(Layout(8), seed: 8).Game));
    }

    [Fact]
    public void Factory_BombAndEndsOnDistinctMachines()
    {
        for (int seed = 0; seed < 30; seed++)
        {
            var g = Make(Layout(8, monitors: 2), seed).Game;
            var machines = g.Cables.Select(c => BombSeats.MachineOf(c.EndNodeId)).Append(BombSeats.MachineOf(g.BombNodeId)).ToList();
            Assert.Equal(4, machines.Distinct().Count());
            Assert.Equal(3, g.Cables.Count);
        }
    }

    [Fact]
    public void Factory_RefusesFewerThanTwoMachines()
    {
        var (game, _) = BombGameFactory.Create(new BombGameRequest(Layout(1, monitors: 1), null, _ => false, 1, "g"), out var error);
        Assert.Null(game);
        Assert.Contains("free seats", error);
        var (allBusy, _) = BombGameFactory.Create(new BombGameRequest(Layout(4), null, _ => true, 1, "g"), out error);
        Assert.Null(allBusy);
        Assert.Contains("4 busy", error);
    }

    [Fact]
    public void Factory_OneMachineWithTwoMonitors_PlaysOnTheMonitors()
    {
        var g = Make(Layout(1, monitors: 2)).Game;   // a home setup: the monitors are the seats
        Assert.NotEqual(g.BombNodeId, g.Cables[0].EndNodeId);
        var (busy, _) = BombGameFactory.Create(new BombGameRequest(Layout(1, monitors: 2), null, id => id.EndsWith("_1"), 1, "g"), out var error);
        Assert.Null(busy);
        Assert.Contains("busy", error);
    }

    [Fact]
    public void Factory_SkipsBusyAndUntargetedSeats()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var g = Make(Layout(8), seed, busy: id => id is "c1" or "c2", targets: new[] { "c0", "c1", "c2", "c3", "c4", "c5" }).Game;
            var used = g.Cables.Select(c => c.EndNodeId).Append(g.BombNodeId).ToList();
            Assert.DoesNotContain("c1", used);
            Assert.DoesNotContain("c2", used);
            Assert.DoesNotContain("c6", used);
            Assert.DoesNotContain("c7", used);
        }
    }

    [Fact]
    public void Factory_TwoMachines_CardsShareTheOtherSeat()
    {
        var g = Make(Layout(2)).Game;
        var end = g.Cables[0].EndNodeId;
        Assert.All(g.Cables, c => Assert.Equal(end, c.EndNodeId));
        var xs = g.Cables.Select(c => c.CardX).OrderBy(x => x).ToList();
        for (int i = 1; i < xs.Count; i++) Assert.True(xs[i] >= xs[i - 1] + g.Cables[0].CardW - 0.01f, "cards overlap");
    }

    [Fact]
    public void Factory_ExactlyOneLiveCable_NotInJson()
    {
        var (g, live) = Make(Layout(6));
        Assert.InRange(live, 0, 2);
        var json = JsonSerializer.Serialize(g);
        Assert.DoesNotContain("Live", json);
        for (int i = 0; i < 3; i++)
        {
            var r = g.Cables[i].Riddle;
            bool cardSaysLive = r.Kind == RiddleKind.Math
                ? Math.Abs(TestFormula.Evaluate(r.Formula!) - 1) < 1e-9
                : r.StrandExits![0] == 0;
            Assert.Equal(i == live, cardSaysLive);
        }
    }

    [Fact]
    public void Route_StartsAtTerminal_EndsAtCard()
    {
        var g = Make(Layout(6)).Game;
        foreach (var c in g.Cables)
        {
            float x0 = c.Points[0], xn = c.Points[^2], yn = c.Points[^1];
            Assert.True(Math.Abs(Math.Abs(x0 - g.BombX) - g.BombSize / 2) < 1f, "starts at the bomb's side");
            bool atLeft = Math.Abs(xn - c.CardX) < 1f, atRight = Math.Abs(xn - (c.CardX + c.CardW)) < 1f;
            Assert.True(atLeft || atRight, $"ends at a card edge ({xn} vs {c.CardX}..{c.CardX + c.CardW})");
            Assert.InRange(yn, c.CardY, c.CardY + c.CardH);
        }
    }

    [Fact]
    public void Route_YStaysInsideTheNodeUnderEachPoint()
    {
        for (int seed = 0; seed < 20; seed++)
            foreach (var c in Make(Layout(8), seed).Game.Cables)
                for (int i = 1; i < c.Points.Length; i += 2)
                    Assert.InRange(c.Points[i], 0.05f * H - 0.01f, 0.95f * H + 0.01f);
    }

    [Fact]
    public void Route_RingTakesShortWayAcrossSeam()
    {
        var layout = Layout(10, wraps: true);
        var rng = new Random(3);
        var pts = BombGameFactory.Route(layout, fromX: 500, fromY: 540, toX: 9 * W + 900, toY: 540, rng);
        Assert.True(pts[^2] < 0, "the short way goes left across the seam (unwrapped x < 0)");
        Assert.True(Math.Abs(pts[^2] - (9 * W + 900 - 10 * W)) < 1f);
        Assert.True(pts.Where((_, i) => i % 2 == 0).All(x => x <= 500 + 1));
    }

    [Fact]
    public void Roulette_EndsOnBomb()
    {
        var g = Make(Layout(8)).Game;
        Assert.InRange(g.RouletteOrders.Length, 8, 12);
        Assert.Equal(g.BombOrder, g.RouletteOrders[^1]);
    }

    [Fact]
    public void MachineOf_GroupsMonitors()
    {
        Assert.Equal("c1", BombSeats.MachineOf("c1_MONITOR_2"));
        Assert.Equal("c1", BombSeats.MachineOf("c1"));
        Assert.Equal(BombSeats.ThisMachine, BombSeats.MachineOf("SERVER_LOCALHOST_MONITOR_1"));
        Assert.Equal(BombSeats.ThisMachine, BombSeats.MachineOf("LOCAL_MACHINE_MONITOR_0"));
    }
}

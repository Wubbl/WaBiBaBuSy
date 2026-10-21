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

    private static (BombGame Game, BombSolution Solution) Make(SeatMapLayoutResult layout, int seed = 7, Func<string, bool>? busy = null,
        IReadOnlyCollection<string>? targets = null, BombMode? mode = null, bool? junction = null, bool? ruleCard = null)
    {
        var (game, solution) = BombGameFactory.Create(new BombGameRequest(layout, targets, busy ?? (_ => false), seed, "g1",
            ForceMode: mode, ForceJunction: junction, ForceRuleCard: ruleCard), out var error);
        Assert.True(game != null, error);
        return (game!, solution!);
    }

    /// <summary>Which cables the game's first rule line makes live (a person reading the RULES card).</summary>
    private static int[] LiveByRule(BombGame g, int[] values)
    {
        string rule = g.RuleLines[0];
        Func<int, bool> pred;
        if (rule.Contains("LARGEST")) { int m = values.Max(); pred = v => v == m; }
        else if (rule.Contains("SMALLEST")) { int m = values.Min(); pred = v => v == m; }
        else if (rule.Contains("is odd")) pred = v => v % 2 == 1;
        else if (rule.Contains("is even")) pred = v => v % 2 == 0;
        else if (rule.Contains("prime")) pred = v => v is 2 or 3 or 5 or 7;
        else if (rule.Contains("greater than 4")) pred = v => v > 4;
        else if (rule.Contains("result = ")) { int k = int.Parse(rule[(rule.IndexOf("= ") + 2)..]); pred = v => v == k; }
        else throw new InvalidOperationException(rule);
        return Enumerable.Range(0, values.Length).Where(i => pred(values[i])).ToArray();
    }

    private static int[] Values(BombGame g) => g.Cables.Select(c => TestRiddle.Value(c.Riddle)).ToArray();

    [Fact]
    public void Classic_ExactlyOneLive_MatchesTheRule()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var (g, sol) = Make(Layout(8), seed, mode: BombMode.Classic);
            Assert.Equal(BombMode.Classic, g.Mode);
            var live = LiveByRule(g, Values(g));
            Assert.True(live.Length == 1, $"seed {seed}: {g.RuleLines[0]} with {string.Join(",", Values(g))}");
            Assert.Equal(sol.LiveCable, live[0]);
        }
    }

    [Fact]
    public void CutOrder_ExactlyTwoLive_InTheStatedOrder()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var (g, sol) = Make(Layout(8), seed, mode: BombMode.CutOrder);
            var values = Values(g);
            var live = LiveByRule(g, values);
            Assert.True(live.Length == 2, $"seed {seed}: {g.RuleLines[0]} with {string.Join(",", values)}");
            Assert.NotEqual(values[live[0]], values[live[1]]);
            bool smallest = string.Join(" ", g.RuleLines).Contains("smallest result first");
            var expected = smallest ? live.OrderBy(i => values[i]).ToArray() : live.OrderByDescending(i => values[i]).ToArray();
            Assert.Equal(expected, sol.CutOrder);
        }
    }

    [Fact]
    public void Keypad_CodeIsTheResultsInTheStatedColourOrder()
    {
        string[] names = { "RED", "GREEN", "BLUE" };
        for (int seed = 0; seed < 300; seed++)
        {
            var (g, sol) = Make(Layout(8), seed, mode: BombMode.Keypad);
            var values = Values(g);
            Assert.Equal(string.Concat(g.CodeOrder.Select(i => values[i])), sol.Code);
            Assert.Equal("CODE = " + string.Join(" · ", g.CodeOrder.Select(i => names[i])), g.RuleLines[0]);
        }
    }

    [Fact]
    public void Modes_AllRolled_AndKindsVary()
    {
        var modes = new HashSet<BombMode>();
        var kinds = new HashSet<RiddleKind>();
        for (int seed = 0; seed < 200; seed++)
        {
            var (g, _) = Make(Layout(8), seed);
            modes.Add(g.Mode);
            foreach (var c in g.Cables) kinds.Add(c.Riddle.Kind);
            Assert.True(g.Cables.Select(c => c.Riddle.Kind).Distinct().Count() >= 2, "a game mixes at least two riddle kinds");
        }
        Assert.Equal(3, modes.Count);
        Assert.Equal(7, kinds.Count);
    }

    [Fact]
    public void RuleCard_SitsOnAnotherMachine()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var (g, _) = Make(Layout(8), seed, ruleCard: true);
            Assert.NotEqual("", g.RuleNodeId);
            var used = g.Cables.Select(c => c.EndNodeId).Append(g.BombNodeId);
            Assert.DoesNotContain(g.RuleNodeId, used);
            Assert.Equal("", Make(Layout(8), seed, ruleCard: false).Game.RuleNodeId);
        }
        Assert.Equal("", Make(Layout(4), 1, ruleCard: true).Game.RuleNodeId);   // no spare machine: rules at the bomb
    }

    [Fact]
    public void Junction_RecoloursTheCablesThroughTheBox()
    {
        int found = 0;
        foreach (bool wraps in new[] { false, true })
            for (int seed = 0; seed < 80; seed++)
            {
                var (g, _) = Make(Layout(10, wraps: wraps), seed, junction: true);
                if (g.Junction is not { } j) continue;
                found++;
                Assert.True(j.Cables.Length >= 2);
                Assert.NotEqual(g.BombNodeId, j.NodeId);
                float canvasW = 10 * W;
                float Mod(float x) => wraps ? ((x % canvasW) + canvasW) % canvasW : x;
                foreach (int i in j.Cables)
                {
                    var c = g.Cables[i];
                    Assert.InRange(c.SwapIndex, 1, c.Points.Length / 2 - 1);
                    float sx = c.Points[2 * c.SwapIndex - 2], ex = c.Points[2 * c.SwapIndex];
                    Assert.True(Math.Abs(Mod(sx) - (j.X + j.W / 2)) <= j.W / 2 + 1 && Math.Abs(Mod(ex) - (j.X + j.W / 2)) <= j.W / 2 + 1,
                        "the swap happens inside the box");
                    var xs = c.Points.Where((_, k) => k % 2 == 0).ToArray();
                    bool up = xs.Zip(xs.Skip(1)).All(p => p.Second >= p.First - 0.01f), down = xs.Zip(xs.Skip(1)).All(p => p.Second <= p.First + 0.01f);
                    Assert.True(up || down, "the short way, no turning back");
                }
                Assert.Contains(j.Cables, i => g.Cables[i].CardColorHex != g.Cables[i].ColorHex);
                foreach (var (c, i) in g.Cables.Select((c, i) => (c, i)).Where(t => !j.Cables.Contains(t.i)))
                {
                    Assert.Equal(c.ColorHex, c.CardColorHex);
                    Assert.Equal(-1, c.SwapIndex);
                }
            }
        Assert.True(found > 10, $"junction rolled only {found} times");
    }

    [Fact]
    public void Junction_NeverUnderACardOrTheRulesCard()
    {
        int found = 0;
        foreach (int machines in new[] { 3, 4, 6, 8 })
            for (int seed = 0; seed < 150; seed++)
            {
                var (g, _) = Make(Layout(machines), seed, junction: true, ruleCard: true);
                if (g.Junction is not { } j) continue;
                found++;
                Assert.NotEqual(g.RuleNodeId, j.NodeId);
                Assert.DoesNotContain(g.Cables, c => c.EndNodeId == j.NodeId);
            }
        Assert.True(found > 20, $"junction rolled only {found} times");
    }

    [Fact]
    public void Solution_NeverInTheGameJson()
    {
        foreach (var mode in Enum.GetValues<BombMode>())
        {
            var json = JsonSerializer.Serialize(Make(Layout(8), 3, mode: mode).Game);
            Assert.DoesNotContain("Solution", json);
            Assert.DoesNotContain("LiveCable", json);
            Assert.DoesNotContain("\"Code\"", json);
            Assert.DoesNotContain("Value", json);
        }
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
    public void Route_Parallel_StaysInTheRowItRunsIn()
    {
        // two rows stacked (Parallel): both start at x = 0; row 1 is 1200 lower
        var nodes = new List<NodeLayout>();
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < 3; i++)
                nodes.Add(new NodeLayout { Id = $"r{row}n{i}", OffsetX = i * W, OffsetY = row * 1200, Width = W, Height = H,
                    CanvasWidth = 3 * W, CanvasHeight = 2280, Order = row * 3 + i, RowIndex = row });
        var layout = new SeatMapLayoutResult { Nodes = nodes, CanvasWidth = 3 * W, CanvasHeight = 2280, Traversal = TraversalMode.Parallel };
        var pts = BombGameFactory.Route(layout, 500, 1200 + 540, 2500, 1200 + 600, new Random(5));
        for (int i = 1; i < pts.Length; i += 2) Assert.InRange(pts[i], 1200 + 0.05f * H - 0.01f, 1200 + 0.95f * H + 0.01f);
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

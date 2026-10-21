using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Show;

/// <summary>Everything the server knows when BiBaBo starts.</summary>
/// <param name="Layout">The room's seat-map canvas (the stand-in or running scene's layout).</param>
/// <param name="TargetNodeIds">The Events strip target; null = every seat.</param>
/// <param name="IsBusy">A window covers this node's monitor (design §8.1).</param>
/// <param name="GroupByMachine">False: every monitor is a seat (test runs on a single machine).</param>
/// <param name="ForceMode">Tests / showcase: this mode instead of the roll.</param>
/// <param name="ForceKinds">Tests / showcase: these riddle kinds (one per cable) instead of the roll.</param>
/// <param name="ForceJunction">Tests / showcase: junction box yes / no instead of the roll.</param>
/// <param name="ForceRuleCard">Tests / showcase: RULES card elsewhere yes / no instead of the roll.</param>
public sealed record BombGameRequest(SeatMapLayoutResult Layout, IReadOnlyCollection<string>? TargetNodeIds, Func<string, bool> IsBusy,
    int Seed, string GameId, int TimerMs = 300_000, bool GroupByMachine = true,
    BombMode? ForceMode = null, RiddleKind[]? ForceKinds = null, bool? ForceJunction = null, bool? ForceRuleCard = null);

/// <summary>
/// Builds a BiBaBo game (design §4, §14): bomb seat, roulette, the rolled mode and rule, three riddle cards, cable routes
/// along the seat chain (optionally through a junction box), and the RULES card. Pure and seeded; runs on the server
/// only — the players get the game, the answers stay in the <see cref="BombSolution"/>.
/// </summary>
public static class BombGameFactory
{
    public static readonly string[] CableColors = { "#E53935", "#43A047", "#1E88E5" };
    public static readonly string[] CableColorNames = { "RED", "GREEN", "BLUE" };

    private const float PointSpacing = 30f;
    private const float BombHeight = 0.5f;      // of the bomb node's height
    private const float BombCentre = 0.42f;     // of the bomb node's height from its top: room for the keypad below
    private const float TerminalSpread = 0.12f; // × BombSize between terminals
    private const float CardHeight = 0.55f;     // of the end node's height
    private const float CardAspect = 1.4f;
    private static readonly RiddleKind[] AllKinds = Enum.GetValues<RiddleKind>();

    public static (BombGame? Game, BombSolution? Solution) Create(BombGameRequest r, out string? error)
    {
        error = null;
        var candidates = BombSeats.Candidates(r.Layout, r.TargetNodeIds, r.IsBusy, r.GroupByMachine);
        if (candidates.Count < 2 && r.GroupByMachine)
            candidates = BombSeats.Candidates(r.Layout, r.TargetNodeIds, r.IsBusy, groupByMachine: false);   // one machine, several monitors: play on the monitors
        if (candidates.Count < 2)
        {
            var targets = r.Layout.Nodes.Where(n => r.TargetNodeIds == null || r.TargetNodeIds.Contains(n.Id)).ToList();
            int busy = targets.Count(n => r.IsBusy(n.Id));
            error = busy > 0 ? $"Not enough free seats — {busy} busy." : "Not enough free seats — BiBaBo needs at least two machines.";
            return (null, null);
        }

        var rng = new Random(r.Seed);
        int bombIndex = rng.Next(candidates.Count);
        var bomb = candidates[bombIndex];
        var ends = PickEnds(candidates, bombIndex, r.Layout.Wraps, rng);

        var mode = r.ForceMode ?? (rng.Next(4) switch { 0 or 1 => BombMode.Classic, 2 => BombMode.CutOrder, _ => BombMode.Keypad });
        var kinds = r.ForceKinds is { Length: 3 } forced ? forced : RollKinds(rng);
        var (values, ruleLines, solution) = RollRule(mode, kinds, rng);

        var game = new BombGame
        {
            GameId = r.GameId,
            BombNodeId = bomb.Id,
            BombOrder = bomb.Order,
            BombX = bomb.OffsetX + bomb.Width / 2f,
            BombY = bomb.OffsetY + BombCentre * bomb.Height,
            BombSize = BombHeight * bomb.Height,
            TimerMs = r.TimerMs,
            Mode = mode,
            RuleLines = ruleLines,
            CodeOrder = solution.Mode == BombMode.Keypad ? CodeOrderOf(ruleLines[0]) : Array.Empty<int>(),
        };
        int hops = 8 + rng.Next(5);
        game.RouletteOrders = Enumerable.Range(0, hops - 1).Select(_ => candidates[rng.Next(candidates.Count)].Order).Append(bomb.Order).ToArray();

        // RULES card on a spare machine (not the bomb, not a card seat)
        var usedMachines = ends.Select(e => BombSeats.MachineOf(e.Id)).Append(BombSeats.MachineOf(bomb.Id)).ToHashSet();
        var spare = candidates.Where(c => !usedMachines.Contains(BombSeats.MachineOf(c.Id)) && !ends.Any(e => e.Id == c.Id) && c.Id != bomb.Id).ToList();
        bool ruleCard = r.ForceRuleCard ?? rng.Next(2) == 0;
        if (ruleCard && spare.Count > 0)
        {
            var seat = spare[rng.Next(spare.Count)];
            float h = CardHeight * seat.Height, w = Math.Min(CardAspect * h, 0.9f * seat.Width);
            h = w / CardAspect;
            game.RuleNodeId = seat.Id;
            (game.RuleX, game.RuleY, game.RuleW, game.RuleH) = (seat.OffsetX + seat.Width / 2f - w / 2f, seat.OffsetY + seat.Height / 2f - h / 2f, w, h);
        }

        // cards and riddles
        float canvasW = r.Layout.CanvasWidth;
        var dx = new float[3];
        for (int i = 0; i < 3; i++)
        {
            var end = ends[i];
            int k = ends.Count(e => e.Id == end.Id), j = ends.Take(i).Count(e => e.Id == end.Id);
            float cellW = end.Width / (float)k;
            float h = CardHeight * end.Height, w = Math.Min(CardAspect * h, 0.9f * cellW);
            h = w / CardAspect;
            game.Cables.Add(new BombCable
            {
                ColorHex = CableColors[i],
                CardColorHex = CableColors[i],
                EndNodeId = end.Id,
                EndOrder = end.Order,
                CardX = end.OffsetX + cellW * (j + 0.5f) - w / 2f,
                CardY = end.OffsetY + end.Height / 2f - h / 2f,
                CardW = w,
                CardH = h,
                Riddle = BombRiddles.Make(kinds[i], values[i], rng),
            });
            dx[i] = game.Cables[i].CardX + w / 2f - game.BombX;
            if (r.Layout.Wraps && Math.Abs(dx[i]) > canvasW / 2f) dx[i] -= Math.Sign(dx[i]) * canvasW;   // the short way round
        }

        var junction = (r.ForceJunction ?? rng.Next(5) < 2) ? PlanJunction(r.Layout, bomb, game, dx, rng) : null;
        game.Junction = junction?.Box;
        for (int i = 0; i < 3; i++)
        {
            var c = game.Cables[i];
            bool right = dx[i] >= 0;
            float fromX = game.BombX + (right ? 1 : -1) * game.BombSize / 2f;
            float fromY = game.BombY + (i - 1) * TerminalSpread * game.BombSize;
            float shift = game.BombX + dx[i] - (c.CardX + c.CardW / 2f);   // 0, or ±canvasW across the seam
            float toX = (right ? c.CardX : c.CardX + c.CardW) + shift, toY = c.CardY + c.CardH / 2f;
            int slot = junction == null ? -1 : Array.IndexOf(junction.Box.Cables, i);
            if (slot >= 0)
            {
                var part1 = Route(r.Layout, fromX, fromY, junction!.EntryX, junction.InY[slot], rng);
                var part2 = Route(r.Layout, junction.ExitX, junction.OutY[slot], toX, toY, rng);
                c.Points = part1.Concat(part2).ToArray();
                c.SwapIndex = part1.Length / 2;
                c.CardColorHex = CableColors[junction.Box.Cables[junction.Perm[slot]]];
            }
            else
            {
                c.Points = Route(r.Layout, fromX, fromY, toX, toY, rng);
            }
        }
        return (game, solution);
    }

    /// <summary>Three riddle kinds; never all the same.</summary>
    private static RiddleKind[] RollKinds(Random rng)
    {
        while (true)
        {
            var kinds = Enumerable.Range(0, 3).Select(_ => AllKinds[rng.Next(AllKinds.Length)]).ToArray();
            if (kinds.Distinct().Count() >= 2) return kinds;
        }
    }

    // ── rules (design §14.2) ────────────────────────────────────────────────

    private static (int[] Values, string[] Lines, BombSolution Solution) RollRule(BombMode mode, RiddleKind[] kinds, Random rng)
    {
        var ranges = kinds.Select(k => BombRiddles.Range(k)).ToArray();
        int Pick(int i, Func<int, bool> allowed)
        {
            var options = Enumerable.Range(ranges[i].Min, ranges[i].Max - ranges[i].Min + 1).Where(allowed).ToArray();
            return options.Length == 0 ? -1 : options[rng.Next(options.Length)];
        }

        while (true)
        {
            var values = new int[3];
            switch (mode)
            {
                case BombMode.Classic:
                {
                    int live = rng.Next(3);
                    int type = rng.Next(6);
                    if (type >= 4)   // largest / smallest: distinct values, the live one at the extreme
                    {
                        for (int i = 0; i < 3; i++) values[i] = Pick(i, _ => true);
                        if (values.Distinct().Count() < 3) continue;
                        int extreme = type == 4 ? Array.IndexOf(values, values.Max()) : Array.IndexOf(values, values.Min());
                        return (values, new[] { type == 4 ? "LIVE ⇔ the LARGEST result" : "LIVE ⇔ the SMALLEST result", "Cut the LIVE cable at the bomb" },
                            new BombSolution { Mode = mode, Values = values, LiveCable = extreme });
                    }
                    int k = Pick(live, _ => true);
                    Func<int, bool> p = type switch
                    {
                        0 => v => v == k,
                        1 => v => v % 2 == 1,
                        2 => v => v % 2 == 0,
                        _ => v => v is 2 or 3 or 5 or 7,
                    };
                    for (int i = 0; i < 3; i++) values[i] = Pick(i, v => i == live ? p(v) : !p(v));
                    if (values.Contains(-1)) continue;
                    string rule = type switch
                    {
                        0 => $"LIVE ⇔ result = {k}",
                        1 => "LIVE ⇔ result is odd",
                        2 => "LIVE ⇔ result is even",
                        _ => "LIVE ⇔ result is a prime (2 3 5 7)",
                    };
                    return (values, new[] { rule, "Cut the LIVE cable at the bomb" }, new BombSolution { Mode = mode, Values = values, LiveCable = live });
                }
                case BombMode.CutOrder:
                {
                    int dead = rng.Next(3), type = rng.Next(3);
                    Func<int, bool> p = type switch { 0 => v => v % 2 == 0, 1 => v => v % 2 == 1, _ => v => v > 4 };
                    for (int i = 0; i < 3; i++) values[i] = Pick(i, v => i == dead ? !p(v) : p(v));
                    var live = Enumerable.Range(0, 3).Where(i => i != dead).ToArray();
                    if (values.Contains(-1) || values[live[0]] == values[live[1]]) continue;
                    bool smallest = rng.Next(2) == 0;
                    var order = smallest ? live.OrderBy(i => values[i]).ToArray() : live.OrderByDescending(i => values[i]).ToArray();
                    string rule = type switch { 0 => "LIVE ⇔ result is even", 1 => "LIVE ⇔ result is odd", _ => "LIVE ⇔ result is greater than 4" };
                    return (values, new[] { rule, $"Cut both live cables — {(smallest ? "smallest" : "largest")} result first" },
                        new BombSolution { Mode = mode, Values = values, CutOrder = order });
                }
                default:
                {
                    for (int i = 0; i < 3; i++) values[i] = Pick(i, _ => true);
                    var codeOrder = new[] { 0, 1, 2 }.OrderBy(_ => rng.Next()).ToArray();
                    string line = "CODE = " + string.Join(" · ", codeOrder.Select(i => CableColorNames[i]));
                    return (values, new[] { line, "(colours at the bomb) — click or type it on the keypad" },
                        new BombSolution { Mode = mode, Values = values, Code = string.Concat(codeOrder.Select(i => values[i])) });
                }
            }
        }
    }

    private static int[] CodeOrderOf(string codeLine) =>
        codeLine["CODE = ".Length..].Split(" · ").Select(name => Array.IndexOf(CableColorNames, name)).ToArray();

    // ── junction box (design §14.4) ─────────────────────────────────────────

    private sealed record JunctionPlan(BombJunction Box, float EntryX, float ExitX, float[] InY, float[] OutY, int[] Perm);

    /// <summary>
    /// A box on the bomb's neighbouring seat on the side most cables leave; the cables that run past that seat go through
    /// it and come out in each other's colours (a derangement). Null when fewer than two cables pass.
    /// </summary>
    private static JunctionPlan? PlanJunction(SeatMapLayoutResult layout, NodeLayout bomb, BombGame g, float[] dx, Random rng)
    {
        int right = dx.Count(d => d >= 0);
        foreach (int dir in right >= 2 ? new[] { 1, -1 } : new[] { -1, 1 })
        {
            var neighbour = Neighbour(layout, bomb, dir);
            // the box must stay visible: never on a seat that shows a card or the RULES (they are drawn over it)
            if (neighbour == null || neighbour.Id == g.RuleNodeId || g.Cables.Any(c => c.EndNodeId == neighbour.Id)) continue;
            float jc = neighbour.OffsetX + neighbour.Width / 2f;
            float jdx = jc - g.BombX;
            if (layout.Wraps && Math.Abs(jdx) > layout.CanvasWidth / 2f) jdx -= Math.Sign(jdx) * layout.CanvasWidth;
            if (Math.Sign(jdx) != dir) continue;
            var passing = Enumerable.Range(0, 3)
                .Where(i => (dx[i] >= 0 ? 1 : -1) == dir && g.Cables[i].EndNodeId != neighbour.Id && Math.Abs(dx[i]) > Math.Abs(jdx) + neighbour.Width / 2f)
                .ToArray();
            if (passing.Length < 2) continue;

            float bh = 0.45f * neighbour.Height, bw = 0.3f * neighbour.Height;
            var box = new BombJunction
            {
                NodeId = neighbour.Id, Cables = passing,
                X = jc - bw / 2f, Y = neighbour.OffsetY + neighbour.Height / 2f - bh / 2f, W = bw, H = bh,
            };
            int n = passing.Length;
            int[] perm = n == 2 ? new[] { 1, 0 } : rng.Next(2) == 0 ? new[] { 1, 2, 0 } : new[] { 2, 0, 1 };   // nobody keeps their colour
            float ucx = g.BombX + jdx;   // box centre, unwrapped like the routes
            var inY = Enumerable.Range(0, n).Select(k => box.Y + (k + 1) * bh / (n + 1)).ToArray();
            var outY = Enumerable.Range(0, n).Select(k => box.Y + (perm[k] + 1) * bh / (n + 1)).ToArray();
            return new JunctionPlan(box, ucx - dir * bw / 2f, ucx + dir * bw / 2f, inY, outY, perm);
        }
        return null;
    }

    /// <summary>The seat next to <paramref name="node"/> along x (same row band), wrapping on a Ring.</summary>
    private static NodeLayout? Neighbour(SeatMapLayoutResult layout, NodeLayout node, int dir)
    {
        var row = layout.Nodes.Where(n => n.Id != node.Id && Math.Abs(n.OffsetY - node.OffsetY) < node.Height / 2f).ToList();
        var ahead = dir > 0
            ? row.Where(n => n.OffsetX > node.OffsetX).OrderBy(n => n.OffsetX).FirstOrDefault()
            : row.Where(n => n.OffsetX < node.OffsetX).OrderByDescending(n => n.OffsetX).FirstOrDefault();
        if (ahead != null || !layout.Wraps) return ahead;
        return dir > 0 ? row.OrderBy(n => n.OffsetX).FirstOrDefault() : row.OrderByDescending(n => n.OffsetX).FirstOrDefault();
    }

    // ── routes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A cable from (from) to (to) along the seat chain: one point every 30 canvas units, y wiggling (three seeded sines,
    /// anchored at both ends) and clamped into the vertical extent of the node under each point. On a wrapping canvas
    /// the shorter way round; x stays unwrapped.
    /// </summary>
    public static float[] Route(SeatMapLayoutResult layout, float fromX, float fromY, float toX, float toY, Random rng)
    {
        float canvasW = layout.CanvasWidth;
        if (layout.Wraps && canvasW > 0 && Math.Abs(toX - fromX) > canvasW / 2f) toX -= Math.Sign(toX - fromX) * canvasW;
        float length = Math.Abs(toX - fromX);
        int count = Math.Max(2, (int)Math.Ceiling(length / PointSpacing) + 1);
        float nodeH = layout.Nodes.Count > 0 ? layout.Nodes.Max(n => n.Height) : 1080f;
        float cycles = Math.Max(1f, length / Math.Max(1f, nodeH * 1.5f));
        var (a, f, phi) = BombRiddles.Wiggle(rng, 0.25f * nodeH, 0.5f * cycles, 1.5f * cycles);

        var points = new float[count * 2];
        NodeLayout? band = null;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)(count - 1);
            float x = fromX + (toX - fromX) * t;
            float baseY = fromY + (toY - fromY) * t;
            float y = baseY + MathF.Sin(MathF.PI * t) * BombRiddles.Noise(a, f, phi, t);
            band = NodeAt(layout, x, baseY) ?? band;
            if (band != null) y = Math.Clamp(y, band.OffsetY + 0.05f * band.Height, band.OffsetY + 0.95f * band.Height);
            points[2 * i] = x;
            points[2 * i + 1] = y;
        }
        return points;
    }

    /// <summary>
    /// The node whose slice holds canvas x (wrapped on a Ring) — of several (Parallel rows share x), the one nearest
    /// vertically to <paramref name="y"/>; null in a bezel / distance gap.
    /// </summary>
    private static NodeLayout? NodeAt(SeatMapLayoutResult layout, float x, float y)
    {
        if (layout.Wraps && layout.CanvasWidth > 0) x = NodeMapping.PositiveModulo(x, layout.CanvasWidth);
        NodeLayout? best = null;
        float bestDist = float.MaxValue;
        foreach (var n in layout.Nodes)
        {
            if (x < n.OffsetX || x >= n.OffsetX + n.Width) continue;
            float dist = y < n.OffsetY ? n.OffsetY - y : y > n.OffsetY + n.Height ? y - n.OffsetY - n.Height : 0f;
            if (dist < bestDist) { bestDist = dist; best = n; }
        }
        return best;
    }

    /// <summary>Three end seats ≠ the bomb: far ones (≥ 2 machines away) first, shuffled; round-robin when fewer than three.</summary>
    private static NodeLayout[] PickEnds(IReadOnlyList<NodeLayout> candidates, int bombIndex, bool wraps, Random rng)
    {
        int n = candidates.Count;
        int Distance(int i) { int d = Math.Abs(i - bombIndex); return wraps ? Math.Min(d, n - d) : d; }
        var others = Enumerable.Range(0, n).Where(i => i != bombIndex).ToList();
        var ordered = others.Where(i => Distance(i) >= 2).OrderBy(_ => rng.Next())
            .Concat(others.Where(i => Distance(i) < 2).OrderBy(_ => rng.Next()))
            .Select(i => candidates[i]).ToList();
        return Enumerable.Range(0, 3).Select(i => ordered[i % ordered.Count]).ToArray();
    }
}

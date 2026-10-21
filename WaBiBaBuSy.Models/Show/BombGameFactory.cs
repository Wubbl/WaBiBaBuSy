using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Show;

/// <summary>Everything the server knows when BiBaBo starts.</summary>
/// <param name="Layout">The room's seat-map canvas (the stand-in or running scene's layout).</param>
/// <param name="TargetNodeIds">The Events strip target; null = every seat.</param>
/// <param name="IsBusy">A window covers this node's monitor (design §8.1).</param>
/// <param name="GroupByMachine">False: every monitor is a seat (test runs on a single machine).</param>
public sealed record BombGameRequest(SeatMapLayoutResult Layout, IReadOnlyCollection<string>? TargetNodeIds, Func<string, bool> IsBusy,
    int Seed, string GameId, int TimerMs = 300_000, bool GroupByMachine = true);

/// <summary>
/// Builds a BiBaBo game (design §4): bomb seat, roulette, three cable routes along the seat chain, cards and riddles.
/// Pure and seeded; runs on the server only — the players get the result. The live cable is returned separately and
/// never stored in the game.
/// </summary>
public static class BombGameFactory
{
    public static readonly string[] CableColors = { "#E53935", "#43A047", "#1E88E5" };

    private const float PointSpacing = 30f;
    private const float BombHeight = 0.35f;     // of the bomb node's height
    private const float TerminalSpread = 0.12f; // × BombSize between terminals
    private const float CardHeight = 0.55f;     // of the end node's height
    private const float CardAspect = 1.4f;

    public static (BombGame? Game, int LiveCable) Create(BombGameRequest r, out string? error)
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
            return (null, -1);
        }

        var rng = new Random(r.Seed);
        int bombIndex = rng.Next(candidates.Count);
        var bomb = candidates[bombIndex];
        var ends = PickEnds(candidates, bombIndex, r.Layout.Wraps, rng);
        int live = rng.Next(3);

        var game = new BombGame
        {
            GameId = r.GameId,
            BombNodeId = bomb.Id,
            BombOrder = bomb.Order,
            BombX = bomb.OffsetX + bomb.Width / 2f,
            BombY = bomb.OffsetY + bomb.Height / 2f,
            BombSize = BombHeight * bomb.Height,
            TimerMs = r.TimerMs,
        };
        int hops = 8 + rng.Next(5);
        game.RouletteOrders = Enumerable.Range(0, hops - 1).Select(_ => candidates[rng.Next(candidates.Count)].Order).Append(bomb.Order).ToArray();

        float canvasW = r.Layout.CanvasWidth;
        for (int i = 0; i < 3; i++)
        {
            var end = ends[i];
            int k = ends.Count(e => e.Id == end.Id), j = ends.Take(i).Count(e => e.Id == end.Id);
            float cellW = end.Width / (float)k;
            float h = CardHeight * end.Height, w = Math.Min(CardAspect * h, 0.9f * cellW);
            h = w / CardAspect;
            var cable = new BombCable
            {
                ColorHex = CableColors[i],
                EndNodeId = end.Id,
                EndOrder = end.Order,
                CardX = end.OffsetX + cellW * (j + 0.5f) - w / 2f,
                CardY = end.OffsetY + end.Height / 2f - h / 2f,
                CardW = w,
                CardH = h,
                Riddle = rng.Next(2) == 0 ? BombRiddles.Math(rng, i == live) : BombRiddles.Tangle(rng, i == live),
            };

            float dx = cable.CardX + w / 2f - game.BombX;
            if (r.Layout.Wraps && Math.Abs(dx) > canvasW / 2f) dx -= Math.Sign(dx) * canvasW;
            bool right = dx >= 0;
            float shift = game.BombX + dx - (cable.CardX + w / 2f);   // 0, or ±canvasW across the seam
            float fromX = game.BombX + (right ? 1 : -1) * game.BombSize / 2f;
            float fromY = game.BombY + (i - 1) * TerminalSpread * game.BombSize;
            float toX = (right ? cable.CardX : cable.CardX + w) + shift;
            cable.Points = Route(r.Layout, fromX, fromY, toX, cable.CardY + h / 2f, rng);
            game.Cables.Add(cable);
        }
        return (game, live);
    }

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

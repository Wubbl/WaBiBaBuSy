using System.Globalization;
using System.Text;

namespace WaBiBaBuSy.Models.Show;

/// <summary>
/// The riddle cards of BiBaBo (design §4.1, §14.1). Every card yields a digit — the term's result, the missing number,
/// the exit reached, the lit lamps, the mirrored clock's hour, a dot count; what the digit means comes from the game's
/// rule. Server-side, seeded by the caller's Random; the value is never stored in the card.
/// </summary>
public static class BombRiddles
{
    private const int StrandPoints = 24;
    private static readonly float[] Slots = { 0.2f, 0.5f, 0.8f };
    private static readonly string[] GateOps = { "AND", "OR", "XOR", "NAND", "NOR" };
    private static readonly string[] DotPaletteHex = { "#E53935", "#C2185B", "#F4511E", "#EC407A" };
    private static readonly string[] DotPaletteNames = { "RED", "CRIMSON", "ORANGE", "PINK" };

    public static (int Min, int Max) Range(RiddleKind kind) => kind switch
    {
        RiddleKind.Logic => (0, 7),
        RiddleKind.Clock => (1, 9),
        _ => (0, 9),
    };

    public static BombRiddle Make(RiddleKind kind, int value, Random rng)
    {
        var (min, max) = Range(kind);
        if (value < min || value > max) throw new ArgumentOutOfRangeException(nameof(value), $"{kind} yields {min}..{max}");
        return kind switch
        {
            RiddleKind.Math => new BombRiddle { Kind = kind, Formula = MathTerm(rng, value) },
            RiddleKind.Sequence => Sequence(rng, value),
            RiddleKind.Tangle => Tangle(rng, value),
            RiddleKind.Maze => Maze(rng, value),
            RiddleKind.Logic => Logic(rng, value),
            RiddleKind.Clock => new BombRiddle { Kind = kind, ClockHour = value, ClockMinute = 5 * rng.Next(10) },
            _ => Dots(rng, value),
        };
    }

    /// <summary>The card's instruction line (what "result" means on this card).</summary>
    public static string Subtitle(BombRiddle r) => r.Kind switch
    {
        RiddleKind.Math => "result = the value of the term",
        RiddleKind.Sequence => "result = the missing number",
        RiddleKind.Tangle => "result = the number your cable reaches",
        RiddleKind.Maze => "result = the only exit you can reach",
        RiddleKind.Logic => "result = add up the lamps that light (4 2 1)",
        RiddleKind.Clock => "result = the real hour (you see it in a MIRROR)",
        _ => $"result = how many {r.DotTargetName} dots",
    };

    // ── Math: a scary term with exactly this value ──────────────────────────

    private static string MathTerm(Random rng, int v)
    {
        switch (rng.Next(7))
        {
            case 0:   // power of zero
            {
                int a, b, c;
                do { a = rng.Next(7, 20); b = rng.Next(2, 10); c = rng.Next(2, 10); } while (a * b == c * c);
                int d = rng.Next(3, 100);
                return Adjust($"({a}·{b} − {c}²)⁽{Sup(d)}⁻{Sup(d)}⁾", 1, v);
            }
            case 1:   // factorials: m! ÷ (m−1)! = m
            {
                int m = rng.Next(Math.Max(v + 1, 3), 13);
                return Adjust($"{m}! ÷ {m - 1}!", m, v);
            }
            case 2:   // a hidden zero factor
            {
                int a = rng.Next(3, 10), b = rng.Next(3, 10), c = a * b - 1;
                return Adjust($"({c} − {a}·{b} + 1) · {rng.Next(1000, 1_000_000)}", 0, v);
            }
            case 3:   // −1 to a big power
            {
                bool even = v == 0 ? false : rng.Next(2) == 0;
                int e = rng.Next(500, 1500) * 2 + (even ? 0 : 1);
                return Adjust($"(−1){Sup(e)}", even ? 1 : -1, v);
            }
            case 4:   // order of operations
            {
                int y = rng.Next(2, 10), z = rng.Next(2, 10), w = rng.Next(1, Math.Max(2, v + y * z));
                return $"{v + y * z - w} − {y} · {z} + {w}";
            }
            case 5:   // difference of squares
            {
                int n = rng.Next(11, 100);
                return rng.Next(2) == 0 ? Adjust($"{n + 1}² − {n}² − {2 * n}", 1, v) : Adjust($"{n - 1} · {n + 1} − {n}²", -1, v);
            }
            default:  // reciprocals
            {
                int p = rng.Next(2, 10), q;
                do { q = rng.Next(2, 10); } while (q == p);
                return v >= 2 ? $"({p}/{q}) · ({q}/{p}) · {v}" : Adjust($"({p}/{q}) · ({q}/{p})", 1, v);
            }
        }
    }

    /// <summary><paramref name="term"/> (worth <paramref name="worth"/>) turned into one worth <paramref name="v"/>: "+ k" / "− k".</summary>
    private static string Adjust(string term, int worth, int v) =>
        v == worth ? term : v > worth ? $"{term} + {v - worth}" : $"{term} − {worth - v}";

    // ── Sequence: the missing next number ───────────────────────────────────

    private static BombRiddle Sequence(Random rng, int v)
    {
        int[] terms;
        int kind = v == 0 ? 1 + rng.Next(3) : rng.Next(4);
        switch (kind)
        {
            case 0:   // halving: v·32, v·16, …, v·2, ?
                terms = Enumerable.Range(1, 5).Select(k => v << (6 - k)).ToArray();
                break;
            case 1:   // arithmetic, counting down to v
            {
                int d = rng.Next(2, 10);
                terms = Enumerable.Range(1, 5).Select(k => v + (6 - k) * d).ToArray();
                break;
            }
            case 2:   // growing differences: v + m·T(k), T = 15, 10, 6, 3, 1
            {
                int m = rng.Next(1, 4);
                terms = new[] { 15, 10, 6, 3, 1 }.Select(t => v + m * t).ToArray();
                break;
            }
            default:  // interleaved: x counts down to v, y counts up; the ? is the next x
            {
                int d = rng.Next(2, 6), b = rng.Next(10, 40), e = rng.Next(3, 9);
                terms = new[] { v + 3 * d, b, v + 2 * d, b + e, v + d, b + 2 * e };
                break;
            }
        }
        return new BombRiddle
        {
            Kind = RiddleKind.Sequence,
            SequenceTerms = terms,
            Formula = string.Join(", ", terms.Select(t => t.ToString(CultureInfo.InvariantCulture))) + ", ?",
        };
    }

    // ── Tangle: strand 0 (this cable) reaches the exit labelled v ───────────

    private static BombRiddle Tangle(Random rng, int v)
    {
        int[] exits = new[] { 0, 1, 2 }.OrderBy(_ => rng.Next()).ToArray();   // strand s ends at exit exits[s]
        int[] labels = ThreeLabels(rng, v, exits[0]);
        float[] exitY = Slots.OrderBy(_ => rng.Next()).ToArray();
        float[] entryY = rng.Next(2) == 0 ? new[] { 0.5f, 0.2f, 0.8f } : new[] { 0.5f, 0.8f, 0.2f };
        var strands = new float[3][];
        for (int s = 0; s < 3; s++)
        {
            var (a, f, phi) = Wiggle(rng, 0.3f, 2, 4);
            strands[s] = new float[StrandPoints];
            for (int i = 0; i < StrandPoints; i++)
            {
                float t = i / (float)(StrandPoints - 1);
                float y = entryY[s] + (exitY[exits[s]] - entryY[s]) * t + MathF.Sin(MathF.PI * t) * Noise(a, f, phi, t);
                strands[s][i] = Math.Clamp(y, 0.06f, 0.94f);
            }
        }
        return new BombRiddle { Kind = RiddleKind.Tangle, Strands = strands, StrandExits = exits, ExitLabels = labels };
    }

    /// <summary>Three distinct digits with <paramref name="v"/> at index <paramref name="at"/>.</summary>
    private static int[] ThreeLabels(Random rng, int v, int at)
    {
        var others = Enumerable.Range(0, 10).Where(d => d != v).OrderBy(_ => rng.Next()).Take(2).ToArray();
        var labels = new int[3];
        for (int e = 0, o = 0; e < 3; e++) labels[e] = e == at ? v : others[o++];
        return labels;
    }

    // ── Labyrinth: only the exit labelled v is reachable ────────────────────

    private const int MazeW = 9, MazeH = 7;

    private static BombRiddle Maze(Random rng, int v)
    {
        while (true)
        {
            var walls = PerfectMaze(rng);
            int entryRow = MazeH / 2;
            int[] rows = Enumerable.Range(0, MazeH).OrderBy(_ => rng.Next()).Take(3).OrderBy(r => r).ToArray();
            int target = rng.Next(3);
            var parent = Parents(walls, entryRow * MazeW);
            var targetPath = PathTo(parent, rows[target] * MazeW + MazeW - 1);
            bool ok = true;
            for (int e = 0; e < 3 && ok; e++)
            {
                if (e == target) continue;
                int cell = rows[e] * MazeW + MazeW - 1;
                if (targetPath.Contains(cell)) { ok = false; break; }   // the other exit lies on the way: try another maze
                var path = PathTo(parent, cell);
                int i = 0;
                while (i < path.Count && targetPath.Contains(path[i])) i++;   // first cell off the target's way
                if (i == 0 || i >= path.Count) { ok = false; break; }
                Close(walls, path[i - 1], path[i]);
                parent = Parents(walls, entryRow * MazeW);
            }
            if (!ok) continue;
            var reach = Parents(walls, entryRow * MazeW);
            if (Enumerable.Range(0, 3).Count(e => reach.ContainsKey(rows[e] * MazeW + MazeW - 1)) != 1) continue;
            return new BombRiddle
            {
                Kind = RiddleKind.Maze, MazeW = MazeW, MazeH = MazeH, MazeWalls = walls, MazeEntryRow = entryRow,
                ExitRows = rows, ExitLabels = ThreeLabels(rng, v, target),
            };
        }
    }

    /// <summary>A spanning-tree maze (randomised depth-first): bit 1 = wall on the right, bit 2 = wall below.</summary>
    private static int[] PerfectMaze(Random rng)
    {
        var walls = Enumerable.Repeat(3, MazeW * MazeH).ToArray();
        var seen = new bool[MazeW * MazeH];
        var stack = new Stack<int>();
        stack.Push(0);
        seen[0] = true;
        while (stack.Count > 0)
        {
            int c = stack.Peek(), x = c % MazeW, y = c / MazeW;
            var next = new List<int>(4);
            if (x + 1 < MazeW && !seen[c + 1]) next.Add(c + 1);
            if (x > 0 && !seen[c - 1]) next.Add(c - 1);
            if (y + 1 < MazeH && !seen[c + MazeW]) next.Add(c + MazeW);
            if (y > 0 && !seen[c - MazeW]) next.Add(c - MazeW);
            if (next.Count == 0) { stack.Pop(); continue; }
            int n = next[rng.Next(next.Count)];
            Open(walls, c, n);
            seen[n] = true;
            stack.Push(n);
        }
        return walls;
    }

    private static void Open(int[] walls, int a, int b) => SetWall(walls, a, b, open: true);
    private static void Close(int[] walls, int a, int b) => SetWall(walls, a, b, open: false);

    private static void SetWall(int[] walls, int a, int b, bool open)
    {
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        int bit = hi - lo == 1 ? 1 : 2;
        walls[lo] = open ? walls[lo] & ~bit : walls[lo] | bit;
    }

    /// <summary>BFS tree from <paramref name="start"/>: cell → previous cell (start → −1).</summary>
    private static Dictionary<int, int> Parents(int[] walls, int start)
    {
        var parent = new Dictionary<int, int> { [start] = -1 };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int c = queue.Dequeue(), x = c % MazeW, y = c / MazeW;
            void Go(int n) { if (parent.TryAdd(n, c)) queue.Enqueue(n); }
            if (x + 1 < MazeW && (walls[c] & 1) == 0) Go(c + 1);
            if (y + 1 < MazeH && (walls[c] & 2) == 0) Go(c + MazeW);
            if (x > 0 && (walls[c - 1] & 1) == 0) Go(c - 1);
            if (y > 0 && (walls[c - MazeW] & 2) == 0) Go(c - MazeW);
        }
        return parent;
    }

    private static List<int> PathTo(Dictionary<int, int> parent, int cell)
    {
        var path = new List<int>();
        for (int c = cell; c != -1 && parent.ContainsKey(c); c = parent[c]) path.Add(c);
        path.Reverse();
        return path;
    }

    // ── Logic gates: lamps 4 2 1 ────────────────────────────────────────────

    private static BombRiddle Logic(Random rng, int v)
    {
        while (true)
        {
            int[] inputs = { rng.Next(2), rng.Next(2), rng.Next(2) };
            var gates = new string[3];
            int value = 0;
            for (int k = 0; k < 3; k++)
            {
                int a = rng.Next(3), b;
                do { b = rng.Next(3); } while (b == a);
                if (a > b) (a, b) = (b, a);
                string op = GateOps[rng.Next(GateOps.Length)];
                gates[k] = $"{op} {"ABC"[a]} {"ABC"[b]}";
                int x = inputs[a], y = inputs[b];
                int o = op switch { "AND" => x & y, "OR" => x | y, "XOR" => x ^ y, "NAND" => 1 - (x & y), _ => 1 - (x | y) };
                value += o * (4 >> k);
            }
            if (value == v) return new BombRiddle { Kind = RiddleKind.Logic, LogicInputs = inputs, LogicGates = gates };
        }
    }

    // ── Dots: count one of four similar colours ─────────────────────────────

    private static BombRiddle Dots(Random rng, int v)
    {
        int total = rng.Next(26, 35), target = rng.Next(DotPaletteHex.Length);
        var points = new List<(float X, float Y)>();
        while (points.Count < total)
        {
            float x = 0.05f + 0.9f * (float)rng.NextDouble(), y = 0.05f + 0.9f * (float)rng.NextDouble();
            if (points.All(p => MathF.Abs(p.X - x) + MathF.Abs(p.Y - y) > 0.09f)) points.Add((x, y));
        }
        var colors = new int[total];
        var order = Enumerable.Range(0, total).OrderBy(_ => rng.Next()).ToArray();
        for (int i = 0; i < total; i++)
        {
            int c;
            do { c = rng.Next(DotPaletteHex.Length); } while (c == target);
            colors[order[i]] = i < v ? target : c;
        }
        return new BombRiddle
        {
            Kind = RiddleKind.Dots,
            Dots = points.SelectMany(p => new[] { p.X, p.Y }).ToArray(),
            DotColors = colors,
            DotPalette = DotPaletteHex,
            DotTarget = target,
            DotTargetName = DotPaletteNames[target],
        };
    }

    // ── shared ──────────────────────────────────────────────────────────────

    /// <summary>Three summed sines (amplitudes add up to <paramref name="amplitude"/>), frequencies in cycles over t ∈ [0, 1].</summary>
    internal static (float[] A, float[] F, float[] Phi) Wiggle(Random rng, float amplitude, float minF, float maxF)
    {
        var a = new float[3];
        var f = new float[3];
        var phi = new float[3];
        for (int k = 0; k < 3; k++)
        {
            a[k] = amplitude / 3f * (0.5f + 0.5f * (float)rng.NextDouble());
            f[k] = minF + (maxF - minF) * (float)rng.NextDouble();
            phi[k] = 2f * MathF.PI * (float)rng.NextDouble();
        }
        return (a, f, phi);
    }

    internal static float Noise(float[] a, float[] f, float[] phi, float t)
    {
        float y = 0;
        for (int k = 0; k < a.Length; k++) y += a[k] * MathF.Sin(2f * MathF.PI * f[k] * t + phi[k]);
        return y;
    }

    private static string Sup(int n)
    {
        const string digits = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        var sb = new StringBuilder();
        foreach (char ch in n.ToString(CultureInfo.InvariantCulture)) sb.Append(ch == '-' ? '⁻' : digits[ch - '0']);
        return sb.ToString();
    }
}

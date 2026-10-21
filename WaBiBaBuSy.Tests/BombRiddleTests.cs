using System.Globalization;
using System.Text;
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>BiBaBo riddles: every card, solved independently, yields exactly the value it was made for.</summary>
public class BombRiddleTests
{
    private const int SeedsPerValue = 30;

    private static IEnumerable<(int Value, int Seed, BombRiddle Riddle)> All(RiddleKind kind)
    {
        var (min, max) = BombRiddles.Range(kind);
        for (int v = min; v <= max; v++)
            for (int seed = 0; seed < SeedsPerValue; seed++)
                yield return (v, seed, BombRiddles.Make(kind, v, new Random(seed * 1000 + v)));
    }

    [Theory]
    [InlineData(RiddleKind.Math), InlineData(RiddleKind.Sequence), InlineData(RiddleKind.Tangle), InlineData(RiddleKind.Maze),
     InlineData(RiddleKind.Logic), InlineData(RiddleKind.Clock), InlineData(RiddleKind.Dots)]
    public void EveryKind_SolvesToItsValue(RiddleKind kind)
    {
        foreach (var (v, seed, r) in All(kind))
        {
            Assert.Equal(kind, r.Kind);
            Assert.True(TestRiddle.Value(r) == v, $"{kind} seed {seed} value {v}: solver says {TestRiddle.Value(r)} ({r.Formula})");
            Assert.False(string.IsNullOrWhiteSpace(BombRiddles.Subtitle(r)));
        }
    }

    [Fact]
    public void Ranges()
    {
        Assert.Equal((0, 9), BombRiddles.Range(RiddleKind.Math));
        Assert.Equal((0, 7), BombRiddles.Range(RiddleKind.Logic));
        Assert.Equal((1, 9), BombRiddles.Range(RiddleKind.Clock));
    }

    [Fact]
    public void Math_UsesManyShapes()
    {
        var shapes = new HashSet<string>();
        for (int seed = 0; seed < 300; seed++)
            shapes.Add(Shape(BombRiddles.Make(RiddleKind.Math, seed % 10, new Random(seed)).Formula!));
        Assert.True(shapes.Count >= 7, string.Join(" | ", shapes));
    }

    [Fact]
    public void Maze_HasExactlyOneReachableExit_AndDistinctLabels()
    {
        foreach (var (_, _, r) in All(RiddleKind.Maze))
        {
            Assert.Single(TestRiddle.ReachableExits(r));
            Assert.Equal(3, r.ExitLabels!.Distinct().Count());
            Assert.Equal(3, r.ExitRows!.Distinct().Count());
        }
    }

    [Fact]
    public void Tangle_ExitsArePermuted_LabelsDistinct()
    {
        foreach (var (_, _, r) in All(RiddleKind.Tangle))
        {
            Assert.Equal(new[] { 0, 1, 2 }, r.StrandExits!.OrderBy(e => e));
            Assert.Equal(3, r.ExitLabels!.Distinct().Count());
            Assert.All(r.Strands!, s => Assert.All(s, y => Assert.InRange(y, 0f, 1f)));
        }
    }

    [Fact]
    public void Dots_AreApart_AndTheTargetColourIsNamed()
    {
        foreach (var (_, _, r) in All(RiddleKind.Dots))
        {
            var d = r.Dots!;
            for (int i = 0; i < d.Length / 2; i++)
                for (int j = i + 1; j < d.Length / 2; j++)
                    Assert.True(MathF.Abs(d[2 * i] - d[2 * j]) + MathF.Abs(d[2 * i + 1] - d[2 * j + 1]) > 0.05f, "dots overlap");
            Assert.Contains(r.DotTargetName!, BombRiddles.Subtitle(r));
            // the four colours must be told apart: hues at least 25° apart
            var hues = r.DotPalette!.Select(Hue).OrderBy(h => h).ToArray();
            for (int k = 0; k < hues.Length; k++)
            {
                double gap = k + 1 < hues.Length ? hues[k + 1] - hues[k] : hues[0] + 360 - hues[^1];
                Assert.True(gap >= 25, $"palette hues too close: {string.Join(", ", hues.Select(h => h.ToString("0")))}");
            }
            Assert.InRange(d.Length / 2, 20, 40);
        }
    }

    private static double Hue(string hex)
    {
        var c = System.Drawing.ColorTranslator.FromHtml(hex);
        return c.GetHue();
    }

    [Fact]
    public void Clock_MinutesStayClearOfTheNextHour()
    {
        foreach (var (_, _, r) in All(RiddleKind.Clock))
            Assert.True(r.ClockMinute % 5 == 0 && r.ClockMinute <= 45);
    }

    /// <summary>The formula with numbers and superscripts removed, so templates can be told apart.</summary>
    private static string Shape(string formula)
    {
        var sb = new StringBuilder();
        foreach (char c in TestFormula.Normalize(formula))
            if (!char.IsDigit(c) && !"⁰¹²³⁴⁵⁶⁷⁸⁹".Contains(c) && (sb.Length == 0 || sb[^1] != c)) sb.Append(c);
        return sb.ToString();
    }
}

/// <summary>Independent solvers for the riddle cards (what a person at the seat works out).</summary>
internal static class TestRiddle
{
    public static int Value(BombRiddle r) => r.Kind switch
    {
        RiddleKind.Math => (int)Math.Round(TestFormula.Evaluate(r.Formula!)) is var m && Math.Abs(TestFormula.Evaluate(r.Formula!) - m) < 1e-9 ? m : int.MinValue,
        RiddleKind.Sequence => Sequence(r),
        RiddleKind.Tangle => r.ExitLabels![r.StrandExits![0]],
        RiddleKind.Maze => ReachableExits(r) is [var only] ? r.ExitLabels![only] : int.MinValue,
        RiddleKind.Logic => Logic(r),
        RiddleKind.Clock => r.ClockHour,
        RiddleKind.Dots => r.DotColors!.Count(c => c == r.DotTarget),
        _ => int.MinValue,
    };

    /// <summary>The next term if the shown terms follow one of the patterns people know; MinValue otherwise.</summary>
    private static int Sequence(BombRiddle r)
    {
        var t = r.SequenceTerms!;
        Assert.Equal(string.Join(", ", t) + ", ?", r.Formula);
        int n = t.Length;
        if (t.Zip(t.Skip(1)).All(p => p.First == 2 * p.Second)) return t[^1] / 2;                       // halving
        var d = t.Zip(t.Skip(1), (a, b) => b - a).ToArray();
        if (d.Distinct().Count() == 1) return t[^1] + d[0];                                                // arithmetic
        var dd = d.Zip(d.Skip(1), (a, b) => b - a).ToArray();
        if (dd.Distinct().Count() == 1) return t[^1] + d[^1] + dd[0];                                      // growing differences
        if (n % 2 == 1)                                                                                     // interleaved: odd positions arithmetic
        {
            var odd = t.Where((_, i) => i % 2 == 0).ToArray();
            var even = t.Where((_, i) => i % 2 == 1).ToArray();
            var od = odd.Zip(odd.Skip(1), (a, b) => b - a).Distinct().ToArray();
            var ed = even.Zip(even.Skip(1), (a, b) => b - a).Distinct().ToArray();
            if (od.Length == 1 && ed.Length == 1) return even[^1] + ed[0];
        }
        else
        {
            var odd = t.Where((_, i) => i % 2 == 0).ToArray();
            var even = t.Where((_, i) => i % 2 == 1).ToArray();
            var od = odd.Zip(odd.Skip(1), (a, b) => b - a).Distinct().ToArray();
            var ed = even.Zip(even.Skip(1), (a, b) => b - a).Distinct().ToArray();
            if (od.Length == 1 && ed.Length == 1) return odd[^1] + od[0];
        }
        return int.MinValue;
    }

    private static int Logic(BombRiddle r)
    {
        int In(string name) => r.LogicInputs!["ABC".IndexOf(name[0])];
        int value = 0;
        for (int k = 0; k < 3; k++)
        {
            var parts = r.LogicGates![k].Split(' ');
            int a = In(parts[1]), b = In(parts[2]);
            int o = parts[0] switch
            {
                "AND" => a & b, "OR" => a | b, "XOR" => a ^ b, "NAND" => 1 - (a & b), "NOR" => 1 - (a | b),
                _ => throw new InvalidOperationException(parts[0]),
            };
            value += o * (4 >> k);
        }
        return value;
    }

    /// <summary>Exits (indices into ExitRows) reachable from the entry, walking through open walls.</summary>
    public static List<int> ReachableExits(BombRiddle r)
    {
        int w = r.MazeW, h = r.MazeH;
        var walls = r.MazeWalls!;
        var seen = new bool[w * h];
        var queue = new Queue<int>();
        int start = r.MazeEntryRow * w;
        seen[start] = true;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int c = queue.Dequeue(), x = c % w, y = c / w;
            void Go(int nx, int ny) { int n = ny * w + nx; if (!seen[n]) { seen[n] = true; queue.Enqueue(n); } }
            if (x + 1 < w && (walls[c] & 1) == 0) Go(x + 1, y);
            if (y + 1 < h && (walls[c] & 2) == 0) Go(x, y + 1);
            if (x > 0 && (walls[c - 1] & 1) == 0) Go(x - 1, y);
            if (y > 0 && (walls[c - w] & 2) == 0) Go(x, y - 1);
        }
        return Enumerable.Range(0, r.ExitRows!.Length).Where(e => seen[r.ExitRows[e] * w + (w - 1)]).ToList();
    }
}

/// <summary>
/// Test-only evaluator for the riddle notation: digits, + − · ÷ / ( ) !, unary minus (binds looser than powers, so
/// −3² = −9), and exponents written as Unicode superscript groups (⁰-⁹ ⁽ ⁾ ⁺ ⁻).
/// </summary>
internal static class TestFormula
{
    private const string Sup = "⁰¹²³⁴⁵⁶⁷⁸⁹⁽⁾⁺⁻";
    private const string Plain = "0123456789()+-";

    public static string Normalize(string s) => s.Replace(" ", "").Replace("−", "-").Replace("·", "*").Replace("÷", "/");

    public static double Evaluate(string formula)
    {
        var p = new Parser(Normalize(formula));
        double v = p.Expr();
        if (p.Pos != p.Text.Length) throw new FormatException($"Unparsed rest in {formula} at {p.Pos}");
        return v;
    }

    private sealed class Parser(string text)
    {
        public string Text { get; } = text;
        public int Pos;

        private char Peek => Pos < Text.Length ? Text[Pos] : '\0';

        public double Expr()
        {
            double v = Term();
            while (Peek is '+' or '-')
                v = Text[Pos++] == '+' ? v + Term() : v - Term();
            return v;
        }

        private double Term()
        {
            double v = Unary();
            while (Peek is '*' or '/')
                v = Text[Pos++] == '*' ? v * Unary() : v / Unary();
            return v;
        }

        private double Unary()
        {
            if (Peek != '-') return Power();
            Pos++;
            return -Unary();
        }

        private double Power()
        {
            double b = Postfix();
            if (Pos < Text.Length && Sup.Contains(Text[Pos]))
            {
                var sb = new StringBuilder();
                while (Pos < Text.Length && Sup.Contains(Text[Pos])) sb.Append(Plain[Sup.IndexOf(Text[Pos++])]);
                return Math.Pow(b, new Parser(sb.ToString()).Expr());
            }
            return b;
        }

        private double Postfix()
        {
            double v = Primary();
            while (Peek == '!') { Pos++; v = Factorial(v); }
            return v;
        }

        private double Primary()
        {
            if (Peek == '(')
            {
                Pos++;
                double v = Expr();
                if (Peek != ')') throw new FormatException($"Missing ) in {Text}");
                Pos++;
                return v;
            }
            int start = Pos;
            while (char.IsDigit(Peek)) Pos++;
            if (start == Pos) throw new FormatException($"Number expected in {Text} at {Pos}");
            return double.Parse(Text[start..Pos], CultureInfo.InvariantCulture);
        }

        private static double Factorial(double n)
        {
            double r = 1;
            for (int i = 2; i <= (int)n; i++) r *= i;
            return r;
        }
    }
}

using System.Globalization;
using System.Text;
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>BiBaBo riddles: every math card evaluates to its intended value; tangles end where they say.</summary>
public class BombRiddleTests
{
    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 200).Select(i => new object[] { i });

    [Theory, MemberData(nameof(Seeds))]
    public void Math_LiveEvaluatesToOne(int seed)
    {
        var r = BombRiddles.Math(new Random(seed), live: true);
        Assert.Equal(RiddleKind.Math, r.Kind);
        Assert.True(Math.Abs(TestFormula.Evaluate(r.Formula!) - 1) < 1e-9, $"{r.Formula} = {TestFormula.Evaluate(r.Formula!)}");
    }

    [Theory, MemberData(nameof(Seeds))]
    public void Math_DeadNeverOne(int seed)
    {
        var r = BombRiddles.Math(new Random(seed), live: false);
        double v = TestFormula.Evaluate(r.Formula!);
        Assert.True(Math.Abs(v - 1) > 1e-6, $"{r.Formula} = {v}");
    }

    [Fact]
    public void Math_UsesEveryTemplate()
    {
        var shapes = new HashSet<string>();
        for (int seed = 0; seed < 500; seed++)
            foreach (bool live in new[] { true, false })
                shapes.Add(Shape(BombRiddles.Math(new Random(seed), live).Formula!));
        Assert.True(shapes.Count >= 7, string.Join(" | ", shapes));
    }

    [Theory, MemberData(nameof(Seeds))]
    public void Tangle_StrandZeroEndsAtDetonatorIffLive(int seed)
    {
        foreach (bool live in new[] { true, false })
        {
            var r = BombRiddles.Tangle(new Random(seed), live);
            Assert.Equal(RiddleKind.Tangle, r.Kind);
            Assert.Equal(live, r.StrandExits![0] == 0);
        }
    }

    [Fact]
    public void Tangle_EachExitUsedOnce()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var r = BombRiddles.Tangle(new Random(seed), seed % 2 == 0);
            Assert.Equal(3, r.Strands!.Length);
            Assert.Equal(new[] { 0, 1, 2 }, r.StrandExits!.OrderBy(e => e));
            Assert.All(r.Strands, s => Assert.All(s, v => Assert.InRange(v, 0f, 1f)));
            // strands that end at different exits end at different heights
            var endYs = r.Strands.Select(s => s[^1]).ToArray();
            Assert.Equal(3, endYs.Distinct().Count());
        }
    }

    /// <summary>The formula with numbers replaced, so templates can be told apart.</summary>
    private static string Shape(string formula)
    {
        var sb = new StringBuilder();
        foreach (char c in TestFormula.Normalize(formula))
            if (!char.IsDigit(c) && (sb.Length == 0 || sb[^1] != c)) sb.Append(c);
        return sb.ToString();
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

        private double Unary() => Peek == '-' ? (Pos++ > -1 ? -Unary() : 0) : Power();

        private double Power()
        {
            double b = Postfix();
            if (Pos < Text.Length && Sup.Contains(Text[Pos]))
            {
                var sb = new StringBuilder();
                while (Pos < Text.Length && Sup.Contains(Text[Pos])) sb.Append(Plain[Sup.IndexOf(Text[Pos++])]);
                var inner = new Parser(sb.ToString());
                double e = inner.Expr();
                return Math.Pow(b, e);
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

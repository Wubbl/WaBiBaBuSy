using System.Globalization;
using System.Text;

namespace WaBiBaBuSy.Models.Show;

/// <summary>
/// The riddle cards of BiBaBo (design §4.1). Math: a scary-looking term that is 1 for the live cable and not 1 for a
/// dead one (powers of zero, 0!, hidden zero factors, …; never ambiguous notation). Tangle: three crossing strands,
/// the cable's own strand ends at the DETONATOR only when it is live. Server-side, seeded by the caller's Random.
/// </summary>
public static class BombRiddles
{
    public const string MathRule = "LIVE ⇔ result = 1";
    public const string TangleRule = "LIVE ⇔ leads to the DETONATOR";

    private const int TemplateCount = 7;
    private const int StrandPoints = 24;
    private static readonly float[] Slots = { 0.2f, 0.5f, 0.8f };

    public static BombRiddle Math(Random rng, bool live) => new()
    {
        Kind = RiddleKind.Math,
        Formula = rng.Next(TemplateCount) switch
        {
            0 => PowerOfZero(rng, live),
            1 => ZeroFactorial(rng, live),
            2 => HiddenZero(rng, live),
            3 => SignPower(rng, live),
            4 => OrderOfOperations(rng, live),
            5 => DifferenceOfSquares(rng, live),
            _ => Reciprocal(rng, live),
        },
    };

    /// <summary>Strand 0 enters in the middle (where the cable arrives); exit 0 is the DETONATOR.</summary>
    public static BombRiddle Tangle(Random rng, bool live)
    {
        // Which exit each strand reaches: strand 0 → 0 when live, else 1 or 2; the others take what is left.
        int first = live ? 0 : 1 + rng.Next(2);
        var rest = new[] { 0, 1, 2 }.Where(e => e != first).OrderBy(_ => rng.Next()).ToArray();
        int[] exits = { first, rest[0], rest[1] };
        // Exit e sits at a shuffled slot; entries: strand 0 in the middle, 1 and 2 above / below (shuffled).
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
                strands[s][i] = System.Math.Clamp(y, 0.06f, 0.94f);
            }
        }
        return new BombRiddle { Kind = RiddleKind.Tangle, Strands = strands, StrandExits = exits };
    }

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

    // ── Math templates (live = 1, dead ≠ 1) ─────────────────────────────────

    private static string PowerOfZero(Random rng, bool live)
    {
        int a, b, c;
        do { a = rng.Next(7, 20); b = rng.Next(2, 10); c = rng.Next(2, 10); } while (a * b == c * c);
        int d = rng.Next(3, 100);
        string term = $"({a}·{b} − {c}²)⁽{Sup(d)}⁻{Sup(d)}⁾";
        return live ? term : $"{term} · 0{Sup(rng.Next(2, 10))}";
    }

    private static string ZeroFactorial(Random rng, bool live)
    {
        int n = rng.Next(20, 100), m = rng.Next(2, 10);
        return live ? $"0! · ({n} − {n - 1}){Sup(m)}" : $"0 · {m}! + 1{Sup(rng.Next(10, 100))} − 1";
    }

    private static string HiddenZero(Random rng, bool live)
    {
        int a = rng.Next(3, 10), b = rng.Next(3, 10), c = a * b - 1;
        return live ? $"({a}·{b} − {c}){Sup(rng.Next(100, 2027))}" : $"({c} − {a}·{b} + 1) · {rng.Next(1000, 1_000_000)}";
    }

    private static string SignPower(Random rng, bool live)
    {
        int e = rng.Next(500, 1500) * 2 + (live ? 0 : 1);
        return $"(−1){Sup(e)}";
    }

    private static string OrderOfOperations(Random rng, bool live)
    {
        while (true)
        {
            int y = rng.Next(2, 10), z = rng.Next(2, 10), w = rng.Next(1, y * z), x = 1 + y * z - w;
            if (live) return $"{x} − {y} · {z} + {w}";
            if ((x - y) * z + w != 1) return $"({x} − {y}) · {z} + {w}";
        }
    }

    private static string DifferenceOfSquares(Random rng, bool live)
    {
        int n = rng.Next(11, 100);
        return live ? $"{n + 1}² − {n}² − {2 * n}" : $"{n - 1} · {n + 1} − {n}²";
    }

    private static string Reciprocal(Random rng, bool live)
    {
        int p = rng.Next(2, 10), q;
        do { q = rng.Next(2, 10); } while (q == p);
        return live ? $"({p}/{q}) · ({q}/{p})" : $"({p}/{q}) ÷ ({q}/{p})";
    }

    private static string Sup(int n)
    {
        const string digits = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        var sb = new StringBuilder();
        foreach (char ch in n.ToString(CultureInfo.InvariantCulture)) sb.Append(ch == '-' ? '⁻' : digits[ch - '0']);
        return sb.ToString();
    }
}

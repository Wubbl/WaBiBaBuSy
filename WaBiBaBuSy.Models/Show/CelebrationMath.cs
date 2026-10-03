using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Show;

/// <summary>One confetti piece: top-left, size and rotation (degrees, about its center), color slot (0 = event color).</summary>
public readonly record struct CelebrationParticle(int Index, float X, float Y, float Width, float Height, float RotationDeg, int ColorIndex);

/// <summary>
/// Celebration (design §3.3): a seeded, closed-form confetti burst over the whole canvas. Particle i is launched from
/// below the bottom edge at x0 = (i + u)/N × CanvasW, rises ballistically to its apex, then drifts down slowly with a
/// sway. Particles are sorted by x0, so a node only evaluates the index range covering its slice.
/// </summary>
public static class CelebrationMath
{
    public const float PerThousandPx = 250f;
    public const int MinCount = 50;
    public const int MaxCount = 6000;
    /// <summary>× canvas height per s².</summary>
    public const float Gravity = 2.0f;
    /// <summary>× canvas height per s after the apex.</summary>
    public const float FallSpeed = 0.15f;
    /// <summary>Horizontal drift during the rise, ± units/s.</summary>
    public const float MaxDriftSpeed = 150f;
    public const float SwayAmplitude = 30f;
    public const double SwayHz = 0.8;
    public const float MaxSize = 18f;
    /// <summary>Longest rise: apex ≤ 0.95 H at g = 2 H/s² → ≤ 0.975 s.</summary>
    public const float MaxRiseS = 1.0f;
    /// <summary>How far a particle can end up from its x0 (drift + sway range + size).</summary>
    public const float Margin = MaxDriftSpeed * MaxRiseS + 2f * SwayAmplitude + MaxSize;
    public const int LaunchSpreadMs = 800;
    public static readonly string[] PartyColors = { "#FF4136", "#2ECC40", "#0074D9", "#FFDC00", "#F012BE" };

    public static int Count(int canvasWidth) =>
        Math.Clamp((int)Math.Ceiling(Math.Max(1, canvasWidth) / 1000.0 * PerThousandPx), MinCount, MaxCount);

    /// <summary>Color of a slot: 0 = the event color, 1..5 = the party colors.</summary>
    public static string ColorFor(ShowEvent evt, int colorIndex) =>
        colorIndex <= 0 ? evt.ColorHex : PartyColors[(colorIndex - 1) % PartyColors.Length];

    private static double U(int seed, int i, int k) => (uint)PatternLayout.Hash3(seed, i, k) / 4294967296.0;   // [0, 1)

    /// <summary>Particle <paramref name="i"/> of <paramref name="count"/> in virtual canvas coordinates; false before its launch.</summary>
    public static bool Particle(ShowEvent evt, int i, int count, int canvasW, int canvasH, long localMs, out CelebrationParticle p)
    {
        p = default;
        int seed = evt.Seed;
        double t = (localMs - U(seed, i, 3) * LaunchSpreadMs) / 1000.0;
        if (t < 0) return false;

        double h = Math.Max(1, canvasH);
        double x0 = (i + U(seed, i, 0)) / Math.Max(1, count) * canvasW;
        double y0 = h + 20.0;
        double g = Gravity * h;
        double apex = (0.4 + 0.55 * U(seed, i, 1)) * h;
        double vy0 = Math.Sqrt(2.0 * g * apex);
        double tUp = vy0 / g;
        double vx = (U(seed, i, 2) - 0.5) * 2.0 * MaxDriftSpeed;

        double x, y;
        if (t <= tUp)
        {
            x = x0 + vx * t;
            y = y0 - vy0 * t + 0.5 * g * t * t;
        }
        else
        {
            double tf = t - tUp;
            double phase = U(seed, i, 7) * 2.0 * Math.PI;
            x = x0 + vx * tUp + SwayAmplitude * (Math.Sin(2.0 * Math.PI * SwayHz * tf + phase) - Math.Sin(phase));
            y = y0 - apex + FallSpeed * h * tf;
        }

        float size = (float)(8.0 + 10.0 * U(seed, i, 5));
        double spinDegPerS = (U(seed, i, 6) - 0.5) * 720.0;
        double rot = (U(seed, i, 6) * 360.0 + spinDegPerS * t) % 360.0;
        if (rot < 0) rot += 360.0;
        int color = Math.Min(PartyColors.Length, (int)(U(seed, i, 4) * (PartyColors.Length + 1)));
        p = new CelebrationParticle(i, (float)x - size / 2f, (float)y - size * 0.3f, size, size * 0.6f, (float)rot, color);
        return true;
    }

    /// <summary>Upper bound of <see cref="VisibleOnNode"/>'s output for this node (size of the caller's buffer).</summary>
    public static int Capacity(NodeLayout node, int count)
    {
        int cw = node.CanvasWidth > 0 ? node.CanvasWidth : Math.Max(1, node.Width);
        return Math.Min(count, (int)Math.Ceiling((node.Width + 2 * Margin) / cw * count) + 3);
    }

    /// <summary>
    /// The particles that can touch <paramref name="node"/>, in node-local coordinates (X mirrored on mirrored nodes);
    /// returns how many were written. Wrapping (Ring) layouts take the copy of each particle nearest the node.
    /// A node (plus margin) wider than the whole ring evaluates every particle once.
    /// </summary>
    public static int VisibleOnNode(ShowEvent evt, NodeLayout node, long localMs, Span<CelebrationParticle> output)
    {
        int cw = node.CanvasWidth > 0 ? node.CanvasWidth : Math.Max(1, node.Width);
        int ch = node.CanvasHeight > 0 ? node.CanvasHeight : Math.Max(1, node.Height);
        int n = Count(cw);
        double lo = node.OffsetX - Margin, hi = node.OffsetX + node.Width + Margin;
        int jLo = (int)Math.Floor(lo / cw * n) - 1;
        int jHi = (int)Math.Ceiling(hi / cw * n) + 1;
        if (!node.Wraps)
        {
            jLo = Math.Max(jLo, 0);
            jHi = Math.Min(jHi, n - 1);
        }
        else if (jHi - jLo + 1 > n)
        {
            jLo = 0;
            jHi = n - 1;
        }

        int written = 0;
        for (int j = jLo; j <= jHi && written < output.Length; j++)
        {
            int i = node.Wraps ? (int)(((long)j % n + n) % n) : j;
            if (!Particle(evt, i, n, cw, ch, localMs, out var p)) continue;
            float vx = p.X;
            if (node.Wraps) vx += (float)Math.Floor((double)j / n) * cw;   // j = i + k·n → the copy shifted by k·cw
            float lx = NodeMapping.ToLocalX(vx, p.Width, node);
            float ly = NodeMapping.ToLocalY(p.Y, node);
            float r = Math.Max(p.Width, p.Height);                          // rotation slack
            if (lx + r < 0f || lx - r > node.Width || ly + r < 0f || ly - r > node.Height) continue;
            output[written++] = p with { X = lx, Y = ly };
        }
        return written;
    }
}

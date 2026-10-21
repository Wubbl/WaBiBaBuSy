using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.Models.Show;

public enum BombPhase { Blackout, Roulette, Drop, Cables, Countdown, Hold, Ended }

/// <summary>
/// BiBaBo timeline and drawing math (design §2, §6): pure functions of the game and the event-local time, shared by
/// the players and the room preview.
/// </summary>
public static class BombMath
{
    public const int BlackoutMs = 1000, RouletteEndMs = 4000, DropEndMs = 5000;

    /// <summary>The last 30 s of the countdown blink (and 0:00 while waiting for the server's outcome).</summary>
    public const int BlinkFromMs = 30_000;

    public const int OutcomeDurationMs = 8000, AbortDurationMs = 1000;

    public static BombPhase Phase(BombGame g, long localMs) =>
        localMs < 0 ? BombPhase.Ended
        : localMs < BlackoutMs ? BombPhase.Blackout
        : localMs < RouletteEndMs ? BombPhase.Roulette
        : localMs < DropEndMs ? BombPhase.Drop
        : localMs < g.IntroMs ? BombPhase.Cables
        : localMs < (long)g.IntroMs + g.TimerMs ? BombPhase.Countdown
        : BombPhase.Hold;

    /// <summary>0 → 1 over the blackout, then 1.</summary>
    public static float BlackOpacity(long localMs) => localMs <= 0 ? 0f : Math.Min(1f, localMs / (float)BlackoutMs);

    /// <summary>The seat the roulette is on (decelerating hops, last = the bomb seat); −1 outside the roulette.</summary>
    public static int RouletteOrder(BombGame g, long localMs)
    {
        if (localMs < BlackoutMs || localMs >= RouletteEndMs || g.RouletteOrders.Length == 0) return -1;
        double p = (localMs - BlackoutMs) / (double)(RouletteEndMs - BlackoutMs);
        int n = g.RouletteOrders.Length;
        int i = Math.Min(n - 1, (int)(n * (1 - (1 - p) * (1 - p))));
        return g.RouletteOrders[i];
    }

    /// <summary>How much of each cable is drawn: 0 before the cables phase, 1 from the countdown on.</summary>
    public static float CableProgress(BombGame g, long localMs)
    {
        int span = Math.Max(1, g.IntroMs - DropEndMs);
        return Math.Clamp((localMs - DropEndMs) / (float)span, 0f, 1f);
    }

    /// <summary>Points to draw of a growing cable (1 = nothing, all = done).</summary>
    public static int PrefixPointCount(float[] points, float progress)
    {
        int n = points.Length / 2;
        return n == 0 ? 0 : 1 + (int)MathF.Round(Math.Clamp(progress, 0f, 1f) * (n - 1));
    }

    /// <summary>The timer: full before the countdown, then counting down, 0 while waiting for the outcome.</summary>
    public static int RemainingMs(BombGame g, long localMs)
    {
        if (localMs < g.IntroMs) return g.TimerMs;
        return (int)Math.Clamp(g.TimerMs - (localMs - g.IntroMs), 0, g.TimerMs);
    }

    /// <summary>False during the off half of the 2 Hz blink (last 30 s and 0:00).</summary>
    public static bool TimerVisible(BombGame g, long localMs) =>
        RemainingMs(g, localMs) > BlinkFromMs || localMs < g.IntroMs || (localMs / 250) % 2 == 0;

    /// <summary>"4:59".</summary>
    public static string FormatTimer(int remainingMs)
    {
        int s = (Math.Max(0, remainingMs) + 999) / 1000;
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>Segments a..g (top, top-right, bottom-right, bottom, bottom-left, top-left, middle) of a digit.</summary>
    public static bool[] SevenSegment(int digit) => Segments[Math.Clamp(digit, 0, 9)];

    private static readonly bool[][] Segments =
    {
        new[] { true, true, true, true, true, true, false },     // 0
        new[] { false, true, true, false, false, false, false }, // 1
        new[] { true, true, false, true, true, false, true },    // 2
        new[] { true, true, true, true, false, false, true },    // 3
        new[] { false, true, true, false, false, true, true },   // 4
        new[] { true, false, true, true, false, true, true },    // 5
        new[] { true, false, true, true, true, true, true },     // 6
        new[] { true, true, true, false, false, false, false },  // 7
        new[] { true, true, true, true, true, true, true },      // 8
        new[] { true, true, true, true, false, true, true },     // 9
    };

    /// <summary>
    /// Cable segments (point i → i+1 of the first <paramref name="pointCount"/> points) that touch this node's slice
    /// (± <paramref name="pad"/>), each with the x shift to draw it at (0, or ±CanvasWidth across a Ring seam).
    /// Returns how many were written; output should hold 3 × segments.
    /// </summary>
    public static int SegmentsOnNode(float[] points, int pointCount, NodeLayout node, float pad, Span<(int I, float Shift)> output)
    {
        int n = 0;
        float lo = node.OffsetX - pad, hi = node.OffsetX + node.Width + pad;
        int shifts = node.Wraps && node.CanvasWidth > 0 ? 3 : 1;
        for (int i = 0; i + 1 < pointCount && n < output.Length; i++)
        {
            float a = points[2 * i], b = points[2 * i + 2];
            float min = Math.Min(a, b), max = Math.Max(a, b);
            for (int s = 0; s < shifts && n < output.Length; s++)
            {
                float shift = s == 0 ? 0 : s == 1 ? node.CanvasWidth : -node.CanvasWidth;
                if (max + shift >= lo && min + shift <= hi) output[n++] = (i, shift);
            }
        }
        return n;
    }

    // ── keypad (design §14.2): below the bomb, offsets from the bomb's on-screen centre so it reads 1 2 3 even mirrored ──

    private const float KeySize = 0.11f, KeyGap = 0.025f, KeypadTop = 0.58f;   // × BombSize

    /// <summary>Key 0–9 or 10 (C): its box as (dx, dy) from the bomb's local centre + size, canvas units (not mirrored).</summary>
    public static (float Dx, float Dy, float W, float H) KeyRect(BombGame g, int key)
    {
        float s = KeySize * g.BombSize, gap = KeyGap * g.BombSize;
        int col = key switch { 0 => 1, 10 => 0, _ => (key - 1) % 3 };
        int row = key switch { 0 or 10 => 3, _ => (key - 1) / 3 };
        float left = -(3 * s + 2 * gap) / 2f, top = KeypadTop * g.BombSize + s + gap;   // the display sits above the keys
        return (left + col * (s + gap), top + row * (s + gap), s, s);
    }

    /// <summary>The typed-digits display above the keypad (same convention as <see cref="KeyRect"/>).</summary>
    public static (float Dx, float Dy, float W, float H) KeypadDisplay(BombGame g)
    {
        float s = KeySize * g.BombSize, gap = KeyGap * g.BombSize, w = 3 * s + 2 * gap;
        return (-w / 2f, KeypadTop * g.BombSize, w, s);
    }

    /// <summary>The bomb is drawn from the drop until its outcome's T0.</summary>
    public static bool BombVisible(BombGame g, long bombLocalMs, ShowEvent? outcome, long outcomeLocalMs) =>
        bombLocalMs >= RouletteEndMs && (outcome == null || outcomeLocalMs < 0);

    /// <summary>Drop-in bounce: vertical offset as a fraction of the bomb size (−1 above → 0 landed).</summary>
    public static float DropOffset(long localMs)
    {
        float t = Math.Clamp((localMs - RouletteEndMs) / (float)(DropEndMs - RouletteEndMs), 0f, 1f);
        return -(1 - t) * (1 - t) + 0.08f * MathF.Sin(t * MathF.PI) * (1 - t);
    }

    /// <summary>BOOM: the shockwave's radius around the bomb (canvas units).</summary>
    public static float ShockwaveRadius(long outcomeLocalMs, float canvasUnitsPerMs) => Math.Max(0, outcomeLocalMs) * canvasUnitsPerMs;

    /// <summary>BOOM: screen shake (canvas units, ±6, decaying to 0 over 1.5 s), the same on every seat.</summary>
    public static (float X, float Y) Shake(int seed, long outcomeLocalMs)
    {
        if (outcomeLocalMs < 0 || outcomeLocalMs >= 1500) return (0f, 0f);
        float amp = 6f * (1 - outcomeLocalMs / 1500f);
        float t = outcomeLocalMs;
        return (amp * MathF.Sin(t * 0.09f + seed % 97), amp * MathF.Cos(t * 0.11f + seed % 89));
    }

    /// <summary>The outcome's black layer: 1, fading out over the last second.</summary>
    public static float OutcomeBlack(ShowEvent outcome, long outcomeLocalMs)
    {
        int d = ShowEventMath.ClampDuration(outcome.DurationMs);
        return outcomeLocalMs < 0 || outcomeLocalMs >= d ? 0f : Math.Clamp((d - outcomeLocalMs) / 1000f, 0f, 1f);
    }
}

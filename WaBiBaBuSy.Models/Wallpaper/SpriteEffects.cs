using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>One trail ghost: how far back in time it samples the sprite, its opacity and its size factor.</summary>
public readonly record struct TrailGhost(long OffsetMs, float Opacity, float Scale);

/// <summary>Everything the pose of the sprite depends on, for one node.</summary>
public readonly record struct SpritePoseInput(
    MovementConfig Movement, int AnimW, int AnimH, int CanvasW, int CanvasH, float TileAlignStepX, NodeLayout Node);

/// <summary>Which crossing a bezel event marks.</summary>
public enum BezelEventKind { Entry, Exit }

/// <summary>A bezel crossing on this node that is still within the effect's duration.</summary>
/// <param name="Kind">Entry or exit.</param>
/// <param name="Effect">What to draw for it.</param>
/// <param name="EventElapsedMs">First 16 ms grid time in the new state (identical on every node and frame).</param>
/// <param name="Progress">0 at the crossing, 1 when the effect ends.</param>
/// <param name="AtRightEdge">The crossing happened at the node's right screen edge.</param>
/// <param name="CenterVirtualY">Sprite center Y (virtual canvas) at the crossing.</param>
public readonly record struct BezelEvent(
    BezelEventKind Kind, BezelEffect Effect, long EventElapsedMs, float Progress, bool AtRightEdge, float CenterVirtualY);

/// <summary>What to draw for a bezel event, in node-local canvas units.</summary>
/// <remarks>Glow: rect X/Y/Width/Height, bright at the edge (EdgeOnRight). Flash: whole node. Ripple: circle at X/Y with Radius and StrokeWidth.</remarks>
public readonly record struct BezelVisual(
    BezelEffect Effect, float Opacity, float X, float Y, float Width, float Height, float Radius, float StrokeWidth, bool EdgeOnRight);

/// <summary>
/// Pure math of the sprite effects (<see cref="SpriteEffectsConfig"/>), shared by the D2D players and the room
/// preview so both draw the same thing. Every function depends only on its arguments.
/// </summary>
public static class SpriteEffects
{
    /// <summary>Upper bound for <see cref="TrailConfig.Count"/> (draw cost per frame).</summary>
    public const int MaxTrailCount = 32;

    /// <summary>True when <paramref name="fx"/> asks for at least one effect.</summary>
    public static bool HasAny(SpriteEffectsConfig? fx) =>
        fx != null && (TrailCount(fx.Trail) > 0 || PulseActive(fx.Pulse) || fx.SpinDegPerSecond != 0f
                       || fx.Entry != BezelEffect.None || fx.Exit != BezelEffect.None);

    /// <summary>True when the pulse changes the size at all.</summary>
    public static bool PulseActive(PulseConfig? pulse) => pulse != null && pulse.Amplitude > 0f && pulse.FrequencyHz > 0f;

    /// <summary>Short labels of what is drawn ("trail 8", "pulse", "spin 45°/s", "entry ripple", …) for summaries.</summary>
    public static IReadOnlyList<string> Describe(SpriteEffectsConfig? fx)
    {
        var parts = new List<string>();
        if (fx == null) return parts;
        int trail = TrailCount(fx.Trail);
        if (trail > 0) parts.Add($"trail {trail}");
        if (PulseActive(fx.Pulse)) parts.Add("pulse");
        if (fx.SpinDegPerSecond != 0f) parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"spin {fx.SpinDegPerSecond:0}°/s"));
        if (fx.Entry != BezelEffect.None) parts.Add($"entry {fx.Entry.ToString().ToLowerInvariant()}");
        if (fx.Exit != BezelEffect.None) parts.Add($"exit {fx.Exit.ToString().ToLowerInvariant()}");
        return parts;
    }

    /// <summary>
    /// Effects are drawn for a single image moved by <see cref="MovementCalculator"/>: not with a pattern (cells
    /// cross bezels continuously), not with extra images, not on an IconZone path (its position is not a pure
    /// function of time, so ghosts and crossings cannot be sampled).
    /// </summary>
    public static bool Supported(AnimationLayerConfig animation, MovementConfig? movement, BackgroundMode backgroundMode) =>
        HasAny(animation.Effects)
        && animation.Pattern == null
        && animation.AdditionalAnimationPaths?.Any(p => !string.IsNullOrWhiteSpace(p)) != true
        && movement != null
        && !MovementCalculator.FollowsIconPath(backgroundMode, movement);

    /// <summary>Scale factor at <paramref name="elapsedMs"/>: 1 + A·sin(2πft), A clamped to 0..0.9; 1 without a pulse.</summary>
    public static float PulseScale(PulseConfig? pulse, long elapsedMs)
    {
        if (pulse == null || pulse.FrequencyHz <= 0f || pulse.Amplitude <= 0f) return 1f;
        float amplitude = Math.Min(pulse.Amplitude, 0.9f);
        double periodMs = 1000.0 / pulse.FrequencyHz;
        double t = elapsedMs % periodMs;            // fold in double first: exact after days of uptime
        if (t < 0) t += periodMs;
        return 1f + amplitude * (float)Math.Sin(2.0 * Math.PI * t / periodMs);
    }

    /// <summary>Rotation in degrees, in [0, 360), after <paramref name="elapsedMs"/> at <paramref name="degPerSecond"/>.</summary>
    public static float SpinDegrees(float degPerSecond, long elapsedMs)
    {
        if (degPerSecond == 0f) return 0f;
        double deg = elapsedMs / 1000.0 * degPerSecond % 360.0;
        if (deg < 0) deg += 360.0;
        return deg >= 360.0 ? 0f : (float)deg;
    }

    /// <summary>True for "#RRGGBB" / "RRGGBB" (surrounding blanks ignored).</summary>
    public static bool IsValidBezelColor(string? hex) => TryParseRgb(hex, out _);

    /// <summary>
    /// <see cref="SpriteEffectsConfig.BezelColorHex"/> as RGB; white (the default) when it is not "#RRGGBB". Never
    /// throws — the players parse it on every frame of a crossing.
    /// </summary>
    public static (byte R, byte G, byte B) ParseBezelColor(string? hex) => TryParseRgb(hex, out var rgb) ? rgb : ((byte)255, (byte)255, (byte)255);

    private static bool TryParseRgb(string? hex, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        var s = hex?.Trim() ?? string.Empty;
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6) return false;
        var style = System.Globalization.NumberStyles.HexNumber;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!byte.TryParse(s.AsSpan(0, 2), style, inv, out byte r)
            || !byte.TryParse(s.AsSpan(2, 2), style, inv, out byte g)
            || !byte.TryParse(s.AsSpan(4, 2), style, inv, out byte b)) return false;
        rgb = (r, g, b);
        return true;
    }

    /// <summary>Number of ghosts to draw (0..<see cref="MaxTrailCount"/>).</summary>
    public static int TrailCount(TrailConfig? trail) => trail == null ? 0 : Math.Clamp(trail.Count, 0, MaxTrailCount);

    /// <summary>
    /// Ghost <paramref name="k"/> (1 = nearest): sampled k × spacing ago, opacity StartOpacity × (1 − (k−1)/Count),
    /// size ScaleFalloff^k.
    /// </summary>
    public static TrailGhost Ghost(TrailConfig trail, int k)
    {
        int count = Math.Max(1, TrailCount(trail));
        long offset = (long)k * Math.Max(1, trail.SpacingMs);
        float opacity = Math.Clamp(trail.StartOpacity, 0f, 1f) * (1f - (k - 1) / (float)count);
        float scale = MathF.Pow(Math.Clamp(trail.ScaleFalloff, 0.1f, 1f), k);
        return new TrailGhost(offset, Math.Clamp(opacity, 0f, 1f), scale);
    }

    /// <summary>Grid of the bezel scan, ms (one 60 Hz frame).</summary>
    public const int BezelScanStepMs = 16;

    /// <summary>Width of the Glow strip, canvas units.</summary>
    public const float GlowWidth = 120f;

    /// <summary>Ripple radius when the effect ends, canvas units.</summary>
    public const float RippleMaxRadius = 600f;

    /// <summary><see cref="SpriteEffectsConfig.BezelDurationMs"/> clamped to 50..3000 ms (bounds the scan cost).</summary>
    public static int BezelDurationMs(SpriteEffectsConfig fx) => Math.Clamp(fx.BezelDurationMs, 50, 3000);

    /// <summary>Sprite top-left in virtual canvas units — exactly what the players compute for MovementCalculator movement.</summary>
    public static (float VirtualX, float VirtualY) Pose(in SpritePoseInput p, long elapsedMs) =>
        MovementCalculator.Calculate(p.Movement, elapsedMs, p.AnimW, p.AnimH, p.CanvasW, p.CanvasH,
            tileAlignStepX: p.TileAlignStepX, canvasWraps: p.Node.Wraps);

    /// <summary>
    /// True when any wrap copy of the sprite overlaps the node at <paramref name="elapsedMs"/>. <paramref name="localX"/>:
    /// screen-space left edge of the visible copy (of the first copy when none is visible).
    /// </summary>
    public static bool IsOnNode(in SpritePoseInput p, long elapsedMs, out float localX, out float virtualY)
    {
        var (vx, vy) = Pose(p, elapsedMs);
        virtualY = vy;
        Span<float> copies = stackalloc float[2];
        int n = NodeMapping.WrapCopies(vx, p.AnimW, p.Node, copies);
        for (int i = 0; i < n; i++)
        {
            float lx = NodeMapping.ToLocalX(copies[i], p.AnimW, p.Node);
            if (NodeMapping.IsVisible(lx, p.AnimW, p.Node)) { localX = lx; return true; }
        }
        localX = NodeMapping.ToLocalX(copies[0], p.AnimW, p.Node);
        return false;
    }

    /// <summary>
    /// Screen-space facing at <paramref name="elapsedMs"/> from the motion over the previous 16 ms (mirrored nodes
    /// included); <paramref name="fallback"/> when it barely moves or wraps around the ring.
    /// </summary>
    public static bool FacingLeftAt(in SpritePoseInput p, long elapsedMs, bool fallback)
    {
        float x1 = NodeMapping.ToLocalX(Pose(p, elapsedMs).VirtualX, p.AnimW, p.Node);
        float x0 = NodeMapping.ToLocalX(Pose(p, elapsedMs - BezelScanStepMs).VirtualX, p.AnimW, p.Node);
        float dx = x1 - x0;
        if (MathF.Abs(dx) > p.Node.Width) dx = 0f;    // loop / ring wrap, not a direction change
        return SyncTiming.ResolveFacingLeft(dx, fallback);
    }

    /// <summary>
    /// The newest bezel crossing on this node within the effect duration, or null. Scans the sprite's visibility
    /// backwards on a 16 ms grid aligned to absolute elapsed time, so every frame and every node agree on the
    /// crossing time without any frame history. Samples before elapsed 0 end the scan: a sprite that is on a node
    /// when the scene starts did not cross into it. Crossings whose kind has no effect are skipped.
    /// </summary>
    public static BezelEvent? FindBezelEvent(in SpritePoseInput p, SpriteEffectsConfig fx, long elapsedMs)
    {
        if (elapsedMs < 0 || (fx.Entry == BezelEffect.None && fx.Exit == BezelEffect.None)) return null;
        int duration = BezelDurationMs(fx);
        long newerT = elapsedMs - SyncTiming.PositiveModulo(elapsedMs, BezelScanStepMs);
        bool newerVisible = IsOnNode(p, newerT, out float newerX, out float newerY);
        for (long t = newerT - BezelScanStepMs; t >= 0 && elapsedMs - newerT <= duration; t -= BezelScanStepMs)
        {
            bool olderVisible = IsOnNode(p, t, out float olderX, out float olderY);
            if (olderVisible != newerVisible)
            {
                var kind = newerVisible ? BezelEventKind.Entry : BezelEventKind.Exit;
                var effect = kind == BezelEventKind.Entry ? fx.Entry : fx.Exit;
                if (effect != BezelEffect.None)
                {
                    float x = newerVisible ? newerX : olderX;       // the copy that is (entry) / was (exit) on the node
                    float y = newerVisible ? newerY : olderY;
                    bool right = x + p.AnimW / 2f > p.Node.Width / 2f;
                    return new BezelEvent(kind, effect, newerT, (elapsedMs - newerT) / (float)duration, right, y + p.AnimH / 2f);
                }
                // A crossing without an effect (e.g. exit while only Entry is set) must not cut an older,
                // still-running effect short: keep scanning.
            }
            newerT = t; newerVisible = olderVisible; newerX = olderX; newerY = olderY;
        }
        return null;
    }

    /// <summary>Geometry and opacity of <paramref name="e"/> on <paramref name="node"/> (design section 3 table).</summary>
    public static BezelVisual Visual(in BezelEvent e, NodeLayout node)
    {
        float p = Math.Clamp(e.Progress, 0f, 1f);
        float fade = 1f - p;
        float edgeX = e.AtRightEdge ? node.Width : 0f;
        switch (e.Effect)
        {
            case BezelEffect.Glow:
                float w = Math.Min(GlowWidth, node.Width / 2f);
                return new BezelVisual(BezelEffect.Glow, fade, e.AtRightEdge ? node.Width - w : 0f, 0f, w, node.Height, 0f, 0f, e.AtRightEdge);
            case BezelEffect.Flash:
                return new BezelVisual(BezelEffect.Flash, 0.35f * fade * fade, 0f, 0f, node.Width, node.Height, 0f, 0f, e.AtRightEdge);
            case BezelEffect.Ripple:
                float cy = NodeMapping.ToLocalY(e.CenterVirtualY, node);
                return new BezelVisual(BezelEffect.Ripple, fade, edgeX, cy, 0f, 0f, p * RippleMaxRadius, 2f + 10f * fade, e.AtRightEdge);
            default:
                return new BezelVisual(BezelEffect.None, 0f, 0f, 0f, 0f, 0f, 0f, 0f, e.AtRightEdge);
        }
    }
}

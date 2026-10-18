namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// One independent sprite (or pattern grid) of a scene: its own source file(s), movement, look,
/// effects, opacity, distribution and clock offset. Layers draw in list order (index 0 = bottom)
/// and never read each other's state, so each stays a pure function of the shared clock.
/// Up to two layers may be videos (SceneChecks.MaxVideoLayers); only the bottom layer follows the IconZone path / corridor.
/// </summary>
public class SceneLayer
{
    /// <summary>Display name in the editor's Layers strip.</summary>
    public string Name { get; set; } = "Layer";

    /// <summary>Disabled layers are kept in the scene but not drawn.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>0..1, multiplied into every draw of this layer.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>Source file(s), size, fit, pattern, color grading and effects.</summary>
    public AnimationLayerConfig Animation { get; set; } = new();

    /// <summary>Movement across the canvas.</summary>
    public MovementConfig Movement { get; set; } = new();

    /// <summary>Sequential = spans the seat-map canvas; Simultaneous = each monitor is its own canvas.</summary>
    public AnimationDistributionMode Distribution { get; set; } = AnimationDistributionMode.Sequential;

    /// <summary>This layer's clock runs ahead by this much: position and frame use elapsed + PhaseOffsetMs.</summary>
    public long PhaseOffsetMs { get; set; }

    /// <summary>Spawn window: the layer appears once per this many ms of its clock (0 = always shown).</summary>
    public long ShowEveryMs { get; set; }

    /// <summary>Spawn window: how long it stays each time (from the start of each cycle; fades 250 ms in and out).</summary>
    public long ShowForMs { get; set; } = 10_000;
}

/// <summary>When a layer with a spawn window is shown — a pure function of the layer's clock, like everything else.</summary>
public static class LayerSchedule
{
    public const long MinEveryMs = 1000;
    public const int FadeMs = 250;

    /// <summary>0 hidden … 1 shown, at the layer's own elapsed (<see cref="SyncTiming.LayerElapsed"/>).</summary>
    public static float Visibility(long showEveryMs, long showForMs, long layerElapsedMs)
    {
        if (showEveryMs <= 0) return 1f;
        long every = Math.Max(MinEveryMs, showEveryMs);
        long dur = Math.Clamp(showForMs, FadeMs, every);
        if (dur >= every) return 1f;
        long t = SyncTiming.PositiveModulo(layerElapsedMs, every);
        if (t >= dur) return 0f;
        float fade = Math.Min(FadeMs, dur / 2f);
        return Math.Clamp(Math.Min(t / fade, (dur - t) / fade), 0f, 1f);
    }
}

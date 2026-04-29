namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Pattern multiplier configuration. The animation layer is tiled into a moving grid that
/// fills the desktop wall-to-wall. The grid is conceptually infinite in world space —
/// cells leaving one edge are continuously replaced by cells entering the opposite side.
/// </summary>
public class PatternConfig
{
    public enum SizingMode
    {
        /// <summary>
        /// Auto-derive cell counts from canvas size, image size, and spacing. Recommended default —
        /// this gives the wall-to-wall fill effect.
        /// </summary>
        Fill,

        /// <summary>Use user-supplied <see cref="CountX"/> / <see cref="CountY"/>.</summary>
        Explicit
    }

    public SizingMode Sizing { get; set; } = SizingMode.Fill;

    /// <summary>Cell count along X (ignored when <see cref="Sizing"/> is Fill).</summary>
    public int CountX { get; set; } = 0;

    /// <summary>Cell count along Y (ignored when <see cref="Sizing"/> is Fill).</summary>
    public int CountY { get; set; } = 0;

    /// <summary>Gap between cells along X (pixels).</summary>
    public float SpacingX { get; set; } = 20f;

    /// <summary>Gap between cells along Y (pixels).</summary>
    public float SpacingY { get; set; } = 20f;

    /// <summary>
    /// Extra pixel ring outside the visible area where cells are still rendered. Prevents popping at
    /// the edges as cells scroll in/out. Increase when <see cref="RandomOffsetMaxPx"/> is large.
    /// </summary>
    public float Margin { get; set; } = 0f;

    /// <summary>Maximum per-cell positional jitter (pixels) — stable per logical cell.</summary>
    public float RandomOffsetMaxPx { get; set; } = 0f;

    /// <summary>Maximum per-cell rotation jitter (degrees) — stable per logical cell.</summary>
    public float RandomRotationMaxDeg { get; set; } = 0f;

    /// <summary>Seed for per-cell jitter and per-cell image distribution.</summary>
    public int Seed { get; set; } = 1;
}

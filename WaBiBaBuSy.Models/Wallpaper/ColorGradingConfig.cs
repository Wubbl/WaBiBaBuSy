using System.Collections.Generic;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Color grading modes applied to the animation layer. The clan logo is monochrome,
/// so all modes use tint-mapping (gray luminance × tint color) rather than literal HSV rotation —
/// this is what gives a colorless logo visible color cycling.
/// </summary>
public enum ColorGradingMode
{
    /// <summary>No color grading — direct DrawBitmap, zero overhead.</summary>
    None,

    /// <summary>Cycle continuously through the full hue spectrum at <see cref="ColorGradingConfig.CyclesPerSecond"/>.</summary>
    Rainbow,

    /// <summary>Pick a color from <see cref="ColorGradingConfig.ColorList"/> at random (deterministic) every step.</summary>
    RandomColors,

    /// <summary>Ping-pong gradient between <see cref="ColorGradingConfig.GradientA"/> and <see cref="ColorGradingConfig.GradientB"/>.</summary>
    Gradient,

    /// <summary>Step through <see cref="ColorGradingConfig.ColorList"/> in order, looping.</summary>
    CycleColorList,

    /// <summary>Each pattern cell gets a fixed hue derived from its grid identity (LogicalI, LogicalJ).
    /// Colors travel with the cell as it moves — full hue spectrum distributed across cells.</summary>
    TravelingRainbow,

    /// <summary>Each pattern cell gets a fixed color from <see cref="ColorGradingConfig.ColorList"/>,
    /// chosen by its diagonal grid position (LogicalI + LogicalJ) mod count — produces a stripe pattern.
    /// Colors travel with the cell as it moves.</summary>
    TravelingList,

    /// <summary>Each pattern cell gets a fixed color from <see cref="ColorGradingConfig.ColorList"/>,
    /// chosen by hashing its grid identity (scattered distribution). Colors travel with the cell as it moves.</summary>
    TravelingRandom
}

/// <summary>
/// Color grading configuration. All timing is deterministic from elapsedMs so every monitor
/// stays color-synced without per-frame IPC.
/// </summary>
public class ColorGradingConfig
{
    public ColorGradingMode Mode { get; set; } = ColorGradingMode.None;

    /// <summary>
    /// How many full color cycles complete per second. Independent of movement speed.
    /// Examples: 0.1 = one rainbow sweep every 10 seconds; 1.0 = one sweep per second.
    /// </summary>
    public double CyclesPerSecond { get; set; } = 0.1;

    /// <summary>
    /// List of hex colors used by <see cref="ColorGradingMode.RandomColors"/>,
    /// <see cref="ColorGradingMode.CycleColorList"/>, <see cref="ColorGradingMode.TravelingList"/>,
    /// and <see cref="ColorGradingMode.TravelingRandom"/>.
    /// </summary>
    public List<string> ColorList { get; set; } = new();

    /// <summary>Hex color A for <see cref="ColorGradingMode.Gradient"/>.</summary>
    public string GradientA { get; set; } = "#FF0000";

    /// <summary>Hex color B for <see cref="ColorGradingMode.Gradient"/>.</summary>
    public string GradientB { get; set; } = "#0000FF";

    /// <summary>Seed for <see cref="ColorGradingMode.RandomColors"/> — same seed yields the same sequence.</summary>
    public int Seed { get; set; } = 1;

    /// <summary>
    /// Fraction of pattern cells that receive traveling color (0.0 = none, 1.0 = all). Default 1.0.
    /// Only used with Traveling modes. Uncolored cells are drawn monochrome.
    /// The selection is deterministic per (LogicalI, LogicalJ) so the same cells are always colored.
    /// </summary>
    public float ColoredCellPercentage { get; set; } = 1.0f;
}

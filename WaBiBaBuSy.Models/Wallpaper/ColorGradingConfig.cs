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
    CycleColorList
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
    /// List of hex colors used by <see cref="ColorGradingMode.RandomColors"/> and
    /// <see cref="ColorGradingMode.CycleColorList"/>.
    /// </summary>
    public List<string> ColorList { get; set; } = new();

    /// <summary>Hex color A for <see cref="ColorGradingMode.Gradient"/>.</summary>
    public string GradientA { get; set; } = "#FF0000";

    /// <summary>Hex color B for <see cref="ColorGradingMode.Gradient"/>.</summary>
    public string GradientB { get; set; } = "#0000FF";

    /// <summary>Seed for <see cref="ColorGradingMode.RandomColors"/> — same seed yields the same sequence.</summary>
    public int Seed { get; set; } = 1;
}

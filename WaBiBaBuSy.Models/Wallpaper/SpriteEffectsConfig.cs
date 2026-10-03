namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Visual effects on the single sprite (roadmap 2.4): a fading trail, a scale pulse, a constant spin and an
/// effect where the sprite crosses into or out of a node. Every effect is a pure function of the shared clock,
/// so all machines draw the same thing. Drawn only for a single image moved by <see cref="MovementCalculator"/>
/// (see <see cref="SpriteEffects.Supported"/>).
/// </summary>
public class SpriteEffectsConfig
{
    /// <summary>Ghost copies behind the sprite; null = no trail.</summary>
    public TrailConfig? Trail { get; set; }

    /// <summary>Scale breathing; null = no pulse.</summary>
    public PulseConfig? Pulse { get; set; }

    /// <summary>Constant rotation in degrees per second (negative = counter-clockwise); 0 = none.</summary>
    public float SpinDegPerSecond { get; set; }

    /// <summary>Effect on the node the sprite enters, at the edge it comes in.</summary>
    public BezelEffect Entry { get; set; } = BezelEffect.None;

    /// <summary>Effect on the node the sprite leaves, at the edge it goes out.</summary>
    public BezelEffect Exit { get; set; } = BezelEffect.None;

    /// <summary>Color of the bezel effects (#RRGGBB).</summary>
    public string BezelColorHex { get; set; } = "#FFFFFF";

    /// <summary>How long a bezel effect lasts after the crossing, ms (clamped to 50..3000).</summary>
    public int BezelDurationMs { get; set; } = 350;
}

/// <summary>Trail: ghost k shows the sprite where it was k × <see cref="SpacingMs"/> ago.</summary>
public class TrailConfig
{
    /// <summary>Number of ghosts (clamped to 0..32).</summary>
    public int Count { get; set; } = 6;

    /// <summary>Time between ghosts, ms (at least 1).</summary>
    public int SpacingMs { get; set; } = 60;

    /// <summary>Opacity of the nearest ghost; farther ghosts fade linearly to 0.</summary>
    public float StartOpacity { get; set; } = 0.5f;

    /// <summary>Size factor per ghost step (ghost k is ScaleFalloff^k of the sprite); clamped to 0.1..1.</summary>
    public float ScaleFalloff { get; set; } = 0.9f;

    /// <summary>With a time-based color mode, tint each ghost with the color the sprite had then (a rainbow comet).</summary>
    public bool UseGradingColor { get; set; } = true;
}

/// <summary>Pulse: scale = 1 + Amplitude × sin(2π × FrequencyHz × t).</summary>
public class PulseConfig
{
    /// <summary>Relative scale swing (0.08 = ±8 %); clamped to 0..0.9.</summary>
    public float Amplitude { get; set; } = 0.08f;

    /// <summary>Breaths per second; 0 or less = no pulse.</summary>
    public float FrequencyHz { get; set; } = 0.8f;
}

/// <summary>What marks a bezel crossing on the node where it happens.</summary>
public enum BezelEffect
{
    /// <summary>Nothing.</summary>
    None,
    /// <summary>A soft vertical strip at the crossing edge that fades out.</summary>
    Glow,
    /// <summary>The whole screen flashes the bezel color briefly.</summary>
    Flash,
    /// <summary>A ring expands from the crossing point.</summary>
    Ripple,
}

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// One independent sprite (or pattern grid) of a scene: its own source file(s), movement, look,
/// effects, opacity, distribution and clock offset. Layers draw in list order (index 0 = bottom)
/// and never read each other's state, so each stays a pure function of the shared clock.
/// Only the bottom layer may be a video and only it follows the IconZone path / corridor.
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
}

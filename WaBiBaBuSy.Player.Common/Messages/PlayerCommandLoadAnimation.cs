using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Command sent from parent to D2DPlayer to load animation composition.
/// This replaces the per-frame approach with metadata-based rendering.
/// The player will load the animation file and composition config locally,
/// eliminating the need for JPEG encoding/decoding and per-frame IPC.
/// </summary>
public class PlayerCommandLoadAnimation : PlayerMessageBase
{
    public PlayerCommandLoadAnimation()
    {
        MessageType = "cmd_load_animation";
    }

    /// <summary>
    /// Animation layer configuration (file path, size, alignment, looping)
    /// </summary>
    public AnimationLayerConfig AnimationConfig { get; set; } = new();

    /// <summary>
    /// Background layer configuration (solid color, image, or tiled pattern)
    /// </summary>
    public BackgroundLayerConfig BackgroundConfig { get; set; } = new();

    /// <summary>
    /// Monitor index for multi-monitor setups (0-based)
    /// </summary>
    public int MonitorIndex { get; set; } = 0;

    /// <summary>
    /// Virtual canvas height for composition calculations
    /// </summary>
    public int VirtualCanvasHeight { get; set; } = 1080;
}

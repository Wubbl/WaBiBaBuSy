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

    /// <summary>
    /// Total virtual canvas width (all monitors combined) for movement calculations
    /// </summary>
    public int VirtualCanvasWidth { get; set; } = 1920;

    /// <summary>
    /// This monitor's X offset in the virtual canvas (for multi-monitor positioning)
    /// </summary>
    public int MonitorOffsetX { get; set; } = 0;

    /// <summary>
    /// Movement configuration for animation positioning.
    /// When null, falls back to legacy PixelsPerSecond-based linear movement.
    /// </summary>
    public MovementConfig? MovementConfig { get; set; }

    /// <summary>
    /// Icon-zone rectangles in virtual-canvas coordinates. Populated by the orchestrator from
    /// the ZonePlanner result. Used as a clip mask when <see cref="MaskZones"/> is true so that
    /// pattern cells overlapping desktop icon regions are not drawn.
    /// Null = no zones broadcast (player will detect locally if needed for legacy IconZone modes).
    /// </summary>
    public System.Collections.Generic.List<ZoneRect>? ZoneRects { get; set; }

    /// <summary>
    /// When true, the player builds a clip-mask geometry of (canvas \ union(ZoneRects)) and
    /// pushes it as an ID2D1Layer around the animation/pattern draw. Set automatically when
    /// BackgroundMode == IconZone AND AnimationConfig.Pattern != null.
    /// </summary>
    public bool MaskZones { get; set; } = false;

    /// <summary>
    /// When true, the player renders the colored per-cluster icon-zone palette rectangles.
    /// Defaults to false — the palette is mostly a debug visualization. Mirrors
    /// <see cref="BackgroundLayerConfig.IconZonePaletteEnabled"/>.
    /// </summary>
    public bool UseZonePalette { get; set; } = false;

    /// <summary>
    /// Width in pixels of the feathered gradient zone outside each icon zone rectangle.
    /// 0 = off (legacy per-cell alpha fade). Mirrors <see cref="BackgroundLayerConfig.IconFadePaddingPx"/>.
    /// </summary>
    public int IconFadePaddingPx { get; set; } = 50;

    /// <summary>
    /// Pixels to expand each icon zone rectangle beyond its detected bounds before applying fade.
    /// Mirrors <see cref="BackgroundLayerConfig.IconZoneExpansionPx"/>.
    /// </summary>
    public int IconZoneExpansionPx { get; set; } = 10;
}

using WaBiBaBuSy.Models.Wallpaper;

using WaBiBaBuSy.Models.Topology;

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
    /// The scene's sprite layers, bottom first, already resolved to canvas pixels. Each layer picks its canvas
    /// by its own <see cref="SceneLayer.Distribution"/>: Sequential = <see cref="Layout"/>, Simultaneous = this
    /// monitor alone. Only layer 0 may be a video and only layer 0 follows the IconZone path / corridor.
    /// </summary>
    public System.Collections.Generic.List<SceneLayer> Layers { get; set; } = new();

    /// <summary>
    /// Background layer configuration (solid color, image, or tiled pattern)
    /// </summary>
    public BackgroundLayerConfig BackgroundConfig { get; set; } = new();

    /// <summary>
    /// Monitor index for multi-monitor setups (0-based)
    /// </summary>
    public int MonitorIndex { get; set; } = 0;

    /// <summary>
    /// This node's place on the seat-map canvas (offset, mirroring, ring wrap, canvas size, order, scale) —
    /// the canvas of every Sequential layer. Null = this monitor alone is the canvas.
    /// </summary>
    public NodeLayout? Layout { get; set; }

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
    /// BackgroundMode == IconZone AND layer 0 has a pattern.
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

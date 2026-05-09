using System.Collections.Generic;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Configuration for cross-screen spanning wallpaper mode
/// </summary>
public class CrossScreenConfig
{
    /// <summary>
    /// Background layer configuration
    /// </summary>
    public BackgroundLayerConfig Background { get; set; } = new();

    /// <summary>
    /// Animation layer configuration
    /// </summary>
    public AnimationLayerConfig Animation { get; set; } = new();

    /// <summary>
    /// Animation movement speed in pixels per second
    /// </summary>
    public int AnimationSpeedPxPerSecond { get; set; } = 500;

    /// <summary>
    /// List of client/monitor IDs selected for animation.
    /// If empty, animation applies to all connected clients.
    /// </summary>
    public List<string> SelectedMonitorIds { get; set; } = new();

    /// <summary>
    /// Whether to use distributed rendering (client-side) instead of centralized server rendering.
    /// When true, server sends animation file once and clients render locally.
    /// When false, server renders frames and sends to all clients (high CPU usage).
    /// Default: false (use existing centralized rendering for now).
    /// </summary>
    public bool UseDistributedRendering { get; set; } = false;

    /// <summary>
    /// Animation distribution mode for orchestrator-based animation (Phase 3).
    /// Sequential: Animation flows from one client to the next in order
    /// Simultaneous: All clients animate at the same time
    /// Default: Sequential
    /// </summary>
    public AnimationDistributionMode DistributionMode { get; set; } = AnimationDistributionMode.Sequential;

    /// <summary>
    /// Movement configuration for animation positioning across the virtual canvas
    /// </summary>
    public MovementConfig Movement { get; set; } = new();

}

/// <summary>
/// Animation distribution modes for orchestrator-based animation (Phase 3)
/// </summary>
public enum AnimationDistributionMode
{
    /// <summary>
    /// Animation flows from one client to the next in the specified order (handoff timing)
    /// </summary>
    Sequential,

    /// <summary>
    /// All clients animate at the same time with synchronized timing
    /// </summary>
    Simultaneous
}

/// <summary>
/// Background layer rendering modes
/// </summary>
public enum BackgroundMode
{
    /// <summary>
    /// Fill with solid color
    /// </summary>
    SolidColor,

    /// <summary>
    /// Stretch a single image across all screens
    /// </summary>
    StretchedImage,

    /// <summary>
    /// Tile an image pattern across all screens
    /// </summary>
    TiledImage,

    /// <summary>
    /// Top zone + middle corridor + bottom zone, each with distinct color.
    /// </summary>
    ThreeZone,

    /// <summary>
    /// Zones derived from actual desktop icon positions. Path navigates all free bands.
    /// </summary>
    IconZone
}

/// <summary>
/// A rectangular zone used by IconZone mode (per-icon colored rect or free corridor).
/// X/Width describe the 2D position; Width=-1 means "use full canvas width" (backward compat).
/// </summary>
public class ZoneRect
{
    public float X      { get; set; } = 0f;
    public float Width  { get; set; } = -1f; // -1 = full canvas width (backward compat)
    public float Y      { get; set; }
    public float Height { get; set; }
    /// <summary>True = animation can enter this zone; false = blocked by icons.</summary>
    public bool  IsFree { get; set; }
    public string ColorHex { get; set; } = "#000000";
}

/// <summary>
/// A single waypoint in the precomputed animation path through free bands.
/// </summary>
public class WaypointF
{
    public float X { get; set; }
    public float Y { get; set; }
}

/// <summary>
/// Result of ZonePlanner.Compute: zone bands and animation path.
/// </summary>
public class ZoneLayout
{
    public List<ZoneRect>  Bands { get; set; } = new();
    public List<WaypointF> Path  { get; set; } = new();
}

/// <summary>
/// Configuration for the background layer
/// </summary>
public class BackgroundLayerConfig
{
    /// <summary>
    /// Background rendering mode
    /// </summary>
    public BackgroundMode Mode { get; set; } = BackgroundMode.SolidColor;

    /// <summary>
    /// Solid color in hex format (e.g., "#000000" for black)
    /// </summary>
    public string ColorHex { get; set; } = "#000000";

    /// <summary>
    /// Path to background image file (for StretchedImage or TiledImage modes)
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>ThreeZone: pixels from top where the corridor starts.</summary>
    public int CorridorTopPx { get; set; } = 324;

    /// <summary>ThreeZone: height of the corridor in pixels.</summary>
    public int CorridorHeightPx { get; set; } = 432;

    /// <summary>ThreeZone: color for the top zone (icon area).</summary>
    public string TopZoneColorHex { get; set; } = "#1A3A5C";

    /// <summary>ThreeZone: color for the bottom zone (icon area).</summary>
    public string BottomZoneColorHex { get; set; } = "#3C1A5C";

    /// <summary>ThreeZone: color for the middle corridor.</summary>
    public string CorridorColorHex { get; set; } = "#1E1E1E";

    // ── IconZone mode ────────────────────────────────────────────────────────
    /// <summary>
    /// Colors for each detected icon cluster (connected component), in order.
    /// Each client uses these colors when rendering its own locally-detected zones.
    /// </summary>
    public List<string> IconZonePaletteHexes  { get; set; } = new();
    /// <summary>Color for free corridor bands.</summary>
    public string       IconCorridorColorHex  { get; set; } = "#1E1E1E";

    /// <summary>
    /// When true, render the colored per-cluster palette rectangles in IconZone mode.
    /// Default: false — the palette is mostly a debug visualization. When false, icon zones
    /// render as plain corridor color (or are simply masked out from the animation layer).
    /// </summary>
    public bool IconZonePaletteEnabled { get; set; } = false;

    /// <summary>
    /// Width in pixels of the feathered fade zone outside each icon zone rectangle.
    /// Pattern cells in this band are drawn at full opacity but then overlaid with a
    /// stepped gradient from the corridor color (opaque at zone edge) to transparent.
    /// 0 = no feathering (legacy per-cell fade behavior). 40–120 px is a good range.
    /// </summary>
    public int IconFadePaddingPx { get; set; } = 50;

    /// <summary>
    /// Pixels to expand each icon zone rectangle beyond its detected bounds.
    /// Zones from neighbouring icons that overlap after expansion merge into one invisible region.
    /// 0 = use detected zone size as-is.
    /// </summary>
    public int IconZoneExpansionPx { get; set; } = 10;
}

/// <summary>
/// Configuration for the animation layer
/// </summary>
public class AnimationLayerConfig
{
    /// <summary>
    /// Path to animation file (video or GIF)
    /// </summary>
    public string AnimationPath { get; set; } = string.Empty;

    /// <summary>
    /// Target height for the animation in pixels.
    /// Width is calculated automatically to maintain aspect ratio.
    /// </summary>
    public int TargetHeight { get; set; } = 720;

    /// <summary>
    /// Whether to loop the animation continuously
    /// </summary>
    public bool Loop { get; set; } = true;

    /// <summary>
    /// Vertical alignment of the animation on the canvas
    /// </summary>
    public VerticalAlignment VerticalAlign { get; set; } = VerticalAlignment.Center;

    /// <summary>
    /// If true, start the animation centered (X=0) instead of off-screen.
    /// Used for simple playback mode where we want stationary centered content.
    /// </summary>
    public bool CenterInitialPosition { get; set; } = false;

    /// <summary>
    /// Speed multiplier for GIF/video playback (1.0 = normal, 2.0 = 2x speed, 0.5 = half speed).
    /// Applied to frame delays to adjust animation speed.
    /// Default: 2.0 (2x speed, compensates for typical slow GIF frame delays)
    /// </summary>
    public double SpeedMultiplier { get; set; } = 2.0;

    /// <summary>
    /// How the animation content is fitted to the screen.
    /// Default: Center (render at native resolution — no scaling, preserves sharpness).
    /// </summary>
    public ContentFitMode FitMode { get; set; } = ContentFitMode.Center;

    /// <summary>
    /// Precomputed animation path in virtual-canvas coordinates (for sequential IconZone mode).
    /// When set by the server/main process, D2D players use this path directly instead of
    /// computing a local path from their own icon positions.
    /// </summary>
    public List<WaypointF>? PrecomputedPath { get; set; }

    /// <summary>
    /// When true, the animation rotates to align with the direction of travel along the corridor path.
    /// Only has effect in IconZone background mode with path-following active.
    /// </summary>
    public bool RotateWithPath { get; set; } = false;

    /// <summary>
    /// Additional animation file paths beyond <see cref="AnimationPath"/>.
    /// Used by the multi-image feature: with <see cref="Pattern"/>, images are randomly distributed across cells;
    /// without a pattern, each image moves with its own seeded offset/phase.
    /// </summary>
    public List<string> AdditionalAnimationPaths { get; set; } = new();

    /// <summary>
    /// All effective animation source paths (primary + additional, with empty entries filtered out).
    /// Method — not serialized — keeps the Models layer free of serializer dependencies.
    /// </summary>
    public IReadOnlyList<string> GetAllAnimationPaths()
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(AnimationPath)) list.Add(AnimationPath);
        foreach (var p in AdditionalAnimationPaths)
            if (!string.IsNullOrWhiteSpace(p)) list.Add(p);
        return list;
    }

    /// <summary>
    /// Color grading applied to the animation layer (rainbow tint, gradient, color cycle, etc.).
    /// </summary>
    public ColorGradingConfig ColorGrading { get; set; } = new();

    /// <summary>
    /// Pattern multiplier configuration. When set, the animation is tiled into a moving grid.
    /// Null = single-instance behavior (back-compat).
    /// </summary>
    public PatternConfig? Pattern { get; set; }

    /// <summary>
    /// When multiple animation paths are configured AND Pattern is null, controls the maximum
    /// per-image position spread (in pixels) around the base movement anchor.
    /// 0 = all images stack on top of each other and move identically.
    /// </summary>
    public float MultiImageSpread { get; set; } = 0f;

    /// <summary>
    /// When multiple animation paths are configured AND Pattern is null, applies a per-image
    /// elapsed-time phase offset so individual images animate slightly out of sync.
    /// 0 = no jitter.
    /// </summary>
    public float MultiImagePhaseJitterMs { get; set; } = 0f;
}

/// <summary>
/// How animation content is fitted to the screen
/// </summary>
public enum ContentFitMode
{
    /// <summary>
    /// Stretch to fill screen (may distort aspect ratio)
    /// </summary>
    Stretch,

    /// <summary>
    /// Display at native resolution, centered on screen
    /// </summary>
    Center,

    /// <summary>
    /// Scale to fit within screen bounds, preserving aspect ratio (letterboxed)
    /// </summary>
    Fit,

    /// <summary>
    /// Scale to fill screen bounds, preserving aspect ratio (may crop)
    /// </summary>
    Fill
}

/// <summary>
/// Vertical alignment options for animation layer
/// </summary>
public enum VerticalAlignment
{
    Top,
    Center,
    Bottom
}

/// <summary>
/// Movement pattern types for animation layer
/// </summary>
public enum MovementType
{
    /// <summary>
    /// No movement - animation stays centered
    /// </summary>
    Static,

    /// <summary>
    /// Straight line from start to end point
    /// </summary>
    Linear,

    /// <summary>
    /// Bounces off virtual canvas edges infinitely
    /// </summary>
    Bounce,

    /// <summary>
    /// Horizontal travel with vertical sine wave oscillation
    /// </summary>
    SineWave,

    /// <summary>
    /// Circular orbit around a center point
    /// </summary>
    Circular,

    /// <summary>
    /// Deterministic pseudo-random wandering (seeded for multi-monitor sync)
    /// </summary>
    RandomWalk
}

/// <summary>
/// Configuration for animation movement across the virtual canvas.
/// All movement is computed as pure math from elapsed time, making it deterministic
/// so all Player.D2D processes compute the same position independently.
/// </summary>
public class MovementConfig
{
    /// <summary>
    /// Movement pattern type
    /// </summary>
    public MovementType Type { get; set; } = MovementType.Linear;

    /// <summary>
    /// Movement speed in pixels per second
    /// </summary>
    public float SpeedPixelsPerSecond { get; set; } = 500f;

    /// <summary>
    /// Start X position in virtual canvas coordinates (null = auto-calculate based on movement type)
    /// </summary>
    public float? StartX { get; set; }

    /// <summary>
    /// Start Y position in virtual canvas coordinates (null = auto-calculate)
    /// </summary>
    public float? StartY { get; set; }

    /// <summary>
    /// End X position in virtual canvas coordinates (null = auto-calculate)
    /// </summary>
    public float? EndX { get; set; }

    /// <summary>
    /// End Y position in virtual canvas coordinates (null = auto-calculate)
    /// </summary>
    public float? EndY { get; set; }

    /// <summary>
    /// Initial direction angle in degrees for Bounce/Linear movement.
    /// 0 = right, 90 = down, 45 = diagonal down-right, etc.
    /// </summary>
    public float DirectionAngleDegrees { get; set; } = 30f;

    /// <summary>
    /// Orbit center X in virtual canvas coordinates (null = canvas center)
    /// </summary>
    public float? OrbitCenterX { get; set; }

    /// <summary>
    /// Orbit center Y in virtual canvas coordinates (null = canvas center)
    /// </summary>
    public float? OrbitCenterY { get; set; }

    /// <summary>
    /// Orbit radius in pixels for Circular movement
    /// </summary>
    public float OrbitRadiusPixels { get; set; } = 500f;

    /// <summary>
    /// Vertical oscillation amplitude in pixels for SineWave movement
    /// </summary>
    public float WaveAmplitudePixels { get; set; } = 200f;

    /// <summary>
    /// Vertical oscillation frequency in Hz for SineWave movement
    /// </summary>
    public float WaveFrequencyHz { get; set; } = 0.5f;

    /// <summary>
    /// Random seed for deterministic RandomWalk (same seed = same path on all monitors)
    /// </summary>
    public int RandomSeed { get; set; } = 42;

    /// <summary>
    /// Interval in ms between random walk waypoints
    /// </summary>
    public float RandomStepIntervalMs { get; set; } = 1000f;

    /// <summary>
    /// Whether movement loops when reaching the end (Linear) or continues indefinitely (Bounce/Circular/RandomWalk)
    /// </summary>
    public bool Loop { get; set; } = true;

    /// <summary>
    /// Number of random walk steps per iteration before the path pattern changes.
    /// 0 = never change (same infinite sequence). Default 20 = new pattern every ~20 steps.
    /// All monitors use the same iteration index (derived from elapsed time), so sync is maintained.
    /// </summary>
    public int IterationStepCount { get; set; } = 20;
}

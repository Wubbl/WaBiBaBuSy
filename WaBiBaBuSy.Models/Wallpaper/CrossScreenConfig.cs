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
    TiledImage
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
    /// Default: Stretch (fills entire screen, may distort aspect ratio)
    /// </summary>
    public ContentFitMode FitMode { get; set; } = ContentFitMode.Stretch;
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

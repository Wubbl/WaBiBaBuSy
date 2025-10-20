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

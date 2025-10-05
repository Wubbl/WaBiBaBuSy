namespace WaBiBaBuSy.Models;

/// <summary>
/// Configuration for wallpaper rendering.
/// </summary>
public class WallpaperConfig
{
    /// <summary>
    /// Unique identifier for the wallpaper content.
    /// </summary>
    public string ContentId { get; set; } = string.Empty;

    /// <summary>
    /// Type of wallpaper (Image, Video, Gif).
    /// </summary>
    public WallpaperType Type { get; set; }

    /// <summary>
    /// Local file path to the wallpaper content.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Whether to loop the wallpaper playback.
    /// </summary>
    public bool Loop { get; set; } = true;

    /// <summary>
    /// Whether hardware acceleration is enabled.
    /// </summary>
    public bool HardwareAcceleration { get; set; } = true;

    /// <summary>
    /// Maximum frames per second (0 = unlimited).
    /// </summary>
    public int MaxFPS { get; set; } = 60;

    /// <summary>
    /// Monitor index to render on (-1 = all monitors).
    /// </summary>
    public int MonitorIndex { get; set; } = -1;
}

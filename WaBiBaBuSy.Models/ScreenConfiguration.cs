namespace WaBiBaBuSy.Models;

/// <summary>
/// Represents the screen configuration of a client machine.
/// </summary>
public class ScreenConfiguration
{
    /// <summary>
    /// Number of monitors/screens.
    /// </summary>
    public int MonitorCount { get; set; }

    /// <summary>
    /// Total combined resolution width.
    /// </summary>
    public int TotalWidth { get; set; }

    /// <summary>
    /// Total combined resolution height.
    /// </summary>
    public int TotalHeight { get; set; }

    /// <summary>
    /// Individual monitor information.
    /// </summary>
    public List<MonitorInfo> Monitors { get; set; } = new();
}

/// <summary>
/// Information about a single monitor.
/// </summary>
public class MonitorInfo
{
    /// <summary>
    /// Monitor index (0-based).
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Monitor width in pixels.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Monitor height in pixels.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// X position of this monitor in virtual screen space.
    /// </summary>
    public int X { get; set; }

    /// <summary>
    /// Y position of this monitor in virtual screen space.
    /// </summary>
    public int Y { get; set; }

    /// <summary>
    /// Whether this is the primary monitor.
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Monitor device name.
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Current refresh rate in Hz (e.g. 60, 75, 165). 0 if unknown.
    /// </summary>
    public int RefreshRate { get; set; }
}

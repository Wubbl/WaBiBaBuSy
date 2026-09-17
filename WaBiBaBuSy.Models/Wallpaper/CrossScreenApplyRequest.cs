namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Everything a client needs to start a cross-screen D2D animation, as received
/// from the server via a LOAD command with renderer_type = "d2d_crossscreen".
/// The JSON fields carry full configs (movement/animation/background); the scalar
/// fields are the legacy fallbacks for servers that don't send JSON yet.
/// </summary>
public class CrossScreenApplyRequest
{
    /// <summary>Locally-cached content file path (already downloaded from the server).</summary>
    public required string FilePath { get; init; }

    /// <summary>Which local monitor to render on.</summary>
    public int MonitorIndex { get; init; }

    public string BackgroundColor { get; init; } = "#000000";
    public int FitMode { get; init; }
    public int VirtualCanvasWidth { get; init; }
    public int MonitorOffsetX { get; init; }

    /// <summary>Virtual canvas height (max node height). 0 = legacy server → use this monitor's height.</summary>
    public int VirtualCanvasHeight { get; init; }

    /// <summary>This node's Y offset inside the virtual canvas (vertical centering on mixed heights).</summary>
    public int MonitorOffsetY { get; init; }

    /// <summary>This node's traversal index (0-based) — drives Wave-mode phase in Simultaneous distribution.</summary>
    public int NodeOrder { get; init; }
    public long SharedStartTimestampMs { get; init; }
    public int PixelsPerSecond { get; init; }
    public bool PerMonitorMode { get; init; }
    public int MovementType { get; init; }

    public string PatternJson { get; init; } = "";
    public string ColorGradingJson { get; init; } = "";
    public string MovementJson { get; init; } = "";
    public string AnimationJson { get; init; } = "";
    public string BackgroundJson { get; init; } = "";
}

namespace WaBiBaBuSy.Models.Animation;

/// <summary>
/// Animation metadata sent from server to client for distributed rendering.
/// Contains all information needed for a client to render animation locally.
/// </summary>
public class AnimationMetadata
{
    /// <summary>
    /// Unique animation ID (UUID or file hash)
    /// </summary>
    public string AnimationId { get; set; } = string.Empty;

    /// <summary>
    /// Server-side path to animation file (e.g., "/Content/animation.mp4")
    /// </summary>
    public string ContentPath { get; set; } = string.Empty;

    /// <summary>
    /// Target height for animation rendering in pixels (e.g., 720)
    /// </summary>
    public int TargetHeightPx { get; set; } = 720;

    /// <summary>
    /// Horizontal animation speed in pixels per second (e.g., 500)
    /// </summary>
    public int AnimationSpeedPxSec { get; set; } = 500;

    /// <summary>
    /// Animation duration in milliseconds (e.g., 5000 = 5 seconds)
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// UTC timestamp (milliseconds since epoch) when client should start rendering
    /// </summary>
    public long StartTimestampUtc { get; set; }

    /// <summary>
    /// Background layer configuration (color/image)
    /// </summary>
    public BackgroundLayerConfig? Background { get; set; }

    /// <summary>
    /// Whether animation should loop after completion
    /// </summary>
    public bool Loop { get; set; }

    /// <summary>
    /// Physical monitor index on client machine
    /// </summary>
    public int TargetMonitorIndex { get; set; }

    /// <summary>
    /// Calculate how long the client should wait before starting (milliseconds)
    /// </summary>
    public long CalculateWaitTimeMs()
    {
        var currentUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var delayMs = StartTimestampUtc - currentUtcMs;
        return Math.Max(0, delayMs);
    }

    /// <summary>
    /// Get the time this animation will complete (UTC milliseconds)
    /// </summary>
    public long GetCompletionTimestampUtc()
    {
        return StartTimestampUtc + DurationMs;
    }

    /// <summary>
    /// Validate that all required fields are set
    /// </summary>
    public bool IsValid(out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(AnimationId))
        {
            errorMessage = "AnimationId is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ContentPath))
        {
            errorMessage = "ContentPath is required";
            return false;
        }

        if (DurationMs <= 0)
        {
            errorMessage = "DurationMs must be greater than 0";
            return false;
        }

        if (StartTimestampUtc <= 0)
        {
            errorMessage = "StartTimestampUtc must be set";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }
}

/// <summary>
/// Background layer configuration for animation composition
/// </summary>
public class BackgroundLayerConfig
{
    /// <summary>
    /// Background rendering mode
    /// </summary>
    public enum BackgroundMode
    {
        /// <summary>Solid color background</summary>
        SolidColor = 0,

        /// <summary>Image stretched to fit screen</summary>
        StretchedImage = 1,

        /// <summary>Image tiled to fill screen</summary>
        TiledImage = 2,
    }

    /// <summary>
    /// Which background mode to use
    /// </summary>
    public BackgroundMode Mode { get; set; } = BackgroundMode.SolidColor;

    /// <summary>
    /// Hex color for solid mode (e.g., "#000000" for black)
    /// </summary>
    public string ColorHex { get; set; } = "#000000";

    /// <summary>
    /// Path to image for image modes (server-side path)
    /// </summary>
    public string ImagePath { get; set; } = string.Empty;
}

namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Command sent from parent to D2DPlayer to start animation playback.
/// Contains timing information for synchronized playback.
/// </summary>
public class PlayerCommandStartAnimation : PlayerMessageBase
{
    public PlayerCommandStartAnimation()
    {
        MessageType = "cmd_start_animation";
    }

    /// <summary>
    /// Start timestamp in milliseconds (Unix time or elapsed time from reference point)
    /// </summary>
    public long StartTimestampMs { get; set; } = 0;

    /// <summary>
    /// Animation movement speed in pixels per second (for cross-screen animations)
    /// Set to 0 for static/centered animations
    /// </summary>
    public int PixelsPerSecond { get; set; } = 0;
}

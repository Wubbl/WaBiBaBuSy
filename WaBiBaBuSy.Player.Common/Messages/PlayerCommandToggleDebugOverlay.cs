namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Command to toggle the debug overlay on a D2D player.
/// Overlay renders animation path waypoints and an info panel
/// (monitor offset, animation dimensions, FPS, etc.) on top of the animation.
/// </summary>
public class PlayerCommandToggleDebugOverlay : PlayerMessageBase
{
    public PlayerCommandToggleDebugOverlay()
    {
        MessageType = "cmd_toggle_debug_overlay";
    }

    /// <summary>
    /// When true, the overlay becomes visible. When false, it is hidden.
    /// Use <see cref="Toggle"/> instead to flip whatever state the player is in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// If true, ignore <see cref="Enabled"/> and flip the current overlay state.
    /// </summary>
    public bool Toggle { get; set; }
}

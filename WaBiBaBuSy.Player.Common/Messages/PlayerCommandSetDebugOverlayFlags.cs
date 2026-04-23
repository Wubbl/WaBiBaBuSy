namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Sets all debug overlay flags explicitly on a D2D player.
/// </summary>
public class PlayerCommandSetDebugOverlayFlags : PlayerMessageBase
{
    public PlayerCommandSetDebugOverlayFlags()
    {
        MessageType = "cmd_set_debug_overlay_flags";
    }

    public bool Enabled { get; set; }
    public bool ShowPath { get; set; }
    public bool ShowIconRects { get; set; }
    public bool ShowZoneBandOutlines { get; set; }
    public bool ShowInfoPanel { get; set; }
}

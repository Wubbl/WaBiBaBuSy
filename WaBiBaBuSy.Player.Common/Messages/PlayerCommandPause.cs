namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Pause (a fullscreen app covers this player's monitor) or resume rendering: while paused the player hides its window,
/// draws and presents nothing and holds its videos; resuming needs no resync (every position follows the shared clock).
/// No stdout reply.
/// </summary>
public class PlayerCommandPause : PlayerMessageBase
{
    public PlayerCommandPause()
    {
        MessageType = "cmd_pause";
    }

    public bool Paused { get; set; }
}

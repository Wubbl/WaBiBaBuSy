namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Command sent from parent to player to start playback.
/// </summary>
public class PlayerCommandPlay : PlayerMessageBase
{
    public PlayerCommandPlay()
    {
        MessageType = "cmd_play";
    }
}

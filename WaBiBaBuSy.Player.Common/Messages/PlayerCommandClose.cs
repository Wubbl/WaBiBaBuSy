namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Command sent from parent to player to close/shutdown.
/// </summary>
public class PlayerCommandClose : PlayerMessageBase
{
    public PlayerCommandClose()
    {
        MessageType = "cmd_close";
    }
}

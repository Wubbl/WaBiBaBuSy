namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Command sent from parent to player to refresh composition after SetParent.
/// </summary>
public class PlayerCommandRefresh : PlayerMessageBase
{
    public PlayerCommandRefresh()
    {
        MessageType = "cmd_refresh";
    }
}

namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Command sent from parent to player AFTER SetParent to force WPF composition refresh.
/// </summary>
public class PlayerCommandRefresh : PlayerMessageBase
{
    public PlayerCommandRefresh()
    {
        MessageType = "cmd_refresh";
    }
}

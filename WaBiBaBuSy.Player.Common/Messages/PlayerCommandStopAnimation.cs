namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Command sent from parent to D2DPlayer to stop animation playback.
/// </summary>
public class PlayerCommandStopAnimation : PlayerMessageBase
{
    public PlayerCommandStopAnimation()
    {
        MessageType = "cmd_stop_animation";
    }
}

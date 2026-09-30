namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Automated test mode: timecode strip on/off and a simulated clock skew (ms added to the player's
/// clock). The player sends no stdout reply.
/// </summary>
public class PlayerCommandTestMode : PlayerMessageBase
{
    public PlayerCommandTestMode()
    {
        MessageType = "cmd_test_mode";
    }

    public bool Timecode { get; set; }
    public int ClockSkewMs { get; set; }
}

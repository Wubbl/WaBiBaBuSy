namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Item transition (roadmap 2.5) for this player: <see cref="Incoming"/> = it starts the new item (sent before its
/// start command), otherwise it plays the item being replaced. Times are in the player's clock, like the start.
/// No stdout reply.
/// </summary>
public class PlayerCommandTransition : PlayerMessageBase
{
    public PlayerCommandTransition()
    {
        MessageType = "cmd_transition";
    }

    public bool Incoming { get; set; }
    /// <summary><c>SceneTransition.Encode()</c> form, e.g. "Fade:1500".</summary>
    public string Transition { get; set; } = string.Empty;
    /// <summary>The new item's shared start (UTC ms).</summary>
    public long StartUtcMs { get; set; }
}

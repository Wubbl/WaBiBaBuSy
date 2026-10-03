using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// One-shot show event (roadmap 2.4). The player keeps it and draws it above the scene from T0 for its duration;
/// a second copy with the same EventId (replay) is ignored. No stdout reply.
/// </summary>
public class PlayerCommandShowEvent : PlayerMessageBase
{
    public PlayerCommandShowEvent()
    {
        MessageType = "cmd_show_event";
    }

    public ShowEvent Event { get; set; } = new();
}

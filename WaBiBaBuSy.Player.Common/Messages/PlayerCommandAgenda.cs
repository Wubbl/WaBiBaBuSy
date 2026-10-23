using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// The room's agenda (timetable overlay): the player keeps the latest one and draws it after the show events until a
/// new one arrives. <see cref="FullScreen"/>: this monitor is an agenda screen (full-size timetable). No stdout reply.
/// </summary>
public class PlayerCommandAgenda : PlayerMessageBase
{
    public PlayerCommandAgenda()
    {
        MessageType = "cmd_agenda";
    }

    /// <summary>Null = no agenda.</summary>
    public Agenda? Agenda { get; set; }

    public bool FullScreen { get; set; }
}

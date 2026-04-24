using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Player.Common.Messages;

public class PlayerCommandUpdatePath : PlayerMessageBase
{
    public PlayerCommandUpdatePath() { MessageType = "cmd_update_path"; }
    public List<WaypointF> Path { get; set; } = new();
}

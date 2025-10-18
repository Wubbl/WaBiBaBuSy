namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Command sent from parent to player to load a wallpaper file.
/// </summary>
public class PlayerCommandLoad : PlayerMessageBase
{
    public PlayerCommandLoad()
    {
        MessageType = "cmd_load";
    }

    /// <summary>
    /// Full path to the wallpaper file to load.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;
}

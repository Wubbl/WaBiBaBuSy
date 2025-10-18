namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Message sent from player to parent indicating wallpaper was loaded successfully.
/// </summary>
public class PlayerMessageLoaded : PlayerMessageBase
{
    public PlayerMessageLoaded()
    {
        MessageType = "loaded";
    }

    /// <summary>
    /// Whether the wallpaper loaded successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Optional error message if Success is false.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

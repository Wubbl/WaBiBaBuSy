namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Message sent from player to parent containing the window handle.
/// </summary>
public class PlayerMessageHwnd : PlayerMessageBase
{
    public PlayerMessageHwnd()
    {
        MessageType = "hwnd";
    }

    /// <summary>
    /// The window handle (HWND) as an integer.
    /// </summary>
    public int Hwnd { get; set; }
}

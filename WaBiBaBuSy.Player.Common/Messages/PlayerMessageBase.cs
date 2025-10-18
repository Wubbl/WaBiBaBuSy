namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Base class for all player IPC messages.
/// </summary>
public abstract class PlayerMessageBase
{
    /// <summary>
    /// Message type discriminator for JSON deserialization.
    /// </summary>
    public string MessageType { get; set; } = string.Empty;
}

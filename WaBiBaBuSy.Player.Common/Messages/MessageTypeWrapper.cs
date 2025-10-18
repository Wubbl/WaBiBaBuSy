namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Helper class for deserializing just the MessageType field to determine concrete type.
/// </summary>
internal class MessageTypeWrapper
{
    public string MessageType { get; set; } = string.Empty;
}

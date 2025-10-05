namespace WaBiBaBuSy.Models;

/// <summary>
/// Represents the connection status of a client.
/// </summary>
public enum ClientStatus
{
    /// <summary>
    /// Client is disconnected.
    /// </summary>
    Disconnected,

    /// <summary>
    /// Client is connected but idle.
    /// </summary>
    Connected,

    /// <summary>
    /// Client is synchronizing content.
    /// </summary>
    Syncing,

    /// <summary>
    /// Client is currently playing wallpaper.
    /// </summary>
    Playing,

    /// <summary>
    /// Client is experiencing an error.
    /// </summary>
    Error
}

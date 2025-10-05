namespace WaBiBaBuSy.Models;

/// <summary>
/// Information about a connected client.
/// </summary>
public class ClientInfo
{
    /// <summary>
    /// Unique client identifier (GUID).
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Client machine hostname.
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// Client IP address.
    /// </summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>
    /// Screen configuration of the client.
    /// </summary>
    public ScreenConfiguration? ScreenConfig { get; set; }

    /// <summary>
    /// Timestamp when client registered (Unix milliseconds).
    /// </summary>
    public long RegistrationTimestamp { get; set; }

    /// <summary>
    /// Current status of the client.
    /// </summary>
    public ClientStatus Status { get; set; }

    /// <summary>
    /// Display order position for topology visualization.
    /// </summary>
    public int OrderPosition { get; set; }

    /// <summary>
    /// Last heartbeat timestamp (Unix milliseconds).
    /// </summary>
    public long LastHeartbeat { get; set; }
}

namespace WaBiBaBuSy.Models;

/// <summary>
/// Represents a scheduled synchronization event.
/// </summary>
public class SyncSchedule
{
    /// <summary>
    /// Content ID to be synchronized.
    /// </summary>
    public string ContentId { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when playback should start.
    /// </summary>
    public DateTime StartTimeUtc { get; set; }

    /// <summary>
    /// Target playback position in milliseconds.
    /// </summary>
    public long TargetPositionMs { get; set; }

    /// <summary>
    /// Sequence number for ordering sync commands.
    /// </summary>
    public int SequenceNumber { get; set; }
}

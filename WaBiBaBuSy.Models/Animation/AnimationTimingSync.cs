namespace WaBiBaBuSy.Models.Animation;

/// <summary>
/// Timing synchronization message broadcast by server to all animating clients.
/// Helps clients detect and correct clock drift.
/// </summary>
public class AnimationTimingSync
{
    /// <summary>
    /// Current UTC timestamp on server (milliseconds since epoch)
    /// </summary>
    public long ServerTimestampUtc { get; set; }

    /// <summary>
    /// ID of the animation this sync is for
    /// </summary>
    public string AnimationId { get; set; } = string.Empty;

    /// <summary>
    /// Where the animation should be at this time (milliseconds elapsed since start)
    /// </summary>
    public int ExpectedPositionMs { get; set; }

    /// <summary>
    /// Calculate clock offset in milliseconds (how much client clock differs from expected)
    /// </summary>
    public int CalculateClockOffsetMs(long clientMeasuredTimeMs)
    {
        return (int)(clientMeasuredTimeMs - ExpectedPositionMs);
    }

    /// <summary>
    /// Check if drift exceeds tolerance (default 50ms)
    /// </summary>
    public bool IsDriftExceeded(long clientMeasuredTimeMs, int toleranceMs = 50)
    {
        var drift = CalculateClockOffsetMs(clientMeasuredTimeMs);
        return Math.Abs(drift) > toleranceMs;
    }
}

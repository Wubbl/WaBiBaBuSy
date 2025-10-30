namespace WaBiBaBuSy.Models.Animation;

/// <summary>
/// Report sent by client to server when animation completes.
/// Used for sequential handoff and scheduling next animation.
/// </summary>
public class AnimationCompleteReport
{
    /// <summary>
    /// ID of the client that completed the animation
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// ID of the animation that completed
    /// </summary>
    public string AnimationId { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when animation completed (milliseconds since epoch)
    /// </summary>
    public long CompletionTimestampUtc { get; set; }

    /// <summary>
    /// Whether animation completed successfully
    /// </summary>
    public bool Successful { get; set; } = true;

    /// <summary>
    /// Error message if animation failed
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Get duration since animation started (in milliseconds)
    /// </summary>
    public long GetElapsedMs(long startTimestampUtc)
    {
        return CompletionTimestampUtc - startTimestampUtc;
    }
}

/// <summary>
/// Response to animation control operations
/// </summary>
public class AnimationAck
{
    /// <summary>
    /// Whether operation succeeded
    /// </summary>
    public bool Success { get; set; } = true;

    /// <summary>
    /// Message describing result or error
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Create successful acknowledgment
    /// </summary>
    public static AnimationAck CreateSuccess(string message = "OK")
    {
        return new AnimationAck { Success = true, Message = message };
    }

    /// <summary>
    /// Create failure acknowledgment
    /// </summary>
    public static AnimationAck CreateFailure(string message)
    {
        return new AnimationAck { Success = false, Message = message };
    }
}

/// <summary>
/// Request to stop animation on client
/// </summary>
public class AnimationStopRequest
{
    /// <summary>
    /// ID of animation to stop
    /// </summary>
    public string AnimationId { get; set; } = string.Empty;
}

/// <summary>
/// Status of animation on client
/// </summary>
public class AnimationStatusResponse
{
    /// <summary>
    /// Animation status enum
    /// </summary>
    public enum AnimationStatus
    {
        /// <summary>Idle, no animation running</summary>
        Idle = 0,

        /// <summary>Waiting for start time</summary>
        Waiting = 1,

        /// <summary>Currently rendering</summary>
        Rendering = 2,

        /// <summary>Paused</summary>
        Paused = 3,

        /// <summary>Completed successfully</summary>
        Completed = 4,

        /// <summary>Failed with error</summary>
        Failed = 5,
    }

    /// <summary>
    /// ID of the animation
    /// </summary>
    public string AnimationId { get; set; } = string.Empty;

    /// <summary>
    /// Current status
    /// </summary>
    public AnimationStatus Status { get; set; } = AnimationStatus.Idle;

    /// <summary>
    /// Current position in animation (milliseconds elapsed)
    /// </summary>
    public int CurrentPositionMs { get; set; }

    /// <summary>
    /// Error message if status is Failed
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Check if animation is currently playing
    /// </summary>
    public bool IsAnimating()
    {
        return Status is AnimationStatus.Waiting or AnimationStatus.Rendering;
    }
}

/// <summary>
/// Request to get animation status
/// </summary>
public class AnimationStatusRequest
{
    /// <summary>
    /// ID of animation to query
    /// </summary>
    public string AnimationId { get; set; } = string.Empty;
}

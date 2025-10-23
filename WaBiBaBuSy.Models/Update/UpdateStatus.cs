namespace WaBiBaBuSy.Models.Update;

/// <summary>
/// Status of an update operation
/// </summary>
public enum UpdateStatusType
{
    /// <summary>
    /// Checking for updates
    /// </summary>
    Checking,

    /// <summary>
    /// Downloading update package
    /// </summary>
    Downloading,

    /// <summary>
    /// Update package downloaded
    /// </summary>
    Downloaded,

    /// <summary>
    /// Verifying update package integrity
    /// </summary>
    Verifying,

    /// <summary>
    /// Applying update (replacing files)
    /// </summary>
    Applying,

    /// <summary>
    /// Update successfully applied
    /// </summary>
    Applied,

    /// <summary>
    /// Update failed
    /// </summary>
    Failed,

    /// <summary>
    /// Update cancelled by user
    /// </summary>
    Cancelled
}

/// <summary>
/// Detailed update status information
/// </summary>
public class UpdateStatusInfo
{
    /// <summary>
    /// Current status
    /// </summary>
    public UpdateStatusType Status { get; set; }

    /// <summary>
    /// Progress percentage (0-100)
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Error message if status is Failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Update identifier (version string)
    /// </summary>
    public string UpdateId { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of last status update
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

namespace WaBiBaBuSy.Models.Update;

/// <summary>
/// Information about an available update
/// </summary>
public class UpdateInfo
{
    /// <summary>
    /// New version string (e.g., "2.1.0")
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// New build number
    /// </summary>
    public int BuildNumber { get; set; }

    /// <summary>
    /// Total package size in bytes
    /// </summary>
    public long PackageSize { get; set; }

    /// <summary>
    /// SHA-256 hash of the complete update package
    /// </summary>
    public string PackageHash { get; set; } = string.Empty;

    /// <summary>
    /// Release notes or change description
    /// </summary>
    public string ReleaseNotes { get; set; } = string.Empty;

    /// <summary>
    /// Whether this update is mandatory
    /// </summary>
    public bool IsMandatory { get; set; }

    /// <summary>
    /// List of files included in the update package
    /// </summary>
    public List<string> PackageFiles { get; set; } = new();
}

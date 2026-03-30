using System.Text.Json.Serialization;

namespace WaBiBaBuSy.Models.Update;

/// <summary>
/// Manifest describing an update package
/// </summary>
public class UpdateManifest
{
    /// <summary>
    /// Version string (e.g., "2.1.0")
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Build number
    /// </summary>
    public int BuildNumber { get; set; }

    /// <summary>
    /// Release date (UTC)
    /// </summary>
    public DateTime ReleaseDate { get; set; }

    /// <summary>
    /// Minimum compatible version that can be upgraded
    /// </summary>
    public string MinimumCompatibleVersion { get; set; } = string.Empty;

    /// <summary>
    /// List of files in the update package
    /// </summary>
    public List<UpdateFileInfo> Files { get; set; } = new();

    /// <summary>
    /// Release notes
    /// </summary>
    public string ReleaseNotes { get; set; } = string.Empty;
}

/// <summary>
/// Information about a file in an update package
/// </summary>
public class UpdateFileInfo
{
    /// <summary>
    /// Relative path within the package (e.g., "binaries/WaBiBaBuSy.UI.exe")
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// SHA-256 hash of the file
    /// </summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Action to perform with this file (replace, delete, etc.)
    /// </summary>
    public FileAction Action { get; set; } = FileAction.Replace;
}

/// <summary>
/// Action to perform with an update file
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileAction
{
    /// <summary>
    /// Replace existing file or create if not exists
    /// </summary>
    Replace,

    /// <summary>
    /// Delete existing file
    /// </summary>
    Delete,

    /// <summary>
    /// Add new file (fail if already exists)
    /// </summary>
    Add
}

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Represents a wallpaper item in the gallery (for persistence)
/// </summary>
public class WallpaperGalleryItem
{
    public string WallpaperId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "Video", "Image", "Gif"
    public string Resolution { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    /// <summary>Video length / one GIF loop in ms; 0 = unknown or still image.</summary>
    public long DurationMs { get; set; }
    /// <summary>GIF frame count; 0 = unknown.</summary>
    public int FrameCount { get; set; }
    /// <summary>Video frame rate; 0 = unknown.</summary>
    public double FrameRate { get; set; }
    public bool IsActive { get; set; }
    public DateTime AddedDate { get; set; } = DateTime.UtcNow;
}

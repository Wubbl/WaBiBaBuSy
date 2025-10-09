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
    public bool IsActive { get; set; }
    public DateTime AddedDate { get; set; } = DateTime.UtcNow;
}

using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// Represents a wallpaper item in the gallery
/// </summary>
public partial class WallpaperItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _wallpaperId = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _thumbnailPath = string.Empty;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeName), nameof(TypeBadgeBrush), nameof(Caption), nameof(ToolTipText))]
    private WallpaperType _type;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResolution), nameof(Caption), nameof(ToolTipText))]
    private string _resolution = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileSize), nameof(ToolTipText))]
    private long _fileSizeBytes;

    /// <summary>Video length / one GIF loop in ms; 0 = unknown or still image.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption), nameof(ToolTipText))]
    private long _durationMs;

    /// <summary>GIF frame count; 0 = unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption), nameof(ToolTipText))]
    private int _frameCount;

    /// <summary>Video frame rate; 0 = unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private double _frameRate;

    /// <summary>
    /// Thumbnail caption: resolution, plus length (video) or frames + length (GIF).
    /// </summary>
    public string Caption => MediaDetails.CaptionLine(Type.ToString(), Resolution, DurationMs, FrameCount);

    /// <summary>
    /// Multi-line tooltip: name, type · resolution · size, animation details, path.
    /// </summary>
    public string ToolTipText => MediaDetails.ToolTip(Name, Type.ToString(), Resolution, FileSize,
        DurationMs, FrameCount, FrameRate, FilePath);

    /// <summary>
    /// True while the metadata of an animated file (or any resolution) still has to be read.
    /// </summary>
    public bool NeedsMediaDetails =>
        string.IsNullOrEmpty(MediaDetails.CleanResolution(Resolution))
        || (Type == WallpaperType.Gif && FrameCount == 0)
        || (Type == WallpaperType.Video && DurationMs == 0);

    /// <summary>
    /// Short display label for the wallpaper type
    /// </summary>
    public string TypeName => Type switch
    {
        WallpaperType.Video => "VIDEO",
        WallpaperType.Image => "IMG",
        WallpaperType.Gif   => "GIF",
        _                   => "?"
    };

    /// <summary>
    /// Colored brush for the type badge (Video=green, Image=blue, GIF=purple)
    /// </summary>
    public IBrush TypeBadgeBrush => Type switch
    {
        WallpaperType.Video => new SolidColorBrush(Color.Parse("#1A7F37")),
        WallpaperType.Image => new SolidColorBrush(Color.Parse("#0D6EFD")),
        WallpaperType.Gif   => new SolidColorBrush(Color.Parse("#7B2FBE")),
        _                   => Brushes.Gray
    };

    /// <summary>
    /// True when a resolution string is available (for conditional visibility)
    /// </summary>
    public bool HasResolution => !string.IsNullOrEmpty(Resolution);

    /// <summary>
    /// Human-readable file size
    /// </summary>
    public string FileSize
    {
        get
        {
            if (FileSizeBytes < 1024)
                return $"{FileSizeBytes} B";
            if (FileSizeBytes < 1024 * 1024)
                return $"{FileSizeBytes / 1024.0:F1} KB";
            if (FileSizeBytes < 1024 * 1024 * 1024)
                return $"{FileSizeBytes / (1024.0 * 1024.0):F1} MB";
            return $"{FileSizeBytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        }
    }

    /// <summary>
    /// Load thumbnail from file path
    /// </summary>
    public void LoadThumbnail()
    {
        if (string.IsNullOrEmpty(ThumbnailPath) || !File.Exists(ThumbnailPath))
            return;

        try
        {
            Thumbnail = new Bitmap(ThumbnailPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading thumbnail for {Name}: {ex.Message}");
            Thumbnail = null;
        }
    }
}

/// <summary>
/// Types of wallpapers supported
/// </summary>
public enum WallpaperType
{
    Image,
    Video,
    Gif
}

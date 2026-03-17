using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// Represents a wallpaper item in the gallery
/// </summary>
public partial class WallpaperItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _wallpaperId = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _thumbnailPath = string.Empty;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private WallpaperType _type;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _resolution = string.Empty;

    [ObservableProperty]
    private long _fileSizeBytes;

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

using System;
using System.IO;
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
    private string _resolution = string.Empty;

    [ObservableProperty]
    private long _fileSizeBytes;

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

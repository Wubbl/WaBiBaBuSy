using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

public partial class CrossScreenConfigViewModel : ViewModelBase
{
    private IStorageProvider? _storageProvider;

    [ObservableProperty]
    private int _backgroundModeIndex = 0;

    [ObservableProperty]
    private string _backgroundColor = "#000000";

    [ObservableProperty]
    private string _backgroundImagePath = string.Empty;

    [ObservableProperty]
    private string _animationPath = string.Empty;

    [ObservableProperty]
    private int _animationHeight = 720;

    [ObservableProperty]
    private int _verticalAlignmentIndex = 1; // Center

    [ObservableProperty]
    private bool _animationLoop = true;

    [ObservableProperty]
    private int _animationSpeed = 500;

    public bool IsSolidColorMode => BackgroundModeIndex == 0;
    public bool IsImageMode => BackgroundModeIndex == 1 || BackgroundModeIndex == 2;

    public bool DialogResult { get; private set; }

    public CrossScreenConfigViewModel()
    {
    }

    partial void OnBackgroundModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSolidColorMode));
        OnPropertyChanged(nameof(IsImageMode));
    }

    public void SetStorageProvider(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
    }

    public void LoadFromConfig(CrossScreenConfig config)
    {
        BackgroundModeIndex = config.Background.Mode switch
        {
            BackgroundMode.SolidColor => 0,
            BackgroundMode.StretchedImage => 1,
            BackgroundMode.TiledImage => 2,
            _ => 0
        };

        BackgroundColor = config.Background.ColorHex;
        BackgroundImagePath = config.Background.ImagePath ?? string.Empty;
        AnimationPath = config.Animation.AnimationPath;
        AnimationHeight = config.Animation.TargetHeight;
        AnimationLoop = config.Animation.Loop;
        AnimationSpeed = config.AnimationSpeedPxPerSecond;

        VerticalAlignmentIndex = config.Animation.VerticalAlign switch
        {
            VerticalAlignment.Top => 0,
            VerticalAlignment.Center => 1,
            VerticalAlignment.Bottom => 2,
            _ => 1
        };
    }

    public CrossScreenConfig BuildConfig()
    {
        var backgroundMode = BackgroundModeIndex switch
        {
            0 => BackgroundMode.SolidColor,
            1 => BackgroundMode.StretchedImage,
            2 => BackgroundMode.TiledImage,
            _ => BackgroundMode.SolidColor
        };

        var verticalAlign = VerticalAlignmentIndex switch
        {
            0 => VerticalAlignment.Top,
            1 => VerticalAlignment.Center,
            2 => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Center
        };

        return new CrossScreenConfig
        {
            Background = new BackgroundLayerConfig
            {
                Mode = backgroundMode,
                ColorHex = BackgroundColor,
                ImagePath = string.IsNullOrWhiteSpace(BackgroundImagePath) ? null : BackgroundImagePath
            },
            Animation = new AnimationLayerConfig
            {
                AnimationPath = AnimationPath,
                TargetHeight = AnimationHeight,
                Loop = AnimationLoop,
                VerticalAlign = verticalAlign
            },
            AnimationSpeedPxPerSecond = AnimationSpeed
        };
    }

    [RelayCommand]
    private async Task BrowseBackgroundImage()
    {
        if (_storageProvider == null) return;

        var fileTypes = new FilePickerFileType[]
        {
            new("Image Files") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp" } },
            new("All Files") { Patterns = new[] { "*.*" } }
        };

        var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Background Image",
            AllowMultiple = false,
            FileTypeFilter = fileTypes
        });

        if (files.Count > 0)
        {
            BackgroundImagePath = files[0].Path.LocalPath;
        }
    }

    [RelayCommand]
    private async Task BrowseAnimation()
    {
        if (_storageProvider == null) return;

        var fileTypes = new FilePickerFileType[]
        {
            new("Video Files") { Patterns = new[] { "*.mp4", "*.avi", "*.mkv", "*.mov", "*.wmv", "*.webm" } },
            new("GIF Files") { Patterns = new[] { "*.gif" } },
            new("All Files") { Patterns = new[] { "*.*" } }
        };

        var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Animation File",
            AllowMultiple = false,
            FileTypeFilter = fileTypes
        });

        if (files.Count > 0)
        {
            AnimationPath = files[0].Path.LocalPath;
        }
    }

    [RelayCommand]
    private void Ok()
    {
        // Validate configuration
        if (BackgroundModeIndex > 0 && string.IsNullOrWhiteSpace(BackgroundImagePath))
        {
            // TODO: Show error message
            return;
        }

        if (string.IsNullOrWhiteSpace(AnimationPath))
        {
            // TODO: Show error message
            return;
        }

        if (!File.Exists(AnimationPath))
        {
            // TODO: Show error message
            return;
        }

        DialogResult = true;
        // Close dialog - will be handled by MainWindowViewModel
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        // Close dialog - will be handled by MainWindowViewModel
    }
}

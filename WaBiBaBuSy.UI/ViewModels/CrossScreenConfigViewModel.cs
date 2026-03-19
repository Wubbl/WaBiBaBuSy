using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Services;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// Represents a selectable monitor/client for animation
/// </summary>
public partial class MonitorSelectionItem : ViewModelBase
{
    [ObservableProperty]
    private string _clientId = string.Empty;

    [ObservableProperty]
    private string _hostname = string.Empty;

    [ObservableProperty]
    private string _resolution = string.Empty;

    [ObservableProperty]
    private string _ipAddress = string.Empty;

    [ObservableProperty]
    private bool _isSelected = true;
}

public partial class CrossScreenConfigViewModel : ViewModelBase
{
    private IStorageProvider? _storageProvider;
    private Action? _closeAction;

    /// <summary>
    /// Pre-selected wallpaper from main gallery, used to auto-populate paths
    /// </summary>
    public WallpaperItemViewModel? PreSelectedWallpaper { get; set; }

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

    [ObservableProperty]
    private ObservableCollection<MonitorSelectionItem> _availableMonitors = new();

    [ObservableProperty]
    private bool _hasMultipleMonitors = false;

    [ObservableProperty]
    private int _animationDistributionModeIndex = 0; // 0 = Sequential, 1 = Simultaneous

    [ObservableProperty]
    private int _movementTypeIndex = 0; // Maps to MovementType enum

    [ObservableProperty]
    private float _movementAngle = 30f;

    [ObservableProperty]
    private float _waveAmplitude = 200f;

    [ObservableProperty]
    private float _waveFrequency = 0.5f;

    [ObservableProperty]
    private float _orbitRadius = 500f;

    public bool IsSolidColorMode => BackgroundModeIndex == 0;
    public bool IsImageMode => BackgroundModeIndex == 1 || BackgroundModeIndex == 2;

    public bool IsMovementActive => MovementTypeIndex > 0;
    public bool IsDirectionVisible => MovementTypeIndex == 1 || MovementTypeIndex == 2; // Linear or Bounce
    public bool IsSineWaveMode => MovementTypeIndex == 3;
    public bool IsCircularMode => MovementTypeIndex == 4;
    public bool IsRandomWalkMode => MovementTypeIndex == 5;

    public bool DialogResult { get; private set; }

    public CrossScreenConfigViewModel()
    {
    }

    partial void OnBackgroundModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSolidColorMode));
        OnPropertyChanged(nameof(IsImageMode));
    }

    partial void OnMovementTypeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsMovementActive));
        OnPropertyChanged(nameof(IsDirectionVisible));
        OnPropertyChanged(nameof(IsSineWaveMode));
        OnPropertyChanged(nameof(IsCircularMode));
        OnPropertyChanged(nameof(IsRandomWalkMode));
    }

    public void SetStorageProvider(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
    }

    public void SetCloseAction(Action closeAction)
    {
        _closeAction = closeAction;
    }

    /// <summary>
    /// Set the list of available monitors/clients for selection
    /// </summary>
    public void SetAvailableMonitors(IEnumerable<ClientNodeViewModel> clients)
    {
        AvailableMonitors.Clear();

        var monitors = clients
            .Where(c => c.IsConnected)
            .OrderBy(c => c.Order)
            .ToList();

        HasMultipleMonitors = monitors.Count > 1;

        // If any monitors are selected in topology, use that selection; otherwise default to all
        var anySelected = monitors.Any(c => c.IsSelected);

        foreach (var client in monitors)
        {
            var resolution = $"{client.MonitorWidth}x{client.MonitorHeight}";
            if (resolution == "0x0")
                resolution = "Unknown";

            var monitorItem = new MonitorSelectionItem
            {
                ClientId = client.ClientId,
                Hostname = client.Hostname,
                Resolution = resolution,
                IpAddress = client.IpAddress,
                IsSelected = anySelected ? client.IsSelected : true
            };

            AvailableMonitors.Add(monitorItem);
        }
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

        AnimationDistributionModeIndex = config.DistributionMode switch
        {
            WaBiBaBuSy.Models.Wallpaper.AnimationDistributionMode.Sequential => 0,
            WaBiBaBuSy.Models.Wallpaper.AnimationDistributionMode.Simultaneous => 1,
            _ => 0
        };

        // Restore movement configuration
        var movement = config.Movement;
        MovementTypeIndex = (int)movement.Type;
        AnimationSpeed = (int)movement.SpeedPixelsPerSecond;
        MovementAngle = movement.DirectionAngleDegrees;
        WaveAmplitude = movement.WaveAmplitudePixels;
        WaveFrequency = movement.WaveFrequencyHz;
        OrbitRadius = movement.OrbitRadiusPixels;

        // Restore monitor selection from config
        var selectedIds = new HashSet<string>(config.SelectedMonitorIds);
        foreach (var monitor in AvailableMonitors)
        {
            monitor.IsSelected = selectedIds.Contains(monitor.ClientId) || config.SelectedMonitorIds.Count == 0;
        }
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

        var distributionMode = AnimationDistributionModeIndex switch
        {
            0 => WaBiBaBuSy.Models.Wallpaper.AnimationDistributionMode.Sequential,
            1 => WaBiBaBuSy.Models.Wallpaper.AnimationDistributionMode.Simultaneous,
            _ => WaBiBaBuSy.Models.Wallpaper.AnimationDistributionMode.Sequential
        };

        // Collect selected monitor IDs
        var selectedMonitorIds = AvailableMonitors
            .Where(m => m.IsSelected)
            .Select(m => m.ClientId)
            .ToList();

        var movementType = (MovementType)MovementTypeIndex;

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
            AnimationSpeedPxPerSecond = AnimationSpeed,
            SelectedMonitorIds = selectedMonitorIds,
            DistributionMode = distributionMode,
            Movement = new MovementConfig
            {
                Type = movementType,
                SpeedPixelsPerSecond = AnimationSpeed,
                DirectionAngleDegrees = MovementAngle,
                WaveAmplitudePixels = WaveAmplitude,
                WaveFrequencyHz = WaveFrequency,
                OrbitRadiusPixels = OrbitRadius,
                Loop = AnimationLoop
            }
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

    /// <summary>
    /// Apply pre-selected wallpaper from main gallery to populate paths.
    /// Call after setting PreSelectedWallpaper and before showing the dialog.
    /// Only fills empty fields (won't overwrite existing config).
    /// </summary>
    public void ApplyPreSelectedWallpaper()
    {
        if (PreSelectedWallpaper == null) return;

        // Auto-populate animation path for Video/GIF types
        if ((PreSelectedWallpaper.Type == WallpaperType.Video || PreSelectedWallpaper.Type == WallpaperType.Gif)
            && string.IsNullOrEmpty(AnimationPath))
        {
            AnimationPath = PreSelectedWallpaper.FilePath;
        }

        // Auto-populate background image path for Image types
        if (PreSelectedWallpaper.Type == WallpaperType.Image && string.IsNullOrEmpty(BackgroundImagePath))
        {
            BackgroundImagePath = PreSelectedWallpaper.FilePath;
            if (BackgroundModeIndex == 0) // Switch from solid color to stretched image
                BackgroundModeIndex = 1;
        }
    }

    [RelayCommand]
    private async Task AutoDetectBackgroundColor()
    {
        // Detect from animation file if available
        var filePath = !string.IsNullOrEmpty(AnimationPath) ? AnimationPath : BackgroundImagePath;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        try
        {
            var color = await BackgroundColorDetector.DetectDominantEdgeColorAsync(filePath);
            BackgroundColor = color;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoDetect] Error: {ex.Message}");
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

        // Validate at least one monitor is selected
        if (!AvailableMonitors.Any(m => m.IsSelected))
        {
            // TODO: Show error message - "Please select at least one monitor for the animation"
            return;
        }

        DialogResult = true;
        _closeAction?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        _closeAction?.Invoke();
    }
}

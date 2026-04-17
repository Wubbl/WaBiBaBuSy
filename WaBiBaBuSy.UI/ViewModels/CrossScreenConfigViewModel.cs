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
using WaBiBaBuSy.Core.Services.Desktop;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Services;
// Note: DesktopIconService / ZonePlanner are intentionally NOT used here.
// Each Player.D2D node detects its own desktop icons at runtime.

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

public record MovementTypeOption(string Name, MovementType Type);

public partial class ZoneColorItem : ViewModelBase
{
    [ObservableProperty] private string _colorHex = "#1A3A5C";
    [ObservableProperty] private string _label    = "Zone";
}

public partial class CrossScreenConfigViewModel : ViewModelBase
{
    private IStorageProvider? _storageProvider;
    private Action? _closeAction;

    private static readonly MovementTypeOption[] AllMovementOptions =
    [
        new("Static (Centered)",   MovementType.Static),
        new("Linear (A to B)",     MovementType.Linear),
        new("Bounce (Off Edges)",  MovementType.Bounce),
        new("Sine Wave",           MovementType.SineWave),
        new("Circular (Orbit)",    MovementType.Circular),
        new("Random Walk",         MovementType.RandomWalk),
    ];

    /// <summary>
    /// Pre-selected wallpaper from main gallery, used to auto-populate paths
    /// </summary>
    public WallpaperItemViewModel? PreSelectedWallpaper { get; set; }

    [ObservableProperty]
    private int _backgroundModeIndex = 4; // Icon Zone

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
    private MovementTypeOption _selectedMovementType = AllMovementOptions[0];

    [ObservableProperty]
    private float _movementAngle = 30f;

    [ObservableProperty]
    private float _waveAmplitude = 200f;

    [ObservableProperty]
    private float _waveFrequency = 0.5f;

    [ObservableProperty]
    private float _orbitRadius = 500f;

    [ObservableProperty]
    private int _corridorTopPx = 324;

    [ObservableProperty]
    private int _corridorHeightPx = 432;

    [ObservableProperty]
    private string _topZoneColorHex = "#1A3A5C";

    [ObservableProperty]
    private string _bottomZoneColorHex = "#3C1A5C";

    [ObservableProperty]
    private string _corridorColorHex = "#1E1E1E";

    // ── IconZone mode ────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<ZoneColorItem> _iconZonePalette = new();
    [ObservableProperty] private string _iconCorridorColorHex = "#1E1E1E";
    [ObservableProperty] private bool _rotateWithPath = true;

    public bool IsSolidColorMode => BackgroundModeIndex == 0;
    public bool IsImageMode => BackgroundModeIndex == 1 || BackgroundModeIndex == 2;
    public bool IsThreeZoneMode => BackgroundModeIndex == 3;
    public bool IsIconZoneMode  => BackgroundModeIndex == 4;

    private readonly ObservableCollection<MovementTypeOption> _availableMovementOptions =
        new(AllMovementOptions);

    public ObservableCollection<MovementTypeOption> AvailableMovementOptions => _availableMovementOptions;

    public bool IsMovementActive => SelectedMovementType?.Type != MovementType.Static;
    public bool IsDirectionVisible => SelectedMovementType?.Type is MovementType.Linear or MovementType.Bounce;
    public bool IsSineWaveMode => SelectedMovementType?.Type == MovementType.SineWave;
    public bool IsCircularMode => SelectedMovementType?.Type == MovementType.Circular;
    public bool IsRandomWalkMode => SelectedMovementType?.Type == MovementType.RandomWalk;

    public bool DialogResult { get; private set; }

    public CrossScreenConfigViewModel()
    {
        // Seed a default 8-color palette so Icon Zone mode is ready out of the box
        var defaults = PaletteGenerator.GenerateHarmonious(8);
        for (int i = 0; i < defaults.Count; i++)
            _iconZonePalette.Add(new ZoneColorItem { ColorHex = defaults[i], Label = $"Zone {i + 1}" });
    }

    partial void OnBackgroundModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSolidColorMode));
        OnPropertyChanged(nameof(IsImageMode));
        OnPropertyChanged(nameof(IsThreeZoneMode));
        OnPropertyChanged(nameof(IsIconZoneMode));
        // If Circular was selected and user switches to ThreeZone/IconZone, fall back to Bounce first
        bool hideCircular = IsThreeZoneMode || IsIconZoneMode;
        if (hideCircular && SelectedMovementType?.Type == MovementType.Circular)
            SelectedMovementType = AllMovementOptions.First(o => o.Type == MovementType.Bounce);

        // Mutate the collection in-place so the ComboBox keeps its SelectedItem reference
        var circular = AllMovementOptions.First(o => o.Type == MovementType.Circular);
        if (hideCircular && _availableMovementOptions.Contains(circular))
            _availableMovementOptions.Remove(circular);
        else if (!hideCircular && !_availableMovementOptions.Contains(circular))
            _availableMovementOptions.Insert(4, circular);
    }

    partial void OnSelectedMovementTypeChanged(MovementTypeOption value)
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
            BackgroundMode.SolidColor    => 0,
            BackgroundMode.StretchedImage => 1,
            BackgroundMode.TiledImage    => 2,
            BackgroundMode.ThreeZone     => 3,
            BackgroundMode.IconZone      => 4,
            _                            => 0
        };

        BackgroundColor = config.Background.ColorHex;
        BackgroundImagePath = config.Background.ImagePath ?? string.Empty;

        CorridorTopPx      = config.Background.CorridorTopPx;
        CorridorHeightPx   = config.Background.CorridorHeightPx;
        TopZoneColorHex    = config.Background.TopZoneColorHex;
        BottomZoneColorHex = config.Background.BottomZoneColorHex;
        CorridorColorHex   = config.Background.CorridorColorHex;

        // IconZone
        IconCorridorColorHex = config.Background.IconCorridorColorHex;
        IconZonePalette.Clear();
        var paletteHexes = config.Background.IconZonePaletteHexes.Count > 0
            ? config.Background.IconZonePaletteHexes
            : PaletteGenerator.GenerateHarmonious(8);
        int zIdx = 0;
        foreach (var hex in paletteHexes)
            IconZonePalette.Add(new ZoneColorItem { ColorHex = hex, Label = $"Zone {++zIdx}" });
        RotateWithPath = config.Animation.RotateWithPath;
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
        SelectedMovementType = AllMovementOptions.FirstOrDefault(o => o.Type == movement.Type) ?? AllMovementOptions[0];
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
            3 => BackgroundMode.ThreeZone,
            4 => BackgroundMode.IconZone,
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

        var movementType = SelectedMovementType.Type;

        return new CrossScreenConfig
        {
            Background = new BackgroundLayerConfig
            {
                Mode = backgroundMode,
                ColorHex = BackgroundColor,
                ImagePath = string.IsNullOrWhiteSpace(BackgroundImagePath) ? null : BackgroundImagePath,
                CorridorTopPx      = CorridorTopPx,
                CorridorHeightPx   = CorridorHeightPx,
                TopZoneColorHex    = TopZoneColorHex,
                BottomZoneColorHex = BottomZoneColorHex,
                CorridorColorHex   = CorridorColorHex,
                IconCorridorColorHex  = IconCorridorColorHex,
                IconZonePaletteHexes = IconZonePalette.Select(z => z.ColorHex).ToList()
            },
            Animation = new AnimationLayerConfig
            {
                AnimationPath = AnimationPath,
                TargetHeight = AnimationHeight,
                Loop = AnimationLoop,
                VerticalAlign = verticalAlign,
                RotateWithPath = RotateWithPath
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
    private void MoveMonitorUp(MonitorSelectionItem? monitor)
    {
        if (monitor == null) return;
        var index = AvailableMonitors.IndexOf(monitor);
        if (index > 0)
        {
            AvailableMonitors.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveMonitorDown(MonitorSelectionItem? monitor)
    {
        if (monitor == null) return;
        var index = AvailableMonitors.IndexOf(monitor);
        if (index >= 0 && index < AvailableMonitors.Count - 1)
        {
            AvailableMonitors.Move(index, index + 1);
        }
    }

    [RelayCommand]
    private void RandomizePalette()
    {
        int count = Math.Max(IconZonePalette.Count, 4);
        var palette = PaletteGenerator.GenerateHarmonious(count);
        IconZonePalette.Clear();
        for (int i = 0; i < palette.Count; i++)
            IconZonePalette.Add(new ZoneColorItem { ColorHex = palette[i], Label = $"Zone {i + 1}" });
    }

    [RelayCommand]
    private void AddZoneColor()
    {
        int idx = IconZonePalette.Count + 1;
        var single = PaletteGenerator.GenerateHarmonious(idx);
        IconZonePalette.Add(new ZoneColorItem { ColorHex = single[idx - 1], Label = $"Zone {idx}" });
    }

    [RelayCommand]
    private void RemoveLastZoneColor()
    {
        if (IconZonePalette.Count > 1)
            IconZonePalette.RemoveAt(IconZonePalette.Count - 1);
    }

    [RelayCommand]
    private void Ok()
    {
        // Validate configuration
        if (IsImageMode && string.IsNullOrWhiteSpace(BackgroundImagePath))
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

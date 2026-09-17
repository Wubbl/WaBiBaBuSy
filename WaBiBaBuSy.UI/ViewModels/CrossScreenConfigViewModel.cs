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
using WaBiBaBuSy.UI.Views;
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
    private Avalonia.Controls.Window? _ownerWindow;
    private List<WallpaperItemViewModel> _galleryWallpapers = new();

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

    // ComboBox order: 0=Center, 1=Fit, 2=Fill, 3=Stretch (does NOT match enum order)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnimationHeightRelevant))]
    private int _fitModeIndex = 0; // 0 = Center

    /// <summary>
    /// True when AnimationHeight actually affects rendering. In Center mode the native
    /// resolution wins and the height field is ignored, so the UI hides it.
    /// </summary>
    public bool IsAnimationHeightRelevant => FitModeIndex != 0; // 0 = Center; enum order differs from ComboBox order

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
    private MovementTypeOption _selectedMovementType =
        AllMovementOptions.First(o => o.Type == MovementType.Linear);

    [ObservableProperty]
    private float _movementAngle = 30f;

    [ObservableProperty]
    private float _waveAmplitude = 200f;

    [ObservableProperty]
    private float _waveFrequency = 0.5f;

    [ObservableProperty]
    private float _orbitRadius = 500f;

    [ObservableProperty]
    private int _randomWalkIterationSteps = 20;

    // When true, Linear movement is reversed so cells travel from first node to last node.
    [ObservableProperty] private bool _isMovementReversed = false;

    // When true, the pattern scrolls endlessly with no loop reset (requires Linear + Traveling mode).
    [ObservableProperty] private bool _isMovementEndless = false;

    // Tier 0.6: previously hardcoded in BuildConfig (42 / 1000) — now user-editable.
    [ObservableProperty] private int _randomSeed = 42;
    [ObservableProperty] private float _randomStepIntervalMs = 1000f;
    [ObservableProperty] private double _speedMultiplier = 2.0;

    // Tier 0.5: mirror the sprite horizontally whenever it travels leftwards on screen.
    [ObservableProperty] private bool _faceTravelDirection = false;

    // Tier 0.7: Wave mode — per-node clock shift in Simultaneous distribution (ms per node).
    [ObservableProperty] private int _nodePhaseDelayMs = 0;

    public bool IsSimultaneousMode => AnimationDistributionModeIndex == 1;

    partial void OnAnimationDistributionModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSimultaneousMode));
    }

    [RelayCommand]
    private void RandomizeMovementSeed()
    {
        // UI-only randomness: the chosen seed is broadcast as config, so every node still
        // computes the same deterministic path from it.
        RandomSeed = Random.Shared.Next(1, 999_999);
    }

    public bool IsLinearMode => SelectedMovementType?.Type == MovementType.Linear;
    // Reversed is meaningful for Linear (swaps start/end), SineWave (flips horizontal travel), and Circular (CW↔CCW).
    public bool IsReversibleMode => SelectedMovementType?.Type is MovementType.Linear
                                                                or MovementType.SineWave
                                                                or MovementType.Circular;

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
    [ObservableProperty] private bool _iconZonePaletteEnabled = false;

    // ── Color grading (F2) ───────────────────────────────────────────────────
    // Index maps to ColorGradingMode: 0=None,1=Rainbow,2=RandomColors,3=Gradient,4=CycleColorList,
    //   5=TravelingRainbow, 6=TravelingList, 7=TravelingRandom
    [ObservableProperty] private int _colorGradingModeIndex = 0;
    [ObservableProperty] private double _colorGradingCyclesPerSecond = 0.1;
    [ObservableProperty] private string _colorGradingGradientA = "#FF0000";
    [ObservableProperty] private string _colorGradingGradientB = "#0000FF";
    [ObservableProperty] private int _colorGradingSeed = 1;
    [ObservableProperty] private string _colorGradingColorListCsv = "#FF0000,#FFFF00,#00FF00,#00FFFF,#0000FF,#FF00FF";

    // Fraction of cells to color in Traveling modes (0.0–1.0, default 1.0 = all).
    [ObservableProperty] private float _colorGradingColoredCellPercentage = 1.0f;

    public bool IsColorGradingNone           => ColorGradingModeIndex == 0;
    public bool IsColorGradingGradient       => ColorGradingModeIndex == 3;
    public bool IsColorGradingColorListMode  => ColorGradingModeIndex == 2 || ColorGradingModeIndex == 4
                                             || ColorGradingModeIndex == 6 || ColorGradingModeIndex == 7;
    // Traveling modes (5-7) are time-independent — hide the CyclesPerSecond control for them.
    public bool IsColorGradingTimeBased      => ColorGradingModeIndex >= 1 && ColorGradingModeIndex <= 4;
    // Density slider is only meaningful for traveling modes.
    public bool IsColorGradingTraveling      => ColorGradingModeIndex >= 5;

    partial void OnColorGradingModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsColorGradingNone));
        OnPropertyChanged(nameof(IsColorGradingGradient));
        OnPropertyChanged(nameof(IsColorGradingColorListMode));
        OnPropertyChanged(nameof(IsColorGradingTimeBased));
        OnPropertyChanged(nameof(IsColorGradingTraveling));
    }

    // ── Pattern multiplier (F3) ──────────────────────────────────────────────
    [ObservableProperty] private bool _patternEnabled = false;
    // 0 = Fill (auto-derive counts), 1 = Explicit
    [ObservableProperty] private int _patternSizingIndex = 0;
    [ObservableProperty] private int _patternCountX = 5;
    [ObservableProperty] private int _patternCountY = 3;
    [ObservableProperty] private float _patternSpacingX = 20f;
    [ObservableProperty] private float _patternSpacingY = 20f;
    [ObservableProperty] private float _patternMargin = 0f;
    [ObservableProperty] private float _patternRandomOffset = 0f;
    [ObservableProperty] private float _patternRandomRotation = 0f;
    [ObservableProperty] private int _patternSeed = 1;
    [ObservableProperty] private int _iconFadePaddingPx = 50;
    [ObservableProperty] private int _iconZoneExpansionPx = 10;

    public bool IsPatternFill => PatternSizingIndex == 0;
    public bool IsPatternExplicit => PatternSizingIndex == 1;
    public bool IsPatternWithIconZone => PatternEnabled && IsIconZoneMode;

    partial void OnPatternSizingIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPatternFill));
        OnPropertyChanged(nameof(IsPatternExplicit));
    }

    partial void OnPatternEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPatternWithIconZone));
    }

    // ── Multi-image (F4) ─────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<string> _additionalAnimationPaths = new();
    [ObservableProperty] private float _multiImageSpread = 0f;
    [ObservableProperty] private float _multiImagePhaseJitterMs = 0f;

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
        OnPropertyChanged(nameof(IsPatternWithIconZone));
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
        OnPropertyChanged(nameof(IsLinearMode));
        OnPropertyChanged(nameof(IsReversibleMode));
    }

    public void SetStorageProvider(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
    }

    public void SetOwnerWindow(Avalonia.Controls.Window? window) => _ownerWindow = window;

    public void SetGalleryWallpapers(IEnumerable<WallpaperItemViewModel> wallpapers)
        => _galleryWallpapers = wallpapers.ToList();

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
        FitModeIndex = config.Animation.FitMode switch
        {
            ContentFitMode.Center  => 0,
            ContentFitMode.Fit     => 1,
            ContentFitMode.Fill    => 2,
            ContentFitMode.Stretch => 3,
            _                      => 0
        };
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
        RandomWalkIterationSteps = movement.IterationStepCount;
        IsMovementReversed = movement.Reversed;
        IsMovementEndless = movement.Endless;
        RandomSeed = movement.RandomSeed;
        RandomStepIntervalMs = movement.RandomStepIntervalMs;
        NodePhaseDelayMs = movement.NodePhaseDelayMs;
        SpeedMultiplier = config.Animation.SpeedMultiplier;
        FaceTravelDirection = config.Animation.FaceTravelDirection;

        // Restore monitor selection from config
        var selectedIds = new HashSet<string>(config.SelectedMonitorIds);
        foreach (var monitor in AvailableMonitors)
        {
            monitor.IsSelected = selectedIds.Contains(monitor.ClientId) || config.SelectedMonitorIds.Count == 0;
        }

        // Color grading
        var grading = config.Animation.ColorGrading ?? new ColorGradingConfig();
        ColorGradingModeIndex = (int)grading.Mode;
        ColorGradingCyclesPerSecond = grading.CyclesPerSecond;
        ColorGradingGradientA = grading.GradientA;
        ColorGradingGradientB = grading.GradientB;
        ColorGradingSeed = grading.Seed;
        ColorGradingColorListCsv = grading.ColorList.Count > 0
            ? string.Join(",", grading.ColorList)
            : "#FF0000,#FFFF00,#00FF00,#00FFFF,#0000FF,#FF00FF";
        ColorGradingColoredCellPercentage = grading.ColoredCellPercentage;

        // Pattern
        var pattern = config.Animation.Pattern;
        PatternEnabled = pattern != null;
        if (pattern != null)
        {
            PatternSizingIndex = (int)pattern.Sizing;
            PatternCountX = Math.Max(1, pattern.CountX);
            PatternCountY = Math.Max(1, pattern.CountY);
            PatternSpacingX = pattern.SpacingX;
            PatternSpacingY = pattern.SpacingY;
            PatternMargin = pattern.Margin;
            PatternRandomOffset = pattern.RandomOffsetMaxPx;
            PatternRandomRotation = pattern.RandomRotationMaxDeg;
            PatternSeed = pattern.Seed;
        }

        // Multi-image
        AdditionalAnimationPaths.Clear();
        foreach (var path in config.Animation.AdditionalAnimationPaths)
            AdditionalAnimationPaths.Add(path);
        MultiImageSpread = config.Animation.MultiImageSpread;
        MultiImagePhaseJitterMs = config.Animation.MultiImagePhaseJitterMs;

        // IconZone palette toggle
        IconZonePaletteEnabled = config.Background.IconZonePaletteEnabled;
        IconFadePaddingPx = config.Background.IconFadePaddingPx;
        IconZoneExpansionPx = config.Background.IconZoneExpansionPx;
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
                IconZonePaletteHexes = IconZonePalette.Select(z => z.ColorHex).ToList(),
                IconZonePaletteEnabled = IconZonePaletteEnabled,
                IconFadePaddingPx = IconFadePaddingPx,
                IconZoneExpansionPx = IconZoneExpansionPx
            },
            Animation = new AnimationLayerConfig
            {
                AnimationPath = AnimationPath,
                AdditionalAnimationPaths = AdditionalAnimationPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
                TargetHeight = AnimationHeight,
                FitMode = FitModeIndex switch
                {
                    0 => ContentFitMode.Center,
                    1 => ContentFitMode.Fit,
                    2 => ContentFitMode.Fill,
                    3 => ContentFitMode.Stretch,
                    _ => ContentFitMode.Center
                },
                Loop = AnimationLoop,
                VerticalAlign = verticalAlign,
                RotateWithPath = RotateWithPath,
                SpeedMultiplier = SpeedMultiplier > 0 ? SpeedMultiplier : 1.0,
                FaceTravelDirection = FaceTravelDirection,
                ColorGrading = new ColorGradingConfig
                {
                    Mode = (ColorGradingMode)ColorGradingModeIndex,
                    CyclesPerSecond = ColorGradingCyclesPerSecond,
                    GradientA = ColorGradingGradientA,
                    GradientB = ColorGradingGradientB,
                    Seed = ColorGradingSeed,
                    ColorList = ColorGradingColorListCsv
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList(),
                    ColoredCellPercentage = ColorGradingColoredCellPercentage
                },
                Pattern = PatternEnabled ? new PatternConfig
                {
                    Sizing = (PatternConfig.SizingMode)PatternSizingIndex,
                    CountX = PatternCountX,
                    CountY = PatternCountY,
                    SpacingX = PatternSpacingX,
                    SpacingY = PatternSpacingY,
                    Margin = PatternMargin,
                    RandomOffsetMaxPx = PatternRandomOffset,
                    RandomRotationMaxDeg = PatternRandomRotation,
                    Seed = PatternSeed
                } : null,
                MultiImageSpread = MultiImageSpread,
                MultiImagePhaseJitterMs = MultiImagePhaseJitterMs
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
                Loop = AnimationLoop,
                RandomSeed = RandomSeed,
                RandomStepIntervalMs = Math.Max(100f, RandomStepIntervalMs),
                IterationStepCount = RandomWalkIterationSteps,
                Reversed = IsMovementReversed,
                Endless = IsMovementEndless,
                NodePhaseDelayMs = Math.Max(0, NodePhaseDelayMs)
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
    private async Task SelectAnimationFromGallery()
    {
        if (_ownerWindow == null || _galleryWallpapers.Count == 0) return;

        var dialogVm = new WallpaperMultiSelectDialogViewModel();
        dialogVm.LoadWallpapers(_galleryWallpapers);

        var dialog = new WallpaperMultiSelectDialog { DataContext = dialogVm };
        dialogVm.SetCloseAction(() => dialog.Close());
        await dialog.ShowDialog(_ownerWindow);

        if (dialogVm.DialogResult)
        {
            var selected = dialogVm.GetSelectedWallpapers();
            if (selected.Count > 0)
                AnimationPath = selected[0].FilePath;
        }
    }

    /// <summary>F4: add images from gallery to the multi-image source list.</summary>
    [RelayCommand]
    private async Task AddAdditionalImagesFromGallery()
    {
        if (_ownerWindow == null || _galleryWallpapers.Count == 0) return;

        var dialogVm = new WallpaperMultiSelectDialogViewModel();
        dialogVm.LoadWallpapers(_galleryWallpapers);

        var dialog = new WallpaperMultiSelectDialog { DataContext = dialogVm };
        dialogVm.SetCloseAction(() => dialog.Close());
        await dialog.ShowDialog(_ownerWindow);

        if (dialogVm.DialogResult)
        {
            foreach (var item in dialogVm.GetSelectedWallpapers())
                AdditionalAnimationPaths.Add(item.FilePath);
        }
    }

    /// <summary>F4: remove an additional image from the multi-image source list.</summary>
    [RelayCommand]
    private void RemoveAdditionalImage(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        AdditionalAnimationPaths.Remove(path);
    }

    /// <summary>
    /// Apply pre-selected wallpaper from main gallery to populate paths.
    /// Call after setting PreSelectedWallpaper and before showing the dialog.
    /// Only fills empty fields (won't overwrite existing config).
    /// </summary>
    public void ApplyPreSelectedWallpaper()
    {
        if (PreSelectedWallpaper == null) return;

        // Auto-populate animation path for all media types (image/gif/video)
        if (string.IsNullOrEmpty(AnimationPath))
        {
            AnimationPath = PreSelectedWallpaper.FilePath;
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

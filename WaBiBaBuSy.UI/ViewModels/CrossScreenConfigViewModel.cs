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

using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.UI.ViewModels;

public record MovementTypeOption(string Name, MovementType Type);

public partial class ZoneColorItem : ViewModelBase
{
    [ObservableProperty] private string _colorHex = "#1A3A5C";
    [ObservableProperty] private string _label    = "Zone";
}

/// <summary>
/// The Scene editor's draft: every field of one <see cref="CrossScreenConfig"/>, loaded with
/// <see cref="LoadFromConfig"/> and built with <see cref="BuildConfig"/>. Hosted by the main window's
/// right panel (<c>SceneEditorPanel</c>); targets and preview come from the host.
/// </summary>
public partial class CrossScreenConfigViewModel : ViewModelBase
{
    private IStorageProvider? _storageProvider;

    private static readonly MovementTypeOption[] AllMovementOptions =
    [
        new("Static (Centered)",   MovementType.Static),
        new("Linear (A to B)",     MovementType.Linear),
        new("Bounce (Off Edges)",  MovementType.Bounce),
        new("Sine Wave",           MovementType.SineWave),
        new("Circular (Orbit)",    MovementType.Circular),
        new("Random Walk",         MovementType.RandomWalk),
    ];

    [ObservableProperty]
    private int _backgroundModeIndex = 4; // Icon Zone

    [ObservableProperty]
    private string _backgroundColor = "#000000";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImageFileName))]
    private string _backgroundImagePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimationFileName))]
    private string _animationPath = string.Empty;

    /// <summary>Caption of the primary file in the Content tab.</summary>
    public string AnimationFileName => string.IsNullOrWhiteSpace(AnimationPath)
        ? "Pick a file in the strip above" : Path.GetFileName(AnimationPath);

    /// <summary>Caption of the background image in the Background tab.</summary>
    public string BackgroundImageFileName => string.IsNullOrWhiteSpace(BackgroundImagePath)
        ? "No image yet" : Path.GetFileName(BackgroundImagePath);

    [ObservableProperty]
    private int _animationHeight = 720;

    // ComboBox order: 0=Center, 1=Fit, 2=Fill, 3=Stretch, 4=Target height (does NOT match enum order)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnimationHeightRelevant))]
    private int _fitModeIndex = 0; // 0 = Center

    /// <summary>
    /// True when the height field actually affects the main sprite: only the "Target height" fit
    /// mode uses it (Center = native size, Fit/Fill/Stretch derive from the screen).
    /// </summary>
    public bool IsAnimationHeightRelevant => FitModeIndex == 4;

    /// <summary>
    /// Units follow the room (set by the host from Room ⚙ "Physical units"): cm and cm/s on a physical
    /// canvas, px and px/s otherwise. Both values are stored in the config; only the matching one is shown.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSizeInCm), nameof(IsSpeedInCm))]
    private bool _usePhysicalUnits;

    [ObservableProperty] private float _animationHeightCm = 15f;
    /// <summary>True when the height field shows cm (physical room).</summary>
    public bool IsSizeInCm => UsePhysicalUnits;

    [ObservableProperty] private float _animationSpeedCm = 20f;
    /// <summary>True when the speed slider shows cm/s (physical room).</summary>
    public bool IsSpeedInCm => UsePhysicalUnits;

    [ObservableProperty]
    private int _verticalAlignmentIndex = 1; // Center

    [ObservableProperty]
    private bool _animationLoop = true;

    [ObservableProperty]
    private int _animationSpeed = 500;

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

    /// <summary>Sequential radio button: true = one canvas spans all targets. Setting false is ignored (the other button sets its mode).</summary>
    public bool IsSequentialMode
    {
        get => AnimationDistributionModeIndex == 0;
        set { if (value) AnimationDistributionModeIndex = 0; }
    }

    /// <summary>Simultaneous radio button: true = every monitor plays its own copy. Setting false is ignored.</summary>
    public bool IsSimultaneousMode
    {
        get => AnimationDistributionModeIndex == 1;
        set { if (value) AnimationDistributionModeIndex = 1; }
    }

    partial void OnAnimationDistributionModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSequentialMode));
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

    /// <summary>Count X/Y fields: pattern on and sized by explicit count.</summary>
    public bool IsPatternCountVisible => PatternEnabled && PatternSizingIndex == 1;

    partial void OnPatternSizingIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPatternFill));
        OnPropertyChanged(nameof(IsPatternExplicit));
        OnPropertyChanged(nameof(IsPatternCountVisible));
    }

    partial void OnPatternEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPatternWithIconZone));
        OnPropertyChanged(nameof(IsPatternCountVisible));
    }

    // ── Multi-image (F4) ─────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<string> _additionalAnimationPaths = new();
    [ObservableProperty] private float _multiImageSpread = 0f;
    [ObservableProperty] private float _multiImagePhaseJitterMs = 0f;

    /// <summary>True when extra source images are listed (shows the chip row).</summary>
    public bool HasExtraImages => AdditionalAnimationPaths.Count > 0;

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

    // ── Docked editor (UI redesign Plan 2) ───────────────────────────────────

    /// <summary>One-line warnings shown as amber chips above the tabs (computed by the host from the draft + room).</summary>
    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>Crossing-time readout under the speed slider, e.g. "crosses the room in 12.5 s".</summary>
    [ObservableProperty] private string _speedReadout = string.Empty;

    /// <summary>Replace the warning chips and the speed readout. Unchanged warnings do not touch the collection.</summary>
    public void SetChecks(IReadOnlyList<string> warnings, string speedReadout)
    {
        if (!Warnings.SequenceEqual(warnings))
        {
            Warnings.Clear();
            foreach (var w in warnings) Warnings.Add(w);
        }
        SpeedReadout = speedReadout;
    }

    /// <summary>True while the next gallery-strip click sets the background image instead of the animation file.</summary>
    [ObservableProperty] private bool _isPickingBackgroundImage;

    /// <summary>
    /// Apply a gallery-strip click: while picking a background it becomes the background image;
    /// otherwise it becomes the primary file, or (additive, Ctrl+click) an extra source image.
    /// </summary>
    public void PickFromStrip(string path, bool additive)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (IsPickingBackgroundImage)
        {
            BackgroundImagePath = path;
            IsPickingBackgroundImage = false;
            return;
        }
        if (additive)
        {
            if (!string.Equals(path, AnimationPath, StringComparison.OrdinalIgnoreCase) && !AdditionalAnimationPaths.Contains(path))
                AdditionalAnimationPaths.Add(path);
            return;
        }
        AnimationPath = path;
        AdditionalAnimationPaths.Remove(path);
    }

    public CrossScreenConfigViewModel()
    {
        // Seed a default 8-color palette so Icon Zone mode is ready out of the box
        var defaults = PaletteGenerator.GenerateHarmonious(8);
        for (int i = 0; i < defaults.Count; i++)
            _iconZonePalette.Add(new ZoneColorItem { ColorHex = defaults[i], Label = $"Zone {i + 1}" });
        _additionalAnimationPaths.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasExtraImages));
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
            ContentFitMode.Center       => 0,
            ContentFitMode.Fit          => 1,
            ContentFitMode.Fill         => 2,
            ContentFitMode.Stretch      => 3,
            ContentFitMode.TargetHeight => 4,
            _                           => 0
        };
        // The unit flags are not loaded: the room decides which of the two stored values is shown.
        AnimationHeightCm = config.Animation.TargetHeightCm;
        AnimationSpeedCm = config.Movement.SpeedCmPerSecond;
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

        var movementType = SelectedMovementType.Type;

        var config = new CrossScreenConfig
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
                    4 => ContentFitMode.TargetHeight,
                    _ => ContentFitMode.Center
                },
                TargetHeightCm = Math.Max(0.5f, AnimationHeightCm),
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
            SelectedMonitorIds = new List<string>(),   // targets are set by the caller (room selection / playlist item)
            DistributionMode = distributionMode,
            Movement = new MovementConfig
            {
                Type = movementType,
                SpeedPixelsPerSecond = AnimationSpeed,
                SpeedCmPerSecond = Math.Max(0.1f, AnimationSpeedCm),
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
        PhysicalUnits.ApplyRoomUnits(config, UsePhysicalUnits);
        return config;
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

    /// <summary>F4: remove an additional image from the multi-image source list.</summary>
    [RelayCommand]
    private void RemoveAdditionalImage(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        AdditionalAnimationPaths.Remove(path);
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

}

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

/// <summary>One chip of the Scene editor's Layers strip.</summary>
public partial class LayerRow : ViewModelBase
{
    public LayerRow(SceneLayer layer)
    {
        Layer = layer;
        _name = layer.Name;
        _enabled = layer.Enabled;
    }

    /// <summary>The layer's config; replaced with the editor fields whenever this row was the selected one.</summary>
    public SceneLayer Layer { get; set; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Rename box of the chip's context flyout is open.</summary>
    [ObservableProperty] private bool _isRenaming;
}

/// <summary>
/// The Scene editor's draft: the background plus every layer of one <see cref="CrossScreenConfig"/>, loaded with
/// <see cref="LoadFromConfig"/> and built with <see cref="BuildConfig"/>. The Content / Motion / Look / Effects
/// fields edit the selected layer (<see cref="SelectedLayerIndex"/>); the other layers wait in <see cref="Layers"/>.
/// Hosted by the main window's right panel (<c>SceneEditorPanel</c>); targets and preview come from the host.
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
    [NotifyPropertyChangedFor(nameof(HasAnimationFile))]
    private string _animationPath = string.Empty;

    /// <summary>True when an animation file is picked (enables ✨ Suggest).</summary>
    public bool HasAnimationFile => !string.IsNullOrWhiteSpace(AnimationPath);

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

    // ── Effects (roadmap 2.4) ──
    [ObservableProperty] private bool _trailEnabled;
    [ObservableProperty] private int _trailCount = 6;
    [ObservableProperty] private int _trailSpacingMs = 60;
    [ObservableProperty] private bool _trailUseGradingColor = true;
    [ObservableProperty] private bool _pulseEnabled;
    [ObservableProperty] private int _pulseAmplitudePercent = 8;
    [ObservableProperty] private float _pulseFrequencyHz = 0.8f;
    [ObservableProperty] private float _spinDegPerSecond;
    [ObservableProperty] private int _bezelEntryIndex;      // BezelEffect: 0 None, 1 Glow, 2 Flash, 3 Ripple, 4 Portal
    [ObservableProperty] private int _bezelExitIndex;
    [ObservableProperty] private string _bezelColorHex = "#FFFFFF";
    [ObservableProperty] private int _bezelDurationMs = 350;

    // Tier 0.7: Wave mode — per-node clock shift in Simultaneous distribution (ms per node).
    [ObservableProperty] private int _nodePhaseDelayMs = 0;

    // ── Layers (roadmap 2.3) ──

    /// <summary>The scene's layers, bottom first (the Layers strip, left to right).</summary>
    public ObservableCollection<LayerRow> Layers { get; } = new();

    /// <summary>Layer the Content / Motion / Look / Effects tabs edit.</summary>
    [ObservableProperty] private int _selectedLayerIndex;

    /// <summary>Selected layer's opacity, 0–100 % (Look tab).</summary>
    [ObservableProperty] private int _layerOpacityPercent = 100;

    /// <summary>Selected layer's clock offset in ms: it runs this far ahead of the scene (Motion tab).</summary>
    [ObservableProperty] private int _layerPhaseOffsetMs;

    /// <summary>Selected layer's spawn window: appears every N s (0 = always shown) for M s (Motion tab).</summary>
    [ObservableProperty] private double _layerShowEverySeconds;
    [ObservableProperty] private double _layerShowForSeconds = 10;

    /// <summary>More than one layer: the strip shows remove / reorder.</summary>
    public bool HasSeveralLayers => Layers.Count > 1;

    private bool _switchingLayer;

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

    // The loaded scene's effects: fields the Effects tab does not show (trail opacity / falloff, an exact pulse
    // amplitude) are carried through from here, so editing a scene never resets them.
    private SpriteEffectsForm _loadedEffects = SpriteEffectsForm.From(null);

    // Fields of a loaded scene the editor has no control for (explicit start / end / orbit points, centered start):
    // carried through BuildConfig so "Update loaded item" / save as preset keeps them (review 2026-10-13 E9).
    private MovementConfig? _loadedMovement;
    private bool _loadedCenterInitialPosition;

    /// <summary>The Effects tab as a config; null when nothing is enabled (keeps saved scenes unchanged).</summary>
    private SpriteEffectsConfig? BuildEffects()
    {
        bool pulseUnchanged = PulseAmplitudePercent == (int)MathF.Round(_loadedEffects.PulseAmplitude * 100f);
        return (_loadedEffects with
        {
            TrailEnabled = TrailEnabled, TrailCount = TrailCount, TrailSpacingMs = TrailSpacingMs, TrailUseGradingColor = TrailUseGradingColor,
            PulseEnabled = PulseEnabled,
            PulseAmplitude = pulseUnchanged ? _loadedEffects.PulseAmplitude : PulseAmplitudePercent / 100f,
            PulseFrequencyHz = PulseFrequencyHz,
            SpinDegPerSecond = SpinDegPerSecond,
            Entry = (BezelEffect)BezelEntryIndex,
            Exit = (BezelEffect)BezelExitIndex,
            BezelColorHex = BezelColorHex,
            BezelDurationMs = BezelDurationMs,
        }).ToConfig();
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

    partial void OnSelectedLayerIndexChanging(int oldValue, int newValue)
    {
        // Park the fields of the layer we leave before the new one is loaded.
        if (!_switchingLayer && oldValue >= 0 && oldValue < Layers.Count) Layers[oldValue].Layer = BuildLayer();
    }

    partial void OnSelectedLayerIndexChanged(int value)
    {
        if (_switchingLayer || value < 0 || value >= Layers.Count) return;
        _switchingLayer = true;
        try { LoadLayer(Layers[value].Layer); }
        finally { _switchingLayer = false; }
        MarkSelectedRow();
    }

    private void MarkSelectedRow()
    {
        for (int i = 0; i < Layers.Count; i++) Layers[i].IsSelected = i == SelectedLayerIndex;
    }

    /// <summary>Select a layer chip.</summary>
    [RelayCommand]
    private void SelectLayer(LayerRow? row)
    {
        int i = row == null ? -1 : Layers.IndexOf(row);
        if (i >= 0) SelectedLayerIndex = i;
    }

    /// <summary>Add an empty layer on top and select it.</summary>
    [RelayCommand]
    private void AddLayer() => InsertLayer(Layers.Count, new SceneLayer { Name = $"Layer {Layers.Count + 1}" });

    /// <summary>Copy the selected layer (current edits included) above itself and select the copy.</summary>
    [RelayCommand]
    private void DuplicateLayer()
    {
        var copy = BuildLayer();
        copy.Name = Layers[SelectedLayerIndex].Name + " copy";
        InsertLayer(SelectedLayerIndex + 1, copy);
    }

    /// <summary>
    /// Put copies of <paramref name="layers"/> on top of the stack (a preset's layers, a Suggest card's sprite) and select
    /// the topmost. Returns how many were added: never past <see cref="SceneChecks.MaxLayers"/>.
    /// </summary>
    public int AppendLayers(IEnumerable<SceneLayer> layers)
    {
        int added = 0;
        foreach (var layer in layers)
        {
            if (Layers.Count >= SceneChecks.MaxLayers) break;
            InsertLayer(Layers.Count, Clone(layer));
            added++;
        }
        return added;
    }

    /// <summary>The selected layer with its current edits, and its name (Save layer as preset).</summary>
    public (SceneLayer Layer, string Name) SelectedLayerSnapshot() =>
        (Clone(BuildLayer()), SelectedLayerIndex >= 0 && SelectedLayerIndex < Layers.Count ? Layers[SelectedLayerIndex].Name : "Layer");

    /// <summary>Remove the selected layer (never the last one).</summary>
    [RelayCommand]
    private void RemoveLayer()
    {
        if (Layers.Count <= 1) return;
        int i = SelectedLayerIndex;
        _switchingLayer = true;
        try
        {
            Layers.RemoveAt(i);
            SelectedLayerIndex = Math.Min(i, Layers.Count - 1);
            LoadLayer(Layers[SelectedLayerIndex].Layer);
        }
        finally { _switchingLayer = false; }
        LayersChanged();
    }

    /// <summary>◀: draw the selected layer one step lower.</summary>
    [RelayCommand]
    private void MoveLayerDown() => MoveSelectedLayer(-1);

    /// <summary>▶: draw the selected layer one step higher.</summary>
    [RelayCommand]
    private void MoveLayerUp() => MoveSelectedLayer(+1);

    private void MoveSelectedLayer(int delta)
    {
        int from = SelectedLayerIndex, to = from + delta;
        if (to < 0 || to >= Layers.Count) return;
        Layers[from].Layer = BuildLayer();
        _switchingLayer = true;
        try
        {
            Layers.Move(from, to);
            SelectedLayerIndex = to;
        }
        finally { _switchingLayer = false; }
        LayersChanged();
    }

    private void InsertLayer(int index, SceneLayer layer)
    {
        if (SelectedLayerIndex >= 0 && SelectedLayerIndex < Layers.Count) Layers[SelectedLayerIndex].Layer = BuildLayer();
        Layers.Insert(index, NewRow(layer));
        _switchingLayer = true;
        try
        {
            SelectedLayerIndex = index;
            LoadLayer(layer);
        }
        finally { _switchingLayer = false; }
        LayersChanged();
    }

    private LayerRow NewRow(SceneLayer layer)
    {
        var row = new LayerRow(layer);
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LayerRow.Name) or nameof(LayerRow.Enabled)) OnPropertyChanged(nameof(Layers));
        };
        return row;
    }

    /// <summary>Selection marks, the strip's button states and one change notification for the host's draft update.</summary>
    private void LayersChanged()
    {
        MarkSelectedRow();
        OnPropertyChanged(nameof(HasSeveralLayers));
        OnPropertyChanged(nameof(Layers));
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
    public bool IsImageMode => BackgroundModeIndex is 1 or 2 or 5;
    public bool IsPanoramaMode => BackgroundModeIndex == 5;
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

    /// <summary>Panorama background: the image size that covers the target room's canvas pixel for pixel (empty without a room).</summary>
    [ObservableProperty] private string _panoramaCanvasSize = string.Empty;

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
        // A fresh "Layer N" takes the file's name, so the strip reads "Fish", "Bubbles", …
        if (SelectedLayerIndex >= 0 && SelectedLayerIndex < Layers.Count
            && System.Text.RegularExpressions.Regex.IsMatch(Layers[SelectedLayerIndex].Name, @"^Layer \d+$"))
            Layers[SelectedLayerIndex].Name = Path.GetFileNameWithoutExtension(path);
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
        OnPropertyChanged(nameof(IsPanoramaMode));
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
            BackgroundMode.Panorama      => 5,
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

        // IconZone palette toggle
        IconZonePaletteEnabled = config.Background.IconZonePaletteEnabled;
        IconFadePaddingPx = config.Background.IconFadePaddingPx;
        IconZoneExpansionPx = config.Background.IconZoneExpansionPx;

        // Layers: a background-only scene still gets one (empty) layer to edit.
        var layers = config.Layers.Count > 0 ? config.Layers : new List<SceneLayer> { new() { Name = "Layer 1" } };
        _switchingLayer = true;
        try
        {
            Layers.Clear();
            foreach (var layer in layers) Layers.Add(NewRow(Clone(layer)));
            SelectedLayerIndex = 0;
            LoadLayer(Layers[0].Layer);
        }
        finally { _switchingLayer = false; }
        LayersChanged();
    }

    private static SceneLayer Clone(SceneLayer layer) =>
        System.Text.Json.JsonSerializer.Deserialize<SceneLayer>(System.Text.Json.JsonSerializer.Serialize(layer))!;

    /// <summary>Show one layer in the Content / Motion / Look / Effects fields.</summary>
    private void LoadLayer(SceneLayer layer)
    {
        var anim = layer.Animation;
        LayerOpacityPercent = (int)MathF.Round(Math.Clamp(layer.Opacity, 0f, 1f) * 100f);
        LayerPhaseOffsetMs = (int)Math.Clamp(layer.PhaseOffsetMs, int.MinValue, int.MaxValue);
        LayerShowEverySeconds = layer.ShowEveryMs / 1000.0;
        LayerShowForSeconds = layer.ShowForMs / 1000.0;
        RotateWithPath = anim.RotateWithPath;
        AnimationPath = anim.AnimationPath;
        AnimationHeight = anim.TargetHeight;
        FitModeIndex = anim.FitMode switch
        {
            ContentFitMode.Center       => 0,
            ContentFitMode.Fit          => 1,
            ContentFitMode.Fill         => 2,
            ContentFitMode.Stretch      => 3,
            ContentFitMode.TargetHeight => 4,
            _                           => 0
        };
        // The unit flags are not loaded: the room decides which of the two stored values is shown.
        AnimationHeightCm = anim.TargetHeightCm;
        AnimationSpeedCm = layer.Movement.SpeedCmPerSecond;
        AnimationLoop = anim.Loop;

        VerticalAlignmentIndex = anim.VerticalAlign switch
        {
            VerticalAlignment.Top => 0,
            VerticalAlignment.Center => 1,
            VerticalAlignment.Bottom => 2,
            _ => 1
        };

        AnimationDistributionModeIndex = layer.Distribution switch
        {
            WaBiBaBuSy.Models.Wallpaper.AnimationDistributionMode.Sequential => 0,
            WaBiBaBuSy.Models.Wallpaper.AnimationDistributionMode.Simultaneous => 1,
            _ => 0
        };

        // Restore movement configuration
        var movement = layer.Movement;
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
        SpeedMultiplier = anim.SpeedMultiplier;
        FaceTravelDirection = anim.FaceTravelDirection;

        _loadedMovement = movement;
        _loadedCenterInitialPosition = anim.CenterInitialPosition;

        var fx = _loadedEffects = SpriteEffectsForm.From(anim.Effects);
        TrailEnabled = fx.TrailEnabled;
        TrailCount = fx.TrailCount;
        TrailSpacingMs = fx.TrailSpacingMs;
        TrailUseGradingColor = fx.TrailUseGradingColor;
        PulseEnabled = fx.PulseEnabled;
        PulseAmplitudePercent = (int)MathF.Round(fx.PulseAmplitude * 100f);
        PulseFrequencyHz = fx.PulseFrequencyHz;
        SpinDegPerSecond = fx.SpinDegPerSecond;
        BezelEntryIndex = (int)fx.Entry;
        BezelExitIndex = (int)fx.Exit;
        BezelColorHex = fx.BezelColorHex;
        BezelDurationMs = fx.BezelDurationMs;

        // Color grading
        var grading = anim.ColorGrading ?? new ColorGradingConfig();
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
        var pattern = anim.Pattern;
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
        foreach (var path in anim.AdditionalAnimationPaths)
            AdditionalAnimationPaths.Add(path);
        MultiImageSpread = anim.MultiImageSpread;
        MultiImagePhaseJitterMs = anim.MultiImagePhaseJitterMs;
    }

    /// <summary>
    /// The whole scene: background plus every layer (the selected one from the editor fields), unit flags from the room.
    /// Returns fresh objects — the editor keeps no reference into the result.
    /// </summary>
    public CrossScreenConfig BuildConfig()
    {
        var backgroundMode = BackgroundModeIndex switch
        {
            0 => BackgroundMode.SolidColor,
            1 => BackgroundMode.StretchedImage,
            2 => BackgroundMode.TiledImage,
            3 => BackgroundMode.ThreeZone,
            4 => BackgroundMode.IconZone,
            5 => BackgroundMode.Panorama,
            _ => BackgroundMode.SolidColor
        };

        if (SelectedLayerIndex >= 0 && SelectedLayerIndex < Layers.Count) Layers[SelectedLayerIndex].Layer = BuildLayer();

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
            Layers = Layers.Select(r =>
            {
                var layer = Clone(r.Layer);
                layer.Name = r.Name;
                layer.Enabled = r.Enabled;
                return layer;
            }).ToList(),
            SelectedMonitorIds = new List<string>(),   // targets are set by the caller (room selection / playlist item)
        };
        PhysicalUnits.ApplyRoomUnits(config, UsePhysicalUnits);
        return config;
    }

    /// <summary>The selected layer as edited (name / enabled come from its chip).</summary>
    private SceneLayer BuildLayer()
    {
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
        var row = SelectedLayerIndex >= 0 && SelectedLayerIndex < Layers.Count ? Layers[SelectedLayerIndex] : null;

        var layer = new SceneLayer
        {
            Name = row?.Name ?? "Layer",
            Enabled = row?.Enabled ?? true,
            Opacity = Math.Clamp(LayerOpacityPercent, 0, 100) / 100f,
            PhaseOffsetMs = LayerPhaseOffsetMs,
            ShowEveryMs = (long)Math.Round(Math.Max(0, LayerShowEverySeconds) * 1000),
            ShowForMs = (long)Math.Round(Math.Max(0, LayerShowForSeconds) * 1000),
            Distribution = distributionMode,
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
                Effects = BuildEffects(),
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
                MultiImagePhaseJitterMs = MultiImagePhaseJitterMs,
                CenterInitialPosition = _loadedCenterInitialPosition
            },
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
                NodePhaseDelayMs = Math.Max(0, NodePhaseDelayMs),
                StartX = _loadedMovement?.StartX,
                StartY = _loadedMovement?.StartY,
                EndX = _loadedMovement?.EndX,
                EndY = _loadedMovement?.EndY,
                OrbitCenterX = _loadedMovement?.OrbitCenterX,
                OrbitCenterY = _loadedMovement?.OrbitCenterY,
            }
        };
        if (row != null && string.IsNullOrWhiteSpace(row.Name) && !string.IsNullOrWhiteSpace(AnimationPath))
            layer.Name = Path.GetFileNameWithoutExtension(AnimationPath);
        return layer;
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

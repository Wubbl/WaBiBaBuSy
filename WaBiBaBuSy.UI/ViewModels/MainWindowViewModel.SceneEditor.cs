using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Core.Services.Desktop;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// Docked Scene editor (UI redesign Plan 2): the editor holds one draft scene; every change is
/// debounced into the room preview (draft layout = the room selection, or all nodes) and nothing
/// reaches the machines until Play. The preview badge switches the room between that draft on a
/// local design clock and the running scene on the players' shared clock.
/// </summary>
public partial class MainWindowViewModel
{
    private const int DraftDebounceMs = 150;
    private DispatcherTimer? _draftDebounce;

    /// <summary>The Scene editor (one draft config, <see cref="CrossScreenConfigViewModel.BuildConfig"/>).</summary>
    public CrossScreenConfigViewModel SceneEditor { get; } = new();

    /// <summary>The editor's draft as last built; replaced only when its content changes (keeps the design clock running).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomScene))]
    private CrossScreenConfig? _draftScene;

    /// <summary>Seat-map layout of the draft's targets (room selection, or every node).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomLayout))]
    private SeatMapLayoutResult? _draftLayout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomSpriteImagePath))]
    private string? _draftSpriteImagePath;

    /// <summary>True: the room paints the editor draft; false: the running scene. Follows the panel, toggled by the badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomSharedStartMs), nameof(RoomLayout), nameof(RoomSpriteImagePath), nameof(RoomScene), nameof(PreviewBadgeText))]
    private bool _isPreviewDraft = true;

    /// <summary>Right panel expanded (false = thin rail).</summary>
    [ObservableProperty] private bool _isRightPanelOpen = true;

    /// <summary>Selected right-panel tab: 0 = Scene, 1 = Playlist.</summary>
    [ObservableProperty] private int _rightPanelTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPauseGlyph))]
    private bool _previewPaused;

    /// <summary>Draft clock speed: 0 = 1×, 1 = 4×, 2 = 16× (ComboBox order).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewClockSpeed))]
    private int _previewSpeedIndex;

    /// <summary>Bumped to restart the draft clock at t = 0.</summary>
    [ObservableProperty] private int _previewRestartToken;

    /// <summary>Scene the room paints.</summary>
    public CrossScreenConfig? RoomScene => IsPreviewDraft ? DraftScene : ActiveScene;
    /// <summary>Shared clock epoch for the room; 0 = design clock (draft).</summary>
    public long RoomSharedStartMs => IsPreviewDraft ? 0 : ActiveSharedStartMs;
    /// <summary>Sprite image the room paints.</summary>
    public string? RoomSpriteImagePath => IsPreviewDraft ? DraftSpriteImagePath : ActiveSpriteImagePath;
    /// <summary>Layout the room paints with (draft targets, or the layout the players were started with).</summary>
    public SeatMapLayoutResult? RoomLayout => IsPreviewDraft ? DraftLayout : ActiveLayout;
    /// <summary>Draft clock multiplier for the room.</summary>
    public double PreviewClockSpeed => PreviewSpeedIndex switch { 1 => 4.0, 2 => 16.0, _ => 1.0 };
    /// <summary>Preview badge caption.</summary>
    public string PreviewBadgeText => IsPreviewDraft ? "◉ Preview: draft (not live)" : "● Preview: live";
    /// <summary>Pause button glyph.</summary>
    public string PreviewPauseGlyph => PreviewPaused ? "▶" : "⏸";
    /// <summary>Editor footer: who Play on selection targets.</summary>
    public string TargetsSummary => SelectedNodeCount == 0
        ? "Targets: all nodes (select nodes in the room to narrow)"
        : $"Targets: {SelectedNodeCount} selected";

    partial void OnActiveSceneChanged(CrossScreenConfig? value) => OnPropertyChanged(nameof(RoomScene));
    partial void OnActiveSharedStartMsChanged(long value) => OnPropertyChanged(nameof(RoomSharedStartMs));
    partial void OnActiveSpriteImagePathChanged(string? value) => OnPropertyChanged(nameof(RoomSpriteImagePath));
    partial void OnActiveLayoutChanged(SeatMapLayoutResult? value) => OnPropertyChanged(nameof(RoomLayout));

    /// <summary>Opening the panel previews the draft, collapsing it shows what is live.</summary>
    partial void OnIsRightPanelOpenChanged(bool value) => IsPreviewDraft = value;

    /// <summary>Every switch between draft and live starts the draft at t = 0.</summary>
    partial void OnIsPreviewDraftChanged(bool value) => PreviewRestartToken++;

    partial void OnSeatMapVersionChanged(int value) => ScheduleDraftUpdate();

    /// <summary>Default scene for a fresh editor (IconZone background, empty file).</summary>
    public static CrossScreenConfig DefaultScene() => new()
    {
        Background = new BackgroundLayerConfig
        {
            Mode = BackgroundMode.IconZone,
            ColorHex = "#000000",
            IconZonePaletteHexes = PaletteGenerator.GenerateHarmonious(8),
            IconCorridorColorHex = "#1E1E1E"
        },
        Animation = new AnimationLayerConfig
        {
            AnimationPath = string.Empty,
            TargetHeight = 720,
            Loop = true,
            VerticalAlign = WaBiBaBuSy.Models.Wallpaper.VerticalAlignment.Center,
            RotateWithPath = true
        },
        AnimationSpeedPxPerSecond = 500
    };

    /// <summary>Called once at the end of the constructor.</summary>
    private void InitSceneEditor()
    {
        SceneEditor.UsePhysicalUnits = RoomPhysicalUnits;
        SceneEditor.LoadFromConfig(DefaultScene());
        SceneEditor.PropertyChanged += OnSceneEditorPropertyChanged;
        SceneEditor.AdditionalAnimationPaths.CollectionChanged += (_, _) => ScheduleDraftUpdate();
        SceneEditor.IconZonePalette.CollectionChanged += OnPaletteChanged;
        foreach (var z in SceneEditor.IconZonePalette) z.PropertyChanged += OnPaletteItemChanged;
        ScheduleDraftUpdate();
    }

    private void OnSceneEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Outputs of the draft update itself must not schedule another one.
        if (e.PropertyName is nameof(CrossScreenConfigViewModel.SpeedReadout)
            or nameof(CrossScreenConfigViewModel.IsPickingBackgroundImage)) return;
        ScheduleDraftUpdate();
    }

    private void OnPaletteChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Clear() (LoadFromConfig, Randomize) raises Reset without OldItems: re-attach to every current item.
        foreach (var z in SceneEditor.IconZonePalette)
        {
            z.PropertyChanged -= OnPaletteItemChanged;
            z.PropertyChanged += OnPaletteItemChanged;
        }
        ScheduleDraftUpdate();
    }

    private void OnPaletteItemChanged(object? sender, PropertyChangedEventArgs e) => ScheduleDraftUpdate();

    /// <summary>Rebuild the draft preview after <see cref="DraftDebounceMs"/> ms of quiet. Safe from any thread.</summary>
    public void ScheduleDraftUpdate()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ScheduleDraftUpdate);
            return;
        }
        _draftDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(DraftDebounceMs), DispatcherPriority.Background, (_, _) =>
        {
            _draftDebounce!.Stop();
            UpdateDraft();
        });
        _draftDebounce.Stop();
        _draftDebounce.Start();
    }

    private void UpdateDraft()
    {
        try
        {
            var config = SceneEditor.BuildConfig();
            var targets = new HashSet<string>(SceneTargets.Resolve(RoomOrder, SelectedNodeIds));
            var nodes = targets.Count == 0 ? Clients.ToList() : Clients.Where(c => targets.Contains(c.ClientId)).ToList();
            var layout = nodes.Count == 0 ? new SeatMapLayoutResult() : BuildSeatLayout(nodes);

            DraftLayout = layout;
            DraftSpriteImagePath = ResolveSpriteImagePath(config.Animation.AnimationPath);
            // The 2 s topology refresh re-runs this: only a real change may restart the draft clock.
            if (!SceneChecks.SameScene(DraftScene, config)) DraftScene = config;
            SceneEditor.SetChecks(SceneChecks.Validate(config, layout), SceneChecks.CrossingReadout(config, layout));
        }
        catch (Exception ex)
        {
            AppLogger.CreateLogger<MainWindowViewModel>().LogWarning(ex, "Scene draft preview update failed");
        }
    }

    /// <summary>
    /// Gallery-strip click: sets the primary file (and selects it for the Dev tools), Ctrl+click adds
    /// an extra image, and while the Background tab is picking, sets the background image.
    /// </summary>
    public void PickGalleryItem(WallpaperItemViewModel item, bool additive)
    {
        bool setsPrimary = !SceneEditor.IsPickingBackgroundImage && !additive;
        SceneEditor.PickFromStrip(item.FilePath, additive);
        if (!setsPrimary) return;
        SelectWallpaper(item);
        if (SceneEditor.BackgroundColor is "" or "#000000")
            _ = SceneEditor.AutoDetectBackgroundColorCommand.ExecuteAsync(null);
    }

    [RelayCommand] private void TogglePreviewMode() => IsPreviewDraft = !IsPreviewDraft;
    [RelayCommand] private void TogglePreviewPause() => PreviewPaused = !PreviewPaused;
    [RelayCommand] private void RestartPreview() => PreviewRestartToken++;
    [RelayCommand] private void ToggleRightPanel() => IsRightPanelOpen = !IsRightPanelOpen;

    [RelayCommand]
    private void OpenSceneEditor()
    {
        IsRightPanelOpen = true;
        RightPanelTabIndex = 0;
    }

    [RelayCommand(CanExecute = nameof(HasNodeSelection))]
    private Task PlayOnSelection() => PlayDraftAsync(selectionOnly: true);

    [RelayCommand]
    private Task PlayOnAll() => PlayDraftAsync(selectionOnly: false);

    /// <summary>
    /// Send the draft to the machines: tears down a running show (playlist loop + its players/remote
    /// nodes) or a running scene first, then starts the draft on the selection (chain order) or on
    /// every node.
    /// </summary>
    private async Task PlayDraftAsync(bool selectionOnly)
    {
        var config = SceneEditor.BuildConfig();
        var path = config.Animation.AnimationPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            var warnings = SceneEditor.Warnings.ToList();
            warnings.Insert(0, string.IsNullOrWhiteSpace(path) ? "Pick an animation file from the strip" : $"File not found: {Path.GetFileName(path)}");
            SceneEditor.SetChecks(warnings.Distinct().ToList(), SceneEditor.SpeedReadout);
            return;
        }

        var targets = selectionOnly ? SceneTargets.Resolve(RoomOrder, SelectedNodeIds) : new List<string>();
        if (selectionOnly && targets.Count == 0) return;   // stale selection: never fall back to "all"
        config.SelectedMonitorIds = targets;

        if (_playlistOrchestrator?.IsRunning == true)
        {
            // A show never sets IsCrossScreenRunning, so StopCrossScreen alone would only cancel the
            // playlist loop and leave its current item's D2D players and remote nodes running.
            await StopPlaylistAsync();
            await ClearNodesAsync(Clients.Select(c => c.ClientId).Distinct().ToList());
        }
        if (IsCrossScreenRunning)
            await StopCrossScreen();

        _crossScreenConfig = config;
        HasAnimationConfig = true;
        await StartCrossScreen();
    }

    [RelayCommand]
    private void RevertScene() => SceneEditor.LoadFromConfig(RevertTarget());

    /// <summary>What Revert restores: the last played scene, else the default scene.</summary>
    private CrossScreenConfig RevertTarget() => _crossScreenConfig ?? DefaultScene();
}

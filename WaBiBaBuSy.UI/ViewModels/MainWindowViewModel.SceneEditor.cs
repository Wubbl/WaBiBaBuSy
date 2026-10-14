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
using WaBiBaBuSy.Models.Wallpaper.Suggest;
using WaBiBaBuSy.UI.Services;

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

    /// <summary>The ✨ Suggest strip over the Scene editor (spec 2026-10-14).</summary>
    public SuggestionsViewModel Suggestions { get; private set; } = null!;

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

    /// <summary>
    /// True while a Play request is in flight (draft build → teardown of any running show/scene → start).
    /// The guard lives in the playback host, so test runs and the show ▶ respect it too.
    /// </summary>
    public bool IsStartingScene => _server.IsStartingScene;

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

    partial void OnActiveSpriteImagePathChanged(string? value) => OnPropertyChanged(nameof(RoomSpriteImagePath));

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
        Suggestions = new SuggestionsViewModel(() => SceneEditor.BuildConfig(), c => SceneEditor.LoadFromConfig(c),
            BuildSelectionProfileAsync);
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
        if (_detached) return;   // the window closed
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

    /// <summary>Message from the last logged <see cref="UpdateDraft"/> failure, to de-duplicate log spam.</summary>
    private string? _lastDraftUpdateError;

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

            var warnings = SceneChecks.Validate(config, layout).ToList();
            var path = config.Animation.AnimationPath;
            // UI-only check (Models stays pure): flag a file the editor points at but that no longer
            // exists, instead of only catching it once the user hits Play.
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
                warnings.Add($"File not found: {Path.GetFileName(path)}");
            SceneEditor.SetChecks(warnings, SceneChecks.CrossingReadout(config, layout));

            _lastDraftUpdateError = null;
        }
        catch (Exception ex)
        {
            // The 150 ms debounce can re-run this many times while a bad edit stands; only log once per
            // distinct failure instead of spamming the same warning on every keystroke.
            if (_lastDraftUpdateError != ex.Message)
            {
                _lastDraftUpdateError = ex.Message;
                AppLogger.CreateLogger<MainWindowViewModel>().LogWarning(ex, "Scene draft preview update failed");
            }
        }
    }

    /// <summary>
    /// Gallery-strip click: sets the primary file (and selects it for the Dev tools), Ctrl+click adds
    /// an extra image, and while the Background tab is picking, sets the background image.
    /// </summary>
    public void PickGalleryItem(WallpaperItemViewModel item, bool additive)
    {
        Suggestions.Discard();
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

    private bool CanPlayOnSelection() => HasNodeSelection && !IsStartingScene;
    private bool CanPlayOnAll() => !IsStartingScene;

    [RelayCommand(CanExecute = nameof(CanPlayOnSelection))]
    private Task PlayOnSelection() => PlayDraftAsync(selectionOnly: true);

    [RelayCommand(CanExecute = nameof(CanPlayOnAll))]
    private Task PlayOnAll() => PlayDraftAsync(selectionOnly: false);

    private bool CanPlayPlainOnSelection(WallpaperItemViewModel? item) => CanPlayOnSelection();
    private bool CanPlayPlainOnAll(WallpaperItemViewModel? item) => CanPlayOnAll();

    /// <summary>Plain wallpaper on the selection: a gallery item, or the editor's file when null.</summary>
    [RelayCommand(CanExecute = nameof(CanPlayPlainOnSelection))]
    private Task PlayPlainOnSelection(WallpaperItemViewModel? item) => PlayPlainAsync(item, selectionOnly: true);

    /// <summary>Plain wallpaper on every node: a gallery item, or the editor's file when null.</summary>
    [RelayCommand(CanExecute = nameof(CanPlayPlainOnAll))]
    private Task PlayPlainOnAll(WallpaperItemViewModel? item) => PlayPlainAsync(item, selectionOnly: false);

    /// <summary>
    /// Show a file as a plain wallpaper (<see cref="PlainScene"/>: fills each monitor, no movement,
    /// colors, pattern or rotation). The plain scene is loaded into the editor first, so the editor,
    /// the preview, Revert and Save to playlist all see exactly what is playing; then it plays like a draft.
    /// </summary>
    private Task PlayPlainAsync(WallpaperItemViewModel? item, bool selectionOnly)
    {
        if (IsStartingScene) return Task.CompletedTask;
        var path = item?.FilePath ?? SceneEditor.AnimationPath;
        if (string.IsNullOrWhiteSpace(path)) return PlayDraftAsync(selectionOnly);   // reports "Pick an animation file"

        if (item != null) SelectWallpaper(item);
        LoadDraft(PlainScene.Create(path));
        return PlayDraftAsync(selectionOnly);
    }

    /// <summary>
    /// Send the draft to the machines: tears down a running show (playlist loop + its players/remote
    /// nodes) or a running scene first, then starts the draft on the selection (chain order) or on
    /// every node. Guarded by <see cref="IsStartingScene"/> so the two Play buttons (and the toolbar
    /// show ▶, via <see cref="StartPlaylist"/>) cannot both start a scene before the first apply
    /// finishes tearing down what came before it.
    /// </summary>
    private async Task PlayDraftAsync(bool selectionOnly)
    {
        if (!_server.TryBeginStart()) return;
        try
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

            // Tears down a running show (loop + its players / remote nodes) or scene, then starts the draft.
            SyncRoom();
            await _server.PlaySceneAsync(config);
        }
        finally
        {
            _server.EndStart();
        }
    }

    [RelayCommand]
    private void RevertScene() => LoadDraft(RevertTarget());

    /// <summary>What Revert restores: the loaded playlist item, else what is playing now (a running scene,
    /// or the last one played), else the default scene.</summary>
    private CrossScreenConfig RevertTarget() => PlaylistEditor.LoadedItem?.Model.Config ?? ActiveScene ?? _server.LastScene ?? DefaultScene();

    /// <summary>
    /// Replace the draft from outside the Suggest strip (preset, playlist item, plain wallpaper, editor revert):
    /// an open strip is closed first, so its ✕ cannot later restore its saved draft over this one.
    /// </summary>
    private void LoadDraft(CrossScreenConfig config)
    {
        Suggestions.Discard();
        SceneEditor.LoadFromConfig(config);
    }

    /// <summary>
    /// Profile the draft's animation files for the Suggest strip: gallery facts (type, duration, video resolution)
    /// on the UI thread, then the first-frame decode off it. Null when no file is picked.
    /// </summary>
    private async Task<SelectionProfile?> BuildSelectionProfileAsync()
    {
        var paths = new[] { SceneEditor.AnimationPath }.Concat(SceneEditor.AdditionalAnimationPaths)
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0) return null;

        // Gallery facts snapshotted on the UI thread; only the decode runs off it.
        var facts = paths.Select(p =>
        {
            var item = Wallpapers.FirstOrDefault(w => string.Equals(w.FilePath, p, StringComparison.OrdinalIgnoreCase));
            bool video = item?.Type == WallpaperType.Video;
            return (path: p, video, thumb: item?.ThumbnailPath, resolution: item?.Resolution, durationMs: item?.DurationMs ?? 0);
        }).ToList();

        int screenW = 1920, screenH = 1080;
        float pixelsPerCm = 0f;
        try
        {
            var primary = LocalMonitors.Snapshot().Monitors.FirstOrDefault(m => m.IsPrimary);
            if (primary != null) (screenW, screenH) = (primary.Width, primary.Height);
            pixelsPerCm = LocalMonitors.ReferencePixelsPerCm;
        }
        catch (Exception ex)
        {
            AppLogger.CreateLogger<MainWindowViewModel>().LogWarning(ex, "Suggest: could not read the primary monitor, assuming 1920x1080");
        }

        var files = await Task.Run(() => facts.Select(x =>
        {
            var (w, h, pixels) = (0, 0, PixelFacts.Unknown);
            try
            {
                (w, h, pixels) = MediaProbe.Read(x.video && !string.IsNullOrEmpty(x.thumb) ? x.thumb : x.path);
            }
            catch (Exception ex)
            {
                // ponytail: any decode failure (OOM on a huge file, a Magick edge case) degrades to "unknown pixels"
                AppLogger.CreateLogger<MainWindowViewModel>().LogWarning(ex, "Suggest: could not analyse {Path}", x.path);
            }
            if (x.video && TryParseResolution(x.resolution, out int vw, out int vh)) (w, h) = (vw, vh);
            return MediaProfile.Create(x.path, w, h, screenW, screenH, pixels, x.durationMs);
        }).ToList());

        return new SelectionProfile(files, screenW, screenH, pixelsPerCm);
    }

    private static bool TryParseResolution(string? resolution, out int width, out int height)
    {
        width = height = 0;
        var parts = (resolution ?? string.Empty).Split('x', 'X', '×');
        return parts.Length == 2 && int.TryParse(parts[0].Trim(), out width) && int.TryParse(parts[1].Trim(), out height);
    }
}

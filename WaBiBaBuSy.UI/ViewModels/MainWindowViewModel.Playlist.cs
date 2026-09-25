using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>Playlist tab wiring (UI redesign Plan 2): the tab edits playlists, the Scene editor edits their items.</summary>
public partial class MainWindowViewModel
{
    /// <summary>The Playlist tab's view model. Not named "Playlist": that would shadow the model type in this class.</summary>
    public PlaylistViewModel PlaylistEditor { get; } = new();

    /// <summary>Called once at the end of the constructor, after <see cref="InitSceneEditor"/>.</summary>
    private void InitPlaylist()
    {
        PlaylistEditor.StartShow = StartPlaylist;
        PlaylistEditor.StopShow = StopPlaylistAsync;
        PlaylistEditor.CaptureDraft = () => SceneEditor.BuildConfig();
        PlaylistEditor.LoadIntoEditor = config => SceneEditor.LoadFromConfig(config);
        PlaylistEditor.ThumbnailProvider = path =>
            Wallpapers.FirstOrDefault(w => string.Equals(w.FilePath, path, StringComparison.OrdinalIgnoreCase))?.Thumbnail;
        PlaylistEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistViewModel.HasLoadedItem))
                UpdateLoadedPlaylistItemCommand.NotifyCanExecuteChanged();
            else if (e.PropertyName == nameof(PlaylistViewModel.IsShowRunning))
                OnPropertyChanged(nameof(IsAnythingPlaying));
        };
        _ = PlaylistEditor.InitializeAsync();
    }

    /// <summary>True while a show or a manual scene is running (shows the toolbar ■).</summary>
    public bool IsAnythingPlaying => IsCrossScreenRunning || PlaylistEditor.IsShowRunning;

    /// <summary>Toolbar ⏭: advance the running show to its next item.</summary>
    [RelayCommand]
    private void SkipShowItem() => _playlistOrchestrator?.Skip();

    /// <summary>Editor footer: append the draft to the playlist as a new item.</summary>
    [RelayCommand]
    private void SaveSceneAsNewItem() => PlaylistEditor.AddItem(SceneEditor.BuildConfig());

    /// <summary>Editor footer: write the draft into the loaded playlist item.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateLoadedPlaylistItem))]
    private void UpdateLoadedPlaylistItem() => PlaylistEditor.UpdateLoadedItem(SceneEditor.BuildConfig());

    private bool CanUpdateLoadedPlaylistItem() => PlaylistEditor.HasLoadedItem;

    [RelayCommand]
    private void OpenPlaylistTab()
    {
        IsRightPanelOpen = true;
        RightPanelTabIndex = 1;
    }
}

using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// The Playlist tab: pick / create / save playlists, reorder items by dragging, edit durations in
/// seconds. Selecting an item loads its scene into the Scene editor as the draft; the editor's
/// "Save to playlist" writes back through <see cref="AddItem"/> / <see cref="UpdateLoadedItem"/>.
/// The show is started and stopped through host callbacks.
/// </summary>
public partial class PlaylistViewModel : ViewModelBase
{
    private readonly PlaylistStore _store;
    private bool _suppressPlaylistLoad;

    /// <summary>Host callback: the Scene editor's current draft (a fresh copy), for "＋ Add current scene".</summary>
    public Func<CrossScreenConfig>? CaptureDraft { get; set; }

    /// <summary>Host callback: load a scene into the Scene editor as the draft.</summary>
    public Action<CrossScreenConfig>? LoadIntoEditor { get; set; }

    /// <summary>Host callback: gallery thumbnail for a content path, or null.</summary>
    public Func<string, Bitmap?>? ThumbnailProvider { get; set; }

    /// <summary>Host callback: start rotating the given playlist.</summary>
    public Action<Playlist>? StartShow { get; set; }

    /// <summary>Host callback: stop the running show.</summary>
    public Func<Task>? StopShow { get; set; }

    [ObservableProperty] private string _playlistName = "Party";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartPlaylistShowCommand))]
    private bool _isShowRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSummary))]
    private bool _loop = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSummary))]
    private bool _shuffle = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSummary), nameof(DefaultDurationSeconds))]
    private int _defaultDurationMs = 30_000;

    /// <summary>Default dwell in whole seconds for the header box (1 s – 24 h).</summary>
    public int DefaultDurationSeconds
    {
        get => Math.Max(1, DefaultDurationMs / 1000);
        set => DefaultDurationMs = Math.Clamp(value, 1, PlaylistEditing.MaxSeconds) * 1000;
    }

    public ObservableCollection<PlaylistItemRow> Items { get; } = new();

    [ObservableProperty] private PlaylistItemRow? _selectedItem;

    /// <summary>The item whose scene is in the Scene editor ("Save to playlist → Update" writes here), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadedItem), nameof(LoadedItemName))]
    private PlaylistItemRow? _loadedItem;

    /// <summary>True when an item is loaded into the Scene editor.</summary>
    public bool HasLoadedItem => LoadedItem != null;

    /// <summary>Name of the loaded item, for the "Update …" menu entry.</summary>
    public string LoadedItemName => LoadedItem?.Name ?? string.Empty;

    /// <summary>Saved playlist names (file names, newest first) for the pickers.</summary>
    public ObservableCollection<string> SavedPlaylists { get; } = new();

    /// <summary>Picker selection; choosing a name loads that playlist.</summary>
    [ObservableProperty] private string? _selectedSavedPlaylist;

    public PlaylistViewModel(PlaylistStore? store = null)
    {
        _store = store ?? new PlaylistStore();
        Items.CollectionChanged += OnItemsChanged;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A drag reorder raises Move with the item in both NewItems and OldItems: nothing to (un)subscribe.
        if (e.Action == NotifyCollectionChangedAction.Move) return;
        foreach (var row in e.NewItems?.OfType<PlaylistItemRow>() ?? Enumerable.Empty<PlaylistItemRow>())
        {
            row.PlaylistDefaultDurationMs = DefaultDurationMs;
            row.Thumbnail ??= ThumbnailProvider?.Invoke(row.Model.Config.Animation.AnimationPath);
            row.PropertyChanged += OnRowPropertyChanged;
        }
        foreach (var row in e.OldItems?.OfType<PlaylistItemRow>() ?? Enumerable.Empty<PlaylistItemRow>())
            row.PropertyChanged -= OnRowPropertyChanged;
        OnPropertyChanged(nameof(ShowSummary));
        StartPlaylistShowCommand.NotifyCanExecuteChanged();
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistItemRow.DurationText))
            OnPropertyChanged(nameof(ShowSummary));
        else if (e.PropertyName == nameof(PlaylistItemRow.Name) && sender == LoadedItem)
            OnPropertyChanged(nameof(LoadedItemName));
    }

    /// <summary>Keep every row's effective-duration display in sync with the playlist-wide fallback.</summary>
    partial void OnDefaultDurationMsChanged(int value)
    {
        foreach (var row in Items) row.PlaylistDefaultDurationMs = value;
    }

    /// <summary>Selecting a row loads its scene into the editor and makes it the target of "Update".</summary>
    partial void OnSelectedItemChanged(PlaylistItemRow? value)
    {
        if (value == null) return;
        LoadedItem = value;
        LoadIntoEditor?.Invoke(value.Model.Config);
    }

    partial void OnSelectedSavedPlaylistChanged(string? value)
    {
        if (_suppressPlaylistLoad || string.IsNullOrEmpty(value)) return;
        _ = LoadSavedAsync(value);
    }

    /// <summary>Header line: item count and total run time of one full cycle.</summary>
    public string ShowSummary
    {
        get
        {
            if (Items.Count == 0) return "No items yet — build a scene, then ＋ Add current scene.";
            var totalMs = Items.Sum(r => (long)r.EffectiveDurationMs);
            var span = TimeSpan.FromMilliseconds(totalMs);
            var length = span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s"
                : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
                                         : $"{span.TotalSeconds:0.#}s";
            return $"{Items.Count} item{(Items.Count == 1 ? "" : "s")} · one cycle ≈ {length}"
                 + (Loop ? " · looping" : " · stops after last item")
                 + (Shuffle ? " · shuffled" : string.Empty);
        }
    }

    // --- Saved playlists ----------------------------------------------------

    /// <summary>Fill the picker and load the most recently saved playlist, if any.</summary>
    public async Task InitializeAsync()
    {
        RefreshSavedPlaylists();
        if (SavedPlaylists.Count > 0) await LoadSavedAsync(SavedPlaylists[0]);
    }

    /// <summary>Re-read the saved playlist names; <paramref name="select"/> becomes the picker selection without reloading it.</summary>
    public void RefreshSavedPlaylists(string? select = null)
    {
        _suppressPlaylistLoad = true;
        try
        {
            SavedPlaylists.Clear();
            foreach (var path in _store.List()) SavedPlaylists.Add(Path.GetFileNameWithoutExtension(path));
            SelectedSavedPlaylist = select != null && SavedPlaylists.Contains(select) ? select : null;
        }
        finally { _suppressPlaylistLoad = false; }
    }

    /// <summary>Load a saved playlist by file name (as listed in <see cref="SavedPlaylists"/>).</summary>
    public async Task LoadSavedAsync(string name)
    {
        try
        {
            var playlist = await _store.LoadAsync(_store.PathFor(name));
            LoadFrom(playlist);
            _suppressPlaylistLoad = true;
            try { SelectedSavedPlaylist = name; }
            finally { _suppressPlaylistLoad = false; }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Playlist] Loading '{name}' failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void NewPlaylist()
    {
        LoadFrom(new Playlist { Name = UniqueName("Playlist"), DefaultItemDurationMs = DefaultDurationMs });
        RefreshSavedPlaylists();
    }

    private string UniqueName(string stem)
    {
        for (int i = 1; ; i++)
        {
            var name = $"{stem} {i}";
            if (!SavedPlaylists.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;
        }
    }

    [RelayCommand]
    private async Task SavePlaylist()
    {
        try
        {
            await _store.SaveAsync(BuildPlaylist());
            RefreshSavedPlaylists(Path.GetFileNameWithoutExtension(_store.PathFor(PlaylistName)));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Playlist] Saving '{PlaylistName}' failed: {ex.Message}");
        }
    }

    // --- Items --------------------------------------------------------------

    [RelayCommand]
    private void AddCurrentScene()
    {
        var config = CaptureDraft?.Invoke();
        if (config != null) AddItem(config);
    }

    /// <summary>Append a scene as a new item (named after its file) and make it the loaded item.</summary>
    public PlaylistItemRow AddItem(CrossScreenConfig config)
    {
        var name = string.IsNullOrWhiteSpace(config.Animation.AnimationPath)
            ? "Scene"
            : Path.GetFileNameWithoutExtension(config.Animation.AnimationPath);
        var row = new PlaylistItemRow(new PlaylistItem { Name = name, Config = config });
        Items.Add(row);
        SelectedItem = row;
        return row;
    }

    /// <summary>Write a scene into the loaded item. The item keeps its own target list.</summary>
    public void UpdateLoadedItem(CrossScreenConfig config)
    {
        if (LoadedItem == null) return;
        config.SelectedMonitorIds = LoadedItem.Model.Config.SelectedMonitorIds;
        LoadedItem.ReplaceConfig(config);
        LoadedItem.Thumbnail = ThumbnailProvider?.Invoke(config.Animation.AnimationPath);
    }

    [RelayCommand]
    private void DuplicateItem(PlaylistItemRow? row)
    {
        if (row == null) return;
        row.CommitToModel();
        Items.Insert(Items.IndexOf(row) + 1, new PlaylistItemRow(PlaylistEditing.Duplicate(row.Model)));
    }

    [RelayCommand]
    private void RemoveItem(PlaylistItemRow? row)
    {
        if (row == null) return;
        if (LoadedItem == row) LoadedItem = null;
        Items.Remove(row);
    }

    /// <summary>Drag reorder: move the row at <paramref name="from"/> into the gap before row <paramref name="insertBefore"/>.</summary>
    public void MoveItem(int from, int insertBefore)
    {
        int to = PlaylistEditing.MoveTarget(Items.Count, from, insertBefore);
        if (to >= 0) Items.Move(from, to);
    }

    /// <summary>Highlight the row the running show is on (-1 = none). Rows edited during a show may be off by the edit.</summary>
    public void MarkPlaying(int index)
    {
        for (int i = 0; i < Items.Count; i++) Items[i].IsPlaying = i == index;
    }

    // --- Show ---------------------------------------------------------------

    private Playlist BuildPlaylist()
    {
        foreach (var row in Items) row.CommitToModel();
        return new Playlist
        {
            Name = PlaylistName,
            Loop = Loop,
            Shuffle = Shuffle,
            DefaultItemDurationMs = DefaultDurationMs,
            Items = Items.Select(r => r.Model).ToList(),
        };
    }

    private bool CanStartShow() => !IsShowRunning && Items.Count > 0;

    [RelayCommand(CanExecute = nameof(CanStartShow))]
    private void StartPlaylistShow()
    {
        var playlist = BuildPlaylist();
        // The orchestrator gets its own deep copy (like PlaylistEditing.Duplicate's JSON round-trip):
        // editing items while the show runs must not mutate the running show or trigger unprefetched
        // downloads. SavePlaylist() still calls BuildPlaylist() itself, so Save keeps using the live rows.
        var snapshot = JsonSerializer.Deserialize<Playlist>(JsonSerializer.Serialize(playlist))!;
        StartShow?.Invoke(snapshot);
        IsShowRunning = true;
    }

    [RelayCommand]
    private async Task StopPlaylistShow()
    {
        if (StopShow != null) await StopShow();
        IsShowRunning = false;
        MarkPlaying(-1);
    }

    /// <summary>Show a playlist model in the tab (after loading or creating one). Nothing is loaded into the editor.</summary>
    public void LoadFrom(Playlist playlist)
    {
        PlaylistName = playlist.Name;
        Loop = playlist.Loop;
        Shuffle = playlist.Shuffle;
        DefaultDurationMs = playlist.DefaultItemDurationMs;
        LoadedItem = null;
        SelectedItem = null;
        // Clear() raises a Reset without OldItems, so detach row handlers explicitly.
        foreach (var row in Items) row.PropertyChanged -= OnRowPropertyChanged;
        Items.Clear();
        foreach (var item in playlist.Items) Items.Add(new PlaylistItemRow(item));
    }
}

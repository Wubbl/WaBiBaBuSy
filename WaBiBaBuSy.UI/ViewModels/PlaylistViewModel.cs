using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// ViewModel for the Playlist / Party Mode dialog. Composes saved CrossScreen configs into an
/// ordered, persisted list and starts/stops the rotation via callbacks the host wires up.
/// </summary>
public partial class PlaylistViewModel : ViewModelBase
{
    private readonly PlaylistStore _store = new();

    /// <summary>Host callback: open the CrossScreen config dialog, return the built config or null.</summary>
    public Func<CrossScreenConfig?, Task<CrossScreenConfig?>>? EditConfigAsync { get; set; }

    /// <summary>Host callback: start rotating the given playlist.</summary>
    public Action<Playlist>? StartShow { get; set; }

    /// <summary>Host callback: stop the running show.</summary>
    public Func<Task>? StopShow { get; set; }

    [ObservableProperty] private string _playlistName = "Party";
    [ObservableProperty] private bool _isShowRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSummary))]
    private bool _loop = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSummary))]
    private bool _shuffle = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSummary))]
    private int _defaultDurationMs = 30_000;

    public ObservableCollection<PlaylistItemRow> Items { get; } = new();

    [ObservableProperty] private PlaylistItemRow? _selectedItem;

    public PlaylistViewModel()
    {
        Items.CollectionChanged += (_, e) =>
        {
            foreach (var row in e.NewItems?.OfType<PlaylistItemRow>() ?? Enumerable.Empty<PlaylistItemRow>())
            {
                row.PlaylistDefaultDurationMs = DefaultDurationMs;
                row.PropertyChanged += OnRowPropertyChanged;
            }
            foreach (var row in e.OldItems?.OfType<PlaylistItemRow>() ?? Enumerable.Empty<PlaylistItemRow>())
                row.PropertyChanged -= OnRowPropertyChanged;
            OnPropertyChanged(nameof(ShowSummary));
        };
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistItemRow.DurationText))
            OnPropertyChanged(nameof(ShowSummary));
    }

    /// <summary>Keep every row's effective-duration display in sync with the playlist-wide fallback.</summary>
    partial void OnDefaultDurationMsChanged(int value)
    {
        foreach (var row in Items) row.PlaylistDefaultDurationMs = value;
    }

    /// <summary>Header line: item count and total run time of one full cycle.</summary>
    public string ShowSummary
    {
        get
        {
            if (Items.Count == 0) return "No items — use Add to capture an animation config.";
            var totalMs = Items.Sum(r => (long)r.EffectiveDurationMs);
            var span = System.TimeSpan.FromMilliseconds(totalMs);
            var length = span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s"
                : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
                                         : $"{span.TotalSeconds:0.#}s";
            return $"{Items.Count} item{(Items.Count == 1 ? "" : "s")} · one cycle ≈ {length}"
                 + (Loop ? " · looping" : " · stops after last item")
                 + (Shuffle ? " · shuffled" : string.Empty);
        }
    }

    // --- Item commands ------------------------------------------------------

    [RelayCommand]
    private async Task AddItem()
    {
        if (EditConfigAsync == null) return;
        var config = await EditConfigAsync(null);
        if (config == null) return;

        var item = new PlaylistItem
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(config.Animation.AnimationPath),
            Config = config,
        };
        Items.Add(new PlaylistItemRow(item));
    }

    [RelayCommand]
    private async Task EditItem()
    {
        if (EditConfigAsync == null || SelectedItem == null) return;
        var updated = await EditConfigAsync(SelectedItem.Model.Config);
        if (updated == null) return;
        SelectedItem.Model.Config = updated;
        SelectedItem.RefreshSummary();
    }

    [RelayCommand]
    private void RemoveItem()
    {
        if (SelectedItem != null) Items.Remove(SelectedItem);
    }

    [RelayCommand]
    private void DuplicateItem()
    {
        if (SelectedItem == null) return;
        SelectedItem.CommitToModel();
        var clone = System.Text.Json.JsonSerializer.Deserialize<PlaylistItem>(
            System.Text.Json.JsonSerializer.Serialize(SelectedItem.Model))!;
        Items.Add(new PlaylistItemRow(clone));
    }

    [RelayCommand]
    private void MoveUp()
    {
        var i = SelectedItem == null ? -1 : Items.IndexOf(SelectedItem);
        if (i > 0) Items.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveDown()
    {
        var i = SelectedItem == null ? -1 : Items.IndexOf(SelectedItem);
        if (i >= 0 && i < Items.Count - 1) Items.Move(i, i + 1);
    }

    // --- Playlist-level commands -------------------------------------------

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

    [RelayCommand]
    private async Task SavePlaylist() => await _store.SaveAsync(BuildPlaylist());

    [RelayCommand]
    private void StartShowCommand()
    {
        if (Items.Count == 0) return;
        StartShow?.Invoke(BuildPlaylist());
        IsShowRunning = true;
    }

    [RelayCommand]
    private async Task StopShowCommand()
    {
        if (StopShow != null) await StopShow();
        IsShowRunning = false;
    }

    /// <summary>Load a playlist model into the VM (e.g. after a Load-file action).</summary>
    public void LoadFrom(Playlist playlist)
    {
        PlaylistName = playlist.Name;
        Loop = playlist.Loop;
        Shuffle = playlist.Shuffle;
        DefaultDurationMs = playlist.DefaultItemDurationMs;
        // Clear() raises a Reset without OldItems, so detach row handlers explicitly.
        foreach (var row in Items) row.PropertyChanged -= OnRowPropertyChanged;
        Items.Clear();
        foreach (var item in playlist.Items) Items.Add(new PlaylistItemRow(item));
    }
}

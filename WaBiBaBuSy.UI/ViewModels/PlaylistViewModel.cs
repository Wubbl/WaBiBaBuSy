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
    [ObservableProperty] private bool _loop = true;
    [ObservableProperty] private bool _shuffle = false;
    [ObservableProperty] private int _defaultDurationMs = 30_000;
    [ObservableProperty] private bool _isShowRunning;

    public ObservableCollection<PlaylistItemRow> Items { get; } = new();

    [ObservableProperty] private PlaylistItemRow? _selectedItem;

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
        Items.Clear();
        foreach (var item in playlist.Items) Items.Add(new PlaylistItemRow(item));
    }
}

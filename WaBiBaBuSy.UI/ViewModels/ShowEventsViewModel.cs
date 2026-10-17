using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.UI.Services;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>One seat in the Spotlight picker.</summary>
public sealed record SeatChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The Events strip under the room view: one-shot events on every seat (roadmap 2.4).</summary>
public partial class ShowEventsViewModel : ObservableObject
{
    private readonly ServerPlaybackHost _server;
    private IStorageProvider? _storageProvider;

    public ShowEventsViewModel(ServerPlaybackHost server)
    {
        _server = server;
        _server.PropertyChanged += OnServerPropertyChanged;
        _announceText = server.EventSettings.AnnounceText;
        _hotkeysEnabled = server.EventSettings.HotkeysEnabled;
    }

    public ObservableCollection<SeatChoice> Seats { get; } = new();

    /// <summary>Who the event buttons fire for: Everyone, a tag, or one seat of the room.</summary>
    public ObservableCollection<EventTarget> Targets { get; } = new() { EventTarget.Everyone };

    [ObservableProperty] private EventTarget? _selectedTarget = EventTarget.Everyone;

    [ObservableProperty] private SeatChoice? _selectedSeat;
    [ObservableProperty] private string _announceText;

    /// <summary>Ctrl+Alt+1..5 fire events while the server runs (off frees AltGr+digit on German keyboards).</summary>
    [ObservableProperty] private bool _hotkeysEnabled;

    public bool CanFire => _server.CanFireEvents;
    public string? StatusText => _server.EventStatusText;

    /// <summary>File name of the Traversal image, or the fallback.</summary>
    public string TraversalImageName => string.IsNullOrWhiteSpace(_server.EventSettings.TraversalImagePath)
        ? "scene sprite"
        : Path.GetFileName(_server.EventSettings.TraversalImagePath);

    public void SetStorageProvider(IStorageProvider storageProvider) => _storageProvider = storageProvider;

    /// <summary>
    /// Seats of the running layout in traversal order; keeps the selection when it still exists. No-op when the
    /// list is unchanged (same ids and labels in order), so a periodic refresh leaves the picker alone.
    /// </summary>
    public void RefreshSeats(IReadOnlyList<SeatChoice> seats)
    {
        if (Seats.SequenceEqual(seats)) return;   // records: compares Id and Label
        var keep = SelectedSeat?.Id ?? _server.EventSettings.LastTargetSeatId;
        Seats.Clear();
        foreach (var seat in seats) Seats.Add(seat);
        SelectedSeat = Seats.FirstOrDefault(s => s.Id == keep) ?? Seats.FirstOrDefault();
    }

    /// <summary>
    /// Target picker: Everyone, the tags in use, then every seat of the room (<paramref name="seats"/>: id, label).
    /// No-op when unchanged; keeps the selection when it still exists, else falls back to Everyone.
    /// </summary>
    public void RefreshTargets(IReadOnlyList<(string Id, string Label)> seats)
    {
        var choices = EventTarget.Choices(_server.Tags.All(), seats);
        if (Targets.SequenceEqual(choices)) return;
        var keep = SelectedTarget;
        Targets.Clear();
        foreach (var t in choices) Targets.Add(t);
        SelectedTarget = Targets.FirstOrDefault(t => keep != null && t.Kind == keep.Kind && t.Value == keep.Value) ?? Targets[0];
    }

    /// <summary>Node ids of the picked target; null = everyone.</summary>
    private IReadOnlyList<string>? TargetIds() => (SelectedTarget ?? EventTarget.Everyone).Resolve(_server.Tags.NodesWith);

    /// <summary>The room view's selected node becomes the Spotlight target (when it is a seat of the running layout).</summary>
    public void SelectSeat(string? id)
    {
        var seat = Seats.FirstOrDefault(s => s.Id == id);
        if (seat != null) SelectedSeat = seat;
    }

    partial void OnSelectedSeatChanged(SeatChoice? value)
    {
        if (value == null || value.Id == _server.EventSettings.LastTargetSeatId) return;
        _server.EventSettings.LastTargetSeatId = value.Id;   // the Spotlight hotkey uses it
        _server.SaveEventSettings(_server.EventSettings);
    }

    // In memory only; saved when Announce fires (no disk write per keystroke).
    partial void OnAnnounceTextChanged(string value) => _server.EventSettings.AnnounceText = value;

    partial void OnHotkeysEnabledChanged(bool value)
    {
        if (value == _server.EventSettings.HotkeysEnabled) return;
        _server.EventSettings.HotkeysEnabled = value;
        _server.SaveEventSettings(_server.EventSettings);   // the tray re-registers the hotkeys
    }

    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task Spotlight() => _server.FireEventAsync(ShowEventType.Spotlight, SelectedSeat?.Id, TargetIds());

    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task Celebration() => _server.FireEventAsync(ShowEventType.Celebration, null, TargetIds());

    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task Traversal() => _server.FireEventAsync(ShowEventType.Traversal, null, TargetIds());

    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task Blackout() => _server.FireEventAsync(ShowEventType.Blackout, null, TargetIds());

    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task Announce()
    {
        _server.SaveEventSettings(_server.EventSettings);   // persist the typed text (interrupts and the hotkey use it)
        return _server.FireEventAsync(ShowEventType.Announce, null, TargetIds());
    }

    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task Identify() => _server.FireEventAsync(ShowEventType.Identify, null, TargetIds());

    [RelayCommand]
    private async Task PickTraversalImage()
    {
        if (_storageProvider == null) return;
        var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Traversal image",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.gif", "*.jpg", "*.jpeg", "*.bmp" } } },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        _server.EventSettings.TraversalImagePath = path;
        _server.SaveEventSettings(_server.EventSettings);
        OnPropertyChanged(nameof(TraversalImageName));
        _ = _server.PrefetchEventImageAsync();
    }

    [RelayCommand]
    private void ClearTraversalImage()
    {
        _server.EventSettings.TraversalImagePath = string.Empty;
        _server.SaveEventSettings(_server.EventSettings);
        OnPropertyChanged(nameof(TraversalImageName));
    }

    private void OnServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ServerPlaybackHost.CanFireEvents):
            case nameof(ServerPlaybackHost.ActiveScene):
            case nameof(ServerPlaybackHost.ActiveLayout):
                OnPropertyChanged(nameof(CanFire));
                SpotlightCommand.NotifyCanExecuteChanged();
                CelebrationCommand.NotifyCanExecuteChanged();
                TraversalCommand.NotifyCanExecuteChanged();
                BlackoutCommand.NotifyCanExecuteChanged();
                AnnounceCommand.NotifyCanExecuteChanged();
                IdentifyCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ServerPlaybackHost.EventStatusText):
                OnPropertyChanged(nameof(StatusText));
                break;
        }
    }

    /// <summary>The window closed: stop listening to the app-lifetime host.</summary>
    public void Detach() => _server.PropertyChanged -= OnServerPropertyChanged;
}

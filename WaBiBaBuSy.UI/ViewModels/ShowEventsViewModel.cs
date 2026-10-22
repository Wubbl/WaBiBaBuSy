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

/// <summary>A seat of the room: the Spotlight target (the node selected in the room view).</summary>
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

    // The room's seats, for labelling the room view's selection.
    private IReadOnlyList<SeatChoice> _seats = Array.Empty<SeatChoice>();

    /// <summary>Who the event buttons fire for: Everyone, a tag, or one seat of the room.</summary>
    public ObservableCollection<EventTarget> Targets { get; } = new() { EventTarget.Everyone };

    [ObservableProperty] private EventTarget? _selectedTarget = EventTarget.Everyone;

    /// <summary>The Spotlight target: the node selected in the room view (null = Spotlight disabled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpotlightLabel))]
    [NotifyCanExecuteChangedFor(nameof(SpotlightCommand))]
    private SeatChoice? _selectedSeat;
    [ObservableProperty] private string _announceText;

    /// <summary>Ctrl+Alt+1..5 fire events while the server runs (off frees AltGr+digit on German keyboards).</summary>
    [ObservableProperty] private bool _hotkeysEnabled;

    public bool CanFire => _server.CanFireEvents;

    /// <summary>"★ Spotlight #2" for the selected node.</summary>
    public string SpotlightLabel => SelectedSeat == null ? "★ Spotlight" : $"★ Spotlight {SelectedSeat.Label}";

    private bool CanSpotlight => CanFire && SelectedSeat != null;

    /// <summary>Traversal needs an image: the picked one, or the running scene's sprite when it is a picture.</summary>
    private bool CanTraverse => CanFire && (!string.IsNullOrWhiteSpace(_server.EventSettings.TraversalImagePath)
        || Path.GetExtension(_server.ActiveScene?.FirstLayer?.Animation.AnimationPath ?? "").ToLowerInvariant() is ".png" or ".gif" or ".jpg" or ".jpeg" or ".bmp");
    public string? StatusText => _server.EventStatusText;

    /// <summary>File name of the Traversal image, or the fallback.</summary>
    public string TraversalImageName => string.IsNullOrWhiteSpace(_server.EventSettings.TraversalImagePath)
        ? "scene sprite"
        : Path.GetFileName(_server.EventSettings.TraversalImagePath);

    public void SetStorageProvider(IStorageProvider storageProvider) => _storageProvider = storageProvider;

    /// <summary>The room's seats (id, "#2"-style label); keeps the Spotlight target's label current.</summary>
    public void RefreshSeats(IReadOnlyList<SeatChoice> seats)
    {
        _seats = seats;
        if (SelectedSeat != null) SelectedSeat = seats.FirstOrDefault(s => s.Id == SelectedSeat.Id);
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

    /// <summary>The room view's selected node becomes the Spotlight target; no selection disables Spotlight.</summary>
    public void SelectSeat(string? id) => SelectedSeat = _seats.FirstOrDefault(s => s.Id == id);

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

    [RelayCommand(CanExecute = nameof(CanSpotlight))]
    private Task Spotlight() => _server.FireEventAsync(ShowEventType.Spotlight, SelectedSeat?.Id, TargetIds());

    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task Celebration() => _server.FireEventAsync(ShowEventType.Celebration, null, TargetIds());

    [RelayCommand(CanExecute = nameof(CanTraverse))]
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

    /// <summary>"💣 BiBaBo", or "■ Abort BiBaBo" while a game runs.</summary>
    public string BiBaBoLabel => _server.IsBiBaBoRunning ? "■ Abort BiBaBo" : "💣 BiBaBo";

    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task BiBaBo() => _server.ToggleBiBaBoAsync(TargetIds());

    private static readonly int[] BiBaBoTimerChoicesMs = { 60_000, 120_000, 180_000, 300_000, 420_000, 600_000 };

    /// <summary>BiBaBo's countdown as an index into the strip's choices (1, 2, 3, 5, 7, 10 min); saved at once.</summary>
    public int BiBaBoTimerIndex
    {
        get { int i = Array.IndexOf(BiBaBoTimerChoicesMs, _server.EventSettings.BiBaBoTimerMs); return i < 0 ? 3 : i; }
        set
        {
            if (value < 0 || value >= BiBaBoTimerChoicesMs.Length || BiBaBoTimerChoicesMs[value] == _server.EventSettings.BiBaBoTimerMs) return;
            _server.EventSettings.BiBaBoTimerMs = BiBaBoTimerChoicesMs[value];
            _server.SaveEventSettings(_server.EventSettings);
            OnPropertyChanged();
        }
    }

    /// <summary>BiBaBo's riddle count as an index (0 = 3 riddles … 2 = 5); saved at once.</summary>
    public int BiBaBoRiddlesIndex
    {
        get => Math.Clamp(_server.EventSettings.BiBaBoRiddles, BombGameFactory.MinRiddles, BombGameFactory.MaxRiddles) - BombGameFactory.MinRiddles;
        set
        {
            int riddles = BombGameFactory.MinRiddles + value;
            if (value < 0 || riddles > BombGameFactory.MaxRiddles || riddles == _server.EventSettings.BiBaBoRiddles) return;
            _server.EventSettings.BiBaBoRiddles = riddles;
            _server.SaveEventSettings(_server.EventSettings);
            OnPropertyChanged();
        }
    }

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
        TraversalCommand.NotifyCanExecuteChanged();
        _ = _server.PrefetchEventImageAsync();
    }

    [RelayCommand]
    private void ClearTraversalImage()
    {
        _server.EventSettings.TraversalImagePath = string.Empty;
        _server.SaveEventSettings(_server.EventSettings);
        OnPropertyChanged(nameof(TraversalImageName));
        TraversalCommand.NotifyCanExecuteChanged();
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
                BiBaBoCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ServerPlaybackHost.IsBiBaBoRunning):
                OnPropertyChanged(nameof(BiBaBoLabel));
                break;
            case nameof(ServerPlaybackHost.EventStatusText):
                OnPropertyChanged(nameof(StatusText));
                break;
        }
    }

    /// <summary>The window closed: stop listening to the app-lifetime host.</summary>
    public void Detach() => _server.PropertyChanged -= OnServerPropertyChanged;
}

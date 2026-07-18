using CommunityToolkit.Mvvm.ComponentModel;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>Observable wrapper around a <see cref="PlaylistItem"/> for the playlist grid.</summary>
public partial class PlaylistItemRow : ViewModelBase
{
    public PlaylistItem Model { get; }

    public PlaylistItemRow(PlaylistItem model)
    {
        Model = model;
        _name = model.Name;
        _durationText = model.DurationMs?.ToString() ?? string.Empty;
        _snapToLap = model.SnapToLap;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _durationText; // blank = use playlist default
    [ObservableProperty] private bool _snapToLap;

    public string Summary =>
        $"{System.IO.Path.GetFileName(Model.Config.Animation.AnimationPath)} · " +
        $"{Model.Config.Movement.Type} · {Model.Config.DistributionMode}";

    /// <summary>Push edited row fields back into the underlying model.</summary>
    public void CommitToModel()
    {
        Model.Name = Name;
        Model.SnapToLap = SnapToLap;
        Model.DurationMs = int.TryParse(DurationText, out var ms) && ms > 0 ? ms : null;
    }
}

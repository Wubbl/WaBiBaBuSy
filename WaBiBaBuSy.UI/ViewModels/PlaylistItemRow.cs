using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>Observable wrapper around a <see cref="PlaylistItem"/> for the Playlist tab.</summary>
public partial class PlaylistItemRow : ViewModelBase
{
    public PlaylistItem Model { get; }

    public PlaylistItemRow(PlaylistItem model)
    {
        Model = model;
        _name = model.Name;
        _durationText = PlaylistEditing.FormatMsAsSeconds(model.DurationMs);
        _snapToLap = model.SnapToLap;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _snapToLap;

    /// <summary>Dwell in seconds as typed; blank = use the playlist default. Kept as text so an empty box round-trips to null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationSummary))]
    private string _durationText;

    /// <summary>Playlist-wide fallback dwell, pushed in by the owning VM so the row can show the effective value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationSummary))]
    private int _playlistDefaultDurationMs = 30_000;

    /// <summary>True while the running show is on this item.</summary>
    [ObservableProperty] private bool _isPlaying;

    /// <summary>Gallery thumbnail of the item's animation file (null when the gallery has none).</summary>
    [ObservableProperty] private Bitmap? _thumbnail;

    // --- Detail lines -------------------------------------------------------

    /// <summary>Source content: file name (+ extra images), render height and fit mode.</summary>
    public string SourceSummary
    {
        get
        {
            var anim = Model.Config.Animation;
            var paths = anim.GetAllAnimationPaths();
            var primary = paths.Count > 0
                ? System.IO.Path.GetFileName(paths[0])
                : "(no file)";
            var extra = paths.Count > 1 ? $" +{paths.Count - 1} more" : string.Empty;
            return $"{primary}{extra} · {anim.TargetHeight}px · {anim.FitMode}";
        }
    }

    /// <summary>Movement type, speed and the modifier flags that change how it travels.</summary>
    public string MovementSummary
    {
        get
        {
            var m = Model.Config.Movement;
            var parts = new List<string>
            {
                m.Type.ToString(),
                m.SpeedUnit == SpeedUnit.CentimetersPerSecond ? $"{m.SpeedCmPerSecond:0.#} cm/s" : $"{m.SpeedPixelsPerSecond:0} px/s",
            };

            switch (m.Type)
            {
                case MovementType.SineWave:
                    parts.Add($"amp {m.WaveAmplitudePixels:0}px @ {m.WaveFrequencyHz:0.##}Hz");
                    break;
                case MovementType.Circular:
                    parts.Add($"r {m.OrbitRadiusPixels:0}px");
                    break;
                case MovementType.RandomWalk:
                    parts.Add($"seed {m.RandomSeed}, step {m.RandomStepIntervalMs:0}ms");
                    break;
                case MovementType.Linear:
                case MovementType.Bounce:
                    parts.Add($"{m.DirectionAngleDegrees:0}°");
                    break;
            }

            if (m.Reversed) parts.Add("reversed");
            if (m.Endless) parts.Add("endless");
            if (!m.Loop) parts.Add("no loop");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Distribution mode plus how many machines/monitors this item is aimed at.</summary>
    public string TargetSummary
    {
        get
        {
            var ids = Model.Config.SelectedMonitorIds;
            var targets = ids.Count == 0
                ? "all monitors"
                : $"{ids.Count} monitor{(ids.Count == 1 ? "" : "s")}";
            return $"{Model.Config.DistributionMode} · {targets}";
        }
    }

    /// <summary>Background mode, pattern grid and color grading — the visual layers of the item.</summary>
    public string VisualsSummary
    {
        get
        {
            var cfg = Model.Config;
            var parts = new List<string> { $"bg {cfg.Background.Mode}" };

            var pattern = cfg.Animation.Pattern;
            if (pattern == null)
            {
                parts.Add("no pattern");
            }
            else
            {
                var grid = pattern.Sizing == PatternConfig.SizingMode.Fill
                    ? "fill"
                    : $"{pattern.CountX}×{pattern.CountY}";
                var jitter = pattern.RandomOffsetMaxPx > 0 || pattern.RandomRotationMaxDeg > 0
                    ? $", jitter {pattern.RandomOffsetMaxPx:0}px/{pattern.RandomRotationMaxDeg:0}°"
                    : string.Empty;
                parts.Add($"pattern {grid}, gap {pattern.SpacingX:0}×{pattern.SpacingY:0}{jitter}");
            }

            var grading = cfg.Animation.ColorGrading;
            if (grading.Mode == ColorGradingMode.None)
            {
                parts.Add("no grading");
            }
            else
            {
                var detail = grading.Mode is ColorGradingMode.TravelingList
                                          or ColorGradingMode.TravelingRandom
                                          or ColorGradingMode.RandomColors
                                          or ColorGradingMode.CycleColorList
                    ? $" ({grading.ColorList.Count} colors)"
                    : $" @ {grading.CyclesPerSecond:0.##}/s";
                parts.Add($"{grading.Mode}{detail}");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Effective dwell time, resolved against the playlist default.</summary>
    public string DurationSummary
    {
        get
        {
            var explicitMs = ParseDuration();
            var effective = explicitMs ?? PlaylistDefaultDurationMs;
            var seconds = (effective / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
            return explicitMs == null ? $"{seconds}s (default)" : $"{seconds}s";
        }
    }

    /// <summary>Dwell actually used for this item, resolved against the playlist default.</summary>
    public int EffectiveDurationMs => ParseDuration() ?? PlaylistDefaultDurationMs;

    private int? ParseDuration() => PlaylistEditing.ParseSecondsToMs(DurationText);

    /// <summary>Push edited row fields back into the underlying model.</summary>
    public void CommitToModel()
    {
        Model.Name = Name;
        Model.SnapToLap = SnapToLap;
        Model.DurationMs = ParseDuration();
    }

    /// <summary>Replace the item's scene (Save to playlist → Update) and refresh the detail lines.</summary>
    public void ReplaceConfig(CrossScreenConfig config)
    {
        Model.Config = config;
        RefreshSummary();
    }

    /// <summary>Re-raise change notification for the computed detail lines after the model's config changes.</summary>
    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(MovementSummary));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(VisualsSummary));
        OnPropertyChanged(nameof(DurationSummary));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
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
        _transitionChoice = model.Transition?.Kind.ToString() ?? PlaylistDefaultChoice;
        _transitionSecondsText = model.Transition == null ? string.Empty : PlaylistEditing.FormatMsAsSeconds(model.Transition.ClampedMs);
    }

    private const string PlaylistDefaultChoice = "Playlist default";

    /// <summary>Picker entries for how this item comes in.</summary>
    public static IReadOnlyList<string> TransitionChoices { get; } =
        new[] { PlaylistDefaultChoice, nameof(TransitionKind.Cut), nameof(TransitionKind.Fade), nameof(TransitionKind.Wipe) };

    /// <summary>"Playlist default" or a <see cref="TransitionKind"/> name.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Chips))] private string _transitionChoice;

    /// <summary>Transition length in seconds as typed; blank = 1.5 s.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Chips))] private string _transitionSecondsText;

    [ObservableProperty] private string _name;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Chips))] private bool _snapToLap;

    /// <summary>Dwell in seconds as typed; blank = use the playlist default. Kept as text so an empty box round-trips to null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationSummary))]
    private string _durationText;

    /// <summary>Playlist-wide fallback dwell, pushed in by the owning VM so the row can show the effective value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationSummary))]
    private int _playlistDefaultDurationMs = 30_000;

    /// <summary>True while the running show is on this item.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PlayingFrame))] private bool _isPlaying;

    /// <summary>Thumbnail frame: green 2 px while playing.</summary>
    public Avalonia.Thickness PlayingFrame => new(IsPlaying ? 2 : 0);

    /// <summary>Thumbnail of <see cref="ThumbnailSource"/> (null until loaded, or when there is no file).</summary>
    [ObservableProperty] private Bitmap? _thumbnail;

    /// <summary>The file the thumbnail shows: the bottom layer's animation, else any layer's, else the background image.</summary>
    public string? ThumbnailSource
    {
        get
        {
            var cfg = Model.Config;
            foreach (var layer in cfg.Layers)
                if (!string.IsNullOrEmpty(layer.Animation.AnimationPath)) return layer.Animation.AnimationPath;
            return string.IsNullOrEmpty(cfg.Background.ImagePath) ? null : cfg.Background.ImagePath;
        }
    }

    /// <summary>The scene's background color, behind the thumbnail (transparent sprites sit on what the wall shows).</summary>
    public IBrush SceneBackground
    {
        get
        {
            var bg = Model.Config.Background;
            var hex = bg.Mode switch
            {
                BackgroundMode.IconZone => bg.IconCorridorColorHex,
                BackgroundMode.ThreeZone => bg.CorridorColorHex,
                _ => bg.ColorHex,
            };
            return new SolidColorBrush(Controls.ScenePainter.ParseHex(hex));
        }
    }

    // --- Detail lines -------------------------------------------------------

    /// <summary>Colored tags under the name: source, motion + speed, distribution, look, transition override.</summary>
    public IReadOnlyList<SceneChip> Chips
    {
        get
        {
            var cfg = Model.Config;
            var anim = Bottom.Animation;
            var m = Bottom.Movement;
            var chips = new List<SceneChip>();

            var paths = anim.GetAllAnimationPaths();
            if (paths.Count > 0)
                chips.Add(SceneGlyphs.Chip(SceneGlyphs.Source, SceneGlyphs.Media(paths[0]),
                    System.IO.Path.GetFileName(paths[0]) + (paths.Count > 1 ? $" +{paths.Count - 1}" : string.Empty), SourceSummary));
            if (cfg.Layers.Count > 1)
                chips.Add(SceneGlyphs.Chip(SceneGlyphs.Source, "☰", $"{cfg.Layers.Count} layers"));

            var speed = m.Type == MovementType.Static ? string.Empty
                : m.SpeedUnit == SpeedUnit.CentimetersPerSecond ? $" {m.SpeedCmPerSecond:0.#} cm/s" : $" {m.SpeedPixelsPerSecond:0} px/s";
            chips.Add(SceneGlyphs.Chip(SceneGlyphs.Motion, SceneGlyphs.Movement(m.Type), m.Type + speed, MovementSummary));

            chips.Add(SceneGlyphs.Chip(SceneGlyphs.Spread, SceneGlyphs.Distribution(Bottom.Distribution),
                Bottom.Distribution.ToString(), TargetSummary));

            chips.Add(SceneGlyphs.Chip(SceneGlyphs.Look, SceneGlyphs.Background(cfg.Background.Mode),
                cfg.Background.Mode.ToString(), VisualsSummary));
            if (anim.Pattern != null)
                chips.Add(SceneGlyphs.Chip(SceneGlyphs.Look, SceneGlyphs.BgTiled,
                    anim.Pattern.Sizing == PatternConfig.SizingMode.Fill ? "grid fill" : $"grid {anim.Pattern.CountX}×{anim.Pattern.CountY}"));
            if (anim.ColorGrading.Mode != ColorGradingMode.None)
                chips.Add(SceneGlyphs.Chip(SceneGlyphs.Look, "◒", anim.ColorGrading.Mode.ToString()));
            var fx = SpriteEffects.Describe(anim.Effects);
            if (fx.Count > 0)
                chips.Add(SceneGlyphs.Chip(SceneGlyphs.Look, "✦", string.Join(", ", fx)));

            if (Enum.TryParse<TransitionKind>(TransitionChoice, out var kind))
            {
                var secs = kind == TransitionKind.Cut ? string.Empty
                    : " " + (string.IsNullOrWhiteSpace(TransitionSecondsText) ? "1.5" : TransitionSecondsText) + "s";
                chips.Add(SceneGlyphs.Chip(SceneGlyphs.Transition, SceneGlyphs.TransitionGlyph(kind), kind + secs, "Comes in with (overrides the playlist default)"));
            }
            if (SnapToLap)
                chips.Add(SceneGlyphs.Chip(SceneGlyphs.Transition, "⟲", "lap-snap", "Duration rounded up to whole movement laps"));
            return chips;
        }
    }

    /// <summary>The bottom layer the detail lines describe (an empty one for a background-only scene).</summary>
    private SceneLayer Bottom => Model.Config.FirstLayer ?? new SceneLayer();

    /// <summary>Bottom layer's file name (+ extra images), render height and fit mode, then "+ N layers".</summary>
    public string SourceSummary
    {
        get
        {
            var anim = Bottom.Animation;
            var paths = anim.GetAllAnimationPaths();
            var primary = paths.Count > 0
                ? System.IO.Path.GetFileName(paths[0])
                : "(no file)";
            var extra = paths.Count > 1 ? $" +{paths.Count - 1} more" : string.Empty;
            int others = Model.Config.Layers.Count - 1;
            var layers = others > 0 ? $" + {others} layer{(others == 1 ? "" : "s")}" : string.Empty;
            return $"{primary}{extra} · {anim.TargetHeight}px · {anim.FitMode}{layers}";
        }
    }

    /// <summary>Movement type, speed and the modifier flags that change how it travels.</summary>
    public string MovementSummary
    {
        get
        {
            var m = Bottom.Movement;
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
            return $"{Bottom.Distribution} · {targets}";
        }
    }

    /// <summary>Background mode, pattern grid and color grading — the visual layers of the item.</summary>
    public string VisualsSummary
    {
        get
        {
            var cfg = Model.Config;
            var parts = new List<string> { $"bg {cfg.Background.Mode}" };

            var pattern = Bottom.Animation.Pattern;
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

            var grading = Bottom.Animation.ColorGrading;
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

            parts.AddRange(SpriteEffects.Describe(Bottom.Animation.Effects));

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
        Model.Transition = Enum.TryParse<TransitionKind>(TransitionChoice, out var kind)
            ? new SceneTransition { Kind = kind, DurationMs = PlaylistEditing.ParseSecondsToMs(TransitionSecondsText) ?? 1500 }
            : null;
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
        OnPropertyChanged(nameof(Chips));
        OnPropertyChanged(nameof(SceneBackground));
    }
}

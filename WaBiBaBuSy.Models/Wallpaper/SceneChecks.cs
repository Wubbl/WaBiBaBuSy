using System.Globalization;
using System.Text.Json;
using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Editor-side checks on a scene draft: one-line warnings for the Scene editor, the crossing-time
/// readout next to the speed slider, and change detection for the room preview. Pure functions;
/// never used by the players.
/// </summary>
public static class SceneChecks
{
    /// <summary>A crossing faster than this (seconds) is flagged as too fast to follow.</summary>
    public const double MinCrossingSeconds = 5;

    /// <summary>A crossing slower than this (seconds) is flagged as too slow to notice.</summary>
    public const double MaxCrossingSeconds = 600;

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm", ".flv" };

    /// <summary>True when the path has a video file extension.</summary>
    public static bool IsVideo(string? path)
        => !string.IsNullOrEmpty(path) && VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Seconds the sprite needs to travel the full canvas width once at the scene's effective speed
    /// (cm/s resolved with the layout's reference DPI). 0 for Static movement, zero speed or no canvas.
    /// </summary>
    public static double CrossingSeconds(CrossScreenConfig scene, SeatMapLayoutResult? layout)
    {
        if (layout == null || layout.CanvasWidth <= 0 || scene.Movement.Type == MovementType.Static) return 0;
        float speed = PhysicalUnits.EffectiveSpeedPx(scene.Movement, layout.RefPixelsPerCm);
        return speed > 0f ? layout.CanvasWidth / (double)speed : 0;
    }

    /// <summary>"crosses the room in 12.5 s" / "… in 2 min 5 s"; empty when there is nothing to cross.</summary>
    public static string CrossingReadout(CrossScreenConfig scene, SeatMapLayoutResult? layout)
    {
        double s = CrossingSeconds(scene, layout);
        return s <= 0 ? string.Empty : "crosses the room in " + FormatSeconds(s);
    }

    private static string FormatSeconds(double s)
    {
        if (s < 60) return s.ToString("0.#", CultureInfo.InvariantCulture) + " s";
        int total = (int)Math.Round(s);
        return $"{total / 60} min {total % 60} s";
    }

    /// <summary>One-line warnings for the editor's amber chips, in display order. Empty = nothing to fix.</summary>
    public static IReadOnlyList<string> Validate(CrossScreenConfig scene, SeatMapLayoutResult? layout)
    {
        var warnings = new List<string>();
        var anim = scene.Animation;

        if (string.IsNullOrWhiteSpace(anim.AnimationPath))
            warnings.Add("Pick an animation file from the strip");

        if (scene.Background.Mode is BackgroundMode.StretchedImage or BackgroundMode.TiledImage
            && string.IsNullOrWhiteSpace(scene.Background.ImagePath))
            warnings.Add("Pick a background image");

        var grading = anim.ColorGrading?.Mode ?? ColorGradingMode.None;
        if (scene.Movement.Type == MovementType.Linear && scene.Movement.Endless
            && grading is not (ColorGradingMode.TravelingRainbow or ColorGradingMode.TravelingList or ColorGradingMode.TravelingRandom))
            warnings.Add("Endless needs a Traveling color mode");

        if (anim.Pattern != null && IsVideo(anim.AnimationPath))
            warnings.Add("Pattern with a video: every cell decodes the video");

        double crossing = CrossingSeconds(scene, layout);
        if (crossing > 0 && crossing < MinCrossingSeconds)
            warnings.Add("Very fast: " + CrossingReadout(scene, layout));
        else if (crossing > MaxCrossingSeconds)
            warnings.Add("Very slow: " + CrossingReadout(scene, layout));

        return warnings;
    }

    /// <summary>True when both scenes serialize identically (null equals only null).</summary>
    public static bool SameScene(CrossScreenConfig? a, CrossScreenConfig? b)
    {
        if (a == null || b == null) return a == b;
        return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    }
}

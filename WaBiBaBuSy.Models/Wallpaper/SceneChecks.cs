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

    private static readonly HashSet<string> StillAndGifExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".gif", ".png", ".jpg", ".jpeg", ".bmp" };

    /// <summary>True for a file the players can show (image, GIF, video), by extension.</summary>
    public static bool IsMediaFile(string? path)
        => IsVideo(path) || (!string.IsNullOrEmpty(path) && StillAndGifExtensions.Contains(Path.GetExtension(path)));

    /// <summary>Soft limit: more layers than this get a warning (draw cost per node).</summary>
    public const int MaxLayers = 6;

    /// <summary>Soft limit: more pattern layers than this get a warning (hundreds of cells each).</summary>
    public const int MaxPatternLayers = 2;

    /// <summary>
    /// Seconds the layer's sprite needs to travel the full canvas width once at its effective speed
    /// (cm/s resolved with the layout's reference DPI). 0 for Static movement, zero speed or no canvas.
    /// </summary>
    public static double CrossingSeconds(SceneLayer? layer, SeatMapLayoutResult? layout)
    {
        if (layer == null || layout == null || layout.CanvasWidth <= 0 || layer.Movement.Type == MovementType.Static) return 0;
        float speed = PhysicalUnits.EffectiveSpeedPx(layer.Movement, layout.RefPixelsPerCm);
        return speed > 0f ? layout.CanvasWidth / (double)speed : 0;
    }

    /// <summary>"crosses the room in 12.5 s" / "… in 2 min 5 s"; empty when there is nothing to cross.</summary>
    public static string CrossingReadout(SceneLayer? layer, SeatMapLayoutResult? layout)
    {
        double s = CrossingSeconds(layer, layout);
        return s <= 0 ? string.Empty : "crosses the room in " + FormatSeconds(s);
    }

    private static string FormatSeconds(double s)
    {
        if (s < 60) return s.ToString("0.#", CultureInfo.InvariantCulture) + " s";
        int total = (int)Math.Round(s);
        return $"{total / 60} min {total % 60} s";
    }

    /// <summary>
    /// One-line warnings for the editor's amber chips, in display order. Empty = nothing to fix.
    /// With more than one layer, layer warnings carry a "Layer N (name): " prefix (N counts from 1, bottom first).
    /// </summary>
    public static IReadOnlyList<string> Validate(CrossScreenConfig scene, SeatMapLayoutResult? layout)
    {
        var warnings = new List<string>();

        if (scene.Background.Mode is BackgroundMode.StretchedImage or BackgroundMode.TiledImage
            && string.IsNullOrWhiteSpace(scene.Background.ImagePath))
            warnings.Add("Pick a background image");

        if (scene.Layers.Count > MaxLayers)
            warnings.Add($"More than {MaxLayers} layers: every node draws them all each frame");
        if (scene.Layers.Count(l => l.Enabled && l.Animation.Pattern != null) > MaxPatternLayers)
            warnings.Add($"More than {MaxPatternLayers} pattern layers: hundreds of cells each");

        bool prefix = scene.Layers.Count > 1;
        for (int i = 0; i < scene.Layers.Count; i++)
        {
            var layer = scene.Layers[i];
            if (!layer.Enabled) continue;
            string head = prefix ? $"Layer {i + 1} ({layer.Name}): " : string.Empty;
            foreach (var w in ValidateLayer(layer, i, scene.Background.Mode, layout))
                warnings.Add(head + w);
        }
        return warnings;
    }

    private static IEnumerable<string> ValidateLayer(SceneLayer layer, int index, BackgroundMode backgroundMode, SeatMapLayoutResult? layout)
    {
        var anim = layer.Animation;

        if (string.IsNullOrWhiteSpace(anim.AnimationPath))
            yield return "Pick an animation file from the strip";

        if (index > 0 && IsVideo(anim.AnimationPath))
            yield return "Only the bottom layer can play a video — this layer is skipped";

        var grading = anim.ColorGrading?.Mode ?? ColorGradingMode.None;
        if (layer.Movement.Type == MovementType.Linear && layer.Movement.Endless
            && grading is not (ColorGradingMode.TravelingRainbow or ColorGradingMode.TravelingList or ColorGradingMode.TravelingRandom))
            yield return "Endless needs a Traveling color mode";

        var bad = BadGradingColors(anim.ColorGrading);
        if (bad.Count > 0)
            yield return $"Not a color (#RRGGBB): {string.Join(", ", bad)} — drawn white";

        if (anim.Pattern != null && IsVideo(anim.AnimationPath))
            yield return "Pattern with a video: every cell decodes the video";

        if (SpriteEffects.HasAny(anim.Effects))
        {
            if (anim.Pattern != null)
                yield return "Effects are not drawn with a pattern";
            else if (anim.AdditionalAnimationPaths?.Any(p => !string.IsNullOrWhiteSpace(p)) == true)
                yield return "Effects need a single image (no extra images)";
            else if (index == 0 && MovementCalculator.FollowsIconPath(backgroundMode, layer.Movement))
                yield return "Effects are not drawn on an IconZone path";
            if ((anim.Effects!.Entry != BezelEffect.None || anim.Effects.Exit != BezelEffect.None)
                && !SpriteEffects.IsValidBezelColor(anim.Effects.BezelColorHex))
                yield return "Bezel color must be #RRGGBB";
        }

        double crossing = CrossingSeconds(layer, layout);
        if (crossing > 0 && crossing < MinCrossingSeconds)
            yield return "Very fast: " + CrossingReadout(layer, layout);
        else if (crossing > MaxCrossingSeconds)
            yield return "Very slow: " + CrossingReadout(layer, layout);
    }

    /// <summary>The grading colors the active mode uses that do not parse (list for list modes, A/B for Gradient).</summary>
    private static List<string> BadGradingColors(ColorGradingConfig? grading)
    {
        IEnumerable<string?> used = grading?.Mode switch
        {
            ColorGradingMode.Gradient => new[] { grading.GradientA, grading.GradientB },
            ColorGradingMode.RandomColors or ColorGradingMode.CycleColorList or ColorGradingMode.TravelingList or ColorGradingMode.TravelingRandom
                => grading.ColorList ?? new List<string>(),
            _ => Array.Empty<string?>(),
        };
        return used.Where(c => !ColorGrader.IsValidColor(c)).Select(c => $"\"{c}\"").ToList();
    }

    /// <summary>True when both scenes serialize identically (null equals only null).</summary>
    public static bool SameScene(CrossScreenConfig? a, CrossScreenConfig? b)
    {
        if (a == null || b == null) return a == b;
        return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    }
}

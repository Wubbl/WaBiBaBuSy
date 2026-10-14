namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>Rules every recipe shares (spec 2026-10-14 §2.1): seeded numbers, background, loop-snapped timing, the fresh scene.</summary>
public static class SuggestKit
{
    private static readonly string[] Party = { "#FF3B30", "#FFCC00", "#34C759", "#0A84FF", "#BF5AF2" };
    private const double DarkLuma = 0.08, LightLuma = 0.85;
    private const double DarkMedia = 0.25;

    /// <summary>A deterministic number in [0, 1) for (seed, salt) — the only randomness recipes use.</summary>
    public static double Unit(int seed, int salt) => (uint)PatternLayout.Hash3(seed, salt, 0x5EED) / 4294967296.0;

    /// <summary>A deterministic value in [min, max].</summary>
    public static float Range(int seed, int salt, float min, float max) => (float)(min + (max - min) * Unit(seed, salt));

    /// <summary>Five vivid colours (the DVD Bounce preset's list), rotated by the seed.</summary>
    public static IReadOnlyList<string> PartyColors(int seed)
    {
        int start = (int)(Unit(seed, 77) * Party.Length);
        return Enumerable.Range(0, Party.Length).Select(i => Party[(start + i) % Party.Length]).ToList();
    }

    /// <summary>
    /// The first palette colour pushed dark (light or mid media, or <paramref name="forceDark"/>) or light (dark media).
    /// No palette → a near-black or near-white grey.
    /// </summary>
    public static string BackgroundHex(SelectionProfile s, bool forceDark = false)
    {
        bool light = !forceDark && s.MeanLuminance < DarkMedia;
        string seed = s.SharedPalette.Count > 0 ? s.SharedPalette[0] : "#808080";
        var (r, g, b) = Parse(seed);
        double luma = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        if (light)
        {
            double t = luma >= LightLuma ? 0 : (LightLuma - luma) / (1 - luma);
            return Hex(r + (255 - r) * t, g + (255 - g) * t, b + (255 - b) * t);
        }
        if (luma <= 0) return Hex(20, 20, 20);
        double k = Math.Min(1, DarkLuma / luma);
        return Hex(r * k, g * k, b * k);
    }

    /// <summary>
    /// With a GIF in the selection, round a cycle frequency so one cycle lasts a whole number of loops (at least one);
    /// without one, return it unchanged.
    /// </summary>
    public static double SnapCyclesPerSecond(SelectionProfile s, double hz)
    {
        if (s.ShortestLoopMs is not long loop || loop <= 0 || hz <= 0) return hz;
        double periodMs = 1000.0 / hz;
        long loops = Math.Max(1, (long)Math.Round(periodMs / loop));
        return 1000.0 / (loops * loop);
    }

    /// <summary>
    /// A fresh scene for a recipe: the base scene's monitor selection and its first layer's distribution mode, a solid background
    /// from <see cref="BackgroundHex"/>, <paramref name="sprites"/> as the one layer's animation files, scaled to
    /// <paramref name="heightFraction"/> of the screen height. Movement is a default <see cref="MovementConfig"/>.
    /// </summary>
    public static CrossScreenConfig NewScene(CrossScreenConfig baseScene, SelectionProfile s,
        IReadOnlyList<MediaProfile> sprites, double heightFraction) => new()
    {
        SelectedMonitorIds = new List<string>(baseScene.SelectedMonitorIds),
        Background = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = BackgroundHex(s) },
        Layers =
        {
            new SceneLayer
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(sprites[0].Path),
                Distribution = baseScene.FirstLayer?.Distribution ?? AnimationDistributionMode.Sequential,
                Animation = new AnimationLayerConfig
                {
                    AnimationPath = sprites[0].Path,
                    AdditionalAnimationPaths = sprites.Skip(1).Select(f => f.Path).ToList(),
                    FitMode = ContentFitMode.TargetHeight,
                    SizeUnit = SizeUnit.Pixels,
                    TargetHeight = (int)Math.Round(heightFraction * s.ScreenHeight),
                    SpeedMultiplier = 1.0,
                },
                Movement = new MovementConfig(),
            }
        },
    };

    private static (int r, int g, int b) Parse(string hex)
    {
        try
        {
            return (Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));
        }
        catch (Exception e) when (e is FormatException or ArgumentException) { return (128, 128, 128); }
    }

    private static string Hex(double r, double g, double b) =>
        $"#{Clamp(r):X2}{Clamp(g):X2}{Clamp(b):X2}";

    private static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, 0, 255));
}

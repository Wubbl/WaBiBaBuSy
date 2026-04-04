namespace WaBiBaBuSy.Core.Services.Desktop;

/// <summary>
/// Generates harmonious color palettes for zone coloring (up to 16 colors).
/// Uses HSL with evenly-spaced hues at a fixed dark saturation/lightness
/// suitable for desktop background zones.
/// </summary>
public static class PaletteGenerator
{
    // Named presets — hue starting points that produce well-known themes
    private static readonly Dictionary<string, float> PresetHues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ocean"]       = 200f,
        ["Forest"]      = 120f,
        ["Ember"]       = 20f,
        ["Monochrome"]  = 0f,    // all grays (saturation will be near 0)
        ["Twilight"]    = 270f,
    };

    /// <summary>
    /// Generates <paramref name="count"/> harmonious dark hex colors (e.g. "#1A3A5C").
    /// </summary>
    /// <param name="count">Number of colors (1–16).</param>
    /// <param name="seed">Random seed for hue offset. -1 = random each call.</param>
    public static List<string> GenerateHarmonious(int count, int seed = -1)
    {
        count = Math.Clamp(count, 1, 16);
        var rng = seed >= 0 ? new Random(seed) : new Random();
        float hueStart = (float)(rng.NextDouble() * 360.0);
        return GenerateFromHue(count, hueStart);
    }

    /// <summary>
    /// Returns a palette based on a named preset theme.
    /// </summary>
    public static List<string> GetPreset(string name, int count = 8)
    {
        count = Math.Clamp(count, 1, 16);
        float hue = PresetHues.TryGetValue(name, out float h) ? h : 0f;
        return GenerateFromHue(count, hue);
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private static List<string> GenerateFromHue(int count, float hueStart)
    {
        var colors = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            float hue = (hueStart + i * 360f / count) % 360f;
            float sat = 0.50f;
            float lit = 0.25f;
            var (r, g, b) = HslToRgb(hue, sat, lit);
            colors.Add($"#{r:X2}{g:X2}{b:X2}");
        }
        return colors;
    }

    private static (byte r, byte g, byte b) HslToRgb(float h, float s, float l)
    {
        float c = (1f - MathF.Abs(2f * l - 1f)) * s;
        float x = c * (1f - MathF.Abs((h / 60f) % 2f - 1f));
        float m = l - c / 2f;

        float r1, g1, b1;
        if      (h < 60f)  { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120f) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180f) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240f) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300f) { r1 = x; g1 = 0; b1 = c; }
        else               { r1 = c; g1 = 0; b1 = x; }

        return (
            (byte)Math.Clamp((r1 + m) * 255f, 0, 255),
            (byte)Math.Clamp((g1 + m) * 255f, 0, 255),
            (byte)Math.Clamp((b1 + m) * 255f, 0, 255)
        );
    }
}

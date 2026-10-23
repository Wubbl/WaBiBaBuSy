using SkiaSharp;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Client.Linux.Rendering;

/// <summary>
/// Skia counterpart of the Windows player's GDI+ <c>TextSprite</c>: bold text on transparent, rendered once per text
/// and scaled into the box the event math gives (so seats agree on positions even if their fonts differ slightly).
/// </summary>
public static class TextSprite
{
    /// <summary>Rendered font size; the image is scaled into the event's box when drawn.</summary>
    public const float FontPx = 160f;
    private const int MaxWidthPx = 8192;

    // Segoe UI is the Windows font; these are the usual Linux sans faces, first match wins.
    private static readonly Lazy<SKTypeface> s_typeface = new(() =>
    {
        foreach (var family in new[] { "Segoe UI", "Noto Sans", "DejaVu Sans", "Liberation Sans", "Cantarell", "Ubuntu" })
        {
            var face = SKTypeface.FromFamilyName(family, SKFontStyle.Bold);
            if (face != null && string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase)) return face;
            face?.Dispose();
        }
        return SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
    });

    /// <summary>The text in <paramref name="colorHex"/> on transparent, anti-aliased. Caller disposes.</summary>
    public static SKImage Render(string text, string colorHex)
    {
        using var font = new SKFont(s_typeface.Value, FontPx) { Edging = SKFontEdging.Antialias, Subpixel = true };
        float width = font.MeasureText(text);
        var metrics = font.Metrics;
        float height = metrics.Descent - metrics.Ascent;

        int w = Math.Clamp((int)Math.Ceiling(width), 1, MaxWidthPx);
        int h = Math.Max(1, (int)Math.Ceiling(height));
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        // Wider than MaxWidthPx: squeeze horizontally so the whole text fits (drawn into the event box anyway).
        if (width > w) canvas.Scale(w / width, 1f);
        var (r, g, b) = ShowEventMath.ParseColor(colorHex);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(r, g, b) };
        canvas.DrawText(text, 0, -metrics.Ascent, SKTextAlign.Left, font, paint);
        return surface.Snapshot();
    }
}

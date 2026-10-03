// WaBiBaBuSy.WallpaperEngine/Show/TextSprite.cs
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.WallpaperEngine.Show;

/// <summary>
/// Announce text as a bitmap (the player has no DirectWrite). The server measures the aspect with the same routine
/// and sends it in the event; every node draws its bitmap into the box aspect × height, so positions agree even if a
/// node's font metrics differ slightly.
/// </summary>
public static class TextSprite
{
    /// <summary>Rendered font size; the bitmap is scaled into the event's box when drawn.</summary>
    public const float FontPx = 160f;
    private const int MaxWidthPx = 8192;
    private const string FontFamilyName = "Segoe UI";

    private static Font CreateFont() => new(FontFamilyName, FontPx, FontStyle.Bold, GraphicsUnit.Pixel);

    /// <summary>Width ÷ height of the rendered text (0.2 … 20); 1 for empty text.</summary>
    public static float MeasureAspect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 1f;
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        using var font = CreateFont();
        var size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
        return size.Height > 0 ? Math.Clamp(size.Width / size.Height, 0.2f, 20f) : 1f;
    }

    /// <summary>The text in <paramref name="colorHex"/> on transparent, anti-aliased. Caller disposes.</summary>
    public static Bitmap Render(string text, string colorHex)
    {
        using var font = CreateFont();
        SizeF size;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
            size = pg.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);

        int w = Math.Clamp((int)Math.Ceiling(size.Width), 1, MaxWidthPx);
        int h = Math.Max(1, (int)Math.Ceiling(size.Height));
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var (r, gr, b) = ShowEventMath.ParseColor(colorHex);
        using var brush = new SolidBrush(Color.FromArgb(255, r, gr, b));
        // Wider than MaxWidthPx: squeeze horizontally so the whole text fits (drawn into the event box anyway).
        if (size.Width > w) g.ScaleTransform(w / size.Width, 1f);
        g.DrawString(text, font, brush, PointF.Empty, StringFormat.GenericTypographic);
        return bmp;
    }
}

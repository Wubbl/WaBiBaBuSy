using System;
using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Pure virtual-canvas → local-screen coordinate mapping shared by the D2D player and the live
/// preview. Handles mirrored nodes (path runs against the screen's +x) and Ring wrap (sprite
/// straddling the seam is drawn twice). Mirroring affects coordinates only — never bitmap content.
/// </summary>
public static class NodeMapping
{
    /// <summary>Local X of a sprite's left edge for a given virtual X (top-left) and sprite width.</summary>
    public static float ToLocalX(float virtualX, float spriteWidth, NodeLayout layout)
    {
        float rel = virtualX - layout.OffsetX;
        return layout.Mirrored ? layout.Width - rel - spriteWidth : rel;
    }

    /// <summary>Local Y for a virtual Y (top-left).</summary>
    public static float ToLocalY(float virtualY, NodeLayout layout) => virtualY - layout.OffsetY;

    /// <summary>Inverse of <see cref="ToLocalX"/> — used by the preview and for debug readouts.</summary>
    public static float ToVirtualX(float localX, float spriteWidth, NodeLayout layout)
    {
        float rel = layout.Mirrored ? layout.Width - localX - spriteWidth : localX;
        return rel + layout.OffsetX;
    }

    /// <summary>
    /// Virtual X positions at which a sprite must be drawn so a Ring seam is seamless: the
    /// position itself, plus a second copy shifted by ±canvasWidth when the sprite straddles
    /// the seam. Non-wrapping canvases return exactly one position.
    /// </summary>
    public static int WrapCopies(float virtualX, float spriteWidth, NodeLayout layout, Span<float> output)
    {
        if (output.Length == 0) return 0;
        if (!layout.Wraps || layout.CanvasWidth <= 0)
        {
            output[0] = virtualX;
            return 1;
        }

        float p = layout.CanvasWidth;
        float x = PositiveModulo(virtualX, p);
        output[0] = x;
        int count = 1;
        if (output.Length > 1 && x + spriteWidth > p)
        {
            output[1] = x - p;    // the part that already re-entered at the start of the ring
            count = 2;
        }
        return count;
    }

    /// <summary>Mirror a pattern cell's local X inside the node's slice (cells are computed unmirrored).</summary>
    public static float MirrorLocalX(float localX, float cellWidth, NodeLayout layout)
        => layout.Mirrored ? layout.Width - localX - cellWidth : localX;

    /// <summary>True when any part of [localX, localX+width) is inside [0, layout.Width).</summary>
    public static bool IsVisible(float localX, float width, NodeLayout layout)
        => localX + width > 0f && localX < layout.Width;

    /// <summary>
    /// The part of a <see cref="BackgroundMode.Panorama"/> image this node shows: <c>Src</c> in image pixels, drawn into
    /// <c>Dst</c> in the node's local pixels (unmirrored). The image is placed over the canvas width and the tallest screen
    /// of the node's row (<see cref="NodeLayout.RowHeight"/>) per <paramref name="fit"/>; a shorter screen shows the
    /// vertically centred part. Without a row height (older server) the band is the canvas height. No layout → the whole
    /// image over the whole screen (<c>Dst</c> null). A screen the image does not reach gets an empty <c>Src</c>.
    /// Mirrored nodes flip the slice and <c>Dst</c> horizontally (the caller does that) so seams still meet.
    /// </summary>
    public static ((int X, int Y, int Width, int Height) Src, (int X, int Y, int Width, int Height)? Dst) PanoramaSource(
        NodeLayout? layout, int imageWidth, int imageHeight, PanoramaFit fit = PanoramaFit.FitHeight)
    {
        if (layout == null || layout.CanvasWidth <= 0 || layout.CanvasHeight <= 0 || layout.Width <= 0 || layout.Height <= 0
            || imageWidth <= 0 || imageHeight <= 0)
            return ((0, 0, imageWidth, imageHeight), null);
        int bandH = layout.RowHeight > 0 ? layout.RowHeight : layout.CanvasHeight;
        int top = layout.RowHeight > 0 ? (layout.RowHeight - layout.Height) / 2 : layout.OffsetY;
        // Screen pixels per image pixel.
        double fx = layout.CanvasWidth / (double)imageWidth, fy = bandH / (double)imageHeight;
        var (sx, sy) = fit switch
        {
            PanoramaFit.Stretch => (fx, fy),
            PanoramaFit.Fill => (Math.Max(fx, fy), Math.Max(fx, fy)),
            PanoramaFit.Fit => (Math.Min(fx, fy), Math.Min(fx, fy)),
            _ => (fy, fy),
        };
        double imgLeft = (layout.CanvasWidth - imageWidth * sx) / 2, imgTop = (bandH - imageHeight * sy) / 2;
        // The part of this screen the image covers, in band coordinates, rounded to whole screen pixels.
        int x0 = (int)Math.Round(Math.Max(layout.OffsetX, imgLeft));
        int x1 = (int)Math.Round(Math.Min(layout.OffsetX + layout.Width, imgLeft + imageWidth * sx));
        int y0 = (int)Math.Round(Math.Max(top, imgTop));
        int y1 = (int)Math.Round(Math.Min(top + layout.Height, imgTop + imageHeight * sy));
        if (x1 <= x0 || y1 <= y0) return ((0, 0, 0, 0), (0, 0, 0, 0));
        int u0 = Math.Clamp((int)Math.Round((x0 - imgLeft) / sx), 0, imageWidth - 1);
        int v0 = Math.Clamp((int)Math.Round((y0 - imgTop) / sy), 0, imageHeight - 1);
        int u1 = Math.Clamp((int)Math.Round((x1 - imgLeft) / sx), u0 + 1, imageWidth);
        int v1 = Math.Clamp((int)Math.Round((y1 - imgTop) / sy), v0 + 1, imageHeight);
        return ((u0, v0, u1 - u0, v1 - v0), (x0 - layout.OffsetX, y0 - top, x1 - x0, y1 - y0));
    }

    public static float PositiveModulo(float value, float period)
    {
        if (period <= 0f) return value;
        float m = value % period;
        return m < 0f ? m + period : m;
    }
}

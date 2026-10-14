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
    /// The part of a <see cref="BackgroundMode.Panorama"/> image (in image pixels) this node shows: the image is
    /// stretched over the whole canvas and the node's slice is cut out. No layout → the whole image. Clamped to
    /// the image. Mirrored nodes flip the slice horizontally (the caller does that) so seams still meet.
    /// </summary>
    public static (int X, int Y, int Width, int Height) PanoramaSource(NodeLayout? layout, int imageWidth, int imageHeight)
    {
        if (layout == null || layout.CanvasWidth <= 0 || layout.CanvasHeight <= 0 || layout.Width <= 0 || layout.Height <= 0)
            return (0, 0, imageWidth, imageHeight);
        double kx = imageWidth / (double)layout.CanvasWidth, ky = imageHeight / (double)layout.CanvasHeight;
        int x0 = Math.Clamp((int)Math.Round(layout.OffsetX * kx), 0, imageWidth);
        int y0 = Math.Clamp((int)Math.Round(layout.OffsetY * ky), 0, imageHeight);
        int x1 = Math.Clamp((int)Math.Round((layout.OffsetX + layout.Width) * kx), x0, imageWidth);
        int y1 = Math.Clamp((int)Math.Round((layout.OffsetY + layout.Height) * ky), y0, imageHeight);
        return x1 > x0 && y1 > y0 ? (x0, y0, x1 - x0, y1 - y0) : (0, 0, imageWidth, imageHeight);
    }

    public static float PositiveModulo(float value, float period)
    {
        if (period <= 0f) return value;
        float m = value % period;
        return m < 0f ? m + period : m;
    }
}

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

    public static float PositiveModulo(float value, float period)
    {
        if (period <= 0f) return value;
        float m = value % period;
        return m < 0f ? m + period : m;
    }
}

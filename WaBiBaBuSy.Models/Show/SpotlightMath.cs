using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Show;

/// <summary>What one node draws for a Spotlight, in node-local canvas units. Default = nothing.</summary>
/// <param name="ChevronDirection">+1 = points right on this screen, -1 = left, 0 = no chevron.</param>
public readonly record struct SpotlightVisual(
    bool IsTarget, float DimOpacity, float BorderOpacity, float BorderWidth,
    int ChevronDirection, float ChevronCenterX, float ChevronCenterY, float ChevronSize, float ChevronOpacity);

/// <summary>
/// Spotlight (design §3.2): the target seat's border pulses in the event color; every other seat dims to 40 % and
/// shows a chevron pointing along the traversal toward the target.
/// </summary>
public static class SpotlightMath
{
    public const float BorderWidth = 16f;
    public const float DimMax = 0.6f;
    public const double PulseHz = 1.5;
    public const float ChevronSlide = 40f;
    public const double ChevronHz = 1.0;
    public const float ChevronHeightFraction = 0.18f;

    /// <param name="sharedCanvas">Sequential (one canvas across the room); false in Simultaneous mode.</param>
    public static SpotlightVisual Visual(ShowEvent evt, NodeLayout node, bool sharedCanvas, long localMs)
    {
        if (evt.TargetOrder < 0) return default;
        float env = ShowEventMath.Envelope(localMs, evt.DurationMs);
        if (env <= 0f) return default;

        if (node.Order == evt.TargetOrder)
        {
            float pulse = 0.6f + 0.4f * (float)ShowEventMath.Sin(localMs, PulseHz);
            return new SpotlightVisual(true, 0f, env * pulse, BorderWidth, 0, 0f, 0f, 0f, 0f);
        }

        int dir = ChevronDirection(evt, node, sharedCanvas);
        float slide = ChevronSlide * (float)ShowEventMath.Sin(localMs, ChevronHz) * dir;
        return new SpotlightVisual(false, DimMax * env, 0f, 0f, dir,
            node.Width / 2f + slide, node.Height / 2f, ChevronHeightFraction * node.Height, env);
    }

    /// <summary>
    /// Screen direction toward the target: along the shared canvas (the shorter way around on a Ring), or by
    /// traversal order without a shared canvas; negated on mirrored nodes.
    /// </summary>
    public static int ChevronDirection(ShowEvent evt, NodeLayout node, bool sharedCanvas)
    {
        float delta;
        if (sharedCanvas && node.CanvasWidth > 0)
        {
            delta = evt.TargetCenterX - (node.OffsetX + node.Width / 2f);
            if (node.Wraps)
            {
                float w = node.CanvasWidth;
                delta = NodeMapping.PositiveModulo(delta + w / 2f, w) - w / 2f;
            }
        }
        else
        {
            delta = evt.TargetOrder - node.Order;
        }
        int dir = delta >= 0f ? 1 : -1;
        return node.Mirrored ? -dir : dir;
    }
}

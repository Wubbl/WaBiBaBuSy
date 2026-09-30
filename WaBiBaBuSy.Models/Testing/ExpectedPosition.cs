using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Testing;

public enum MarkerVisibility { Visible, Partial, OffScreen, NotStarted }

/// <summary>Where the marker must be on one node, device px, top-left of the node's surface.</summary>
public sealed class MarkerExpectation
{
    public MarkerVisibility Visibility { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float CenterX => X + Width / 2f;
    public float CenterY => Y + Height / 2f;
}

/// <summary>
/// Server-side replica of the player's single-sprite placement: <see cref="MovementCalculator"/> in
/// canvas units → <see cref="NodeMapping"/> → × <see cref="NodeLayout.Scale"/>.
/// </summary>
public static class ExpectedPosition
{
    public static MarkerExpectation Compute(MovementConfig movement, long effectiveElapsedMs, float spriteWidth, float spriteHeight, NodeLayout layout)
    {
        if (!SyncTiming.ShouldDrawAnimation(effectiveElapsedMs))
            return new MarkerExpectation { Visibility = MarkerVisibility.NotStarted };

        var (vx, vy) = MovementCalculator.Calculate(
            movement, effectiveElapsedMs,
            (int)MathF.Round(spriteWidth), (int)MathF.Round(spriteHeight),
            layout.CanvasWidth, layout.CanvasHeight,
            tileAlignStepX: 0f, canvasWraps: layout.Wraps);

        float scale = layout.Scale > 0f ? layout.Scale : 1f;
        float localY = NodeMapping.ToLocalY(vy, layout);
        bool yInside = localY >= 0 && localY + spriteHeight <= layout.Height;

        Span<float> copies = stackalloc float[2];
        int count = NodeMapping.WrapCopies(vx, spriteWidth, layout, copies);
        MarkerExpectation? partial = null;
        for (int i = 0; i < count; i++)
        {
            float localX = NodeMapping.ToLocalX(copies[i], spriteWidth, layout);
            bool fullyInside = localX >= 0 && localX + spriteWidth <= layout.Width && yInside;
            bool overlaps = localX + spriteWidth > 0 && localX < layout.Width && localY + spriteHeight > 0 && localY < layout.Height;
            if (fullyInside) return Make(MarkerVisibility.Visible, localX, localY, spriteWidth, spriteHeight, scale);
            if (overlaps) partial ??= Make(MarkerVisibility.Partial, localX, localY, spriteWidth, spriteHeight, scale);
        }
        return partial ?? new MarkerExpectation { Visibility = MarkerVisibility.OffScreen };
    }

    /// <summary>Layout a Simultaneous-mode player uses: its own monitor is the whole canvas.</summary>
    public static NodeLayout PerMonitorLayout(string nodeId, int width, int height) => new()
    {
        Id = nodeId, OffsetX = 0, OffsetY = 0, Width = width, Height = height,
        CanvasWidth = width, CanvasHeight = height, Scale = 1f,
    };

    private static MarkerExpectation Make(MarkerVisibility v, float x, float y, float w, float h, float scale) => new()
    {
        Visibility = v, X = x * scale, Y = y * scale, Width = w * scale, Height = h * scale,
    };
}

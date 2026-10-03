using Vortice.Direct2D1;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Player.D2D;

/// <summary>Sprite effects (roadmap 2.4): trail, pulse, spin and bezel crossing effects. Math in <see cref="SpriteEffects"/>.</summary>
partial class Program
{
    /// <summary>Pose input of this node for the pure effect math (same arguments as UpdateAnimationPosition's MovementCalculator call).</summary>
    private static SpritePoseInput PoseInput() => new(
        _movementConfig!, _animWidth, _animHeight, _virtualCanvasWidth, _virtualCanvasHeight,
        _isTravelingMode ? _tileAlignStepX : 0f, _layout);

    /// <summary>ThreeZone background: keep a sprite top (canvas units) inside the corridor (device px → canvas units).</summary>
    private static float ClampToCorridor(float y)
    {
        if (!_hasCorridorConstraint || _corridorHeightPx <= 0) return y;
        float minY = _corridorTopPx / Scale;
        float maxY = (_corridorTopPx + _corridorHeightPx) / Scale - _animHeight;
        if (maxY < minY) maxY = minY;
        return Math.Clamp(y, minY, maxY);
    }

    /// <summary>
    /// Ghosts behind the sprite: ghost k is the sprite k × spacing ago — position, facing, pulse, spin and
    /// (optionally) color of that moment — at falling opacity and size. Farthest first, so nearer ghosts cover
    /// farther ones; ghosts that already crossed a bezel are drawn by the node they are on.
    /// </summary>
    private static void DrawTrail(SpriteEffectsConfig fx, ID2D1Bitmap bmp, long elapsedMs, bool gradingActive)
    {
        int count = SpriteEffects.TrailCount(fx.Trail);
        if (count == 0) return;
        var input = PoseInput();
        var grading = _animationConfig!.ColorGrading;
        bool tintGhosts = gradingActive && fx.Trail!.UseGradingColor && grading != null;
        Span<float> copies = stackalloc float[2];
        for (int k = count; k >= 1; k--)
        {
            var ghost = SpriteEffects.Ghost(fx.Trail!, k);
            long t = elapsedMs - ghost.OffsetMs;
            if (t < 0 || ghost.Opacity <= 0.005f) continue;    // before the start the sprite was not drawn
            var (vx, vy) = SpriteEffects.Pose(input, t);
            float ly = ClampToCorridor(NodeMapping.ToLocalY(vy, _layout));
            bool flip = _animationConfig.FaceTravelDirection && SpriteEffects.FacingLeftAt(input, t, _lastFlipX);
            float rot = SpriteEffects.SpinDegrees(fx.SpinDegPerSecond, t);
            float scale = SpriteEffects.PulseScale(fx.Pulse, t) * ghost.Scale;
            if (tintGhosts) SetColorMatrixOnEffect(ColorGrader.Compute(grading!, t));
            int n = NodeMapping.WrapCopies(vx, _animWidth, _layout, copies);
            for (int c = 0; c < n; c++)
            {
                float lx = NodeMapping.ToLocalX(copies[c], _animWidth, _layout);
                if (!NodeMapping.IsVisible(lx, _animWidth, _layout)) continue;
                DrawCellWithOptionalGrading(bmp, lx, ly, _animWidth, _animHeight, rot, gradingActive, ghost.Opacity, flip, scale);
            }
        }
        if (tintGhosts) SetColorMatrixOnEffect(ColorGrader.Compute(grading!, elapsedMs));   // the sprite's own color
    }
}

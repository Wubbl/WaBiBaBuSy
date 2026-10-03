using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
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

    // Bezel effect brushes: created on first use, recreated when the color changes, disposed with the D2D resources.
    private static string? _bezelBrushColor;
    private static ID2D1SolidColorBrush? _bezelSolidBrush;
    private static ID2D1LinearGradientBrush? _bezelGlowBrush;

    private static void EnsureBezelBrushes(string colorHex)
    {
        if (_bezelSolidBrush != null && _bezelBrushColor == colorHex) return;
        DisposeEffectBrushes();
        var (r, g, b) = SpriteEffects.ParseBezelColor(colorHex);   // never throws (white when invalid)
        var color = new Color4(r / 255f, g / 255f, b / 255f, 1f);
        var clear = new Color4(color.R, color.G, color.B, 0f);
        _bezelSolidBrush = _d2dContext!.CreateSolidColorBrush(color);
        var stops = new GradientStop[2];
        stops[0] = new GradientStop(0f, ref color);   // bright at the crossing edge
        stops[1] = new GradientStop(1f, ref clear);   // fades into the screen
        using var stopCol = _d2dContext.CreateGradientStopCollection(stops, Gamma.StandardRgb, ExtendMode.Clamp);
        var start = Vector2.Zero; var end = Vector2.UnitX;
        _bezelGlowBrush = _d2dContext.CreateLinearGradientBrush(new LinearGradientBrushProperties(ref start, ref end), stopCol);
        _bezelBrushColor = colorHex;
    }

    /// <summary>Release the bezel brushes (reload / device loss / exit).</summary>
    private static void DisposeEffectBrushes()
    {
        _bezelGlowBrush?.Dispose(); _bezelGlowBrush = null;
        _bezelSolidBrush?.Dispose(); _bezelSolidBrush = null;
        _bezelBrushColor = null;
    }

    /// <summary>
    /// Glow / Flash / Ripple where the sprite crossed this node's edge within the last BezelDurationMs. The crossing
    /// comes from a pure scan of the sprite's pose, so exact frames and every node agree on it. Drawn on top of the
    /// sprite, in canvas units scaled to device pixels.
    /// </summary>
    private static void DrawBezelEffect(SpriteEffectsConfig fx, long elapsedMs)
    {
        if (_d2dContext == null) return;
        var ev = SpriteEffects.FindBezelEvent(PoseInput(), fx, elapsedMs);
        if (ev == null) return;
        var v = SpriteEffects.Visual(ev.Value, _layout);
        if (v.Opacity <= 0.005f || v.Effect == BezelEffect.None) return;

        EnsureBezelBrushes(fx.BezelColorHex);
        var prior = _d2dContext.Transform;
        _d2dContext.Transform = Matrix3x2.CreateScale(Scale) * prior;   // canvas units → device px
        try
        {
            switch (v.Effect)
            {
                case BezelEffect.Glow:
                    _bezelGlowBrush!.StartPoint = new Vector2(v.EdgeOnRight ? v.X + v.Width : v.X, 0f);
                    _bezelGlowBrush.EndPoint = new Vector2(v.EdgeOnRight ? v.X : v.X + v.Width, 0f);
                    _bezelGlowBrush.Opacity = v.Opacity;
                    _d2dContext.FillRectangle(new System.Drawing.RectangleF(v.X, v.Y, v.Width, v.Height), _bezelGlowBrush);
                    break;
                case BezelEffect.Flash:
                    _bezelSolidBrush!.Opacity = v.Opacity;
                    _d2dContext.FillRectangle(new System.Drawing.RectangleF(v.X, v.Y, v.Width, v.Height), _bezelSolidBrush);
                    break;
                case BezelEffect.Ripple:
                    _bezelSolidBrush!.Opacity = v.Opacity;
                    _d2dContext.DrawEllipse(new Ellipse(new Vector2(v.X, v.Y), v.Radius, v.Radius), _bezelSolidBrush, v.StrokeWidth);
                    break;
            }
        }
        finally
        {
            _d2dContext.Transform = prior;
        }
    }
}

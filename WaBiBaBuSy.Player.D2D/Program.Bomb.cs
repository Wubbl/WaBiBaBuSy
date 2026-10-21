// WaBiBaBuSy.Player.D2D/Program.Bomb.cs
using System.Drawing;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Show;

namespace WaBiBaBuSy.Player.D2D;

/// <summary>
/// BiBaBo (design 2026-10-21-bibabo-bomb-game-design.md): the Bomb event (blackout, roulette, bomb, cables, timer,
/// riddle cards) and its BombOutcome (BOOM / DEFUSED / RECYCLED). Everything from BombMath and the event; drawn in canvas
/// units under the show-event transform, inside DrawShowEvents. Text is TextSprite bitmaps decoded per event ("id#slot").
/// </summary>
partial class Program
{
    private static readonly Color4 BombTimerRed = new(1f, 0.09f, 0.27f, 1f);      // #FF1744
    private static readonly Color4 BombTimerGreen = new(0f, 0.9f, 0.46f, 1f);     // #00E676
    private static readonly Color4 BombCard = new(0.1f, 0.1f, 0.1f, 1f);          // #1A1A1A
    private static readonly Color4 BombGrey = new(0.62f, 0.62f, 0.62f, 1f);       // #9E9E9E
    private static readonly Color4 BombOrange = new(1f, 0.43f, 0f, 1f);           // #FF6D00
    private static readonly string[] FirePalette = { "#FF6D00", "#FFAB00", "#FF3D00", "#FFD600", "#DD2C00", "#FFFFFF" };
    private const float CableWidth = 10f, StrandWidth = 5f;

    private static (int I, float Shift)[] _bombSegments = new (int, float)[1024];   // render thread only

    /// <summary>The sprite ids of one Bomb / BombOutcome event ("id#slot"), built once — no string per frame.</summary>
    private sealed class BombSpriteIds
    {
        public BombSpriteIds(string id, int cables)
        {
            Logo = id + "#logo"; Rule0 = id + "#rule0"; Rule1 = id + "#rule1"; Det = id + "#det"; Gnd = id + "#gnd"; Text = id + "#text";
            Formulas = Enumerable.Range(0, cables).Select(i => $"{id}#f{i}").ToArray();
        }
        public string Logo { get; }
        public string Rule0 { get; }
        public string Rule1 { get; }
        public string Det { get; }
        public string Gnd { get; }
        public string Text { get; }
        public string[] Formulas { get; }
    }

    private static readonly Dictionary<string, BombSpriteIds> _bombIds = new();   // render thread only

    private static BombSpriteIds IdsOf(ShowEvent evt)
    {
        if (_bombIds.TryGetValue(evt.EventId, out var ids)) return ids;
        if (_bombIds.Count > 16) _bombIds.Clear();   // a few games per party: drop the old ones now and then
        ids = new BombSpriteIds(evt.EventId, evt.Bomb?.Cables.Count ?? 0);
        _bombIds[evt.EventId] = ids;
        return ids;
    }
    private static ID2D1StrokeStyle? _roundStroke;

    /// <summary>stdin thread: the sprites a Bomb / BombOutcome event needs (logo, rules, formulas, labels, outcome text).</summary>
    private static void StartBombSpriteDecodes(ShowEvent evt)
    {
        var g = evt.Bomb;
        if (g == null) return;
        string id = evt.EventId;
        if (!string.IsNullOrEmpty(evt.ImagePath))
        {
            var path = evt.ImagePath!;
            StartSpriteDecode(id + "#logo", id, () => DecodeImageFrames(path));
        }
        if (evt.Type == ShowEventType.Bomb)
        {
            StartSpriteDecode(id + "#rule0", id, () => TextFrames(BombRiddles.MathRule, "#BDBDBD"));
            StartSpriteDecode(id + "#rule1", id, () => TextFrames(BombRiddles.TangleRule, "#BDBDBD"));
            StartSpriteDecode(id + "#det", id, () => TextFrames("DETONATOR", "#FF1744"));
            StartSpriteDecode(id + "#gnd", id, () => TextFrames("GROUND", "#9E9E9E"));
            for (int i = 0; i < g.Cables.Count; i++)
                if (g.Cables[i].Riddle is { Kind: RiddleKind.Math, Formula: { } formula })
                    StartSpriteDecode($"{id}#f{i}", id, () => TextFrames(formula, "#FFFFFF"));
            return;
        }
        string? text = g.Result switch
        {
            BombResult.WrongCable or BombResult.Timeout => "BOOM!",
            BombResult.Defused => $"DEFUSED — {BombMath.FormatTimer(g.RemainingMs)} left",
            BombResult.Recycled => "RECYCLED ♻",
            _ => null,
        };
        string color = g.Result is BombResult.Defused or BombResult.Recycled ? "#00E676" : "#FF6D00";
        if (text != null) StartSpriteDecode(id + "#text", id, () => TextFrames(text, color));
    }

    private static EventSpriteFrames TextFrames(string text, string color) =>
        new() { Frames = new List<Bitmap> { TextSprite.Render(text, color) }, DelaysMs = new List<int> { 100 } };

    private static ShowEvent? FindBombOutcome(int count, string gameId)
    {
        for (int i = 0; i < count; i++)
        {
            var e = _showEventScratch[i];
            if (e.Type == ShowEventType.BombOutcome && e.Bomb?.GameId == gameId) return e;
        }
        return null;
    }

    // ── Bomb event ──────────────────────────────────────────────────────────

    private static void DrawBomb(ShowEvent evt, long local, int count)
    {
        var g = evt.Bomb;
        if (g == null) return;
        var outcome = FindBombOutcome(count, evt.EventId);
        long outcomeLocal = outcome == null ? -1 : local + evt.T0Utc - outcome.T0Utc;
        if (outcome != null && outcomeLocal >= 0) return;   // the outcome event draws from its T0 on

        float black = BombMath.BlackOpacity(local);
        if (black > 0.5f) _agendaHiddenByEvent = true;
        SetEventBrushBlack(black);
        FillNode();
        if (black < 0.999f) return;

        int roulette = BombMath.RouletteOrder(g, local);
        if (roulette == _layout.Order)
        {
            SetEventBrush("#FFFFFF", 0.15f);
            FillNode();
            SetEventBrush("#FFFFFF", 0.9f);
            _d2dContext!.DrawRectangle(new RectangleF(6, 6, _layout.Width - 12, _layout.Height - 12), _eventBrush!, 12f);
        }

        float progress = BombMath.CableProgress(g, local);
        if (progress > 0)
            for (int i = 0; i < g.Cables.Count; i++)
                DrawCable(g.Cables[i].Points, BombMath.PrefixPointCount(g.Cables[i].Points, progress), g.Cables[i].ColorHex, 1f);

        bool onBomb = _layout.Id == g.BombNodeId;
        if (onBomb && BombMath.BombVisible(g, local, outcome, outcomeLocal))
        {
            float dy = BombMath.DropOffset(local) * (g.BombY - _layout.OffsetY + g.BombSize);
            DrawBombBody(evt, g, local, dy, 1f, 0f);
            if (local >= BombMath.DropEndMs)
            {
                for (int i = 0; i < g.Cables.Count; i++)
                {
                    var p = g.Cables[i].Points;
                    if (p.Length < 2) continue;
                    SetEventBrush(g.Cables[i].ColorHex, 1f);
                    _d2dContext!.FillEllipse(new Ellipse(new Vector2(LocalX(p[0]), p[1] - _layout.OffsetY), 0.06f * g.BombSize, 0.06f * g.BombSize), _eventBrush!);
                }
                DrawBombRules(evt, g);
            }
        }

        if (local >= g.IntroMs)
        {
            float cardFade = Math.Clamp((local - g.IntroMs) / 500f, 0f, 1f);
            for (int i = 0; i < g.Cables.Count; i++)
                if (g.Cables[i].EndNodeId == _layout.Id) DrawBombCard(evt, g, i, cardFade);
        }

        if (local >= BombMath.DropEndMs && BombMath.TimerVisible(g, local))
        {
            int remaining = BombMath.RemainingMs(g, local);
            if (onBomb) DrawSevenSegTimer(remaining, g.BombX, g.BombY - _layout.OffsetY - 0.5f * g.BombSize - 0.55f * g.BombSize, 0.4f * g.BombSize, BombTimerRed, centred: true);
            DrawCornerTimer(remaining, BombTimerRed);
        }
    }

    private static void FillNode() =>
        _d2dContext!.FillRectangle(new RectangleF(0, 0, _layout.Width, _layout.Height), _eventBrush!);

    /// <summary>Local x of a canvas x on this node (mirroring included).</summary>
    private static float LocalX(float virtualX) => NodeMapping.ToLocalX(virtualX, 0f, _layout);

    private static void DrawCable(float[] points, int pointCount, string colorHex, float opacity)
    {
        if (pointCount < 2) return;
        if (_bombSegments.Length < 3 * pointCount) _bombSegments = new (int, float)[3 * pointCount];   // grows once per game
        int n = BombMath.SegmentsOnNode(points, pointCount, _layout, CableWidth, _bombSegments);
        if (n == 0) return;
        _roundStroke ??= _d2dFactory?.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });
        SetEventBrush(colorHex, opacity);
        for (int k = 0; k < n; k++)
        {
            var (i, shift) = _bombSegments[k];
            var a = new Vector2(LocalX(points[2 * i] + shift), points[2 * i + 1] - _layout.OffsetY);
            var b = new Vector2(LocalX(points[2 * i + 2] + shift), points[2 * i + 3] - _layout.OffsetY);
            _d2dContext!.DrawLine(a, b, _eventBrush!, CableWidth, _roundStroke);
        }
    }

    /// <summary>The bomb: the logo (GIF frames on the event clock) or a drawn bomb when there is none.</summary>
    private static void DrawBombBody(ShowEvent evt, BombGame g, long local, float dy, float scale, float rotationDeg)
    {
        float size = g.BombSize * scale;
        if (size <= 1f) return;
        float cx = LocalX(g.BombX), cy = g.BombY - _layout.OffsetY + dy;
        var prior = _d2dContext!.Transform;
        if (rotationDeg != 0f) _d2dContext.Transform = Matrix3x2.CreateRotation(rotationDeg * MathF.PI / 180f, new Vector2(cx, cy)) * prior;
        try
        {
            if (_eventSprites.TryGetValue(IdsOf(evt).Logo, out var logo))
            {
                var frame = SpriteFrame(logo, local);
                var bs = frame.Size;
                float k = Math.Min(size / bs.Width, size / bs.Height);
                float w = bs.Width * k, h = bs.Height * k;
                _d2dContext.DrawBitmap(frame, new RectangleF(cx - w / 2f, cy - h / 2f, w, h), 1f, BitmapInterpolationMode.Linear, null);
                return;
            }
            float r = size * 0.42f;
            SetEventBrush("#303030", 1f);
            _d2dContext.FillEllipse(new Ellipse(new Vector2(cx, cy), r, r), _eventBrush!);
            SetEventBrush("#6A6A6A", 1f);
            _d2dContext.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), _eventBrush!, size * 0.02f);
            var fuseFrom = new Vector2(cx + r * 0.55f, cy - r * 0.8f);
            var fuseTo = new Vector2(cx + r * 0.95f, cy - r * 1.25f);
            SetEventBrush("#8D6E63", 1f);
            _d2dContext.DrawLine(fuseFrom, fuseTo, _eventBrush!, size * 0.03f);
            float spark = size * 0.05f * (1f + 0.4f * MathF.Sin(local * 0.03f));
            SetEventBrush("#FFC400", 1f);
            _d2dContext.FillEllipse(new Ellipse(fuseTo, spark, spark), _eventBrush!);
        }
        finally
        {
            _d2dContext.Transform = prior;
        }
    }

    private static ID2D1Bitmap SpriteFrame(EventSpriteGpu sprite, long local)
    {
        if (sprite.Frames.Length == 1) return sprite.Frames[0];
        long t = SyncTiming.PositiveModulo(local, sprite.TotalMs);
        for (int f = 0; f < sprite.DelaysMs.Length; f++)
        {
            if (t < sprite.DelaysMs[f]) return sprite.Frames[f];
            t -= sprite.DelaysMs[f];
        }
        return sprite.Frames[0];
    }

    /// <summary>A text sprite into a box: height = box height, width by aspect (squeezed to the box), centred or left.</summary>
    private static void DrawBombText(string spriteId, float x, float y, float w, float h, float opacity, bool centred)
    {
        if (!_eventSprites.TryGetValue(spriteId, out var sprite)) return;
        var bmp = sprite.Frames[0];
        var s = bmp.Size;
        if (s.Width <= 0 || s.Height <= 0) return;
        float tw = Math.Min(h * s.Width / s.Height, w);
        float th = tw * s.Height / s.Width;
        float tx = centred ? x + (w - tw) / 2f : x;
        _d2dContext!.DrawBitmap(bmp, new RectangleF(tx, y + (h - th) / 2f, tw, th), opacity, BitmapInterpolationMode.Linear, null);
    }

    private static void DrawBombRules(ShowEvent evt, BombGame g)
    {
        float w = 2.6f * g.BombSize, h = 0.1f * g.BombSize;
        float x = LocalX(g.BombX) - w / 2f, y = g.BombY - _layout.OffsetY + 0.62f * g.BombSize;
        var ids = IdsOf(evt);
        DrawBombText(ids.Rule0, x, y, w, h, 1f, centred: true);
        DrawBombText(ids.Rule1, x, y + 1.3f * h, w, h, 1f, centred: true);
    }

    /// <summary>The riddle card at the end of cable <paramref name="i"/>.</summary>
    private static void DrawBombCard(ShowEvent evt, BombGame g, int i, float fade)
    {
        var c = g.Cables[i];
        float x = NodeMapping.ToLocalX(c.CardX, c.CardW, _layout), y = c.CardY - _layout.OffsetY, w = c.CardW, h = c.CardH;
        _eventBrush!.Color = BombCard;
        _eventBrush.Opacity = fade;   // opaque: cables to the other cards pass behind it
        _d2dContext!.FillRectangle(new RectangleF(x, y, w, h), _eventBrush);
        SetEventBrush(c.ColorHex, fade);
        _d2dContext.DrawRectangle(new RectangleF(x, y, w, h), _eventBrush!, 6f);
        _d2dContext.FillRectangle(new RectangleF(x, y, w, 0.1f * h), _eventBrush!);

        var r = c.Riddle;
        if (r.Kind == RiddleKind.Math)
        {
            var ids = IdsOf(evt);
            DrawBombText(i < ids.Formulas.Length ? ids.Formulas[i] : "", x + 0.06f * w, y + 0.25f * h, 0.88f * w, 0.3f * h, fade, centred: true);
            DrawBombText(ids.Rule0, x + 0.1f * w, y + 0.75f * h, 0.8f * w, 0.09f * h, fade, centred: true);
            return;
        }
        if (r.Strands == null || r.StrandExits == null) return;

        // The cable arrives at the edge nearest its last point; strands run from there, labels sit at the far side.
        float lastX = c.Points.Length >= 2 ? LocalX(c.Points[^2]) : x;
        if (_layout.Wraps && _layout.CanvasWidth > 0)
            lastX = LocalX(NodeMapping.PositiveModulo(c.Points[^2], _layout.CanvasWidth));
        bool fromLeft = Math.Abs(lastX - x) <= Math.Abs(lastX - (x + w));
        float areaX = fromLeft ? x + 0.04f * w : x + 0.30f * w, areaW = 0.66f * w;
        float areaY = y + 0.16f * h, areaH = 0.78f * h;
        _roundStroke ??= _d2dFactory?.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });
        for (int s = 2; s >= 0; s--)
        {
            var strand = r.Strands[s];
            int n = strand.Length;
            for (int k = 0; k + 1 < n; k++)
            {
                float t0 = 0.05f + 0.9f * k / (n - 1), t1 = 0.05f + 0.9f * (k + 1) / (n - 1);
                if (!fromLeft) { t0 = 1f - t0; t1 = 1f - t1; }
                bool colored = s == 0 && k < n * 0.15f;
                if (colored) SetEventBrush(c.ColorHex, fade);
                else { _eventBrush.Color = BombGrey; _eventBrush.Opacity = fade; }
                _d2dContext.DrawLine(new Vector2(areaX + t0 * areaW, areaY + strand[k] * areaH),
                                     new Vector2(areaX + t1 * areaW, areaY + strand[k + 1] * areaH), _eventBrush!, StrandWidth, _roundStroke);
            }
            float endY = areaY + strand[n - 1] * areaH;
            float labelW = 0.26f * w, labelH = 0.07f * h;
            float labelX = fromLeft ? x + 0.71f * w : x + 0.03f * w;
            DrawBombText(r.StrandExits[s] == 0 ? IdsOf(evt).Det : IdsOf(evt).Gnd, labelX, endY - labelH / 2f, labelW, labelH, fade, centred: false);
        }
        DrawBombText(IdsOf(evt).Rule1, x + 0.1f * w, y + 0.9f * h, 0.8f * w, 0.07f * h, fade, centred: true);
    }

    /// <summary>"m:ss" as a 7-segment LED: unlit segments faint. (cx, top) in local units when centred, else left x.</summary>
    private static void DrawSevenSegTimer(int remainingMs, float virtualOrLocalX, float top, float h, Color4 color, bool centred)
    {
        int s = (Math.Max(0, remainingMs) + 999) / 1000;
        int m = Math.Min(9, s / 60), sec = s % 60;
        float dw = 0.55f * h, gap = 0.22f * h, colon = 0.25f * h;
        float total = 3 * dw + 3 * gap + colon;
        float x = centred ? LocalX(virtualOrLocalX) - total / 2f : virtualOrLocalX;
        DrawDigit(m, x, top, dw, h, color);
        x += dw + gap;
        _eventBrush!.Color = color;
        _eventBrush.Opacity = 1f;
        float t = 0.12f * h;
        _d2dContext!.FillRectangle(new RectangleF(x + (colon - t) / 2f, top + 0.3f * h, t, t), _eventBrush);
        _d2dContext.FillRectangle(new RectangleF(x + (colon - t) / 2f, top + 0.62f * h, t, t), _eventBrush);
        x += colon + gap;
        DrawDigit(sec / 10, x, top, dw, h, color);
        DrawDigit(sec % 10, x + dw + gap, top, dw, h, color);
    }

    private static void DrawDigit(int digit, float x, float y, float w, float h, Color4 color)
    {
        var lit = BombMath.SevenSegment(digit);
        float t = 0.12f * h, half = h / 2f;
        for (int k = 0; k < 7; k++)
        {
            var r = k switch
            {
                0 => new RectangleF(x + t, y, w - 2 * t, t),
                1 => new RectangleF(x + w - t, y + t, t, half - t),
                2 => new RectangleF(x + w - t, y + half, t, half - t),
                3 => new RectangleF(x + t, y + h - t, w - 2 * t, t),
                4 => new RectangleF(x, y + half, t, half - t),
                5 => new RectangleF(x, y + t, t, half - t),
                _ => new RectangleF(x + t, y + half - t / 2f, w - 2 * t, t),
            };
            _eventBrush!.Color = color;
            _eventBrush.Opacity = lit[k] ? 1f : 0.1f;
            _d2dContext!.FillRectangle(r, _eventBrush);
        }
    }

    /// <summary>The small timer in every seat's bottom-right corner (people walking the room keep the time in view).</summary>
    private static void DrawCornerTimer(int remainingMs, Color4 color)
    {
        float h = 0.05f * _layout.Height, margin = 0.03f * _layout.Height;
        float total = h * (3 * 0.55f + 3 * 0.22f + 0.25f);
        DrawSevenSegTimer(remainingMs, _layout.Width - margin - total, _layout.Height - margin - h, h, color, centred: false);
    }

    // ── Outcome event ───────────────────────────────────────────────────────

    private static void DrawBombOutcome(ShowEvent evt, long local, Matrix3x2 canvas)
    {
        var g = evt.Bomb;
        if (g == null) return;
        float black = BombMath.OutcomeBlack(evt, local);
        if (black > 0.5f) _agendaHiddenByEvent = true;
        SetEventBrushBlack(black);
        FillNode();
        if (g.Result == BombResult.Aborted) return;
        float fadeOut = black;

        if (g.Result is BombResult.WrongCable or BombResult.Timeout)
        {
            var (sx, sy) = BombMath.Shake(evt.Seed, local);
            _d2dContext!.Transform = Matrix3x2.CreateTranslation(sx, sy) * canvas;
            try
            {
                if (_layout.Id == g.BombNodeId && local < 600)
                {
                    SetEventBrush("#FFFFFF", 1f - local / 600f);
                    FillNode();
                }
                float canvasW = _layout.CanvasWidth > 0 ? _layout.CanvasWidth : _layout.Width;
                float radius = BombMath.ShockwaveRadius(local, canvasW / 2500f);
                float ringOpacity = 1f - local / 3000f;
                // the room lights up where the wave passes
                float nodeCenter = _layout.OffsetX + _layout.Width / 2f, dist = Math.Abs(nodeCenter - g.BombX);
                if (_layout.Wraps) dist = Math.Min(dist, canvasW - dist);
                float pass = 0.5f * Math.Max(0f, 1f - Math.Abs(radius - dist) / 500f);
                if (pass > 0.005f)
                {
                    _eventBrush!.Color = BombOrange;
                    _eventBrush.Opacity = pass;
                    FillNode();
                }
                if (ringOpacity > 0.005f)
                {
                    _eventBrush!.Color = BombOrange;
                    _eventBrush.Opacity = ringOpacity;
                    int copies = _layout.Wraps ? 3 : 1;
                    for (int c = 0; c < copies; c++)
                    {
                        float shift = c == 0 ? 0 : c == 1 ? canvasW : -canvasW;
                        var center = new Vector2(LocalX(g.BombX + shift), g.BombY - _layout.OffsetY);
                        _d2dContext.DrawEllipse(new Ellipse(center, radius, radius), _eventBrush, 60f);
                    }
                }
                DrawCelebration(evt, local, canvas, FirePalette);
                if (local >= 300)
                    DrawBombText(IdsOf(evt).Text, 0.1f * _layout.Width, 0.35f * _layout.Height, 0.8f * _layout.Width, 0.3f * _layout.Height, fadeOut, centred: true);
            }
            finally
            {
                _d2dContext!.Transform = canvas;
            }
            return;
        }

        // Defused / Recycled: grey cables, the bomb (shrinking into the bin when recycled), frozen green timer, confetti.
        foreach (var c in g.Cables) DrawCable(c.Points, c.Points.Length / 2, "#616161", fadeOut);
        if (_layout.Id == g.BombNodeId)
        {
            bool recycled = g.Result == BombResult.Recycled;
            float scale = recycled ? Math.Max(0f, 1f - local / 1200f) : 1f;
            DrawBombBody(evt, g, local, 0f, scale, recycled ? local * 0.4f : 0f);
            DrawSevenSegTimer(g.RemainingMs, g.BombX, g.BombY - _layout.OffsetY - 1.05f * g.BombSize, 0.4f * g.BombSize, BombTimerGreen, centred: true);
        }
        DrawCornerTimer(g.RemainingMs, BombTimerGreen);
        DrawCelebration(evt, local, canvas, null);
        // below the bomb (the frozen timer sits above it)
        DrawBombText(IdsOf(evt).Text, 0.1f * _layout.Width, 0.74f * _layout.Height, 0.8f * _layout.Width, 0.12f * _layout.Height, fadeOut, centred: true);
    }

    private static void DisposeBombResources()
    {
        _roundStroke?.Dispose();
        _roundStroke = null;
    }
}

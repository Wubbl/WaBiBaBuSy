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
/// BiBaBo (design 2026-10-21-bibabo-bomb-game-design.md, §14 variation): the Bomb event (blackout, roulette, bomb,
/// cables through an optional junction box, LED timer, keypad, RULES, riddle cards of seven kinds), its newest BombStep
/// (cut cables, typed digits) and the BombOutcome (BOOM / DEFUSED / RECYCLED). Everything from BombMath and the events;
/// drawn in canvas units under the show-event transform, inside DrawShowEvents. Text is TextSprite bitmaps decoded per
/// event ("id#slot", ids built once in <see cref="BombSpriteIds"/>).
/// </summary>
partial class Program
{
    private static readonly Color4 BombTimerRed = new(1f, 0.09f, 0.27f, 1f);      // #FF1744
    private static readonly Color4 BombTimerGreen = new(0f, 0.9f, 0.46f, 1f);     // #00E676
    private static readonly Color4 BombCard = new(0.1f, 0.1f, 0.1f, 1f);          // #1A1A1A
    private static readonly Color4 BombGrey = new(0.62f, 0.62f, 0.62f, 1f);       // #9E9E9E
    private static readonly Color4 BombOrange = new(1f, 0.43f, 0f, 1f);           // #FF6D00
    private static readonly Color4 BombBox = new(0.15f, 0.2f, 0.22f, 1f);         // #263238
    private static readonly string[] FirePalette = { "#FF6D00", "#FFAB00", "#FF3D00", "#FFD600", "#DD2C00", "#FFFFFF" };
    private static readonly string[] GateNames = { "AND", "OR", "XOR", "NAND", "NOR" };
    private const float CableWidth = 10f, StrandWidth = 5f;

    private static (int I, float Shift)[] _bombSegments = new (int, float)[1024];   // render thread only
    private static ID2D1StrokeStyle? _roundStroke;

    /// <summary>The sprite ids of one BiBaBo event ("id#slot"), built once — no string per frame.</summary>
    private sealed class BombSpriteIds
    {
        public BombSpriteIds(string id, BombGame? g)
        {
            Logo = id + "#logo"; Text = id + "#text"; Header = id + "#rulesHdr"; Away = id + "#rulesAway"; Junction = id + "#junction"; Clear = id + "#C";
            int rules = g?.RuleLines.Length ?? 0, cables = g?.Cables.Count ?? 0;
            Rules = Enumerable.Range(0, rules).Select(k => $"{id}#rule{k}").ToArray();
            Formulas = Enumerable.Range(0, cables).Select(i => $"{id}#f{i}").ToArray();
            Subtitles = Enumerable.Range(0, cables).Select(i => $"{id}#sub{i}").ToArray();
            Inputs = Enumerable.Range(0, cables).Select(i => Enumerable.Range(0, 3).Select(k => $"{id}#in{i}{k}").ToArray()).ToArray();
            Digits = Enumerable.Range(0, 10).Select(d => $"{id}#d{d}").ToArray();
            Gates = GateNames.ToDictionary(n => n, n => $"{id}#g{n}");
        }
        public string Logo { get; }
        public string Text { get; }
        public string Header { get; }
        public string Away { get; }
        public string Junction { get; }
        public string Clear { get; }
        public string[] Rules { get; }
        public string[] Formulas { get; }
        public string[] Subtitles { get; }
        public string[][] Inputs { get; }
        public string[] Digits { get; }
        public Dictionary<string, string> Gates { get; }
    }

    private static readonly Dictionary<string, BombSpriteIds> _bombIds = new();   // render thread only

    private static BombSpriteIds IdsOf(ShowEvent evt)
    {
        if (_bombIds.TryGetValue(evt.EventId, out var ids)) return ids;
        if (_bombIds.Count > 16) _bombIds.Clear();   // a few games per party: drop the old ones now and then
        ids = new BombSpriteIds(evt.EventId, evt.Bomb);
        _bombIds[evt.EventId] = ids;
        return ids;
    }

    /// <summary>stdin thread: the sprites a Bomb / BombOutcome event needs.</summary>
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
            for (int k = 0; k < g.RuleLines.Length; k++)
            {
                string line = g.RuleLines[k];
                StartSpriteDecode($"{id}#rule{k}", id, () => TextFrames(line, k == 0 ? "#FFFFFF" : "#BDBDBD"));
            }
            StartSpriteDecode(id + "#rulesHdr", id, () => TextFrames("RULES", "#FFD700"));
            StartSpriteDecode(id + "#rulesAway", id, () => TextFrames("RULES: somewhere in the room", "#FFD700"));
            StartSpriteDecode(id + "#junction", id, () => TextFrames("JUNCTION", "#B0BEC5"));
            StartSpriteDecode(id + "#C", id, () => TextFrames("C", "#FFFFFF"));
            for (int d = 0; d < 10; d++)
            {
                string digit = d.ToString();
                StartSpriteDecode($"{id}#d{d}", id, () => TextFrames(digit, "#FFFFFF"));
            }
            foreach (var gate in GateNames)
                StartSpriteDecode($"{id}#g{gate}", id, () => TextFrames(gate, "#FFFFFF"));
            for (int i = 0; i < g.Cables.Count; i++)
            {
                var r = g.Cables[i].Riddle;
                string sub = BombRiddles.Subtitle(r);
                StartSpriteDecode($"{id}#sub{i}", id, () => TextFrames(sub, "#BDBDBD"));
                if (r.Formula is { } formula)
                    StartSpriteDecode($"{id}#f{i}", id, () => TextFrames(formula, "#FFFFFF"));
                if (r.LogicInputs is { } inputs)
                    for (int k = 0; k < 3; k++)
                    {
                        string label = $"{"ABC"[k]} = {inputs[k]}";
                        StartSpriteDecode($"{id}#in{i}{k}", id, () => TextFrames(label, "#FFFFFF"));
                    }
            }
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

    /// <summary>The game's BombOutcome, or its newest BombStep that has started (<paramref name="type"/>).</summary>
    private static ShowEvent? FindBombEvent(int count, string gameId, ShowEventType type, long bombT0, long bombLocal)
    {
        ShowEvent? best = null;
        for (int i = 0; i < count; i++)
        {
            var e = _showEventScratch[i];
            if (e.Type != type || e.Bomb?.GameId != gameId) continue;
            if (type == ShowEventType.BombStep && bombLocal + bombT0 - e.T0Utc < 0) continue;   // not shown yet
            if (best == null || e.T0Utc > best.T0Utc) best = e;
        }
        return best;
    }

    // ── Bomb event ──────────────────────────────────────────────────────────

    private static void DrawBomb(ShowEvent evt, long local, int count)
    {
        var g = evt.Bomb;
        if (g == null) return;
        var outcome = FindBombEvent(count, evt.EventId, ShowEventType.BombOutcome, evt.T0Utc, local);
        long outcomeLocal = outcome == null ? -1 : local + evt.T0Utc - outcome.T0Utc;
        if (outcome != null && outcomeLocal >= 0) return;   // the outcome event draws from its T0 on
        var step = FindBombEvent(count, evt.EventId, ShowEventType.BombStep, evt.T0Utc, local)?.Bomb;
        var cut = step?.CutCables;

        float black = BombMath.BlackOpacity(local);
        if (black > 0.5f) _agendaHiddenByEvent = true;
        SetEventBrushBlack(black);
        FillNode();
        if (black < 0.999f) return;

        if (BombMath.RouletteOrder(g, local) == _layout.Order)
        {
            SetEventBrush("#FFFFFF", 0.15f);
            FillNode();
            SetEventBrush("#FFFFFF", 0.9f);
            _d2dContext!.DrawRectangle(new RectangleF(6, 6, _layout.Width - 12, _layout.Height - 12), _eventBrush!, 12f);
        }

        float progress = BombMath.CableProgress(g, local);
        if (progress > 0)
            for (int i = 0; i < g.Cables.Count; i++)
                DrawCableColoured(g.Cables[i], BombMath.PrefixPointCount(g.Cables[i].Points, progress), cut?.Contains(i) == true ? 0.3f : 1f);
        if (g.Junction is { } j && j.NodeId == _layout.Id && progress > 0) DrawJunction(evt, g, j, progress);

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
                    if (cut?.Contains(i) == true) { _eventBrush!.Color = BombGrey; _eventBrush.Opacity = 1f; }
                    else SetEventBrush(g.Cables[i].ColorHex, 1f);
                    _d2dContext!.FillEllipse(new Ellipse(new Vector2(LocalX(p[0]), p[1] - _layout.OffsetY), 0.06f * g.BombSize, 0.06f * g.BombSize), _eventBrush!);
                }
                if (g.Mode == BombMode.Keypad) DrawKeypad(evt, g, step?.Typed ?? "");
                DrawBombRules(evt, g);
            }
        }

        if (local >= g.IntroMs)
        {
            float fade = Math.Clamp((local - g.IntroMs) / 500f, 0f, 1f);
            for (int i = 0; i < g.Cables.Count; i++)
                if (g.Cables[i].EndNodeId == _layout.Id) DrawBombCard(evt, g, i, fade);
            if (g.RuleNodeId == _layout.Id) DrawRulesCard(evt, g, fade);
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

    private static void EnsureRoundStroke() =>
        _roundStroke ??= _d2dFactory?.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });

    /// <summary>A cable: its bomb colour up to the junction box, the card colour after it (the wire inside the box is the box's).</summary>
    private static void DrawCableColoured(BombCable c, int pointCount, float opacity)
    {
        if (c.SwapIndex <= 0)
        {
            DrawCable(c.Points, pointCount, 0, int.MaxValue, c.ColorHex, opacity, CableWidth);
            return;
        }
        DrawCable(c.Points, pointCount, 0, c.SwapIndex - 1, c.ColorHex, opacity, CableWidth);
        DrawCable(c.Points, pointCount, c.SwapIndex, int.MaxValue, c.CardColorHex, opacity, CableWidth);
    }

    /// <summary>Segments [fromSeg, toSeg) of the first <paramref name="pointCount"/> points that touch this node.</summary>
    private static void DrawCable(float[] points, int pointCount, int fromSeg, int toSeg, string colorHex, float opacity, float width)
    {
        if (pointCount < 2 || fromSeg >= toSeg) return;
        if (_bombSegments.Length < 3 * pointCount) _bombSegments = new (int, float)[3 * pointCount];   // grows once per game
        int n = BombMath.SegmentsOnNode(points, pointCount, _layout, width, _bombSegments);
        if (n == 0) return;
        EnsureRoundStroke();
        SetEventBrush(colorHex, opacity);
        for (int k = 0; k < n; k++)
        {
            var (i, shift) = _bombSegments[k];
            if (i < fromSeg || i >= toSeg) continue;
            var a = new Vector2(LocalX(points[2 * i] + shift), points[2 * i + 1] - _layout.OffsetY);
            var b = new Vector2(LocalX(points[2 * i + 2] + shift), points[2 * i + 3] - _layout.OffsetY);
            _d2dContext!.DrawLine(a, b, _eventBrush!, width, _roundStroke);
        }
    }

    /// <summary>The junction box with its wires visible inside (the colours change here).</summary>
    private static void DrawJunction(ShowEvent evt, BombGame g, BombJunction j, float progress)
    {
        float x = NodeMapping.ToLocalX(j.X, j.W, _layout), y = j.Y - _layout.OffsetY;
        _eventBrush!.Color = BombBox;
        _eventBrush.Opacity = 1f;
        _d2dContext!.FillRectangle(new RectangleF(x, y, j.W, j.H), _eventBrush);
        _eventBrush.Color = BombGrey;
        _d2dContext.DrawRectangle(new RectangleF(x, y, j.W, j.H), _eventBrush, 4f);
        DrawBombText(IdsOf(evt).Junction, x, y - 0.14f * j.H, j.W, 0.1f * j.H, 1f, centred: true);
        foreach (int i in j.Cables)
        {
            var c = g.Cables[i];
            int pointCount = BombMath.PrefixPointCount(c.Points, progress);
            if (pointCount > c.SwapIndex) DrawCable(c.Points, pointCount, c.SwapIndex - 1, c.SwapIndex, "#CFD8DC", 1f, 6f);
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

    /// <summary>The rules under the bomb (left of the keypad in keypad games), or where to find them.</summary>
    private static void DrawBombRules(ShowEvent evt, BombGame g)
    {
        var ids = IdsOf(evt);
        float cx = LocalX(g.BombX), top = g.BombY - _layout.OffsetY + 0.62f * g.BombSize;
        float w = 2.6f * g.BombSize, h = 0.1f * g.BombSize, x = cx - w / 2f;
        if (g.Mode == BombMode.Keypad)
        {
            w = 1.3f * g.BombSize;
            x = cx - 0.4f * g.BombSize - w;
        }
        if (g.RuleNodeId.Length > 0)
        {
            DrawBombText(ids.Away, x, top, w, h, 1f, centred: g.Mode != BombMode.Keypad);
            return;
        }
        for (int k = 0; k < ids.Rules.Length; k++)
            DrawBombText(ids.Rules[k], x, top + 1.3f * h * k, w, h, 1f, centred: g.Mode != BombMode.Keypad);
    }

    /// <summary>The RULES card on its seat.</summary>
    private static void DrawRulesCard(ShowEvent evt, BombGame g, float fade)
    {
        var ids = IdsOf(evt);
        float x = NodeMapping.ToLocalX(g.RuleX, g.RuleW, _layout), y = g.RuleY - _layout.OffsetY, w = g.RuleW, h = g.RuleH;
        _eventBrush!.Color = BombCard;
        _eventBrush.Opacity = fade;
        _d2dContext!.FillRectangle(new RectangleF(x, y, w, h), _eventBrush);
        SetEventBrush("#FFD700", fade);
        _d2dContext.DrawRectangle(new RectangleF(x, y, w, h), _eventBrush!, 6f);
        DrawBombText(ids.Header, x, y + 0.08f * h, w, 0.16f * h, fade, centred: true);
        for (int k = 0; k < ids.Rules.Length; k++)
            DrawBombText(ids.Rules[k], x + 0.06f * w, y + 0.36f * h + k * 0.16f * h, 0.88f * w, (k == 0 ? 0.12f : 0.09f) * h, fade, centred: true);
    }

    /// <summary>Keypad games: the display with the digits typed so far and the 1–9 / C / 0 keys below the bomb.</summary>
    private static void DrawKeypad(ShowEvent evt, BombGame g, string typed)
    {
        var ids = IdsOf(evt);
        float cx = LocalX(g.BombX), cy = g.BombY - _layout.OffsetY;
        var (dx, dy, dw, dh) = BombMath.KeypadDisplay(g);
        _eventBrush!.Color = BombBox;
        _eventBrush.Opacity = 1f;
        _d2dContext!.FillRectangle(new RectangleF(cx + dx, cy + dy, dw, dh), _eventBrush);
        float slot = dw / 3f;
        for (int k = 0; k < 3; k++)
        {
            if (k < typed.Length) DrawBombText(ids.Digits[typed[k] - '0'], cx + dx + k * slot, cy + dy + 0.1f * dh, slot, 0.8f * dh, 1f, centred: true);
            else
            {
                _eventBrush.Color = BombGrey;
                _d2dContext.FillRectangle(new RectangleF(cx + dx + k * slot + 0.3f * slot, cy + dy + 0.78f * dh, 0.4f * slot, 0.06f * dh), _eventBrush);
            }
        }
        for (int key = 0; key <= 10; key++)
        {
            var (kx, ky, kw, kh) = BombMath.KeyRect(g, key);
            var r = new RectangleF(cx + kx, cy + ky, kw, kh);
            _eventBrush.Color = BombBox;
            _d2dContext.FillRectangle(r, _eventBrush);
            _eventBrush.Color = BombGrey;
            _d2dContext.DrawRectangle(r, _eventBrush, 2f);
            DrawBombText(key == 10 ? ids.Clear : ids.Digits[key], r.X, r.Y + 0.15f * kh, kw, 0.7f * kh, 1f, centred: true);
        }
    }

    // ── riddle cards ────────────────────────────────────────────────────────

    /// <summary>The riddle card at the end of cable <paramref name="i"/>: frame in the card colour, the riddle, its instruction.</summary>
    private static void DrawBombCard(ShowEvent evt, BombGame g, int i, float fade)
    {
        var c = g.Cables[i];
        var ids = IdsOf(evt);
        float x = NodeMapping.ToLocalX(c.CardX, c.CardW, _layout), y = c.CardY - _layout.OffsetY, w = c.CardW, h = c.CardH;
        _eventBrush!.Color = BombCard;
        _eventBrush.Opacity = fade;   // opaque: cables to the other cards pass behind it
        _d2dContext!.FillRectangle(new RectangleF(x, y, w, h), _eventBrush);
        SetEventBrush(c.CardColorHex, fade);
        _d2dContext.DrawRectangle(new RectangleF(x, y, w, h), _eventBrush!, 6f);
        _d2dContext.FillRectangle(new RectangleF(x, y, w, 0.1f * h), _eventBrush!);
        DrawBombText(ids.Subtitles[i], x + 0.05f * w, y + 0.87f * h, 0.9f * w, 0.08f * h, fade, centred: true);

        // the cable arrives at the edge nearest its last point; entries face it
        float lastX = c.Points.Length >= 2 ? LocalX(_layout.Wraps && _layout.CanvasWidth > 0 ? NodeMapping.PositiveModulo(c.Points[^2], _layout.CanvasWidth) : c.Points[^2]) : x;
        bool fromLeft = Math.Abs(lastX - x) <= Math.Abs(lastX - (x + w));
        float ax = x + 0.05f * w, ay = y + 0.15f * h, aw = 0.9f * w, ah = 0.68f * h;   // content area
        var r = c.Riddle;
        switch (r.Kind)
        {
            case RiddleKind.Math:
            case RiddleKind.Sequence:
                DrawBombText(ids.Formulas[i], ax, ay + 0.25f * ah, aw, 0.45f * ah, fade, centred: true);
                break;
            case RiddleKind.Tangle: DrawTangle(ids, c, ax, ay, aw, ah, fromLeft, fade); break;
            case RiddleKind.Maze: DrawMaze(ids, c, ax, ay, aw, ah, fromLeft, fade); break;
            case RiddleKind.Logic: DrawLogic(ids, i, r, ax, ay, aw, ah, fade); break;
            case RiddleKind.Clock: DrawMirroredClock(r, ax, ay, aw, ah, fade); break;
            case RiddleKind.Dots: DrawDots(r, ax, ay, aw, ah, fade); break;
        }
    }

    private static void DrawTangle(BombSpriteIds ids, BombCable c, float ax, float ay, float aw, float ah, bool fromLeft, float fade)
    {
        var r = c.Riddle;
        if (r.Strands == null || r.StrandExits == null || r.ExitLabels == null) return;
        EnsureRoundStroke();
        float sw = 0.8f * aw, sx = fromLeft ? ax : ax + 0.2f * aw;
        for (int s = 2; s >= 0; s--)
        {
            var strand = r.Strands[s];
            int n = strand.Length;
            for (int k = 0; k + 1 < n; k++)
            {
                float t0 = 0.02f + 0.96f * k / (n - 1), t1 = 0.02f + 0.96f * (k + 1) / (n - 1);
                if (!fromLeft) { t0 = 1f - t0; t1 = 1f - t1; }
                if (s == 0 && k < n * 0.15f) SetEventBrush(c.CardColorHex, fade);
                else { _eventBrush!.Color = BombGrey; _eventBrush.Opacity = fade; }
                _d2dContext!.DrawLine(new Vector2(sx + t0 * sw, ay + strand[k] * ah), new Vector2(sx + t1 * sw, ay + strand[k + 1] * ah), _eventBrush!, StrandWidth, _roundStroke);
            }
            float endY = ay + strand[n - 1] * ah, lh = 0.12f * ah;
            float lx = fromLeft ? ax + 0.84f * aw : ax;
            DrawBombText(ids.Digits[r.ExitLabels[r.StrandExits[s]]], lx, endY - lh / 2f, 0.16f * aw, lh, fade, centred: true);
        }
    }

    private static void DrawMaze(BombSpriteIds ids, BombCable c, float ax, float ay, float aw, float ah, bool fromLeft, float fade)
    {
        var r = c.Riddle;
        if (r.MazeWalls == null || r.ExitRows == null || r.ExitLabels == null) return;
        int mw = r.MazeW, mh = r.MazeH;
        float cell = Math.Min(0.82f * aw / mw, ah / mh);
        float gw = cell * mw, gh = cell * mh;
        float gx = fromLeft ? ax + 0.04f * aw : ax + aw - 0.04f * aw - gw, gy = ay + (ah - gh) / 2f;
        float X(int col) => fromLeft ? gx + col * cell : gx + gw - col * cell;   // mirrored when the cable arrives from the right
        _eventBrush!.Color = BombGrey;
        _eventBrush.Opacity = fade;
        float t = Math.Max(2f, 0.08f * cell);
        void Line(float x0, float y0, float x1, float y1) => _d2dContext!.DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), _eventBrush!, t);
        for (int row = 0; row < mh; row++)
        {
            float y0 = gy + row * cell, y1 = y0 + cell;
            if (row != r.MazeEntryRow) Line(X(0), y0, X(0), y1);                    // entry side, with the entry gap
            if (Array.IndexOf(r.ExitRows, row) < 0) Line(X(mw), y0, X(mw), y1);      // exit side, with the exit gaps
            for (int col = 0; col < mw; col++)
            {
                int walls = r.MazeWalls[row * mw + col];
                if (col < mw - 1 && (walls & 1) != 0) Line(X(col + 1), y0, X(col + 1), y1);
                if (row < mh - 1 && (walls & 2) != 0) Line(X(col), y1, X(col + 1), y1);
            }
        }
        Line(X(0), gy, X(mw), gy);
        Line(X(0), gy + gh, X(mw), gy + gh);
        SetEventBrush(c.CardColorHex, fade);   // the cable comes in
        float ey = gy + (r.MazeEntryRow + 0.5f) * cell;
        _d2dContext!.DrawLine(new Vector2(fromLeft ? ax : ax + aw, ey), new Vector2(X(0) + (fromLeft ? 0.5f : -0.5f) * cell, ey), _eventBrush!, StrandWidth);
        for (int e = 0; e < r.ExitRows.Length; e++)
        {
            float lx = fromLeft ? X(mw) + 0.15f * cell : X(mw) - 1.15f * cell;
            DrawBombText(ids.Digits[r.ExitLabels[e]], lx, gy + r.ExitRows[e] * cell + 0.1f * cell, cell, 0.8f * cell, fade, centred: true);
        }
    }

    private static void DrawLogic(BombSpriteIds ids, int cable, BombRiddle r, float ax, float ay, float aw, float ah, float fade)
    {
        if (r.LogicInputs == null || r.LogicGates == null) return;
        float rowH = ah / 3f, boxH = 0.55f * rowH;
        float inX = ax, inW = 0.22f * aw, gX = ax + 0.4f * aw, gW = 0.24f * aw, lampX = ax + 0.86f * aw, lampR = 0.2f * rowH;
        float Row(int k) => ay + (k + 0.5f) * rowH;
        _eventBrush!.Color = BombGrey;
        _eventBrush.Opacity = fade;
        for (int k = 0; k < 3; k++)   // inputs
        {
            _d2dContext!.DrawRectangle(new RectangleF(inX, Row(k) - boxH / 2f, inW, boxH), _eventBrush, 2f);
            DrawBombText(ids.Inputs[cable][k], inX, Row(k) - 0.35f * boxH, inW, 0.7f * boxH, fade, centred: true);
            _eventBrush.Color = BombGrey;
        }
        for (int k = 0; k < 3; k++)   // gates and their wires
        {
            var parts = r.LogicGates[k].Split(' ');   // ponytail: three tiny splits per card per frame — fine for a card
            int a = "ABC".IndexOf(parts[1][0]), b = "ABC".IndexOf(parts[2][0]);
            float gy = Row(k);
            _d2dContext!.DrawLine(new Vector2(inX + inW, Row(a)), new Vector2(gX, gy - 0.2f * boxH), _eventBrush, 3f);
            _d2dContext.DrawLine(new Vector2(inX + inW, Row(b)), new Vector2(gX, gy + 0.2f * boxH), _eventBrush, 3f);
            _d2dContext.DrawRectangle(new RectangleF(gX, gy - boxH / 2f, gW, boxH), _eventBrush, 3f);
            if (ids.Gates.TryGetValue(parts[0], out var gateId)) DrawBombText(gateId, gX, gy - 0.35f * boxH, gW, 0.7f * boxH, fade, centred: true);
            _eventBrush.Color = BombGrey;
            _d2dContext.DrawLine(new Vector2(gX + gW, gy), new Vector2(lampX - lampR, gy), _eventBrush, 3f);
            _d2dContext.DrawEllipse(new Ellipse(new Vector2(lampX, gy), lampR, lampR), _eventBrush, 3f);
            DrawBombText(ids.Digits[4 >> k], lampX - lampR, gy - 0.6f * lampR, 2 * lampR, 1.2f * lampR, fade, centred: true);
            _eventBrush.Color = BombGrey;
        }
    }

    /// <summary>An analog clock as seen in a mirror: 12 stays on top, everything else is flipped left ↔ right.</summary>
    private static void DrawMirroredClock(BombRiddle r, float ax, float ay, float aw, float ah, float fade)
    {
        float radius = 0.45f * Math.Min(aw, ah);
        var c = new Vector2(ax + aw / 2f, ay + ah / 2f);
        _eventBrush!.Color = new Color4(0.93f, 0.93f, 0.93f, 1f);
        _eventBrush.Opacity = fade;
        _d2dContext!.DrawEllipse(new Ellipse(c, radius, radius), _eventBrush, Math.Max(3f, 0.04f * radius));
        for (int tick = 0; tick < 12; tick++)
        {
            float angle = tick / 12f * 2f * MathF.PI, len = tick == 0 ? 0.25f : tick % 3 == 0 ? 0.17f : 0.09f;
            var dir = new Vector2(-MathF.Sin(angle), -MathF.Cos(angle));   // mirrored: clockwise becomes counter-clockwise
            _d2dContext.DrawLine(c + dir * radius * (1f - len), c + dir * radius * 0.97f, _eventBrush, tick == 0 ? 0.06f * radius : 0.03f * radius);
        }
        float hourAngle = (r.ClockHour % 12 + r.ClockMinute / 60f) / 12f * 2f * MathF.PI, minuteAngle = r.ClockMinute / 60f * 2f * MathF.PI;
        var hour = new Vector2(-MathF.Sin(hourAngle), -MathF.Cos(hourAngle));
        var minute = new Vector2(-MathF.Sin(minuteAngle), -MathF.Cos(minuteAngle));
        EnsureRoundStroke();
        _d2dContext.DrawLine(c, c + hour * radius * 0.5f, _eventBrush, 0.07f * radius, _roundStroke);
        _d2dContext.DrawLine(c, c + minute * radius * 0.8f, _eventBrush, 0.04f * radius, _roundStroke);
        _d2dContext.FillEllipse(new Ellipse(c, 0.05f * radius, 0.05f * radius), _eventBrush);
    }

    private static void DrawDots(BombRiddle r, float ax, float ay, float aw, float ah, float fade)
    {
        if (r.Dots == null || r.DotColors == null || r.DotPalette == null) return;
        float radius = 0.03f * Math.Min(aw, ah);
        for (int k = 0; k < r.DotColors.Length; k++)
        {
            SetEventBrush(r.DotPalette[r.DotColors[k]], fade);
            _d2dContext!.FillEllipse(new Ellipse(new Vector2(ax + r.Dots[2 * k] * aw, ay + r.Dots[2 * k + 1] * ah), radius, radius), _eventBrush!);
        }
    }

    // ── LED timer ───────────────────────────────────────────────────────────

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
        foreach (var c in g.Cables) DrawCable(c.Points, c.Points.Length / 2, 0, int.MaxValue, "#616161", fadeOut, CableWidth);
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

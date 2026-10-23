using SkiaSharp;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Client.Linux.Rendering;

/// <summary>
/// BiBaBo (design 2026-10-21-bibabo-bomb-game-design.md, §14 variation): Skia port of the Windows player's
/// Program.Bomb.cs — the Bomb event (blackout, roulette, bomb, cables through an optional junction box, LED timer,
/// keypad, RULES, riddle cards of seven kinds), its newest BombStep and the BombOutcome. Canvas units, inside
/// <see cref="Draw"/>'s scale. Text is <see cref="TextSprite"/> images decoded per event ("id#slot").
/// </summary>
internal sealed partial class ShowEventPainter
{
    private static readonly SKColor BombTimerRed = new(0xFF, 0x17, 0x44);
    private static readonly SKColor BombTimerGreen = new(0x00, 0xE6, 0x76);
    private static readonly SKColor BombCard = new(0x1A, 0x1A, 0x1A);
    private static readonly SKColor BombGrey = new(0x9E, 0x9E, 0x9E);
    private static readonly SKColor BombOrange = new(0xFF, 0x6D, 0x00);
    private static readonly SKColor BombBox = new(0x26, 0x32, 0x38);
    private static readonly string[] FirePalette = { "#FF6D00", "#FFAB00", "#FF3D00", "#FFD600", "#DD2C00", "#FFFFFF" };
    private static readonly string[] GateNames = { "AND", "OR", "XOR", "NAND", "NOR" };
    private const float CableWidth = 10f, StrandWidth = 5f;

    private (int I, float Shift)[] _bombSegments = new (int, float)[1024];   // render thread only

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
            // logic cards: (gate sprite, input a, input b) per lamp, parsed once
            Wires = (g?.Cables ?? new List<BombCable>()).Select(c => (c.Riddle.LogicGates ?? Array.Empty<string>()).Select(gate =>
            {
                var parts = gate.Split(' ');
                return (Gate: Gates.GetValueOrDefault(parts[0], ""), A: "ABC".IndexOf(parts[1][0]), B: "ABC".IndexOf(parts[2][0]));
            }).ToArray()).ToArray();
        }
        public (string Gate, int A, int B)[][] Wires { get; }
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

    private readonly Dictionary<string, BombSpriteIds> _bombIds = new();   // render thread only

    private BombSpriteIds IdsOf(ShowEvent evt)
    {
        if (_bombIds.TryGetValue(evt.EventId, out var ids)) return ids;
        if (_bombIds.Count > 16) _bombIds.Clear();   // a few games per party: drop the old ones now and then
        ids = new BombSpriteIds(evt.EventId, evt.Bomb);
        _bombIds[evt.EventId] = ids;
        return ids;
    }

    /// <summary>The sprites a Bomb / BombOutcome event needs.</summary>
    private void StartBombDecodes(ShowEvent evt)
    {
        var g = evt.Bomb;
        if (g == null) return;
        string id = evt.EventId;
        if (!string.IsNullOrEmpty(evt.ImagePath))
        {
            var path = evt.ImagePath!;
            StartDecode(id + "#logo", id, () => DecodeImage(path));
        }
        if (evt.Type == ShowEventType.Bomb)
        {
            for (int k = 0; k < g.RuleLines.Length; k++)
            {
                string line = g.RuleLines[k];
                string color = k == 0 ? "#FFFFFF" : "#BDBDBD";
                StartDecode($"{id}#rule{k}", id, () => Text(line, color));
            }
            StartDecode(id + "#rulesHdr", id, () => Text("RULES", "#FFD700"));
            StartDecode(id + "#rulesAway", id, () => Text("RULES: somewhere in the room", "#FFD700"));
            StartDecode(id + "#junction", id, () => Text("JUNCTION", "#B0BEC5"));
            StartDecode(id + "#C", id, () => Text("C", "#FFFFFF"));
            for (int d = 0; d < 10; d++)
            {
                string digit = d.ToString();
                StartDecode($"{id}#d{d}", id, () => Text(digit, "#FFFFFF"));
            }
            foreach (var gate in GateNames)
                StartDecode($"{id}#g{gate}", id, () => Text(gate, "#FFFFFF"));
            for (int i = 0; i < g.Cables.Count; i++)
            {
                var r = g.Cables[i].Riddle;
                string sub = BombRiddles.Subtitle(r);
                StartDecode($"{id}#sub{i}", id, () => Text(sub, "#BDBDBD"));
                if (r.Formula is { } formula)
                    StartDecode($"{id}#f{i}", id, () => Text(formula, "#FFFFFF"));
                if (r.LogicInputs is { } inputs)
                    for (int k = 0; k < 3; k++)
                    {
                        string label = $"{"ABC"[k]} = {inputs[k]}";
                        StartDecode($"{id}#in{i}{k}", id, () => Text(label, "#FFFFFF"));
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
        string textColor = g.Result is BombResult.Defused or BombResult.Recycled ? "#00E676" : "#FF6D00";
        if (text != null) StartDecode(id + "#text", id, () => Text(text, textColor));
    }

    /// <summary>The game's BombOutcome, or its newest BombStep that has started (<paramref name="type"/>).</summary>
    private ShowEvent? FindBombEvent(int count, string gameId, ShowEventType type, long bombT0, long bombLocal)
    {
        ShowEvent? best = null;
        for (int i = 0; i < count; i++)
        {
            var e = _scratch[i];
            if (e.Type != type || e.Bomb?.GameId != gameId) continue;
            if (type == ShowEventType.BombStep && bombLocal + bombT0 - e.T0Utc < 0) continue;   // not shown yet
            if (best == null || e.T0Utc > best.T0Utc) best = e;
        }
        return best;
    }

    // ── Bomb event ──────────────────────────────────────────────────────────

    private void DrawBomb(SKCanvas canvas, ShowEvent evt, long local, int count)
    {
        var g = evt.Bomb;
        if (g == null) return;
        var outcome = FindBombEvent(count, evt.EventId, ShowEventType.BombOutcome, evt.T0Utc, local);
        long outcomeLocal = outcome == null ? -1 : local + evt.T0Utc - outcome.T0Utc;
        if (outcome != null && outcomeLocal >= 0) return;   // the outcome event draws from its T0 on
        var step = FindBombEvent(count, evt.EventId, ShowEventType.BombStep, evt.T0Utc, local)?.Bomb;
        var cut = step?.CutCables;

        float black = BombMath.BlackOpacity(local);
        if (black > 0.5f) AgendaHidden = true;
        FillNode(canvas, Fill(SKColors.Black, black));
        if (black < 0.999f) return;

        if (BombMath.RouletteOrder(g, local) == _layout.Order)
        {
            FillNode(canvas, Fill(SKColors.White, 0.15f));
            canvas.DrawRect(SKRect.Create(6, 6, _layout.Width - 12, _layout.Height - 12), Stroke(SKColors.White, 0.9f, 12f));
        }

        float progress = BombMath.CableProgress(g, local);
        if (progress > 0)
            for (int i = 0; i < g.Cables.Count; i++)
                DrawCableColoured(canvas, g.Cables[i], BombMath.PrefixPointCount(g.Cables[i].Points, progress), cut?.Contains(i) == true ? 0.3f : 1f);
        if (g.Junction is { } j && j.NodeId == _layout.Id && progress > 0) DrawJunction(canvas, evt, g, j, progress);

        bool onBomb = _layout.Id == g.BombNodeId;
        if (onBomb && BombMath.BombVisible(g, local, outcome, outcomeLocal))
        {
            float dy = BombMath.DropOffset(local) * (g.BombY - _layout.OffsetY + g.BombSize);
            DrawBombBody(canvas, evt, g, local, dy, 1f, 0f);
            if (local >= BombMath.DropEndMs)
            {
                for (int i = 0; i < g.Cables.Count; i++)
                {
                    var p = g.Cables[i].Points;
                    if (p.Length < 2) continue;
                    var paint = cut?.Contains(i) == true ? Fill(BombGrey, 1f) : Fill(g.Cables[i].ColorHex, 1f);
                    canvas.DrawCircle(LocalX(p[0]), p[1] - _layout.OffsetY, BombMath.TerminalRadius(g), paint);
                }
                if (g.Mode == BombMode.Keypad) DrawKeypad(canvas, evt, g, step?.Typed ?? "");
                DrawBombRules(canvas, evt, g);
            }
        }

        if (local >= g.IntroMs)
        {
            float fade = Math.Clamp((local - g.IntroMs) / 500f, 0f, 1f);
            for (int i = 0; i < g.Cables.Count; i++)
                if (g.Cables[i].EndNodeId == _layout.Id) DrawBombCard(canvas, evt, g, i, fade);
            if (g.RuleNodeId == _layout.Id) DrawRulesCard(canvas, evt, g, fade);
        }

        if (local >= BombMath.DropEndMs && BombMath.TimerVisible(g, local))
        {
            int remaining = BombMath.RemainingMs(g, local);
            if (onBomb && !DrawTimerInLogo(canvas, evt, g, remaining, BombTimerRed))
                DrawSevenSegTimer(canvas, remaining, g.BombX, g.BombY - _layout.OffsetY + BombMath.TimerAboveTop * g.BombSize, BombMath.TimerAboveHeight * g.BombSize, BombTimerRed, centred: true);
            DrawCornerTimer(canvas, remaining, BombTimerRed);
        }
    }

    /// <summary>Local x of a canvas x on this node (mirroring included).</summary>
    private float LocalX(float virtualX) => NodeMapping.ToLocalX(virtualX, 0f, _layout);

    /// <summary>A cable: its bomb colour up to the junction box, the card colour after it.</summary>
    private void DrawCableColoured(SKCanvas canvas, BombCable c, int pointCount, float opacity)
    {
        if (c.SwapIndex <= 0)
        {
            DrawCable(canvas, c.Points, pointCount, 0, int.MaxValue, c.ColorHex, opacity, CableWidth);
            return;
        }
        DrawCable(canvas, c.Points, pointCount, 0, c.SwapIndex - 1, c.ColorHex, opacity, CableWidth);
        DrawCable(canvas, c.Points, pointCount, c.SwapIndex, int.MaxValue, c.CardColorHex, opacity, CableWidth);
    }

    /// <summary>Segments [fromSeg, toSeg) of the first <paramref name="pointCount"/> points that touch this node.</summary>
    private void DrawCable(SKCanvas canvas, float[] points, int pointCount, int fromSeg, int toSeg, string colorHex, float opacity, float width)
    {
        if (pointCount < 2 || fromSeg >= toSeg) return;
        if (_bombSegments.Length < 3 * pointCount) _bombSegments = new (int, float)[3 * pointCount];   // grows once per game
        int n = BombMath.SegmentsOnNode(points, pointCount, _layout, width, _bombSegments);
        if (n == 0) return;
        var paint = Stroke(colorHex, opacity, width, round: true);
        for (int k = 0; k < n; k++)
        {
            var (i, shift) = _bombSegments[k];
            if (i < fromSeg || i >= toSeg) continue;
            canvas.DrawLine(LocalX(points[2 * i] + shift), points[2 * i + 1] - _layout.OffsetY,
                LocalX(points[2 * i + 2] + shift), points[2 * i + 3] - _layout.OffsetY, paint);
        }
    }

    /// <summary>The junction box with its wires visible inside (the colours change here).</summary>
    private void DrawJunction(SKCanvas canvas, ShowEvent evt, BombGame g, BombJunction j, float progress)
    {
        float x = NodeMapping.ToLocalX(j.X, j.W, _layout), y = j.Y - _layout.OffsetY;
        canvas.DrawRect(SKRect.Create(x, y, j.W, j.H), Fill(BombBox, 1f));
        canvas.DrawRect(SKRect.Create(x, y, j.W, j.H), Stroke(BombGrey, 1f, 4f));
        DrawBombText(canvas, IdsOf(evt).Junction, x, y - 0.14f * j.H, j.W, 0.1f * j.H, 1f, centred: true);
        foreach (int i in j.Cables)
        {
            var c = g.Cables[i];
            int pointCount = BombMath.PrefixPointCount(c.Points, progress);
            if (pointCount > c.SwapIndex) DrawCable(canvas, c.Points, pointCount, c.SwapIndex - 1, c.SwapIndex, "#CFD8DC", 1f, 6f);
        }
    }

    /// <summary>The bomb: the logo (GIF frames on the event clock) or a drawn bomb when there is none.</summary>
    private void DrawBombBody(SKCanvas canvas, ShowEvent evt, BombGame g, long local, float dy, float scale, float rotationDeg)
    {
        float size = g.BombSize * scale;
        if (size <= 1f) return;
        float cx = LocalX(g.BombX), cy = g.BombY - _layout.OffsetY + dy;
        canvas.Save();
        if (rotationDeg != 0f) canvas.RotateDegrees(rotationDeg, cx, cy);
        try
        {
            if (TryGetSprite(IdsOf(evt).Logo, out var logo))
            {
                var frame = FrameAt(logo, local);
                float k = Math.Min(size / frame.Width, size / frame.Height);
                float w = frame.Width * k, h = frame.Height * k;
                DrawImage(canvas, frame, SKRect.Create(cx - w / 2f, cy - h / 2f, w, h), 1f);
                return;
            }
            float r = size * 0.42f;
            canvas.DrawCircle(cx, cy, r, Fill("#303030", 1f));
            canvas.DrawCircle(cx, cy, r, Stroke("#6A6A6A", 1f, size * 0.02f));
            var fuseFrom = new SKPoint(cx + r * 0.55f, cy - r * 0.8f);
            var fuseTo = new SKPoint(cx + r * 0.95f, cy - r * 1.25f);
            canvas.DrawLine(fuseFrom, fuseTo, Stroke("#8D6E63", 1f, size * 0.03f));
            float spark = size * 0.05f * (1f + 0.4f * MathF.Sin(local * 0.03f));
            canvas.DrawCircle(fuseTo, spark, Fill("#FFC400", 1f));
        }
        finally
        {
            canvas.Restore();
        }
    }

    /// <summary>The outcome's animation (explosion on BOOM, beer on DEFUSED) over the bomb, played once. False = none.</summary>
    private bool DrawOutcomeAnimation(SKCanvas canvas, ShowEvent evt, BombGame g, long local, bool holdLast)
    {
        if (g.OutcomeImageScale <= 0 || local < 0 || !TryGetSprite(IdsOf(evt).Logo, out var sprite)) return false;
        if (local >= sprite.TotalMs && !holdLast) return true;
        var frame = local >= sprite.TotalMs ? sprite.Frames[^1] : FrameAt(sprite, local);
        float size = g.OutcomeImageScale * g.BombSize, k = Math.Min(size / frame.Width, size / frame.Height);
        float w = frame.Width * k, h = frame.Height * k, cx = LocalX(g.BombX), cy = g.BombY - _layout.OffsetY;
        DrawImage(canvas, frame, SKRect.Create(cx - w / 2f, cy - h / 2f, w, h), 1f);
        return true;
    }

    /// <summary>The beer's first frame is the logo: the frozen timer stays in its box until the morph starts.</summary>
    private bool OnOutcomeFirstFrame(ShowEvent evt, long local) =>
        TryGetSprite(IdsOf(evt).Logo, out var sprite) && local < sprite.DelaysMs[0];

    /// <summary>A text sprite into a box: height = box height, width by aspect (squeezed to the box), centred, left or right.</summary>
    private void DrawBombText(SKCanvas canvas, string spriteId, float x, float y, float w, float h, float opacity, bool centred, bool right = false)
    {
        if (!TryGetSprite(spriteId, out var sprite)) return;
        var img = sprite.Frames[0];
        if (img.Width <= 0 || img.Height <= 0) return;
        float tw = Math.Min(h * img.Width / img.Height, w);
        float th = tw * img.Height / img.Width;
        float tx = centred ? x + (w - tw) / 2f : right ? x + w - tw : x;
        DrawImage(canvas, img, SKRect.Create(tx, y + (h - th) / 2f, tw, th), opacity);
    }

    /// <summary>The rules left of the bomb, beside its lower half, or where to find them; on a dark panel.</summary>
    private void DrawBombRules(SKCanvas canvas, ShowEvent evt, BombGame g)
    {
        var ids = IdsOf(evt);
        float s = g.BombSize, h = 0.085f * s, gap = 0.3f * h;
        float right = LocalX(g.BombX) - 0.45f * s, left = Math.Max(0.03f * _layout.Width, right - 1.6f * s);
        if (right - left < 4 * h) return;
        string[] lines = g.RuleNodeId.Length > 0 ? new[] { ids.Away } : ids.Rules;
        float blockH = lines.Length * h + (lines.Length - 1) * gap;
        float top = g.BombY - _layout.OffsetY + 0.38f * s - blockH / 2f, pad = 0.4f * h;   // below the lowest of 5 terminals
        canvas.DrawRect(SKRect.Create(left - pad, top - pad, right - left + 2 * pad, blockH + 2 * pad), Fill(SKColors.Black, 0.75f));
        for (int k = 0; k < lines.Length; k++)
            DrawBombText(canvas, lines[k], left, top + k * (h + gap), right - left, h, 1f, centred: false, right: true);
    }

    /// <summary>The RULES card on its seat.</summary>
    private void DrawRulesCard(SKCanvas canvas, ShowEvent evt, BombGame g, float fade)
    {
        var ids = IdsOf(evt);
        float x = NodeMapping.ToLocalX(g.RuleX, g.RuleW, _layout), y = g.RuleY - _layout.OffsetY, w = g.RuleW, h = g.RuleH;
        canvas.DrawRect(SKRect.Create(x, y, w, h), Fill(BombCard, fade));
        canvas.DrawRect(SKRect.Create(x, y, w, h), Stroke("#FFD700", fade, 6f));
        DrawBombText(canvas, ids.Header, x, y + 0.08f * h, w, 0.16f * h, fade, centred: true);
        for (int k = 0; k < ids.Rules.Length; k++)
            DrawBombText(canvas, ids.Rules[k], x + 0.06f * w, y + 0.36f * h + k * 0.16f * h, 0.88f * w, (k == 0 ? 0.12f : 0.09f) * h, fade, centred: true);
    }

    /// <summary>Keypad games: the display with the digits typed so far and the 1–9 / C / 0 keys below the bomb.</summary>
    private void DrawKeypad(SKCanvas canvas, ShowEvent evt, BombGame g, string typed)
    {
        var ids = IdsOf(evt);
        float cx = LocalX(g.BombX), cy = g.BombY - _layout.OffsetY;
        var (dx, dy, dw, dh) = BombMath.KeypadDisplay(g);
        canvas.DrawRect(SKRect.Create(cx + dx, cy + dy, dw, dh), Fill(BombBox, 1f));
        int digits = Math.Max(1, g.CodeOrder.Length);   // one digit per riddle
        float slot = dw / digits;
        for (int k = 0; k < digits; k++)
        {
            if (k < typed.Length) DrawBombText(canvas, ids.Digits[typed[k] - '0'], cx + dx + k * slot, cy + dy + 0.1f * dh, slot, 0.8f * dh, 1f, centred: true);
            else canvas.DrawRect(SKRect.Create(cx + dx + k * slot + 0.3f * slot, cy + dy + 0.78f * dh, 0.4f * slot, 0.06f * dh), Fill(BombGrey, 1f));
        }
        for (int key = 0; key <= 10; key++)
        {
            var (kx, ky, kw, kh) = BombMath.KeyRect(g, key);
            var r = SKRect.Create(cx + kx, cy + ky, kw, kh);
            canvas.DrawRect(r, Fill(BombBox, 1f));
            canvas.DrawRect(r, Stroke(BombGrey, 1f, 2f));
            DrawBombText(canvas, key == 10 ? ids.Clear : ids.Digits[key], r.Left, r.Top + 0.15f * kh, kw, 0.7f * kh, 1f, centred: true);
        }
    }

    // ── riddle cards ────────────────────────────────────────────────────────

    /// <summary>The riddle card at the end of cable <paramref name="i"/>: frame in the card colour, the riddle, its instruction.</summary>
    private void DrawBombCard(SKCanvas canvas, ShowEvent evt, BombGame g, int i, float fade)
    {
        var c = g.Cables[i];
        var ids = IdsOf(evt);
        float x = NodeMapping.ToLocalX(c.CardX, c.CardW, _layout), y = c.CardY - _layout.OffsetY, w = c.CardW, h = c.CardH;
        canvas.DrawRect(SKRect.Create(x, y, w, h), Fill(BombCard, fade));   // opaque: cables to the other cards pass behind it
        canvas.DrawRect(SKRect.Create(x, y, w, h), Stroke(c.CardColorHex, fade, 6f));
        canvas.DrawRect(SKRect.Create(x, y, w, 0.1f * h), Fill(c.CardColorHex, fade));
        DrawBombText(canvas, ids.Subtitles[i], x + 0.05f * w, y + 0.87f * h, 0.9f * w, 0.08f * h, fade, centred: true);
        if (c.Riddle is { Kind: RiddleKind.Dots, DotPalette: { } palette } && c.Riddle.DotTarget < palette.Length)
            canvas.DrawCircle(x + 0.05f * w, y + 0.91f * h, 0.035f * h, Fill(palette[c.Riddle.DotTarget], fade));   // the colour to count

        // the cable arrives at the edge nearest its last point; entries face it
        float lastX = c.Points.Length >= 2 ? LocalX(_layout.Wraps && _layout.CanvasWidth > 0 ? NodeMapping.PositiveModulo(c.Points[^2], _layout.CanvasWidth) : c.Points[^2]) : x;
        bool fromLeft = Math.Abs(lastX - x) <= Math.Abs(lastX - (x + w));
        float ax = x + 0.05f * w, ay = y + 0.15f * h, aw = 0.9f * w, ah = 0.68f * h;   // content area
        var r = c.Riddle;
        switch (r.Kind)
        {
            case RiddleKind.Math:
            case RiddleKind.Sequence:
                DrawBombText(canvas, ids.Formulas[i], ax, ay + 0.25f * ah, aw, 0.45f * ah, fade, centred: true);
                break;
            case RiddleKind.Tangle: DrawTangle(canvas, ids, c, ax, ay, aw, ah, fromLeft, fade); break;
            case RiddleKind.Maze: DrawMaze(canvas, ids, c, ax, ay, aw, ah, fromLeft, fade); break;
            case RiddleKind.Logic: DrawLogic(canvas, ids, i, r, ax, ay, aw, ah, fade); break;
            case RiddleKind.Clock: DrawMirroredClock(canvas, r, ax, ay, aw, ah, fade); break;
            case RiddleKind.Dots: DrawDots(canvas, r, ax, ay, aw, ah, fade); break;
        }
    }

    private void DrawTangle(SKCanvas canvas, BombSpriteIds ids, BombCable c, float ax, float ay, float aw, float ah, bool fromLeft, float fade)
    {
        var r = c.Riddle;
        if (r.Strands == null || r.StrandExits == null || r.ExitLabels == null) return;
        float sw = 0.8f * aw, sx = fromLeft ? ax : ax + 0.2f * aw;
        for (int s = 2; s >= 0; s--)
        {
            var strand = r.Strands[s];
            int n = strand.Length;
            for (int k = 0; k + 1 < n; k++)
            {
                float t0 = 0.02f + 0.96f * k / (n - 1), t1 = 0.02f + 0.96f * (k + 1) / (n - 1);
                if (!fromLeft) { t0 = 1f - t0; t1 = 1f - t1; }
                var paint = s == 0 && k < n * 0.15f ? Stroke(c.CardColorHex, fade, StrandWidth, round: true) : Stroke(BombGrey, fade, StrandWidth, round: true);
                canvas.DrawLine(sx + t0 * sw, ay + strand[k] * ah, sx + t1 * sw, ay + strand[k + 1] * ah, paint);
            }
            float endY = ay + strand[n - 1] * ah, lh = 0.12f * ah;
            float lx = fromLeft ? ax + 0.84f * aw : ax;
            DrawBombText(canvas, ids.Digits[r.ExitLabels[r.StrandExits[s]]], lx, endY - lh / 2f, 0.16f * aw, lh, fade, centred: true);
        }
    }

    private void DrawMaze(SKCanvas canvas, BombSpriteIds ids, BombCable c, float ax, float ay, float aw, float ah, bool fromLeft, float fade)
    {
        var r = c.Riddle;
        if (r.MazeWalls == null || r.ExitRows == null || r.ExitLabels == null) return;
        int mw = r.MazeW, mh = r.MazeH;
        float cell = Math.Min(0.82f * aw / mw, ah / mh);
        float gw = cell * mw, gh = cell * mh;
        float gx = fromLeft ? ax + 0.04f * aw : ax + aw - 0.04f * aw - gw, gy = ay + (ah - gh) / 2f;
        float X(int col) => fromLeft ? gx + col * cell : gx + gw - col * cell;   // mirrored when the cable arrives from the right
        var wall = Stroke(BombGrey, fade, Math.Max(2f, 0.08f * cell));
        void Line(float x0, float y0, float x1, float y1) => canvas.DrawLine(x0, y0, x1, y1, wall);
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
        float ey = gy + (r.MazeEntryRow + 0.5f) * cell;   // the cable comes in
        canvas.DrawLine(fromLeft ? ax : ax + aw, ey, X(0) + (fromLeft ? 0.5f : -0.5f) * cell, ey, Stroke(c.CardColorHex, fade, StrandWidth));
        for (int e = 0; e < r.ExitRows.Length; e++)
        {
            float lx = fromLeft ? X(mw) + 0.15f * cell : X(mw) - 1.15f * cell;
            DrawBombText(canvas, ids.Digits[r.ExitLabels[e]], lx, gy + r.ExitRows[e] * cell + 0.1f * cell, cell, 0.8f * cell, fade, centred: true);
        }
    }

    private void DrawLogic(SKCanvas canvas, BombSpriteIds ids, int cable, BombRiddle r, float ax, float ay, float aw, float ah, float fade)
    {
        if (r.LogicInputs == null || r.LogicGates == null) return;
        float rowH = ah / 3f, boxH = 0.55f * rowH;
        float inX = ax, inW = 0.22f * aw, gX = ax + 0.4f * aw, gW = 0.24f * aw, lampX = ax + 0.86f * aw, lampR = 0.2f * rowH;
        float Row(int k) => ay + (k + 0.5f) * rowH;
        for (int k = 0; k < 3; k++)   // inputs
        {
            canvas.DrawRect(SKRect.Create(inX, Row(k) - boxH / 2f, inW, boxH), Stroke(BombGrey, fade, 2f));
            DrawBombText(canvas, ids.Inputs[cable][k], inX, Row(k) - 0.35f * boxH, inW, 0.7f * boxH, fade, centred: true);
        }
        for (int k = 0; k < 3 && k < ids.Wires[cable].Length; k++)   // gates and their wires
        {
            var (gateId, a, b) = ids.Wires[cable][k];
            float gy = Row(k);
            var wire = Stroke(BombGrey, fade, 3f);
            canvas.DrawLine(inX + inW, Row(a), gX, gy - 0.2f * boxH, wire);
            canvas.DrawLine(inX + inW, Row(b), gX, gy + 0.2f * boxH, wire);
            canvas.DrawRect(SKRect.Create(gX, gy - boxH / 2f, gW, boxH), wire);
            DrawBombText(canvas, gateId, gX, gy - 0.35f * boxH, gW, 0.7f * boxH, fade, centred: true);
            wire = Stroke(BombGrey, fade, 3f);
            canvas.DrawLine(gX + gW, gy, lampX - lampR, gy, wire);
            canvas.DrawCircle(lampX, gy, lampR, wire);
            DrawBombText(canvas, ids.Digits[4 >> k], lampX - lampR, gy - 0.6f * lampR, 2 * lampR, 1.2f * lampR, fade, centred: true);
        }
    }

    /// <summary>An analog clock as seen in a mirror: 12 stays on top, everything else is flipped left ↔ right.</summary>
    private void DrawMirroredClock(SKCanvas canvas, BombRiddle r, float ax, float ay, float aw, float ah, float fade)
    {
        var face = new SKColor(0xED, 0xED, 0xED);
        float radius = 0.45f * Math.Min(aw, ah);
        var c = new SKPoint(ax + aw / 2f, ay + ah / 2f);
        canvas.DrawCircle(c, radius, Stroke(face, fade, Math.Max(3f, 0.04f * radius)));
        for (int tick = 0; tick < 12; tick++)
        {
            float angle = tick / 12f * 2f * MathF.PI, len = tick == 0 ? 0.25f : tick % 3 == 0 ? 0.17f : 0.09f;
            float dx = -MathF.Sin(angle), dy = -MathF.Cos(angle);   // mirrored: clockwise becomes counter-clockwise
            canvas.DrawLine(c.X + dx * radius * (1f - len), c.Y + dy * radius * (1f - len), c.X + dx * radius * 0.97f, c.Y + dy * radius * 0.97f,
                Stroke(face, fade, tick == 0 ? 0.06f * radius : 0.03f * radius));
        }
        float hourAngle = (r.ClockHour % 12 + r.ClockMinute / 60f) / 12f * 2f * MathF.PI, minuteAngle = r.ClockMinute / 60f * 2f * MathF.PI;
        canvas.DrawLine(c.X, c.Y, c.X - MathF.Sin(hourAngle) * radius * 0.5f, c.Y - MathF.Cos(hourAngle) * radius * 0.5f, Stroke(face, fade, 0.07f * radius, round: true));
        canvas.DrawLine(c.X, c.Y, c.X - MathF.Sin(minuteAngle) * radius * 0.8f, c.Y - MathF.Cos(minuteAngle) * radius * 0.8f, Stroke(face, fade, 0.04f * radius, round: true));
        canvas.DrawCircle(c, 0.05f * radius, Fill(face, fade));
    }

    private void DrawDots(SKCanvas canvas, BombRiddle r, float ax, float ay, float aw, float ah, float fade)
    {
        if (r.Dots == null || r.DotColors == null || r.DotPalette == null) return;
        float radius = 0.03f * Math.Min(aw, ah);
        for (int k = 0; k < r.DotColors.Length; k++)
            canvas.DrawCircle(ax + r.Dots[2 * k] * aw, ay + r.Dots[2 * k + 1] * ah, radius, Fill(r.DotPalette[r.DotColors[k]], fade));
    }

    // ── LED timer ───────────────────────────────────────────────────────────

    /// <summary>Width of <see cref="DrawSevenSegTimer"/>'s "m:ss" per unit of height.</summary>
    private const float SevenSegAspect = 3 * 0.55f + 3 * 0.22f + 0.25f;

    /// <summary>The countdown inside the logo's timer box (the shipped BiBaBo logo); false = no box or logo yet.</summary>
    private bool DrawTimerInLogo(SKCanvas canvas, ShowEvent evt, BombGame g, int remainingMs, SKColor color)
    {
        if (!TryGetSprite(IdsOf(evt).Logo, out _) || BombMath.LogoTimerRect(g) is not { } r) return false;
        float h = Math.Min(0.72f * r.H, 0.9f * r.W / SevenSegAspect);
        float cx = LocalX(g.BombX) + r.Dx + r.W / 2f, cy = g.BombY - _layout.OffsetY + r.Dy + r.H / 2f;
        DrawSevenSegTimer(canvas, remainingMs, cx - h * SevenSegAspect / 2f, cy - h / 2f, h, color, centred: false);
        return true;
    }

    /// <summary>"m:ss" as a 7-segment LED: unlit segments faint. (cx, top) in canvas units when centred, else left x.</summary>
    private void DrawSevenSegTimer(SKCanvas canvas, int remainingMs, float virtualOrLocalX, float top, float h, SKColor color, bool centred)
    {
        int s = (Math.Max(0, remainingMs) + 999) / 1000;
        int m = Math.Min(9, s / 60), sec = s % 60;
        float dw = 0.55f * h, gap = 0.22f * h, colon = 0.25f * h;
        float total = 3 * dw + 3 * gap + colon;
        float x = centred ? LocalX(virtualOrLocalX) - total / 2f : virtualOrLocalX;
        DrawDigit(canvas, m, x, top, dw, h, color);
        x += dw + gap;
        float t = 0.12f * h;
        var lit = Fill(color, 1f);
        canvas.DrawRect(SKRect.Create(x + (colon - t) / 2f, top + 0.3f * h, t, t), lit);
        canvas.DrawRect(SKRect.Create(x + (colon - t) / 2f, top + 0.62f * h, t, t), lit);
        x += colon + gap;
        DrawDigit(canvas, sec / 10, x, top, dw, h, color);
        DrawDigit(canvas, sec % 10, x + dw + gap, top, dw, h, color);
    }

    private void DrawDigit(SKCanvas canvas, int digit, float x, float y, float w, float h, SKColor color)
    {
        var lit = BombMath.SevenSegment(digit);
        float t = 0.12f * h, half = h / 2f;
        for (int k = 0; k < 7; k++)
        {
            var r = k switch
            {
                0 => SKRect.Create(x + t, y, w - 2 * t, t),
                1 => SKRect.Create(x + w - t, y + t, t, half - t),
                2 => SKRect.Create(x + w - t, y + half, t, half - t),
                3 => SKRect.Create(x + t, y + h - t, w - 2 * t, t),
                4 => SKRect.Create(x, y + half, t, half - t),
                5 => SKRect.Create(x, y + t, t, half - t),
                _ => SKRect.Create(x + t, y + half - t / 2f, w - 2 * t, t),
            };
            canvas.DrawRect(r, Fill(color, lit[k] ? 1f : 0.1f));
        }
    }

    /// <summary>The small timer in every seat's bottom-right corner (people walking the room keep the time in view).</summary>
    private void DrawCornerTimer(SKCanvas canvas, int remainingMs, SKColor color)
    {
        float h = 0.05f * _layout.Height, margin = 0.03f * _layout.Height;
        DrawSevenSegTimer(canvas, remainingMs, _layout.Width - margin - h * SevenSegAspect, _layout.Height - margin - h, h, color, centred: false);
    }

    // ── Outcome event ───────────────────────────────────────────────────────

    private void DrawBombOutcome(SKCanvas canvas, ShowEvent evt, long local)
    {
        var g = evt.Bomb;
        if (g == null) return;
        float black = BombMath.OutcomeBlack(evt, local);
        if (black > 0.5f) AgendaHidden = true;
        FillNode(canvas, Fill(SKColors.Black, black));
        if (g.Result == BombResult.Aborted) return;
        float fadeOut = black;

        if (g.Result is BombResult.WrongCable or BombResult.Timeout)
        {
            var (sx, sy) = BombMath.Shake(evt.Seed, local);
            canvas.Save();
            canvas.Translate(sx, sy);
            try
            {
                if (_layout.Id == g.BombNodeId && local < 600) FillNode(canvas, Fill(SKColors.White, 1f - local / 600f));
                float canvasW = _layout.CanvasWidth > 0 ? _layout.CanvasWidth : _layout.Width;
                float radius = BombMath.ShockwaveRadius(local, canvasW / 2500f);
                float ringOpacity = 1f - local / 3000f;
                // the room lights up where the wave passes
                float nodeCenter = _layout.OffsetX + _layout.Width / 2f, dist = Math.Abs(nodeCenter - g.BombX);
                if (_layout.Wraps) dist = Math.Min(dist, canvasW - dist);
                float pass = 0.5f * Math.Max(0f, 1f - Math.Abs(radius - dist) / 500f);
                if (pass > 0.005f) FillNode(canvas, Fill(BombOrange, pass));
                if (ringOpacity > 0.005f)
                {
                    var ring = Stroke(BombOrange, ringOpacity, 60f);
                    int copies = _layout.Wraps ? 3 : 1;
                    for (int c = 0; c < copies; c++)
                    {
                        float shift = c == 0 ? 0 : c == 1 ? canvasW : -canvasW;
                        canvas.DrawCircle(LocalX(g.BombX + shift), g.BombY - _layout.OffsetY, radius, ring);
                    }
                }
                if (_layout.Id == g.BombNodeId) DrawOutcomeAnimation(canvas, evt, g, local, holdLast: false);
                DrawCelebration(canvas, evt, local, FirePalette);
                if (local >= 300)
                    DrawBombText(canvas, IdsOf(evt).Text, 0.1f * _layout.Width, 0.35f * _layout.Height, 0.8f * _layout.Width, 0.3f * _layout.Height, fadeOut, centred: true);
            }
            finally
            {
                canvas.Restore();
            }
            return;
        }

        // Defused / Recycled: grey cables, the bomb (shrinking into the bin when recycled), frozen green timer, confetti.
        foreach (var c in g.Cables) DrawCable(canvas, c.Points, c.Points.Length / 2, 0, int.MaxValue, "#616161", fadeOut, CableWidth);
        if (_layout.Id == g.BombNodeId)
        {
            bool recycled = g.Result == BombResult.Recycled;
            float scale = recycled ? Math.Max(0f, 1f - local / 1200f) : 1f;
            if (!recycled && DrawOutcomeAnimation(canvas, evt, g, local, holdLast: true))
            {
                if (OnOutcomeFirstFrame(evt, local)) DrawTimerInLogo(canvas, evt, g, g.RemainingMs, BombTimerGreen);
            }
            else
            {
                DrawBombBody(canvas, evt, g, local, 0f, scale, recycled ? local * 0.4f : 0f);
                if (recycled || !DrawTimerInLogo(canvas, evt, g, g.RemainingMs, BombTimerGreen))
                    DrawSevenSegTimer(canvas, g.RemainingMs, g.BombX, g.BombY - _layout.OffsetY + BombMath.TimerAboveTop * g.BombSize, BombMath.TimerAboveHeight * g.BombSize, BombTimerGreen, centred: true);
            }
        }
        DrawCornerTimer(canvas, g.RemainingMs, BombTimerGreen);
        DrawCelebration(canvas, evt, local, null);
        // below the bomb (the frozen timer sits above it)
        DrawBombText(canvas, IdsOf(evt).Text, 0.1f * _layout.Width, 0.74f * _layout.Height, 0.8f * _layout.Width, 0.12f * _layout.Height, fadeOut, centred: true);
    }
}

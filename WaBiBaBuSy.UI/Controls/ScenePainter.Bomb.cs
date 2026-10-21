// WaBiBaBuSy.UI/Controls/ScenePainter.Bomb.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>
/// BiBaBo in the room view (design 2026-10-21-bibabo-bomb-game-design.md §7): the same BombMath as the players, drawn
/// simpler — text instead of LED segments, no particles. Never shows which cable is live (the server keeps that).
/// </summary>
public sealed partial class ScenePainter
{
    private static readonly IBrush BombRed = new SolidColorBrush(Color.FromRgb(0xFF, 0x17, 0x44));
    private static readonly IBrush BombGreen = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
    private static readonly IBrush BombOrange = new SolidColorBrush(Color.FromRgb(0xFF, 0x6D, 0x00));
    private static readonly IBrush BombCardBrush = new SolidColorBrush(Color.FromArgb(242, 0x1A, 0x1A, 0x1A));
    private static readonly IBrush BombBodyBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));

    /// <summary>True while a BiBaBo game or its outcome covers the room (the agenda hides under it).</summary>
    public static bool BombActive(IReadOnlyList<ShowEvent>? events, long nowUtcMs)
    {
        if (events == null) return false;
        foreach (var e in events)
            if (e.Type is ShowEventType.Bomb or ShowEventType.BombOutcome && nowUtcMs >= e.T0Utc && !ShowEventMath.Ended(e, nowUtcMs)) return true;
        return false;
    }

    private void PaintBomb(DrawingContext ctx, Rect rect, double s, NodeLayout node, ShowEvent evt, long local, IReadOnlyList<ShowEvent> events)
    {
        var g = evt.Bomb;
        if (g == null) return;
        foreach (var e in events)
            if (e.Type == ShowEventType.BombOutcome && e.Bomb?.GameId == evt.EventId && local + evt.T0Utc - e.T0Utc >= 0) return;

        ctx.FillRectangle(new SolidColorBrush(Colors.Black, BombMath.BlackOpacity(local)), rect);
        if (BombMath.BlackOpacity(local) < 0.999f) return;
        if (BombMath.RouletteOrder(g, local) == node.Order)
            ctx.DrawRectangle(new SolidColorBrush(Colors.White, 0.15), new Pen(Brushes.White, Math.Max(1, 12 * s)), rect.Deflate(6 * s));

        float progress = BombMath.CableProgress(g, local);
        if (progress > 0)
            foreach (var c in g.Cables)
                PaintCable(ctx, rect, s, node, c.Points, BombMath.PrefixPointCount(c.Points, progress), EventBrush(c.ColorHex, 1));

        if (node.Id == g.BombNodeId && local >= BombMath.RouletteEndMs)
        {
            double dy = BombMath.DropOffset(local) * (g.BombY - node.OffsetY + g.BombSize);
            var center = new Point(rect.X + NodeMapping.ToLocalX(g.BombX, 0, node) * s, rect.Y + (g.BombY - node.OffsetY + dy) * s);
            PaintBombBody(ctx, center, g.BombSize * s, evt.ImagePath);
        }
        if (local >= BombMath.DropEndMs)
        {
            int remaining = BombMath.RemainingMs(g, local);
            if (BombMath.TimerVisible(g, local)) PaintBombTimer(ctx, rect, s, node, g, remaining, BombRed);
        }
        if (local >= g.IntroMs)
            foreach (var c in g.Cables)
                if (c.EndNodeId == node.Id) PaintBombCard(ctx, rect, s, node, c);
    }

    private void PaintBombOutcome(DrawingContext ctx, Rect rect, double s, NodeLayout node, ShowEvent evt, long local)
    {
        var g = evt.Bomb;
        if (g == null) return;
        ctx.FillRectangle(new SolidColorBrush(Colors.Black, BombMath.OutcomeBlack(evt, local)), rect);
        if (g.Result == BombResult.Aborted) return;
        bool boom = g.Result is BombResult.WrongCable or BombResult.Timeout;
        if (boom)
        {
            float canvasW = node.CanvasWidth > 0 ? node.CanvasWidth : node.Width;
            double radius = BombMath.ShockwaveRadius(local, canvasW / 2500f) * s;
            double ring = 1 - local / 3000.0;
            if (ring > 0)
            {
                var c = new Point(rect.X + NodeMapping.ToLocalX(g.BombX, 0, node) * s, rect.Y + (g.BombY - node.OffsetY) * s);
                ctx.DrawEllipse(null, new Pen(EventBrush("#FF6D00", ring), Math.Max(1, 60 * s)), c, radius, radius);
            }
        }
        else
        {
            foreach (var c in g.Cables) PaintCable(ctx, rect, s, node, c.Points, c.Points.Length / 2, EventBrush("#616161", 1));
            if (node.Id == g.BombNodeId && g.Result == BombResult.Defused)
            {
                var center = new Point(rect.X + NodeMapping.ToLocalX(g.BombX, 0, node) * s, rect.Y + (g.BombY - node.OffsetY) * s);
                PaintBombBody(ctx, center, g.BombSize * s, evt.ImagePath);
                PaintBombTimer(ctx, rect, s, node, g, g.RemainingMs, BombGreen);
            }
        }
        string text = g.Result switch
        {
            BombResult.Defused => $"DEFUSED — {BombMath.FormatTimer(g.RemainingMs)} left",
            BombResult.Recycled => "RECYCLED ♻",
            _ => "BOOM!",
        };
        var box = boom ? new Rect(rect.X + rect.Width * 0.1, rect.Y + rect.Height * 0.35, rect.Width * 0.8, rect.Height * 0.3)
                       : new Rect(rect.X + rect.Width * 0.1, rect.Y + rect.Height * 0.1, rect.Width * 0.8, rect.Height * 0.14);
        PaintFittedText(ctx, box, text, boom ? BombOrange : BombGreen);
    }

    private static void PaintCable(DrawingContext ctx, Rect rect, double s, NodeLayout node, float[] points, int count, IBrush brush)
    {
        if (count < 2) return;
        Span<(int I, float Shift)> segments = count * 3 <= 4096 ? stackalloc (int, float)[count * 3] : new (int, float)[count * 3];
        int n = BombMath.SegmentsOnNode(points, count, node, 10f, segments);
        var pen = new Pen(brush, Math.Max(1, 10 * s), lineCap: PenLineCap.Round);
        for (int k = 0; k < n; k++)
        {
            var (i, shift) = segments[k];
            var a = new Point(rect.X + NodeMapping.ToLocalX(points[2 * i] + shift, 0, node) * s, rect.Y + (points[2 * i + 1] - node.OffsetY) * s);
            var b = new Point(rect.X + NodeMapping.ToLocalX(points[2 * i + 2] + shift, 0, node) * s, rect.Y + (points[2 * i + 3] - node.OffsetY) * s);
            ctx.DrawLine(pen, a, b);
        }
    }

    private void PaintBombBody(DrawingContext ctx, Point center, double size, string? logoPath)
    {
        if (EventImage(logoPath) is { } logo)
        {
            double k = Math.Min(size / logo.Size.Width, size / logo.Size.Height);
            ctx.DrawImage(logo, new Rect(logo.Size), new Rect(center.X - logo.Size.Width * k / 2, center.Y - logo.Size.Height * k / 2, logo.Size.Width * k, logo.Size.Height * k));
            return;
        }
        double r = size * 0.42;
        ctx.DrawEllipse(BombBodyBrush, new Pen(Brushes.Gray, Math.Max(1, size * 0.02)), center, r, r);
        ctx.DrawLine(new Pen(EventBrush("#8D6E63", 1), Math.Max(1, size * 0.03)), new Point(center.X + r * 0.55, center.Y - r * 0.8), new Point(center.X + r * 0.95, center.Y - r * 1.25));
    }

    private static void PaintBombTimer(DrawingContext ctx, Rect rect, double s, NodeLayout node, BombGame g, int remainingMs, IBrush brush)
    {
        if (node.Id == g.BombNodeId)
        {
            double h = 0.4 * g.BombSize * s, w = 2.2 * h;
            double cx = rect.X + NodeMapping.ToLocalX(g.BombX, 0, node) * s, top = rect.Y + (g.BombY - node.OffsetY - 1.05 * g.BombSize) * s;
            PaintFittedText(ctx, new Rect(cx - w / 2, top, w, h), BombMath.FormatTimer(remainingMs), brush);
        }
        double ch = 0.05 * node.Height * s, cw = 2.2 * ch, m = 0.03 * node.Height * s;
        PaintFittedText(ctx, new Rect(rect.Right - m - cw, rect.Bottom - m - ch, cw, ch), BombMath.FormatTimer(remainingMs), brush);
    }

    private static void PaintBombCard(DrawingContext ctx, Rect rect, double s, NodeLayout node, BombCable c)
    {
        var card = new Rect(rect.X + NodeMapping.ToLocalX(c.CardX, c.CardW, node) * s, rect.Y + (c.CardY - node.OffsetY) * s, c.CardW * s, c.CardH * s);
        var color = EventBrush(c.ColorHex, 1);
        ctx.DrawRectangle(BombCardBrush, new Pen(color, Math.Max(1, 6 * s)), card);
        ctx.FillRectangle(color, card.WithHeight(card.Height * 0.1));
        if (c.Riddle is { Kind: RiddleKind.Math, Formula: { } formula })
        {
            PaintFittedText(ctx, new Rect(card.X + card.Width * 0.06, card.Y + card.Height * 0.25, card.Width * 0.88, card.Height * 0.3), formula, Brushes.White);
            return;
        }
        if (c.Riddle.Strands is not { } strands) return;
        var grey = new Pen(Brushes.Gray, Math.Max(1, 5 * s));
        var area = new Rect(card.X + card.Width * 0.04, card.Y + card.Height * 0.16, card.Width * 0.66, card.Height * 0.78);
        foreach (var strand in strands)
            for (int k = 0; k + 1 < strand.Length; k++)
            {
                double t0 = 0.05 + 0.9 * k / (strand.Length - 1), t1 = 0.05 + 0.9 * (k + 1) / (strand.Length - 1);
                ctx.DrawLine(grey, new Point(area.X + t0 * area.Width, area.Y + strand[k] * area.Height),
                                   new Point(area.X + t1 * area.Width, area.Y + strand[k + 1] * area.Height));
            }
    }

    /// <summary>Text scaled into a box (keeps its aspect, centred).</summary>
    private static void PaintFittedText(DrawingContext ctx, Rect box, string text, IBrush brush)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), 100, brush);
        if (ft.Width <= 0 || ft.Height <= 0) return;
        double k = Math.Min(box.Width / ft.Width, box.Height / ft.Height);
        var m = Matrix.CreateScale(k, k) * Matrix.CreateTranslation(box.X + (box.Width - ft.Width * k) / 2, box.Y + (box.Height - ft.Height * k) / 2);
        using (ctx.PushTransform(m))
            ctx.DrawText(ft, new Point(0, 0));
    }
}

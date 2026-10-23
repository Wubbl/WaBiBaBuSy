// WaBiBaBuSy.UI/Controls/ScenePainter.Events.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>Live show events over a node tile, with the players' own math (roadmap 2.4).</summary>
public sealed partial class ScenePainter
{
    /// <summary>Confetti pieces drawn per tile at most (the preview is small; the players draw all).</summary>
    private const int MaxPreviewParticles = 150;

    private CelebrationParticle[] _particles = new CelebrationParticle[256];
    private readonly Dictionary<string, Bitmap?> _eventImages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Draws the active events of <paramref name="events"/> on one node (clipped to <paramref name="rect"/>), in the players' layer order.</summary>
    public void PaintShowEvents(DrawingContext ctx, Rect rect, double s, NodeLayout node, IReadOnlyList<ShowEvent> events,
        long sceneStartUtcMs, long sceneRawElapsedMs, bool sharedCanvas)
    {
        using var clip = ctx.PushClip(rect);
        for (int layer = 0; layer <= 3; layer++)
        {
            foreach (var evt in events)
            {
                int l = evt.Type switch
                {
                    ShowEventType.Spotlight => 1,
                    ShowEventType.Blackout => 2,
                    ShowEventType.Bomb or ShowEventType.BombOutcome => 3,
                    _ => 0,
                };
                if (l != layer || !evt.IsFor(node.Id)) continue;
                long local = ShowEventMath.LocalMs(evt, sceneStartUtcMs, sceneRawElapsedMs);
                if (!ShowEventMath.IsActive(evt, local)) continue;
                switch (evt.Type)
                {
                    case ShowEventType.Blackout:
                        ctx.FillRectangle(EventBrush(evt.ColorHex, ShowEventMath.BlackoutOpacity(evt, local)), rect);
                        break;
                    case ShowEventType.Spotlight:
                        PaintSpotlight(ctx, rect, s, node, evt, local, sharedCanvas);
                        break;
                    case ShowEventType.Celebration:
                        PaintCelebration(ctx, rect, s, node, evt, local);
                        break;
                    case ShowEventType.Traversal:
                    case ShowEventType.Announce:
                        PaintEventSprite(ctx, rect, s, node, evt, local);
                        break;
                    case ShowEventType.Bomb:
                        PaintBomb(ctx, rect, s, node, evt, local, events);
                        break;
                    case ShowEventType.BombOutcome:
                        PaintBombOutcome(ctx, rect, s, node, evt, local);
                        break;
                }
            }
        }
    }

    private static IBrush EventBrush(string? hex, double opacity) => new SolidColorBrush(ParseHex(hex), Math.Clamp(opacity, 0, 1));

    private static void PaintSpotlight(DrawingContext ctx, Rect rect, double s, NodeLayout node, ShowEvent evt, long local, bool sharedCanvas)
    {
        var v = SpotlightMath.Visual(evt, node, sharedCanvas, local);
        if (v.IsTarget)
        {
            double w = v.BorderWidth * s;
            ctx.DrawRectangle(null, new Pen(EventBrush(evt.ColorHex, v.BorderOpacity), w), rect.Deflate(w / 2));
            return;
        }
        if (v.DimOpacity > 0) ctx.FillRectangle(new SolidColorBrush(Colors.Black, v.DimOpacity), rect);
        if (v.ChevronDirection == 0) return;
        double half = v.ChevronSize / 2 * s, d = v.ChevronDirection;
        double cx = rect.X + v.ChevronCenterX * s, cy = rect.Y + v.ChevronCenterY * s;
        var pen = new Pen(EventBrush(evt.ColorHex, v.ChevronOpacity), v.ChevronSize * 0.18 * s);
        ctx.DrawLine(pen, new Point(cx - d * half, cy - half), new Point(cx + d * half, cy));
        ctx.DrawLine(pen, new Point(cx - d * half, cy + half), new Point(cx + d * half, cy));
    }

    private void PaintCelebration(DrawingContext ctx, Rect rect, double s, NodeLayout node, ShowEvent evt, long local)
    {
        int count = CelebrationMath.Count(node.CanvasWidth > 0 ? node.CanvasWidth : node.Width);
        int capacity = CelebrationMath.Capacity(node, count);
        if (_particles.Length < capacity) _particles = new CelebrationParticle[capacity];
        int n = CelebrationMath.VisibleOnNode(evt, node, local, _particles);
        double env = ShowEventMath.Envelope(local, evt.DurationMs);
        int step = Math.Max(1, n / MaxPreviewParticles);
        for (int i = 0; i < n; i += step)
        {
            var p = _particles[i];
            var box = new Rect(rect.X + p.X * s, rect.Y + p.Y * s, Math.Max(1, p.Width * s), Math.Max(1, p.Height * s));
            var c = box.Center;
            var m = Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateRotation(p.RotationDeg * Math.PI / 180) * Matrix.CreateTranslation(c.X, c.Y);
            using (ctx.PushTransform(m))
                ctx.FillRectangle(EventBrush(CelebrationMath.ColorFor(evt, p.ColorIndex), env), box);
        }
    }

    private void PaintEventSprite(DrawingContext ctx, Rect rect, double s, NodeLayout node, ShowEvent evt, long local)
    {
        int cw = node.CanvasWidth > 0 ? node.CanvasWidth : node.Width;
        int ch = node.CanvasHeight > 0 ? node.CanvasHeight : node.Height;
        if (TraversalMath.Pose(evt, cw, ch, node.Wraps, local) is not { } p) return;
        Span<float> copies = stackalloc float[2];
        int n = NodeMapping.WrapCopies(p.VirtualX, p.Width, node, copies);
        double y = rect.Y + NodeMapping.ToLocalY(p.VirtualY, node) * s;
        using var opacity = ctx.PushOpacity(p.Opacity);
        for (int c = 0; c < n; c++)
        {
            float lx = NodeMapping.ToLocalX(copies[c], p.Width, node);
            if (!NodeMapping.IsVisible(lx, p.Width, node)) continue;
            var dest = new Rect(rect.X + lx * s, y, p.Width * s, p.Height * s);
            if (evt.Type == ShowEventType.Announce) PaintAnnounceText(ctx, dest, evt);
            else if (EventImage(evt.ImagePath) is { } bmp) ctx.DrawImage(bmp, new Rect(bmp.Size), dest);
            else ctx.DrawRectangle(EventBrush(evt.ColorHex, 0.5), null, dest);   // image not readable here: a placeholder box
        }
    }

    /// <summary>Announce text scaled into its box (same box the players draw their text bitmap into).</summary>
    private static void PaintAnnounceText(DrawingContext ctx, Rect dest, ShowEvent evt)
    {
        var ft = new FormattedText(evt.Text ?? string.Empty, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), 100, EventBrush(evt.ColorHex, 1));
        if (ft.Width <= 0 || ft.Height <= 0) return;
        var m = Matrix.CreateScale(dest.Width / ft.Width, dest.Height / ft.Height) * Matrix.CreateTranslation(dest.X, dest.Y);
        using (ctx.PushTransform(m))
            ctx.DrawText(ft, new Point(0, 0));
    }

    private Bitmap? EventImage(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_eventImages.TryGetValue(path, out var cached)) return cached;
        Bitmap? bmp = null;
        try { if (File.Exists(path)) bmp = new Bitmap(path); }
        catch (Exception) { bmp = null; }
        if (_eventImages.Count > 8) _eventImages.Clear();
        _eventImages[path] = bmp;
        return bmp;
    }
}

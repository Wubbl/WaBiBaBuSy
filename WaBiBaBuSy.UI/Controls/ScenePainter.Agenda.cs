// WaBiBaBuSy.UI/Controls/ScenePainter.Agenda.cs
using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>The room agenda over a node tile, with the players' own rows and layout (design 2026-10-21-agenda-overlay-design.md).</summary>
public sealed partial class ScenePainter
{
    private static readonly IBrush AgendaCardBrush = new SolidColorBrush(Color.FromArgb(217, 0x14, 0x14, 0x14));   // 85 %
    private static readonly IBrush AgendaDimBrush = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0));               // 60 %
    private static readonly IBrush AgendaHighlightBrush = new SolidColorBrush(Color.FromArgb(64, 0xFF, 0xD7, 0x00)); // 25 %
    private static readonly IBrush AgendaGoldBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly Typeface AgendaFont = new("Segoe UI", FontStyle.Normal, FontWeight.Bold);

    /// <summary>
    /// The agenda on one monitor of <paramref name="monitorW"/> × <paramref name="monitorH"/> px drawn into <paramref name="screen"/>:
    /// the corner panel, or the full-size timetable when <paramref name="nodeId"/> is an agenda screen.
    /// </summary>
    public void PaintAgenda(DrawingContext ctx, Rect screen, double monitorW, double monitorH, string nodeId, Agenda agenda, long nowUtcMs)
    {
        if (!agenda.Enabled || monitorW <= 0 || monitorH <= 0) return;
        double s = screen.Width / monitorW;
        using var clip = ctx.PushClip(screen);
        var notes = agenda.Notes.Take(3).ToList();
        Rect R(AgendaRect r) => new(screen.X + r.X * s, screen.Y + r.Y * s, r.W * s, r.H * s);

        if (agenda.IsScreen(new[] { nodeId }))
        {
            var days = AgendaMath.Days(agenda, nowUtcMs);
            int slots = 1 + Math.Min(14, days.Count == 0 ? 1 : days.Max(d => d.Rows.Count));
            var layout = AgendaLayout.Screen((float)monitorW, (float)monitorH, Math.Max(1, days.Count), slots, notes.Count);
            ctx.FillRectangle(AgendaDimBrush, screen);
            if (!string.IsNullOrWhiteSpace(agenda.Title)) AgendaText(ctx, R(layout.Title), agenda.Title.Trim(), AgendaGoldBrush, 1, right: false);
            for (int c = 0; c < days.Count; c++)
            {
                AgendaText(ctx, R(layout.Rows[c * slots]), days[c].Day, AgendaGoldBrush, 1, right: false);
                for (int r = 0; r < days[c].Rows.Count && r < slots - 1; r++)
                    AgendaRow(ctx, R(layout.Rows[c * slots + 1 + r]), days[c].Rows[r], withDay: false);
            }
            for (int i = 0; i < notes.Count; i++) AgendaText(ctx, R(layout.Notes[i]), notes[i], Brushes.White, 0.75, right: false);
            return;
        }

        var rows = AgendaMath.Rows(agenda, nowUtcMs);
        bool hasTitle = !string.IsNullOrWhiteSpace(agenda.Title);
        var panel = AgendaLayout.Panel((float)monitorW, (float)monitorH, agenda.Corner, hasTitle, Math.Max(1, rows.Count), notes.Count);
        ctx.FillRectangle(AgendaCardBrush, R(panel.Card));
        if (hasTitle) AgendaText(ctx, R(panel.Title), agenda.Title.Trim(), AgendaGoldBrush, 1, right: false);
        if (rows.Count == 0) AgendaText(ctx, R(panel.Rows[0]), "No items yet", Brushes.White, 0.5, right: false);
        for (int i = 0; i < rows.Count; i++) AgendaRow(ctx, R(panel.Rows[i]), rows[i], withDay: rows[i].DayHeader);
        for (int i = 0; i < notes.Count; i++) AgendaText(ctx, R(panel.Notes[i]), notes[i], Brushes.White, 0.75, right: false);
    }

    /// <summary>Same row as the players draw: highlight, "Sat 18:00  Pizza" left, countdown / "now" right.</summary>
    private static void AgendaRow(DrawingContext ctx, Rect rect, AgendaRow row, bool withDay)
    {
        if (row.State == AgendaRowState.Current || row.Pulse) ctx.FillRectangle(AgendaHighlightBrush, rect);
        double opacity = row.State == AgendaRowState.Past ? 0.5 : 1;
        string right = row.State == AgendaRowState.Current ? "now" : row.Countdown ?? "";
        string left = withDay ? $"{row.Day}  {row.Time}  {row.Text}" : $"{row.Time}  {row.Text}";
        AgendaText(ctx, right.Length > 0 ? rect.WithWidth(rect.Width * 0.72) : rect, left, Brushes.White, opacity, right: false);
        if (right.Length > 0)
            AgendaText(ctx, new Rect(rect.X + rect.Width * 0.74, rect.Y, rect.Width * 0.26, rect.Height), right,
                row.State == AgendaRowState.Current ? AgendaGoldBrush : Brushes.White, opacity, right: true);
    }

    /// <summary>Text at 80 % of the box height, squeezed horizontally when too long (as the players draw their sprites).</summary>
    private static void AgendaText(DrawingContext ctx, Rect box, string text, IBrush brush, double opacity, bool right)
    {
        if (box.Height <= 0 || string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, AgendaFont, 100, brush);
        if (ft.Width <= 0 || ft.Height <= 0) return;
        double h = box.Height * 0.8, w = Math.Min(h * ft.Width / ft.Height, box.Width);
        double x = right ? box.Right - w : box.X;
        var m = Matrix.CreateScale(w / ft.Width, h / ft.Height) * Matrix.CreateTranslation(x, box.Y + (box.Height - h) / 2);
        using (ctx.PushOpacity(opacity))
        using (ctx.PushTransform(m))
            ctx.DrawText(ft, new Point(0, 0));
    }
}

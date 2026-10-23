using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Where the agenda card and its rows sit on a screen.</summary>
public class AgendaLayoutTests
{
    private static void Inside(AgendaRect r, AgendaRect outer)
    {
        Assert.True(r.X >= outer.X - 0.01f && r.Y >= outer.Y - 0.01f, $"{r} starts outside {outer}");
        Assert.True(r.X + r.W <= outer.X + outer.W + 0.01f && r.Y + r.H <= outer.Y + outer.H + 0.01f, $"{r} ends outside {outer}");
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 1024)]
    [InlineData(3840, 2160)]
    public void Panel_StaysInsideScreen_AllCorners(int w, int h)
    {
        var screen = new AgendaRect(0, 0, w, h);
        foreach (var corner in Enum.GetValues<AgendaCorner>())
        {
            var p = AgendaLayout.Panel(w, h, corner, hasTitle: true, rows: 6, noteLines: 2);
            Inside(p.Card, screen);
            Assert.True(p.Card.H <= 0.3f * h + 0.01f);
        }
        var tr = AgendaLayout.Panel(w, h, AgendaCorner.TopRight, true, 3, 0).Card;
        Assert.True(tr.X + tr.W / 2 > w / 2f && tr.Y < h / 2f);
        var bl = AgendaLayout.Panel(w, h, AgendaCorner.BottomLeft, true, 3, 0).Card;
        Assert.True(bl.X + bl.W / 2 < w / 2f && bl.Y > h / 2f);
    }

    [Fact]
    public void Panel_RowsAreStackedInsideCard()
    {
        var p = AgendaLayout.Panel(1920, 1080, AgendaCorner.TopRight, hasTitle: true, rows: 4, noteLines: 1);
        Assert.Equal(4, p.Rows.Count);
        Assert.Single(p.Notes);
        Inside(p.Title, p.Card);
        foreach (var r in p.Rows.Concat(p.Notes)) Inside(r, p.Card);
        Assert.True(p.Title.Y + p.Title.H <= p.Rows[0].Y + 0.01f);
        for (int i = 1; i < p.Rows.Count; i++) Assert.True(p.Rows[i - 1].Y + p.Rows[i - 1].H <= p.Rows[i].Y + 0.01f);
        Assert.True(p.Rows[^1].Y + p.Rows[^1].H <= p.Notes[0].Y + 0.01f);
    }

    [Fact]
    public void Panel_NoTitle_HasEmptyTitleRect()
    {
        var p = AgendaLayout.Panel(1920, 1080, AgendaCorner.TopLeft, hasTitle: false, rows: 2, noteLines: 0);
        Assert.Equal(0, p.Title.H);
    }

    [Fact]
    public void Screen_OneColumnPerDay()
    {
        var p = AgendaLayout.Screen(1920, 1080, dayColumns: 3, maxRowsPerDay: 5, noteLines: 2);
        Assert.Equal(15, p.Rows.Count);
        var screen = new AgendaRect(0, 0, 1920, 1080);
        foreach (var r in p.Rows.Concat(p.Notes)) Inside(r, screen);
        // column-major: rows 0..4 day 0, 5..9 day 1 — columns left to right
        Assert.True(p.Rows[5].X > p.Rows[0].X);
        Assert.True(p.Rows[10].X > p.Rows[5].X);
        Assert.Equal(p.Rows[0].X, p.Rows[4].X);
    }
}

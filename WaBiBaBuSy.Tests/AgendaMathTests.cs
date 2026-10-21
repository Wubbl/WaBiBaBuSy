using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Agenda "now / next": which rows the corner panel shows, countdowns, pulse, day headers (offset 0 = labels in UTC).</summary>
public class AgendaMathTests
{
    private static readonly DateTimeOffset Fri = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);   // a Friday

    private static long At(int day, int h, int m = 0, int s = 0) => Fri.AddDays(day).AddHours(h).AddMinutes(m).AddSeconds(s).ToUnixTimeMilliseconds();

    private static Agenda Make(params (int Day, int H, int M, string Text)[] items) => new()
    {
        Enabled = true,
        Items = items.Select(i => new AgendaItem { StartUtcMs = At(i.Day, i.H, i.M), Text = i.Text }).ToList(),
    };

    private static Agenda FourItems() => Make((0, 10, 0, "A"), (0, 12, 0, "B"), (0, 14, 0, "C"), (0, 16, 0, "D"));

    [Fact]
    public void Rows_MarksCurrentAndNext_KeepsLastPast()
    {
        var rows = AgendaMath.Rows(FourItems(), At(0, 12, 30));
        Assert.Equal(new[] { "A", "B", "C", "D" }, rows.Select(r => r.Text));
        Assert.Equal(new[] { AgendaRowState.Past, AgendaRowState.Current, AgendaRowState.Next, AgendaRowState.Upcoming }, rows.Select(r => r.State));
        Assert.Equal("in 1:30 h", rows[2].Countdown);
        Assert.Null(rows[0].Countdown);
        Assert.Null(rows[1].Countdown);
        Assert.Null(rows[3].Countdown);
        Assert.Equal("12:00", rows[1].Time);
    }

    [Fact]
    public void Rows_WindowStartsAtTheLastPastItem()
    {
        var rows = AgendaMath.Rows(FourItems(), At(0, 14, 30));
        Assert.Equal(new[] { "B", "C", "D" }, rows.Select(r => r.Text));
    }

    [Fact]
    public void Rows_BeforeFirstItem_StartsAtFirst_NoCurrent()
    {
        var rows = AgendaMath.Rows(FourItems(), At(0, 9));
        Assert.Equal(AgendaRowState.Next, rows[0].State);
        Assert.DoesNotContain(rows, r => r.State == AgendaRowState.Current);
        Assert.Equal("in 1:00 h", rows[0].Countdown);
        Assert.Equal("in 30 min", AgendaMath.Rows(FourItems(), At(0, 9, 30))[0].Countdown);
    }

    [Fact]
    public void Rows_AfterLastItem_LastIsCurrent()
    {
        var rows = AgendaMath.Rows(FourItems(), At(0, 23));
        Assert.Equal(new[] { AgendaRowState.Past, AgendaRowState.Current }, rows.Select(r => r.State));
        Assert.All(rows, r => Assert.Null(r.Countdown));
    }

    [Fact]
    public void Rows_EmptyAgenda_ReturnsEmpty() => Assert.Empty(AgendaMath.Rows(new Agenda { Enabled = true }, At(0, 12)));

    [Fact]
    public void Rows_CapsAtSix()
    {
        var a = Make(Enumerable.Range(0, 10).Select(i => (0, 8 + i, 0, $"I{i}")).ToArray());
        Assert.Equal(6, AgendaMath.Rows(a, At(0, 7)).Count);
    }

    [Fact]
    public void Rows_PulseOnlyWithinTenMinutes()
    {
        // 11:51: A current (row 0), B next (row 1) starts in 9 min
        Assert.True(AgendaMath.Rows(FourItems(), At(0, 11, 51))[1].Pulse);
        Assert.False(AgendaMath.Rows(FourItems(), At(0, 11, 49))[1].Pulse);
        Assert.False(AgendaMath.Rows(FourItems(), At(0, 11, 51))[0].Pulse);
    }

    [Fact]
    public void Rows_DayHeaderOnDayChange()
    {
        var rows = AgendaMath.Rows(Make((0, 22, 0, "Party"), (1, 10, 0, "Breakfast")), At(0, 21));
        Assert.True(rows[0].DayHeader);
        Assert.Equal("Fri", rows[0].Day);
        Assert.True(rows[1].DayHeader);
        Assert.Equal("Sat", rows[1].Day);
        var same = AgendaMath.Rows(FourItems(), At(0, 9));
        Assert.True(same[0].DayHeader);
        Assert.False(same[1].DayHeader);
    }

    [Fact]
    public void Countdown_RoundsUpMinutes()
    {
        long start = At(0, 12);
        Assert.Equal("in 23 min", AgendaMath.Countdown(start - (22 * 60 + 10) * 1000L, start));
        Assert.Equal("in 2:05 h", AgendaMath.Countdown(start - (2 * 60 + 5) * 60_000L, start));
        Assert.Null(AgendaMath.Countdown(start, start));
        Assert.Null(AgendaMath.Countdown(start + 1, start));
    }

    [Fact]
    public void Days_GroupsByLocalDay()
    {
        var a = Make((0, 20, 0, "Dinner"), (0, 23, 30, "Late"));
        a.UtcOffsetMinutes = 120;   // 23:30 UTC Fri = 01:30 Sat local
        var days = AgendaMath.Days(a, At(0, 12));
        Assert.Equal(new[] { "Fri", "Sat" }, days.Select(d => d.Day));
        Assert.Equal("01:30", days[1].Rows[0].Time);
        Assert.Equal("22:00", days[0].Rows[0].Time);
    }

    [Fact]
    public void ShiftTimes_MovesEveryItem()
    {
        var a = FourItems();
        var before = a.Items.Select(i => i.StartUtcMs).ToList();
        AgendaMath.ShiftTimes(a, -90_000);
        Assert.Equal(before.Select(t => t - 90_000), a.Items.Select(i => i.StartUtcMs));
    }

    [Fact]
    public void IsScreen_MatchesAnyCandidate()
    {
        var a = new Agenda { ScreenNodeIds = { "client-7_MONITOR_1" } };
        Assert.True(a.IsScreen(new[] { "client-7", "client-7_MONITOR_1" }));
        Assert.False(a.IsScreen(new[] { "client-7", "client-7_MONITOR_0" }));
    }
}

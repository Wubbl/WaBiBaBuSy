using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>The Agenda tab's text ("Sat 18:00 Pizza" per line) → dated items.</summary>
public class AgendaTextTests
{
    private static readonly DateOnly FirstDay = new(2026, 10, 23);   // Friday

    private static DateTime Utc(AgendaItem i) => DateTimeOffset.FromUnixTimeMilliseconds(i.StartUtcMs).UtcDateTime;

    private static List<AgendaItem> Parse(string text) => AgendaText.Parse(text, FirstDay, TimeZoneInfo.Utc);

    [Fact]
    public void Parse_DayPrefixes_ResolveFromFirstDay()
    {
        var items = Parse("Sat 18:00 Pizza\nFri 20:00 Setup");
        Assert.Equal(2, items.Count);
        Assert.Equal("Setup", items[0].Text);
        Assert.Equal(new DateTime(2026, 10, 23, 20, 0, 0), Utc(items[0]));
        Assert.Equal("Pizza", items[1].Text);
        Assert.Equal(new DateTime(2026, 10, 24, 18, 0, 0), Utc(items[1]));
    }

    [Fact]
    public void Parse_NoDay_FollowsPreviousAndRollsPastMidnight()
    {
        var items = Parse("Fri 23:00 Movie\n01:00 Sleep");
        Assert.Equal(new DateTime(2026, 10, 24, 1, 0, 0), Utc(items[1]));
        Assert.Equal(new DateTime(2026, 10, 23, 18, 0, 0), Utc(Parse("18:00 Arrival")[0]));   // first line without a day = first day
    }

    [Fact]
    public void Parse_GermanDays_AndSingleDigitHours()
    {
        var items = Parse("Sa 9:30 Frühstück\nsunday 12:00 Cleanup");
        Assert.Equal(new DateTime(2026, 10, 24, 9, 30, 0), Utc(items[0]));
        Assert.Equal("Frühstück", items[0].Text);
        Assert.Equal(new DateTime(2026, 10, 25, 12, 0, 0), Utc(items[1]));
    }

    [Fact]
    public void Parse_SkipsJunkLines_SortsResult()
    {
        var items = Parse("hello world\n\n  \nSat 25:00 Nope\n14:00 Lunch\n12:00 Brunch");
        Assert.Equal(new[] { "Lunch", "Brunch" }.Length, items.Count);
        Assert.True(items[0].StartUtcMs < items[1].StartUtcMs);
        Assert.Equal("Lunch", items[0].Text);   // 14:00 Fri; 12:00 after it rolls to Sat
    }

    [Fact]
    public void Parse_UsesTheTimeZone()
    {
        var vienna = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var item = Assert.Single(AgendaText.Parse("Fri 18:00 Pizza", FirstDay, vienna));
        Assert.Equal(new DateTime(2026, 10, 23, 16, 0, 0), Utc(item));
    }
}

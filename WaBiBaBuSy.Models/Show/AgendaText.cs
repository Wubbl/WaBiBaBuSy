using System.Globalization;
using System.Text.RegularExpressions;

namespace WaBiBaBuSy.Models.Show;

/// <summary>
/// The Agenda tab's text → items: one per line, <c>[Day] H:mm Text</c> ("Sat 18:00 Pizza", "Sa 9:30 Frühstück",
/// "01:00 Sleep"). A day resolves to the first date on or after the first LAN day with that weekday; a line without one
/// stays on the previous line's day, or the next day when its time is earlier (past midnight). Junk lines are skipped.
/// </summary>
public static partial class AgendaText
{
    private static readonly Dictionary<string, DayOfWeek> DayTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mon"] = DayOfWeek.Monday, ["monday"] = DayOfWeek.Monday, ["mo"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday, ["tuesday"] = DayOfWeek.Tuesday, ["di"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday, ["mi"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday, ["do"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday, ["friday"] = DayOfWeek.Friday, ["fr"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday, ["sa"] = DayOfWeek.Saturday,
        ["sun"] = DayOfWeek.Sunday, ["sunday"] = DayOfWeek.Sunday, ["so"] = DayOfWeek.Sunday,
    };

    [GeneratedRegex(@"^\s*(?:(?<day>[A-Za-z]+)\.?\s+)?(?<h>\d{1,2})[:.](?<m>\d{2})\s+(?<text>.+?)\s*$")]
    private static partial Regex LinePattern();

    public static List<AgendaItem> Parse(string text, DateOnly firstDay, TimeZoneInfo tz)
    {
        var items = new List<AgendaItem>();
        DateTime? previous = null;   // local
        foreach (var line in (text ?? "").Split('\n'))
        {
            var m = LinePattern().Match(line);
            if (!m.Success) continue;
            int h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            int min = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            if (h > 23 || min > 59) continue;
            var time = new TimeOnly(h, min);

            DateOnly date;
            if (m.Groups["day"].Success)
            {
                if (!DayTokens.TryGetValue(m.Groups["day"].Value, out var weekday)) continue;
                date = firstDay.AddDays(((int)weekday - (int)firstDay.DayOfWeek + 7) % 7);
            }
            else if (previous is { } p)
            {
                date = DateOnly.FromDateTime(p);
                if (time < TimeOnly.FromDateTime(p)) date = date.AddDays(1);
            }
            else
            {
                date = firstDay;
            }

            var local = date.ToDateTime(time);
            previous = local;
            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
            items.Add(new AgendaItem { StartUtcMs = new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds(), Text = m.Groups["text"].Value });
        }
        return items.OrderBy(i => i.StartUtcMs).ToList();
    }
}

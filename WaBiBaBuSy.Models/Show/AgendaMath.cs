using System.Globalization;

namespace WaBiBaBuSy.Models.Show;

public enum AgendaRowState { Past, Current, Next, Upcoming }

/// <summary>One drawn agenda line. <see cref="DayHeader"/>: draw <see cref="Day"/> before the time (first row of a day).</summary>
public sealed record AgendaRow(int ItemIndex, string Day, string Time, string Text, AgendaRowState State, string? Countdown, bool Pulse, bool DayHeader);

/// <summary>Pure "now / next" math of the agenda, shared by the players and the room preview.</summary>
public static class AgendaMath
{
    public const int MaxPanelRows = 6;

    /// <summary>The next item pulses this long before it starts.</summary>
    public const long PulseBeforeMs = 10 * 60_000;

    /// <summary>
    /// The corner panel's rows: from the last past item (or the current one when it is the first) up to
    /// <paramref name="maxRows"/> items. Current = last item already started, Next = first not yet started.
    /// </summary>
    public static IReadOnlyList<AgendaRow> Rows(Agenda a, long nowUtcMs, int maxRows = MaxPanelRows)
    {
        var items = a.Items;
        if (items.Count == 0 || maxRows <= 0) return Array.Empty<AgendaRow>();
        int current = CurrentIndex(items, nowUtcMs);
        int first = Math.Max(0, current - 1);
        int last = Math.Min(items.Count, first + maxRows);
        var rows = new List<AgendaRow>(last - first);
        string? previousDay = null;
        for (int i = first; i < last; i++)
        {
            var row = Row(a, i, current, nowUtcMs, previousDay);
            rows.Add(row);
            previousDay = row.Day;
        }
        return rows;
    }

    /// <summary>Every item grouped by local day, in order (the full-size agenda screen).</summary>
    public static IReadOnlyList<(string Day, IReadOnlyList<AgendaRow> Rows)> Days(Agenda a, long nowUtcMs)
    {
        int current = CurrentIndex(a.Items, nowUtcMs);
        var days = new List<(string Day, IReadOnlyList<AgendaRow> Rows)>();
        List<AgendaRow>? rows = null;
        string? previousDay = null;
        for (int i = 0; i < a.Items.Count; i++)
        {
            var row = Row(a, i, current, nowUtcMs, previousDay);
            if (row.DayHeader)
            {
                rows = new List<AgendaRow>();
                days.Add((row.Day, rows));
            }
            rows!.Add(row);
            previousDay = row.Day;
        }
        return days;
    }

    /// <summary>"in 23 min" below an hour (minutes rounded up), else "in 2:05 h"; null once started.</summary>
    public static string? Countdown(long nowUtcMs, long startUtcMs)
    {
        long ms = startUtcMs - nowUtcMs;
        if (ms <= 0) return null;
        long minutes = (ms + 59_999) / 60_000;
        return minutes < 60
            ? $"in {minutes} min"
            : string.Create(CultureInfo.InvariantCulture, $"in {minutes / 60}:{minutes % 60:00} h");
    }

    /// <summary>Move every item by <paramref name="deltaMs"/> (server clock → this machine's clock).</summary>
    public static void ShiftTimes(Agenda a, long deltaMs)
    {
        foreach (var item in a.Items) item.StartUtcMs += deltaMs;
    }

    /// <summary>"18:00" in the agenda's local time.</summary>
    public static string TimeLabel(Agenda a, long utcMs) => Local(a, utcMs).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"Sat" in the agenda's local time.</summary>
    public static string DayLabel(Agenda a, long utcMs) => Local(a, utcMs).ToString("ddd", CultureInfo.InvariantCulture);

    private static DateTime Local(Agenda a, long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime.AddMinutes(a.UtcOffsetMinutes);

    /// <summary>Index of the last item that has started; -1 when none has.</summary>
    private static int CurrentIndex(IReadOnlyList<AgendaItem> items, long nowUtcMs)
    {
        int current = -1;
        for (int i = 0; i < items.Count && items[i].StartUtcMs <= nowUtcMs; i++) current = i;
        return current;
    }

    private static AgendaRow Row(Agenda a, int i, int current, long nowUtcMs, string? previousDay)
    {
        var item = a.Items[i];
        var state = i < current ? AgendaRowState.Past
                  : i == current ? AgendaRowState.Current
                  : i == current + 1 ? AgendaRowState.Next
                  : AgendaRowState.Upcoming;
        string day = DayLabel(a, item.StartUtcMs);
        bool next = state == AgendaRowState.Next;
        return new AgendaRow(i, day, TimeLabel(a, item.StartUtcMs), item.Text, state,
            next ? Countdown(nowUtcMs, item.StartUtcMs) : null,
            next && item.StartUtcMs - nowUtcMs <= PulseBeforeMs,
            day != previousDay);
    }
}

namespace WaBiBaBuSy.Models.Show;

/// <summary>Which screen corner the agenda panel sits in.</summary>
public enum AgendaCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>One timetable entry: when it starts (UTC ms on the server clock) and what it is.</summary>
public sealed class AgendaItem
{
    public long StartUtcMs { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>
/// The LAN-day timetable shown on every seat (design 2026-10-21-agenda-overlay-design.md). Room-wide state with its
/// own transport (<c>CommandType.AGENDA</c>) — not a show event. Times are UTC; labels use <see cref="UtcOffsetMinutes"/>
/// so every seat shows the server's local times and days.
/// </summary>
public sealed class Agenda
{
    public bool Enabled { get; set; }
    public string Title { get; set; } = "";

    /// <summary>Sorted by <see cref="AgendaItem.StartUtcMs"/>.</summary>
    public List<AgendaItem> Items { get; set; } = new();

    public List<string> Notes { get; set; } = new();
    public AgendaCorner Corner { get; set; } = AgendaCorner.TopRight;

    /// <summary>The server's local UTC offset when the agenda was built: day grouping and "18:00" labels.</summary>
    public int UtcOffsetMinutes { get; set; }

    /// <summary>Nodes tagged "agenda": they show the full-size timetable instead of the corner panel.</summary>
    public List<string> ScreenNodeIds { get; set; } = new();

    /// <summary>Server time the agenda was sent (set by the codec); the receiver shifts item times by its clock offset.</summary>
    public long SentUtcMs { get; set; }

    /// <summary>True when any of the ids this monitor may be known by is an agenda screen.</summary>
    public bool IsScreen(IEnumerable<string> nodeIdCandidates) => nodeIdCandidates.Any(id => ScreenNodeIds.Contains(id, StringComparer.Ordinal));
}

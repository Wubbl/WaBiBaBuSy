namespace WaBiBaBuSy.Models.Show;

/// <summary>
/// The agenda's stand-in players: while the agenda is on, every monitor without a scene player gets one (the seat's own
/// desktop wallpaper + the panel); a scene starting on it, or the agenda going off, stops it.
/// </summary>
public static class AgendaStandIns
{
    public static (IReadOnlyList<int> Start, IReadOnlyList<int> Stop) Plan(bool agendaOn, int monitorCount,
        IReadOnlySet<int> withScene, IReadOnlySet<int> withStandIn)
    {
        var start = new List<int>();
        if (agendaOn)
            for (int m = 0; m < monitorCount; m++)
                if (!withScene.Contains(m) && !withStandIn.Contains(m)) start.Add(m);
        var stop = withStandIn
            .Where(m => !agendaOn || withScene.Contains(m) || m < 0 || m >= monitorCount)
            .OrderBy(m => m)
            .ToList();
        return (start, stop);
    }
}

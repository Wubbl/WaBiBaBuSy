namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>Show status for the control panel: the toolbar's "Now / next" line and the prefetch gate.</summary>
public static class ShowStatus
{
    /// <summary>"Now: Fish · next: Logo Rain in 0:42", or "… last item · ends in 1:05" when nothing follows.</summary>
    public static string Format(string? now, string? next, long remainingMs)
    {
        var remaining = TimeSpan.FromMilliseconds(Math.Max(0, remainingMs));
        var clock = $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
        var nowPart = string.IsNullOrWhiteSpace(now) ? "" : $"Now: {now} · ";
        return next == null ? $"{nowPart}last item · ends in {clock}" : $"{nowPart}next: {next} in {clock}";
    }

    /// <summary>
    /// True when every connected remote reported its show prefetch complete (or none is connected).
    /// A node that reported nothing yet (total 0) is not ready.
    /// </summary>
    public static bool RemotesReady(IEnumerable<(bool Connected, int Ready, int Total)> remotes) =>
        remotes.Where(r => r.Connected).All(r => r.Total > 0 && r.Ready >= r.Total);
}

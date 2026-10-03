namespace WaBiBaBuSy.Models.Show;

/// <summary>
/// The events a host or player knows about, oldest first. Thread-safe. Ended events are kept for a minute (an
/// exact frame requested shortly after still finds them; drawing skips them by time), then pruned.
/// </summary>
public sealed class ShowEventLog
{
    /// <summary>Most events kept; the oldest is dropped beyond it.</summary>
    public const int Capacity = 16;

    /// <summary>How long an ended event stays in the log.</summary>
    public const long KeepAfterEndMs = 60_000;

    private readonly List<ShowEvent> _events = new();
    private readonly object _gate = new();

    public int Count { get { lock (_gate) return _events.Count; } }

    /// <summary>Add <paramref name="evt"/>; false when its id is empty or already known (a replay).</summary>
    public bool Add(ShowEvent evt)
    {
        if (string.IsNullOrEmpty(evt.EventId)) return false;
        lock (_gate)
        {
            if (_events.Any(e => e.EventId == evt.EventId)) return false;
            _events.Add(evt);
            while (_events.Count > Capacity) _events.RemoveAt(0);
            return true;
        }
    }

    public bool Contains(string eventId) { lock (_gate) return _events.Any(e => e.EventId == eventId); }

    /// <summary>Events not yet ended at <paramref name="nowUtcMs"/>, including ones whose T0 is still ahead.</summary>
    public IReadOnlyList<ShowEvent> Active(long nowUtcMs)
    {
        lock (_gate) return _events.Where(e => !ShowEventMath.Ended(e, nowUtcMs)).ToList();
    }

    /// <summary>Drop events that ended at least <see cref="KeepAfterEndMs"/> ago; returns how many.</summary>
    public int Prune(long nowUtcMs)
    {
        lock (_gate)
            return _events.RemoveAll(e => nowUtcMs >= e.T0Utc + ShowEventMath.ClampDuration(e.DurationMs) + KeepAfterEndMs);
    }

    /// <summary>Copy the events into <paramref name="target"/> (no allocation; for the render loop); returns how many.</summary>
    public int CopyTo(ShowEvent[] target)
    {
        lock (_gate)
        {
            int n = Math.Min(target.Length, _events.Count);
            for (int i = 0; i < n; i++) target[i] = _events[i];
            return n;
        }
    }

    public void Clear() { lock (_gate) _events.Clear(); }
}

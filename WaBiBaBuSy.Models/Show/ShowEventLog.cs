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
            // BiBaBo: only the newest step of a game matters — a keypad session must not push the bomb out of the log
            if (evt.Type == ShowEventType.BombStep && evt.Bomb != null)
            {
                var older = _events.FirstOrDefault(e => e.Type == ShowEventType.BombStep && e.Bomb?.GameId == evt.Bomb.GameId);
                if (older != null && older.T0Utc > evt.T0Utc) return false;   // a late replay of an older step
                _events.RemoveAll(e => e.Type == ShowEventType.BombStep && e.Bomb?.GameId == evt.Bomb.GameId);
            }
            _events.Add(evt);
            while (_events.Count > Capacity) _events.RemoveAt(0);
            return true;
        }
    }

    public bool Contains(string eventId) { lock (_gate) return _events.Any(e => e.EventId == eventId); }

    /// <summary>
    /// Events not yet ended at <paramref name="nowUtcMs"/>, including ones whose T0 is still ahead. A BiBaBo game whose
    /// outcome has started is over, however long the Bomb event was booked for.
    /// </summary>
    public IReadOnlyList<ShowEvent> Active(long nowUtcMs)
    {
        lock (_gate) return _events.Where(e => (!ShowEventMath.Ended(e, nowUtcMs) || OutcomeOfBookedBomb(e, nowUtcMs)) && !OutcomeStarted(e, nowUtcMs)).ToList();
    }

    /// <summary>
    /// Drop events that ended at least <see cref="KeepAfterEndMs"/> ago; returns how many. A Bomb goes together with its
    /// outcome (the outcome is what hides it — dropping the outcome first would bring the bomb back).
    /// </summary>
    public int Prune(long nowUtcMs)
    {
        lock (_gate)
        {
            bool Gone(ShowEvent e) => nowUtcMs >= e.T0Utc + ShowEventMath.ClampDuration(e.DurationMs) + KeepAfterEndMs;
            var finishedGames = _events.Where(e => e.Type == ShowEventType.BombOutcome && Gone(e) && e.Bomb != null).Select(e => e.Bomb!.GameId).ToHashSet();
            return _events.RemoveAll(e => e.Type switch
            {
                ShowEventType.Bomb => Gone(e) || finishedGames.Contains(e.EventId),
                ShowEventType.BombOutcome => Gone(e) && (e.Bomb == null || finishedGames.Contains(e.Bomb.GameId)),
                _ => Gone(e),
            });
        }
    }

    /// <summary>
    /// An ended BombOutcome whose Bomb is still booked: it keeps replaying, so a client that missed it learns the game is
    /// over (players draw nothing for it; it only hides the bomb). Call under the lock.
    /// </summary>
    private bool OutcomeOfBookedBomb(ShowEvent e, long nowUtcMs) =>
        e.Type == ShowEventType.BombOutcome && _events.Any(b => b.Type == ShowEventType.Bomb && b.EventId == e.Bomb?.GameId && !ShowEventMath.Ended(b, nowUtcMs));

    /// <summary>A Bomb event whose BombOutcome is in the log and has started (call under the lock).</summary>
    private bool OutcomeStarted(ShowEvent e, long nowUtcMs) =>
        e.Type == ShowEventType.Bomb && _events.Any(o => o.Type == ShowEventType.BombOutcome && o.Bomb?.GameId == e.EventId && o.T0Utc <= nowUtcMs);

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

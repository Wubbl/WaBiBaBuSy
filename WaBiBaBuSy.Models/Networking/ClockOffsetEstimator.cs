namespace WaBiBaBuSy.Models.Networking;

/// <summary>
/// NTP-style clock offset estimator. Feed it heartbeat round-trips
/// (client send time t0, server timestamp, client receive time t3);
/// <see cref="OffsetMs"/> estimates (server_clock - client_clock) using the
/// sample with the lowest round-trip time in a sliding window — the lowest-RTT
/// sample has the smallest possible asymmetry error.
/// Thread-safe: heartbeat loop writes, command stream reads.
/// </summary>
public class ClockOffsetEstimator
{
    private const int WindowSize = 16;
    private readonly object _lock = new();
    private readonly Queue<(long Rtt, long Offset)> _samples = new();

    /// <summary>True once at least one valid round-trip sample has been recorded.</summary>
    public bool HasSamples
    {
        get { lock (_lock) return _samples.Count > 0; }
    }

    /// <summary>Estimated (server - client) clock offset in milliseconds. 0 until samples exist.</summary>
    public long OffsetMs
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count == 0) return 0;
                long bestRtt = long.MaxValue;
                long best = 0;
                foreach (var (rtt, offset) in _samples)
                {
                    if (rtt < bestRtt)
                    {
                        bestRtt = rtt;
                        best = offset;
                    }
                }
                return best;
            }
        }
    }

    /// <summary>
    /// Round-trip time (ms) of the best (lowest-RTT) sample in the window —
    /// the sample <see cref="OffsetMs"/> is derived from. 0 until samples exist.
    /// </summary>
    public long RttMs
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count == 0) return 0;
                long best = long.MaxValue;
                foreach (var (rtt, _) in _samples)
                {
                    if (rtt < best) best = rtt;
                }
                return best;
            }
        }
    }

    /// <summary>
    /// Record one heartbeat round-trip. Assumes the server timestamp was taken
    /// between the client's send and receive instants (true for a unary RPC).
    /// </summary>
    public void AddSample(long clientSendMs, long serverTimestampMs, long clientReceiveMs)
    {
        var rtt = clientReceiveMs - clientSendMs;
        if (rtt < 0) return; // local clock stepped backwards mid-flight; sample is garbage

        var offset = serverTimestampMs - (clientSendMs + clientReceiveMs) / 2;
        lock (_lock)
        {
            _samples.Enqueue((rtt, offset));
            while (_samples.Count > WindowSize)
                _samples.Dequeue();
        }
    }

    public void Reset()
    {
        lock (_lock) _samples.Clear();
    }
}

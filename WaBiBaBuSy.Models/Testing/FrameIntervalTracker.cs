using System.Diagnostics;

namespace WaBiBaBuSy.Models.Testing;

/// <summary>
/// Ring buffer of the last N frame-to-frame intervals (player render loop, test mode). Not thread-safe:
/// record and snapshot from the render thread.
/// </summary>
public sealed class FrameIntervalTracker
{
    private readonly double[] _intervalsMs;
    private readonly double _ticksPerMs;
    private int _count;
    private int _next;
    private long _lastTimestamp;
    private bool _hasLast;

    /// <param name="capacity">Intervals kept.</param>
    /// <param name="ticksPerSecond">Timestamp resolution; default <see cref="Stopwatch.Frequency"/>.</param>
    public FrameIntervalTracker(int capacity = 120, long? ticksPerSecond = null)
    {
        _intervalsMs = new double[Math.Max(1, capacity)];
        _ticksPerMs = (ticksPerSecond ?? Stopwatch.Frequency) / 1000.0;
    }

    /// <summary>Record a frame at <paramref name="timestamp"/> (e.g. <see cref="Stopwatch.GetTimestamp"/> right after Present).</summary>
    public void Record(long timestamp)
    {
        if (_hasLast)
        {
            _intervalsMs[_next] = (timestamp - _lastTimestamp) / _ticksPerMs;
            _next = (_next + 1) % _intervalsMs.Length;
            if (_count < _intervalsMs.Length) _count++;
        }
        _lastTimestamp = timestamp;
        _hasLast = true;
    }

    /// <summary>Stats over the kept intervals; null before the second frame. <paramref name="refreshHz"/> 0 = no drop counting.</summary>
    public FrameIntervalStats? Snapshot(int refreshHz)
    {
        if (_count == 0) return null;
        var sorted = new double[_count];
        Array.Copy(_intervalsMs, sorted, _count);   // order does not matter for the stats
        Array.Sort(sorted);

        double mean = sorted.Average();
        double dropLimit = refreshHz > 0 ? 1.5 * 1000.0 / refreshHz : double.MaxValue;
        return new FrameIntervalStats
        {
            Count = _count,
            MeanFps = mean > 0 ? 1000.0 / mean : 0,
            P50Ms = NearestRank(sorted, 0.50),
            P99Ms = NearestRank(sorted, 0.99),
            MaxMs = sorted[^1],
            Dropped = sorted.Count(ms => ms > dropLimit),
        };
    }

    public void Reset()
    {
        _count = 0;
        _next = 0;
        _hasLast = false;
    }

    private static double NearestRank(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
}

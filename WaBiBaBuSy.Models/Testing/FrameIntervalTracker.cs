using System.Diagnostics;

namespace WaBiBaBuSy.Models.Testing;

/// <summary>
/// Ring buffer of the last N frame-to-frame intervals (player render loop, test mode). Not thread-safe:
/// record and snapshot from the render thread.
/// </summary>
public sealed class FrameIntervalTracker
{
    private readonly double[] _intervalsMs;
    private readonly double[] _sorted;
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
        _sorted = new double[_intervalsMs.Length];
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
        // Sorted in a reused buffer: this runs on the render thread for every probe.
        Array.Copy(_intervalsMs, _sorted, _count);   // order does not matter for the stats
        Array.Sort(_sorted, 0, _count);

        double dropLimit = refreshHz > 0 ? 1.5 * 1000.0 / refreshHz : double.MaxValue;
        double sum = 0;
        int dropped = 0;
        for (int i = 0; i < _count; i++)
        {
            sum += _sorted[i];
            if (_sorted[i] > dropLimit) dropped++;
        }
        double mean = sum / _count;
        return new FrameIntervalStats
        {
            Count = _count,
            MeanFps = mean > 0 ? 1000.0 / mean : 0,
            P50Ms = NearestRank(_sorted, _count, 0.50),
            P99Ms = NearestRank(_sorted, _count, 0.99),
            MaxMs = _sorted[_count - 1],
            Dropped = dropped,
        };
    }

    /// <summary>
    /// Mean of the kept intervals in ms, 0 before the second frame. Allocation-free — use it instead of
    /// <see cref="Snapshot"/> when only the frame interval is needed (e.g. present-time extrapolation).
    /// </summary>
    public double MeanIntervalMs
    {
        get
        {
            if (_count == 0) return 0;
            double sum = 0;
            for (int i = 0; i < _count; i++) sum += _intervalsMs[i];
            return sum / _count;
        }
    }

    /// <summary>Forget every interval and the last timestamp (new scene / test mode switched on).</summary>
    public void Reset()
    {
        _count = 0;
        _next = 0;
        _hasLast = false;
    }

    private static double NearestRank(double[] sorted, int count, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * count) - 1, 0, count - 1)];
}

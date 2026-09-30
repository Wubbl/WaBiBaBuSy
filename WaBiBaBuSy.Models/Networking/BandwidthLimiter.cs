using System.Diagnostics;

namespace WaBiBaBuSy.Models.Networking;

/// <summary>
/// Shared byte-rate cap for server → client transfers (content downloads, update packages).
/// Every stream reserves its next chunk's transmit slot on one common schedule, so the
/// total across all concurrent downloads stays at <see cref="BytesPerSecond"/> — at a LAN
/// party the server's link must not be saturated by 20 machines fetching the same video.
/// Idle time is not banked: after a pause the next chunk goes immediately, then pacing resumes.
/// Thread-safe.
/// </summary>
public sealed class BandwidthLimiter
{
    private readonly object _lock = new();
    private readonly Func<long> _clockMs;
    private double _nextFreeMs = double.MinValue;

    /// <summary>Total byte rate; 0 = unlimited.</summary>
    public long BytesPerSecond { get; }

    /// <param name="bytesPerSecond">Total byte rate; 0 or negative = unlimited.</param>
    /// <param name="clockMs">Monotonic millisecond clock; defaults to <see cref="Stopwatch"/> (tests inject a fake).</param>
    public BandwidthLimiter(long bytesPerSecond, Func<long>? clockMs = null)
    {
        BytesPerSecond = Math.Max(0, bytesPerSecond);
        _clockMs = clockMs ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
    }

    /// <summary>Limiter for a rate in MB/s (1 MB = 1 MiB); 0 or negative = unlimited.</summary>
    public static BandwidthLimiter FromMegabytesPerSecond(int megabytesPerSecond) =>
        new(megabytesPerSecond > 0 ? megabytesPerSecond * 1024L * 1024L : 0);

    /// <summary>
    /// Reserve a transmit slot for <paramref name="bytes"/> and return how many milliseconds
    /// the caller must wait before sending them.
    /// </summary>
    public long Reserve(int bytes)
    {
        if (BytesPerSecond <= 0 || bytes <= 0) return 0;
        lock (_lock)
        {
            double now = _clockMs();
            double start = Math.Max(_nextFreeMs, now);
            _nextFreeMs = start + bytes * 1000.0 / BytesPerSecond;
            return (long)Math.Ceiling(start - now);
        }
    }

    /// <summary>Wait until <paramref name="bytes"/> may be sent. Completes synchronously when unlimited or on schedule.</summary>
    public Task WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        var delayMs = Reserve(bytes);
        return delayMs > 0 ? Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken) : Task.CompletedTask;
    }
}

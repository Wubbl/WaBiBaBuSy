using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Configuration;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Scheduled sync commands wait on the client's sync clock — real UTC plus the simulated TEST_MODE skew
/// (<see cref="WallpaperSyncClient.NowUtcMs"/>) — not on the raw system clock. A command for
/// (converted) server time T runs when the skewed clock reaches T; a command already in the past runs at once.
/// Driven by a manual <see cref="TimeProvider"/>, so no test waits on real time.
/// </summary>
public class SkewedSchedulingTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly long StartMs = Start.ToUnixTimeMilliseconds();

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"wbbs-sched-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _time = new(Start);
    private readonly WallpaperSyncClient _client;
    private readonly WallpaperPlaybackService _playback;
    private int _executed;

    public SkewedSchedulingTests()
    {
        _client = new WallpaperSyncClient(NullLogger<WallpaperSyncClient>.Instance, new ClientConfiguration())
        {
            TimeProvider = _time,
        };
        _playback = new WallpaperPlaybackService(NullLogger<WallpaperPlaybackService>.Instance, _client,
            cacheDirectory: _cacheDir)
        {
            // A STOP runs this delegate first: the observable "the command executed".
            D2DCrossScreenStopDelegate = () => { Interlocked.Increment(ref _executed); return Task.CompletedTask; },
        };
    }

    public void Dispose()
    {
        _playback.Dispose();
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    private static SyncCommand StopAt(long timestampUtc) =>
        new() { Type = CommandType.Stop, ContentId = "c", TimestampUtc = timestampUtc };

    private bool Executed => Volatile.Read(ref _executed) > 0;

    [Fact]
    public void NowUtcMs_IsTheClockPlusTheSimulatedSkew()
    {
        _client.SimulatedClockSkewMs = -1500;
        Assert.Equal(StartMs - 1500, _client.NowUtcMs);
    }

    [Theory]
    [InlineData(2000)]    // clock ahead: T comes 2 s sooner than on the real clock
    [InlineData(-2000)]   // clock behind: T comes 2 s later
    [InlineData(0)]
    public async Task Command_RunsWhenTheSkewedClockReachesItsTimestamp(int skewMs)
    {
        _client.SimulatedClockSkewMs = skewMs;
        const long target = 3000;                                 // T = real start + 3 s
        long waitOnSkewedClock = target - skewMs;                 // 1 s, 5 s, 3 s

        var run = _playback.ExecuteCommandWithTimingAsync(StopAt(StartMs + target));

        _time.Advance(TimeSpan.FromMilliseconds(waitOnSkewedClock - 1));
        Assert.False(Executed, $"ran {waitOnSkewedClock - (_time.GetUtcNow() - Start).TotalMilliseconds} ms early");
        Assert.False(run.IsCompleted);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Executed);
        Assert.Equal(StartMs + target, _client.NowUtcMs);           // the skewed clock reads exactly T
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    [InlineData(-5000)]
    public async Task Command_InThePast_RunsImmediately(int skewMs)
    {
        _client.SimulatedClockSkewMs = skewMs;

        var run = _playback.ExecuteCommandWithTimingAsync(StopAt(StartMs + skewMs - 2500));   // 2.5 s late on the skewed clock

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Executed);
    }

    [Fact]
    public async Task Command_DueOnTheSkewedClock_RunsImmediately_ThoughTheRealClockIsNotThereYet()
    {
        // Real clock: 1 s ahead. Skewed clock (+2 s): 1 s past. The skewed clock decides.
        _client.SimulatedClockSkewMs = 2000;

        var run = _playback.ExecuteCommandWithTimingAsync(StopAt(StartMs + 1000));

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Executed);
    }

    [Fact]
    public async Task OlderCommand_StillWaiting_IsDropped_WhenANewerOneArrived()
    {
        // Review 2026-10-13 C1: commands run on their own tasks; a LOAD / STOP that finishes after a newer
        // one must not act (a stopped or replaced scene came back). Two STOPs make the ordering observable.
        var older = _playback.Dispatch(StopAt(StartMs + 3000));
        var newer = _playback.Dispatch(StopAt(StartMs + 1000));
        // Dispatch runs each command on Task.Run: advance only once both delays exist, or a late one never fires.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_time.PendingTimers < 2 && DateTime.UtcNow < deadline) await Task.Delay(5);

        _time.Advance(TimeSpan.FromMilliseconds(1000));
        await newer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref _executed));

        _time.Advance(TimeSpan.FromMilliseconds(2000));
        await older.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref _executed));   // the older STOP was superseded
    }

    /// <summary>A <see cref="TimeProvider"/> that only moves on <see cref="Advance"/>; due timers fire inside it.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = new();
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock) return _now;
        }

        /// <summary>Timers waiting to fire (a delay started on another task registers here once it runs).</summary>
        public int PendingTimers
        {
            get { lock (_lock) return _timers.Count(t => t.Due != null); }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            DateTimeOffset target;
            lock (_lock) target = _now + by;
            while (true)
            {
                ManualTimer? next;
                lock (_lock)
                {
                    next = _timers.Where(t => t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                    if (next == null) { _now = target; return; }
                    _now = next.Due!.Value;
                    next.Reschedule(_now);
                }
                next.Fire();
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;

            public DateTimeOffset? Due { get; private set; }

            public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._lock)
                {
                    _period = period;
                    _owner._timers.Remove(this);
                    if (dueTime == Timeout.InfiniteTimeSpan) { Due = null; return true; }
                    Due = _owner._now + dueTime;
                    _owner._timers.Add(this);
                }
                return true;
            }

            /// <summary>Called under the owner's lock when this timer fires: one-shot timers are removed.</summary>
            public void Reschedule(DateTimeOffset now)
            {
                if (_period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero)
                {
                    _owner._timers.Remove(this);
                    Due = null;
                }
                else Due = now + _period;
            }

            public void Fire() => _callback(_state);

            public void Dispose()
            {
                lock (_owner._lock) { _owner._timers.Remove(this); Due = null; }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

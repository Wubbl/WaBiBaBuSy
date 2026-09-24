// WaBiBaBuSy.UI/Controls/SceneClock.cs
using System;
using System.Diagnostics;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>
/// Clock for scene previews. <b>Design</b> mode: a local stopwatch × <see cref="Speed"/>, pausable,
/// restarted on config changes. <b>Live</b> mode (<see cref="SharedStartUtcMs"/> &gt; 0): elapsed =
/// now − the shared start the players use, so the preview shows where the sprite <i>is</i>.
/// </summary>
public sealed class SceneClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private double _accumulatedMs;
    private double _speed = 1.0;
    private bool _paused;

    /// <summary>Shared UTC start (ms). &gt; 0 switches to the live clock.</summary>
    public long SharedStartUtcMs { get; set; }

    /// <summary>True when elapsed time follows the players' shared start instead of the design clock.</summary>
    public bool IsLive => SharedStartUtcMs > 0;

    /// <summary>Design-clock speed multiplier (min 0.01). Time run so far is kept at the old speed.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            _accumulatedMs += RunningMs();
            _stopwatch.Restart();
            if (_paused) _stopwatch.Reset();
            _speed = Math.Max(0.01, value);
        }
    }

    /// <summary>Freeze the design clock; time run so far is kept.</summary>
    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (value == _paused) return;
            if (value)
            {
                _accumulatedMs += RunningMs();
                _stopwatch.Reset();
            }
            else
            {
                _stopwatch.Start();
            }
            _paused = value;
        }
    }

    /// <summary>Restart the design clock at t = 0.</summary>
    public void Restart()
    {
        _accumulatedMs = 0;
        _stopwatch.Restart();
        if (_paused) _stopwatch.Reset();
    }

    /// <summary>Milliseconds since the scene started on the active clock (may be negative before a live start).</summary>
    public long ElapsedMs() => IsLive
        ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - SharedStartUtcMs
        : (long)(_accumulatedMs + RunningMs());

    private double RunningMs() => _paused ? 0 : _stopwatch.Elapsed.TotalMilliseconds * _speed;
}

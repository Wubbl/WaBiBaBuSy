using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;

namespace WaBiBaBuSy.Client.Linux.Rendering;

/// <summary>
/// Everything one monitor shows: the playing scene (<see cref="SceneContent"/>), the one it replaces while a
/// transition runs, the show events and the agenda. The Linux counterpart of one Windows D2D player process — and of
/// the standby switch between two of them: the old scene keeps showing until the new one's shared start, a Fade dips
/// through black around it, a Wipe reveals the new scene behind a boundary running along the seat map.
///
/// Every frame is a pure function of the clock (shared start + the Models helpers), so a Linux monitor shows exactly
/// what a Windows monitor at the same seat would.
///
/// Thread model: Load/Start/Stop/AddShowEvent/SetAgenda may be called from any thread; Render runs on the render
/// thread. Scene state is guarded by <see cref="_sync"/>; decoding happens outside the lock.
/// </summary>
public sealed class WallpaperScene : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly int _width, _height;   // monitor, device pixels

    private SceneContent? _pending;    // loaded, waiting for its start command
    private SceneContent? _current;
    private double _currentStartUnixMs;
    private SceneContent? _retiring;   // the scene _current replaces, shown until its start (+ the wipe)
    private double _retiringStartUnixMs;
    private SceneTransition _transition = SceneTransition.Cut;
    private bool _disposed;

    private readonly ShowEventPainter _events;
    private readonly AgendaPainter _agenda;

    // Precise "now": wall clock sampled once, advanced by Stopwatch (monotonic, sub-ms)
    private static readonly double s_anchorUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly long s_anchorTicks = Stopwatch.GetTimestamp();

    public WallpaperScene(int width, int height, ILogger logger, string? machineName = null)
    {
        _width = width;
        _height = height;
        _logger = logger;
        _events = new ShowEventPainter(logger, machineName ?? Environment.MachineName);
        _agenda = new AgendaPainter(logger);
    }

    public int Width => _width;
    public int Height => _height;

    /// <summary>True while a scene is loaded and started (a stand-in included).</summary>
    public bool IsPlaying { get { lock (_sync) return _current != null; } }

    /// <summary>The shared start of the playing scene (this machine's clock), or null.</summary>
    public double? StartUnixMs { get { lock (_sync) return _current != null ? _currentStartUnixMs : null; } }

    /// <summary>Layer 0's layout of the playing scene (the seat-map node show events and BiBaBo use), or null.</summary>
    public NodeLayout? EventLayout { get { lock (_sync) return (_current ?? _pending)?.EventLayout; } }

    /// <summary>True while an agenda is on.</summary>
    public bool AgendaOn => _agenda.IsOn;

    internal static double NowUnixMs()
        => s_anchorUnixMs + (Stopwatch.GetTimestamp() - s_anchorTicks) * 1000.0 / Stopwatch.Frequency;

    // ════════════════════════════════════════════════════════════════════
    // Commands (mirror the player's cmd_load_animation / cmd_start_animation / cmd_transition / stop)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Decode the scene (slow; any thread). It replaces the playing one at <see cref="Start"/>; until then the old
    /// scene keeps rendering.
    /// </summary>
    public void Load(PlayerCommandLoadAnimation cmd)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WallpaperScene));
        var content = SceneContent.Load(_width, _height, cmd, _logger);
        _events.Relayout(content.EventLayout);
        lock (_sync)
        {
            _pending?.Dispose();
            _pending = content;
        }
    }

    /// <summary>
    /// Start the loaded scene at the shared start (already in this machine's clock; 0 = now). The previous scene stays
    /// until then (standby switch) and leaves through <paramref name="transition"/> ("Fade:1500", "" = Cut).
    /// </summary>
    public void Start(PlayerCommandStartAnimation cmd, string? transition = null)
    {
        lock (_sync)
        {
            if (_pending == null)
            {
                _logger.LogWarning("[START] No scene loaded");
                return;
            }
            double start = cmd.StartTimestampMs > 0 ? cmd.StartTimestampMs : NowUnixMs();
            _retiring?.Dispose();
            _retiring = _current;
            _retiringStartUnixMs = _currentStartUnixMs;
            _current = _pending;
            _pending = null;
            _currentStartUnixMs = start;
            _transition = SceneTransition.Parse(transition);
            _logger.LogInformation("[START] ts={Ts}ms ({In:F0} ms from now), transition={Transition}",
                cmd.StartTimestampMs, start - NowUnixMs(), _transition);
        }
    }

    /// <summary>Drop every scene (the monitor's window is hidden by the host).</summary>
    public void Stop()
    {
        lock (_sync)
        {
            _pending?.Dispose(); _pending = null;
            _current?.Dispose(); _current = null;
            _retiring?.Dispose(); _retiring = null;
        }
    }

    /// <summary>A show event for this monitor (kept across scene loads; a replay with a known id is ignored).</summary>
    public void AddShowEvent(ShowEvent evt) => _events.Add(evt, EventLayout);

    /// <summary>The room agenda (null = none); <paramref name="fullScreen"/> = this monitor is an agenda screen.</summary>
    public void SetAgenda(Agenda? agenda, bool fullScreen) => _agenda.Set(agenda, fullScreen);

    // ════════════════════════════════════════════════════════════════════
    // Rendering
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Render the current frame (pixel coordinates, origin = monitor top-left).</summary>
    public void Render(SKCanvas canvas) => Render(canvas, NowUnixMs());

    /// <summary>Render the frame for an explicit wall-clock time (tests / offscreen).</summary>
    public void Render(SKCanvas canvas, double nowUnixMs)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_current == null)
            {
                canvas.Clear(SKColors.Black);
                return;
            }

            long msSinceStart = (long)(nowUnixMs - _currentStartUnixMs);
            var t = _transition;
            if (_retiring != null && msSinceStart >= TransitionMath.OutgoingHoldMs(t))
            {
                _retiring.Dispose();   // the old scene's part of the switch is over
                _retiring = null;
            }

            // Before the shared start the old scene stays; a fresh start shows the new scene's background meanwhile
            bool showNew = msSinceStart >= 0 || _retiring == null;
            var shown = showNew ? _current : _retiring!;
            double shownStart = showNew ? _currentStartUnixMs : _retiringStartUnixMs;
            long shownElapsed = (long)(nowUnixMs - shownStart);
            if (showNew && _retiring != null && t.Kind == TransitionKind.Wipe)
            {
                // The new scene only behind the boundary; ahead of it the old one
                var node = _current.SequentialLayout ?? new NodeLayout { Width = _width, CanvasWidth = _width, Scale = 1f };
                float canvasW = node.CanvasWidth > 0 ? node.CanvasWidth : node.Width;
                var (l, r) = TransitionMath.WipedSpan(TransitionMath.WipeBoundary(msSinceStart, t.ClampedMs, canvasW), node);
                float scale = node.Scale > 0 ? node.Scale : 1f;
                _retiring.Render(canvas, (long)(nowUnixMs - _retiringStartUnixMs));
                canvas.Save();
                canvas.ClipRect(new SKRect(MathF.Floor(l * scale), 0, MathF.Ceiling(r * scale), _height));
                _current.Render(canvas, shownElapsed);
                canvas.Restore();
            }
            else
            {
                shown.Render(canvas, shownElapsed);
            }

            // Show events above the scene, then the agenda (hidden under a blackout), then the transition veil
            _events.Draw(canvas, shown.EventLayout, shown.BottomLayer.PerMonitor, (long)shownStart, shownElapsed, (long)nowUnixMs);
            if (!_events.AgendaHidden) _agenda.Draw(canvas, _width, _height, (long)nowUnixMs);
            DrawTransitionVeil(canvas, t, msSinceStart, hasOld: _retiring != null);
        }
    }

    /// <summary>Fade: the old scene darkens over the half before the start, the new one brightens over the half after.</summary>
    private void DrawTransitionVeil(SKCanvas canvas, SceneTransition t, long msSinceStart, bool hasOld)
    {
        if (t.Kind != TransitionKind.Fade) return;
        float veil = msSinceStart >= 0
            ? TransitionMath.FadeInVeil(msSinceStart, t.ClampedMs)
            : hasOld ? TransitionMath.FadeOutVeil(msSinceStart, t.ClampedMs) : 0f;
        if (veil <= 0.003f) return;
        using var paint = new SKPaint { Color = SKColors.Black.WithAlpha(SpriteLayer.ToByte(veil)) };
        canvas.DrawRect(SKRect.Create(0, 0, _width, _height), paint);
    }

    /// <summary>Layer 0's current animation top-left in local canvas units (for tests/diagnostics).</summary>
    public (float X, float Y, int W, int H) CurrentLayout
    {
        get { lock (_sync) return _current?.BottomLayer.CurrentLayout ?? default; }
    }

    /// <summary>
    /// D2D ColorMatrix (row-vector, 5x4: out = [r g b a 1] · M) → Skia (column-vector, 4x5 row-major:
    /// out_i = Σ m[i*5+j]·in_j + m[i*5+4]). Both operate on unpremultiplied colours with offsets in 0..1.
    /// </summary>
    internal static SKColorFilter ToColorFilter(ColorMatrix5x4 m) => SKColorFilter.CreateColorMatrix(new[]
    {
        m.M11, m.M21, m.M31, m.M41, m.M51,
        m.M12, m.M22, m.M32, m.M42, m.M52,
        m.M13, m.M23, m.M33, m.M43, m.M53,
        m.M14, m.M24, m.M34, m.M44, m.M54,
    });

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Dispose(); _pending = null;
            _current?.Dispose(); _current = null;
            _retiring?.Dispose(); _retiring = null;
            _events.Dispose();
            _agenda.Dispose();
        }
    }
}

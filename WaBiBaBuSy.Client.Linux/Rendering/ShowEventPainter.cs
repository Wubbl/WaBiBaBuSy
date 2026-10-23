using ImageMagick;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Client.Linux.Rendering;

/// <summary>
/// One-shot show events on one monitor, drawn above the scene from T0 for their duration. Skia port of the Windows
/// player's Program.ShowEvents.cs (and Program.Bomb.cs in the other part of this class); all math comes from
/// WaBiBaBuSy.Models/Show. <see cref="Add"/> runs on any thread and decodes sprites on a worker; <see cref="Draw"/>
/// runs on the render thread under the owning scene's lock.
/// </summary>
internal sealed partial class ShowEventPainter : IDisposable
{
    private sealed class EventSprite : IDisposable
    {
        public required SKImage[] Frames { get; init; }
        public required int[] DelaysMs { get; init; }
        public required long TotalMs { get; init; }
        public void Dispose() { foreach (var f in Frames) f.Dispose(); }
    }

    private const int MaxEventSpriteHeight = 1024;
    private const long MaxEventSpriteBytes = 64L * 1024 * 1024;   // decoded size cap per sprite

    private readonly ILogger _logger;
    private readonly string _machineName;
    private readonly object _spriteLock = new();
    private readonly ShowEventLog _events = new();                                           // own lock
    private readonly Dictionary<string, EventSprite> _sprites = new();                      // guarded by _spriteLock
    private readonly HashSet<string> _decoding = new();                                     // guarded by _spriteLock
    private readonly ShowEvent[] _scratch = new ShowEvent[ShowEventLog.Capacity];          // render thread only
    private CelebrationParticle[] _particles = new CelebrationParticle[512];               // render thread only
    private readonly SKPaint _paint = new() { IsAntialias = true };
    private long _lastPruneUtcMs;

    // The frame being drawn (render thread only)
    private NodeLayout _layout = new();

    public ShowEventPainter(ILogger logger, string machineName)
    {
        _logger = logger;
        _machineName = machineName;
    }

    /// <summary>Set by <see cref="Draw"/>: a Blackout / BiBaBo black covers this seat, so the agenda stays hidden under it.</summary>
    public bool AgendaHidden { get; private set; }

    /// <summary>Keep the event (a replay with a known id is ignored) and start decoding its sprites for <paramref name="layout"/>.</summary>
    public void Add(ShowEvent evt, NodeLayout? layout)
    {
        if (!_events.Add(evt)) return;
        string nodeId = layout?.Id ?? string.Empty;
        if (!evt.IsFor(nodeId)) return;   // kept (dedupe), but never drawn here
        _logger.LogInformation("[Event] {Type} {Id} T0={T0} duration={Duration}ms", evt.Type, evt.EventId, evt.T0Utc, evt.DurationMs);
        StartDecodes(evt, layout);
    }

    /// <summary>The events kept so far (a new scene's layout may need sprites they did not decode yet, e.g. Identify).</summary>
    public void Relayout(NodeLayout layout)
    {
        int n = _events.CopyTo(_scratch);
        for (int i = 0; i < n; i++)
            if (_scratch[i].IsFor(layout.Id)) StartDecodes(_scratch[i], layout);
    }

    private void StartDecodes(ShowEvent evt, NodeLayout? layout)
    {
        if (evt.Type is ShowEventType.Bomb or ShowEventType.BombOutcome)
        {
            StartBombDecodes(evt);
            return;
        }
        Func<EventSprite>? decode = evt.Type switch
        {
            ShowEventType.Traversal when !string.IsNullOrEmpty(evt.ImagePath) => () => DecodeImage(evt.ImagePath!),
            ShowEventType.Announce when !string.IsNullOrWhiteSpace(evt.Text) => () => Text(evt.Text!, evt.ColorHex),
            // Each seat renders its own label: the number the room view orders by, and this machine's name
            ShowEventType.Identify => () => Text(IdentifyLabel(evt, layout), evt.ColorHex),
            _ => null,
        };
        if (decode == null)
        {
            if (evt.Type == ShowEventType.Traversal) _logger.LogWarning("[Event] Traversal {Id} has no image on this machine; skipped", evt.EventId);
            return;
        }
        StartDecode(evt.EventId, evt.EventId, decode);
    }

    private string IdentifyLabel(ShowEvent evt, NodeLayout? layout) =>
        layout != null && evt.Labels != null && evt.Labels.TryGetValue(layout.Id, out var label) && !string.IsNullOrWhiteSpace(label)
            ? label
            : $"{(layout?.Order ?? 0) + 1}  {_machineName}";

    /// <summary>Decode one sprite (id = the event id, or "eventId#slot") on a worker unless it is decoded or decoding.</summary>
    private void StartDecode(string spriteId, string eventId, Func<EventSprite> decode)
    {
        lock (_spriteLock)
        {
            if (_sprites.ContainsKey(spriteId) || !_decoding.Add(spriteId)) return;
        }
        _ = Task.Run(() =>
        {
            EventSprite? sprite = null;
            try { sprite = decode(); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Event] Could not decode the sprite {Id}; skipped", spriteId); }
            lock (_spriteLock)
            {
                _decoding.Remove(spriteId);
                if (sprite != null && _events.Contains(eventId)) { _sprites[spriteId] = sprite; sprite = null; }
            }
            sprite?.Dispose();   // the event was pruned meanwhile
        });
    }

    private static EventSprite Text(string text, string colorHex) =>
        new() { Frames = new[] { TextSprite.Render(text, colorHex) }, DelaysMs = new[] { 100 }, TotalMs = 100 };

    /// <summary>All frames of an image / GIF (coalesced), at most 1024 px high.</summary>
    private EventSprite DecodeImage(string path)
    {
        using var collection = new MagickImageCollection(path);
        collection.Coalesce();
        var frames = new List<SKImage>(collection.Count);
        var delays = new List<int>(collection.Count);
        long bytes = 0;
        foreach (var frame in collection)
        {
            if (frame.Height > MaxEventSpriteHeight)
            {
                uint w = (uint)Math.Max(1L, (long)frame.Width * MaxEventSpriteHeight / frame.Height);
                frame.Resize(new MagickGeometry(w, (uint)MaxEventSpriteHeight) { IgnoreAspectRatio = true });
            }
            bytes += (long)frame.Width * frame.Height * 4;
            if (frames.Count > 0 && bytes > MaxEventSpriteBytes)
            {
                _logger.LogWarning("[Event] Sprite {Path} exceeds {Cap} MB decoded; kept the first {Kept} of {Total} frames",
                    path, MaxEventSpriteBytes / (1024 * 1024), frames.Count, collection.Count);
                break;
            }
            frames.Add(AnimationSource.ToSkImage(frame));
            int delay = (int)(frame.AnimationDelay * 10);
            delays.Add(delay <= 0 ? 100 : Math.Min(delay, 5000));   // BiBaBo's beer holds its first frame 2.5 s
        }
        return new EventSprite { Frames = frames.ToArray(), DelaysMs = delays.ToArray(), TotalMs = Math.Max(1, delays.Sum()) };
    }

    private bool TryGetSprite(string id, out EventSprite sprite)
    {
        lock (_spriteLock) return _sprites.TryGetValue(id, out sprite!);
    }

    /// <summary>Drop sprites of events that left the log.</summary>
    private void ReleaseStaleSprites(int eventCount)
    {
        lock (_spriteLock)
        {
            if (_sprites.Count == 0) return;
            foreach (var id in _sprites.Keys.ToList())
            {
                bool known = false;
                for (int i = 0; i < eventCount && !known; i++) known = SpriteBelongsTo(id, _scratch[i].EventId);
                if (known) continue;
                _sprites[id].Dispose();
                _sprites.Remove(id);
            }
        }
    }

    private static bool SpriteBelongsTo(string spriteId, string eventId) =>
        spriteId.StartsWith(eventId, StringComparison.Ordinal) && (spriteId.Length == eventId.Length || spriteId[eventId.Length] == '#');

    /// <summary>Draw order: sprites and confetti, then the spotlight (dims them too), then blackout, then identify, then BiBaBo on top.</summary>
    private static int EventLayer(ShowEventType type) => type switch
    {
        ShowEventType.Spotlight => 1,
        ShowEventType.Blackout => 2,
        ShowEventType.Identify => 3,
        ShowEventType.Bomb or ShowEventType.BombOutcome => 4,
        _ => 0,
    };

    /// <summary>
    /// Draw the active events on <paramref name="layout"/> (device-pixel canvas). <paramref name="sceneRawElapsedMs"/>
    /// is now − <paramref name="sceneStartUtcMs"/> (un-phased).
    /// </summary>
    public void Draw(SKCanvas canvas, NodeLayout layout, bool perMonitor, long sceneStartUtcMs, long sceneRawElapsedMs, long nowUtcMs)
    {
        AgendaHidden = false;
        if (nowUtcMs - _lastPruneUtcMs > 1000)
        {
            _lastPruneUtcMs = nowUtcMs;
            _events.Prune(nowUtcMs);
        }
        int n = _events.CopyTo(_scratch);
        ReleaseStaleSprites(n);
        if (n == 0) return;

        _layout = layout;
        canvas.Save();
        canvas.Scale(layout.Scale > 0 ? layout.Scale : 1f);   // canvas units → device px
        try
        {
            for (int layer = 0; layer <= 4; layer++)
            {
                for (int i = 0; i < n; i++)
                {
                    var evt = _scratch[i];
                    if (EventLayer(evt.Type) != layer || !evt.IsFor(layout.Id)) continue;
                    long local = ShowEventMath.LocalMs(evt, sceneStartUtcMs, sceneRawElapsedMs);
                    if (!ShowEventMath.IsActive(evt, local)) continue;
                    switch (evt.Type)
                    {
                        case ShowEventType.Blackout:
                            DrawBlackout(canvas, evt, local);
                            if (ShowEventMath.BlackoutOpacity(evt, local) > 0.5f) AgendaHidden = true;
                            break;
                        case ShowEventType.Spotlight: DrawSpotlight(canvas, evt, local, perMonitor); break;
                        case ShowEventType.Celebration: DrawCelebration(canvas, evt, local); break;
                        case ShowEventType.Traversal:
                        case ShowEventType.Announce: DrawEventSprite(canvas, evt, local); break;
                        case ShowEventType.Identify: DrawIdentify(canvas, evt, local); break;
                        case ShowEventType.Bomb: DrawBomb(canvas, evt, local, n); break;
                        case ShowEventType.BombOutcome: DrawBombOutcome(canvas, evt, local); break;
                    }
                }
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    // ── paint helpers ───────────────────────────────────────────────────────

    private SKPaint Fill(string hex, float opacity)
    {
        var (r, g, b) = ShowEventMath.ParseColor(hex);
        return Fill(new SKColor(r, g, b), opacity);
    }

    private SKPaint Fill(SKColor color, float opacity)
    {
        _paint.Style = SKPaintStyle.Fill;
        _paint.Shader = null;
        _paint.StrokeCap = SKStrokeCap.Butt;
        _paint.Color = color.WithAlpha(SpriteLayer.ToByte(opacity));
        return _paint;
    }

    private SKPaint Stroke(string hex, float opacity, float width, bool round = false)
    {
        var (r, g, b) = ShowEventMath.ParseColor(hex);
        return Stroke(new SKColor(r, g, b), opacity, width, round);
    }

    private SKPaint Stroke(SKColor color, float opacity, float width, bool round = false)
    {
        Fill(color, opacity);
        _paint.Style = SKPaintStyle.Stroke;
        _paint.StrokeWidth = width;
        _paint.StrokeCap = round ? SKStrokeCap.Round : SKStrokeCap.Butt;
        _paint.StrokeJoin = round ? SKStrokeJoin.Round : SKStrokeJoin.Miter;
        return _paint;
    }

    private void FillNode(SKCanvas canvas, SKPaint paint) => canvas.DrawRect(SKRect.Create(0, 0, _layout.Width, _layout.Height), paint);

    private void DrawImage(SKCanvas canvas, SKImage image, SKRect dest, float opacity)
    {
        var paint = Fill(SKColors.White, opacity);
        canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
    }

    private static SKImage FrameAt(EventSprite sprite, long local)
    {
        if (sprite.Frames.Length == 1) return sprite.Frames[0];
        long t = SyncTiming.PositiveModulo(local, sprite.TotalMs);
        for (int f = 0; f < sprite.DelaysMs.Length; f++)
        {
            if (t < sprite.DelaysMs[f]) return sprite.Frames[f];
            t -= sprite.DelaysMs[f];
        }
        return sprite.Frames[0];
    }

    // ── events ──────────────────────────────────────────────────────────────

    private void DrawBlackout(SKCanvas canvas, ShowEvent evt, long local) =>
        FillNode(canvas, Fill(evt.ColorHex, ShowEventMath.BlackoutOpacity(evt, local)));

    private void DrawSpotlight(SKCanvas canvas, ShowEvent evt, long local, bool perMonitor)
    {
        var v = SpotlightMath.Visual(evt, _layout, sharedCanvas: !perMonitor, local);
        if (v.IsTarget)
        {
            if (v.BorderOpacity <= 0.005f) return;
            float half = v.BorderWidth / 2f;
            canvas.DrawRect(SKRect.Create(half, half, _layout.Width - v.BorderWidth, _layout.Height - v.BorderWidth),
                Stroke(evt.ColorHex, v.BorderOpacity, v.BorderWidth));
            return;
        }
        if (v.DimOpacity > 0.005f) FillNode(canvas, Fill(SKColors.Black, v.DimOpacity));
        if (v.ChevronDirection != 0 && v.ChevronOpacity > 0.005f)
        {
            float half = v.ChevronSize / 2f, d = v.ChevronDirection;
            var tip = new SKPoint(v.ChevronCenterX + d * half, v.ChevronCenterY);
            var top = new SKPoint(v.ChevronCenterX - d * half, v.ChevronCenterY - half);
            var bottom = new SKPoint(v.ChevronCenterX - d * half, v.ChevronCenterY + half);
            var paint = Stroke(evt.ColorHex, v.ChevronOpacity, v.ChevronSize * 0.18f);
            canvas.DrawLine(top, tip, paint);
            canvas.DrawLine(bottom, tip, paint);
        }
    }

    /// <param name="palette">Colours instead of the event colour + party colours (BiBaBo's fire); at most 6.</param>
    private void DrawCelebration(SKCanvas canvas, ShowEvent evt, long local, string[]? palette = null)
    {
        int count = CelebrationMath.Count(_layout.CanvasWidth > 0 ? _layout.CanvasWidth : _layout.Width);
        int capacity = CelebrationMath.Capacity(_layout, count);
        if (_particles.Length < capacity) _particles = new CelebrationParticle[capacity];   // grows once per layout
        int visible = CelebrationMath.VisibleOnNode(evt, _layout, local, _particles);
        float env = ShowEventMath.Envelope(local, evt.DurationMs);
        if (env <= 0.005f) return;
        Span<SKColor> colors = stackalloc SKColor[CelebrationMath.PartyColors.Length + 1];
        var (er, eg, eb) = ShowEventMath.ParseColor(palette?[0] ?? evt.ColorHex);
        colors[0] = new SKColor(er, eg, eb);
        for (int k = 0; k < CelebrationMath.PartyColors.Length; k++)
        {
            var (r, g, b) = ShowEventMath.ParseColor(palette != null ? palette[(k + 1) % palette.Length] : CelebrationMath.PartyColors[k]);
            colors[k + 1] = new SKColor(r, g, b);
        }
        for (int i = 0; i < visible; i++)
        {
            var p = _particles[i];
            int slot = p.ColorIndex <= 0 ? 0 : (p.ColorIndex - 1) % CelebrationMath.PartyColors.Length + 1;   // = CelebrationMath.ColorFor
            canvas.Save();
            canvas.RotateDegrees(p.RotationDeg, p.X + p.Width / 2f, p.Y + p.Height / 2f);
            canvas.DrawRect(SKRect.Create(p.X, p.Y, p.Width, p.Height), Fill(colors[slot], env));
            canvas.Restore();
        }
    }

    /// <summary>Traversal image or Announce text, one lap (TraversalMath), wrap copies on a Ring. Skipped until decoded.</summary>
    private void DrawEventSprite(SKCanvas canvas, ShowEvent evt, long local)
    {
        if (!TryGetSprite(evt.EventId, out var sprite)) return;
        int cw = _layout.CanvasWidth > 0 ? _layout.CanvasWidth : _layout.Width;
        int ch = _layout.CanvasHeight > 0 ? _layout.CanvasHeight : _layout.Height;
        if (TraversalMath.Pose(evt, cw, ch, _layout.Wraps, local) is not { } p || p.Opacity <= 0.005f) return;

        var frame = FrameAt(sprite, local);
        Span<float> copies = stackalloc float[2];
        int nCopies = NodeMapping.WrapCopies(p.VirtualX, p.Width, _layout, copies);
        float ly = NodeMapping.ToLocalY(p.VirtualY, _layout);
        for (int c = 0; c < nCopies; c++)
        {
            float lx = NodeMapping.ToLocalX(copies[c], p.Width, _layout);
            if (!NodeMapping.IsVisible(lx, p.Width, _layout)) continue;
            DrawImage(canvas, frame, SKRect.Create(lx, ly, p.Width, p.Height), p.Opacity);
        }
    }

    /// <summary>Identify: dimmed screen, this seat's label centred at up to 80 % width / 40 % height.</summary>
    private void DrawIdentify(SKCanvas canvas, ShowEvent evt, long local)
    {
        float env = ShowEventMath.Envelope(local, evt.DurationMs);
        if (env <= 0.005f) return;
        FillNode(canvas, Fill(SKColors.Black, 0.7f * env));
        if (!TryGetSprite(evt.EventId, out var sprite)) return;
        var img = sprite.Frames[0];
        if (img.Width <= 0 || img.Height <= 0) return;
        float k = Math.Min(_layout.Width * 0.8f / img.Width, _layout.Height * 0.4f / img.Height);
        float w = img.Width * k, h = img.Height * k;
        DrawImage(canvas, img, SKRect.Create((_layout.Width - w) / 2f, (_layout.Height - h) / 2f, w, h), env);
    }

    public void Dispose()
    {
        lock (_spriteLock)
        {
            foreach (var s in _sprites.Values) s.Dispose();
            _sprites.Clear();
        }
        _paint.Dispose();
    }
}

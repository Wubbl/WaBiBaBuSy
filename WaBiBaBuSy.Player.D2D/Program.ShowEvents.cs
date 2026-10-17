// WaBiBaBuSy.Player.D2D/Program.ShowEvents.cs
using System.Drawing;
using System.Numerics;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;
using WaBiBaBuSy.WallpaperEngine.Show;

namespace WaBiBaBuSy.Player.D2D;

/// <summary>
/// One-shot show events (roadmap 2.4, design 2026-10-03-show-events-design.md): kept from cmd_show_event, drawn above
/// the scene from T0 for their duration. Math in WaBiBaBuSy.Models/Show. The stdin thread adds events and decodes
/// sprite images on a worker task; the render thread uploads and draws.
/// </summary>
partial class Program
{
    private sealed class EventSpriteFrames : IDisposable
    {
        public required List<Bitmap> Frames { get; init; }
        public required List<int> DelaysMs { get; init; }
        public void Dispose() { foreach (var f in Frames) f.Dispose(); }
    }

    private sealed class EventSpriteGpu : IDisposable
    {
        public required ID2D1Bitmap[] Frames { get; init; }
        public required int[] DelaysMs { get; init; }
        public required long TotalMs { get; init; }
        public void Dispose() { foreach (var f in Frames) f.Dispose(); }
    }

    private const int MaxEventSpriteHeight = 1024;
    private const long MaxEventSpriteBytes = 64L * 1024 * 1024;   // decoded size cap per sprite (GDI+ copy, again in VRAM)

    private static readonly object _showEventLock = new();
    private static readonly ShowEventLog _showEvents = new();                                         // own lock
    private static readonly Dictionary<string, EventSpriteFrames> _decodedEventSprites = new();      // guarded by _showEventLock
    private static readonly HashSet<string> _decodingEventSprites = new();                           // guarded by _showEventLock
    private static readonly Dictionary<string, EventSpriteGpu> _eventSprites = new();                // render thread only
    private static readonly ShowEvent[] _showEventScratch = new ShowEvent[ShowEventLog.Capacity];   // render thread only
    private static CelebrationParticle[] _particleScratch = new CelebrationParticle[512];           // render thread only
    private static readonly List<string> _staleSpriteScratch = new();                                // render thread only
    private static readonly Color4[] _celebrationColors = new Color4[6];                            // render thread only
    private static ID2D1SolidColorBrush? _eventBrush;
    private static long _lastEventPruneUtcMs;

    /// <summary>stdin thread: keep the event (a replay with a known id is ignored) and start decoding its sprite.</summary>
    private static void HandleShowEventCommand(PlayerCommandShowEvent cmd)
    {
        var evt = cmd.Event;
        if (evt == null || !_showEvents.Add(evt)) return;
        _logger?.LogInformation("[Event] {Type} {Id} T0={T0} ({InMs} ms from now) duration={Duration}ms",
            evt.Type, evt.EventId, evt.T0Utc, evt.T0Utc - NowUtcMs, evt.DurationMs);
        StartEventSpriteDecode(evt);
    }

    /// <summary>Decode the event's sprite on a worker task unless it is decoded, decoding or uploaded already.</summary>
    private static void StartEventSpriteDecode(ShowEvent evt)
    {
        Func<EventSpriteFrames>? decode = evt.Type switch
        {
            ShowEventType.Traversal when !string.IsNullOrEmpty(evt.ImagePath) => () => DecodeImageFrames(evt.ImagePath!),
            ShowEventType.Announce when !string.IsNullOrWhiteSpace(evt.Text) => () => new EventSpriteFrames
            {
                Frames = new List<Bitmap> { TextSprite.Render(evt.Text!, evt.ColorHex) },
                DelaysMs = new List<int> { 100 },
            },
            // Each seat renders its own label: the number the room view orders by, and this machine's name.
            ShowEventType.Identify => () => new EventSpriteFrames
            {
                Frames = new List<Bitmap> { TextSprite.Render($"{_layout.Order + 1}  {Environment.MachineName}", evt.ColorHex) },
                DelaysMs = new List<int> { 100 },
            },
            _ => null,
        };
        if (decode == null)
        {
            if (evt.Type == ShowEventType.Traversal) _logger?.LogWarning("[Event] Traversal {Id} has no image on this machine; skipped", evt.EventId);
            return;
        }
        lock (_showEventLock)
        {
            if (_decodingEventSprites.Contains(evt.EventId) || _decodedEventSprites.ContainsKey(evt.EventId)) return;
            _decodingEventSprites.Add(evt.EventId);
        }
        _ = Task.Run(() => DecodeEventSprite(evt.EventId, decode));
    }

    private static void DecodeEventSprite(string eventId, Func<EventSpriteFrames> decode)
    {
        EventSpriteFrames? frames = null;
        try
        {
            frames = decode();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Event] Could not decode the sprite of {Id}; skipped", eventId);
        }
        lock (_showEventLock)
        {
            _decodingEventSprites.Remove(eventId);
            if (frames != null && _showEvents.Contains(eventId)) { _decodedEventSprites[eventId] = frames; frames = null; }
        }
        frames?.Dispose();   // the event was pruned meanwhile
    }

    /// <summary>All frames of an image / GIF (coalesced), at most 1024 px high, as GDI+ bitmaps (uploaded later on the render thread).</summary>
    private static EventSpriteFrames DecodeImageFrames(string path)
    {
        using var collection = new MagickImageCollection(path);
        collection.Coalesce();
        var frames = new List<Bitmap>(collection.Count);
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
                _logger?.LogWarning("[Event] Sprite {Path} exceeds {Cap} MB decoded; kept the first {Kept} of {Total} frames",
                    path, MaxEventSpriteBytes / (1024 * 1024), frames.Count, collection.Count);
                break;
            }
            frames.Add(frame.ToBitmap());
            int delay = (int)(frame.AnimationDelay * 10);
            delays.Add(delay <= 0 ? 100 : Math.Min(delay, 1000));
        }
        return new EventSpriteFrames { Frames = frames, DelaysMs = delays };
    }

    /// <summary>Render thread: decoded sprites → GPU; GPU sprites of events that left the log are released.</summary>
    private static void SyncEventSprites(int eventCount)
    {
        List<KeyValuePair<string, EventSpriteFrames>>? ready = null;
        lock (_showEventLock)
        {
            if (_decodedEventSprites.Count > 0)
            {
                ready = _decodedEventSprites.ToList();   // rare (once per event), not per frame
                _decodedEventSprites.Clear();
            }
        }
        if (ready != null)
        {
            foreach (var (id, frames) in ready)
            {
                using (frames)
                {
                    var gpu = new ID2D1Bitmap[frames.Frames.Count];
                    try
                    {
                        for (int k = 0; k < gpu.Length; k++) gpu[k] = UploadBitmapToD2D(frames.Frames[k]);
                        _eventSprites[id] = new EventSpriteGpu { Frames = gpu, DelaysMs = frames.DelaysMs.ToArray(), TotalMs = Math.Max(1, frames.DelaysMs.Sum()) };
                    }
                    catch (Exception ex)
                    {
                        foreach (var b in gpu) b?.Dispose();   // frames uploaded before the failing one
                        _logger?.LogWarning(ex, "[Event] Could not upload the sprite of {Id}", id);
                    }
                }
            }
        }

        if (_eventSprites.Count == 0) return;
        _staleSpriteScratch.Clear();
        foreach (var id in _eventSprites.Keys)
        {
            bool known = false;
            for (int i = 0; i < eventCount && !known; i++) known = _showEventScratch[i].EventId == id;
            if (!known) _staleSpriteScratch.Add(id);
        }
        foreach (var id in _staleSpriteScratch)
        {
            _eventSprites[id].Dispose();
            _eventSprites.Remove(id);
        }
    }

    /// <summary>Draw order: sprites and confetti, then the spotlight (dims them too), then blackout, then identify on top.</summary>
    private static int EventLayer(ShowEventType type) => type switch
    {
        ShowEventType.Spotlight => 1,
        ShowEventType.Blackout => 2,
        ShowEventType.Identify => 3,
        _ => 0,
    };

    /// <summary>
    /// Render thread: draw the active events above the scene. <paramref name="sceneRawElapsedMs"/> is the un-phased scene
    /// elapsed (live: now − shared start; exact frame: the probe's elapsed), so exact frames show events exactly as live.
    /// </summary>
    private static void DrawShowEvents(long sceneRawElapsedMs)
    {
        if (_d2dContext == null || (_showEvents.Count == 0 && _eventSprites.Count == 0)) return;

        long now = NowUtcMs;
        if (now - _lastEventPruneUtcMs > 1000)
        {
            _lastEventPruneUtcMs = now;
            _showEvents.Prune(now);
        }
        int n = _showEvents.CopyTo(_showEventScratch);
        SyncEventSprites(n);
        if (n == 0) return;

        long sceneStartUtc = new DateTimeOffset(_renderLoopStart).ToUnixTimeMilliseconds();
        _eventBrush ??= _d2dContext.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        var prior = _d2dContext.Transform;
        var canvas = Matrix3x2.CreateScale(Scale) * prior;   // canvas units → device px
        _d2dContext.Transform = canvas;
        try
        {
            for (int layer = 0; layer <= 3; layer++)
            {
                for (int i = 0; i < n; i++)
                {
                    var evt = _showEventScratch[i];
                    if (EventLayer(evt.Type) != layer) continue;
                    long local = ShowEventMath.LocalMs(evt, sceneStartUtc, sceneRawElapsedMs);
                    if (!ShowEventMath.IsActive(evt, local)) continue;
                    switch (evt.Type)
                    {
                        case ShowEventType.Blackout: DrawBlackout(evt, local); break;
                        case ShowEventType.Spotlight: DrawSpotlight(evt, local); break;
                        case ShowEventType.Celebration: DrawCelebration(evt, local, canvas); break;
                        case ShowEventType.Traversal:
                        case ShowEventType.Announce: DrawEventSprite(evt, local); break;
                        case ShowEventType.Identify: DrawIdentify(evt, local); break;
                    }
                }
            }
        }
        finally
        {
            _d2dContext.Transform = prior;
        }
    }

    private static void SetEventBrush(string hex, float opacity)
    {
        var (r, g, b) = ShowEventMath.ParseColor(hex);
        _eventBrush!.Color = new Color4(r / 255f, g / 255f, b / 255f, 1f);
        _eventBrush.Opacity = Math.Clamp(opacity, 0f, 1f);
    }

    private static void SetEventBrushBlack(float opacity)
    {
        _eventBrush!.Color = new Color4(0f, 0f, 0f, 1f);
        _eventBrush.Opacity = Math.Clamp(opacity, 0f, 1f);
    }

    private static void DrawBlackout(ShowEvent evt, long local)
    {
        SetEventBrush(evt.ColorHex, ShowEventMath.BlackoutOpacity(evt, local));
        _d2dContext!.FillRectangle(new System.Drawing.RectangleF(0, 0, _layout.Width, _layout.Height), _eventBrush!);
    }

    private static void DrawSpotlight(ShowEvent evt, long local)
    {
        var v = SpotlightMath.Visual(evt, _layout, sharedCanvas: !_perMonitorMode, local);
        if (v.IsTarget)
        {
            if (v.BorderOpacity <= 0.005f) return;
            SetEventBrush(evt.ColorHex, v.BorderOpacity);
            float half = v.BorderWidth / 2f;
            _d2dContext!.DrawRectangle(new System.Drawing.RectangleF(half, half, _layout.Width - v.BorderWidth, _layout.Height - v.BorderWidth), _eventBrush!, v.BorderWidth);
            return;
        }
        if (v.DimOpacity > 0.005f)
        {
            SetEventBrushBlack(v.DimOpacity);
            _d2dContext!.FillRectangle(new System.Drawing.RectangleF(0, 0, _layout.Width, _layout.Height), _eventBrush!);
        }
        if (v.ChevronDirection != 0 && v.ChevronOpacity > 0.005f)
        {
            SetEventBrush(evt.ColorHex, v.ChevronOpacity);
            float half = v.ChevronSize / 2f, d = v.ChevronDirection;
            var tip = new Vector2(v.ChevronCenterX + d * half, v.ChevronCenterY);
            var top = new Vector2(v.ChevronCenterX - d * half, v.ChevronCenterY - half);
            var bottom = new Vector2(v.ChevronCenterX - d * half, v.ChevronCenterY + half);
            float stroke = v.ChevronSize * 0.18f;
            _d2dContext!.DrawLine(top, tip, _eventBrush!, stroke);
            _d2dContext.DrawLine(bottom, tip, _eventBrush!, stroke);
        }
    }

    private static void DrawCelebration(ShowEvent evt, long local, Matrix3x2 canvas)
    {
        int count = CelebrationMath.Count(_layout.CanvasWidth > 0 ? _layout.CanvasWidth : _layout.Width);
        int capacity = CelebrationMath.Capacity(_layout, count);
        if (_particleScratch.Length < capacity) _particleScratch = new CelebrationParticle[capacity];   // grows once per layout
        int visible = CelebrationMath.VisibleOnNode(evt, _layout, local, _particleScratch);
        float env = ShowEventMath.Envelope(local, evt.DurationMs);
        if (env <= 0.005f) return;
        // Slot 0 = the event color, 1..5 = the party colors; parsed once per call, not per particle.
        var (er, eg, eb) = ShowEventMath.ParseColor(evt.ColorHex);
        _celebrationColors[0] = new Color4(er / 255f, eg / 255f, eb / 255f, 1f);
        for (int k = 0; k < CelebrationMath.PartyColors.Length; k++)
        {
            var (r, g, b) = ShowEventMath.ParseColor(CelebrationMath.PartyColors[k]);
            _celebrationColors[k + 1] = new Color4(r / 255f, g / 255f, b / 255f, 1f);
        }
        _eventBrush!.Opacity = Math.Clamp(env, 0f, 1f);
        try
        {
            for (int i = 0; i < visible; i++)
            {
                var p = _particleScratch[i];
                int slot = p.ColorIndex <= 0 ? 0 : (p.ColorIndex - 1) % CelebrationMath.PartyColors.Length + 1;   // = CelebrationMath.ColorFor
                _eventBrush.Color = _celebrationColors[slot];
                var center = new Vector2(p.X + p.Width / 2f, p.Y + p.Height / 2f);
                _d2dContext!.Transform = Matrix3x2.CreateRotation(p.RotationDeg * MathF.PI / 180f, center) * canvas;
                _d2dContext.FillRectangle(new System.Drawing.RectangleF(p.X, p.Y, p.Width, p.Height), _eventBrush!);
            }
        }
        finally
        {
            _d2dContext!.Transform = canvas;
        }
    }

    /// <summary>Traversal image or Announce text bitmap, one lap (TraversalMath), wrap copies on a Ring. Skipped until decoded.</summary>
    private static void DrawEventSprite(ShowEvent evt, long local)
    {
        // Decoding starts only from HandleShowEventCommand (failures log once); until the upload the event is skipped.
        if (!_eventSprites.TryGetValue(evt.EventId, out var sprite)) return;
        int cw = _layout.CanvasWidth > 0 ? _layout.CanvasWidth : _layout.Width;
        int ch = _layout.CanvasHeight > 0 ? _layout.CanvasHeight : _layout.Height;
        var pose = TraversalMath.Pose(evt, cw, ch, _layout.Wraps, local);
        if (pose is not { } p || p.Opacity <= 0.005f) return;

        var frame = sprite.Frames[0];
        if (sprite.Frames.Length > 1)
        {
            long t = SyncTiming.PositiveModulo(local, sprite.TotalMs);
            for (int f = 0; f < sprite.DelaysMs.Length; f++)
            {
                if (t < sprite.DelaysMs[f]) { frame = sprite.Frames[f]; break; }
                t -= sprite.DelaysMs[f];
            }
        }

        Span<float> copies = stackalloc float[2];
        int nCopies = NodeMapping.WrapCopies(p.VirtualX, p.Width, _layout, copies);
        float ly = NodeMapping.ToLocalY(p.VirtualY, _layout);
        for (int c = 0; c < nCopies; c++)
        {
            float lx = NodeMapping.ToLocalX(copies[c], p.Width, _layout);
            if (!NodeMapping.IsVisible(lx, p.Width, _layout)) continue;
            _d2dContext!.DrawBitmap(frame, new System.Drawing.RectangleF(lx, ly, p.Width, p.Height), p.Opacity, BitmapInterpolationMode.Linear, null);
        }
    }

    /// <summary>Identify: dimmed screen, this seat's label centered at up to 80 % width / 40 % height. Skipped until rendered.</summary>
    private static void DrawIdentify(ShowEvent evt, long local)
    {
        float env = ShowEventMath.Envelope(local, evt.DurationMs);
        if (env <= 0.005f) return;
        SetEventBrushBlack(0.7f * env);
        _d2dContext!.FillRectangle(new System.Drawing.RectangleF(0, 0, _layout.Width, _layout.Height), _eventBrush!);
        if (!_eventSprites.TryGetValue(evt.EventId, out var sprite)) return;
        var bmp = sprite.Frames[0];
        var size = bmp.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        float k = Math.Min(_layout.Width * 0.8f / size.Width, _layout.Height * 0.4f / size.Height);
        float w = size.Width * k, h = size.Height * k;
        var dest = new System.Drawing.RectangleF((_layout.Width - w) / 2f, (_layout.Height - h) / 2f, w, h);
        _d2dContext.DrawBitmap(bmp, dest, env, BitmapInterpolationMode.Linear, null);
    }

    /// <summary>Release the event brush and GPU sprites (exit / final cleanup only: scene loads keep the D2D device, so these survive them).</summary>
    private static void DisposeShowEventResources()
    {
        _eventBrush?.Dispose();
        _eventBrush = null;
        foreach (var s in _eventSprites.Values) s.Dispose();
        _eventSprites.Clear();
    }
}

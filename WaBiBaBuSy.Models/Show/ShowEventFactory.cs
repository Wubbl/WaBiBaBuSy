using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Show;

/// <summary>Everything the server knows when an event is fired.</summary>
/// <param name="Layout">Layout of the running scene (its seat map).</param>
/// <param name="PerMonitor">Simultaneous distribution: every node's own monitor is its canvas.</param>
/// <param name="Seed">Drawn once by the server (broadcast — determinism holds).</param>
/// <param name="ImagePath">Traversal: the image file (the caller resolves "empty = scene sprite").</param>
/// <param name="SpriteAspect">Traversal: image width ÷ height; Announce: measured text aspect.</param>
public sealed record ShowEventRequest(
    ShowEventType Type, long NowUtcMs, double MaxRttMs, SeatMapLayoutResult? Layout, bool PerMonitor,
    string? TargetSeatId, int Seed, string EventId, string? ImagePath = null, float SpriteAspect = 1f, string? Text = null);

/// <summary>Builds a <see cref="ShowEvent"/> from a request and the trigger defaults (design §3.8). Pure.</summary>
public static class ShowEventFactory
{
    private const int FallbackCanvasW = 1920, FallbackCanvasH = 1080;

    /// <summary>The event, or null with <paramref name="error"/> (a sentence for the status line) when it cannot be built.</summary>
    public static ShowEvent? Create(ShowEventRequest r, ShowEventSettings s, out string? error)
    {
        error = null;
        var evt = new ShowEvent
        {
            EventId = r.EventId,
            Type = r.Type,
            T0Utc = r.NowUtcMs + SyncTiming.ComputeStartLeadMs(r.MaxRttMs, 0),
            Seed = r.Seed,
        };
        var (canvasW, canvasH, wraps) = Canvas(r);

        switch (r.Type)
        {
            case ShowEventType.Spotlight:
                var node = string.IsNullOrEmpty(r.TargetSeatId) ? null : r.Layout?.Get(r.TargetSeatId);
                if (node == null) { error = "Pick a seat for the spotlight."; return null; }
                evt.DurationMs = s.SpotlightDurationMs;
                evt.ColorHex = s.SpotlightColorHex;
                evt.TargetOrder = node.Order;
                evt.TargetCenterX = node.OffsetX + node.Width / 2f;
                break;

            case ShowEventType.Celebration:
                evt.DurationMs = s.CelebrationDurationMs;
                evt.ColorHex = s.CelebrationColorHex;
                break;

            case ShowEventType.Blackout:
                evt.DurationMs = s.BlackoutDurationMs;
                evt.ColorHex = s.BlackoutColorHex;
                break;

            case ShowEventType.Traversal:
                if (string.IsNullOrWhiteSpace(r.ImagePath)) { error = "No image for the traversal (pick one, or play a scene with a sprite)."; return null; }
                evt.ImagePath = r.ImagePath;
                evt.SpriteAspect = r.SpriteAspect;
                evt.HeightFraction = s.TraversalHeightFraction;
                evt.Reverse = s.TraversalReverse;
                evt.DurationMs = TraversalMath.DurationMs(canvasW, TraversalMath.SpriteSize(evt, canvasH).W, wraps, s.TraversalSpeedPxPerSecond);
                break;

            case ShowEventType.Announce:
                if (string.IsNullOrWhiteSpace(r.Text)) { error = "Type the announcement text first."; return null; }
                evt.Text = r.Text.Trim();
                evt.SpriteAspect = r.SpriteAspect;
                evt.HeightFraction = s.AnnounceHeightFraction;
                evt.ColorHex = s.AnnounceColorHex;
                evt.DurationMs = TraversalMath.DurationMs(canvasW, TraversalMath.SpriteSize(evt, canvasH).W, wraps, s.AnnounceSpeedPxPerSecond);
                break;
        }

        evt.DurationMs = ShowEventMath.ClampDuration(evt.DurationMs);
        return evt;
    }

    /// <summary>Canvas a lap runs over: the shared canvas, or (Simultaneous) the widest / tallest node, never wrapping.</summary>
    private static (int W, int H, bool Wraps) Canvas(ShowEventRequest r)
    {
        var layout = r.Layout;
        if (layout == null || layout.Nodes.Count == 0) return (FallbackCanvasW, FallbackCanvasH, false);
        if (r.PerMonitor) return (layout.Nodes.Max(n => n.Width), layout.Nodes.Max(n => n.Height), false);
        return (layout.CanvasWidth, layout.CanvasHeight, layout.Wraps);
    }
}

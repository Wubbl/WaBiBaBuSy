using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Two 1000 px nodes side by side; a 100 px sprite moves left→right at 1000 px/s.</summary>
public class BezelEffectsTests
{
    private const int W = 1000, H = 600, Sprite = 100;
    private static readonly MovementConfig Linear = new() { Type = MovementType.Linear, SpeedPixelsPerSecond = 1000 };

    private static NodeLayout Node(int offsetX, bool mirrored = false, bool wraps = false, int canvasW = 2 * W) => new()
    {
        Id = $"n{offsetX}", OffsetX = offsetX, Width = W, Height = H,
        CanvasWidth = canvasW, CanvasHeight = H, Mirrored = mirrored, Wraps = wraps, Order = offsetX / W,
    };

    private static SpritePoseInput Input(NodeLayout node, MovementConfig? movement = null) =>
        new(movement ?? Linear, Sprite, Sprite, node.CanvasWidth, node.CanvasHeight, 0f, node);

    private static readonly SpriteEffectsConfig Both = new() { Entry = BezelEffect.Ripple, Exit = BezelEffect.Glow, BezelDurationMs = 400 };

    /// <summary>First grid time (multiple of 16 ms) at which the sprite overlaps the node — from the movement itself.</summary>
    private static long FirstVisibleOn(NodeLayout node)
    {
        for (long t = 0; t < 10_000; t += SpriteEffects.BezelScanStepMs)
            if (SpriteEffects.IsOnNode(Input(node), t, out _, out _)) return t;
        throw new InvalidOperationException("never visible");
    }

    /// <summary>First grid time at or after <paramref name="from"/> at which the sprite is no longer on the node.</summary>
    private static long FirstGoneFrom(NodeLayout node, long from)
    {
        for (long t = from; t < 10_000; t += SpriteEffects.BezelScanStepMs)
            if (!SpriteEffects.IsOnNode(Input(node), t, out _, out _)) return t;
        throw new InvalidOperationException("never gone");
    }

    [Fact]
    public void Entry_OnTheNextNode_AtItsLeftEdge()
    {
        var right = Node(W);
        long tEnter = FirstVisibleOn(right);
        var ev = SpriteEffects.FindBezelEvent(Input(right), Both, tEnter + 100);

        Assert.NotNull(ev);
        Assert.Equal(BezelEventKind.Entry, ev!.Value.Kind);
        Assert.Equal(BezelEffect.Ripple, ev.Value.Effect);
        Assert.False(ev.Value.AtRightEdge);
        Assert.Equal(tEnter, ev.Value.EventElapsedMs);
        Assert.Equal(100f / 400f, ev.Value.Progress, 3);
    }

    [Fact]
    public void Exit_OnTheFirstNode_AtItsRightEdge()
    {
        var left = Node(0);
        long tGone = FirstGoneFrom(left, FirstVisibleOn(Node(W)));

        var ev = SpriteEffects.FindBezelEvent(Input(left), Both, tGone + 50);

        Assert.NotNull(ev);
        Assert.Equal(BezelEventKind.Exit, ev!.Value.Kind);
        Assert.Equal(BezelEffect.Glow, ev.Value.Effect);
        Assert.True(ev.Value.AtRightEdge);
    }

    [Fact]
    public void EventTime_IsTheSameForEveryFrameTime()
    {
        var right = Node(W);
        long tEnter = FirstVisibleOn(right);
        // Frames at arbitrary sub-grid instants (as on different machines) agree on the crossing time.
        var a = SpriteEffects.FindBezelEvent(Input(right), Both, tEnter + 37)!.Value;
        var b = SpriteEffects.FindBezelEvent(Input(right), Both, tEnter + 203)!.Value;
        Assert.Equal(a.EventElapsedMs, b.EventElapsedMs);
    }

    [Fact]
    public void AfterTheDuration_NoEvent()
    {
        var right = Node(W);
        Assert.Null(SpriteEffects.FindBezelEvent(Input(right), Both, FirstVisibleOn(right) + 450));
    }

    [Fact]
    public void SceneStart_SpriteAlreadyOnTheNode_NoEntry()
    {
        // Bounce starts on the canvas: visible on the first node from t = 0, which is not a crossing.
        var bounce = new MovementConfig { Type = MovementType.Bounce, SpeedPixelsPerSecond = 10 };
        var left = Node(0);
        Assert.True(SpriteEffects.IsOnNode(Input(left, bounce), 0, out _, out _));
        Assert.Null(SpriteEffects.FindBezelEvent(Input(left, bounce), Both, 100));
    }

    [Fact]
    public void NegativeElapsed_NoEvent() =>
        Assert.Null(SpriteEffects.FindBezelEvent(Input(Node(W)), Both, -500));

    [Fact]
    public void OnlyEntryConfigured_ExitIsIgnored()
    {
        var entryOnly = new SpriteEffectsConfig { Entry = BezelEffect.Flash };
        var left = Node(0);
        long tGone = FirstGoneFrom(left, FirstVisibleOn(Node(W)));
        Assert.Null(SpriteEffects.FindBezelEvent(Input(left), entryOnly, tGone + 50));
    }

    [Fact]
    public void MirroredNode_EntryIsAtTheRightScreenEdge()
    {
        // Facing row: the slice is mirrored, so a sprite entering at virtual x = 1000 appears at the right screen edge.
        var right = Node(W, mirrored: true);
        long tEnter = FirstVisibleOn(right);
        var ev = SpriteEffects.FindBezelEvent(Input(right), Both, tEnter + 50)!.Value;
        Assert.Equal(BezelEventKind.Entry, ev.Kind);
        Assert.True(ev.AtRightEdge);
    }

    [Fact]
    public void RingSeam_SeatZeroSeesOneEntry()
    {
        // Ring of two seats: the sprite straddles the seam at x = 2000 and re-enters seat 0 through its wrap copy.
        var seat0 = Node(0, wraps: true);
        var input = Input(seat0);
        long t = 0;
        while (SpriteEffects.IsOnNode(input, t, out _, out _)) t += SpriteEffects.BezelScanStepMs;
        while (!SpriteEffects.IsOnNode(input, t, out _, out _)) t += SpriteEffects.BezelScanStepMs;
        long tBack = t;

        var ev = SpriteEffects.FindBezelEvent(input, Both, tBack + 60)!.Value;
        Assert.Equal(BezelEventKind.Entry, ev.Kind);
        Assert.Equal(tBack, ev.EventElapsedMs);
        Assert.False(ev.AtRightEdge);
        // While the sprite straddles the seam it stays visible on seat 0 — no exit in between.
        for (long u = tBack; u < tBack + 300; u += 8)
            Assert.True(SpriteEffects.IsOnNode(input, u, out _, out _));
    }

    [Fact]
    public void BezelDuration_IsClamped()
    {
        Assert.Equal(50, SpriteEffects.BezelDurationMs(new SpriteEffectsConfig { BezelDurationMs = -5 }));
        Assert.Equal(3000, SpriteEffects.BezelDurationMs(new SpriteEffectsConfig { BezelDurationMs = 999_999 }));
    }

    [Fact]
    public void FacingLeftAt_FollowsTheScreenDirection()
    {
        var left = Node(0);
        Assert.False(SpriteEffects.FacingLeftAt(Input(left), 500, fallback: true));                     // moving right
        Assert.True(SpriteEffects.FacingLeftAt(Input(Node(0, mirrored: true)), 500, fallback: false));  // mirrored: screen-left
        var still = new MovementConfig { Type = MovementType.Static };
        Assert.True(SpriteEffects.FacingLeftAt(Input(left, still), 500, fallback: true));              // no motion keeps fallback
    }

    // ── Visual geometry ────────────────────────────────────────────────

    private static BezelEvent Event(BezelEffect effect, float progress, bool right) =>
        new(BezelEventKind.Entry, effect, 1000, progress, right, CenterVirtualY: 300);

    [Fact]
    public void Visual_Glow_IsAStripAtTheEdge()
    {
        var node = Node(W);
        var right = SpriteEffects.Visual(Event(BezelEffect.Glow, 0.25f, right: true), node);
        Assert.Equal(W - SpriteEffects.GlowWidth, right.X);
        Assert.Equal(SpriteEffects.GlowWidth, right.Width);
        Assert.Equal(H, right.Height);
        Assert.Equal(0.75f, right.Opacity, 3);
        Assert.True(right.EdgeOnRight);
        Assert.Equal(0f, SpriteEffects.Visual(Event(BezelEffect.Glow, 0.25f, right: false), node).X);
    }

    [Fact]
    public void Visual_Flash_FadesQuadratically() =>
        Assert.Equal(0.35f * 0.25f, SpriteEffects.Visual(Event(BezelEffect.Flash, 0.5f, right: false), Node(W)).Opacity, 4);

    [Fact]
    public void Visual_Ripple_GrowsFromTheCrossingPoint()
    {
        var v = SpriteEffects.Visual(Event(BezelEffect.Ripple, 0.5f, right: true), Node(W));
        Assert.Equal(W, v.X);
        Assert.Equal(300f, v.Y);                                     // CenterVirtualY − node.OffsetY (0)
        Assert.Equal(SpriteEffects.RippleMaxRadius / 2f, v.Radius, 3);
        Assert.Equal(0.5f, v.Opacity, 3);
    }

    [Fact]
    public void Visual_NarrowNode_GlowIsAtMostHalfTheWidth() =>
        Assert.Equal(100f, SpriteEffects.Visual(Event(BezelEffect.Glow, 0f, right: false), new NodeLayout { Width = 200, Height = 100 }).Width);
}

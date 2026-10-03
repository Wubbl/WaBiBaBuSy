using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ShowEventFactoryTests
{
    private static readonly ShowEventSettings Settings = new();

    private static SeatMapLayoutResult Layout(bool wraps = false) => new()
    {
        CanvasWidth = 3000, CanvasHeight = 1000, Wraps = wraps,
        Nodes = new[]
        {
            new NodeLayout { Id = "a", Order = 0, OffsetX = 0, Width = 1000, Height = 1000 },
            new NodeLayout { Id = "b", Order = 1, OffsetX = 1000, Width = 1000, Height = 1000 },
            new NodeLayout { Id = "c", Order = 2, OffsetX = 2000, Width = 1400, Height = 900 },
        },
    };

    private static ShowEventRequest Req(ShowEventType type, string? seat = null, double rtt = 0, bool wraps = false, bool perMonitor = false,
        string? image = null, float aspect = 1f, string? text = null) =>
        new(type, NowUtcMs: 100_000, MaxRttMs: rtt, Layout(wraps), perMonitor, seat, Seed: 7, EventId: "id-1", image, aspect, text);

    [Theory]
    [InlineData(0.0, 800)]
    [InlineData(500.0, 1500)]
    [InlineData(5000.0, 4000)]
    public void T0_IsNowPlusTheStartLead(double rtt, int lead)
    {
        var e = ShowEventFactory.Create(Req(ShowEventType.Celebration, rtt: rtt), Settings, out _)!;
        Assert.Equal(100_000 + lead, e.T0Utc);
    }

    [Fact]
    public void CarriesIdAndSeed()
    {
        var e = ShowEventFactory.Create(Req(ShowEventType.Blackout), Settings, out _)!;
        Assert.Equal("id-1", e.EventId);
        Assert.Equal(7, e.Seed);
        Assert.Equal(3000, e.DurationMs);
        Assert.Equal("#000000", e.ColorHex);
    }

    [Fact]
    public void Spotlight_TargetsTheSeatsOrderAndCenter()
    {
        var e = ShowEventFactory.Create(Req(ShowEventType.Spotlight, seat: "b"), Settings, out var error)!;
        Assert.Null(error);
        Assert.Equal(1, e.TargetOrder);
        Assert.Equal(1500f, e.TargetCenterX);
        Assert.Equal(8000, e.DurationMs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nope")]
    public void Spotlight_WithoutAKnownSeat_IsRefused(string? seat)
    {
        Assert.Null(ShowEventFactory.Create(Req(ShowEventType.Spotlight, seat: seat), Settings, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Traversal_LapDurationFromTheCanvas()
    {
        // 3000 wide canvas, sprite 500 high × aspect 2 = 1000 wide, 600 px/s: (3000 + 1000) / 600 = 6.667 s
        var e = ShowEventFactory.Create(Req(ShowEventType.Traversal, image: @"C:\logo.png", aspect: 2f), Settings, out _)!;
        Assert.Equal(6667, e.DurationMs);
        Assert.Equal(@"C:\logo.png", e.ImagePath);
        Assert.Equal(2f, e.SpriteAspect);
        Assert.Equal(0.5f, e.HeightFraction);
        var ring = ShowEventFactory.Create(Req(ShowEventType.Traversal, wraps: true, image: @"C:\logo.png", aspect: 2f), Settings, out _)!;
        Assert.Equal(5000, ring.DurationMs);
    }

    [Fact]
    public void Traversal_PerMonitor_UsesTheWidestNodeAndNeverWraps()
    {
        // widest node 1400, tallest 1000 → sprite 500 × 1 = 500 wide: (1400 + 500) / 600 = 3.167 s
        var e = ShowEventFactory.Create(Req(ShowEventType.Traversal, wraps: true, perMonitor: true, image: "x.png"), Settings, out _)!;
        Assert.Equal(3167, e.DurationMs);
    }

    [Fact]
    public void Traversal_WithoutImage_IsRefused()
    {
        Assert.Null(ShowEventFactory.Create(Req(ShowEventType.Traversal, image: " "), Settings, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Announce_WithoutText_IsRefused()
    {
        Assert.Null(ShowEventFactory.Create(Req(ShowEventType.Announce, text: ""), Settings, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Announce_CarriesTextAndAspect()
    {
        var e = ShowEventFactory.Create(Req(ShowEventType.Announce, text: "Round 3", aspect: 6f), Settings, out _)!;
        Assert.Equal("Round 3", e.Text);
        Assert.Equal(6f, e.SpriteAspect);
        Assert.Equal(0.25f, e.HeightFraction);
        Assert.Equal("#FFFFFF", e.ColorHex);
    }

    [Fact]
    public void Duration_IsClamped()
    {
        var s = new ShowEventSettings { CelebrationDurationMs = -1 };
        Assert.Equal(ShowEventMath.MinDurationMs, ShowEventFactory.Create(Req(ShowEventType.Celebration), s, out _)!.DurationMs);
    }
}

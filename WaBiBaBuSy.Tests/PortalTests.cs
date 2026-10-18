using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Portal bezel effect: two seats side by side, a 200 px sprite moving right at 400 px/s. Its left edge is
/// x = −200 + 400·t, so it straddles the seam at 1920 for t ∈ (4.8 s, 5.3 s).
/// </summary>
public class PortalTests
{
    private static readonly MovementConfig Move = new() { Type = MovementType.Linear, SpeedPixelsPerSecond = 400f, Loop = true };

    private static SpritePoseInput Seat(int offsetX) => new(Move, 200, 150, 3840, 1080, 0f,
        new NodeLayout { OffsetX = offsetX, Width = 1920, Height = 1080, CanvasWidth = 3840, CanvasHeight = 1080 });

    private static SpriteEffectsConfig Fx(BezelEffect entry, BezelEffect exit) => new() { Entry = entry, Exit = exit };

    [Fact]
    public void BothSeatsOpenTheirDoorOnTheSameFrame()
    {
        var both = Fx(BezelEffect.Portal, BezelEffect.Portal);
        foreach (long t in new long[] { 4_600, 5_000, 5_500 })
        {
            var left = SpriteEffects.FindPortal(Seat(0), both, t);
            var right = SpriteEffects.FindPortal(Seat(1920), both, t);
            Assert.NotNull(left);
            Assert.NotNull(right);
            Assert.Equal(left!.Value.Openness, right!.Value.Openness, 4);
            Assert.Equal(1920f, left.Value.X);   // exit door at the left seat's right edge
            Assert.Equal(0f, right.Value.X);     // entry door at the right seat's left edge
        }
    }

    [Fact]
    public void OpensBefore_FullyOpenWhilePassing_ClosesAfter()
    {
        var both = Fx(BezelEffect.Portal, BezelEffect.Portal);
        Assert.Null(SpriteEffects.FindPortal(Seat(1920), both, 4_300));                       // 500 ms ahead: shut
        Assert.InRange(SpriteEffects.FindPortal(Seat(1920), both, 4_600)!.Value.Openness, 0.35f, 0.65f);   // ~200 ms ahead (16 ms grid)
        Assert.Equal(1f, SpriteEffects.FindPortal(Seat(1920), both, 5_000)!.Value.Openness, 3);     // passing through
        Assert.InRange(SpriteEffects.FindPortal(Seat(1920), both, 5_500)!.Value.Openness, 0.35f, 0.65f);   // ~200 ms after
        Assert.Null(SpriteEffects.FindPortal(Seat(1920), both, 6_000));
    }

    [Fact]
    public void EntryOnly_OpensWhereTheSpriteComesIn_NotWhereItLeaves()
    {
        var entry = Fx(BezelEffect.Portal, BezelEffect.None);
        Assert.NotNull(SpriteEffects.FindPortal(Seat(1920), entry, 5_000));   // coming into the right seat
        Assert.Null(SpriteEffects.FindPortal(Seat(0), entry, 5_000));         // leaving the left seat
        var exit = Fx(BezelEffect.None, BezelEffect.Portal);
        Assert.Null(SpriteEffects.FindPortal(Seat(1920), exit, 5_000));
        Assert.NotNull(SpriteEffects.FindPortal(Seat(0), exit, 5_000));
    }

    [Fact]
    public void PortalIsNotABezelEvent_AndGlowStillIs()
    {
        // FindBezelEvent leaves the portal to FindPortal; the other effects are unchanged.
        Assert.Null(SpriteEffects.FindBezelEvent(Seat(1920), Fx(BezelEffect.Portal, BezelEffect.Portal), 5_000));
        Assert.NotNull(SpriteEffects.FindBezelEvent(Seat(1920), Fx(BezelEffect.Glow, BezelEffect.None), 5_000));
    }
}

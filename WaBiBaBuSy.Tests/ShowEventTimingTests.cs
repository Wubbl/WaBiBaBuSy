using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ShowEventTimingTests
{
    private static ShowEvent At(long t0, int duration = 4000) => new() { EventId = "e", T0Utc = t0, DurationMs = duration };

    [Fact]
    public void LocalMs_LiveAndExactFrame_Agree()
    {
        var e = At(t0: 10_500);
        long sceneStart = 10_000;
        // Live frame at wall time 12_000 → scene raw elapsed 2000. An exact frame at elapsed 2000 must see the same.
        Assert.Equal(1500, ShowEventMath.LocalMs(e, sceneStart, sceneRawElapsedMs: 12_000 - sceneStart));
        Assert.Equal(1500, ShowEventMath.LocalMs(e, sceneStart, sceneRawElapsedMs: 2000));
    }

    [Theory]
    [InlineData(10_000L)]
    [InlineData(11_900L)]
    [InlineData(-5_000L)]
    public void LocalMs_SameWallInstant_SameValueForAnySceneStart(long sceneStart)
    {
        // A replay after a playlist item switch: new scene start, same wall instant → same event position.
        var e = At(t0: 11_000);
        long wall = 13_250;
        Assert.Equal(2250, ShowEventMath.LocalMs(e, sceneStart, wall - sceneStart));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(3999, true)]
    [InlineData(4000, false)]
    public void IsActive_HalfOpenWindow(long local, bool expected) => Assert.Equal(expected, ShowEventMath.IsActive(At(0), local));

    [Theory]
    [InlineData(0, 250)]
    [InlineData(-5, 250)]
    [InlineData(100, 250)]
    [InlineData(5000, 5000)]
    [InlineData(int.MaxValue, 600_000)]
    public void Duration_Clamped(int input, int expected) => Assert.Equal(expected, ShowEventMath.ClampDuration(input));

    [Fact]
    public void Ended_LateJoinerSeesExpiredEventAsEnded()
    {
        var e = At(t0: 1000, duration: 2000);
        Assert.False(ShowEventMath.Ended(e, 2999));
        Assert.True(ShowEventMath.Ended(e, 3000));
        Assert.False(ShowEventMath.Ended(e, 0));         // T0 in the future: not ended
    }

    [Theory]
    [InlineData(-1, 0f)]
    [InlineData(0, 0f)]
    [InlineData(125, 0.5f)]
    [InlineData(250, 1f)]
    [InlineData(2000, 1f)]
    [InlineData(3875, 0.5f)]
    [InlineData(4000, 0f)]
    public void Envelope_FadesInAndOut(long local, float expected) =>
        Assert.Equal(expected, ShowEventMath.Envelope(local, 4000), 3);

    [Fact]
    public void Envelope_ShortEvent_FadeIsAtMostHalf() =>
        Assert.Equal(1f, ShowEventMath.Envelope(125, 250), 3);   // 250 ms event: 125 ms in, 125 ms out

    [Fact]
    public void BlackoutOpacity_IsTheEnvelope() =>
        Assert.Equal(ShowEventMath.Envelope(1000, 3000), ShowEventMath.BlackoutOpacity(At(0, 3000), 1000));

    [Fact]
    public void ParseColor_Invalid_IsWhite() => Assert.Equal(((byte)255, (byte)255, (byte)255), ShowEventMath.ParseColor("red"));

    [Fact]
    public void Sin_FoldsLongUptimes()
    {
        long forty = 40L * 24 * 3600 * 1000;   // 40 days
        Assert.Equal(ShowEventMath.Sin(250, 1.0), ShowEventMath.Sin(forty + 250, 1.0), 6);
    }
}

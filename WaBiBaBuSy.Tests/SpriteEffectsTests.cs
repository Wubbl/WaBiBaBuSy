using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SpriteEffectsTests
{
    private static AnimationLayerConfig Sprite(SpriteEffectsConfig? fx = null) =>
        new() { AnimationPath = "fish.gif", Effects = fx ?? new SpriteEffectsConfig { SpinDegPerSecond = 30 } };

    private static readonly MovementConfig Linear = new() { Type = MovementType.Linear, SpeedPixelsPerSecond = 500 };

    // ── HasAny / Supported ──────────────────────────────────────────────

    [Fact]
    public void HasAny_DefaultConfig_IsFalse() => Assert.False(SpriteEffects.HasAny(new SpriteEffectsConfig()));

    [Theory]
    [InlineData(true, false, 0f, BezelEffect.None)]
    [InlineData(false, true, 0f, BezelEffect.None)]
    [InlineData(false, false, 10f, BezelEffect.None)]
    [InlineData(false, false, 0f, BezelEffect.Glow)]
    public void HasAny_AnyEffectSet_IsTrue(bool trail, bool pulse, float spin, BezelEffect entry) =>
        Assert.True(SpriteEffects.HasAny(new SpriteEffectsConfig
        {
            Trail = trail ? new TrailConfig() : null, Pulse = pulse ? new PulseConfig() : null,
            SpinDegPerSecond = spin, Entry = entry,
        }));

    [Fact]
    public void Supported_SingleSpriteWithMovementCalculator() =>
        Assert.True(SpriteEffects.Supported(Sprite(), Linear, BackgroundMode.SolidColor));

    [Fact]
    public void Supported_NotWithPattern()
    {
        var a = Sprite(); a.Pattern = new PatternConfig();
        Assert.False(SpriteEffects.Supported(a, Linear, BackgroundMode.SolidColor));
    }

    [Fact]
    public void Supported_NotWithExtraImages()
    {
        var a = Sprite(); a.AdditionalAnimationPaths.Add("b.png");
        Assert.False(SpriteEffects.Supported(a, Linear, BackgroundMode.SolidColor));
    }

    [Fact]
    public void Supported_BlankExtraImagePathsAreIgnored()
    {
        var a = Sprite(); a.AdditionalAnimationPaths.Add("  ");
        Assert.True(SpriteEffects.Supported(a, Linear, BackgroundMode.SolidColor));
    }

    [Fact]
    public void Supported_NotOnIconZonePath() =>
        Assert.False(SpriteEffects.Supported(Sprite(), Linear, BackgroundMode.IconZone));

    [Fact]
    public void Supported_NotWithoutEffectsOrMovement()
    {
        Assert.False(SpriteEffects.Supported(new AnimationLayerConfig { AnimationPath = "a.gif" }, Linear, BackgroundMode.SolidColor));
        Assert.False(SpriteEffects.Supported(Sprite(new SpriteEffectsConfig()), Linear, BackgroundMode.SolidColor));
        Assert.False(SpriteEffects.Supported(Sprite(), null, BackgroundMode.SolidColor));
    }

    // ── Pulse / spin ───────────────────────────────────────────────────

    [Fact]
    public void PulseScale_FollowsTheSine()
    {
        var p = new PulseConfig { Amplitude = 0.1f, FrequencyHz = 1f };
        Assert.Equal(1f, SpriteEffects.PulseScale(p, 0), 4);
        Assert.Equal(1.1f, SpriteEffects.PulseScale(p, 250), 4);
        Assert.Equal(1f, SpriteEffects.PulseScale(p, 500), 4);
        Assert.Equal(0.9f, SpriteEffects.PulseScale(p, 750), 4);
    }

    [Fact]
    public void PulseScale_NullOrOff_IsOne()
    {
        Assert.Equal(1f, SpriteEffects.PulseScale(null, 123));
        Assert.Equal(1f, SpriteEffects.PulseScale(new PulseConfig { FrequencyHz = 0 }, 123));
    }

    [Fact]
    public void PulseScale_AmplitudeIsClamped() =>
        Assert.InRange(SpriteEffects.PulseScale(new PulseConfig { Amplitude = 5f, FrequencyHz = 1f }, 750), 0.1f - 0.0001f, 1.9f);

    [Fact]
    public void SpinDegrees_WrapsInto0To360()
    {
        Assert.Equal(90f, SpriteEffects.SpinDegrees(90f, 1000), 3);
        Assert.Equal(90f, SpriteEffects.SpinDegrees(90f, 5000), 3);       // 450° → 90°
        Assert.Equal(270f, SpriteEffects.SpinDegrees(-90f, 1000), 3);     // counter-clockwise
        Assert.Equal(0f, SpriteEffects.SpinDegrees(0f, 99999));
    }

    [Fact]
    public void PulseAndSpin_StaySmoothAfter40DaysOfUptime()
    {
        long t = 40L * 24 * 3600 * 1000 + 123;
        var p = new PulseConfig { Amplitude = 0.1f, FrequencyHz = 0.8f };
        // One 16 ms frame later the values move by a small amount — no precision jumps.
        Assert.InRange(MathF.Abs(SpriteEffects.PulseScale(p, t + 16) - SpriteEffects.PulseScale(p, t)), 0f, 0.01f);
        float d = SpriteEffects.SpinDegrees(45f, t + 16) - SpriteEffects.SpinDegrees(45f, t);
        if (d < 0) d += 360f;
        Assert.InRange(d, 0.70f, 0.74f);   // 45°/s × 16 ms = 0.72°
    }

    // ── Trail ──────────────────────────────────────────────────────────

    [Fact]
    public void Ghost_UsesSpacingOpacityAndFalloff()
    {
        var t = new TrailConfig { Count = 4, SpacingMs = 50, StartOpacity = 0.8f, ScaleFalloff = 0.5f };
        var g1 = SpriteEffects.Ghost(t, 1);
        var g4 = SpriteEffects.Ghost(t, 4);
        Assert.Equal(50, g1.OffsetMs);
        Assert.Equal(0.8f, g1.Opacity, 4);
        Assert.Equal(0.5f, g1.Scale, 4);
        Assert.Equal(200, g4.OffsetMs);
        Assert.Equal(0.2f, g4.Opacity, 4);          // 0.8 × (1 − 3/4)
        Assert.Equal(0.0625f, g4.Scale, 4);         // 0.5⁴
    }

    [Fact]
    public void TrailCount_IsClamped()
    {
        Assert.Equal(0, SpriteEffects.TrailCount(null));
        Assert.Equal(0, SpriteEffects.TrailCount(new TrailConfig { Count = -3 }));
        Assert.Equal(SpriteEffects.MaxTrailCount, SpriteEffects.TrailCount(new TrailConfig { Count = 1000 }));
    }

    [Fact]
    public void Ghost_HostileValuesAreClamped()
    {
        var g = SpriteEffects.Ghost(new TrailConfig { Count = 3, SpacingMs = 0, StartOpacity = 7f, ScaleFalloff = 3f }, 2);
        Assert.Equal(2, g.OffsetMs);               // spacing at least 1 ms
        Assert.InRange(g.Opacity, 0f, 1f);
        Assert.InRange(g.Scale, 0f, 1f);
    }
}

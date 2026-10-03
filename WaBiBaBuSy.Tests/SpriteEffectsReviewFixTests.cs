using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Regression tests for the final-review findings on the sprite effects (2026-10-03).</summary>
public class SpriteEffectsReviewFixTests
{
    // ── #1: a bad bezel color must not throw (player draws every frame of a crossing) ──

    [Theory]
    [InlineData("#7FDBFF", 0x7F, 0xDB, 0xFF)]
    [InlineData("7fdbff", 0x7F, 0xDB, 0xFF)]
    [InlineData("  #102030 ", 0x10, 0x20, 0x30)]
    public void ParseBezelColor_ValidHex(string hex, int r, int g, int b) =>
        Assert.Equal(((byte)r, (byte)g, (byte)b), SpriteEffects.ParseBezelColor(hex));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#7FDBFG")]
    [InlineData("red")]
    [InlineData("#F00")]
    [InlineData("#80FF0000")]
    public void ParseBezelColor_InvalidFallsBackToWhite(string? hex)
    {
        Assert.Equal(((byte)255, (byte)255, (byte)255), SpriteEffects.ParseBezelColor(hex));
        Assert.False(SpriteEffects.IsValidBezelColor(hex));
    }

    [Fact]
    public void Validate_InvalidBezelColor_Warns()
    {
        var s = new CrossScreenConfig();
        s.Animation.AnimationPath = "a.gif";
        s.Animation.Effects = new SpriteEffectsConfig { Entry = BezelEffect.Glow, BezelColorHex = "#7FDBFG" };
        Assert.Contains("Bezel color must be #RRGGBB", SceneChecks.Validate(s, null));
    }

    [Fact]
    public void Validate_InvalidColorWithoutBezelEffect_NoWarning()
    {
        var s = new CrossScreenConfig();
        s.Animation.AnimationPath = "a.gif";
        s.Animation.Effects = new SpriteEffectsConfig { SpinDegPerSecond = 10, BezelColorHex = "nope" };
        Assert.DoesNotContain("Bezel color must be #RRGGBB", SceneChecks.Validate(s, null));
    }

    // ── #2: the editor form keeps every field it does not show ──

    [Fact]
    public void EffectsForm_RoundTripsEveryField()
    {
        var original = new SpriteEffectsConfig
        {
            Trail = new TrailConfig { Count = 8, SpacingMs = 70, StartOpacity = 0.55f, ScaleFalloff = 0.8f, UseGradingColor = false },
            Pulse = new PulseConfig { Amplitude = 0.12f, FrequencyHz = 1.5f },
            SpinDegPerSecond = -45f,
            Entry = BezelEffect.Ripple,
            Exit = BezelEffect.Glow,
            BezelColorHex = "#7FDBFF",
            BezelDurationMs = 600,
        };

        var back = SpriteEffectsForm.From(original).ToConfig();

        Assert.NotNull(back);
        Assert.Equal(8, back!.Trail!.Count);
        Assert.Equal(70, back.Trail.SpacingMs);
        Assert.Equal(0.55f, back.Trail.StartOpacity);
        Assert.Equal(0.8f, back.Trail.ScaleFalloff);
        Assert.False(back.Trail.UseGradingColor);
        Assert.Equal(0.12f, back.Pulse!.Amplitude, 4);
        Assert.Equal(1.5f, back.Pulse.FrequencyHz);
        Assert.Equal(-45f, back.SpinDegPerSecond);
        Assert.Equal(BezelEffect.Ripple, back.Entry);
        Assert.Equal(BezelEffect.Glow, back.Exit);
        Assert.Equal("#7FDBFF", back.BezelColorHex);
        Assert.Equal(600, back.BezelDurationMs);
    }

    [Fact]
    public void EffectsForm_NoEffects_StaysNull()
    {
        Assert.Null(SpriteEffectsForm.From(null).ToConfig());
        Assert.Null(SpriteEffectsForm.From(new SpriteEffectsConfig()).ToConfig());
    }

    [Fact]
    public void EffectsForm_TrailOffThenOn_UsesDefaults()
    {
        var form = SpriteEffectsForm.From(null) with { TrailEnabled = true };
        var trail = form.ToConfig()!.Trail!;
        Assert.Equal(6, trail.Count);
        Assert.Equal(0.5f, trail.StartOpacity);
        Assert.Equal(0.9f, trail.ScaleFalloff);
    }

    // ── #5: a crossing whose effect is None must not cut a still-running older effect ──

    [Fact]
    public void EntryOnly_LongDuration_SurvivesTheExit()
    {
        // 1000 px node at offset 1000, 100 px sprite at 1000 px/s: it enters at ~0.9 s and has left again by ~2.0 s.
        var node = new NodeLayout { Id = "n", OffsetX = 1000, Width = 1000, Height = 600, CanvasWidth = 3000, CanvasHeight = 600 };
        var input = new SpritePoseInput(new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 1000 }, 100, 100, 3000, 600, 0f, node);
        var fx = new SpriteEffectsConfig { Entry = BezelEffect.Ripple, BezelDurationMs = 3000 };

        long tEnter = 0;
        while (!SpriteEffects.IsOnNode(input, tEnter, out _, out _)) tEnter += SpriteEffects.BezelScanStepMs;
        long tGone = tEnter;
        while (SpriteEffects.IsOnNode(input, tGone, out _, out _)) tGone += SpriteEffects.BezelScanStepMs;

        var ev = SpriteEffects.FindBezelEvent(input, fx, tGone + 200);

        Assert.NotNull(ev);
        Assert.Equal(BezelEventKind.Entry, ev!.Value.Kind);
        Assert.Equal(tEnter, ev.Value.EventElapsedMs);
    }

    // ── #9: a null image list must not throw in the per-frame support check ──

    [Fact]
    public void Supported_NullAdditionalPaths_DoesNotThrow()
    {
        var a = new AnimationLayerConfig { AnimationPath = "a.gif", Effects = new SpriteEffectsConfig { SpinDegPerSecond = 10 } };
        a.AdditionalAnimationPaths = null!;
        Assert.True(SpriteEffects.Supported(a, new MovementConfig { Type = MovementType.Linear }, BackgroundMode.SolidColor));
    }

    [Fact]
    public void Validate_NullAdditionalPaths_DoesNotThrow()
    {
        var s = new CrossScreenConfig();
        s.Animation.AnimationPath = "a.gif";
        s.Animation.AdditionalAnimationPaths = null!;
        s.Animation.Effects = new SpriteEffectsConfig { SpinDegPerSecond = 10 };
        SceneChecks.Validate(s, null);
    }
}

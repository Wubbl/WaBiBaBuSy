using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Scene editor helpers: warning chips, crossing-time readout, draft change detection, targets from
/// the room selection and unit flags that follow the room.
/// </summary>
public class SceneChecksTests
{
    /// <summary>A scene that produces no warnings on a 5000 px canvas (10 s crossing).</summary>
    private static CrossScreenConfig Scene()
    {
        var c = new CrossScreenConfig();
        c.Background.Mode = BackgroundMode.SolidColor;
        c.Animation.AnimationPath = "fish.gif";
        c.Movement.Type = MovementType.Linear;
        c.Movement.SpeedPixelsPerSecond = 500f;
        return c;
    }

    private static SeatMapLayoutResult Canvas(int width, float refPpcm = 0f)
        => new() { CanvasWidth = width, RefPixelsPerCm = refPpcm };

    // ── crossing time ────────────────────────────────────────────────────────

    [Fact]
    public void CrossingSeconds_PixelCanvas_IsWidthOverSpeed()
        => Assert.Equal(10.0, SceneChecks.CrossingSeconds(Scene(), Canvas(5000)), 3);

    [Fact]
    public void CrossingSeconds_PhysicalCanvas_UsesCmSpeed()
    {
        var s = Scene();
        s.Movement.SpeedUnit = SpeedUnit.CentimetersPerSecond;
        s.Movement.SpeedCmPerSecond = 20f;                 // 20 cm/s × 40 px/cm = 800 px/s
        Assert.Equal(10.0, SceneChecks.CrossingSeconds(s, Canvas(8000, refPpcm: 40f)), 3);
    }

    [Fact]
    public void CrossingSeconds_StaticOrNoLayout_IsZero()
    {
        var s = Scene();
        s.Movement.Type = MovementType.Static;
        Assert.Equal(0, SceneChecks.CrossingSeconds(s, Canvas(5000)));
        Assert.Equal(0, SceneChecks.CrossingSeconds(Scene(), null));
        Assert.Equal(0, SceneChecks.CrossingSeconds(Scene(), Canvas(0)));
    }

    [Fact]
    public void CrossingReadout_FormatsSecondsAndMinutes()
    {
        Assert.Equal("crosses the room in 10 s", SceneChecks.CrossingReadout(Scene(), Canvas(5000)));
        Assert.Equal("crosses the room in 12.5 s", SceneChecks.CrossingReadout(Scene(), Canvas(6250)));
        Assert.Equal("crosses the room in 2 min 0 s", SceneChecks.CrossingReadout(Scene(), Canvas(59_800)));
        Assert.Equal("", SceneChecks.CrossingReadout(Scene(), null));
    }

    // ── warnings ─────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_CleanScene_NoWarnings()
        => Assert.Empty(SceneChecks.Validate(Scene(), Canvas(5000)));

    [Fact]
    public void Validate_EmptyPath_AsksForFile()
    {
        var s = Scene();
        s.Animation.AnimationPath = "";
        Assert.Contains("Pick an animation file from the strip", SceneChecks.Validate(s, Canvas(5000)));
    }

    [Fact]
    public void Validate_ImageBackgroundWithoutImage_Warns()
    {
        var s = Scene();
        s.Background.Mode = BackgroundMode.TiledImage;
        s.Background.ImagePath = null;
        Assert.Contains("Pick a background image", SceneChecks.Validate(s, Canvas(5000)));
    }

    [Fact]
    public void Validate_EndlessNeedsTravelingColors()
    {
        var s = Scene();
        s.Movement.Endless = true;
        Assert.Contains("Endless needs a Traveling color mode", SceneChecks.Validate(s, Canvas(5000)));

        s.Animation.ColorGrading.Mode = ColorGradingMode.TravelingList;
        Assert.DoesNotContain("Endless needs a Traveling color mode", SceneChecks.Validate(s, Canvas(5000)));
    }

    [Fact]
    public void Validate_PatternWithVideo_Warns()
    {
        var s = Scene();
        s.Animation.AnimationPath = @"C:\clips\party.MP4";
        s.Animation.Pattern = new PatternConfig();
        Assert.Contains("Pattern with a video: every cell decodes the video", SceneChecks.Validate(s, Canvas(5000)));
    }

    [Fact]
    public void Validate_TooFastAndTooSlow()
    {
        Assert.Contains("Very fast: crosses the room in 2 s", SceneChecks.Validate(Scene(), Canvas(1000)));
        Assert.Contains("Very slow: crosses the room in 11 min 40 s", SceneChecks.Validate(Scene(), Canvas(350_000)));
    }

    // ── draft change detection ───────────────────────────────────────────────

    [Fact]
    public void SameScene_IdenticalContent_IsTrue()
        => Assert.True(SceneChecks.SameScene(Scene(), Scene()));

    [Fact]
    public void SameScene_AnyFieldChanged_IsFalse()
    {
        var b = Scene();
        b.Movement.DirectionAngleDegrees = 45f;
        Assert.False(SceneChecks.SameScene(Scene(), b));
    }

    [Fact]
    public void SameScene_Nulls()
    {
        Assert.True(SceneChecks.SameScene(null, null));
        Assert.False(SceneChecks.SameScene(Scene(), null));
    }

    // ── targets ──────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NoSelection_IsEmptyMeaningAll()
        => Assert.Empty(SceneTargets.Resolve(new[] { "a", "b", "c" }, Array.Empty<string>()));

    [Fact]
    public void Resolve_ReturnsSelectionInChainOrder()
        => Assert.Equal(new[] { "a", "c", "d" }, SceneTargets.Resolve(new[] { "a", "b", "c", "d" }, new[] { "d", "a", "c" }));

    [Fact]
    public void Resolve_SelectionNotInChain_ReturnsEmpty()
        => Assert.Empty(SceneTargets.Resolve(new[] { "a", "b" }, new[] { "gone" }));

    // ── units follow the room ────────────────────────────────────────────────

    [Fact]
    public void ApplyRoomUnits_Physical_SetsCmFlags_KeepsBothValues()
    {
        var s = Scene();
        s.Animation.TargetHeight = 300;
        s.Animation.TargetHeightCm = 12f;
        s.Movement.SpeedCmPerSecond = 25f;

        PhysicalUnits.ApplyRoomUnits(s, physicalRoom: true);

        Assert.Equal(SizeUnit.Centimeters, s.Animation.SizeUnit);
        Assert.Equal(SpeedUnit.CentimetersPerSecond, s.Movement.SpeedUnit);
        Assert.Equal(300, s.Animation.TargetHeight);
        Assert.Equal(12f, s.Animation.TargetHeightCm);
        Assert.Equal(500f, s.Movement.SpeedPixelsPerSecond);
        Assert.Equal(25f, s.Movement.SpeedCmPerSecond);
    }

    [Fact]
    public void ApplyRoomUnits_PixelRoom_SetsPxFlags()
    {
        var s = Scene();
        s.Animation.SizeUnit = SizeUnit.Centimeters;
        s.Movement.SpeedUnit = SpeedUnit.CentimetersPerSecond;

        PhysicalUnits.ApplyRoomUnits(s, physicalRoom: false);

        Assert.Equal(SizeUnit.Pixels, s.Animation.SizeUnit);
        Assert.Equal(SpeedUnit.PixelsPerSecond, s.Movement.SpeedUnit);
    }
}

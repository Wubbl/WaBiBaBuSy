using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Static wallpapers: an IconZone background must not make a Static sprite follow the icon path,
/// and the plain-wallpaper scene shows a file as-is (no movement, colors, pattern or rotation).
/// </summary>
public class PlainSceneTests
{
    // ── IconZone path-following ──────────────────────────────────────────────

    [Fact]
    public void FollowsIconPath_IconZoneWithStatic_IsFalse()
        => Assert.False(MovementCalculator.FollowsIconPath(
            BackgroundMode.IconZone, new MovementConfig { Type = MovementType.Static }));

    [Theory]
    [InlineData(MovementType.Linear)]
    [InlineData(MovementType.Bounce)]
    [InlineData(MovementType.SineWave)]
    [InlineData(MovementType.RandomWalk)]
    public void FollowsIconPath_IconZoneWithMovement_IsTrue(MovementType type)
        => Assert.True(MovementCalculator.FollowsIconPath(
            BackgroundMode.IconZone, new MovementConfig { Type = type }));

    [Fact]
    public void FollowsIconPath_IconZoneWithoutMovementConfig_KeepsLegacyPathFollowing()
        => Assert.True(MovementCalculator.FollowsIconPath(BackgroundMode.IconZone, null));

    [Fact]
    public void FollowsIconPath_OtherBackground_IsFalse()
        => Assert.False(MovementCalculator.FollowsIconPath(
            BackgroundMode.SolidColor, new MovementConfig { Type = MovementType.Linear }));

    // ── plain scene ──────────────────────────────────────────────────────────

    [Fact]
    public void Create_ShowsTheFileStillAndUngraded()
    {
        var s = PlainScene.Create(@"C:\walls\beach.jpg");

        Assert.Equal(@"C:\walls\beach.jpg", s.Layers[0].Animation.AnimationPath);
        Assert.Empty(s.Layers[0].Animation.AdditionalAnimationPaths);
        Assert.Equal(MovementType.Static, s.Layers[0].Movement.Type);
        Assert.Equal(ColorGradingMode.None, s.Layers[0].Animation.ColorGrading.Mode);
        Assert.Null(s.Layers[0].Animation.Pattern);
        Assert.False(s.Layers[0].Animation.RotateWithPath);
        Assert.Equal(BackgroundMode.SolidColor, s.Background.Mode);
    }

    [Fact]
    public void Create_FillsEachMonitor()
    {
        var s = PlainScene.Create("clip.mp4");

        Assert.Equal(ContentFitMode.Fill, s.Layers[0].Animation.FitMode);
        Assert.Equal(AnimationDistributionMode.Simultaneous, s.Layers[0].Distribution);
        Assert.True(s.Layers[0].Animation.Loop);
        Assert.Equal(1.0, s.Layers[0].Animation.SpeedMultiplier);
    }

    [Fact]
    public void Create_StaysStillOnTheMachines()
    {
        // The players' own math: centered and time-invariant.
        var s = PlainScene.Create("beach.jpg");
        Assert.False(MovementCalculator.FollowsIconPath(s.Background.Mode, s.Layers[0].Movement));
        var at0 = MovementCalculator.Calculate(s.Layers[0].Movement, 0, 1920, 1080, 1920, 1080);
        var at9 = MovementCalculator.Calculate(s.Layers[0].Movement, 9_000, 1920, 1080, 1920, 1080);
        Assert.Equal(at0, at9);
    }
}

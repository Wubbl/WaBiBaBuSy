namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// A scene that shows one file as a plain wallpaper: filling every monitor, no movement,
/// no color grading, no pattern, no rotation. Images stay still, GIFs and videos just loop.
/// </summary>
public static class PlainScene
{
    /// <summary>Plain wallpaper scene for <paramref name="path"/> on a solid background.</summary>
    public static CrossScreenConfig Create(string path, string backgroundHex = "#000000") => new()
    {
        Background = new BackgroundLayerConfig
        {
            Mode = BackgroundMode.SolidColor,
            ColorHex = backgroundHex
        },
        Animation = new AnimationLayerConfig
        {
            AnimationPath = path,
            FitMode = ContentFitMode.Fill,
            Loop = true,
            VerticalAlign = VerticalAlignment.Center,
            RotateWithPath = false,
            SpeedMultiplier = 1.0,
            ColorGrading = new ColorGradingConfig { Mode = ColorGradingMode.None },
            Pattern = null
        },
        DistributionMode = AnimationDistributionMode.Simultaneous,
        Movement = new MovementConfig { Type = MovementType.Static }
    };
}

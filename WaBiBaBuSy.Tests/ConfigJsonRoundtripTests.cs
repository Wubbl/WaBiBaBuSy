using System.Text.Json;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// These configs travel to remote clients as JSON inside SyncParameters.
/// Every field must survive a serialize/deserialize roundtrip, otherwise remote
/// machines compute different deterministic math than the server's local players.
/// </summary>
public class ConfigJsonRoundtripTests
{
    [Fact]
    public void MovementConfig_AllNonDefaultFields_Roundtrip()
    {
        var original = new MovementConfig
        {
            Type = MovementType.SineWave,
            SpeedPixelsPerSecond = 123.5f,
            StartX = 10f, StartY = 20f, EndX = 3000f, EndY = 40f,
            DirectionAngleDegrees = 77f,
            OrbitCenterX = 500f, OrbitCenterY = 600f,
            OrbitRadiusPixels = 250f,
            WaveAmplitudePixels = 321f,
            WaveFrequencyHz = 1.25f,
            RandomSeed = 1234,
            RandomStepIntervalMs = 2500f,
            Loop = false,
            IterationStepCount = 7,
            Reversed = true,
            Endless = true,
            NodePhaseDelayMs = 250
        };

        var restored = JsonSerializer.Deserialize<MovementConfig>(JsonSerializer.Serialize(original))!;

        Assert.Equal(original.Type, restored.Type);
        Assert.Equal(original.SpeedPixelsPerSecond, restored.SpeedPixelsPerSecond);
        Assert.Equal(original.StartX, restored.StartX);
        Assert.Equal(original.StartY, restored.StartY);
        Assert.Equal(original.EndX, restored.EndX);
        Assert.Equal(original.EndY, restored.EndY);
        Assert.Equal(original.DirectionAngleDegrees, restored.DirectionAngleDegrees);
        Assert.Equal(original.OrbitCenterX, restored.OrbitCenterX);
        Assert.Equal(original.OrbitCenterY, restored.OrbitCenterY);
        Assert.Equal(original.OrbitRadiusPixels, restored.OrbitRadiusPixels);
        Assert.Equal(original.WaveAmplitudePixels, restored.WaveAmplitudePixels);
        Assert.Equal(original.WaveFrequencyHz, restored.WaveFrequencyHz);
        Assert.Equal(original.RandomSeed, restored.RandomSeed);
        Assert.Equal(original.RandomStepIntervalMs, restored.RandomStepIntervalMs);
        Assert.Equal(original.Loop, restored.Loop);
        Assert.Equal(original.IterationStepCount, restored.IterationStepCount);
        Assert.Equal(original.Reversed, restored.Reversed);
        Assert.Equal(original.Endless, restored.Endless);
        Assert.Equal(original.NodePhaseDelayMs, restored.NodePhaseDelayMs);
    }

    [Fact]
    public void AnimationLayerConfig_IncludingPrecomputedPath_Roundtrip()
    {
        var original = new AnimationLayerConfig
        {
            AnimationPath = @"C:\anim\fish.gif",
            TargetHeight = 333,
            Loop = false,
            VerticalAlign = VerticalAlignment.Bottom,
            CenterInitialPosition = true,
            SpeedMultiplier = 1.5,
            FitMode = ContentFitMode.Fill,
            RotateWithPath = true,
            FaceTravelDirection = true,
            AdditionalAnimationPaths = { @"C:\anim\a.png", @"C:\anim\b.png" },
            MultiImageSpread = 42f,
            MultiImagePhaseJitterMs = 99f,
            Pattern = new PatternConfig { SpacingX = 111, SpacingY = 222, Seed = 5 },
            ColorGrading = new ColorGradingConfig { Mode = ColorGradingMode.TravelingRainbow, Seed = 9 },
            PrecomputedPath = new List<WaypointF>
            {
                new() { X = 1.5f, Y = 2.5f },
                new() { X = 300f, Y = 400f }
            }
        };

        var restored = JsonSerializer.Deserialize<AnimationLayerConfig>(JsonSerializer.Serialize(original))!;

        Assert.Equal(original.TargetHeight, restored.TargetHeight);
        Assert.Equal(original.Loop, restored.Loop);
        Assert.Equal(original.VerticalAlign, restored.VerticalAlign);
        Assert.Equal(original.CenterInitialPosition, restored.CenterInitialPosition);
        Assert.Equal(original.SpeedMultiplier, restored.SpeedMultiplier);
        Assert.Equal(original.FitMode, restored.FitMode);
        Assert.Equal(original.RotateWithPath, restored.RotateWithPath);
        Assert.Equal(original.FaceTravelDirection, restored.FaceTravelDirection);
        Assert.Equal(original.AdditionalAnimationPaths, restored.AdditionalAnimationPaths);
        Assert.Equal(original.MultiImageSpread, restored.MultiImageSpread);
        Assert.Equal(original.MultiImagePhaseJitterMs, restored.MultiImagePhaseJitterMs);
        Assert.NotNull(restored.Pattern);
        Assert.Equal(original.Pattern.SpacingX, restored.Pattern!.SpacingX);
        Assert.Equal(original.Pattern.Seed, restored.Pattern.Seed);
        Assert.Equal(ColorGradingMode.TravelingRainbow, restored.ColorGrading.Mode);
        Assert.NotNull(restored.PrecomputedPath);
        Assert.Equal(2, restored.PrecomputedPath!.Count);
        Assert.Equal(1.5f, restored.PrecomputedPath[0].X);
        Assert.Equal(400f, restored.PrecomputedPath[1].Y);
    }

    [Fact]
    public void BackgroundLayerConfig_AllModes_Roundtrip()
    {
        var original = new BackgroundLayerConfig
        {
            Mode = BackgroundMode.IconZone,
            ColorHex = "#123456",
            ImagePath = @"C:\bg\wall.png",
            CorridorTopPx = 100,
            CorridorHeightPx = 500,
            TopZoneColorHex = "#111111",
            BottomZoneColorHex = "#222222",
            CorridorColorHex = "#333333",
            IconZonePaletteHexes = { "#AA0000", "#00BB00" },
            IconCorridorColorHex = "#444444",
            IconZonePaletteEnabled = true,
            IconFadePaddingPx = 75,
            IconZoneExpansionPx = 15
        };

        var restored = JsonSerializer.Deserialize<BackgroundLayerConfig>(JsonSerializer.Serialize(original))!;

        Assert.Equal(original.Mode, restored.Mode);
        Assert.Equal(original.ColorHex, restored.ColorHex);
        Assert.Equal(original.ImagePath, restored.ImagePath);
        Assert.Equal(original.CorridorTopPx, restored.CorridorTopPx);
        Assert.Equal(original.CorridorHeightPx, restored.CorridorHeightPx);
        Assert.Equal(original.TopZoneColorHex, restored.TopZoneColorHex);
        Assert.Equal(original.BottomZoneColorHex, restored.BottomZoneColorHex);
        Assert.Equal(original.CorridorColorHex, restored.CorridorColorHex);
        Assert.Equal(original.IconZonePaletteHexes, restored.IconZonePaletteHexes);
        Assert.Equal(original.IconCorridorColorHex, restored.IconCorridorColorHex);
        Assert.Equal(original.IconZonePaletteEnabled, restored.IconZonePaletteEnabled);
        Assert.Equal(original.IconFadePaddingPx, restored.IconFadePaddingPx);
        Assert.Equal(original.IconZoneExpansionPx, restored.IconZoneExpansionPx);
    }
}

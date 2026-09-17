using System;
using System.Text.Json;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Converts values authored in centimeters into canvas pixels using the canvas' reference DPI.
/// Players only ever see pixel values: the server resolves a scene with <see cref="ResolveForCanvas"/>
/// right before broadcasting, so the deterministic math stays unit-agnostic and identical on every node.
/// </summary>
public static class PhysicalUnits
{
    /// <summary>Canvas px/s for a movement config. cm/s × refPpcm on a physical canvas; the px value otherwise.</summary>
    public static float EffectiveSpeedPx(MovementConfig m, float refPixelsPerCm)
    {
        if (m.SpeedUnit == SpeedUnit.CentimetersPerSecond && refPixelsPerCm > 0f)
            return Math.Max(0f, m.SpeedCmPerSecond) * refPixelsPerCm;
        return m.SpeedPixelsPerSecond;
    }

    /// <summary>Canvas px for the target height. cm × refPpcm on a physical canvas; the px value otherwise.</summary>
    public static int EffectiveTargetHeightPx(AnimationLayerConfig a, float refPixelsPerCm)
    {
        if (a.SizeUnit == SizeUnit.Centimeters && refPixelsPerCm > 0f)
            return Math.Max(1, (int)Math.Round(a.TargetHeightCm * refPixelsPerCm));
        return a.TargetHeight;
    }

    /// <summary>Centimeters a canvas-pixel length corresponds to (0 when the canvas has no reference DPI).</summary>
    public static float PxToCm(float px, float refPixelsPerCm) => refPixelsPerCm > 0f ? px / refPixelsPerCm : 0f;

    /// <summary>
    /// Deep copies of the scene's movement and animation configs with every cm value resolved to
    /// canvas pixels and the unit flags reset to pixels. This is what gets serialized to players.
    /// When <paramref name="refPixelsPerCm"/> is 0 (pixel canvas) cm values fall back to the
    /// stored pixel values unchanged.
    /// </summary>
    public static (MovementConfig Movement, AnimationLayerConfig Animation) ResolveForCanvas(
        CrossScreenConfig config, float refPixelsPerCm)
    {
        var movement = Clone(config.Movement);
        movement.SpeedPixelsPerSecond = EffectiveSpeedPx(config.Movement, refPixelsPerCm);
        movement.SpeedUnit = SpeedUnit.PixelsPerSecond;

        var animation = Clone(config.Animation);
        animation.TargetHeight = EffectiveTargetHeightPx(config.Animation, refPixelsPerCm);
        animation.SizeUnit = SizeUnit.Pixels;

        return (movement, animation);
    }

    private static T Clone<T>(T value) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}

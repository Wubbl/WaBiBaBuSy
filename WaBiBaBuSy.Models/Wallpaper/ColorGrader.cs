using System;
using System.Globalization;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// 5x4 color matrix in row-major order, matching D2D1_MATRIX_5X4_F semantics.
/// Output channel = sum of inputs * column entries, plus the bias row.
/// </summary>
public readonly struct ColorMatrix5x4
{
    public readonly float M11, M12, M13, M14;
    public readonly float M21, M22, M23, M24;
    public readonly float M31, M32, M33, M34;
    public readonly float M41, M42, M43, M44;
    public readonly float M51, M52, M53, M54;

    public ColorMatrix5x4(
        float m11, float m12, float m13, float m14,
        float m21, float m22, float m23, float m24,
        float m31, float m32, float m33, float m34,
        float m41, float m42, float m43, float m44,
        float m51, float m52, float m53, float m54)
    {
        M11 = m11; M12 = m12; M13 = m13; M14 = m14;
        M21 = m21; M22 = m22; M23 = m23; M24 = m24;
        M31 = m31; M32 = m32; M33 = m33; M34 = m34;
        M41 = m41; M42 = m42; M43 = m43; M44 = m44;
        M51 = m51; M52 = m52; M53 = m53; M54 = m54;
    }

    /// <summary>Identity (output = input).</summary>
    public static ColorMatrix5x4 Identity => new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
        0, 0, 0, 0);
}

/// <summary>
/// Pure deterministic color-grading math. Returns a 5x4 color matrix that the D2D player
/// applies to the animation bitmap each frame. Identical inputs → identical matrix on every monitor,
/// keeping color cycling synced across the wall without any per-frame IPC.
/// </summary>
public static class ColorGrader
{
    /// <summary>
    /// Compute the color matrix for the given grading config at the given elapsed time.
    /// </summary>
    public static ColorMatrix5x4 Compute(ColorGradingConfig config, long elapsedMs)
    {
        if (config == null || config.Mode == ColorGradingMode.None)
            return ColorMatrix5x4.Identity;
        var (r, g, b) = ComputeTintRgb(config, elapsedMs);
        return TintMatrix(r, g, b);
    }

    /// <summary>
    /// Returns the current tint color as normalized RGB (0..1) for the given config and time.
    /// Returns (1,1,1) white when grading is off.
    /// </summary>
    public static (float R, float G, float B) ComputeCurrentColor(ColorGradingConfig? config, long elapsedMs)
    {
        if (config == null || config.Mode == ColorGradingMode.None) return (1f, 1f, 1f);
        return ComputeTintRgb(config, elapsedMs);
    }

    private static (float R, float G, float B) ComputeTintRgb(ColorGradingConfig config, long elapsedMs)
    {
        double phase = (elapsedMs / 1000.0) * Math.Max(0.0, config.CyclesPerSecond);

        switch (config.Mode)
        {
            case ColorGradingMode.Rainbow:
            {
                double hue = (phase % 1.0) * 360.0;
                if (hue < 0) hue += 360.0;
                return HsvToRgb(hue, 1.0, 1.0);
            }

            case ColorGradingMode.Gradient:
            {
                double t = phase % 2.0;
                if (t < 0) t += 2.0;
                if (t > 1.0) t = 2.0 - t;
                var (ar, ag, ab) = HexToRgb(config.GradientA);
                var (br, bg, bb) = HexToRgb(config.GradientB);
                float ft = (float)t;
                return (ar + (br - ar) * ft, ag + (bg - ag) * ft, ab + (bb - ab) * ft);
            }

            case ColorGradingMode.CycleColorList:
            {
                if (config.ColorList == null || config.ColorList.Count == 0)
                    return (1f, 1f, 1f);
                long step = (long)Math.Floor(phase) % config.ColorList.Count;
                if (step < 0) step += config.ColorList.Count;
                return HexToRgb(config.ColorList[(int)step]);
            }

            case ColorGradingMode.RandomColors:
            {
                if (config.ColorList == null || config.ColorList.Count == 0)
                    return (1f, 1f, 1f);
                long step = (long)Math.Floor(phase);
                int idx = (int)((uint)Hash(config.Seed, (int)step) % (uint)config.ColorList.Count);
                return HexToRgb(config.ColorList[idx]);
            }

            default:
                return (1f, 1f, 1f);
        }
    }

    /// <summary>
    /// Compute the color matrix for a single pattern cell identified by its logical grid coordinates.
    /// Called once per cell when a Traveling color mode is active. Returns a deterministic, time-independent
    /// matrix — the same (i, j) always yields the same color regardless of elapsed time.
    /// </summary>
    public static ColorMatrix5x4 ComputeForCell(ColorGradingConfig config, int logicalI, int logicalJ)
    {
        if (config == null) return ColorMatrix5x4.Identity;

        switch (config.Mode)
        {
            case ColorGradingMode.TravelingRainbow:
            {
                int rawHash = PatternLayout.Hash3(config.Seed, logicalI, logicalJ);
                double hue = (uint)rawHash % 360u;
                var (r, g, b) = HsvToRgb(hue, 1.0, 1.0);
                return TintMatrix(r, g, b);
            }

            case ColorGradingMode.TravelingList:
            case ColorGradingMode.TravelingRandom:
            {
                if (config.ColorList == null || config.ColorList.Count == 0)
                    return ColorMatrix5x4.Identity;
                int rawHash = PatternLayout.Hash3(config.Seed, logicalI, logicalJ);
                int idx = (int)((uint)rawHash % (uint)config.ColorList.Count);
                var (r, g, b) = HexToRgb(config.ColorList[idx]);
                return TintMatrix(r, g, b);
            }

            default:
                return ColorMatrix5x4.Identity;
        }
    }

    /// <summary>
    /// Tint matrix: output rgb = luminance(input) * tint, alpha preserved.
    /// On a monochrome (grayscale) logo with alpha, this paints the logo with the chosen color.
    /// </summary>
    private static ColorMatrix5x4 TintMatrix(float r, float g, float b)
    {
        // Luminance weights (Rec. 601). For each output channel out_x, the column
        // is (0.299*x, 0.587*x, 0.114*x, 0) so that out_x = L * x where L is the
        // input luminance, and alpha is preserved unchanged.
        const float lr = 0.299f, lg = 0.587f, lb = 0.114f;
        return new ColorMatrix5x4(
            lr * r, lr * g, lr * b, 0,
            lg * r, lg * g, lg * b, 0,
            lb * r, lb * g, lb * b, 0,
            0,      0,      0,      1,
            0,      0,      0,      0);
    }

    /// <summary>Knuth multiplicative hash, matching MovementCalculator.cs.</summary>
    private static int Hash(int seed, int k) => seed ^ (int)((uint)k * 2654435761u);

    /// <summary>Parse a hex color "#RRGGBB" or "#AARRGGBB" → normalized RGB floats (0..1).</summary>
    private static (float R, float G, float B) HexToRgb(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return (1f, 1f, 1f);
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 6)
        {
            byte r = byte.Parse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = byte.Parse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = byte.Parse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (r / 255f, g / 255f, b / 255f);
        }
        if (s.Length == 8)
        {
            byte r = byte.Parse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = byte.Parse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = byte.Parse(s.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (r / 255f, g / 255f, b / 255f);
        }
        return (1f, 1f, 1f);
    }

    /// <summary>HSV (hue 0..360, sat/val 0..1) → RGB (0..1).</summary>
    private static (float R, float G, float B) HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double hp = h / 60.0;
        double x = c * (1.0 - Math.Abs((hp % 2.0) - 1.0));
        double r1 = 0, g1 = 0, b1 = 0;
        switch ((int)Math.Floor(hp) % 6)
        {
            case 0: r1 = c; g1 = x; b1 = 0; break;
            case 1: r1 = x; g1 = c; b1 = 0; break;
            case 2: r1 = 0; g1 = c; b1 = x; break;
            case 3: r1 = 0; g1 = x; b1 = c; break;
            case 4: r1 = x; g1 = 0; b1 = c; break;
            case 5: r1 = c; g1 = 0; b1 = x; break;
        }
        double m = v - c;
        return ((float)(r1 + m), (float)(g1 + m), (float)(b1 + m));
    }
}

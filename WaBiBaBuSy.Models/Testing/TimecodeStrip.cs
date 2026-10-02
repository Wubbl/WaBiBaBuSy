namespace WaBiBaBuSy.Models.Testing;

/// <summary>
/// Test-mode timecode: 40 square cells bottom-left — start guard 1010, 32 data bits (MSB first) of
/// the rendered elapsed ms, stop guard 0101. White = 1. Drawn in device px so a screenshot (or later
/// a phone photo) says which elapsed value the frame showed.
/// </summary>
public static class TimecodeStrip
{
    /// <summary>Edge of one square cell, device px.</summary>
    public const int CellPx = 8;
    /// <summary>Cells in the strip: 4 start guard + 32 data + 4 stop guard.</summary>
    public const int Cells = 40;
    /// <summary>Distance of the strip from the left and bottom surface edges, device px.</summary>
    public const int MarginPx = 8;

    private static readonly bool[] StartGuard = { true, false, true, false };
    private static readonly bool[] StopGuard = { false, true, false, true };

    /// <summary>Elapsed ms as the 32-bit code (wraps every ~49.7 days; negative values wrap too).</summary>
    public static uint ToCode(long elapsedMs) => unchecked((uint)elapsedMs);

    /// <summary>The <see cref="Cells"/> cells left to right; true = white.</summary>
    public static bool[] Encode(uint value)
    {
        var cells = new bool[Cells];
        StartGuard.CopyTo(cells, 0);
        for (int bit = 0; bit < 32; bit++)
            cells[4 + bit] = ((value >> (31 - bit)) & 1) != 0;
        StopGuard.CopyTo(cells, 36);
        return cells;
    }

    /// <summary>Top-left of the strip on a surface of <paramref name="surfaceHeight"/> device px.</summary>
    public static (int X, int Y) Origin(int surfaceHeight) => (MarginPx, surfaceHeight - MarginPx - CellPx);

    /// <summary>
    /// The code in a 32-bit BGRA buffer (top-down, <paramref name="stride"/> bytes per row), or null when there
    /// is no valid strip. Throws <see cref="ArgumentException"/> when the buffer is too small for its size.
    /// </summary>
    public static uint? Decode(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        BgraBuffer.Require(bgra, width, height, stride, nameof(bgra));
        var (ox, oy) = Origin(height);
        if (oy < 0 || ox + Cells * CellPx > width) return null;

        var cells = new bool[Cells];
        int cy = oy + CellPx / 2;
        for (int c = 0; c < Cells; c++)
        {
            int i = cy * stride + (ox + c * CellPx + CellPx / 2) * 4;
            cells[c] = (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3 > 127;
        }
        for (int g = 0; g < 4; g++)
            if (cells[g] != StartGuard[g] || cells[36 + g] != StopGuard[g]) return null;

        uint value = 0;
        for (int bit = 0; bit < 32; bit++)
            if (cells[4 + bit]) value |= 1u << (31 - bit);
        return value;
    }
}

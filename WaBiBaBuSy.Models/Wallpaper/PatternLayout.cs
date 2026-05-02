using System;
using System.Collections.Generic;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// One rendered pattern cell: world position (already translated to local screen coords),
/// rotation, and the source-image index (selected deterministically from the cell's logical id).
/// </summary>
public readonly struct PatternCell
{
    public readonly float ScreenX;
    public readonly float ScreenY;
    public readonly float RotationDeg;
    public readonly int SourceIndex;
    public readonly int LogicalI;
    public readonly int LogicalJ;

    public PatternCell(float screenX, float screenY, float rotationDeg, int sourceIndex, int logicalI, int logicalJ)
    {
        ScreenX = screenX;
        ScreenY = screenY;
        RotationDeg = rotationDeg;
        SourceIndex = sourceIndex;
        LogicalI = logicalI;
        LogicalJ = logicalJ;
    }
}

/// <summary>
/// Pure deterministic pattern layout. The pattern is a conceptually infinite world-space grid:
/// cell (i, j) is anchored at world position (i * stepX, j * stepY), and the viewport iterates
/// only the integer (i, j) range that intersects the visible monitor area. As the base translation
/// (tx, ty) shifts, the integer range shifts with it — cells leaving one edge are automatically
/// replaced by cells entering the opposite edge with no special wrap math.
/// </summary>
public static class PatternLayout
{
    /// <summary>
    /// Compute the visible cells for this monitor.
    /// </summary>
    /// <param name="config">Pattern config (Sizing, Spacing, Margin, jitter, seed).</param>
    /// <param name="anchorX">Base anchor X in virtual canvas (from MovementCalculator).</param>
    /// <param name="anchorY">Base anchor Y in virtual canvas (from MovementCalculator).</param>
    /// <param name="cellW">Width of one tile in pixels (max source width when multi-image).</param>
    /// <param name="cellH">Height of one tile in pixels.</param>
    /// <param name="virtualCanvasWidth">Total virtual canvas width.</param>
    /// <param name="virtualCanvasHeight">Total virtual canvas height.</param>
    /// <param name="monitorOffsetX">This monitor's X offset within the virtual canvas.</param>
    /// <param name="monitorWidth">This monitor's width in pixels.</param>
    /// <param name="monitorHeight">This monitor's height in pixels.</param>
    /// <param name="sourceImageCount">Number of source images (for deterministic per-cell pick).</param>
    /// <returns>Cells to draw, with screen-space positions and source-image indices.</returns>
    public static List<PatternCell> Compute(
        PatternConfig config,
        float anchorX, float anchorY,
        int cellW, int cellH,
        int virtualCanvasWidth, int virtualCanvasHeight,
        int monitorOffsetX, int monitorWidth, int monitorHeight,
        int sourceImageCount)
    {
        var result = new List<PatternCell>();
        if (config == null) return result;
        int srcCount = Math.Max(1, sourceImageCount);

        float stepX = MathF.Max(1f, cellW + config.SpacingX);
        float stepY = MathF.Max(1f, cellH + config.SpacingY);

        // Logical (i, j) integer ranges that span the visible monitor area in virtual-canvas coordinates,
        // padded by Margin to keep one cell of slack outside the screen for clean scroll-in.
        // The "anchor" represents the base translation: world origin (i=0, j=0) draws at
        // (0 - anchorX, 0 - anchorY) in virtual-canvas space, then we subtract monitorOffsetX to get
        // local screen coordinates.

        if (config.Sizing == PatternConfig.SizingMode.Fill)
        {
            // World X visible to this monitor: [monitorOffsetX + anchorX, monitorOffsetX + anchorX + monitorWidth]
            float worldXMin = monitorOffsetX + anchorX - config.Margin;
            float worldXMax = monitorOffsetX + anchorX + monitorWidth + config.Margin;
            float worldYMin = anchorY - config.Margin;
            float worldYMax = anchorY + monitorHeight + config.Margin;

            int iMin = (int)MathF.Floor(worldXMin / stepX) - 1;
            int iMax = (int)MathF.Ceiling(worldXMax / stepX) + 1;
            int jMin = (int)MathF.Floor(worldYMin / stepY) - 1;
            int jMax = (int)MathF.Ceiling(worldYMax / stepY) + 1;

            for (int j = jMin; j <= jMax; j++)
            {
                for (int i = iMin; i <= iMax; i++)
                {
                    AddCell(result, config, i, j, stepX, stepY, anchorX, anchorY, monitorOffsetX, srcCount);
                }
            }
        }
        else // Explicit
        {
            int countX = Math.Max(1, config.CountX);
            int countY = Math.Max(1, config.CountY);
            for (int j = 0; j < countY; j++)
            {
                for (int i = 0; i < countX; i++)
                {
                    AddCell(result, config, i, j, stepX, stepY, anchorX, anchorY, monitorOffsetX, srcCount);
                }
            }
        }

        return result;
    }

    private static void AddCell(
        List<PatternCell> result, PatternConfig config,
        int i, int j,
        float stepX, float stepY,
        float anchorX, float anchorY,
        int monitorOffsetX, int srcCount)
    {
        // Per-cell jitter (stable per logical (i, j))
        float jitterX = 0f, jitterY = 0f, jitterRot = 0f;
        if (config.RandomOffsetMaxPx > 0f || config.RandomRotationMaxDeg > 0f)
        {
            int hash = Hash3(config.Seed, i, j);
            // Two independent uniform values in [-1, 1] from the hash bits
            float rx = (((hash & 0xFFFF) / 65535f) * 2f) - 1f;
            float ry = ((((hash >> 16) & 0xFFFF) / 65535f) * 2f) - 1f;
            float rrot = (((Hash3(config.Seed ^ 0x55555555, i, j) & 0xFFFF) / 65535f) * 2f) - 1f;
            jitterX = rx * config.RandomOffsetMaxPx;
            jitterY = ry * config.RandomOffsetMaxPx;
            jitterRot = rrot * config.RandomRotationMaxDeg;
        }

        // World position of this cell, then translate to local screen coords.
        float worldX = i * stepX;
        float worldY = j * stepY;
        float screenX = worldX - anchorX - monitorOffsetX + jitterX;
        float screenY = worldY - anchorY + jitterY;

        int sourceIdx = (int)((uint)Hash3(config.Seed, i, j) % (uint)Math.Max(1, srcCount));

        result.Add(new PatternCell(screenX, screenY, jitterRot, sourceIdx, i, j));
    }

    /// <summary>3-input hash mixing the same Knuth multiplicative constant as MovementCalculator.</summary>
    internal static int Hash3(int seed, int i, int j)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)i * 2654435761u;
            h ^= (uint)j * 2246822519u;
            h ^= h >> 13;
            h *= 2654435761u;
            h ^= h >> 16;
            return (int)h;
        }
    }
}

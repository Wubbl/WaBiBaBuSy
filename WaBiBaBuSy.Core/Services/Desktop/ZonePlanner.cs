using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Core.Services.Desktop;

/// <summary>
/// Converts desktop icon pixel positions into horizontal zone bands and a precomputed
/// waypoint path that weaves through every free band.
/// </summary>
public class ZonePlanner
{
    /// <summary>
    /// Computes zone bands and animation path from icon positions.
    /// </summary>
    /// <param name="icons">Raw pixel positions from <see cref="DesktopIconService.GetIconPositions"/>.</param>
    /// <param name="cellW">Icon grid cell width in pixels.</param>
    /// <param name="cellH">Icon grid cell height in pixels.</param>
    /// <param name="screenW">Monitor width in pixels.</param>
    /// <param name="screenH">Monitor height in pixels.</param>
    /// <param name="palette">Colors to assign to blocked zones (index 0..N-1).</param>
    /// <param name="corridorColorHex">Color for free corridor bands.</param>
    public ZoneLayout Compute(
        List<IconGridCell> icons,
        int cellW, int cellH,
        int screenW, int screenH,
        List<string> palette,
        string corridorColorHex = "#1E1E1E")
    {
        if (cellW <= 0) cellW = 75;
        if (cellH <= 0) cellH = 75;

        int cols = Math.Max(1, (int)Math.Ceiling((double)screenW / cellW));
        int rows = Math.Max(1, (int)Math.Ceiling((double)screenH / cellH));

        // ── 1. Build occupied grid ─────────────────────────────────────────
        bool[,] occupied = new bool[rows, cols];
        foreach (var icon in icons)
        {
            int c = icon.PixelX / cellW;
            int r = icon.PixelY / cellH;
            if (r >= 0 && r < rows && c >= 0 && c < cols)
                occupied[r, c] = true;
        }

        // ── 2. Build occupiedRow[] ─────────────────────────────────────────
        bool[] occupiedRow = new bool[rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                if (occupied[r, c]) { occupiedRow[r] = true; break; }

        // ── 3. Run-length encode into bands ────────────────────────────────
        var rawBands = new List<(int startRow, int endRow, bool isFree)>();
        if (rows > 0)
        {
            bool curFree = !occupiedRow[0];
            int bandStart = 0;
            for (int r = 1; r <= rows; r++)
            {
                bool rowFree = (r < rows) ? !occupiedRow[r] : !curFree; // sentinel flushes last
                if (rowFree != curFree)
                {
                    rawBands.Add((bandStart, r - 1, curFree));
                    bandStart = r;
                    curFree = rowFree;
                }
            }
        }

        // ── 4. Convert to ZoneRect with colors ────────────────────────────
        var bands = new List<ZoneRect>();
        int paletteIdx = 0;
        foreach (var (startRow, endRow, isFree) in rawBands)
        {
            float y = startRow * cellH;
            float h = Math.Min((endRow - startRow + 1) * cellH, screenH - y);
            string color = isFree
                ? corridorColorHex
                : (paletteIdx < palette.Count ? palette[paletteIdx++] : "#333333");
            if (!isFree) paletteIdx = Math.Min(paletteIdx, palette.Count); // don't increment twice
            bands.Add(new ZoneRect { Y = y, Height = h, IsFree = isFree, ColorHex = color });
        }

        // ── 5. Build waypoint path through free bands ─────────────────────
        var freeBandIndices = rawBands
            .Select((b, i) => (b, i))
            .Where(x => x.b.isFree)
            .Select(x => x.i)
            .ToList();

        var path = BuildPath(rawBands, freeBandIndices, occupied, cols, cellW, cellH, screenW);

        return new ZoneLayout { Bands = bands, Path = path };
    }

    // ── Path building ─────────────────────────────────────────────────────────

    private static List<WaypointF> BuildPath(
        List<(int startRow, int endRow, bool isFree)> rawBands,
        List<int> freeBandIndices,
        bool[,] occupied,
        int cols, int cellW, int cellH,
        int screenW)
    {
        var path = new List<WaypointF>();
        if (freeBandIndices.Count == 0) return path;

        bool goRight = true;

        for (int fi = 0; fi < freeBandIndices.Count; fi++)
        {
            var fb = rawBands[freeBandIndices[fi]];
            float bandCenterY = fb.startRow * cellH + (fb.endRow - fb.startRow + 1) * cellH / 2f;

            float leftX  = 0f;
            float rightX = screenW;

            // Traverse this free band left→right or right→left
            if (goRight)
            {
                path.Add(new WaypointF { X = leftX,  Y = bandCenterY });
                path.Add(new WaypointF { X = rightX, Y = bandCenterY });
            }
            else
            {
                path.Add(new WaypointF { X = rightX, Y = bandCenterY });
                path.Add(new WaypointF { X = leftX,  Y = bandCenterY });
            }

            // Transition to next free band via a passage through the blocked band
            if (fi < freeBandIndices.Count - 1)
            {
                var nextFb = rawBands[freeBandIndices[fi + 1]];
                float nextCenterY = nextFb.startRow * cellH + (nextFb.endRow - nextFb.startRow + 1) * cellH / 2f;

                int blockedRowStart = fb.endRow + 1;
                int blockedRowEnd   = nextFb.startRow - 1;
                float exitX = goRight ? rightX : leftX;
                float passageX = FindPassageX(occupied, cols, blockedRowStart, blockedRowEnd, cellW, goRight, screenW);

                // Go from current band exit edge to passage, then down to next band
                path.Add(new WaypointF { X = passageX, Y = bandCenterY });
                path.Add(new WaypointF { X = passageX, Y = nextCenterY });
            }

            goRight = !goRight;
        }

        // Close the loop back to the first waypoint
        if (path.Count > 1)
            path.Add(new WaypointF { X = path[0].X, Y = path[0].Y });

        return path;
    }

    private static float FindPassageX(
        bool[,] occupied,
        int cols, int blockedRowStart, int blockedRowEnd,
        int cellW, bool preferRight,
        int screenW)
    {
        if (blockedRowStart > blockedRowEnd)
            return screenW / 2f; // no blocked rows between bands

        int rows = occupied.GetLength(0);
        blockedRowEnd = Math.Min(blockedRowEnd, rows - 1);

        // Try preferred side first, then opposite
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool right = (attempt == 0) ? preferRight : !preferRight;

            int startC = right ? cols - 1 : 0;
            int endC   = right ? -1 : cols;
            int step   = right ? -1 : 1;

            for (int c = startC; c != endC; c += step)
            {
                bool clear = true;
                for (int r = blockedRowStart; r <= blockedRowEnd; r++)
                {
                    if (r < rows && occupied[r, c]) { clear = false; break; }
                }
                if (clear)
                    return c * cellW + cellW / 2f;
            }
        }

        // Fallback: find column with fewest icons (least-blocked passage)
        int bestCol = cols / 2;
        int bestCount = int.MaxValue;
        for (int c = 0; c < cols; c++)
        {
            int iconCount = 0;
            for (int r = blockedRowStart; r <= blockedRowEnd; r++)
                if (r < rows && occupied[r, c]) iconCount++;
            if (iconCount < bestCount) { bestCount = iconCount; bestCol = c; }
        }
        return bestCol * cellW + cellW / 2f;
    }
}

/// <summary>
/// Result of <see cref="ZonePlanner.Compute"/>: zone bands and animation path.
/// </summary>
public class ZoneLayout
{
    public List<ZoneRect>   Bands { get; set; } = new();
    public List<WaypointF>  Path  { get; set; } = new();
}

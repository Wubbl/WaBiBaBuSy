using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.WallpaperEngine.Desktop;

/// <summary>
/// Converts desktop icon pixel positions into zone bands and an animation path.
///
/// Merging: icons that are adjacent horizontally OR vertically are joined into one
/// connected component via BFS flood-fill (4-connectivity). Each component becomes
/// one colored zone rendered as its axis-aligned bounding box.
/// </summary>
public static class ZonePlanner
{
    /// <summary>
    /// Computes zone bands and animation path from icon pixel positions.
    /// </summary>
    /// <param name="iconPixels">Pixel top-left positions of each icon (screen coords).</param>
    /// <param name="cellW">Icon grid cell width in pixels.</param>
    /// <param name="cellH">Icon grid cell height in pixels.</param>
    /// <param name="screenW">Monitor width in pixels.</param>
    /// <param name="screenH">Monitor height in pixels.</param>
    /// <param name="palette">Colors for occupied components (index 0..N-1).</param>
    /// <param name="corridorColorHex">Color applied to free areas.</param>
    public static ZoneLayout Compute(
        IEnumerable<(int X, int Y)> iconPixels,
        int cellW, int cellH,
        int screenW, int screenH,
        IList<string> palette,
        string corridorColorHex = "#1E1E1E")
    {
        if (cellW <= 0) cellW = 75;
        if (cellH <= 0) cellH = 75;

        int cols = Math.Max(1, (int)Math.Ceiling((double)screenW / cellW));
        int rows = Math.Max(1, (int)Math.Ceiling((double)screenH / cellH));

        // ── 1. Build occupied grid ─────────────────────────────────────────
        bool[,] occ = new bool[rows, cols];
        foreach (var (px, py) in iconPixels)
        {
            int c = Math.Clamp(px / cellW, 0, cols - 1);
            int r = Math.Clamp(py / cellH, 0, rows - 1);
            occ[r, c] = true;
        }

        // ── 2. 2D connected components via BFS (4-connectivity) ────────────
        int[,] compId = new int[rows, cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                compId[r, c] = -1;

        var compBounds = new List<(int MinR, int MaxR, int MinC, int MaxC)>();
        int[] dr = { -1, 1, 0, 0 };
        int[] dc = { 0, 0, -1, 1 };

        for (int r0 = 0; r0 < rows; r0++)
        for (int c0 = 0; c0 < cols; c0++)
        {
            if (!occ[r0, c0] || compId[r0, c0] >= 0) continue;

            int id = compBounds.Count;
            int minR = r0, maxR = r0, minC = c0, maxC = c0;

            var queue = new Queue<(int r, int c)>();
            queue.Enqueue((r0, c0));
            compId[r0, c0] = id;

            while (queue.Count > 0)
            {
                var (r, c) = queue.Dequeue();
                if (r < minR) minR = r; if (r > maxR) maxR = r;
                if (c < minC) minC = c; if (c > maxC) maxC = c;

                for (int d = 0; d < 4; d++)
                {
                    int nr = r + dr[d], nc = c + dc[d];
                    if (nr >= 0 && nr < rows && nc >= 0 && nc < cols
                        && occ[nr, nc] && compId[nr, nc] < 0)
                    {
                        compId[nr, nc] = id;
                        queue.Enqueue((nr, nc));
                    }
                }
            }

            compBounds.Add((minR, maxR, minC, maxC));
        }

        // ── 3. Build zone rects (bounding box per component) ──────────────
        var bands = new List<ZoneRect>();
        for (int id = 0; id < compBounds.Count; id++)
        {
            var (minR, maxR, _, _) = compBounds[id];
            string color = id < palette.Count ? palette[id] : "#333333";
            float y = minR * cellH;
            float h = Math.Min((maxR - minR + 1) * cellH, screenH - y);
            bands.Add(new ZoneRect { Y = y, Height = h, IsFree = false, ColorHex = color });
        }

        // ── 4. Free row-bands (for path & free zone rects) ────────────────
        bool[] occupiedRow = new bool[rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                if (occ[r, c]) { occupiedRow[r] = true; break; }

        var rawBands = RunLengthEncodeRows(occupiedRow, rows);

        // Add free bands as ZoneRects for rendering
        foreach (var (startRow, endRow, isFree) in rawBands.Where(b => b.isFree))
        {
            float y = startRow * cellH;
            float h = Math.Min((endRow - startRow + 1) * cellH, screenH - y);
            bands.Add(new ZoneRect { Y = y, Height = h, IsFree = true, ColorHex = corridorColorHex });
        }

        // ── 5. Build waypoint path through free bands ─────────────────────
        var path = BuildPath(rawBands, occ, cols, cellW, cellH, screenW);

        return new ZoneLayout { Bands = bands, Path = path };
    }

    // ── Path building ─────────────────────────────────────────────────────────

    private static List<WaypointF> BuildPath(
        List<(int startRow, int endRow, bool isFree)> rawBands,
        bool[,] occ, int cols, int cellW, int cellH, int screenW)
    {
        var freeBands = rawBands.Where(b => b.isFree).ToList();
        var path = new List<WaypointF>();
        if (freeBands.Count == 0) return path;

        bool goRight = true;
        for (int fi = 0; fi < freeBands.Count; fi++)
        {
            var fb = freeBands[fi];
            float bandCenterY = fb.startRow * cellH + (fb.endRow - fb.startRow + 1) * cellH / 2f;

            if (goRight)
            {
                path.Add(new WaypointF { X = 0,        Y = bandCenterY });
                path.Add(new WaypointF { X = screenW,  Y = bandCenterY });
            }
            else
            {
                path.Add(new WaypointF { X = screenW, Y = bandCenterY });
                path.Add(new WaypointF { X = 0,       Y = bandCenterY });
            }

            if (fi < freeBands.Count - 1)
            {
                var nextFb = freeBands[fi + 1];
                float nextCenterY = nextFb.startRow * cellH + (nextFb.endRow - nextFb.startRow + 1) * cellH / 2f;

                int blockedRowStart = fb.endRow + 1;
                int blockedRowEnd   = nextFb.startRow - 1;
                float passageX = FindPassageX(occ, cols, blockedRowStart, blockedRowEnd, cellW, goRight, screenW);

                path.Add(new WaypointF { X = passageX, Y = bandCenterY  });
                path.Add(new WaypointF { X = passageX, Y = nextCenterY  });
            }

            goRight = !goRight;
        }

        if (path.Count > 1)
            path.Add(new WaypointF { X = path[0].X, Y = path[0].Y });

        return path;
    }

    private static float FindPassageX(
        bool[,] occ, int cols,
        int rowStart, int rowEnd, int cellW,
        bool preferRight, int screenW)
    {
        if (rowStart > rowEnd) return screenW / 2f;

        int rows = occ.GetLength(0);
        rowEnd = Math.Min(rowEnd, rows - 1);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool right = (attempt == 0) == preferRight;
            int s = right ? cols - 1 : 0;
            int e = right ? -1 : cols;
            int step = right ? -1 : 1;

            for (int c = s; c != e; c += step)
            {
                bool clear = true;
                for (int r = rowStart; r <= rowEnd; r++)
                    if (r < rows && occ[r, c]) { clear = false; break; }
                if (clear) return c * cellW + cellW / 2f;
            }
        }

        // Fallback: column with fewest icons
        int best = cols / 2, bestCount = int.MaxValue;
        for (int c = 0; c < cols; c++)
        {
            int n = 0;
            for (int r = rowStart; r <= rowEnd; r++)
                if (r < rows && occ[r, c]) n++;
            if (n < bestCount) { bestCount = n; best = c; }
        }
        return best * cellW + cellW / 2f;
    }

    private static List<(int startRow, int endRow, bool isFree)> RunLengthEncodeRows(
        bool[] occupiedRow, int rows)
    {
        var result = new List<(int, int, bool)>();
        if (rows == 0) return result;

        bool cur = !occupiedRow[0];
        int start = 0;
        for (int r = 1; r <= rows; r++)
        {
            bool rowFree = r < rows ? !occupiedRow[r] : !cur;
            if (rowFree != cur)
            {
                result.Add((start, r - 1, cur));
                start = r;
                cur = rowFree;
            }
        }
        return result;
    }
}

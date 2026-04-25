using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.WallpaperEngine.Desktop;

/// <summary>
/// Converts desktop icon pixel positions into per-icon 2D zone rects and a smooth
/// animation path that navigates through the free space between icons via A* search
/// with 8-directional (diagonal) movement.
/// </summary>
public static class ZonePlanner
{
    /// <summary>
    /// Computes per-icon zone rects and an A* animation path through free space.
    /// </summary>
    /// <param name="iconPixels">Pixel top-left positions of each icon (screen coords).</param>
    /// <param name="cellW">Icon grid cell width in pixels.</param>
    /// <param name="cellH">Icon grid cell height in pixels.</param>
    /// <param name="screenW">Canvas width in pixels.</param>
    /// <param name="screenH">Canvas height in pixels.</param>
    /// <param name="palette">Colors for icon zones (cycled round-robin).</param>
    /// <param name="corridorColorHex">Color applied to free space (used by caller for background).</param>
    /// <param name="paddingPx">Extra padding around each icon zone in all directions.</param>
    /// <param name="monitorOffsetX">
    /// Virtual-canvas X offset for sequential multi-monitor mode.
    /// All path waypoints are shifted by this value so they are in virtual-canvas space.
    /// </param>
    public static ZoneLayout Compute(
        IEnumerable<(int X, int Y)> iconPixels,
        int cellW, int cellH,
        int screenW, int screenH,
        IList<string> palette,
        string corridorColorHex = "#1E1E1E",
        int paddingPx = 0,
        int monitorOffsetX = 0,
        int visualPaddingPx = 8,
        int iconImageW = 0,
        int iconImageH = 0,
        int pathVariationSeed = 0)
    {
        if (cellW <= 0) cellW = 75;
        if (cellH <= 0) cellH = 75;

        int cols = Math.Max(1, (int)Math.Ceiling((double)screenW / cellW));
        int rows = Math.Max(1, (int)Math.Ceiling((double)screenH / cellH));

        // ── 1. Build occupied grid and per-icon zone rects ─────────────────
        bool[,] occ  = new bool[rows, cols];
        var bands    = new List<ZoneRect>();
        int paletteIdx = 0;
        int padCells   = paddingPx > 0
            ? (int)Math.Ceiling((double)paddingPx / Math.Min(cellW, cellH))
            : 0;

        foreach (var (px, py) in iconPixels)
        {
            int c = Math.Clamp(px / cellW, 0, cols - 1);
            int r = Math.Clamp(py / cellH, 0, rows - 1);

            // Mark this cell and padding-expanded neighbors as occupied
            for (int dr = -padCells; dr <= padCells; dr++)
            for (int dc = -padCells; dc <= padCells; dc++)
            {
                int nr = r + dr, nc = c + dc;
                if (nr >= 0 && nr < rows && nc >= 0 && nc < cols)
                    occ[nr, nc] = true;
            }

            // Tight mode (iconImageW/H provided): anchor zone at the actual LVM pixel position
            // and size it to the real icon image + a 2-line label estimate.
            //
            // KEY constraint: zY uses py directly (no upward shift) so consecutive icons whose
            // py values differ by cellH produce exactly a (cellH - tightH) pixel gap with no
            // overlap.  (Old formula subtracted visualPaddingPx from zY, shrinking the gap and
            // causing adjacent zones to overlap when tightH > cellH - visualPaddingPx.)
            //
            // Fallback (no icon size): original cell-boundary rects.
            bool tight = iconImageW > 0;
            // Width: actual icon image width + small padding on each side.
            float zX = tight
                ? Math.Max(0f, px - visualPaddingPx)
                : Math.Max(0f, c * cellW - visualPaddingPx);
            float zW = Math.Min((tight ? iconImageW : cellW) + 2 * visualPaddingPx, screenW - zX);
            // Height: icon image + generous 2-line label allowance, capped so zones don't overlap.
            //   tightH  = iconH + ~36 px label  →  covers icon image + two label lines.
            //   cap      = cellH - 4             →  leaves a small visible gap between zones.
            int tightH = tight ? Math.Min(iconImageH + 36, cellH - 4) : cellH + 2 * visualPaddingPx;
            float zY = tight
                ? Math.Max(0f, py)                  // anchor at icon image top (no upward shift)
                : Math.Max(0f, r * cellH - visualPaddingPx);
            float zH = Math.Min(tightH, screenH - zY);

            string color = palette.Count > 0 ? palette[paletteIdx % palette.Count] : "#333333";
            paletteIdx++;

            bands.Add(new ZoneRect
            {
                X = zX, Y = zY,
                Width = zW, Height = zH,
                IsFree = false,
                ColorHex = color
            });
        }

        // ── 2. Compute A* path through free space ──────────────────────────
        var path = ComputeAStarPath(occ, cols, rows, cellW, cellH, screenW, screenH, pathVariationSeed);

        // ── 3. Shift path to virtual-canvas coordinates ────────────────────
        if (monitorOffsetX != 0)
            for (int i = 0; i < path.Count; i++)
                path[i] = new WaypointF { X = path[i].X + monitorOffsetX, Y = path[i].Y };

        return new ZoneLayout { Bands = bands, Path = path };
    }

    // ── A* Path Planning ─────────────────────────────────────────────────────

    private static List<WaypointF> ComputeAStarPath(
        bool[,] occ, int cols, int rows, int cellW, int cellH, int screenW, int screenH,
        int pathVariationSeed = 0)
    {
        // Two-phase planning. The path must NEVER traverse an icon zone if any
        // free corridor exists — even a long, S-curving one. Only when every
        // possible route is blocked do we permit (heavily-penalised) crossings.
        //
        //   Phase 1: strict A* — occupied cells are impassable. The entry/exit
        //            columns slide inward from the screen edges until at least
        //            one free row exists; the planner extends the path back to
        //            x = 0 / x = screenW with a horizontal segment so the
        //            animation still spans the canvas.
        //   Phase 2: fall back to cost-based traversal (very high penalty) so
        //            heavily-cluttered desktops still produce *some* path.
        //   Phase 3: last-resort straight line at midRow.

        var strict = TryAStar(occ, cols, rows, cellW, cellH, screenW,
                              pathVariationSeed, strictMode: true);
        if (strict != null) return strict;

        var penalised = TryAStar(occ, cols, rows, cellW, cellH, screenW,
                                 pathVariationSeed, strictMode: false);
        if (penalised != null) return penalised;

        int midRow = rows / 2;
        int sRow = FindNearestFreeRow(occ, 0,        rows, midRow);
        int eRow = FindNearestFreeRow(occ, cols - 1, rows, midRow);
        float sy = sRow * cellH + cellH * 0.5f;
        float ey = eRow * cellH + cellH * 0.5f;
        return new List<WaypointF>
        {
            new() { X = 0,       Y = sy },
            new() { X = screenW, Y = ey },
            new() { X = 0,       Y = sy }
        };
    }

    /// <summary>
    /// Runs one A* attempt. <paramref name="strictMode"/> controls whether occupied
    /// cells are impassable (true) or merely very expensive (false).
    /// Returns null when no path exists under the given mode.
    /// </summary>
    private static List<WaypointF>? TryAStar(
        bool[,] occ, int cols, int rows, int cellW, int cellH, int screenW,
        int pathVariationSeed, bool strictMode)
    {
        // Resolve entry/exit columns. In strict mode we slide inward from the
        // screen edges to the first column with at least one free row, so a
        // wall of icons hugging the edge can't force the path to start inside
        // an icon zone. In non-strict mode, columns 0 and cols-1 are always
        // usable because the planner can pay the penalty.
        int startCol = strictMode ? FindFirstFreeColumn(occ, rows, cols, fromLeft: true)  : 0;
        int endCol   = strictMode ? FindFirstFreeColumn(occ, rows, cols, fromLeft: false) : cols - 1;
        if (startCol < 0 || endCol < 0 || startCol >= endCol) return null;

        int midRow = rows / 2;
        int startRow, endRow;
        if (pathVariationSeed == 0)
        {
            startRow = FindNearestFreeRow(occ, startCol, rows, midRow);
            endRow   = FindNearestFreeRow(occ, endCol,   rows, midRow);
        }
        else
        {
            var freeStartRows = Enumerable.Range(0, rows).Where(r => !occ[r, startCol]).ToList();
            var freeEndRows   = Enumerable.Range(0, rows).Where(r => !occ[r, endCol]).ToList();
            var rng = new Random(pathVariationSeed);
            startRow = freeStartRows.Count > 0 ? freeStartRows[rng.Next(freeStartRows.Count)] : FindNearestFreeRow(occ, startCol, rows, midRow);
            endRow   = freeEndRows.Count   > 0 ? freeEndRows  [rng.Next(freeEndRows.Count)]   : FindNearestFreeRow(occ, endCol,   rows, midRow);
        }

        // Strict mode: the chosen rows must actually be free, otherwise no path.
        if (strictMode && (occ[startRow, startCol] || occ[endRow, endCol])) return null;

        var gScore = new Dictionary<int, float>();
        var parent = new Dictionary<int, int>();
        var open   = new MinHeap();
        var closed = new HashSet<int>();

        int startKey = startRow * cols + startCol;
        int endKey   = endRow   * cols + endCol;

        gScore[startKey] = 0f;
        open.Push(Heuristic(startRow, startCol, endRow, endCol), startKey);
        parent[startKey] = -1;

        int[]   dr      = { -1, -1, -1,  0,  0,  1,  1,  1 };
        int[]   dc      = { -1,  0,  1, -1,  1, -1,  0,  1 };
        float[] moveCost= { 1.4142f, 1f, 1.4142f, 1f, 1f, 1.4142f, 1f, 1.4142f };

        // High enough that any reasonable detour is preferred over a single
        // crossing — only used in non-strict mode.
        const float OccupiedPenalty = 100000f;

        bool found = false;
        while (open.Count > 0)
        {
            var (_, curKey) = open.Pop();
            if (closed.Contains(curKey)) continue;
            closed.Add(curKey);

            if (curKey == endKey) { found = true; break; }

            int curR = curKey / cols, curC = curKey % cols;
            float g = gScore.GetValueOrDefault(curKey, float.MaxValue);

            for (int d = 0; d < 8; d++)
            {
                int nr = curR + dr[d], nc = curC + dc[d];
                if (nr < 0 || nr >= rows || nc < 0 || nc >= cols) continue;

                bool nOcc = occ[nr, nc];
                if (strictMode && nOcc) continue;

                if (dr[d] != 0 && dc[d] != 0)
                    if (IsOcc(occ, curR + dr[d], curC, rows, cols) &&
                        IsOcc(occ, curR, curC + dc[d], rows, cols)) continue;

                int nKey = nr * cols + nc;
                if (closed.Contains(nKey)) continue;

                float extra = (!strictMode && nOcc) ? OccupiedPenalty : 0f;
                float newG  = g + moveCost[d] + extra;

                if (newG < gScore.GetValueOrDefault(nKey, float.MaxValue))
                {
                    gScore[nKey] = newG;
                    parent[nKey] = curKey;
                    open.Push(newG + Heuristic(nr, nc, endRow, endCol), nKey);
                }
            }
        }

        if (!found) return null;

        var rawKeys = new List<int>();
        int k = endKey;
        while (k != -1) { rawKeys.Add(k); k = parent.GetValueOrDefault(k, -1); }
        rawKeys.Reverse();

        var pixels = new List<(float X, float Y)>(rawKeys.Count + 2);
        // Extend back to the literal screen edges when entry/exit slid inward,
        // so the animation still enters and exits at x = 0 / x = screenW.
        if (startCol > 0)
            pixels.Add((0f, startRow * cellH + cellH * 0.5f));
        foreach (var key in rawKeys)
            pixels.Add(((key % cols) * cellW + cellW * 0.5f,
                        (key / cols) * cellH + cellH * 0.5f));
        if (endCol < cols - 1)
            pixels.Add((screenW, endRow * cellH + cellH * 0.5f));

        var smoothed = StringPull(pixels, occ, cols, rows, cellW, cellH);

        if (smoothed.Count > 1)
            smoothed.Add(new WaypointF { X = smoothed[0].X, Y = smoothed[0].Y });

        return smoothed;
    }

    /// <summary>
    /// Returns the first column from the chosen side that contains at least
    /// one free row, or -1 if every column is fully occupied.
    /// </summary>
    private static int FindFirstFreeColumn(bool[,] occ, int rows, int cols, bool fromLeft)
    {
        if (fromLeft)
        {
            for (int c = 0; c < cols; c++)
                for (int r = 0; r < rows; r++)
                    if (!occ[r, c]) return c;
        }
        else
        {
            for (int c = cols - 1; c >= 0; c--)
                for (int r = 0; r < rows; r++)
                    if (!occ[r, c]) return c;
        }
        return -1;
    }

    private static int FindNearestFreeRow(bool[,] occ, int col, int rows, int preferRow)
    {
        for (int delta = 0; delta < rows; delta++)
        {
            int r1 = preferRow + delta;
            int r2 = preferRow - delta;
            if (r1 < rows && !occ[r1, col]) return r1;
            if (r2 >= 0  && !occ[r2, col]) return r2;
        }
        return preferRow; // All blocked; fall back to preferred row
    }

    private static float Heuristic(int r0, int c0, int r1, int c1)
    {
        float dr = r0 - r1, dc = c0 - c1;
        return MathF.Sqrt(dr * dr + dc * dc);
    }

    private static bool IsOcc(bool[,] occ, int r, int c, int rows, int cols)
        => r >= 0 && r < rows && c >= 0 && c < cols && occ[r, c];

    // ── String-pulling (visibility shortcutting) ─────────────────────────────

    private static List<WaypointF> StringPull(
        List<(float X, float Y)> pixels,
        bool[,] occ, int cols, int rows, int cellW, int cellH)
    {
        if (pixels.Count <= 2)
            return pixels.Select(p => new WaypointF { X = p.X, Y = p.Y }).ToList();

        var result = new List<WaypointF> { new() { X = pixels[0].X, Y = pixels[0].Y } };
        int i = 0;

        while (i < pixels.Count - 1)
        {
            // Find the furthest pixel we can reach from pixels[i] in a straight line
            int j = pixels.Count - 1;
            while (j > i + 1 &&
                   !HasLineOfSight(occ, cols, rows, cellW, cellH, pixels[i], pixels[j]))
                j--;

            result.Add(new WaypointF { X = pixels[j].X, Y = pixels[j].Y });
            i = j;
        }

        return result;
    }

    private static bool HasLineOfSight(
        bool[,] occ, int cols, int rows, int cellW, int cellH,
        (float X, float Y) from, (float X, float Y) to)
    {
        int c0 = (int)(from.X / cellW), r0 = (int)(from.Y / cellH);
        int c1 = (int)(to.X   / cellW), r1 = (int)(to.Y   / cellH);

        // Bresenham's line
        int dc = Math.Abs(c1 - c0), dr = Math.Abs(r1 - r0);
        int sc = c0 < c1 ? 1 : -1, sr = r0 < r1 ? 1 : -1;
        int err = dc - dr;
        int c = c0, r = r0;

        while (true)
        {
            if (IsOcc(occ, r, c, rows, cols)) return false;
            if (c == c1 && r == r1) break;
            int e2 = 2 * err;
            if (e2 > -dr) { err -= dr; c += sc; }
            if (e2 <  dc) { err += dc; r += sr; }
        }
        return true;
    }

    // ── Minimal binary min-heap ───────────────────────────────────────────────

    private sealed class MinHeap
    {
        private readonly List<(float Priority, int Key)> _data = new();

        public int Count => _data.Count;

        public void Push(float priority, int key)
        {
            _data.Add((priority, key));
            BubbleUp(_data.Count - 1);
        }

        public (float Priority, int Key) Pop()
        {
            var top = _data[0];
            int last = _data.Count - 1;
            _data[0] = _data[last];
            _data.RemoveAt(last);
            if (_data.Count > 0) BubbleDown(0);
            return top;
        }

        private void BubbleUp(int i)
        {
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (_data[p].Priority <= _data[i].Priority) break;
                (_data[p], _data[i]) = (_data[i], _data[p]);
                i = p;
            }
        }

        private void BubbleDown(int i)
        {
            int n = _data.Count;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, min = i;
                if (l < n && _data[l].Priority < _data[min].Priority) min = l;
                if (r < n && _data[r].Priority < _data[min].Priority) min = r;
                if (min == i) break;
                (_data[min], _data[i]) = (_data[i], _data[min]);
                i = min;
            }
        }
    }
}

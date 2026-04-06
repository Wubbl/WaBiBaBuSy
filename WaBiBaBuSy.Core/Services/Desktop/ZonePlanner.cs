using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Core.Services.Desktop;

/// <summary>
/// Converts desktop icon pixel positions into per-icon 2D zone rects and a smooth
/// animation path that navigates through free space via A* with diagonal movement.
/// </summary>
public class ZonePlanner
{
    /// <summary>
    /// Computes per-icon zone rects and an A* animation path through free space.
    /// </summary>
    /// <param name="icons">Raw pixel positions from <see cref="DesktopIconService.GetIconPositions"/>.</param>
    /// <param name="cellW">Icon grid cell width in pixels.</param>
    /// <param name="cellH">Icon grid cell height in pixels.</param>
    /// <param name="screenW">Canvas width in pixels.</param>
    /// <param name="screenH">Canvas height in pixels.</param>
    /// <param name="palette">Colors for icon zones (cycled round-robin).</param>
    /// <param name="corridorColorHex">Color for free space (background).</param>
    /// <param name="paddingPx">Extra padding around each icon zone in all directions.</param>
    /// <param name="monitorOffsetX">
    /// Virtual-canvas X offset for sequential multi-monitor mode.
    /// All path waypoints are shifted by this value.
    /// </param>
    public ZoneLayout Compute(
        List<IconGridCell> icons,
        int cellW, int cellH,
        int screenW, int screenH,
        List<string> palette,
        string corridorColorHex = "#1E1E1E",
        int paddingPx = 0,
        int monitorOffsetX = 0)
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

        foreach (var icon in icons)
        {
            int c = Math.Clamp(icon.PixelX / cellW, 0, cols - 1);
            int r = Math.Clamp(icon.PixelY / cellH, 0, rows - 1);

            for (int dr = -padCells; dr <= padCells; dr++)
            for (int dc = -padCells; dc <= padCells; dc++)
            {
                int nr = r + dr, nc = c + dc;
                if (nr >= 0 && nr < rows && nc >= 0 && nc < cols)
                    occ[nr, nc] = true;
            }

            float zX = Math.Max(0f, icon.PixelX - paddingPx);
            float zY = Math.Max(0f, icon.PixelY - paddingPx);
            float zW = Math.Min(cellW + 2 * paddingPx, screenW - zX);
            float zH = Math.Min(cellH + 2 * paddingPx, screenH - zY);

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

        // ── 2. Compute A* path ────────────────────────────────────────────
        var path = ComputeAStarPath(occ, cols, rows, cellW, cellH, screenW, screenH);

        // ── 3. Shift to virtual-canvas space ──────────────────────────────
        if (monitorOffsetX != 0)
            for (int i = 0; i < path.Count; i++)
                path[i] = new WaypointF { X = path[i].X + monitorOffsetX, Y = path[i].Y };

        return new ZoneLayout { Bands = bands, Path = path };
    }

    // ── A* Path Planning ─────────────────────────────────────────────────────

    private static List<WaypointF> ComputeAStarPath(
        bool[,] occ, int cols, int rows, int cellW, int cellH, int screenW, int screenH)
    {
        int midRow   = rows / 2;
        int startRow = FindNearestFreeRow(occ, 0,       rows, midRow);
        int endRow   = FindNearestFreeRow(occ, cols - 1, rows, midRow);

        var gScore = new Dictionary<int, float>();
        var parent = new Dictionary<int, int>();
        var open   = new MinHeap();
        var closed = new HashSet<int>();

        int startKey = startRow * cols;
        int endKey   = endRow   * cols + (cols - 1);

        gScore[startKey] = 0f;
        open.Push(Heuristic(startRow, 0, endRow, cols - 1), startKey);
        parent[startKey] = -1;

        int[]   dr      = { -1, -1, -1,  0,  0,  1,  1,  1 };
        int[]   dc      = { -1,  0,  1, -1,  1, -1,  0,  1 };
        float[] moveCost= { 1.4142f, 1f, 1.4142f, 1f, 1f, 1.4142f, 1f, 1.4142f };

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

                if (dr[d] != 0 && dc[d] != 0)
                    if (IsOcc(occ, curR + dr[d], curC, rows, cols) &&
                        IsOcc(occ, curR, curC + dc[d], rows, cols)) continue;

                int nKey = nr * cols + nc;
                if (closed.Contains(nKey)) continue;

                float extra = IsOcc(occ, nr, nc, rows, cols) ? 50f : 0f;
                float newG  = g + moveCost[d] + extra;

                if (newG < gScore.GetValueOrDefault(nKey, float.MaxValue))
                {
                    gScore[nKey] = newG;
                    parent[nKey] = curKey;
                    open.Push(newG + Heuristic(nr, nc, endRow, cols - 1), nKey);
                }
            }
        }

        if (!found)
        {
            float sy = startRow * cellH + cellH * 0.5f;
            float ey = endRow   * cellH + cellH * 0.5f;
            return new List<WaypointF>
            {
                new() { X = 0,       Y = sy },
                new() { X = screenW, Y = ey },
                new() { X = 0,       Y = sy }
            };
        }

        var rawKeys = new List<int>();
        int k = endKey;
        while (k != -1) { rawKeys.Add(k); k = parent.GetValueOrDefault(k, -1); }
        rawKeys.Reverse();

        var pixels = rawKeys.Select(key =>
            (X: (key % cols) * cellW + cellW * 0.5f,
             Y: (key / cols) * cellH + cellH * 0.5f)).ToList();

        var smoothed = StringPull(pixels, occ, cols, rows, cellW, cellH);
        if (smoothed.Count > 1)
            smoothed.Add(new WaypointF { X = smoothed[0].X, Y = smoothed[0].Y });

        return smoothed;
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
        return preferRow;
    }

    private static float Heuristic(int r0, int c0, int r1, int c1)
    {
        float dr = r0 - r1, dc = c0 - c1;
        return MathF.Sqrt(dr * dr + dc * dc);
    }

    private static bool IsOcc(bool[,] occ, int r, int c, int rows, int cols)
        => r >= 0 && r < rows && c >= 0 && c < cols && occ[r, c];

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

/// <summary>
/// Result of <see cref="ZonePlanner.Compute"/>: per-icon zone rects and animation path.
/// </summary>
public class ZoneLayout
{
    public List<ZoneRect>   Bands { get; set; } = new();
    public List<WaypointF>  Path  { get; set; } = new();
}

using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Show;

/// <summary>
/// BiBaBo input on the bomb seat (design §8), on the same geometry the players draw: a click in device pixels of the
/// node → the cable it cuts (its terminal or the first stretch of the cable, 40 canvas units around); the Recycle Bin
/// icon's centre on the bomb.
/// </summary>
public static class BombHit
{
    public const float HitRadius = 40f;

    /// <summary>How far along a cable (from its terminal) clicks cut it, in bomb sizes — not the whole long cable.</summary>
    public const float StubBombSizes = 1.5f;

    /// <summary>The cable (0..2) a click at node-local device pixels cuts; −1 for none. The nearest wins.</summary>
    public static int CableAt(BombGame g, NodeLayout node, float localPxX, float localPxY)
    {
        if (g.Mode == BombMode.Keypad) return -1;   // a keypad bomb is not cut
        var (vx, vy) = ToCanvas(node, localPxX, localPxY);
        int best = -1;
        float bestDist = HitRadius;
        for (int c = 0; c < g.Cables.Count; c++)
        {
            var p = g.Cables[c].Points;
            int n = p.Length / 2;
            if (n == 0) continue;
            float d = Distance(vx, vy, p[0], p[1]);
            float along = 0f, stubLength = StubBombSizes * g.BombSize;
            for (int i = 0; i + 1 < n && along < stubLength; i++)
            {
                d = Math.Min(d, SegmentDistance(vx, vy, p[2 * i], p[2 * i + 1], p[2 * i + 2], p[2 * i + 3]));
                along += Distance(p[2 * i], p[2 * i + 1], p[2 * i + 2], p[2 * i + 3]);
            }
            if (d <= bestDist) { bestDist = d; best = c; }
        }
        return best;
    }

    /// <summary>The keypad key (0–9, 10 = C) under a click at node-local device pixels; −1 for none or a cable bomb.</summary>
    public static int KeyAt(BombGame g, NodeLayout node, float localPxX, float localPxY)
    {
        if (g.Mode != BombMode.Keypad) return -1;
        float scale = node.Scale > 0 ? node.Scale : 1f;
        float dx = localPxX / scale - NodeMapping.ToLocalX(g.BombX, 0f, node), dy = localPxY / scale - (g.BombY - node.OffsetY);
        for (int key = 0; key <= 10; key++)
        {
            var (kx, ky, w, h) = BombMath.KeyRect(g, key);
            if (dx >= kx && dx <= kx + w && dy >= ky && dy <= ky + h) return key;
        }
        return -1;
    }

    /// <summary>True when the centre of an icon (node-local device pixels) lies on the bomb.</summary>
    public static bool CoversBomb(BombGame g, NodeLayout node, (float X, float Y, float W, float H) localPxRect)
    {
        var (vx, vy) = ToCanvas(node, localPxRect.X + localPxRect.W / 2f, localPxRect.Y + localPxRect.H / 2f);
        float half = g.BombSize / 2f;
        return Math.Abs(vx - g.BombX) <= half && Math.Abs(vy - g.BombY) <= half;
    }

    private static (float X, float Y) ToCanvas(NodeLayout node, float localPxX, float localPxY)
    {
        float scale = node.Scale > 0 ? node.Scale : 1f;
        return (NodeMapping.ToVirtualX(localPxX / scale, 0f, node), localPxY / scale + node.OffsetY);
    }

    private static float Distance(float x, float y, float px, float py) => MathF.Sqrt((x - px) * (x - px) + (y - py) * (y - py));

    private static float SegmentDistance(float x, float y, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        float t = len2 <= 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0f, 1f);
        return Distance(x, y, ax + t * dx, ay + t * dy);
    }
}

/// <summary>
/// The Recycle Bin egg: fires once when the icon moves onto the bomb. The first real answer is the baseline — lying there
/// when the game started does not count; null (the shell could not be asked) never counts as "off the bomb".
/// </summary>
public sealed class RecycleWatch
{
    private bool? _wasOver;
    private bool _fired;

    /// <summary>True exactly once: the first time the icon is on the bomb after having been seen off it.</summary>
    public bool Update(bool? overlapping)
    {
        if (overlapping is not { } over) return false;
        bool fire = !_fired && over && _wasOver == false;
        _wasOver = over;
        if (fire) _fired = true;
        return fire;
    }
}

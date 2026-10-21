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
    private const float StubFraction = 0.15f;

    /// <summary>The cable (0..2) a click at node-local device pixels cuts; −1 for none. The nearest wins.</summary>
    public static int CableAt(BombGame g, NodeLayout node, float localPxX, float localPxY)
    {
        var (vx, vy) = ToCanvas(node, localPxX, localPxY);
        int best = -1;
        float bestDist = HitRadius;
        for (int c = 0; c < g.Cables.Count; c++)
        {
            var p = g.Cables[c].Points;
            int n = p.Length / 2;
            if (n == 0) continue;
            int stub = Math.Min(n, Math.Max(2, (int)Math.Ceiling(StubFraction * n)));
            float d = Distance(vx, vy, p[0], p[1]);
            for (int i = 0; i + 1 < stub; i++)
                d = Math.Min(d, SegmentDistance(vx, vy, p[2 * i], p[2 * i + 1], p[2 * i + 2], p[2 * i + 3]));
            if (d <= bestDist) { bestDist = d; best = c; }
        }
        return best;
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

/// <summary>The Recycle Bin egg: fires once when the icon moves onto the bomb — lying there when the game started does not count.</summary>
public sealed class RecycleWatch
{
    private bool _wasOver = true;
    private bool _fired;

    public void Start(bool overlapping)
    {
        _wasOver = overlapping;
        _fired = false;
    }

    /// <summary>True exactly once: the first time the icon is on the bomb after having been off it.</summary>
    public bool Update(bool overlapping)
    {
        bool fire = !_fired && overlapping && !_wasOver;
        _wasOver = overlapping;
        if (fire) _fired = true;
        return fire;
    }
}

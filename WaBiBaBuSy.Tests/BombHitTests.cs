using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>BiBaBo input on the bomb seat: which cable a click cuts, and the Recycle Bin dropped on the bomb.</summary>
public class BombHitTests
{
    private static BombGame Game() => new()
    {
        GameId = "g", BombNodeId = "n1", BombX = 1500, BombY = 540, BombSize = 300,
        Cables =
        {
            new BombCable { Points = new float[] { 1650, 504, 1700, 480, 1750, 460, 1800, 440, 1850, 430, 1900, 420, 1950, 410, 2000, 400 } },
            new BombCable { Points = new float[] { 1650, 540, 1700, 560, 1750, 580, 1800, 600, 1850, 620, 1900, 640, 1950, 660, 2000, 680 } },
            new BombCable { Points = new float[] { 1650, 576, 1700, 620, 1750, 660, 1800, 700, 1850, 740, 1900, 780, 1950, 820, 2000, 860 } },
        },
    };

    private static NodeLayout Node(float scale = 1f, bool mirrored = false) => new()
    {
        Id = "n1", OffsetX = 1000, Width = 1000, Height = 1080, CanvasWidth = 3000, CanvasHeight = 1080, Scale = scale, Mirrored = mirrored,
    };

    /// <summary>Device pixels on the node of a canvas point.</summary>
    private static (float X, float Y) Px(NodeLayout node, float vx, float vy) =>
        (NodeMapping.ToLocalX(vx, 0, node) * node.Scale, (vy - node.OffsetY) * node.Scale);

    [Fact]
    public void CableAt_TerminalHit()
    {
        var (x, y) = Px(Node(), 1650, 540);
        Assert.Equal(1, BombHit.CableAt(Game(), Node(), x, y));
        (x, y) = Px(Node(), 1655, 580);
        Assert.Equal(2, BombHit.CableAt(Game(), Node(), x, y));
    }

    [Fact]
    public void CableAt_AlongTheStub()
    {
        var (x, y) = Px(Node(), 1700, 478);
        Assert.Equal(0, BombHit.CableAt(Game(), Node(), x, y));
    }

    [Fact]
    public void CableAt_Miss()
    {
        var (x, y) = Px(Node(), 1200, 200);
        Assert.Equal(-1, BombHit.CableAt(Game(), Node(), x, y));   // far along a long cable: CableAt_FarAlongALongCable_IsNoCut
    }

    [Fact]
    public void CableAt_FarAlongALongCable_IsNoCut()
    {
        // a cable running across 20 seats: its first 15 % is thousands of units — only ~1.5 bomb sizes count
        var g = Game();
        g.Cables[0].Points = Enumerable.Range(0, 1000).SelectMany(i => new float[] { 1650 + 30 * i, 504 }).ToArray();
        var node = Node();
        node.Width = 40_000;
        node.CanvasWidth = 40_000;
        var (x, y) = Px(node, 1650 + 30 * 100, 504);   // 3000 units along, inside the old 15 % stub
        Assert.Equal(-1, BombHit.CableAt(g, node, x, y));
        (x, y) = Px(node, 1650 + 300, 504);             // 300 units = 1 bomb size: still the cut zone
        Assert.Equal(0, BombHit.CableAt(g, node, x, y));
    }

    [Fact]
    public void RecycleWatch_UnknownPollsDoNotBaseline()
    {
        var w = new RecycleWatch();
        Assert.False(w.Update(null));   // the shell could not be asked
        Assert.False(w.Update(true));   // first real answer: already on the bomb → that is the baseline, no win
        Assert.False(w.Update(null));
        Assert.False(w.Update(false));
        Assert.True(w.Update(true));
    }

    [Fact]
    public void CableAt_ScaledAndMirroredNode()
    {
        var node = Node(scale: 1.5f, mirrored: true);
        var (x, y) = Px(node, 1650, 540);
        Assert.Equal(1, BombHit.CableAt(Game(), node, x, y));
    }

    [Fact]
    public void CoversBomb_IconOnBomb()
    {
        var (x, y) = Px(Node(), 1500, 540);
        Assert.True(BombHit.CoversBomb(Game(), Node(), (x - 40, y - 40, 80, 80)));
        var (fx, fy) = Px(Node(), 1100, 100);
        Assert.False(BombHit.CoversBomb(Game(), Node(), (fx, fy, 80, 80)));
    }

    [Fact]
    public void RecycleWatch_IgnoresStartingOverlap()
    {
        var w = new RecycleWatch();
        Assert.False(w.Update(true));   // first answer = baseline
        Assert.False(w.Update(true));
        Assert.False(w.Update(false));
        Assert.True(w.Update(true));   // moved away and back: that counts
    }

    [Fact]
    public void RecycleWatch_FiresOnceOnMoveOnto()
    {
        var w = new RecycleWatch();
        Assert.False(w.Update(false));   // first answer = baseline
        Assert.False(w.Update(false));
        Assert.True(w.Update(true));
        Assert.False(w.Update(true));
        Assert.False(w.Update(false));
        Assert.False(w.Update(true));
    }

    /// <summary>Device pixels on the node of a keypad key's centre (offsets are screen-relative to the bomb's centre).</summary>
    private static (float X, float Y) KeyPx(BombGame g, NodeLayout node, int key)
    {
        var (dx, dy, w, h) = BombMath.KeyRect(g, key);
        float cx = NodeMapping.ToLocalX(g.BombX, 0, node) + dx + w / 2, cy = g.BombY - node.OffsetY + dy + h / 2;
        return (cx * node.Scale, cy * node.Scale);
    }

    [Fact]
    public void KeyAt_EveryKeyCentre_OnPlainAndScaledMirroredNodes()
    {
        var g = Game();
        g.Mode = BombMode.Keypad;
        foreach (var node in new[] { Node(), Node(scale: 1.5f, mirrored: true) })
            for (int key = 0; key <= 10; key++)
            {
                var (x, y) = KeyPx(g, node, key);
                Assert.Equal(key, BombHit.KeyAt(g, node, x, y));
            }
    }

    [Fact]
    public void Keypad_ReadsLeftToRight_EvenMirrored()
    {
        var g = Game();
        g.Mode = BombMode.Keypad;
        var node = Node(mirrored: true);
        Assert.True(KeyPx(g, node, 1).X < KeyPx(g, node, 3).X);
        Assert.True(KeyPx(g, node, 1).Y < KeyPx(g, node, 7).Y);
    }

    [Fact]
    public void KeyAt_MissAndOtherModes()
    {
        var g = Game();
        g.Mode = BombMode.Keypad;
        var (fx, fy) = Px(Node(), 1100, 100);
        Assert.Equal(-1, BombHit.KeyAt(g, Node(), fx, fy));
        var (tx, ty) = Px(Node(), 1650, 540);
        Assert.Equal(-1, BombHit.CableAt(g, Node(), tx, ty));   // no cable cuts on a keypad bomb

        g.Mode = BombMode.Classic;
        var (kx, ky) = KeyPx(g, Node(), 5);
        Assert.Equal(-1, BombHit.KeyAt(g, Node(), kx, ky));    // no keypad on a cable bomb
    }
}

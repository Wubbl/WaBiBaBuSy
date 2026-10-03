using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class CelebrationMathTests
{
    private const int CanvasW = 4000, CanvasH = 1000;

    private static ShowEvent Burst(int seed = 1234) => new() { EventId = "c", Type = ShowEventType.Celebration, DurationMs = 5000, Seed = seed };

    private static NodeLayout Node(int offsetX, int width = 1000, bool wraps = false, bool mirrored = false) => new()
    {
        OffsetX = offsetX, Width = width, Height = CanvasH, CanvasWidth = CanvasW, CanvasHeight = CanvasH, Wraps = wraps, Mirrored = mirrored,
    };

    private static List<CelebrationParticle> Visible(ShowEvent e, NodeLayout node, long local)
    {
        var buffer = new CelebrationParticle[CelebrationMath.Capacity(node, CelebrationMath.Count(node.CanvasWidth))];
        int n = CelebrationMath.VisibleOnNode(e, node, local, buffer);
        return buffer.Take(n).ToList();
    }

    [Theory]
    [InlineData(1920, 480)]
    [InlineData(100, 50)]
    [InlineData(1_000_000, 6000)]
    public void Count_ScalesWithCanvasWidth(int width, int expected) => Assert.Equal(expected, CelebrationMath.Count(width));

    [Theory]
    [InlineData(1500L)]
    [InlineData(4000L)]
    public void TwoNodes_SeeIdenticalVirtualPositions(long local)
    {
        var e = Burst();
        NodeLayout a = Node(0), b = Node(1000, mirrored: true);
        var onA = Visible(e, a, local).ToDictionary(p => p.Index);
        var onB = Visible(e, b, local).ToDictionary(p => p.Index);
        var shared = onA.Keys.Intersect(onB.Keys).ToList();
        Assert.NotEmpty(shared);                       // particles straddling the bezel are drawn by both machines
        foreach (int i in shared)
        {
            Assert.Equal(NodeMapping.ToVirtualX(onA[i].X, onA[i].Width, a), NodeMapping.ToVirtualX(onB[i].X, onB[i].Width, b), 2);
            Assert.Equal(onA[i].Y, onB[i].Y, 3);
            Assert.Equal(onA[i].RotationDeg, onB[i].RotationDeg, 3);
            Assert.Equal(onA[i].ColorIndex, onB[i].ColorIndex);
        }
    }

    [Theory]
    [InlineData(500L, 1500, false, false)]
    [InlineData(2500L, 1500, false, false)]
    [InlineData(2500L, 0, true, false)]      // left ring seam: needs k = -1 copies
    [InlineData(2500L, 3000, true, false)]   // right ring seam: needs j >= n, k = +1 copies
    [InlineData(500L, 0, true, true)]        // one-node ring (slice + margin wider than the ring)
    [InlineData(2500L, 0, true, true)]
    [InlineData(4000L, 0, true, true)]
    public void VisibleOnNode_MissesNoParticleThatTouchesTheNode(long local, int offsetX, bool wraps, bool oneNodeRing)
    {
        var e = Burst(seed: 77);
        var node = oneNodeRing ? Node(0, width: CanvasW, wraps: true) : Node(offsetX, wraps: wraps);
        int n = CelebrationMath.Count(CanvasW);
        var seen = Visible(e, node, local);
        int expectedCopies = 0;
        for (int i = 0; i < n; i++)
        {
            if (!CelebrationMath.Particle(e, i, n, CanvasW, CanvasH, local, out var p)) continue;
            foreach (int k in wraps ? new[] { -1, 0, 1 } : new[] { 0 })
            {
                float x = p.X + k * CanvasW;
                bool touches = x + p.Width > node.OffsetX && x < node.OffsetX + node.Width && p.Y + p.Height > 0 && p.Y < CanvasH;
                if (!touches) continue;
                expectedCopies++;
                // the exact copy (index i shifted by k ring widths) must be among the outputs
                Assert.Contains(seen, s => s.Index == i && Math.Abs(NodeMapping.ToVirtualX(s.X, s.Width, node) - x) < 0.01f);
            }
        }
        Assert.True(expectedCopies > 0, "oracle found no touching particle - test would be vacuous");
    }

    [Fact]
    public void DifferentSeed_DifferentBurst()
    {
        int n = CelebrationMath.Count(CanvasW);
        CelebrationMath.Particle(Burst(1), 10, n, CanvasW, CanvasH, 1500, out var a);
        CelebrationMath.Particle(Burst(2), 10, n, CanvasW, CanvasH, 1500, out var b);
        Assert.NotEqual(a.X, b.X);
    }

    [Fact]
    public void SameSeed_IsRepeatable()
    {
        int n = CelebrationMath.Count(CanvasW);
        CelebrationMath.Particle(Burst(), 10, n, CanvasW, CanvasH, 1500, out var a);
        CelebrationMath.Particle(Burst(), 10, n, CanvasW, CanvasH, 1500, out var b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void BeforeLaunch_ParticleIsNotThere() =>
        Assert.False(CelebrationMath.Particle(Burst(), 0, 100, CanvasW, CanvasH, -1, out _));

    [Fact]
    public void Particles_RiseIntoTheScreenThenFall()
    {
        var e = Burst();
        int n = CelebrationMath.Count(CanvasW);
        int risen = 0;
        for (int i = 0; i < n; i++)
            if (CelebrationMath.Particle(e, i, n, CanvasW, CanvasH, 1800, out var p) && p.Y < CanvasH * 0.6f) risen++;
        Assert.True(risen > n / 2, $"only {risen}/{n} particles are in the upper part of the screen at 1.8 s");
    }

    [Fact]
    public void Capacity_IsBoundedForHugeRooms()
    {
        var node = new NodeLayout { OffsetX = 0, Width = 3840, Height = 2160, CanvasWidth = 20 * 3840, CanvasHeight = 2160 };
        int count = CelebrationMath.Count(node.CanvasWidth);
        Assert.Equal(CelebrationMath.MaxCount, count);
        Assert.InRange(CelebrationMath.Capacity(node, count), 1, 800);
    }

    [Fact]
    public void ColorFor_ZeroIsTheEventColor()
    {
        var e = Burst(); e.ColorHex = "#123456";
        Assert.Equal("#123456", CelebrationMath.ColorFor(e, 0));
        Assert.Equal(CelebrationMath.PartyColors[0], CelebrationMath.ColorFor(e, 1));
    }
}

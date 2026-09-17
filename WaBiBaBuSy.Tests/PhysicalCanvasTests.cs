using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Physical canvas (Tier 1.2): reference pixels + per-node scale so a sprite is the same number of
/// centimeters and moves at the same cm/s on every monitor. Pixel mode must stay bit-identical.
/// </summary>
public class PhysicalCanvasTests
{
    // 24" 1080p ≈ 36.6 px/cm; 27" 1440p ≈ 43.4 px/cm
    private static LayoutNodeInput Node(string id, int w, int h, float ppcm, int gapCm = 0)
        => new() { Id = id, WidthPx = w, HeightPx = h, PixelsPerCm = ppcm, GapBeforeCm = gapCm };

    private static SeatMap Physical(VerticalAnchor anchor = VerticalAnchor.Center)
    {
        var m = SeatMap.SingleRow();
        m.CanvasMode = CanvasMode.Physical;
        m.VerticalAnchor = anchor;
        return m;
    }

    [Fact]
    public void Physical_ScalesEachNodeToReferenceDpi()
    {
        var nodes = new[] { Node("a", 1920, 1080, 36.6f), Node("b", 2560, 1440, 43.4f) };
        var r = SeatMapLayoutBuilder.Build(Physical(), nodes, referencePixelsPerCm: 36.6f);

        Assert.True(r.IsPhysical);
        Assert.Equal(36.6f, r.RefPixelsPerCm);
        var a = r.Get("a")!; var b = r.Get("b")!;
        Assert.Equal(1f, a.Scale, 3);
        Assert.Equal(43.4f / 36.6f, b.Scale, 3);
        Assert.Equal(1920, a.Width);
        // b's slice in reference px = its physical width in cm × ref ppcm ≈ 59.0 cm × 36.6
        Assert.Equal((int)Math.Round(2560 / (43.4f / 36.6f)), b.Width);
        Assert.Equal((int)Math.Round(1440 / (43.4f / 36.6f)), b.Height);
        Assert.Equal(a.Width + b.Width, r.CanvasWidth);
        // The 27" panel is physically taller (30.0 cm vs 29.5 cm) → it defines the canvas height
        Assert.Equal(Math.Max(a.Height, b.Height), r.CanvasHeight);
        Assert.All(r.Nodes, n => Assert.Equal(36.6f, n.RefPixelsPerCm));
    }

    [Fact]
    public void Physical_SameSpriteIsSameCentimeters()
    {
        var nodes = new[] { Node("a", 1920, 1080, 36.6f), Node("b", 2560, 1440, 43.4f) };
        var r = SeatMapLayoutBuilder.Build(Physical(), nodes, 36.6f);
        float spriteRpx = 15f * 36.6f;                                   // 15 cm in canvas px
        float cmOnA = spriteRpx * r.Get("a")!.Scale / 36.6f;             // device px / device ppcm
        float cmOnB = spriteRpx * r.Get("b")!.Scale / 43.4f;
        Assert.Equal(15f, cmOnA, 2);
        Assert.Equal(15f, cmOnB, 2);
    }

    [Fact]
    public void Physical_GapsUseReferenceDpi()
    {
        var nodes = new[] { Node("a", 1920, 1080, 40f), Node("b", 1920, 1080, 80f, gapCm: 10) };
        var r = SeatMapLayoutBuilder.Build(Physical(), nodes, 40f);
        // b is scaled ×2 → 960 canvas px wide; the 10 cm gap is 10 × 40 = 400 canvas px (not 800)
        Assert.Equal(1920 + 400, r.Get("b")!.OffsetX);
        Assert.Equal(960, r.Get("b")!.Width);
    }

    [Fact]
    public void Physical_UnknownDpi_FallsBackToScaleOne()
    {
        var nodes = new[] { Node("a", 1920, 1080, 40f), Node("b", 1920, 1080, 0f) };
        var r = SeatMapLayoutBuilder.Build(Physical(), nodes, 40f);
        Assert.Equal(1f, r.Get("b")!.Scale);
        Assert.Equal(1920, r.Get("b")!.Width);
    }

    [Fact]
    public void PixelMode_IgnoresReferenceDpi_AndIsUnchanged()
    {
        var nodes = new[] { Node("a", 1920, 1080, 36.6f, 5), Node("b", 2560, 1440, 43.4f, 5) };
        var legacy = SeatMapLayoutBuilder.Build(SeatMap.SingleRow(), nodes);
        var withRef = SeatMapLayoutBuilder.Build(SeatMap.SingleRow(), nodes, 36.6f);
        Assert.False(withRef.IsPhysical);
        Assert.Equal(legacy.CanvasWidth, withRef.CanvasWidth);
        Assert.All(withRef.Nodes, n => { Assert.Equal(1f, n.Scale); Assert.Equal(0f, n.RefPixelsPerCm); });
        Assert.Equal(2560, withRef.Get("b")!.Width);
        // legacy gap: 5 cm × b's own 43.4 ppcm
        Assert.Equal(1920 + (int)(5 * 43.4f), withRef.Get("b")!.OffsetX);
    }

    [Theory]
    [InlineData(VerticalAnchor.Center, 180)]
    [InlineData(VerticalAnchor.Top, 0)]
    [InlineData(VerticalAnchor.Bottom, 360)]
    public void VerticalAnchor_PlacesShorterNode(VerticalAnchor anchor, int expectedOffsetY)
    {
        var map = SeatMap.SingleRow();
        map.VerticalAnchor = anchor;
        var nodes = new[] { Node("a", 1920, 1080, 40f), Node("b", 2560, 1440, 40f) };
        var r = SeatMapLayoutBuilder.Build(map, nodes);
        Assert.Equal(expectedOffsetY, r.Get("a")!.OffsetY);
        Assert.Equal(0, r.Get("b")!.OffsetY);
    }

    [Fact]
    public void VerticalAnchor_AppliesInParallelRows()
    {
        var map = SeatMap.TwoRows(2);
        map.Traversal = TraversalMode.Parallel;
        map.VerticalAnchor = VerticalAnchor.Bottom;
        map.RowGapCm = 0;
        var nodes = new[] { Node("a", 1920, 1080, 40f), Node("b", 2560, 1440, 40f), Node("c", 1920, 1080, 40f), Node("d", 1920, 900, 40f) };
        var r = SeatMapLayoutBuilder.Build(map, nodes);
        Assert.Equal(360, r.Get("a")!.OffsetY);              // row 0 band is 1440 tall
        Assert.Equal(1440 + 180, r.Get("d")!.OffsetY);       // row 1 band is 1080 tall, d is 900
    }
}

public class PhysicalUnitsTests
{
    [Fact]
    public void EffectiveSpeed_CmOnPhysicalCanvas_ConvertsWithReferenceDpi()
    {
        var m = new MovementConfig { SpeedUnit = SpeedUnit.CentimetersPerSecond, SpeedCmPerSecond = 25f, SpeedPixelsPerSecond = 999f };
        Assert.Equal(25f * 36.6f, PhysicalUnits.EffectiveSpeedPx(m, 36.6f), 3);
    }

    [Fact]
    public void EffectiveSpeed_PixelCanvas_UsesPixelValue()
    {
        var m = new MovementConfig { SpeedUnit = SpeedUnit.CentimetersPerSecond, SpeedCmPerSecond = 25f, SpeedPixelsPerSecond = 999f };
        Assert.Equal(999f, PhysicalUnits.EffectiveSpeedPx(m, 0f));
        Assert.Equal(999f, PhysicalUnits.EffectiveSpeedPx(new MovementConfig { SpeedPixelsPerSecond = 999f }, 36.6f));
    }

    [Fact]
    public void EffectiveTargetHeight_Rounds()
    {
        var a = new AnimationLayerConfig { SizeUnit = SizeUnit.Centimeters, TargetHeightCm = 15f, TargetHeight = 720 };
        Assert.Equal((int)Math.Round(15f * 36.6f), PhysicalUnits.EffectiveTargetHeightPx(a, 36.6f));
        Assert.Equal(720, PhysicalUnits.EffectiveTargetHeightPx(a, 0f));
    }

    [Fact]
    public void ResolveForCanvas_ReturnsPixelOnlyClones_AndLeavesSourceUntouched()
    {
        var cfg = new CrossScreenConfig
        {
            Movement = new MovementConfig { SpeedUnit = SpeedUnit.CentimetersPerSecond, SpeedCmPerSecond = 10f, SpeedPixelsPerSecond = 1f, Reversed = true, NodePhaseDelayMs = 50 },
            Animation = new AnimationLayerConfig { SizeUnit = SizeUnit.Centimeters, TargetHeightCm = 10f, TargetHeight = 1, FaceTravelDirection = true, FitMode = ContentFitMode.TargetHeight }
        };
        var (mv, an) = PhysicalUnits.ResolveForCanvas(cfg, 40f);

        Assert.Equal(400f, mv.SpeedPixelsPerSecond);
        Assert.Equal(SpeedUnit.PixelsPerSecond, mv.SpeedUnit);
        Assert.True(mv.Reversed); Assert.Equal(50, mv.NodePhaseDelayMs);
        Assert.Equal(400, an.TargetHeight);
        Assert.Equal(SizeUnit.Pixels, an.SizeUnit);
        Assert.True(an.FaceTravelDirection); Assert.Equal(ContentFitMode.TargetHeight, an.FitMode);

        // source untouched
        Assert.Equal(1f, cfg.Movement.SpeedPixelsPerSecond);
        Assert.Equal(SpeedUnit.CentimetersPerSecond, cfg.Movement.SpeedUnit);
        Assert.Equal(1, cfg.Animation.TargetHeight);
        Assert.NotSame(cfg.Movement, mv);
    }

    [Fact]
    public void LapMs_WithSpeedOverride()
    {
        var m = new MovementConfig { Type = MovementType.Linear, Loop = true, SpeedPixelsPerSecond = 1f };
        Assert.Equal(4200, PlaylistScheduler.ComputeLapMs(m, 4000, 200, speedPxOverride: 1000f));
        Assert.Equal(PlaylistScheduler.ComputeLapMs(m, 4000, 200), PlaylistScheduler.ComputeLapMs(m, 4000, 200, 0f));
    }

    [Fact]
    public void PxToCm()
    {
        Assert.Equal(15f, PhysicalUnits.PxToCm(15f * 36.6f, 36.6f), 3);
        Assert.Equal(0f, PhysicalUnits.PxToCm(500f, 0f));
    }
}

using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class PatternLayoutTests
{
    [Fact]
    public void EndlessStep_SameLogicalCell_KeepsItsJitterAndRotation()
    {
        // Review 2026-10-13 E6: at an Endless step the phase wraps by one cell and the logical offset grows by one.
        // A cell must look the same before and after; jitter hashed on the reset index i made the grid pop.
        var config = new PatternConfig
        {
            Sizing = PatternConfig.SizingMode.Explicit, CountX = 6, CountY = 2,
            RandomOffsetMaxPx = 30f, RandomRotationMaxDeg = 20f, Seed = 7,
        };
        const int cell = 100;
        float stepX = cell + config.SpacingX;
        const float anchor = 37f;

        var before = PatternLayout.Compute(config, anchor, 0, cell, cell, 2000, 600, 0, 1920, 600, 3, cellLogicalOffsetI: 4);
        var after = PatternLayout.Compute(config, anchor - stepX, 0, cell, cell, 2000, 600, 0, 1920, 600, 3, cellLogicalOffsetI: 5);

        int compared = 0;
        foreach (var a in after)
        {
            var match = before.Where(c => c.LogicalI == a.LogicalI && c.LogicalJ == a.LogicalJ).ToList();
            if (match.Count == 0) continue;   // the cell that scrolled in
            var b = match[0];
            compared++;
            Assert.Equal(b.ScreenX, a.ScreenX, 3);
            Assert.Equal(b.ScreenY, a.ScreenY, 3);
            Assert.Equal(b.RotationDeg, a.RotationDeg, 3);
            Assert.Equal(b.SourceIndex, a.SourceIndex);
        }
        Assert.Equal(10, compared);   // 6×2 cells, one column scrolled out and one in
    }

    [Theory]   // review 2026-10-13 E10: one function for players and preview
    [InlineData(false)]   // 1 s at 500 px/s = 500 px, step 120 → 4 cells + 20 px
    [InlineData(true)]
    public void EndlessTravelingScroll_SplitsPhaseAndCells(bool reversed)
    {
        const long elapsed = 1000;
        var m = new MovementConfig { Type = MovementType.Linear, Endless = true, Reversed = reversed, SpeedPixelsPerSecond = 500f };
        var r = MovementCalculator.EndlessTravelingScroll(m, elapsed, 120f)!.Value;
        Assert.Equal(reversed ? -20f : 20f, r.X, 3);
        Assert.Equal(reversed ? -4 : 4, r.CellOffsetI);
        Assert.Null(MovementCalculator.EndlessTravelingScroll(new MovementConfig { Type = MovementType.Linear }, elapsed, 120f));
    }
}

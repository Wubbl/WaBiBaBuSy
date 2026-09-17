using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>ComputeCellColor is the RGB the players tint with — the preview must see the same values.</summary>
public class ColorGraderCellColorTests
{
    [Fact]
    public void ComputeForCell_MatchesComputeCellColor_TintMatrix()
    {
        var cfg = new ColorGradingConfig { Mode = ColorGradingMode.TravelingRainbow, Seed = 7 };
        var rgb = ColorGrader.ComputeCellColor(cfg, 3, -2)!.Value;
        var m = ColorGrader.ComputeForCell(cfg, 3, -2);
        // Row 1 of the tint matrix is (0.299*r, 0.299*g, 0.299*b, 0)
        Assert.Equal(0.299f * rgb.R, m.M11, 5);
        Assert.Equal(0.299f * rgb.G, m.M12, 5);
        Assert.Equal(0.299f * rgb.B, m.M13, 5);
    }

    [Fact]
    public void NonTravelingModes_ReturnNull()
    {
        Assert.Null(ColorGrader.ComputeCellColor(new ColorGradingConfig { Mode = ColorGradingMode.Rainbow }, 1, 1));
        Assert.Null(ColorGrader.ComputeCellColor(new ColorGradingConfig { Mode = ColorGradingMode.None }, 1, 1));
        Assert.Null(ColorGrader.ComputeCellColor(null, 1, 1));
    }

    [Fact]
    public void TravelingList_EmptyList_ReturnsNull_AndIdentityMatrix()
    {
        var cfg = new ColorGradingConfig { Mode = ColorGradingMode.TravelingList };
        Assert.Null(ColorGrader.ComputeCellColor(cfg, 0, 0));
        var m = ColorGrader.ComputeForCell(cfg, 0, 0);
        Assert.Equal(1f, m.M11); Assert.Equal(0f, m.M12);
    }

    [Fact]
    public void TravelingList_IsDiagonalStripes()
    {
        var cfg = new ColorGradingConfig { Mode = ColorGradingMode.TravelingList, ColorList = { "#FF0000", "#00FF00" } };
        Assert.Equal(ColorGrader.ComputeCellColor(cfg, 0, 0), ColorGrader.ComputeCellColor(cfg, 1, 1));
        Assert.NotEqual(ColorGrader.ComputeCellColor(cfg, 0, 0), ColorGrader.ComputeCellColor(cfg, 1, 0));
    }

    [Fact]
    public void IsCellColored_FullPercentage_AlwaysTrue_ZeroNeverTrue()
    {
        var all = new ColorGradingConfig { ColoredCellPercentage = 1f };
        var none = new ColorGradingConfig { ColoredCellPercentage = 0f };
        for (int i = -5; i < 5; i++)
        {
            Assert.True(ColorGrader.IsCellColored(all, i, i * 3));
            Assert.False(ColorGrader.IsCellColored(none, i, i * 3));
        }
    }
}

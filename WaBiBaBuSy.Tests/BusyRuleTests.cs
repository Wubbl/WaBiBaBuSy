using System.Drawing;
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>A monitor is busy when windows cover ≥ 80 % of it (BiBaBo skips it).</summary>
public class BusyRuleTests
{
    private static readonly Rectangle Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void Maximised_IsBusy() => Assert.True(BusyRule.IsBusy(Monitor, new[] { new Rectangle(0, 0, 1920, 1040) }));

    [Fact]
    public void SmallWindow_IsFree() => Assert.False(BusyRule.IsBusy(Monitor, new[] { new Rectangle(100, 100, 1000, 800) }));

    [Fact]
    public void TwoHalves_AreBusy() =>
        Assert.True(BusyRule.IsBusy(Monitor, new[] { new Rectangle(0, 0, 960, 1080), new Rectangle(960, 0, 960, 1080) }));

    [Fact]
    public void OverlapCountsOnce() =>
        Assert.False(BusyRule.IsBusy(Monitor, new[] { new Rectangle(0, 0, 1150, 1080), new Rectangle(100, 0, 1150, 1080) }));

    [Fact]
    public void OffMonitorPartIgnored()
    {
        var second = new Rectangle(1920, 0, 1920, 1080);
        Assert.False(BusyRule.IsBusy(second, new[] { new Rectangle(0, 0, 1920, 1080) }));
        Assert.True(BusyRule.IsBusy(second, new[] { new Rectangle(1900, -10, 2000, 1100) }));
    }

    [Fact]
    public void NoWindows_IsFree() => Assert.False(BusyRule.IsBusy(Monitor, Array.Empty<Rectangle>()));
}

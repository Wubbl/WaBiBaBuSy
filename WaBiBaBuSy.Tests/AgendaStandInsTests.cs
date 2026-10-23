using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Which monitors get an agenda stand-in (own desktop wallpaper + panel) and which lose it.</summary>
public class AgendaStandInsTests
{
    private static HashSet<int> Set(params int[] values) => new(values);

    [Fact]
    public void Plan_On_StartsOnMonitorsWithoutSceneOrStandIn()
    {
        var (start, stop) = AgendaStandIns.Plan(agendaOn: true, monitorCount: 3, withScene: Set(1), withStandIn: Set());
        Assert.Equal(new[] { 0, 2 }, start);
        Assert.Empty(stop);
    }

    [Fact]
    public void Plan_On_KeepsExistingStandIns()
    {
        var (start, stop) = AgendaStandIns.Plan(true, 3, Set(1), Set(0, 2));
        Assert.Empty(start);
        Assert.Empty(stop);
    }

    [Fact]
    public void Plan_On_StopsStandInWhereASceneStarted()
    {
        var (start, stop) = AgendaStandIns.Plan(true, 2, withScene: Set(1), withStandIn: Set(0, 1));
        Assert.Empty(start);
        Assert.Equal(new[] { 1 }, stop);
    }

    [Fact]
    public void Plan_Off_StopsEveryStandIn()
    {
        var (start, stop) = AgendaStandIns.Plan(false, 3, Set(), Set(0, 2));
        Assert.Empty(start);
        Assert.Equal(new[] { 0, 2 }, stop);
    }

    [Fact]
    public void Plan_IgnoresMonitorsOutOfRange()
    {
        var (start, stop) = AgendaStandIns.Plan(true, 2, Set(0, 1), Set(5));
        Assert.Empty(start);
        Assert.Equal(new[] { 5 }, stop);
    }
}

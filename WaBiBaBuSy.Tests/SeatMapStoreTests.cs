using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SeatMapStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wbbs-seatmap-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Load_Missing_ReturnsSingleRow()
    {
        var map = SeatMapStore.Load(_path);
        Assert.Single(map.Rows);
        Assert.False(map.IsMultiRow);
        Assert.Equal(TraversalMode.Snake, map.Traversal);
    }

    [Fact]
    public void Save_Load_Roundtrip()
    {
        var map = SeatMap.TwoRows(10, facing: true);
        map.TurnGapCm = 222;
        map.RowGapCm = 33;
        SeatMapStore.Save(map, _path);

        var back = SeatMapStore.Load(_path);
        Assert.Equal(TraversalMode.Ring, back.Traversal);
        Assert.Equal(222, back.TurnGapCm);
        Assert.Equal(33, back.RowGapCm);
        Assert.Equal(2, back.Rows.Count);
        Assert.Equal(10, back.Rows[0].SeatCount);
        Assert.Equal(RowOrientation.Facing, back.Rows[1].Orientation);
        Assert.Contains("\"Ring\"", File.ReadAllText(_path));   // enums stored by name, hand-editable
    }

    [Fact]
    public void Load_Corrupt_ReturnsSingleRow()
    {
        File.WriteAllText(_path, "nope");
        Assert.Single(SeatMapStore.Load(_path).Rows);
    }
}

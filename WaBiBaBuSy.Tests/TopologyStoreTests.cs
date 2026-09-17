using WaBiBaBuSy.Models.Configuration;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Topology (order + bezel distance per node) must survive server restarts and be
/// re-attached to a machine by its persistent ClientId, with hostname as fallback.
/// </summary>
public class TopologyStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wbbs-topology-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyStore()
    {
        var store = TopologyStore.Load(_path);
        Assert.Empty(store.Entries);
        Assert.Equal(1, store.NextFreeOrder());
    }

    [Fact]
    public void Upsert_Save_Load_Roundtrip()
    {
        var store = TopologyStore.Load(_path);
        store.Upsert(new TopologyEntry { ClientId = "id-a", Hostname = "PC-A", OrderPosition = 3, PhysicalDistanceCm = 12 });
        store.Upsert(new TopologyEntry { ClientId = "id-b", Hostname = "PC-B", OrderPosition = 1, PhysicalDistanceCm = 0 });
        store.Save();

        var reloaded = TopologyStore.Load(_path);
        Assert.Equal(2, reloaded.Entries.Count);
        var a = reloaded.Resolve("id-a", "whatever")!;
        Assert.Equal(3, a.OrderPosition);
        Assert.Equal(12, a.PhysicalDistanceCm);
        Assert.Equal("PC-A", a.Hostname);
    }

    [Fact]
    public void Resolve_PrefersClientId_OverHostname()
    {
        var store = TopologyStore.Load(_path);
        store.Upsert(new TopologyEntry { ClientId = "id-a", Hostname = "PC-A", OrderPosition = 1 });
        store.Upsert(new TopologyEntry { ClientId = "id-b", Hostname = "PC-B", OrderPosition = 2 });

        // Same hostname as A but the id of B → B wins.
        Assert.Equal(2, store.Resolve("id-b", "PC-A")!.OrderPosition);
    }

    [Fact]
    public void Resolve_UnknownId_FallsBackToHostname_AndRebindsId()
    {
        var store = TopologyStore.Load(_path);
        store.Upsert(new TopologyEntry { ClientId = "old-id", Hostname = "PC-A", OrderPosition = 4, PhysicalDistanceCm = 7 });

        // Re-imaged machine: new GUID, same hostname → take over the seat and remember the new id.
        var entry = store.Resolve("new-id", "PC-A");
        Assert.NotNull(entry);
        Assert.Equal(4, entry!.OrderPosition);
        Assert.Equal("new-id", entry.ClientId);
        Assert.Null(store.Resolve("old-id", "PC-Z"));   // old id no longer bound
    }

    [Fact]
    public void Resolve_HostnameFallback_IsCaseInsensitive_AndSkipsWhenNoHostname()
    {
        var store = TopologyStore.Load(_path);
        store.Upsert(new TopologyEntry { ClientId = "id-a", Hostname = "pc-a", OrderPosition = 2 });

        Assert.Equal(2, store.Resolve("new", "PC-A")!.OrderPosition);
        Assert.Null(store.Resolve("new2", ""));
        Assert.Null(store.Resolve("new3", null));
    }

    [Fact]
    public void NextFreeOrder_IsOneAboveHighestKnown()
    {
        var store = TopologyStore.Load(_path);
        store.Upsert(new TopologyEntry { ClientId = "a", OrderPosition = 5 });
        store.Upsert(new TopologyEntry { ClientId = "b", OrderPosition = 2 });
        Assert.Equal(6, store.NextFreeOrder());
    }

    [Fact]
    public void SetOrder_And_SetDistance_UpdateExistingOrCreate()
    {
        var store = TopologyStore.Load(_path);
        store.SetOrder("x", "PC-X", 9);
        store.SetDistance("x", "PC-X", 33);
        var e = store.Resolve("x", "PC-X")!;
        Assert.Equal(9, e.OrderPosition);
        Assert.Equal(33, e.PhysicalDistanceCm);

        store.SetDistance("y", null, 5);          // unknown → created with distance only
        Assert.Equal(5, store.Resolve("y", null)!.PhysicalDistanceCm);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyStore_DoesNotThrow()
    {
        File.WriteAllText(_path, "{ not json");
        var store = TopologyStore.Load(_path);
        Assert.Empty(store.Entries);
    }
}

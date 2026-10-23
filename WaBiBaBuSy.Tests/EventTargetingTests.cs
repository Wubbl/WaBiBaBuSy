using Google.Protobuf;
using WaBiBaBuSy.Core.Services.Show;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Targeted show events (tags / one seat), Identify labels, and the empty-seat gap at a row start.</summary>
public class EventTargetingTests
{
    [Fact]
    public void IsFor_NullTargets_IsEveryone_ElseOnlyTheListedSeats()
    {
        var evt = new ShowEvent();
        Assert.True(evt.IsFor("any"));
        evt.TargetNodeIds = new List<string> { "a", "b" };
        Assert.True(evt.IsFor("b"));
        Assert.False(evt.IsFor("c"));
    }

    [Fact]
    public void Codec_RoundTrip_KeepsTargetsAndLabels()
    {
        var evt = new ShowEvent
        {
            EventId = "id", Type = ShowEventType.Identify, T0Utc = 1000, DurationMs = 10000,
            TargetNodeIds = new List<string> { "seat-3" },
            Labels = new Dictionary<string, string> { ["seat-3"] = "3  GAMING-PC" },
        };
        var cmd = SyncCommand.Parser.ParseFrom(ShowEventCodec.ToCommand(evt, 1).ToByteArray());
        Assert.True(ShowEventCodec.TryFromCommand(cmd, out var back));
        Assert.Equal(new[] { "seat-3" }, back.TargetNodeIds);
        Assert.Equal("3  GAMING-PC", back.Labels!["seat-3"]);
    }

    [Fact]
    public void TagStore_SetParsesDedupesAndPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tags-{Guid.NewGuid():N}.json");
        try
        {
            var store = NodeTagStore.Load(path);
            store.Set(new[] { "a", "b" }, new[] { " BiBaBu , bibabu,Guests " });
            store.Set(new[] { "c" }, new[] { "Guests" });

            var reloaded = NodeTagStore.Load(path);
            Assert.Equal(new[] { "BiBaBu", "Guests" }, reloaded.Of("a"));
            Assert.Equal(new[] { "BiBaBu", "Guests" }, reloaded.All());
            Assert.Equal(new[] { "a", "b", "c" }, reloaded.NodesWith("guests").OrderBy(x => x));

            reloaded.Set(new[] { "a" }, new[] { "  " });   // blank clears
            Assert.Empty(reloaded.Of("a"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Target_ChoicesAndResolve()
    {
        var choices = EventTarget.Choices(new[] { "BiBaBu" }, new[] { ("s1", "#1 PC-A") });
        Assert.Equal(new[] { "Everyone", "Selected nodes", "Tag: BiBaBu", "Seat #1 PC-A" }, choices.Select(c => c.Label));

        IReadOnlyList<string> Lookup(string tag) => tag == "BiBaBu" ? new[] { "s1", "s2" } : Array.Empty<string>();
        Assert.Null(choices[0].Resolve(Lookup));
        Assert.Equal(new[] { "s2", "s3" }, choices[1].Resolve(Lookup, () => new[] { "s2", "s3" }));
        Assert.Empty(choices[1].Resolve(Lookup, () => Array.Empty<string>()));   // nothing selected = nobody, never everyone
        Assert.Equal(new[] { "s1", "s2" }, choices[2].Resolve(Lookup));
        Assert.Equal(new[] { "s1" }, choices[3].Resolve(Lookup));
    }

    private static List<LayoutNodeInput> Nodes(int count, Func<int, int>? gapCm = null) =>
        Enumerable.Range(0, count).Select(i => new LayoutNodeInput
        {
            Id = $"n{i}", WidthPx = 1920, HeightPx = 1080, GapBeforeCm = gapCm?.Invoke(i) ?? 0, PixelsPerCm = 40f
        }).ToList();

    [Fact]
    public void RowStartGap_AfterATurn_IsAnEmptySeat()
    {
        // 2 rows × 3, Ring; n3 opens row 1 with a 60 cm hole before it (60 × 40 = 2400 px), turn gap 150 cm = 6000 px.
        var r = SeatMapLayoutBuilder.Build(SeatMap.TwoRows(3), Nodes(6, i => i == 3 ? 60 : 0));
        Assert.Equal(3 * 1920 + 6000 + 2400, r.Get("n3")!.OffsetX);
        Assert.Equal(6 * 1920 + 2 * 6000 + 2400, r.CanvasWidth);
    }

    [Fact]
    public void SeatZeroGap_ExtendsTheRingsClosingTurn_ButNotAnOpenPath()
    {
        var ring = SeatMapLayoutBuilder.Build(SeatMap.TwoRows(3), Nodes(6, i => i == 0 ? 60 : 0));
        Assert.Equal(2400, ring.Get("n0")!.OffsetX);

        var open = SeatMapLayoutBuilder.Build(SeatMap.SingleRow(), Nodes(3, i => i == 0 ? 60 : 0));
        Assert.Equal(0, open.Get("n0")!.OffsetX);
        Assert.Equal(3 * 1920, open.CanvasWidth);
    }
}

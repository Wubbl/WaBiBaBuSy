using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Playlist item transitions (roadmap 2.5): wire form, Fade / Wipe math, item vs playlist default.</summary>
public class SceneTransitionTests
{
    [Theory]
    [InlineData(null, TransitionKind.Cut, 1500)]
    [InlineData("", TransitionKind.Cut, 1500)]
    [InlineData("Fade:2000", TransitionKind.Fade, 2000)]
    [InlineData("wipe:4000", TransitionKind.Wipe, 4000)]
    [InlineData("Wipe", TransitionKind.Wipe, 1500)]
    [InlineData("Spin:900", TransitionKind.Cut, 1500)]   // a newer server's kind → Cut
    public void Parse(string? wire, TransitionKind kind, int ms)
    {
        var t = SceneTransition.Parse(wire);
        Assert.Equal(kind, t.Kind);
        Assert.Equal(ms, t.ClampedMs);
    }

    [Fact]
    public void Encode_RoundTrips_AndCutIsEmpty()
    {
        Assert.Equal("", SceneTransition.Cut.Encode());
        var fade = new SceneTransition { Kind = TransitionKind.Fade, DurationMs = 50 };   // clamped to the minimum
        Assert.Equal("Fade:200", fade.Encode());
        Assert.Equal(TransitionKind.Fade, SceneTransition.Parse(fade.Encode()).Kind);
    }

    [Fact]
    public void Fade_OutThenIn_MeetsAtBlackOnTheSharedStart()
    {
        // 2000 ms: out over [-1000, 0), in over [0, 1000).
        Assert.Equal(0f, TransitionMath.FadeOutVeil(-1500, 2000));
        Assert.Equal(0.5f, TransitionMath.FadeOutVeil(-500, 2000), 3);
        Assert.Equal(1f, TransitionMath.FadeOutVeil(0, 2000));
        Assert.Equal(1f, TransitionMath.FadeInVeil(-1, 2000));
        Assert.Equal(1f, TransitionMath.FadeInVeil(0, 2000));
        Assert.Equal(0.5f, TransitionMath.FadeInVeil(500, 2000), 3);
        Assert.Equal(0f, TransitionMath.FadeInVeil(1000, 2000));
    }

    [Fact]
    public void Wipe_BoundaryRunsTheCanvasOnce_AndEntersMirroredNodesFromTheRight()
    {
        Assert.Equal(0f, TransitionMath.WipeBoundary(-100, 3000, 6000));
        Assert.Equal(3000f, TransitionMath.WipeBoundary(1500, 3000, 6000));
        Assert.Equal(6000f, TransitionMath.WipeBoundary(9999, 3000, 6000));

        var node = new NodeLayout { OffsetX = 2000, Width = 1920 };
        Assert.Equal((0f, 0f), TransitionMath.WipedSpan(1500, node));         // not there yet
        Assert.Equal((0f, 500f), TransitionMath.WipedSpan(2500, node));        // left part
        Assert.Equal((0f, 1920f), TransitionMath.WipedSpan(9000, node));       // passed

        node.Mirrored = true;
        Assert.Equal((1420f, 1920f), TransitionMath.WipedSpan(2500, node));    // from the right edge
    }

    [Fact]
    public void OutgoingPlayersStayForTheWholeWipeOnly()
    {
        Assert.Equal(0, TransitionMath.OutgoingHoldMs(SceneTransition.Cut));
        Assert.Equal(0, TransitionMath.OutgoingHoldMs(new SceneTransition { Kind = TransitionKind.Fade, DurationMs = 3000 }));
        Assert.Equal(3000, TransitionMath.OutgoingHoldMs(new SceneTransition { Kind = TransitionKind.Wipe, DurationMs = 3000 }));
    }

    [Fact]
    public async Task Orchestrator_UsesTheItemsTransition_ElseThePlaylistDefault()
    {
        var seen = new List<SceneTransition>();
        PlaylistOrchestrator? o = null;
        o = new PlaylistOrchestrator(NullLogger<PlaylistOrchestrator>.Instance,
            apply: _ => { lock (seen) seen.Add(o!.CurrentTransition); return Task.FromResult(new ApplyMetrics(1000, 0)); },
            seedProvider: () => 1);
        var show = new Playlist
        {
            DefaultItemDurationMs = 60_000,
            DefaultTransition = new SceneTransition { Kind = TransitionKind.Fade, DurationMs = 1000 },
            Items =
            {
                new PlaylistItem { Name = "a" },
                new PlaylistItem { Name = "b", Transition = new SceneTransition { Kind = TransitionKind.Wipe, DurationMs = 4000 } },
            },
        };
        o.Start(show);
        await WaitFor(() => seen.Count >= 1);
        o.Skip();
        await WaitFor(() => seen.Count >= 2);
        await o.StopAsync();

        Assert.Equal(TransitionKind.Fade, seen[0].Kind);
        Assert.Equal(TransitionKind.Wipe, seen[1].Kind);
        Assert.Equal(4000, seen[1].ClampedMs);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(5);
        }
    }
}

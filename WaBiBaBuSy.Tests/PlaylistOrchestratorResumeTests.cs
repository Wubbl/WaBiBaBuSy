using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Capturing where a running show is and starting it again there (test runs restore a show).</summary>
public class PlaylistOrchestratorResumeTests
{
    private readonly List<CrossScreenConfig> _applied = new();
    private readonly object _gate = new();

    private PlaylistOrchestrator Orchestrator(int seed = 7) => new(
        NullLogger<PlaylistOrchestrator>.Instance,
        apply: config =>
        {
            lock (_gate) _applied.Add(config);
            return Task.FromResult(new ApplyMetrics(1000, 0));
        },
        seedProvider: () => seed);

    /// <summary>Items with a dwell long enough that only Skip() advances them.</summary>
    private static Playlist Show(int items, bool loop = true, bool shuffle = false) => new()
    {
        Name = "party",
        Loop = loop,
        Shuffle = shuffle,
        DefaultItemDurationMs = 60_000,
        Items = Enumerable.Range(0, items).Select(i => new PlaylistItem { Name = $"item {i}", Config = new CrossScreenConfig() }).ToList(),
    };

    private int AppliedCount { get { lock (_gate) return _applied.Count; } }

    private CrossScreenConfig Applied(int i) { lock (_gate) return _applied[i]; }

    private async Task WaitForAppliesAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (AppliedCount < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"expected {count} applies, saw {AppliedCount}");
            await Task.Delay(5);
        }
        await Task.Delay(20);   // let the loop reach its dwell (and publish the position) after the apply
    }

    [Fact]
    public async Task StartWhileAStopIsStillFinishing_TheNewShowSurvivesTheStop()
    {
        // Review 2026-10-13 C5: StopAsync used to null the fields after its awaits, wiping a show started meanwhile.
        var interruptRunning = new TaskCompletionSource();
        var releaseInterrupt = new TaskCompletionSource();
        var o = new PlaylistOrchestrator(NullLogger<PlaylistOrchestrator>.Instance,
            apply: config => { lock (_gate) _applied.Add(config); return Task.FromResult(new ApplyMetrics(1000, 0)); },
            seedProvider: () => 7,
            fireEvent: _ => { interruptRunning.TrySetResult(); return releaseInterrupt.Task; })
        { MinInterruptEveryMs = 1 };
        var first = Show(2);
        first.Interrupts.Add(new PlaylistInterrupt { EveryMs = 10, Type = WaBiBaBuSy.Models.Show.ShowEventType.Blackout });
        o.Start(first);
        await interruptRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = o.StopAsync();   // the loop ends at once; the stop still waits for the running interrupt
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (o.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(5);
        var second = Show(2);
        second.Name = "second";
        o.Start(second);
        releaseInterrupt.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(o.IsRunning);
        Assert.Equal("second", o.CaptureResumePoint()?.Playlist.Name);
        await o.StopAsync();
    }

    [Fact]
    public void CaptureResumePoint_NullWhenNothingRuns()
    {
        Assert.Null(Orchestrator().CaptureResumePoint());
    }

    [Fact]
    public async Task CaptureResumePoint_TracksTheCurrentItem()
    {
        var o = Orchestrator();
        var show = Show(3);
        o.Start(show);
        await WaitForAppliesAsync(1);

        var first = o.CaptureResumePoint();
        Assert.NotNull(first);
        Assert.Same(show, first!.Playlist);
        Assert.Equal(0, first.ItemIndex);

        o.Skip();
        await WaitForAppliesAsync(2);
        var second = o.CaptureResumePoint();
        Assert.Equal(1, second!.ItemIndex);
        Assert.Equal(1, second.CyclePosition);
        Assert.Equal(new[] { 0, 1, 2 }, second.CycleOrder);

        await o.StopAsync();
        Assert.Null(o.CaptureResumePoint());
    }

    [Fact]
    public async Task Resume_StartsAtTheCapturedItem_AndContinuesInOrder()
    {
        var show = Show(3);
        var o = Orchestrator();
        o.Resume(new PlaylistResumePoint(show, new[] { 0, 1, 2 }, 2));
        await WaitForAppliesAsync(1);

        Assert.True(o.IsRunning);
        Assert.Same(show.Items[2].Config, Applied(0));
        Assert.Equal(2, o.CurrentItemIndex);
        Assert.Same(show.Items[0], o.NextItem);                 // looping: the next cycle starts at item 0

        o.Skip();
        await WaitForAppliesAsync(2);
        Assert.Same(show.Items[0].Config, Applied(1));
        await o.StopAsync();
    }

    [Fact]
    public async Task Resume_KeepsTheShuffledOrderForTheRestOfTheCycle()
    {
        var show = Show(3, shuffle: true);
        var o = Orchestrator();
        o.Resume(new PlaylistResumePoint(show, new[] { 2, 0, 1 }, 1));
        await WaitForAppliesAsync(1);

        Assert.Same(show.Items[0].Config, Applied(0));
        Assert.Same(show.Items[1], o.NextItem);
        var point = o.CaptureResumePoint();
        Assert.Equal(new[] { 2, 0, 1 }, point!.CycleOrder);
        Assert.Equal(1, point.CyclePosition);

        o.Skip();
        await WaitForAppliesAsync(2);
        Assert.Same(show.Items[1].Config, Applied(1));
        await o.StopAsync();
    }

    [Fact]
    public async Task Resume_NonLoopingShowAtItsLastItem_PlaysItThenEnds()
    {
        var show = Show(2, loop: false);
        var o = Orchestrator();
        o.Resume(new PlaylistResumePoint(show, new[] { 0, 1 }, 1));
        await WaitForAppliesAsync(1);

        Assert.Same(show.Items[1].Config, Applied(0));
        Assert.Null(o.NextItem);
        o.Skip();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (o.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.False(o.IsRunning);
        Assert.Equal(1, AppliedCount);
        Assert.Null(o.CaptureResumePoint());
    }

    [Fact]
    public async Task Resume_WithAStaleOrder_StartsTheShowFromTheTop()
    {
        var show = Show(2);
        var o = Orchestrator();
        // The order does not fit the playlist (e.g. built for a longer list): start over instead of failing.
        o.Resume(new PlaylistResumePoint(show, new[] { 0, 1, 2 }, 2));
        await WaitForAppliesAsync(1);

        Assert.Same(show.Items[0].Config, Applied(0));
        await o.StopAsync();
    }

    [Fact]
    public async Task Resume_IsIgnoredWhileAShowRuns()
    {
        var show = Show(2);
        var o = Orchestrator();
        o.Start(show);
        await WaitForAppliesAsync(1);
        o.Resume(new PlaylistResumePoint(show, new[] { 0, 1 }, 1));
        await Task.Delay(50);

        Assert.Equal(1, AppliedCount);
        Assert.Equal(0, o.CurrentItemIndex);
        await o.StopAsync();
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class PlaylistInterruptTests
{
    private readonly List<ShowEventType> _fired = new();

    private PlaylistOrchestrator Orchestrator() => new(
        NullLogger<PlaylistOrchestrator>.Instance,
        apply: _ => Task.FromResult(new ApplyMetrics(1000, 0)),
        seedProvider: () => 1,
        fireEvent: t => { lock (_fired) _fired.Add(t); return Task.CompletedTask; })
    { MinInterruptEveryMs = 0 };

    private static Playlist Show(params PlaylistInterrupt[] interrupts) => new()
    {
        DefaultItemDurationMs = 60_000,
        Items = { new PlaylistItem { Name = "a", Config = new CrossScreenConfig() } },
        Interrupts = interrupts.ToList(),
    };

    private int Count(ShowEventType t) { lock (_fired) return _fired.Count(x => x == t); }

    [Fact]
    public async Task Interrupt_FiresRepeatedly_WithoutAdvancingTheItem()
    {
        var o = Orchestrator();
        o.Start(Show(new PlaylistInterrupt { EveryMs = 40, Type = ShowEventType.Celebration }));
        await Task.Delay(600);
        Assert.True(Count(ShowEventType.Celebration) >= 3, $"fired {Count(ShowEventType.Celebration)}×");
        Assert.Equal(0, o.CurrentItemIndex);
        await o.StopAsync();
    }

    [Fact]
    public async Task Stop_EndsTheInterrupts()
    {
        var o = Orchestrator();
        o.Start(Show(new PlaylistInterrupt { EveryMs = 30, Type = ShowEventType.Blackout }));
        await Task.Delay(120);
        await o.StopAsync();
        int after = Count(ShowEventType.Blackout);
        Assert.True(after > 0, "the interrupt never fired before Stop");
        await Task.Delay(150);
        Assert.Equal(after, Count(ShowEventType.Blackout));
    }

    [Fact]
    public async Task TooFrequent_NeverFires()
    {
        var o = Orchestrator();
        o.MinInterruptEveryMs = 10_000;
        o.Start(Show(new PlaylistInterrupt { EveryMs = 20, Type = ShowEventType.Traversal }));
        await Task.Delay(150);
        Assert.Equal(0, Count(ShowEventType.Traversal));
        await o.StopAsync();
    }

    [Fact]
    public async Task Disabled_NeverFires()
    {
        var o = Orchestrator();   // MinInterruptEveryMs = 0: only Enabled = false holds it back
        o.Start(Show(new PlaylistInterrupt { EveryMs = 20, Type = ShowEventType.Celebration, Enabled = false }));
        await Task.Delay(150);
        Assert.Equal(0, Count(ShowEventType.Celebration));
        await o.StopAsync();
    }

    [Fact]
    public async Task NonLoopingShow_EndsItsInterruptsWhenTheShowEnds()
    {
        var o = Orchestrator();
        var show = Show(new PlaylistInterrupt { EveryMs = 20, Type = ShowEventType.Spotlight });
        show.Loop = false;
        show.DefaultItemDurationMs = 50;
        o.Start(show);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (o.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.False(o.IsRunning);
        await Task.Delay(40);   // let a cancellation in flight settle
        int after = Count(ShowEventType.Spotlight);
        await Task.Delay(150);
        Assert.Equal(after, Count(ShowEventType.Spotlight));
        await o.StopAsync();
    }
}

using WaBiBaBuSy.Core.Services.Show;
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>The server's BiBaBo referee: first valid action wins, grace after the deadline, timeout once, abort.</summary>
public class BombGameServiceTests
{
    private const long T0 = 1_000_000;
    private long _now = T0;
    private readonly List<ShowEvent> _dispatched = new();
    private readonly BombGameService _service;

    public BombGameServiceTests()
    {
        _service = new BombGameService(() => _now, e => { _dispatched.Add(e); return Task.CompletedTask; }, () => 500);
    }

    private static ShowEvent BombEvent() => new()
    {
        EventId = "game-1", Type = ShowEventType.Bomb, T0Utc = T0, DurationMs = 323_000, Seed = 9, ImagePath = "logo.gif",
        TargetNodeIds = new List<string> { "c1", "c2" },
        Bomb = new BombGame { GameId = "game-1", BombNodeId = "c3_MONITOR_1", IntroMs = 8000, TimerMs = 300_000, Cables = { new(), new(), new() } },
    };

    private long Deadline => T0 + 8000 + 300_000;

    private ShowEvent Outcome() => Assert.Single(_dispatched);

    [Fact]
    public async Task Service_CutLive_Defuses()
    {
        _service.Begin(BombEvent(), liveCable: 2);
        Assert.Equal("c3", _service.BombMachine);
        _now = T0 + 8000 + 100_000;
        Assert.True(await _service.OnActionAsync("c3", "game-1", "cut", 2));
        var o = Outcome();
        Assert.Equal(ShowEventType.BombOutcome, o.Type);
        Assert.Equal(BombResult.Defused, o.Bomb!.Result);
        Assert.Equal(2, o.Bomb.CutCable);
        Assert.Equal(200_000, o.Bomb.RemainingMs);
        Assert.Equal(_now + 500, o.T0Utc);
        Assert.Equal("game-1", o.Bomb.GameId);
        Assert.Equal(new[] { "c1", "c2" }, o.TargetNodeIds);
        Assert.Equal("logo.gif", o.ImagePath);
        Assert.Equal(BombMath.OutcomeDurationMs, o.DurationMs);
        Assert.False(_service.IsRunning);
    }

    [Fact]
    public async Task Service_CutDead_Booms()
    {
        _service.Begin(BombEvent(), liveCable: 2);
        _now = T0 + 20_000;
        Assert.True(await _service.OnActionAsync("c3", "game-1", "cut", 0));
        Assert.Equal(BombResult.WrongCable, Outcome().Bomb!.Result);
    }

    [Fact]
    public async Task Service_Recycle_Recycles()
    {
        _service.Begin(BombEvent(), liveCable: 1);
        _now = T0 + 20_000;
        Assert.True(await _service.OnActionAsync("c3", "game-1", "recycle", -1));
        Assert.Equal(BombResult.Recycled, Outcome().Bomb!.Result);
    }

    [Fact]
    public async Task Service_FirstActionWins()
    {
        _service.Begin(BombEvent(), liveCable: 1);
        _now = T0 + 20_000;
        Assert.True(await _service.OnActionAsync("c3", "game-1", "cut", 1));
        Assert.False(await _service.OnActionAsync("c3", "game-1", "recycle", -1));
        await _service.TickAsync();
        _now = Deadline + 10_000;
        await _service.TickAsync();
        Assert.Equal(BombResult.Defused, Outcome().Bomb!.Result);
    }

    [Fact]
    public async Task Service_WrongMachineOrGame_Ignored()
    {
        _service.Begin(BombEvent(), liveCable: 1);
        _now = T0 + 20_000;
        Assert.False(await _service.OnActionAsync("c4", "game-1", "cut", 1));
        Assert.False(await _service.OnActionAsync("c3", "game-0", "cut", 1));
        Assert.False(await _service.OnActionAsync("c3", "game-1", "cut", 7));
        Assert.False(await _service.OnActionAsync("c3", "game-1", "dance", 1));
        Assert.Empty(_dispatched);
        Assert.True(_service.IsRunning);
    }

    [Fact]
    public async Task Service_CutBeforeTheCables_Ignored()
    {
        _service.Begin(BombEvent(), liveCable: 1);
        _now = T0 + 3000;
        Assert.False(await _service.OnActionAsync("c3", "game-1", "cut", 1));
    }

    [Fact]
    public async Task Service_CutWithinGrace_Wins()
    {
        _service.Begin(BombEvent(), liveCable: 0);
        _now = Deadline + 200;
        Assert.True(await _service.OnActionAsync("c3", "game-1", "cut", 0));
        Assert.Equal(BombResult.Defused, Outcome().Bomb!.Result);
        Assert.Equal(0, Outcome().Bomb!.RemainingMs);
    }

    [Fact]
    public async Task Service_CutAfterGrace_Ignored()
    {
        _service.Begin(BombEvent(), liveCable: 0);
        _now = Deadline + 400;
        Assert.False(await _service.OnActionAsync("c3", "game-1", "cut", 0));
        await _service.TickAsync();
        Assert.Equal(BombResult.Timeout, Outcome().Bomb!.Result);
    }

    [Fact]
    public async Task Service_TimeoutFiresOnce()
    {
        _service.Begin(BombEvent(), liveCable: 0);
        _now = Deadline + 100;
        await _service.TickAsync();
        Assert.Empty(_dispatched);   // still in the grace window
        _now = Deadline + 301;
        await _service.TickAsync();
        await _service.TickAsync();
        Assert.Equal(BombResult.Timeout, Outcome().Bomb!.Result);
    }

    [Fact]
    public async Task Service_Abort_DispatchesAborted()
    {
        _service.Begin(BombEvent(), liveCable: 0);
        _now = T0 + 50_000;
        await _service.AbortAsync();
        var o = Outcome();
        Assert.Equal(BombResult.Aborted, o.Bomb!.Result);
        Assert.Equal(BombMath.AbortDurationMs, o.DurationMs);
        await _service.AbortAsync();
        Assert.Single(_dispatched);
    }

    [Fact]
    public async Task Service_LocalSeats_AreThisMachine()
    {
        var e = BombEvent();
        e.Bomb!.BombNodeId = "SERVER_LOCALHOST_MONITOR_0";
        _service.Begin(e, liveCable: 0);
        _now = T0 + 20_000;
        Assert.True(await _service.OnActionAsync(BombSeats.ThisMachine, "game-1", "cut", 0));
    }
}

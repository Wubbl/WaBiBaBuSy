using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Core.Services.Show;

/// <summary>
/// The server's BiBaBo referee (design §5.2): knows the live cable (never sent), accepts the first valid action from the
/// bomb seat's machine, and fires the BombOutcome event — on a cut, the Recycle Bin, the timeout (deadline + 300 ms
/// grace for reports in flight) or an abort. Clock, dispatch and outcome lead are injected; the host calls
/// <see cref="TickAsync"/> a few times a second while a game runs.
/// </summary>
public sealed class BombGameService
{
    /// <summary>A report this late after the deadline still counts (it was sent before 0:00).</summary>
    public const int GraceMs = 300;

    private readonly Func<long> _nowUtcMs;
    private readonly Func<ShowEvent, Task> _dispatch;
    private readonly Func<int> _leadMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ShowEvent? _bomb;
    private int _liveCable;

    /// <param name="leadMs">How far ahead the outcome starts, so every seat shows it on the same frame.</param>
    public BombGameService(Func<long> nowUtcMs, Func<ShowEvent, Task> dispatch, Func<int> leadMs)
    {
        _nowUtcMs = nowUtcMs;
        _dispatch = dispatch;
        _leadMs = leadMs;
    }

    public bool IsRunning => _bomb != null;
    public BombGame? Current => _bomb?.Bomb;
    public ShowEvent? CurrentEvent => _bomb;

    /// <summary>The machine whose input counts (<see cref="BombSeats.MachineOf"/> of the bomb node).</summary>
    public string? BombMachine => _bomb?.Bomb is { } g ? BombSeats.MachineOf(g.BombNodeId) : null;

    private long Deadline(BombGame g) => _bomb!.T0Utc + g.IntroMs + g.TimerMs;

    /// <summary>A game was fired: referee it.</summary>
    public void Begin(ShowEvent bombEvent, int liveCable)
    {
        _bomb = bombEvent;
        _liveCable = liveCable;
    }

    /// <summary>"cut" (cable 0..2) or "recycle" from <paramref name="machine"/>; true when it decided the game.</summary>
    public async Task<bool> OnActionAsync(string machine, string gameId, string action, int cable)
    {
        await _gate.WaitAsync();
        try
        {
            if (_bomb?.Bomb is not { } g || g.GameId != gameId || machine != BombMachine) return false;
            long now = _nowUtcMs();
            if (now < _bomb.T0Utc + BombMath.DropEndMs || now > Deadline(g) + GraceMs) return false;
            BombResult result;
            if (action == "recycle") result = BombResult.Recycled;
            else if (action == "cut" && cable >= 0 && cable < g.Cables.Count) result = cable == _liveCable ? BombResult.Defused : BombResult.WrongCable;
            else return false;
            await ResolveAsync(result, action == "cut" ? cable : -1, now);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Timeout check: past the deadline + grace with no action → BOOM.</summary>
    public async Task TickAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_bomb?.Bomb is { } g && _nowUtcMs() > Deadline(g) + GraceMs)
                await ResolveAsync(BombResult.Timeout, -1, _nowUtcMs());
        }
        finally { _gate.Release(); }
    }

    /// <summary>The trigger again, Stop or Clear all: end quietly.</summary>
    public async Task AbortAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_bomb != null) await ResolveAsync(BombResult.Aborted, -1, _nowUtcMs());
        }
        finally { _gate.Release(); }
    }

    private async Task ResolveAsync(BombResult result, int cut, long now)
    {
        var bomb = _bomb!;
        var g = bomb.Bomb!;
        _bomb = null;
        var outcome = new ShowEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Type = ShowEventType.BombOutcome,
            T0Utc = now + _leadMs(),
            DurationMs = result == BombResult.Aborted ? BombMath.AbortDurationMs : BombMath.OutcomeDurationMs,
            Seed = bomb.Seed,
            ColorHex = bomb.ColorHex,
            ImagePath = bomb.ImagePath,
            TargetNodeIds = bomb.TargetNodeIds,
            Bomb = g.WithOutcome(result, cut, BombMath.RemainingMs(g, now - bomb.T0Utc)),
        };
        await _dispatch(outcome);
    }
}

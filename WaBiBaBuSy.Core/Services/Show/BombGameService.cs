using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Core.Services.Show;

/// <summary>
/// The server's BiBaBo referee (design §5.2, §14): knows the answers (never sent), accepts valid actions from the bomb
/// seat's machine and fires BombStep events (cut-order progress, typed digits) and the BombOutcome — on the deciding
/// cut / code, the Recycle Bin, the timeout (deadline + 300 ms grace for reports in flight) or an abort. Clock,
/// dispatch and outcome lead are injected; the host calls <see cref="TickAsync"/> a few times a second while a game runs.
/// </summary>
public sealed class BombGameService
{
    /// <summary>A report this late after the deadline still counts (it was sent before 0:00).</summary>
    public const int GraceMs = 300;

    /// <summary>Keypad "C": clear the typed digits.</summary>
    public const int ClearKey = 10;

    private readonly Func<long> _nowUtcMs;
    private readonly Func<ShowEvent, Task> _dispatch;
    private readonly Func<int> _leadMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ShowEvent? _bomb;
    private BombSolution _solution = new();
    private readonly List<int> _cut = new();
    private string _typed = "";

    /// <param name="leadMs">How far ahead steps and the outcome start, so every seat shows them on the same frame.</param>
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

    /// <summary>A game was fired: referee it with its answers.</summary>
    public void Begin(ShowEvent bombEvent, BombSolution solution)
    {
        _bomb = bombEvent;
        _solution = solution;
        _cut.Clear();
        _typed = "";
    }

    /// <summary>
    /// "cut" (cable 0..2), "key" (0..9, <see cref="ClearKey"/>) or "recycle" from <paramref name="machine"/>; true when it
    /// changed the game (a step or the outcome).
    /// </summary>
    public async Task<bool> OnActionAsync(string machine, string gameId, string action, int value)
    {
        await _gate.WaitAsync();
        try
        {
            if (_bomb?.Bomb is not { } g || g.GameId != gameId || machine != BombMachine) return false;
            long now = _nowUtcMs();
            if (now < _bomb.T0Utc + BombMath.DropEndMs || now > Deadline(g) + GraceMs) return false;
            switch (action)
            {
                case "recycle":
                    await ResolveAsync(BombResult.Recycled, -1, now);
                    return true;
                case "cut" when value >= 0 && value < g.Cables.Count && _solution.Mode != BombMode.Keypad:
                    return await CutAsync(value, now);
                case "key" when _solution.Mode == BombMode.Keypad && value >= 0 && value <= ClearKey:
                    return await KeyAsync(value, now);
                default:
                    return false;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> CutAsync(int cable, long now)
    {
        if (_solution.Mode == BombMode.Classic)
        {
            await ResolveAsync(cable == _solution.LiveCable ? BombResult.Defused : BombResult.WrongCable, cable, now);
            return true;
        }
        if (_cut.Contains(cable)) return false;   // already cut
        _cut.Add(cable);
        if (_solution.CutOrder.Length < _cut.Count || _solution.CutOrder[_cut.Count - 1] != cable)
            await ResolveAsync(BombResult.WrongCable, cable, now);
        else if (_cut.Count == _solution.CutOrder.Length)
            await ResolveAsync(BombResult.Defused, cable, now);
        else
            await StepAsync(now);
        return true;
    }

    private async Task<bool> KeyAsync(int key, long now)
    {
        if (key == ClearKey) _typed = "";
        else _typed += (char)('0' + key);
        if (_typed.Length < _solution.Code.Length)
        {
            await StepAsync(now);
            return true;
        }
        await ResolveAsync(_typed == _solution.Code ? BombResult.Defused : BombResult.WrongCable, -1, now);
        return true;
    }

    /// <summary>Timeout check: past the deadline + grace with no decision → BOOM.</summary>
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

    /// <summary>
    /// Progress everyone sees (a cable cut, digits typed); lasts as long as the bomb is booked. Shown at once — no start
    /// lead: a typed digit that appears a second late gets typed twice.
    /// </summary>
    private Task StepAsync(long now)
    {
        var bomb = _bomb!;
        long t0 = now;
        return _dispatch(new ShowEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Type = ShowEventType.BombStep,
            T0Utc = t0,
            DurationMs = ShowEventMath.ClampDuration((int)Math.Max(1, bomb.T0Utc + ShowEventMath.ClampDuration(bomb.DurationMs) - t0)),
            Seed = bomb.Seed,
            TargetNodeIds = bomb.TargetNodeIds,
            Bomb = bomb.Bomb!.WithProgress(_cut, _typed),
        });
    }

    private async Task ResolveAsync(BombResult result, int cut, long now)
    {
        var bomb = _bomb!;
        var g = bomb.Bomb!.WithProgress(_cut, _typed);
        _bomb = null;
        var (image, scale) = BombLogo.OutcomeImage(result, bomb.ImagePath, shippedLogo: g.LogoTimerBox.Length == 4);
        var decided = g.WithOutcome(result, cut, BombMath.RemainingMs(g, now - bomb.T0Utc));
        decided.OutcomeImageScale = scale;
        var outcome = new ShowEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Type = ShowEventType.BombOutcome,
            T0Utc = now + _leadMs(),
            DurationMs = result == BombResult.Aborted ? BombMath.AbortDurationMs : BombMath.OutcomeDurationMs,
            Seed = bomb.Seed,
            ColorHex = bomb.ColorHex,
            ImagePath = image,
            TargetNodeIds = bomb.TargetNodeIds,
            Bomb = decided,
        };
        await _dispatch(outcome);
    }
}

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.UI.Services;

/// <summary>
/// BiBaBo input on this machine when it holds the bomb (design §8): desktop clicks on a cable's cut zone, and the Recycle
/// Bin icon moved onto the bomb (polled 4× a second). Started for a Bomb event whose bomb node is one of this machine's
/// monitors, stopped at the outcome or shortly after the deadline. Observes only; reports through <c>report</c>
/// ("cut", cable) / ("recycle", −1) — the server referees.
/// </summary>
public sealed class BombInputWatcher : IDisposable
{
    private readonly Func<string, string, int, Task> _report;   // (game id, action, cable)
    private RawMouseSink? _mouse;
    private CancellationTokenSource? _cts;
    private string? _gameId;

    public BombInputWatcher(Func<string, string, int, Task> report) => _report = report;

    /// <summary>Watch <paramref name="monitorIndex"/> (laid out as <paramref name="node"/>) for the game of <paramref name="bomb"/>.</summary>
    public void Start(ShowEvent bomb, NodeLayout node, int monitorIndex)
    {
        Stop();
        if (bomb.Bomb is not { } game) return;
        var monitors = NativeMonitorInfo.GetAllMonitors();
        if (monitorIndex < 0 || monitorIndex >= monitors.Length) return;
        var bounds = monitors[monitorIndex].Bounds;
        // Device pixels of the node per screen pixel (1 when this process sees physical pixels, else the DPI scale).
        float scaleX = node.Width * (node.Scale > 0 ? node.Scale : 1f) / Math.Max(1, bounds.Width);
        float scaleY = node.Height * (node.Scale > 0 ? node.Scale : 1f) / Math.Max(1, bounds.Height);
        _gameId = bomb.EventId;
        _cts = new CancellationTokenSource();
        long untilMs = bomb.T0Utc + game.IntroMs + game.TimerMs + 1000 - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, untilMs)));
        var token = _cts.Token;
        token.Register(() => _mouse?.Stop());

        _mouse = new RawMouseSink();
        _mouse.LeftButtonDown += (x, y) =>
        {
            if (token.IsCancellationRequested || !bounds.Contains(x, y)) return;
            int cable = BombHit.CableAt(game, node, (x - bounds.X) * scaleX, (y - bounds.Y) * scaleY);
            Debug.WriteLine($"[BiBaBo] Desktop click at {x},{y} → cable {cable}");
            if (cable >= 0) _ = Report(bomb.EventId, "cut", cable);
        };
        _mouse.Start();

        var thread = new Thread(() => WatchRecycleBin(bomb.EventId, game, node, bounds, scaleX, scaleY, token)) { IsBackground = true, Name = "BiBaBoRecycleBin" };
        thread.SetApartmentState(ApartmentState.STA);   // shell COM
        thread.Start();
    }

    private void WatchRecycleBin(string gameId, BombGame game, NodeLayout node, System.Drawing.Rectangle bounds, float sx, float sy, CancellationToken token)
    {
        var watch = new RecycleWatch();
        while (!token.IsCancellationRequested)
        {
            var bin = RecycleBinLocator.Find();   // null: not on the desktop or the shell did not answer — unknown, not "off"
            bool? over = bin is { } r
                ? bounds.Contains(r.X + r.Width / 2, r.Y + r.Height / 2)
                  && BombHit.CoversBomb(game, node, ((r.X - bounds.X) * sx, (r.Y - bounds.Y) * sy, r.Width * sx, r.Height * sy))
                : null;
            if (watch.Update(over))
            {
                Debug.WriteLine("[BiBaBo] Recycle Bin dropped on the bomb");
                _ = Report(gameId, "recycle", -1);
            }
            token.WaitHandle.WaitOne(250);
        }
    }

    private async Task Report(string gameId, string action, int cable)
    {
        try { await _report(gameId, action, cable); }
        catch (Exception ex) { Debug.WriteLine($"[BiBaBo] Report {action} failed: {ex.Message}"); }
    }

    /// <summary>Stop watching (the outcome arrived, Stop, or a new game).</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _mouse?.Stop();
        _mouse = null;
        _gameId = null;
    }

    /// <summary>True while watching the game <paramref name="gameId"/>.</summary>
    public bool IsWatching(string gameId) => _gameId == gameId;

    public void Dispose() => Stop();
}

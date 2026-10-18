using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WaBiBaBuSy.WallpaperEngine.Composition;

namespace WaBiBaBuSy.UI.Services;

/// <summary>
/// Standby switch: the players of the previous scene keep showing while the next scene's players load, and stop only
/// once those have appeared (a player keeps its window hidden until its shared start). No blank gap between items.
/// </summary>
internal sealed class RetiringPlayers
{
    /// <summary>After the new start: a few frames for the new player to present and show its window.</summary>
    private const int GraceMs = 250;
    /// <summary>Never keep an old player longer than this past now (a bogus start time must not leak a process).</summary>
    private const int MaxWaitMs = 15_000;

    private readonly ConcurrentDictionary<D2DCompositionService, byte> _pending = new();

    /// <summary>Stop and dispose <paramref name="players"/> once <paramref name="startUtcMs"/> (in <paramref name="nowUtcMs"/>'s clock) has passed.</summary>
    public void RetireAt(IEnumerable<D2DCompositionService> players, long startUtcMs, Func<long> nowUtcMs)
    {
        var list = players.ToList();
        if (list.Count == 0) return;
        foreach (var p in list) _pending[p] = 0;
        _ = RetireLaterAsync(list, startUtcMs, nowUtcMs);
    }

    /// <summary>Stop everything still waiting now (Stop, Clear all, a failed apply, shutdown).</summary>
    public Task StopAllNowAsync() => StopAsync(_pending.Keys.ToList());

    private async Task RetireLaterAsync(List<D2DCompositionService> players, long startUtcMs, Func<long> nowUtcMs)
    {
        long wait = startUtcMs + GraceMs - nowUtcMs();
        if (wait > 0) await Task.Delay((int)Math.Min(wait, MaxWaitMs));
        await StopAsync(players);
    }

    private async Task StopAsync(List<D2DCompositionService> players)
    {
        foreach (var p in players)
        {
            if (!_pending.TryRemove(p, out _)) continue;   // already stopped by the other path
            try { await p.StopAsync(); } catch (Exception ex) { Debug.WriteLine($"[Standby] Stop of a retiring player failed: {ex.Message}"); }
            try { p.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[Standby] Dispose of a retiring player failed: {ex.Message}"); }
        }
    }
}

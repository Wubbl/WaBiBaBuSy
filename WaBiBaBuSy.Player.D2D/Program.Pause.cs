// WaBiBaBuSy.Player.D2D/Program.Pause.cs
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Player.Common.Messages;

namespace WaBiBaBuSy.Player.D2D;

/// <summary>
/// Pause on fullscreen (<see cref="PlayerCommandPause"/>): while paused the window is hidden, the render loop only pumps
/// messages and sleeps (no draw, no Present) and the video sync timers hold their LibVLC players. Resuming needs no
/// resync: positions, show events and the videos' seek all follow the shared clock.
/// </summary>
partial class Program
{
    private const int SW_HIDE = 0;
    private const int PausedPollMs = 100;

    private static volatile bool _paused;                    // set by the stdin thread
    private static bool _pauseApplied;                       // render thread only: the window state for _paused
    private static readonly ManualResetEventSlim _resumeSignal = new(true);

    private static void HandlePauseCommand(PlayerCommandPause cmd)
    {
        _paused = cmd.Paused;
        if (cmd.Paused) _resumeSignal.Reset(); else _resumeSignal.Set();
        _logger?.LogInformation("[Pause] {State}", cmd.Paused ? "Paused (fullscreen app on this monitor)" : "Resumed");
    }

    /// <summary>
    /// Render thread, after the message pump: hides / re-shows the window on a change. True = paused: skip the frame
    /// (the caller waits on <see cref="WaitWhilePaused"/>).
    /// </summary>
    private static bool PauseGate()
    {
        bool paused = _paused;
        if (paused != _pauseApplied && _hwnd != IntPtr.Zero)
        {
            _pauseApplied = paused;
            if (paused) ShowWindow(_hwnd, SW_HIDE);
            else if (_windowShown)   // not shown yet = the first frame at the shared start shows it
            {
                if (_zOrderReference != IntPtr.Zero)
                    SetWindowPos(_hwnd, _zOrderReference, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                else
                    ShowWindow(_hwnd, 5);
            }
        }
        return paused;
    }

    /// <summary>Sleep until resumed (or the next poll, so window messages keep flowing).</summary>
    private static void WaitWhilePaused() => _resumeSignal.Wait(PausedPollMs);

    /// <summary>Video sync timers: hold the player while paused. True = paused (skip the sync step).</summary>
    private static bool HoldVideoWhilePaused(LibVLCSharp.Shared.MediaPlayer player)
    {
        if (!_paused) return false;
        if (player.IsPlaying) player.SetPause(true);
        return true;
    }
}

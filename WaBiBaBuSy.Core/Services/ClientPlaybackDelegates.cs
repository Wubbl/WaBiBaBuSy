using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Core.Services;

/// <summary>
/// Client mode: how this machine renders what the server sends — plain D2D, cross-screen D2D and its stop.
/// Registered once by the app and applied to every <see cref="WallpaperPlaybackService"/> a connect creates,
/// so tray connect, main-window connect and reconnects all render the same way, and a command the server
/// sends right after registering (session resume) already finds them.
/// </summary>
public sealed class ClientPlaybackDelegates
{
    /// <summary>Plain D2D wallpaper on one monitor: (file path, monitor index, background color, fit mode).</summary>
    public Func<string, int, string, int, Task>? D2DApply { get; set; }

    /// <summary>Synchronized cross-screen D2D animation (see <see cref="CrossScreenApplyRequest"/>).</summary>
    public Func<CrossScreenApplyRequest, Task>? CrossScreenApply { get; set; }

    /// <summary>Tear down the players started by <see cref="CrossScreenApply"/> / <see cref="D2DApply"/>.</summary>
    public Func<Task>? CrossScreenStop { get; set; }

    /// <summary>One-shot show event for the local players (see <see cref="WallpaperPlaybackService.ShowEventDelegate"/>).</summary>
    public Func<ShowEvent, Task>? ShowEvent { get; set; }

    /// <summary>Copy every delegate onto <paramref name="playback"/> (unset ones stay null there too).</summary>
    public void ApplyTo(WallpaperPlaybackService playback)
    {
        playback.D2DApplyDelegate = D2DApply;
        playback.D2DCrossScreenApplyDelegate = CrossScreenApply;
        playback.D2DCrossScreenStopDelegate = CrossScreenStop;
        playback.ShowEventDelegate = ShowEvent;
    }
}

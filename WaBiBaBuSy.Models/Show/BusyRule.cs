using System.Drawing;

namespace WaBiBaBuSy.Models.Show;

/// <summary>
/// BiBaBo's "busy seat" (design §8.1): a monitor counts as busy when the union of the windows over it covers at least
/// 80 % of its area — a fullscreen game or a maximised browser is busy, a chat window in a corner is not.
/// </summary>
public static class BusyRule
{
    public const double BusyCoverage = 0.8;
    private const int Cell = 32;   // sampled on a 32-px grid: union coverage without polygon clipping

    public static bool IsBusy(Rectangle monitor, IEnumerable<Rectangle> windows)
    {
        var over = windows.Select(w => Rectangle.Intersect(w, monitor)).Where(r => r.Width > 0 && r.Height > 0).ToList();
        if (over.Count == 0 || monitor.Width <= 0 || monitor.Height <= 0) return false;
        int total = 0, covered = 0;
        for (int y = monitor.Top + Cell / 2; y < monitor.Bottom; y += Cell)
            for (int x = monitor.Left + Cell / 2; x < monitor.Right; x += Cell)
            {
                total++;
                foreach (var r in over)
                    if (r.Contains(x, y)) { covered++; break; }
            }
        return covered >= BusyCoverage * total;
    }

    /// <summary>
    /// Pause on fullscreen: one window covers the whole monitor (exclusive or borderless fullscreen). Stricter than
    /// <see cref="IsBusy"/> — a maximised window leaves the taskbar free, two snapped halves are two windows.
    /// </summary>
    public static bool IsFullscreen(Rectangle monitor, IEnumerable<Rectangle> windows) =>
        monitor.Width > 0 && monitor.Height > 0 && windows.Any(w => w.Contains(monitor));

    /// <summary>
    /// Pause on fullscreen: is <paramref name="monitor"/> paused while a fullscreen app covers <paramref name="fullscreen"/>?
    /// <paramref name="onlyThatMonitor"/> = the setting "pause only the busy screen"; off = any one pauses the whole machine.
    /// </summary>
    public static bool IsPausedMonitor(int monitor, IReadOnlyCollection<int> fullscreen, bool onlyThatMonitor) =>
        fullscreen.Count > 0 && (!onlyThatMonitor || fullscreen.Contains(monitor));
}

using System;
using System.Diagnostics;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.UI.Services;

/// <summary>Force-kill <c>WaBiBaBuSy.Player.D2D</c> processes this app no longer tracks.</summary>
public static class PlayerProcesses
{
    private const string PlayerProcessName = "WaBiBaBuSy.Player.D2D";

    /// <summary>
    /// Kill player processes and return how many were killed. <paramref name="priorSessionOnly"/>: only those
    /// that started before this app instance (left behind by a crashed or killed session) — players this
    /// instance launched always start later, so they survive. Otherwise every player process (Clear all,
    /// after <see cref="ServerPlaybackHost"/> and <see cref="RemotePlaybackHost"/> stopped theirs).
    /// </summary>
    public static int Kill(bool priorSessionOnly)
    {
        int killed = 0;
        try
        {
            var appStart = Process.GetCurrentProcess().StartTime;
            foreach (var proc in Process.GetProcessesByName(PlayerProcessName))
            {
                try
                {
                    if (priorSessionOnly && proc.StartTime >= appStart) continue;
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                    killed++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[KillPlayers] Failed to kill PID {proc.Id}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KillPlayers] Enumeration failed: {ex.Message}");
        }
        // A killed player never runs its cleanup; repaint the user's wallpaper over its last frame.
        if (killed > 0)
            DesktopWindowManager.RefreshUserWallpaper();
        return killed;
    }
}

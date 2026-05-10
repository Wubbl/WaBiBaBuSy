using System;
using System.IO;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.WallpaperEngine.Services;

/// <summary>
/// Pre-initializes LibVLC at application startup to eliminate the 9-second initialization delay
/// when first wallpaper is applied
/// </summary>
public static class LibVLCPreloader
{
    private static bool _isInitialized = false;
    private static readonly object _lock = new object();

    /// <summary>
    /// Returns the directory containing libvlc.dll for the current platform.
    /// Checks the production layout (vlc-libs/ next to the exe) first, then the
    /// development layout (vlc-libs/ one level above the TFM output directory, which
    /// keeps it outside the Avalonia previewer's shadow-copy scope).
    /// </summary>
    public static string GetLibDirectory()
    {
        string platform = Environment.Is64BitProcess ? "win-x64" : "win-x86";
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "vlc-libs", platform),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "vlc-libs", platform)),
            Path.Combine(AppContext.BaseDirectory, "libvlc", platform),
        ];
        return Array.Find(candidates, Directory.Exists)
               ?? Path.Combine(AppContext.BaseDirectory, "libvlc", platform);
    }

    /// <summary>
    /// Pre-initialize LibVLC in the background. Safe to call multiple times.
    /// </summary>
    public static Task PreloadAsync(ILogger? logger = null)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_isInitialized)
                {
                    logger?.LogDebug("LibVLC already initialized, skipping preload");
                    return;
                }

                try
                {
                    logger?.LogInformation("Pre-initializing LibVLC in background...");
                    var startTime = DateTime.Now;

                    // This is the slow call (~9 seconds)
                    LibVLCSharp.Shared.Core.Initialize(GetLibDirectory());

                    var elapsed = DateTime.Now - startTime;
                    logger?.LogInformation("LibVLC pre-initialized successfully in {ElapsedMs}ms", elapsed.TotalMilliseconds);

                    _isInitialized = true;
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Failed to pre-initialize LibVLC");
                }
            }
        });
    }

    /// <summary>
    /// Check if LibVLC has been pre-initialized
    /// </summary>
    public static bool IsInitialized => _isInitialized;
}

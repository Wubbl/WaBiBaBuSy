using System;
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
                    LibVLCSharp.Shared.Core.Initialize();

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

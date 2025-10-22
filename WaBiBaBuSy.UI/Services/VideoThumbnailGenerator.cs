using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FFMpegCore;
using FFMpegCore.Enums;
using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.UI.Services;

/// <summary>
/// Generates thumbnails from video files using FFMpegCore (with bundled FFmpeg binaries)
/// </summary>
public class VideoThumbnailGenerator : IDisposable
{
    private readonly ILogger<VideoThumbnailGenerator> _logger;
    private readonly string _thumbnailCacheDir;

    public VideoThumbnailGenerator(ILogger<VideoThumbnailGenerator> logger)
    {
        _logger = logger;

        // Create thumbnail cache directory
        _thumbnailCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WaBiBaBuSy",
            "Thumbnails");

        Directory.CreateDirectory(_thumbnailCacheDir);
        _logger.LogInformation("Thumbnail cache directory: {CacheDir}", _thumbnailCacheDir);

        // FFMpegCore will automatically download FFmpeg binaries if not found
        _logger.LogInformation("FFMpegCore initialized - FFmpeg binaries will be auto-downloaded if needed");
    }

    /// <summary>
    /// Generate a thumbnail from a video file using FFMpegCore
    /// </summary>
    /// <param name="videoPath">Path to the video file</param>
    /// <param name="thumbnailWidth">Desired thumbnail width (maintains aspect ratio)</param>
    /// <returns>Path to the generated thumbnail, or null if failed</returns>
    public async Task<string?> GenerateThumbnail(string videoPath, int thumbnailWidth = 320)
    {
        if (!File.Exists(videoPath))
        {
            _logger.LogWarning("Video file not found: {VideoPath}", videoPath);
            return null;
        }

        try
        {
            // Generate thumbnail filename based on video file
            var videoFileName = Path.GetFileNameWithoutExtension(videoPath);
            // Sanitize filename to remove invalid characters
            var safeFileName = string.Join("_", videoFileName.Split(Path.GetInvalidFileNameChars()));
            var thumbnailFileName = $"{safeFileName}_{thumbnailWidth}.jpg";
            var thumbnailPath = Path.Combine(_thumbnailCacheDir, thumbnailFileName);

            // If thumbnail already exists, return it
            if (File.Exists(thumbnailPath))
            {
                _logger.LogDebug("Using cached thumbnail: {ThumbnailPath}", thumbnailPath);
                return thumbnailPath;
            }

            _logger.LogInformation("Generating thumbnail for: {VideoPath}", videoPath);

            // Get video info to calculate 10% position
            var mediaInfo = await FFProbe.AnalyseAsync(videoPath);
            var duration = mediaInfo.Duration;
            var seekTime = TimeSpan.FromSeconds(Math.Min(duration.TotalSeconds * 0.1, 5)); // 10% or max 5 seconds

            _logger.LogDebug("Video duration: {Duration}, seeking to: {SeekTime}", duration, seekTime);

            // Generate thumbnail using FFMpegCore
            var success = await FFMpeg.SnapshotAsync(
                videoPath,
                thumbnailPath,
                new System.Drawing.Size(thumbnailWidth, -1), // -1 maintains aspect ratio
                seekTime
            );

            if (!success || !File.Exists(thumbnailPath))
            {
                _logger.LogWarning("Failed to generate thumbnail for: {VideoPath}", videoPath);
                return null;
            }

            _logger.LogInformation("Thumbnail generated successfully: {ThumbnailPath}", thumbnailPath);
            return thumbnailPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating thumbnail for {VideoPath}", videoPath);
            return null;
        }
    }

    /// <summary>
    /// Clear all cached thumbnails
    /// </summary>
    public void ClearCache()
    {
        try
        {
            if (Directory.Exists(_thumbnailCacheDir))
            {
                Directory.Delete(_thumbnailCacheDir, true);
                Directory.CreateDirectory(_thumbnailCacheDir);
                _logger.LogInformation("Thumbnail cache cleared");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing thumbnail cache");
        }
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}

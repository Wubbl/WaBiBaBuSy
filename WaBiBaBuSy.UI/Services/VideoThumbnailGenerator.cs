using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

        // Configure FFMpegCore to look for binaries in application directory
        var appDir = AppContext.BaseDirectory;
        var ffmpegPath = Path.Combine(appDir, "ffmpeg.exe");
        var ffprobePath = Path.Combine(appDir, "ffprobe.exe");

        if (File.Exists(ffmpegPath) && File.Exists(ffprobePath))
        {
            GlobalFFOptions.Configure(options =>
            {
                options.BinaryFolder = appDir;
                options.TemporaryFilesFolder = Path.GetTempPath();
            });
            _logger.LogInformation("FFmpeg binaries found in application directory: {Path}", appDir);
        }
        else
        {
            _logger.LogWarning("FFmpeg binaries not found in application directory.");
            _logger.LogWarning("Please download FFmpeg from https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip");
            _logger.LogWarning("Extract and place ffmpeg.exe and ffprobe.exe in: {AppDir}", appDir);
            _logger.LogWarning("Video thumbnails will not be available until FFmpeg is installed.");
        }
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
            // Generate unique cache key based on full path and last modified time
            var fileInfo = new FileInfo(videoPath);
            var cacheKey = $"{videoPath}|{fileInfo.LastWriteTimeUtc.Ticks}|{thumbnailWidth}";
            var hash = ComputeHash(cacheKey);
            var thumbnailFileName = $"{hash}.jpg";
            var thumbnailPath = Path.Combine(_thumbnailCacheDir, thumbnailFileName);

            // If thumbnail already exists, return it
            if (File.Exists(thumbnailPath))
            {
                _logger.LogDebug("Using cached thumbnail: {ThumbnailPath} for {VideoPath}", thumbnailPath, videoPath);
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
    /// Get the resolution (WxH) of a video file using FFProbe
    /// </summary>
    public async Task<string?> GetVideoResolution(string videoPath)
    {
        if (!File.Exists(videoPath))
            return null;

        try
        {
            var mediaInfo = await FFProbe.AnalyseAsync(videoPath);
            var stream = mediaInfo.VideoStreams.FirstOrDefault();
            if (stream != null)
                return $"{stream.Width}x{stream.Height}";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read resolution for {VideoPath}", videoPath);
        }

        return null;
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

    /// <summary>
    /// Compute SHA256 hash of a string to use as cache key
    /// </summary>
    private static string ComputeHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}

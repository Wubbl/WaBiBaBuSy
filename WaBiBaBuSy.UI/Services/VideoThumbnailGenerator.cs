using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.UI.Services;

/// <summary>
/// Generates thumbnails from video files using LibVLC
/// </summary>
public class VideoThumbnailGenerator : IDisposable
{
    private readonly ILogger<VideoThumbnailGenerator> _logger;
    private readonly LibVLC? _libVLC;
    private readonly string _thumbnailCacheDir;

    public VideoThumbnailGenerator(ILogger<VideoThumbnailGenerator> logger)
    {
        _logger = logger;

        // Initialize LibVLC (Core.Initialize() is safe to call multiple times)
        try
        {
            _logger.LogInformation("Checking LibVLC Core initialization status for thumbnails...");

            // Core.Initialize() can be called multiple times safely - it's idempotent
            // The error occurs when multiple instances try to load at the exact same time
            // So we just create the LibVLC instance directly
            try
            {
                _libVLC = new LibVLC(enableDebugLogs: false, "--no-audio", "--quiet");
                _logger.LogInformation("LibVLC instance created successfully for thumbnail generation");
            }
            catch (Exception innerEx)
            {
                // If that fails, try initializing Core first
                _logger.LogDebug("First attempt failed, trying with Core.Initialize(). Error: {Error}", innerEx.Message);
                LibVLCSharp.Shared.Core.Initialize();
                _libVLC = new LibVLC(enableDebugLogs: false, "--no-audio", "--quiet");
                _logger.LogInformation("LibVLC initialized successfully after Core.Initialize()");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize LibVLC for thumbnails. Error: {Message}", ex.Message);
            _logger.LogError("Stack trace: {StackTrace}", ex.StackTrace);
        }

        // Create thumbnail cache directory
        _thumbnailCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WaBiBaBuSy",
            "Thumbnails");

        try
        {
            Directory.CreateDirectory(_thumbnailCacheDir);
            _logger.LogInformation("Thumbnail cache directory: {CacheDir}", _thumbnailCacheDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create thumbnail cache directory");
        }
    }

    /// <summary>
    /// Generate a thumbnail from a video file
    /// </summary>
    /// <param name="videoPath">Path to the video file</param>
    /// <param name="thumbnailWidth">Desired thumbnail width (maintains aspect ratio)</param>
    /// <returns>Path to the generated thumbnail, or null if failed</returns>
    public async Task<string?> GenerateThumbnail(string videoPath, int thumbnailWidth = 320)
    {
        if (_libVLC == null)
        {
            _logger.LogWarning("LibVLC not initialized, cannot generate thumbnail");
            return null;
        }

        if (!File.Exists(videoPath))
        {
            _logger.LogWarning("Video file not found: {VideoPath}", videoPath);
            return null;
        }

        try
        {
            // Generate thumbnail filename based on video file hash
            var videoFileName = Path.GetFileNameWithoutExtension(videoPath);
            // Sanitize filename to remove invalid characters
            var safeFileName = string.Join("_", videoFileName.Split(Path.GetInvalidFileNameChars()));
            var thumbnailFileName = $"{safeFileName}_{thumbnailWidth}.jpg";
            var thumbnailPath = Path.Combine(_thumbnailCacheDir, thumbnailFileName);

            _logger.LogInformation("Thumbnail will be saved to: {ThumbnailPath}", thumbnailPath);

            // If thumbnail already exists, return it
            if (File.Exists(thumbnailPath))
            {
                _logger.LogDebug("Using cached thumbnail: {ThumbnailPath}", thumbnailPath);
                return thumbnailPath;
            }

            _logger.LogInformation("Generating thumbnail for: {VideoPath}", videoPath);

            // Create media from video file
            _logger.LogInformation("Step 1: Creating media object for video");
            using var media = new Media(_libVLC, videoPath, FromType.FromPath);

            // Parse media to get duration
            _logger.LogInformation("Step 2: Parsing media to get duration (timeout 5s)");
            var parseResult = await media.Parse(MediaParseOptions.ParseNetwork, 5000);
            if (parseResult != MediaParsedStatus.Done)
            {
                _logger.LogWarning("Failed to parse media: {VideoPath}, Status: {Status}", videoPath, parseResult);
                return null;
            }

            var duration = media.Duration; // in milliseconds
            _logger.LogInformation("Step 3: Media duration: {Duration}ms", duration);
            if (duration <= 0)
            {
                _logger.LogWarning("Invalid media duration: {VideoPath}", videoPath);
                return null;
            }

            // Seek to 10% of video duration for better thumbnail (skip intros)
            var seekPosition = Math.Min(duration * 0.1, 5000); // Max 5 seconds in
            _logger.LogInformation("Step 4: Will seek to position: {Position}ms", seekPosition);

            // Use LibVLC's thumbnail generation feature (vout=dummy for headless)
            _logger.LogInformation("Step 5: Using FFmpeg to extract thumbnail frame");

            // Use FFmpeg-based approach via LibVLC's image module
            // This is more reliable than trying to use TakeSnapshot on background threads
            using var thumbnailMedia = new Media(_libVLC, videoPath, FromType.FromPath,
                $"start-time={(int)(seekPosition / 1000)}",
                "stop-time=" + (int)(seekPosition / 1000 + 1),
                "no-audio");

            using var mediaPlayer = new MediaPlayer(_libVLC);
            mediaPlayer.Media = thumbnailMedia;

            // Set up event to capture when first frame is available
            var frameAvailable = false;
            var snapshotReady = new System.Threading.ManualResetEventSlim(false);

            mediaPlayer.Vout += (sender, args) =>
            {
                if (args.Count > 0 && !frameAvailable)
                {
                    frameAvailable = true;
                    _logger.LogInformation("Step 6: Video output available, taking snapshot");

                    // Wait a moment for frame to render
                    System.Threading.Thread.Sleep(200);

                    // Take snapshot
                    if (mediaPlayer.TakeSnapshot(0, thumbnailPath, 0, 0))
                    {
                        _logger.LogInformation("Step 7: Snapshot saved successfully");
                        snapshotReady.Set();
                    }
                }
            };

            // Start playback (headless)
            _logger.LogInformation("Step 8: Starting headless playback");
            mediaPlayer.Play();

            // Wait for snapshot to be taken (max 5 seconds)
            _logger.LogInformation("Step 9: Waiting for snapshot (timeout 5s)");
            if (!snapshotReady.Wait(5000))
            {
                _logger.LogWarning("Snapshot timeout - trying alternative approach");
                mediaPlayer.Stop();

                // Fallback: Just seek and snapshot without waiting for Vout event
                mediaPlayer.Time = (long)seekPosition;
                System.Threading.Thread.Sleep(500);
                mediaPlayer.TakeSnapshot(0, thumbnailPath, 0, 0);
                System.Threading.Thread.Sleep(500);
            }

            mediaPlayer.Stop();

            // Wait for file to be written to disk
            _logger.LogInformation("Step 10: Waiting for snapshot file to be written to disk");
            var fileCheckTimeout = DateTime.Now.AddSeconds(3);
            while (!File.Exists(thumbnailPath) && DateTime.Now < fileCheckTimeout)
            {
                System.Threading.Thread.Sleep(100);
            }

            if (!File.Exists(thumbnailPath))
            {
                _logger.LogWarning("Failed to create snapshot file after 3 seconds. Expected path: {ThumbnailPath}", thumbnailPath);

                // Check if LibVLC created it in a different location
                var possiblePaths = new[]
                {
                    Path.Combine(Path.GetDirectoryName(videoPath) ?? "", Path.GetFileName(thumbnailPath)),
                    Path.Combine(Environment.CurrentDirectory, Path.GetFileName(thumbnailPath)),
                    thumbnailPath
                };

                foreach (var path in possiblePaths)
                {
                    _logger.LogInformation("Checking path: {Path}", path);
                    if (File.Exists(path))
                    {
                        _logger.LogInformation("Found snapshot at: {Path}", path);
                        // Move it to the correct location
                        File.Move(path, thumbnailPath, true);
                        break;
                    }
                }

                if (!File.Exists(thumbnailPath))
                {
                    _logger.LogWarning("Snapshot file not found in any expected location");
                    return null;
                }
            }

            _logger.LogInformation("Step 11: Snapshot file confirmed at: {ThumbnailPath}", thumbnailPath);

            // Resize the snapshot to thumbnail size
            try
            {
                _logger.LogInformation("Step 12: Resizing snapshot to {Width}px width", thumbnailWidth);

                // Wait a bit to ensure file is fully written and not locked
                System.Threading.Thread.Sleep(200);

                using var originalImage = System.Drawing.Image.FromFile(thumbnailPath);
                var aspectRatio = (double)originalImage.Height / originalImage.Width;
                var thumbnailHeight = (int)(thumbnailWidth * aspectRatio);

                using var thumbnail = new Bitmap(thumbnailWidth, thumbnailHeight);
                using var graphics = Graphics.FromImage(thumbnail);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(originalImage, 0, 0, thumbnailWidth, thumbnailHeight);

                // Save resized thumbnail (overwrite original)
                thumbnail.Save(thumbnailPath, ImageFormat.Jpeg);

                _logger.LogInformation("Thumbnail generated successfully: {ThumbnailPath}", thumbnailPath);
                return thumbnailPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resizing thumbnail");
                return thumbnailPath; // Return original snapshot if resize fails
            }
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
        _libVLC?.Dispose();
    }
}

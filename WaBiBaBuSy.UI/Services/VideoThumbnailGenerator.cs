using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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

        // Initialize LibVLC
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC();
            _logger.LogInformation("LibVLC initialized for thumbnail generation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize LibVLC for thumbnails");
        }

        // Create thumbnail cache directory
        _thumbnailCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WaBiBaBuSy",
            "Thumbnails");

        Directory.CreateDirectory(_thumbnailCacheDir);
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
            var thumbnailFileName = $"{videoFileName}_{thumbnailWidth}.jpg";
            var thumbnailPath = Path.Combine(_thumbnailCacheDir, thumbnailFileName);

            // If thumbnail already exists, return it
            if (File.Exists(thumbnailPath))
            {
                _logger.LogDebug("Using cached thumbnail: {ThumbnailPath}", thumbnailPath);
                return thumbnailPath;
            }

            _logger.LogInformation("Generating thumbnail for: {VideoPath}", videoPath);

            // Create media from video file
            using var media = new Media(_libVLC, videoPath, FromType.FromPath);

            // Parse media to get duration
            var parseResult = await media.Parse(MediaParseOptions.ParseNetwork, 5000);
            if (parseResult != MediaParsedStatus.Done)
            {
                _logger.LogWarning("Failed to parse media: {VideoPath}", videoPath);
                return null;
            }

            var duration = media.Duration; // in milliseconds
            if (duration <= 0)
            {
                _logger.LogWarning("Invalid media duration: {VideoPath}", videoPath);
                return null;
            }

            // Seek to 10% of video duration for better thumbnail (skip intros)
            var seekPosition = Math.Min(duration * 0.1, 5000); // Max 5 seconds in

            // Create a media player for snapshot
            using var mediaPlayer = new MediaPlayer(_libVLC);
            mediaPlayer.Media = media;

            // Use a temporary window for rendering (required for snapshots)
            using var tempForm = new System.Windows.Forms.Form
            {
                Width = 1920,
                Height = 1080,
                ShowInTaskbar = false,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None
            };

            // Set video output
            mediaPlayer.Hwnd = tempForm.Handle;

            // Start playback
            mediaPlayer.Play();

            // Wait for video to start
            var timeout = DateTime.Now.AddSeconds(10);
            while (!mediaPlayer.IsPlaying && DateTime.Now < timeout)
            {
                System.Threading.Thread.Sleep(50);
            }

            if (!mediaPlayer.IsPlaying)
            {
                _logger.LogWarning("Video failed to start playing: {VideoPath}", videoPath);
                return null;
            }

            // Seek to desired position
            mediaPlayer.Time = (long)seekPosition;
            System.Threading.Thread.Sleep(500); // Wait for seek to complete

            // Take snapshot
            var snapshotTaken = false;
            var snapshotTimeout = DateTime.Now.AddSeconds(5);

            while (!snapshotTaken && DateTime.Now < snapshotTimeout)
            {
                try
                {
                    // Take snapshot at original video resolution
                    if (mediaPlayer.TakeSnapshot(0, thumbnailPath, 0, 0))
                    {
                        snapshotTaken = true;
                        _logger.LogDebug("Snapshot saved: {ThumbnailPath}", thumbnailPath);
                    }
                    else
                    {
                        System.Threading.Thread.Sleep(100);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error taking snapshot");
                    break;
                }
            }

            mediaPlayer.Stop();

            if (!snapshotTaken || !File.Exists(thumbnailPath))
            {
                _logger.LogWarning("Failed to create snapshot: {VideoPath}", videoPath);
                return null;
            }

            // Resize the snapshot to thumbnail size
            try
            {
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

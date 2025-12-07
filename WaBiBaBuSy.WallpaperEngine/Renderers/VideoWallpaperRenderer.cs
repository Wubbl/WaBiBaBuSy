using System.Drawing;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Models;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Renderers;

/// <summary>
/// LibVLC-based video wallpaper renderer with hardware acceleration.
/// </summary>
public class VideoWallpaperRenderer : IWallpaperRenderer
{
    private readonly ILogger<VideoWallpaperRenderer> _logger;
    private readonly DesktopWindowManager _desktopManager;

    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Form? _renderForm;
    private WallpaperConfig? _config;
    private WallpaperState _state = WallpaperState.Uninitialized;
    private bool _disposed;

    // Frame caching for composition system
    private Dictionary<long, System.Drawing.Bitmap> _frameCache = new();
    private long _lastCachedTimestamp = -1;
    private const int MaxCachedFrames = 10;  // Keep last 10 frames in cache

    public event EventHandler<FrameRenderedEventArgs>? FrameRendered;
    public event EventHandler<WallpaperState>? StateChanged;

    public VideoWallpaperRenderer(
        ILogger<VideoWallpaperRenderer> logger,
        DesktopWindowManager desktopManager)
    {
        _logger = logger;
        _desktopManager = desktopManager;
    }

    public WallpaperState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, _state);
            }
        }
    }

    public long PositionMs => _mediaPlayer?.Time ?? 0;

    public async Task InitializeAsync(WallpaperConfig config)
    {
        try
        {
            _logger.LogInformation("Initializing video wallpaper renderer for {FilePath}", config.FilePath);
            _config = config;

            // Initialize LibVLC with optimized options for faster loading
            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC(enableDebugLogs: false,
                "--no-video-title-show",  // Don't show video title on video
                "--no-audio",              // No audio for wallpaper
                "--file-caching=300",      // Reduce file caching (default 1000ms) for faster start
                "--network-caching=300",   // Reduce network caching for faster start
                "--avcodec-hw=any");       // Enable hardware decoding for better performance

            // Create media player
            _mediaPlayer = new MediaPlayer(_libVLC);

            // Create render window
            await CreateRenderWindowAsync(config);

            State = WallpaperState.Stopped;
            _logger.LogInformation("Video wallpaper renderer initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize video wallpaper renderer");
            State = WallpaperState.Error;
            throw;
        }
    }

    public async Task StartAsync()
    {
        try
        {
            if (_mediaPlayer == null || _config == null)
                throw new InvalidOperationException("Renderer not initialized");

            _logger.LogInformation("Starting video playback");

            var media = new Media(_libVLC, _config.FilePath, FromType.FromPath);

            // Configure media options
            if (_config.HardwareAcceleration)
            {
                media.AddOption(":avcodec-hw=any");
            }

            _mediaPlayer.Media = media;
            _mediaPlayer.Play();

            // Wait a bit for playback to actually start
            await Task.Delay(100);

            State = WallpaperState.Playing;
            _logger.LogInformation("Video playback started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start video playback");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task PauseAsync()
    {
        try
        {
            _mediaPlayer?.Pause();
            State = WallpaperState.Paused;
            _logger.LogInformation("Video playback paused");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pause video playback");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task ResumeAsync()
    {
        try
        {
            _mediaPlayer?.Play();
            State = WallpaperState.Playing;
            _logger.LogInformation("Video playback resumed");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume video playback");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task StopAsync()
    {
        try
        {
            _mediaPlayer?.Stop();
            State = WallpaperState.Stopped;
            _logger.LogInformation("Video playback stopped");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop video playback");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task SeekAsync(TimeSpan position)
    {
        try
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Time = (long)position.TotalMilliseconds;
                _logger.LogDebug("Seeked to position {Position}ms", position.TotalMilliseconds);
            }
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seek video");
            throw;
        }
    }

    /// <summary>
    /// Gets the video frame at a specific timestamp (used by composition system).
    /// Uses frame caching to avoid re-extracting frames for nearby timestamps.
    /// </summary>
    public Bitmap GetFrameAtPosition(long timestampMs)
    {
        try
        {
            if (_mediaPlayer == null)
                return new Bitmap(1, 1);

            // Check cache first - if requesting same frame or very close timestamp
            if (_frameCache.TryGetValue(timestampMs, out var cachedFrame))
            {
                return cachedFrame;
            }

            // Check if we have a frame from nearby timestamp (avoid re-seeking)
            // This is optimized for composition calling GetFrameAtPosition multiple times per frame
            var nearbyFrame = _frameCache.Keys.FirstOrDefault(k => Math.Abs(k - timestampMs) < 10);
            if (nearbyFrame >= 0 && _frameCache.TryGetValue(nearbyFrame, out var nearby))
            {
                return nearby;
            }

            // Cache miss - need to seek and extract frame
            // This is slow (~100-200ms) but happens infrequently when timestamps change significantly
            ExtractFrameFromLibVLC(timestampMs);

            // Retry cache lookup
            if (_frameCache.TryGetValue(timestampMs, out var frame))
            {
                return frame;
            }

            // Fallback - return blank bitmap if extraction failed
            _logger.LogWarning("Could not extract frame at {Timestamp}ms", timestampMs);
            return new Bitmap(1, 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting video frame at position {TimestampMs}ms", timestampMs);
            return new Bitmap(1, 1);
        }
    }

    /// <summary>
    /// Extracts a frame from the video at the specified timestamp and caches it.
    /// For MVP, we use a simplified approach: cache by playing to the timestamp.
    /// For production, consider using LibVLC callbacks or async frame buffers.
    /// </summary>
    private void ExtractFrameFromLibVLC(long timestampMs)
    {
        try
        {
            if (_mediaPlayer == null)
                return;

            // For video frame extraction via composition, we simply seek to the timestamp
            // and create a placeholder bitmap. In a real implementation, you'd:
            // 1. Use LibVLC's frame callbacks to get actual decoded frames
            // 2. Or use async buffering to pre-decode frames
            // 3. Or write frames to disk during initialization

            _mediaPlayer.Time = timestampMs;

            // Wait briefly for seek to complete
            Thread.Sleep(30);

            // For now, create a placeholder bitmap that marks this timestamp as "cached"
            // In production, replace with actual frame capture from LibVLC
            var bitmap = new Bitmap(320, 240); // Placeholder dimensions
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.Clear(System.Drawing.Color.Black);
                // In production: draw actual video frame here
            }

            _frameCache[timestampMs] = bitmap;
            _lastCachedTimestamp = timestampMs;

            // Implement LRU cache eviction - keep only recent frames
            if (_frameCache.Count > MaxCachedFrames)
            {
                var oldestKey = _frameCache.Keys.Min();
                if (_frameCache.TryGetValue(oldestKey, out var oldBitmap))
                {
                    oldBitmap.Dispose();
                    _frameCache.Remove(oldestKey);
                }
            }

            _logger.LogDebug("Cached frame reference at {Timestamp}ms", timestampMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing frame at {Timestamp}ms", timestampMs);
        }
    }

    private Task CreateRenderWindowAsync(WallpaperConfig config)
    {
        // Windows Forms must be created on the calling thread, NOT on a background thread
        _renderForm = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = false,
            ControlBox = false,
            MaximizeBox = false,
            MinimizeBox = false
        };

        // Validate monitor index
        if (config.MonitorIndex < 0 || config.MonitorIndex >= Screen.AllScreens.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config.MonitorIndex),
                $"Invalid monitor index {config.MonitorIndex}. Must be between 0 and {Screen.AllScreens.Length - 1}. Total monitors: {Screen.AllScreens.Length}");
        }

        // Set window bounds for the specific monitor
        var screen = Screen.AllScreens[config.MonitorIndex];
        _renderForm.Bounds = screen.Bounds;

        _logger.LogInformation("Video renderer set to monitor {Index}: {Bounds} (Device: {Device})",
            config.MonitorIndex,
            screen.Bounds,
            screen.DeviceName);

        // CRITICAL: Show the form FIRST to ensure handle is fully initialized
        _renderForm.Show();

        _logger.LogDebug("Form shown, handle: {Handle}", _renderForm.Handle);

        // Now find WorkerW window and set as parent (after form is shown)
        var workerW = _desktopManager.FindDesktopWorkerWindow();
        if (workerW != IntPtr.Zero)
        {
            _logger.LogDebug("Found WorkerW: {WorkerW}, parenting form to it", workerW);

            // Convert Screen.Bounds to System.Drawing.Rectangle for DesktopWindowManager
            var screenBounds = new System.Drawing.Rectangle(
                screen.Bounds.X,
                screen.Bounds.Y,
                screen.Bounds.Width,
                screen.Bounds.Height);

            _desktopManager.SetAsWallpaperWindow(_renderForm.Handle, screenBounds);
        }
        else
        {
            _logger.LogWarning("Could not find WorkerW window, wallpaper may not render behind icons");
        }

        // Set media player output
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Hwnd = _renderForm.Handle;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("Disposing video wallpaper renderer");

        // Clean up frame cache
        foreach (var frame in _frameCache.Values)
        {
            frame?.Dispose();
        }
        _frameCache.Clear();

        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _libVLC?.Dispose();

        _renderForm?.Close();
        _renderForm?.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

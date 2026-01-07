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

            // Enable looping for GIFs and videos
            if (_config.Loop)
            {
                // Set media to loop - use MediaPlayer's repeat feature
                _mediaPlayer.Media.AddOption("input-repeat=-1");  // -1 = infinite loop
                _logger.LogInformation("Looping enabled for media playback");
            }

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
    /// Gets the current frame from the playing GIF/video (used by composition system).
    /// For GIFs, we don't seek - we just capture whatever frame LibVLC is currently displaying.
    /// LibVLC handles the frame timing and looping automatically.
    /// </summary>
    public Bitmap GetFrameAtPosition(long timestampMs)
    {
        try
        {
            if (_mediaPlayer == null)
                return new Bitmap(1, 1);

            // For GIF/video playback, we capture the CURRENT frame being displayed by LibVLC
            // Not the frame at the requested timestamp - LibVLC is playing in real-time
            // This avoids slow seeks and lets LibVLC handle frame timing naturally

            // Check if we have a recent frame (within last 100ms)
            // This avoids hammering TakeSnapshot() on every frame of composition
            var recentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastCachedTimestamp > 0 && (recentTimestamp - _lastCachedTimestamp) < 100)
            {
                // Return the last captured frame (it's recent enough)
                var lastFrame = _frameCache.Values.LastOrDefault();
                if (lastFrame != null)
                {
                    return new Bitmap(lastFrame);  // Clone to avoid concurrent access issues
                }
            }

            // Time to capture a new frame - do it async to avoid blocking
            // For now, return last frame if available, and trigger background capture
            var existingFrame = _frameCache.Values.LastOrDefault();
            if (existingFrame != null)
            {
                // Trigger async capture for next time (don't block)
                _ = Task.Run(() => CaptureCurrentFrame());
                return new Bitmap(existingFrame);
            }

            // No frames yet - do synchronous capture for first frame
            CaptureCurrentFrame();
            var frame = _frameCache.Values.LastOrDefault();
            if (frame != null)
            {
                return new Bitmap(frame);
            }

            // Fallback - return small black bitmap
            _logger.LogWarning("Could not capture current frame");
            return new Bitmap(320, 240);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current video frame");
            return new Bitmap(1, 1);
        }
    }

    /// <summary>
    /// Captures the current frame from LibVLC (whatever is being displayed right now).
    /// Does NOT seek - just takes a snapshot of the current playback position.
    /// </summary>
    private void CaptureCurrentFrame()
    {
        try
        {
            if (_mediaPlayer == null || _config == null)
                return;

            // Take snapshot of CURRENT frame (no seeking)
            var tempSnapshotPath = Path.Combine(Path.GetTempPath(), $"vlc_current_{Guid.NewGuid()}.png");

            try
            {
                bool success = _mediaPlayer.TakeSnapshot(0, tempSnapshotPath, 0, 0);

                if (success)
                {
                    // Wait briefly for file to be written
                    Thread.Sleep(30);

                    if (File.Exists(tempSnapshotPath))
                    {
                        using (var tempImage = Image.FromFile(tempSnapshotPath))
                        {
                            var bitmap = new Bitmap(tempImage);
                            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                            // Store with current timestamp
                            _frameCache[timestamp] = bitmap;
                            _lastCachedTimestamp = timestamp;

                            _logger.LogDebug("Captured current frame at playback position {Position}ms", _mediaPlayer.Time);
                        }

                        try { File.Delete(tempSnapshotPath); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to capture current frame");
            }

            // Keep only most recent frame (we don't need history for real-time playback)
            if (_frameCache.Count > 2)
            {
                var oldestKey = _frameCache.Keys.OrderBy(k => k).First();
                if (_frameCache.TryGetValue(oldestKey, out var oldBitmap))
                {
                    oldBitmap?.Dispose();
                    _frameCache.Remove(oldestKey);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error capturing current frame");
        }
    }

    private Task CreateRenderWindowAsync(WallpaperConfig config)
    {
        // Even in headless mode, LibVLC needs a window handle to decode and provide frames
        // Create a minimal hidden window for LibVLC's internal rendering
        // Frames will be extracted via TakeSnapshot() in GetFrameAtPosition()

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

        if (config.HeadlessMode)
        {
            // Headless mode: Create a minimal hidden window just for LibVLC to have an output target
            // This allows TakeSnapshot() to work for frame extraction
            _renderForm.Size = new Size(320, 240);  // Small size to minimize overhead
            _renderForm.Location = new Point(-10000, -10000);  // Off-screen
            _renderForm.Opacity = 0;  // Fully transparent (hidden)
            _renderForm.Show();
            _renderForm.Hide();  // Extra insurance to keep it hidden

            _logger.LogInformation("Headless mode: Created hidden off-screen window for LibVLC frame extraction");
        }
        else
        {
            // Normal mode: Full-screen window on specified monitor for visible wallpaper
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

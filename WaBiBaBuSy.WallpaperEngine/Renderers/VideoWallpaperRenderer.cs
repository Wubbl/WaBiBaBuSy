using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
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

    // LibVLC Memory Callbacks (for composition system)
    // Instead of TakeSnapshot() which uses disk I/O, we use LibVLC's direct memory access
    private IntPtr _frameBufferPtr;
    private int _videoWidth;
    private int _videoHeight;
    private Bitmap? _currentFrameBitmap;
    private readonly object _frameLock = new object();
    private bool _useMemoryCallbacks = false;  // Enabled for headless mode

    // Legacy frame cache (not used with memory callbacks)
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
    /// Uses LibVLC memory callbacks for zero-copy frame access (no disk I/O).
    /// </summary>
    public Bitmap GetFrameAtPosition(long timestampMs)
    {
        try
        {
            if (_mediaPlayer == null)
                return new Bitmap(1, 1);

            // If using memory callbacks (headless mode), return the callback-provided frame
            if (_useMemoryCallbacks)
            {
                lock (_frameLock)
                {
                    if (_currentFrameBitmap != null)
                    {
                        // Clone to avoid threading issues with composition system
                        return new Bitmap(_currentFrameBitmap);
                    }
                }

                // No frame decoded yet - return placeholder
                _logger.LogTrace("No frame available yet from memory callbacks");
                return new Bitmap(320, 240);
            }

            // Legacy path: TakeSnapshot approach for non-headless mode
            // (Not used in composition system, kept for backwards compatibility)
            var recentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastCachedTimestamp > 0 && (recentTimestamp - _lastCachedTimestamp) < 100)
            {
                var lastFrame = _frameCache.Values.LastOrDefault();
                if (lastFrame != null)
                {
                    return new Bitmap(lastFrame);
                }
            }

            var existingFrame = _frameCache.Values.LastOrDefault();
            if (existingFrame != null)
            {
                _ = Task.Run(() => CaptureCurrentFrame());
                return new Bitmap(existingFrame);
            }

            CaptureCurrentFrame();
            var frame = _frameCache.Values.LastOrDefault();
            if (frame != null)
            {
                return new Bitmap(frame);
            }

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
        if (config.HeadlessMode)
        {
            // Headless mode: Use LibVLC memory callbacks for direct frame access
            // No window needed - callbacks provide frames in memory
            _useMemoryCallbacks = true;
            SetupVideoCallbacks();
            _logger.LogInformation("Headless mode: Using LibVLC memory callbacks for composition system (zero disk I/O)");
            return Task.CompletedTask;
        }

        // Normal mode: Create visible window for wallpaper display
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

    #region LibVLC Memory Callbacks

    /// <summary>
    /// Setup LibVLC memory callbacks for direct frame access (headless mode).
    /// Uses LibVLCSharp 3.x simplified callback API.
    /// Based on: https://github.com/mfkl/libvlcsharp-samples/blob/master/PreviewThumbnailExtractor/Program.cs
    /// </summary>
    private void SetupVideoCallbacks()
    {
        if (_mediaPlayer == null)
        {
            _logger.LogWarning("Cannot setup video callbacks - media player is null");
            return;
        }

        try
        {
            // Set fixed video format (will be updated when media starts playing)
            // Using 1920x1080 as initial size - LibVLC will call back with actual dimensions
            _videoWidth = 1920;
            _videoHeight = 1080;
            uint pitch = (uint)(_videoWidth * 4);  // 4 bytes per pixel for RGBA

            // Allocate frame buffer
            int bufferSize = _videoWidth * _videoHeight * 4;
            _frameBufferPtr = Marshal.AllocHGlobal(bufferSize);

            // LibVLCSharp 3.x simplified API: SetVideoFormat + SetVideoCallbacks
            // RV32 = RGBA 32-bit format
            _mediaPlayer.SetVideoFormat("RV32", (uint)_videoWidth, (uint)_videoHeight, pitch);
            _mediaPlayer.SetVideoCallbacks(VideoLockCallback, null, VideoDisplayCallback);

            _logger.LogInformation("LibVLC memory callbacks configured: RV32 {Width}x{Height}, buffer={Bytes} bytes",
                _videoWidth, _videoHeight, bufferSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup video callbacks");
        }
    }

    /// <summary>
    /// Lock callback - called by LibVLC before decoding a frame.
    /// Returns pointer to buffer where LibVLC will write decoded frame data.
    /// Signature: IntPtr Lock(IntPtr opaque, IntPtr planes)
    /// </summary>
    private IntPtr VideoLockCallback(IntPtr opaque, IntPtr planes)
    {
        try
        {
            // Provide buffer pointer to LibVLC via planes parameter
            Marshal.WriteIntPtr(planes, _frameBufferPtr);
            return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in VideoLockCallback");
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Display callback - called by LibVLC when frame is ready to display.
    /// This is where we convert the raw frame buffer to a Bitmap for composition.
    /// Signature: void Display(IntPtr opaque, IntPtr picture)
    /// </summary>
    private void VideoDisplayCallback(IntPtr opaque, IntPtr picture)
    {
        try
        {
            if (_frameBufferPtr == IntPtr.Zero || _videoWidth == 0 || _videoHeight == 0)
                return;

            lock (_frameLock)
            {
                // Dispose old frame
                _currentFrameBitmap?.Dispose();

                // Convert raw RGBA buffer to Bitmap
                _currentFrameBitmap = ConvertRawFrameToBitmap(_frameBufferPtr, _videoWidth, _videoHeight);

                _logger.LogTrace("Frame decoded: {Width}x{Height} via memory callback", _videoWidth, _videoHeight);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in VideoDisplayCallback");
        }
    }

    /// <summary>
    /// Convert raw RGBA frame buffer to System.Drawing.Bitmap.
    /// Uses unsafe pointer for fast memory copy.
    /// </summary>
    private Bitmap ConvertRawFrameToBitmap(IntPtr bufferPtr, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            // Copy raw frame data to bitmap (direct memory copy - fast!)
            int byteCount = width * height * 4;
            unsafe
            {
                Buffer.MemoryCopy(
                    (void*)bufferPtr,
                    (void*)bitmapData.Scan0,
                    byteCount,
                    byteCount
                );
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        return bitmap;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("Disposing video wallpaper renderer");

        // Clean up memory callback resources
        lock (_frameLock)
        {
            _currentFrameBitmap?.Dispose();
            _currentFrameBitmap = null;
        }

        if (_frameBufferPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_frameBufferPtr);
            _frameBufferPtr = IntPtr.Zero;
        }

        // Clean up legacy frame cache
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

using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
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

    // Diagnostic counters for TASK-008 investigation
    private long _lockCallbackCount = 0;
    private long _displayCallbackCount = 0;
    private DateTime _callbackTrackingStart = DateTime.MinValue;
    private DateTime _lastCallbackLogTime = DateTime.MinValue;

    // TASK-011: GIF-specific logging fields
    private long _animationLengthMs = 0;          // Total animation duration
    private float _lastLoggedPosition = -1f;      // Last position for loop detection
    private int _loopCounter = 0;                 // Number of loops completed
    private float _estimatedFPS = 30f;            // Estimated frame rate
    private int _totalFrameCount = 0;             // Estimated total frames in animation
    private long _getFrameCallCount = 0;          // Track how many times GetFrameAtPosition is called
    private long _lastDisplayCallbackValue = 0;   // Track if _displayCallbackCount is changing
    private int _lastBitmapHashCode = 0;          // Track if bitmap instance changes

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

            // TASK-008 VERIFICATION: Hook EndReached event to detect when media ends
            _mediaPlayer.EndReached += OnMediaEndReached;
            _logger.LogInformation("[GIF-INIT] EndReached event handler subscribed");

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

            // If using memory callbacks (headless mode), we need to detect video dimensions first
            if (_useMemoryCallbacks)
            {
                _logger.LogInformation("Parsing media to detect video dimensions for memory callbacks...");

                // Parse media to get track information
                await media.Parse(MediaParseOptions.ParseNetwork);

                // Get video track to determine actual dimensions
                var videoTracks = media.Tracks.Where(t => t.TrackType == TrackType.Video).ToArray();
                if (videoTracks.Length > 0)
                {
                    var videoTrack = videoTracks[0];
                    // Access video track data to get dimensions
                    _videoWidth = (int)videoTrack.Data.Video.Width;
                    _videoHeight = (int)videoTrack.Data.Video.Height;

                    _logger.LogInformation("Detected video dimensions: {Width}x{Height}", _videoWidth, _videoHeight);

                    // Now setup callbacks with correct dimensions
                    SetupVideoCallbacksWithDimensions(_videoWidth, _videoHeight);
                }
                else
                {
                    _logger.LogWarning("Could not detect video dimensions, using default 1920x1080");
                    SetupVideoCallbacksWithDimensions(1920, 1080);
                }
            }

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

            // TASK-008: Log LibVLC player state to verify playback started
            if (_useMemoryCallbacks)
            {
                _callbackTrackingStart = DateTime.UtcNow;
                _lastCallbackLogTime = DateTime.UtcNow;

                var isPlaying = _mediaPlayer.IsPlaying;
                var position = _mediaPlayer.Position;
                var time = _mediaPlayer.Time;
                var length = _mediaPlayer.Length;
                var rate = _mediaPlayer.Rate;

                // TASK-011: Capture animation metadata
                _animationLengthMs = length;
                _loopCounter = 0;
                _lastLoggedPosition = 0f;

                // Estimate frame count (assume 30 FPS for GIFs if unknown)
                if (length > 0)
                {
                    _totalFrameCount = (int)((length / 1000.0) * _estimatedFPS);
                }

                _logger.LogInformation("[GIF-DEBUG] LibVLC playback started | IsPlaying: {IsPlaying} | Position: {Position:F3} | Time: {Time}ms | Length: {Length}ms | Rate: {Rate}",
                    isPlaying, position, time, length, rate);

                // TASK-011: Log animation metadata
                _logger.LogInformation("[GIF] Animation Metadata | Duration: {Duration}ms ({Seconds:F1}s) | Est. Total Frames: {Frames} @ {FPS} FPS | Dimensions: {Width}x{Height}",
                    length, length / 1000.0, _totalFrameCount, _estimatedFPS, _videoWidth, _videoHeight);

                if (!isPlaying)
                {
                    _logger.LogWarning("[GIF-DEBUG] WARNING: LibVLC reports IsPlaying=false after Play() call!");
                }
            }

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
                        // VERIFICATION: Track every GetFrameAtPosition call
                        Interlocked.Increment(ref _getFrameCallCount);

                        // TASK-008 DEBUG: Log every 10 calls (~6 times/second at 60 FPS)
                        if (_getFrameCallCount % 10 == 0)
                        {
                            int currentBitmapHash = _currentFrameBitmap.GetHashCode();
                            bool bitmapChanged = (currentBitmapHash != _lastBitmapHashCode);
                            _lastBitmapHashCode = currentBitmapHash;

                            string changeStatus = bitmapChanged ? "✅ CHANGED" : "⚠️ SAME";
                            _logger.LogInformation("[FRAME-REQUEST] GetFrameAtPosition #{Count} | Timestamp: {TimestampMs}ms | DisplayCallbacks: {DisplayCount} | Bitmap: {BitmapStatus}",
                                _getFrameCallCount, timestampMs, _displayCallbackCount, changeStatus);
                        }

                        // TASK-008 VERIFICATION: Log every 60 calls (once per second at 60 FPS)
                        // This verifies D2DPlayer is actually requesting frames
                        if (_getFrameCallCount % 60 == 0)
                        {
                            // Check if DisplayCallback is still being called
                            bool callbacksStuck = (_displayCallbackCount == _lastDisplayCallbackValue);
                            _lastDisplayCallbackValue = _displayCallbackCount;

                            float currentPosition = 0f;
                            bool isPlaying = false;
                            if (_mediaPlayer != null)
                            {
                                try
                                {
                                    currentPosition = _mediaPlayer.Position;
                                    isPlaying = _mediaPlayer.IsPlaying;
                                }
                                catch { }
                            }

                            int currentFrameEstimate = _totalFrameCount > 0
                                ? (int)(currentPosition * _totalFrameCount)
                                : 0;

                            string status = callbacksStuck ? "⚠️ STUCK" : "✅ OK";
                            _logger.LogInformation("[VERIFY] GetFrameAtPosition #{GetFrameCount} | DisplayCallbacks: {DisplayCount} {Status} | Frame: {Frame}/{Total} | Pos: {Pos:F3} | IsPlaying: {IsPlaying}",
                                _getFrameCallCount, _displayCallbackCount, status,
                                currentFrameEstimate, _totalFrameCount, currentPosition, isPlaying);

                            // TASK-008 CRITICAL FIX: If callbacks are stuck near the end, manually restart playback
                            if (callbacksStuck && currentPosition > 0.90f && isPlaying && _config?.Loop == true)
                            {
                                _logger.LogWarning("[GIF-RESTART] ⚠️ DETECTED STUCK STATE! Callbacks frozen at {Count}, Position={Pos:F3}. Restarting playback...",
                                    _displayCallbackCount, currentPosition);

                                try
                                {
                                    // Stop and restart to force LibVLC to re-decode from start
                                    _mediaPlayer.Stop();
                                    Thread.Sleep(50); // Give LibVLC time to stop
                                    _mediaPlayer.Play();
                                    _loopCounter++;
                                    _logger.LogInformation("[GIF-RESTART] ✅ Playback restarted | Loop #{Loop}", _loopCounter);
                                }
                                catch (Exception restartEx)
                                {
                                    _logger.LogError(restartEx, "[GIF-RESTART] Failed to restart playback");
                                }
                            }
                        }

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
            // Callbacks will be setup in StartAsync() after detecting actual video dimensions
            _useMemoryCallbacks = true;
            _logger.LogInformation("Headless mode: Will use LibVLC memory callbacks (dimensions detected in StartAsync)");
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
    /// Setup LibVLC memory callbacks for direct frame access (headless mode) with specified dimensions.
    /// Uses LibVLCSharp 3.x simplified callback API.
    /// Based on: https://github.com/mfkl/libvlcsharp-samples/blob/master/PreviewThumbnailExtractor/Program.cs
    /// </summary>
    /// <param name="width">Actual video/GIF width in pixels (detected from media track)</param>
    /// <param name="height">Actual video/GIF height in pixels (detected from media track)</param>
    private void SetupVideoCallbacksWithDimensions(int width, int height)
    {
        if (_mediaPlayer == null)
        {
            _logger.LogWarning("Cannot setup video callbacks - media player is null");
            return;
        }

        try
        {
            // Use actual video dimensions (detected from media track in StartAsync)
            _videoWidth = width;
            _videoHeight = height;
            uint pitch = (uint)(_videoWidth * 4);  // 4 bytes per pixel for RGBA

            // Allocate frame buffer based on actual video/GIF dimensions
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
            // TASK-008: Track callback frequency
            Interlocked.Increment(ref _lockCallbackCount);

            // Provide buffer pointer to LibVLC via planes parameter
            Marshal.WriteIntPtr(planes, _frameBufferPtr);

            // Log every 30 callbacks (roughly once per second at 30 FPS)
            if (_lockCallbackCount % 30 == 0)
            {
                var elapsed = (DateTime.UtcNow - _callbackTrackingStart).TotalSeconds;
                var callbacksPerSecond = elapsed > 0 ? _lockCallbackCount / elapsed : 0;
                _logger.LogInformation("[GIF-DEBUG] VideoLockCallback #{Count} | Callbacks/sec: {Rate:F1}",
                    _lockCallbackCount, callbacksPerSecond);
            }

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

            // TASK-008: Track callback frequency
            Interlocked.Increment(ref _displayCallbackCount);

            lock (_frameLock)
            {
                // Dispose old frame
                _currentFrameBitmap?.Dispose();

                // Convert raw RGBA buffer to Bitmap
                _currentFrameBitmap = ConvertRawFrameToBitmap(_frameBufferPtr, _videoWidth, _videoHeight);

                _logger.LogTrace("Frame decoded: {Width}x{Height} via memory callback", _videoWidth, _videoHeight);
            }

            // TASK-008 FIX: Manual looping - Check on EVERY frame, not just every 30
            // CRITICAL: This must be OUTSIDE the logging block to work properly!
            if (_mediaPlayer != null)
            {
                try
                {
                    float currentPosition = _mediaPlayer.Position;
                    bool isPlaying = _mediaPlayer.IsPlaying;

                    // When we reach 95% of the animation, seek back to start to create infinite loop
                    if (currentPosition > 0.95f && isPlaying)
                    {
                        _logger.LogInformation("[GIF-LOOP] Near end detected (Pos: {Pos:F3}, Frame: {Frame}), seeking to start NOW",
                            currentPosition, _displayCallbackCount);
                        _mediaPlayer.Time = 0;
                        _loopCounter++;
                        _logger.LogInformation("[GIF-LOOP] 🔄 Seeked to start | Loop #{Loop}", _loopCounter);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in loop check");
                }
            }

            // TASK-008 & TASK-011: Log every 30 frames (roughly once per second at 30 FPS)
            if (_displayCallbackCount % 30 == 0)
            {
                var elapsed = (DateTime.UtcNow - _callbackTrackingStart).TotalSeconds;
                var framesPerSecond = elapsed > 0 ? _displayCallbackCount / elapsed : 0;

                // Get current LibVLC player state
                float currentPosition = 0f;
                long currentTime = 0;
                bool isPlaying = false;

                if (_mediaPlayer != null)
                {
                    try
                    {
                        isPlaying = _mediaPlayer.IsPlaying;
                        currentPosition = _mediaPlayer.Position;
                        currentTime = _mediaPlayer.Time;

                        // TASK-011: Detect loop (position went backwards)
                        if (currentPosition < _lastLoggedPosition && _lastLoggedPosition > 0.8f)
                        {
                            _loopCounter++;
                            _logger.LogInformation("[GIF] 🔄 Loop Completed | Loop #{Loop} | Position reset: {OldPos:F3} → {NewPos:F3}",
                                _loopCounter, _lastLoggedPosition, currentPosition);
                        }
                        _lastLoggedPosition = currentPosition;
                    }
                    catch { }
                }

                // TASK-011: Calculate frame number and progress
                int currentFrameEstimate = _totalFrameCount > 0
                    ? (int)(currentPosition * _totalFrameCount)
                    : (int)_displayCallbackCount;

                float progressPercent = currentPosition * 100f;

                // TASK-011: Enhanced logging with frame tracking
                _logger.LogInformation("[GIF] Frame {CurrentFrame}/{TotalFrames} ({Progress:F1}%) | Loop: {Loop} | FPS: {Rate:F1} | Time: {Time}ms / {Length}ms | IsPlaying: {IsPlaying}",
                    currentFrameEstimate, _totalFrameCount, progressPercent, _loopCounter,
                    framesPerSecond, currentTime, _animationLengthMs, isPlaying);
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

    /// <summary>
    /// TASK-008 VERIFICATION: Handle media end event
    /// LibVLC's input-repeat=-1 doesn't work in headless/callback mode, so we manually restart
    /// </summary>
    private void OnMediaEndReached(object? sender, EventArgs e)
    {
        try
        {
            _logger.LogWarning("[GIF-END] ⚠️ Media EndReached event fired! Animation reached end at DisplayCallback #{Count}",
                _displayCallbackCount);

            // Manually restart playback if looping is enabled
            if (_config?.Loop == true && _mediaPlayer != null)
            {
                _logger.LogInformation("[GIF-END] Restarting playback from beginning (Loop #{Loop})",
                    _loopCounter + 1);

                // Stop and restart the media
                _mediaPlayer.Stop();
                Task.Delay(50).Wait(); // Small delay to ensure stop completes
                _mediaPlayer.Play();

                _loopCounter++;
                _logger.LogInformation("[GIF-END] ✅ Playback restarted for loop #{Loop}", _loopCounter);
            }
            else
            {
                _logger.LogWarning("[GIF-END] Loop disabled or player null, playback will stop");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnMediaEndReached handler");
        }
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

        // Unsubscribe from events
        if (_mediaPlayer != null)
        {
            _mediaPlayer.EndReached -= OnMediaEndReached;
        }

        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _libVLC?.Dispose();

        _renderForm?.Close();
        _renderForm?.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

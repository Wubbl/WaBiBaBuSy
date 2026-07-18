using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using ImageMagick;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Models;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Renderers;

/// <summary>
/// GIF-based wallpaper renderer with frame caching and timing.
/// </summary>
public class GifWallpaperRenderer : IWallpaperRenderer
{
    private readonly ILogger<GifWallpaperRenderer> _logger;
    private readonly DesktopWindowManager _desktopManager;
    private readonly object _gifImageLock = new(); // Thread-safe access to _gifImage (GDI+ not thread-safe!)

    private Image? _gifImage;
    private FrameDimension? _frameDimension;
    private int _frameCount;
    private int _currentFrameIndex;
    private int[]? _frameDelays; // Delay in milliseconds for each frame
    private Bitmap[]? _frameCache; // Cached frames to avoid repeated SelectActiveFrame calls
    private volatile bool _frameCacheReady = false; // True when all frames are cached
    private CancellationTokenSource? _cachingCancellation; // Cancellation token for background caching
    private Form? _renderForm;
    private PictureBox? _pictureBox;
    private System.Threading.Timer? _frameTimer;
    private WallpaperConfig? _config;
    private WallpaperState _state = WallpaperState.Uninitialized;
    private long _playbackStartTime;
    private bool _disposed;
    private double _speedMultiplier = 1.0; // Speed multiplier (2.0 = 2x faster, 0.5 = half speed)

    public event EventHandler<FrameRenderedEventArgs>? FrameRendered;
    public event EventHandler<WallpaperState>? StateChanged;

    public GifWallpaperRenderer(
        ILogger<GifWallpaperRenderer> logger,
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

    public long PositionMs
    {
        get
        {
            if (State != WallpaperState.Playing || _frameDelays == null)
                return 0;

            // Calculate position based on current frame and elapsed time
            var elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _playbackStartTime;
            return elapsedMs;
        }
    }

    public int ContentWidth => _gifImage?.Width ?? 0;
    public int ContentHeight => _gifImage?.Height ?? 0;

    public IntPtr WindowHandle => IntPtr.Zero; // GIF renderer uses headless mode (no window)

    /// <summary>
    /// Set the speed multiplier for GIF playback.
    /// 1.0 = normal speed, 2.0 = 2x faster, 0.5 = half speed.
    /// </summary>
    public void SetSpeedMultiplier(double speedMultiplier)
    {
        if (speedMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(speedMultiplier), "Speed multiplier must be greater than 0");

        _speedMultiplier = speedMultiplier;
        _logger.LogInformation("GIF speed multiplier set to {Multiplier}x", speedMultiplier);
    }

    public async Task InitializeAsync(WallpaperConfig config)
    {
        try
        {
            _logger.LogInformation("Initializing GIF wallpaper renderer for {FilePath}", config.FilePath);
            _config = config;

            // Set speed multiplier from config BEFORE extracting frame delays
            _speedMultiplier = config.SpeedMultiplier;
            _logger.LogInformation("Using speed multiplier: {Multiplier}x", _speedMultiplier);

            // Load GIF image
            _gifImage = Image.FromFile(config.FilePath);

            // Get frame dimension for GIF animation
            _frameDimension = new FrameDimension(_gifImage.FrameDimensionsList[0]);
            _frameCount = _gifImage.GetFrameCount(_frameDimension);

            _logger.LogInformation("Loaded GIF with {FrameCount} frames", _frameCount);

            // CRITICAL: Extract and cache frames SYNCHRONOUSLY during initialization
            // Frame delays are also extracted during caching (from Magick.NET metadata)
            // For distributed systems, all clients must have frames cached BEFORE animation starts
            // This ensures timing consistency across all machines
            _logger.LogInformation("Extracting and caching all frames BEFORE initialization completes (blocking for distributed sync)...");
            _cachingCancellation = new CancellationTokenSource();
            await ExtractAndCacheFramesAsync(_cachingCancellation.Token);
            _logger.LogInformation("Frame caching complete, initialization can proceed");

            // Create render window (only in non-headless mode)
            // In headless mode, we only provide frames via GetFrameAtPosition()
            if (!config.HeadlessMode)
            {
                await CreateRenderWindowAsync(config);
            }
            else
            {
                _logger.LogInformation("GIF renderer initialized in HEADLESS mode (no window created)");
            }

            State = WallpaperState.Stopped;
            _logger.LogInformation("GIF wallpaper renderer initialized successfully (Headless={Headless})", config.HeadlessMode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize GIF wallpaper renderer");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task StartAsync()
    {
        try
        {
            if (_gifImage == null || _frameDimension == null || _frameDelays == null)
                throw new InvalidOperationException("Renderer not initialized");

            _logger.LogInformation("Starting GIF playback");

            _playbackStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _currentFrameIndex = 0;

            // Start frame animation timer
            var initialDelay = _frameDelays[0];
            _frameTimer = new System.Threading.Timer(
                OnFrameTimer,
                null,
                initialDelay,
                System.Threading.Timeout.Infinite);

            State = WallpaperState.Playing;
            _logger.LogInformation("GIF playback started");

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start GIF playback");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task PauseAsync()
    {
        try
        {
            _frameTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            State = WallpaperState.Paused;
            _logger.LogInformation("GIF playback paused");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pause GIF playback");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task ResumeAsync()
    {
        try
        {
            if (_frameDelays == null)
                throw new InvalidOperationException("Renderer not initialized");

            var delay = _frameDelays[_currentFrameIndex];
            _frameTimer?.Change(delay, System.Threading.Timeout.Infinite);

            State = WallpaperState.Playing;
            _logger.LogInformation("GIF playback resumed");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume GIF playback");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task StopAsync()
    {
        try
        {
            _frameTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _currentFrameIndex = 0;
            State = WallpaperState.Stopped;
            _logger.LogInformation("GIF playback stopped");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop GIF playback");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task SeekAsync(TimeSpan position)
    {
        try
        {
            if (_frameDelays == null || _frameDimension == null || _gifImage == null)
                throw new InvalidOperationException("Renderer not initialized");

            var targetMs = (long)position.TotalMilliseconds;

            // Calculate which frame corresponds to this position
            long accumulatedMs = 0;
            int targetFrame = 0;

            for (int i = 0; i < _frameCount; i++)
            {
                accumulatedMs += _frameDelays[i];
                if (accumulatedMs >= targetMs)
                {
                    targetFrame = i;
                    break;
                }
            }

            _currentFrameIndex = targetFrame;

            // Update the displayed frame
            if (_pictureBox != null && _renderForm != null)
            {
                _renderForm.Invoke(() =>
                {
                    // CRITICAL: Lock access to _gifImage (GDI+ not thread-safe!)
                    lock (_gifImageLock)
                    {
                        _gifImage.SelectActiveFrame(_frameDimension, _currentFrameIndex);
                        _pictureBox.Image = (Image)_gifImage.Clone();
                        _pictureBox.Refresh();
                    }
                });
            }

            _logger.LogDebug("Seeked to frame {Frame} at position {Position}ms", targetFrame, position.TotalMilliseconds);

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seek GIF");
            throw;
        }
    }

    /// <summary>
    /// Gets the frame at a specific timestamp (used by composition system).
    /// This method is called by the composition pipeline to get frames on-demand.
    /// </summary>
    public Bitmap GetFrameAtPosition(long timestampMs)
    {
        try
        {
            if (_frameDelays == null)
                throw new InvalidOperationException("Renderer not initialized");

            // Calculate total animation duration for looping
            long totalDurationMs = 0;
            for (int i = 0; i < _frameCount; i++)
            {
                totalDurationMs += _frameDelays[i];
            }

            // Handle looping: wrap timestamp around total duration
            if (totalDurationMs > 0)
            {
                timestampMs = timestampMs % totalDurationMs;
            }

            // Calculate which frame corresponds to this timestamp
            long accumulatedMs = 0;
            int frameIndex = 0;

            for (int i = 0; i < _frameCount; i++)
            {
                accumulatedMs += _frameDelays[i];
                if (timestampMs < accumulatedMs)
                {
                    frameIndex = i;
                    break;
                }
            }

            // Return cached frame if available (FAST PATH - no SelectActiveFrame needed!)
            if (_frameCache != null && frameIndex < _frameCache.Length)
            {
                // CRITICAL: Check if this specific frame is cached (may still be caching in background)
                var cachedFrame = _frameCache[frameIndex];
                if (cachedFrame != null)
                {
                    // Clone the cached frame to avoid disposal issues in caller
                    return new Bitmap(cachedFrame);
                }
                // else: Frame not yet cached, fall through to on-demand selection below
            }

            // Fallback: select frame on-demand if cache not available (SLOW PATH)
            if (_gifImage == null || _frameDimension == null)
                throw new InvalidOperationException("GIF image not loaded and frame cache unavailable");

            _logger.LogWarning("Frame cache unavailable, using slow on-demand frame selection");

            // CRITICAL: Lock access to _gifImage (GDI+ not thread-safe!)
            // Prevents "Object is currently in use elsewhere" when caching is in progress
            lock (_gifImageLock)
            {
                _gifImage.SelectActiveFrame(_frameDimension, frameIndex);
                var frameBitmap = new Bitmap(_gifImage);
                return frameBitmap;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting GIF frame at position {TimestampMs}ms", timestampMs);
            // Return blank bitmap on error rather than throwing (composition system should continue)
            return new Bitmap(1, 1);
        }
    }

    private void ExtractFrameDelays()
    {
        try
        {
            if (_gifImage == null)
                return;

            _frameDelays = new int[_frameCount];

            // PropertyTagFrameDelay = 0x5100
            const int PropertyTagFrameDelay = 0x5100;

            // Try to get frame delay property
            if (_gifImage.PropertyIdList.Contains(PropertyTagFrameDelay))
            {
                var delayProperty = _gifImage.GetPropertyItem(PropertyTagFrameDelay);
                if (delayProperty?.Value != null)
                {
                    for (int i = 0; i < _frameCount; i++)
                    {
                        // Frame delay is in 1/100th of a second, stored as 4-byte integer
                        var delayInHundredths = BitConverter.ToInt32(delayProperty.Value, i * 4);

                        // Convert to milliseconds
                        // NOTE: GIF spec allows delays < 20ms, but browsers clamp 0-10ms delays to 100ms!
                        // We honor the original timing but apply speed multiplier to allow user control
                        var delayMs = delayInHundredths * 10;

                        // Apply speed multiplier (2.0 = half the delay = 2x speed)
                        delayMs = (int)(delayMs / _speedMultiplier);

                        // Enforce absolute minimum 1ms to prevent infinite loops
                        _frameDelays[i] = Math.Max(delayMs, 1);
                    }

                    _logger.LogDebug("Extracted frame delays from GIF metadata");
                    return;
                }
            }

            // Fallback: Use default 100ms per frame if no metadata found
            _logger.LogWarning("No frame delay metadata found, using default 100ms per frame");
            for (int i = 0; i < _frameCount; i++)
            {
                _frameDelays[i] = 100;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting frame delays, using default");
            _frameDelays = new int[_frameCount];
            for (int i = 0; i < _frameCount; i++)
            {
                _frameDelays[i] = 100;
            }
        }
    }

    private async Task ExtractAndCacheFramesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_config == null || string.IsNullOrEmpty(_config.FilePath))
            {
                _logger.LogWarning("Cannot extract frames: config or file path not set");
                return;
            }

            // Only cache small-to-medium GIFs to avoid excessive memory usage
            const int MAX_FRAMES_TO_CACHE = 1000;

            if (_frameCount > MAX_FRAMES_TO_CACHE)
            {
                var estimatedMemoryMB = _gifImage != null
                    ? (_frameCount * _gifImage.Width * _gifImage.Height * 4) / (1024 * 1024)
                    : 0;
                _logger.LogInformation("GIF has {FrameCount} frames (>{Max}), skipping frame cache to save ~{MemoryMB}MB memory. Using on-demand frame selection.",
                    _frameCount, MAX_FRAMES_TO_CACHE, estimatedMemoryMB);
                // Still need frame delays even without caching
                ExtractFrameDelays();
                _frameCache = null;
                _frameCacheReady = false;
                return;
            }

            _logger.LogInformation("Caching {FrameCount} frames using Magick.NET (BLOCKING - required for distributed sync)...", _frameCount);
            var startTime = DateTime.UtcNow;

            // Use Magick.NET for fast single-pass frame extraction
            // Coalesce() applies GIF disposal methods so each frame is a full image
            using var collection = new MagickImageCollection(_config.FilePath);
            collection.Coalesce();

            _frameCount = collection.Count;
            _frameCache = new Bitmap[_frameCount];
            _frameDelays = new int[_frameCount];
            var lastProgressLog = 0;

            for (int i = 0; i < _frameCount; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Frame caching cancelled - renderer stopped/disposed");
                    if (_frameCache != null)
                    {
                        for (int j = 0; j < i; j++)
                            _frameCache[j]?.Dispose();
                    }
                    _frameCache = null;
                    _frameCacheReady = false;
                    return;
                }

                var frame = collection[i];

                // Extract delay from Magick.NET (AnimationDelay is in 1/100th of a second)
                int delayMs = (int)(frame.AnimationDelay * 10);
                if (delayMs <= 0) delayMs = 10; // Browser standard: 0-delay = 10ms
                if (delayMs > 1000) delayMs = 1000; // Cap "stop" markers
                // Apply speed multiplier
                _frameDelays[i] = Math.Max((int)(delayMs / _speedMultiplier), 1);

                // Convert Magick frame to System.Drawing.Bitmap
                _frameCache[i] = frame.ToBitmap();

                // Log progress every 10%
                var currentProgress = (i + 1) * 100 / _frameCount;
                if (currentProgress >= lastProgressLog + 10 || i == _frameCount - 1)
                {
                    lastProgressLog = currentProgress;
                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    var eta = elapsed / (i + 1) * (_frameCount - i - 1);
                    _logger.LogInformation("Frame caching: {Progress}% ({Current}/{Total}) - Elapsed: {Elapsed:F1}s, ETA: {ETA:F1}s",
                        currentProgress, i + 1, _frameCount, elapsed, eta);
                }
            }

            _frameCacheReady = true;
            var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var memoryMB = _gifImage != null
                ? (_frameCount * _gifImage.Width * _gifImage.Height * 4) / (1024 * 1024)
                : 0;

            _logger.LogInformation("Frame cache complete: {FrameCount} frames cached in {ElapsedMs}ms (~{MemoryMB}MB) using Magick.NET",
                _frameCount, (int)elapsedMs, memoryMB);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Frame caching cancelled");
            _frameCache = null;
            _frameCacheReady = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching GIF frames with Magick.NET, falling back to on-demand frame selection");
            _frameCache = null;
            _frameCacheReady = false;
        }
    }

    private void OnFrameTimer(object? state)
    {
        if (_disposed || _gifImage == null || _frameDimension == null || _frameDelays == null || _pictureBox == null || _renderForm == null)
            return;

        try
        {
            // Move to next frame
            _currentFrameIndex = (_currentFrameIndex + 1) % _frameCount;

            // Update the frame on UI thread
            _renderForm.Invoke(() =>
            {
                // CRITICAL: Lock access to _gifImage (GDI+ not thread-safe!)
                lock (_gifImageLock)
                {
                    _gifImage.SelectActiveFrame(_frameDimension, _currentFrameIndex);
                    _pictureBox.Image = (Image)_gifImage.Clone();
                    _pictureBox.Refresh();
                }
            });

            // Schedule next frame
            var nextDelay = _frameDelays[_currentFrameIndex];
            _frameTimer?.Change(nextDelay, System.Threading.Timeout.Infinite);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating GIF frame");
        }
    }

    private Task CreateRenderWindowAsync(WallpaperConfig config)
    {
        // Windows Forms controls must be created on the calling thread
        // Do NOT wrap in Task.Run - this causes threading issues
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

        // Validate monitor index using native API (avoids WinForms dependency)
        var monitors = Native.NativeMonitorInfo.GetAllMonitors();
        if (config.MonitorIndex < 0 || config.MonitorIndex >= monitors.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config.MonitorIndex),
                $"Invalid monitor index {config.MonitorIndex}. Must be between 0 and {monitors.Length - 1}. Total monitors: {monitors.Length}");
        }

        // Set window bounds for the specific monitor
        var screen = monitors[config.MonitorIndex];
        _renderForm.Bounds = screen.Bounds;

        _logger.LogInformation("GIF renderer set to monitor {Index}: {Bounds} (Device: {Device})",
            config.MonitorIndex,
            screen.Bounds,
            screen.DeviceName);

        // Create PictureBox to display GIF
        _pictureBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.StretchImage,
            BackColor = Color.Black
        };

        _renderForm.Controls.Add(_pictureBox);

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

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("[Dispose] Starting GIF wallpaper renderer disposal");
        var startTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        // Cancel background frame caching FIRST to prevent race conditions
        _logger.LogInformation("[Dispose] Cancelling background frame caching");
        _cachingCancellation?.Cancel();
        _cachingCancellation?.Dispose();
        _cachingCancellation = null;
        var afterCancelTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] Caching cancelled in {ElapsedMs}ms", afterCancelTime - startTime);

        _logger.LogInformation("[Dispose] Disposing frame timer");
        _frameTimer?.Dispose();
        _frameTimer = null;
        var afterTimerTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] Timer disposed in {ElapsedMs}ms", afterTimerTime - afterCancelTime);

        _logger.LogInformation("[Dispose] Disposing picture box");
        _pictureBox?.Dispose();
        _pictureBox = null;
        var afterPicTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] PictureBox disposed in {ElapsedMs}ms", afterPicTime - afterTimerTime);

        // Dispose cached frames
        if (_frameCache != null)
        {
            _logger.LogInformation("[Dispose] Disposing {FrameCount} cached frames", _frameCache.Length);
            foreach (var frame in _frameCache)
            {
                frame?.Dispose();
            }
            _frameCache = null;
            var afterCacheTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            _logger.LogInformation("[Dispose] Frame cache disposed in {ElapsedMs}ms", afterCacheTime - afterPicTime);
        }

        _logger.LogInformation("[Dispose] Disposing GIF image");
        _gifImage?.Dispose();
        _gifImage = null;
        var afterGifTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] GIF image disposed in {ElapsedMs}ms", afterGifTime - afterPicTime);

        _logger.LogInformation("[Dispose] Closing and disposing render form");
        _renderForm?.Close();
        _renderForm?.Dispose();
        _renderForm = null;
        var afterFormTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] Render form disposed in {ElapsedMs}ms", afterFormTime - afterGifTime);

        _disposed = true;
        var totalTime = afterFormTime - startTime;
        _logger.LogInformation("[Dispose] GIF WALLPAPER RENDERER TOTAL DISPOSAL TIME: {TotalMs}ms", totalTime);

        GC.SuppressFinalize(this);
    }
}

using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
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

    private Image? _gifImage;
    private FrameDimension? _frameDimension;
    private int _frameCount;
    private int _currentFrameIndex;
    private int[]? _frameDelays; // Delay in milliseconds for each frame
    private Form? _renderForm;
    private PictureBox? _pictureBox;
    private System.Threading.Timer? _frameTimer;
    private WallpaperConfig? _config;
    private WallpaperState _state = WallpaperState.Uninitialized;
    private long _playbackStartTime;
    private bool _disposed;

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

    public async Task InitializeAsync(WallpaperConfig config)
    {
        try
        {
            _logger.LogInformation("Initializing GIF wallpaper renderer for {FilePath}", config.FilePath);
            _config = config;

            // Load GIF image
            _gifImage = Image.FromFile(config.FilePath);

            // Get frame dimension for GIF animation
            _frameDimension = new FrameDimension(_gifImage.FrameDimensionsList[0]);
            _frameCount = _gifImage.GetFrameCount(_frameDimension);

            _logger.LogInformation("Loaded GIF with {FrameCount} frames", _frameCount);

            // Extract frame delays from GIF metadata
            ExtractFrameDelays();

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
                    _gifImage.SelectActiveFrame(_frameDimension, _currentFrameIndex);
                    _pictureBox.Image = (Image)_gifImage.Clone();
                    _pictureBox.Refresh();
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
            if (_gifImage == null || _frameDimension == null || _frameDelays == null)
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

            // Select the frame and convert to Bitmap
            _gifImage.SelectActiveFrame(_frameDimension, frameIndex);
            var frameBitmap = new Bitmap(_gifImage);

            return frameBitmap;
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

                        // Convert to milliseconds (and ensure minimum 10ms to avoid too fast animation)
                        _frameDelays[i] = Math.Max(delayInHundredths * 10, 10);
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
                _gifImage.SelectActiveFrame(_frameDimension, _currentFrameIndex);
                _pictureBox.Image = (Image)_gifImage.Clone();
                _pictureBox.Refresh();
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

        _logger.LogInformation("[Dispose] Disposing frame timer");
        _frameTimer?.Dispose();
        _frameTimer = null;
        var afterTimerTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] Timer disposed in {ElapsedMs}ms", afterTimerTime - startTime);

        _logger.LogInformation("[Dispose] Disposing picture box");
        _pictureBox?.Dispose();
        _pictureBox = null;
        var afterPicTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _logger.LogInformation("[Dispose] PictureBox disposed in {ElapsedMs}ms", afterPicTime - afterTimerTime);

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

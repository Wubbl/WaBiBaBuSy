using System.Windows.Forms;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Models;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Renderers;

/// <summary>
/// LibVLC-based static image wallpaper renderer for JPG, PNG, BMP formats.
/// Uses LibVLC's image rendering which works reliably with desktop parenting.
/// </summary>
public class ImageWallpaperRendererLibVLC : IWallpaperRenderer
{
    private readonly ILogger<ImageWallpaperRendererLibVLC> _logger;
    private readonly DesktopWindowManager _desktopManager;

    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Form? _renderForm;
    private WallpaperConfig? _config;
    private WallpaperState _state = WallpaperState.Uninitialized;
    private bool _disposed;
    private System.Drawing.Bitmap? _cachedFrame; // Cache the image for GetFrameAtPosition

    public event EventHandler<FrameRenderedEventArgs>? FrameRendered;
    public event EventHandler<WallpaperState>? StateChanged;

    public ImageWallpaperRendererLibVLC(
        ILogger<ImageWallpaperRendererLibVLC> logger,
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

    /// <summary>
    /// For static images, position is always 0
    /// </summary>
    public long PositionMs => 0;

    public int ContentWidth => _cachedFrame?.Width ?? 0;
    public int ContentHeight => _cachedFrame?.Height ?? 0;

    public IntPtr WindowHandle => _renderForm?.Handle ?? IntPtr.Zero;

    public async Task InitializeAsync(WallpaperConfig config)
    {
        try
        {
            _logger.LogInformation("Initializing LibVLC image wallpaper renderer for {FilePath}", config.FilePath);
            _config = config;

            // Verify image file exists
            if (!File.Exists(config.FilePath))
            {
                throw new FileNotFoundException($"Image file not found: {config.FilePath}");
            }

            // CRITICAL: Check HeadlessMode - if true, we're being used by the composition pipeline
            // In HeadlessMode, we don't create windows or LibVLC players - just cache the image
            if (config.HeadlessMode)
            {
                _logger.LogInformation("HeadlessMode enabled - caching image for composition pipeline");

                // Load and cache the image once
                using var sourceImage = System.Drawing.Image.FromFile(config.FilePath);
                _cachedFrame = new System.Drawing.Bitmap(sourceImage);

                State = WallpaperState.Stopped;
                _logger.LogInformation("Image cached successfully for headless rendering: {Width}x{Height}",
                    _cachedFrame.Width, _cachedFrame.Height);
                return;
            }

            // Normal mode (not headless) - use LibVLC for direct rendering
            _logger.LogInformation("Normal mode - initializing LibVLC for direct rendering");

            // Initialize LibVLC
            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC(enableDebugLogs: false,
                "--no-video-title-show",  // Don't show video title
                "--no-audio",             // No audio
                "--image-duration=-1");   // Show image indefinitely

            // Create media player
            _mediaPlayer = new MediaPlayer(_libVLC);

            // Create render window
            await CreateRenderWindowAsync(config);

            // Load the image
            var media = new Media(_libVLC, config.FilePath, FromType.FromPath);
            _mediaPlayer.Media = media;

            State = WallpaperState.Stopped;
            _logger.LogInformation("LibVLC image wallpaper renderer initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize LibVLC image wallpaper renderer");
            State = WallpaperState.Error;
            throw;
        }
    }

    public async Task StartAsync()
    {
        try
        {
            // In HeadlessMode, we don't have a media player - just mark as playing
            if (_config?.HeadlessMode == true)
            {
                _logger.LogInformation("Starting image display (HeadlessMode - no-op)");
                State = WallpaperState.Playing;
                return;
            }

            if (_mediaPlayer == null || _config == null)
                throw new InvalidOperationException("Renderer not initialized");

            _logger.LogInformation("Starting image display (LibVLC)");

            // Play the image (LibVLC will display it)
            _mediaPlayer.Play();

            // Wait for playback to start
            await Task.Delay(100);

            State = WallpaperState.Playing;
            _logger.LogInformation("Image display started (LibVLC)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start image display");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task PauseAsync()
    {
        try
        {
            // Static images don't have playback, but we'll track the state
            _mediaPlayer?.SetPause(true);
            State = WallpaperState.Paused;
            _logger.LogInformation("Image display paused");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pause image display");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task ResumeAsync()
    {
        try
        {
            _mediaPlayer?.SetPause(false);
            State = WallpaperState.Playing;
            _logger.LogInformation("Image display resumed");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume image display");
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
            _logger.LogInformation("Image display stopped");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop image display");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task SeekAsync(TimeSpan position)
    {
        // Static images have no timeline, seeking is a no-op
        _logger.LogDebug("Seek called on static image (no-op)");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the frame at a specific timestamp (used by composition system).
    /// For static images, always returns the same frame regardless of timestamp.
    /// CRITICAL: Returns a CLONE of the cached frame to prevent disposal issues.
    /// </summary>
    public System.Drawing.Bitmap GetFrameAtPosition(long timestampMs)
    {
        try
        {
            // If we have a cached frame (HeadlessMode), return a clone
            if (_cachedFrame != null)
            {
                // Return a clone to prevent the caller from disposing our cached frame
                return new System.Drawing.Bitmap(_cachedFrame);
            }

            // Fallback: Load from file (shouldn't happen in HeadlessMode)
            if (_config == null || string.IsNullOrEmpty(_config.FilePath))
            {
                _logger.LogWarning("GetFrameAtPosition called with no config - returning 1x1 bitmap");
                return new System.Drawing.Bitmap(1, 1);
            }

            _logger.LogWarning("GetFrameAtPosition called without cached frame - loading from disk (slow!)");

            // Load and return the image as a bitmap
            using var image = System.Drawing.Image.FromFile(_config.FilePath);
            return new System.Drawing.Bitmap(image);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting image frame at position {Timestamp}ms", timestampMs);
            return new System.Drawing.Bitmap(1, 1);
        }
    }

    private Task CreateRenderWindowAsync(WallpaperConfig config)
    {
        // Windows Forms must be created on the calling thread
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

        _logger.LogInformation("Image renderer set to monitor {Index}: {Bounds} (Device: {Device})",
            config.MonitorIndex,
            screen.Bounds,
            screen.DeviceName);

        // CRITICAL: Show the form FIRST to ensure handle is fully initialized
        _renderForm.Show();

        _logger.LogDebug("Form shown, handle: {Handle}", _renderForm.Handle);

        // Set LibVLC to render to this window
        _mediaPlayer!.Hwnd = _renderForm.Handle;
        _logger.LogInformation("LibVLC Hwnd set to form handle");

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

            _logger.LogInformation("Form parented to desktop (WorkerW) successfully");
        }
        else
        {
            _logger.LogWarning("WorkerW not found, wallpaper may not render behind desktop icons");
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("Disposing LibVLC image wallpaper renderer");

        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;

        _renderForm?.Close();
        _renderForm?.Dispose();
        _renderForm = null;

        _libVLC?.Dispose();
        _libVLC = null;

        // Dispose cached frame
        _cachedFrame?.Dispose();
        _cachedFrame = null;

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

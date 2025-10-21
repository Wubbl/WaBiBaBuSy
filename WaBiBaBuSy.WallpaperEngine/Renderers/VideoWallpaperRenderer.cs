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

        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _libVLC?.Dispose();

        _renderForm?.Close();
        _renderForm?.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

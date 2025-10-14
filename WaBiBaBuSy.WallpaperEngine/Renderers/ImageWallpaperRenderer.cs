using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Models;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Renderers;

/// <summary>
/// Static image wallpaper renderer for JPG, PNG, BMP formats.
/// </summary>
public class ImageWallpaperRenderer : IWallpaperRenderer
{
    private readonly ILogger<ImageWallpaperRenderer> _logger;
    private readonly DesktopWindowManager _desktopManager;

    private Image? _image;
    private Form? _renderForm;
    private PictureBox? _pictureBox;
    private WallpaperConfig? _config;
    private WallpaperState _state = WallpaperState.Uninitialized;
    private bool _disposed;

    public event EventHandler<FrameRenderedEventArgs>? FrameRendered;
    public event EventHandler<WallpaperState>? StateChanged;

    public ImageWallpaperRenderer(
        ILogger<ImageWallpaperRenderer> logger,
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

    public async Task InitializeAsync(WallpaperConfig config)
    {
        try
        {
            _logger.LogInformation("Initializing Image wallpaper renderer for {FilePath}", config.FilePath);
            _config = config;

            // Load image from file
            _image = Image.FromFile(config.FilePath);

            _logger.LogInformation("Loaded image: {Width}x{Height}", _image.Width, _image.Height);

            // Create render window
            await CreateRenderWindowAsync(config);

            State = WallpaperState.Stopped;
            _logger.LogInformation("Image wallpaper renderer initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Image wallpaper renderer");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task StartAsync()
    {
        try
        {
            if (_image == null || _pictureBox == null)
                throw new InvalidOperationException("Renderer not initialized");

            _logger.LogInformation("Starting Image display");

            // For static images, just ensure it's visible
            // The image is already loaded and displayed in the PictureBox

            State = WallpaperState.Playing;
            _logger.LogInformation("Image display started");

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Image display");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task PauseAsync()
    {
        try
        {
            // Static images don't have playback, but we'll track the state
            State = WallpaperState.Paused;
            _logger.LogInformation("Image display paused");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pause Image display");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task ResumeAsync()
    {
        try
        {
            State = WallpaperState.Playing;
            _logger.LogInformation("Image display resumed");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume Image display");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task StopAsync()
    {
        try
        {
            State = WallpaperState.Stopped;
            _logger.LogInformation("Image display stopped");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop Image display");
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
            MinimizeBox = false,
            BackColor = Color.Black
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

        _logger.LogInformation("Image renderer set to monitor {Index}: {Bounds} (Device: {Device})",
            config.MonitorIndex,
            screen.Bounds,
            screen.DeviceName);

        // Create PictureBox to display image
        _pictureBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom, // Maintain aspect ratio
            BackColor = Color.Black,
            Image = _image
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
            _logger.LogInformation("Set as wallpaper window behind desktop icons");
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

        _logger.LogInformation("Disposing Image wallpaper renderer");

        _pictureBox?.Dispose();
        _pictureBox = null;

        _image?.Dispose();
        _image = null;

        _renderForm?.Close();
        _renderForm?.Dispose();
        _renderForm = null;

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

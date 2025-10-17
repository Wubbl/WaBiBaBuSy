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

    /// <summary>
    /// Custom Form that prevents Windows Forms from resetting the parent after SetParent is called.
    /// </summary>
    private class WallpaperForm : Form
    {
        private IntPtr _customParent = IntPtr.Zero;
        private bool _isWallpaperMode = false;

        public void SetCustomParent(IntPtr parent)
        {
            _customParent = parent;
        }

        public void EnableWallpaperMode()
        {
            _isWallpaperMode = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // Set the parent in CreateParams to prevent Windows Forms from fighting SetParent
                if (_customParent != IntPtr.Zero)
                {
                    cp.Parent = _customParent;
                }
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // Block messages that might reset the parent when in wallpaper mode
            if (_isWallpaperMode)
            {
                const int WM_PARENTNOTIFY = 0x0210;
                const int WM_WINDOWPOSCHANGING = 0x0046;
                const int WM_WINDOWPOSCHANGED = 0x0047;
                const int WM_SHOWWINDOW = 0x0018;

                // Allow these messages through but log them
                if (m.Msg == WM_PARENTNOTIFY || m.Msg == WM_WINDOWPOSCHANGING ||
                    m.Msg == WM_WINDOWPOSCHANGED || m.Msg == WM_SHOWWINDOW)
                {
                    // Just pass through, don't block
                    base.WndProc(ref m);
                    return;
                }
            }

            base.WndProc(ref m);
        }
    }

    private Task CreateRenderWindowAsync(WallpaperConfig config)
    {
        // Windows Forms controls must be created on the calling thread
        // Do NOT wrap in Task.Run - this causes threading issues
        var wallpaperForm = new WallpaperForm
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = false,
            ControlBox = false,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.Black,
            ShowIcon = false
        };
        _renderForm = wallpaperForm;

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

        // Find WorkerW BEFORE creating the window handle
        var workerW = _desktopManager.FindDesktopWorkerWindow();
        if (workerW != IntPtr.Zero)
        {
            _logger.LogDebug("Found WorkerW: {WorkerW}, setting as parent BEFORE showing form", workerW);

            // CRITICAL: Set the parent in CreateParams BEFORE the handle is created
            wallpaperForm.SetCustomParent(workerW);
        }

        // NOW show the form - this will create the handle with WorkerW as parent from the start
        _renderForm.Show();
        _logger.LogDebug("Form shown with parent={Parent}, handle: {Handle}, size: {Size}",
            workerW, _renderForm.Handle, _renderForm.Size);

        // Process any pending messages to ensure form is fully initialized
        Application.DoEvents();

        if (workerW != IntPtr.Zero)
        {
            // Convert Screen.Bounds to System.Drawing.Rectangle for DesktopWindowManager
            var screenBounds = new System.Drawing.Rectangle(
                screen.Bounds.X,
                screen.Bounds.Y,
                screen.Bounds.Width,
                screen.Bounds.Height);

            // Still call SetAsWallpaperWindow for positioning and other setup
            _desktopManager.SetAsWallpaperWindow(_renderForm.Handle, screenBounds);
            _logger.LogInformation("Set as wallpaper window behind desktop icons");

            // CRITICAL: Enable wallpaper mode to prevent Windows Forms from resetting parent
            wallpaperForm.EnableWallpaperMode();
            _logger.LogInformation("Enabled wallpaper mode to lock parenting");
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

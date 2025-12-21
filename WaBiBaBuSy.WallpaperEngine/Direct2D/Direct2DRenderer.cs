using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Direct2D;

/// <summary>
/// Direct2D-based renderer that displays pre-composed bitmap frames.
/// Creates a dedicated rendering window parented to Progman (like standalone renderers).
/// This approach is required for Windows 11 24H2+ layered desktop mode.
/// </summary>
public class Direct2DRenderer : IDisposable
{
    private readonly ILogger<Direct2DRenderer> _logger;
    private readonly DesktopWindowManager _desktopWindowManager;
    private bool _disposed;

    // Rendering window and controls
    private Form? _renderForm;
    private PictureBox? _pictureBox;
    private bool _windowInitialized;

    // Frame buffer cache
    private readonly Dictionary<string, Bitmap> _previousFrames = new();

    public Direct2DRenderer(
        ILogger<Direct2DRenderer> logger,
        DesktopWindowManager desktopWindowManager)
    {
        _logger = logger;
        _desktopWindowManager = desktopWindowManager;
    }

    /// <summary>
    /// Initialize the rendering window for a specific screen.
    /// Must be called before DisplayFrame.
    /// </summary>
    public void Initialize(ScreenMapping screen)
    {
        if (_windowInitialized)
        {
            _logger.LogWarning("Direct2D renderer already initialized");
            return;
        }

        _logger.LogInformation("[Direct2D] Initializing render window for screen {Order}", screen.Order);

        // Create render form (similar to GIF/Video renderers)
        _renderForm = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = false,
            BackColor = Color.Black
        };

        // Set window bounds for the specific monitor
        _renderForm.Bounds = screen.ScreenBounds;

        _logger.LogInformation("[Direct2D] Render window set to: {Bounds}", screen.ScreenBounds);

        // Create PictureBox for frame display
        _pictureBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.StretchImage,
            BackColor = Color.Black
        };

        _renderForm.Controls.Add(_pictureBox);

        // Show the form to initialize its handle
        _renderForm.Show();

        _logger.LogDebug("[Direct2D] Form shown, handle: {Handle}", _renderForm.Handle);

        // Find WorkerW and set as wallpaper window
        var workerW = _desktopWindowManager.FindDesktopWorkerWindow();
        if (workerW != IntPtr.Zero)
        {
            _logger.LogInformation("[Direct2D] Found WorkerW: {WorkerW}, parenting render window", workerW);

            // Use the same SetAsWallpaperWindow that works for GIF/Video renderers
            _desktopWindowManager.SetAsWallpaperWindow(_renderForm.Handle, screen.ScreenBounds);
            _logger.LogInformation("[Direct2D] Render window parented to desktop successfully");

            // CRITICAL: Force the form to be visible after parenting
            // The SetAsWallpaperWindow may hide/show the form during style changes
            _renderForm.Visible = true;
            _renderForm.Invalidate();
            _renderForm.Update();
            _logger.LogInformation("[Direct2D] Forced form visibility and repaint after parenting");
        }
        else
        {
            _logger.LogWarning("[Direct2D] Could not find WorkerW window, wallpaper may not render behind icons");
        }

        _windowInitialized = true;
        _logger.LogInformation("[Direct2D] Render window initialized successfully");
    }

    /// <summary>
    /// Display a pre-composed frame on screen.
    /// Called by the composer after frame composition is complete.
    /// </summary>
    public void DisplayFrame(string clientId, Bitmap frame, ScreenMapping screen)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Direct2DRenderer));

        if (frame == null)
            throw new ArgumentNullException(nameof(frame));

        // Initialize window on first frame if not already done
        if (!_windowInitialized)
        {
            Initialize(screen);
        }

        try
        {
            _logger.LogTrace("[Direct2D] Displaying frame for screen {Order} (client {ClientId})", screen.Order, clientId);

            // Update the PictureBox image
            if (_pictureBox != null && _renderForm != null && !_renderForm.IsDisposed)
            {
                // Store previous frame for disposal
                var previousFrame = _previousFrames.TryGetValue(clientId, out var cached) ? cached : null;

                // Update on UI thread
                if (_renderForm.InvokeRequired)
                {
                    _renderForm.Invoke(() =>
                    {
                        _pictureBox.Image = frame;
                        // CRITICAL: Force immediate repaint - without this, WM_PAINT messages
                        // may queue up without being processed since we don't have a full message pump
                        _pictureBox.Refresh();
                        // Pump messages to ensure paint happens
                        Application.DoEvents();
                    });
                }
                else
                {
                    _pictureBox.Image = frame;
                    // CRITICAL: Force immediate repaint
                    _pictureBox.Refresh();
                    // Pump messages to ensure paint happens
                    Application.DoEvents();
                }

                // Cache current frame
                _previousFrames[clientId] = frame;

                // Dispose previous frame to avoid memory leak
                previousFrame?.Dispose();

                _logger.LogInformation("[Direct2D] Frame displayed on screen {Order} ({Width}x{Height})",
                    screen.Order, frame.Width, frame.Height);
            }
            else
            {
                _logger.LogWarning("[Direct2D] Cannot display frame - render form/pictureBox not available");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Direct2D] Error displaying frame for screen {Order}", screen.Order);
            throw;
        }
    }

    /// <summary>
    /// Display all frames from a composed set.
    /// </summary>
    public void DisplayFrames(Dictionary<string, Bitmap> frames, Dictionary<string, ScreenMapping> screenMappings)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Direct2DRenderer));

        if (frames == null || frames.Count == 0)
        {
            _logger.LogWarning("[Direct2D] No frames to display");
            return;
        }

        foreach (var (clientId, frame) in frames)
        {
            if (screenMappings.TryGetValue(clientId, out var screen))
            {
                DisplayFrame(clientId, frame, screen);
            }
            else
            {
                _logger.LogWarning("[Direct2D] Screen mapping not found for client {ClientId}", clientId);
                frame.Dispose();
            }
        }
    }

    /// <summary>
    /// Clear all cached frame buffers.
    /// </summary>
    public void ClearCache()
    {
        foreach (var frame in _previousFrames.Values)
        {
            frame?.Dispose();
        }
        _previousFrames.Clear();

        _logger.LogDebug("[Direct2D] Frame buffer cache cleared");
    }

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("[Direct2D] Disposing renderer");

        ClearCache();

        // Dispose picture box
        if (_pictureBox != null)
        {
            _pictureBox.Image = null;
            _pictureBox.Dispose();
            _pictureBox = null;
        }

        // Close and dispose render form
        if (_renderForm != null)
        {
            _renderForm.Close();
            _renderForm.Dispose();
            _renderForm = null;
        }

        _windowInitialized = false;
        _disposed = true;
        GC.SuppressFinalize(this);

        _logger.LogInformation("[Direct2D] Renderer disposed");
    }
}

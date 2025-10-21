using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Renderers;

/// <summary>
/// Renders received cross-screen frames to the desktop wallpaper
/// </summary>
public class CrossScreenFrameRenderer : IDisposable
{
    private readonly ILogger<CrossScreenFrameRenderer> _logger;
    private readonly DesktopWindowManager _desktopWindowManager;
    private System.Windows.Forms.Form? _wallpaperForm;
    private System.Windows.Forms.PictureBox? _pictureBox;
    private bool _isActive;
    private int _lastFrameNumber = -1;
    private readonly object _renderLock = new();

    // Performance metrics
    private readonly System.Diagnostics.Stopwatch _performanceTimer = new();
    private double _averageDecodeTimeMs;
    private double _averageRenderTimeMs;
    private int _frameCount;

    public CrossScreenFrameRenderer(
        ILogger<CrossScreenFrameRenderer> logger,
        DesktopWindowManager desktopWindowManager)
    {
        _logger = logger;
        _desktopWindowManager = desktopWindowManager;
    }

    /// <summary>
    /// Initialize the wallpaper form for cross-screen rendering
    /// </summary>
    public void Initialize()
    {
        if (_isActive)
        {
            _logger.LogWarning("CrossScreenFrameRenderer already initialized");
            return;
        }

        try
        {
            // Get screen bounds (assuming single screen for client)
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            var bounds = screen!.Bounds;

            _logger.LogInformation("Initializing cross-screen frame renderer for screen: {Width}x{Height}",
                bounds.Width, bounds.Height);

            // Create wallpaper form
            _wallpaperForm = new System.Windows.Forms.Form
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new Point(bounds.X, bounds.Y),
                Size = new Size(bounds.Width, bounds.Height),
                BackColor = Color.Black,
                ShowInTaskbar = false,
                TopMost = false
            };

            // Create picture box for displaying frames
            _pictureBox = new System.Windows.Forms.PictureBox
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };

            _wallpaperForm.Controls.Add(_pictureBox);
            _wallpaperForm.Show();

            // Set as wallpaper window using DesktopWindowManager
            var success = _desktopWindowManager.SetAsWallpaperWindow(_wallpaperForm.Handle, bounds);
            if (success)
            {
                _logger.LogInformation("Wallpaper form successfully set as desktop wallpaper");
            }
            else
            {
                _logger.LogError("Failed to set wallpaper form - frame rendering may not work correctly");
            }

            _isActive = true;
            _logger.LogInformation("Cross-screen frame renderer initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing cross-screen frame renderer");
            throw;
        }
    }

    /// <summary>
    /// Render a received cross-screen frame
    /// </summary>
    public void RenderFrame(CrossScreenFrame frame)
    {
        // Auto-initialize on first frame
        if (!_isActive)
        {
            _logger.LogInformation("Auto-initializing cross-screen frame renderer on first frame");
            Initialize();
        }

        if (_wallpaperForm == null || _pictureBox == null)
        {
            _logger.LogWarning("Cannot render frame - renderer initialization failed");
            return;
        }

        lock (_renderLock)
        {
            try
            {
                _performanceTimer.Restart();

                // Check for frame skipping
                if (_lastFrameNumber >= 0 && frame.FrameNumber != _lastFrameNumber + 1)
                {
                    _logger.LogWarning("Frame skip detected: expected {Expected}, received {Received}",
                        _lastFrameNumber + 1, frame.FrameNumber);
                }
                _lastFrameNumber = frame.FrameNumber;

                // Decode JPEG frame data
                using var ms = new MemoryStream(frame.FrameData.ToByteArray());
                var bitmap = new Bitmap(ms);

                var decodeTime = _performanceTimer.Elapsed.TotalMilliseconds;
                _performanceTimer.Restart();

                // Update picture box on UI thread
                if (_pictureBox.InvokeRequired)
                {
                    _pictureBox.Invoke(() =>
                    {
                        var oldImage = _pictureBox.Image;
                        _pictureBox.Image = bitmap;
                        oldImage?.Dispose();
                    });
                }
                else
                {
                    var oldImage = _pictureBox.Image;
                    _pictureBox.Image = bitmap;
                    oldImage?.Dispose();
                }

                var renderTime = _performanceTimer.Elapsed.TotalMilliseconds;
                _frameCount++;

                // Update performance metrics
                _averageDecodeTimeMs = (_averageDecodeTimeMs * (_frameCount - 1) + decodeTime) / _frameCount;
                _averageRenderTimeMs = (_averageRenderTimeMs * (_frameCount - 1) + renderTime) / _frameCount;

                _logger.LogTrace("Rendered frame {FrameNumber}: decode {DecodeMs:F2}ms, render {RenderMs:F2}ms",
                    frame.FrameNumber, decodeTime, renderTime);

                // Log performance every 100 frames
                if (_frameCount % 100 == 0)
                {
                    _logger.LogInformation("Performance: Frame {Count}, Avg decode: {DecodeMs:F2}ms, Avg render: {RenderMs:F2}ms",
                        _frameCount, _averageDecodeTimeMs, _averageRenderTimeMs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rendering frame {FrameNumber}", frame.FrameNumber);
            }
        }
    }

    /// <summary>
    /// Stop rendering and cleanup
    /// </summary>
    public void Stop()
    {
        if (!_isActive)
        {
            return;
        }

        _logger.LogInformation("Stopping cross-screen frame renderer. Total frames: {Count}, Avg decode: {DecodeMs:F2}ms, Avg render: {RenderMs:F2}ms",
            _frameCount, _averageDecodeTimeMs, _averageRenderTimeMs);

        _isActive = false;

        if (_wallpaperForm != null)
        {
            if (_wallpaperForm.InvokeRequired)
            {
                _wallpaperForm.Invoke(() =>
                {
                    _pictureBox?.Dispose();
                    _wallpaperForm.Close();
                    _wallpaperForm.Dispose();
                });
            }
            else
            {
                _pictureBox?.Dispose();
                _wallpaperForm.Close();
                _wallpaperForm.Dispose();
            }
        }

        _wallpaperForm = null;
        _pictureBox = null;
        _frameCount = 0;
        _lastFrameNumber = -1;
    }

    public void Dispose()
    {
        Stop();
    }
}

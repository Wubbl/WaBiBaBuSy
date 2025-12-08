using System.Drawing;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Direct2D;

/// <summary>
/// Direct2D-based renderer that displays pre-composed bitmap frames via GPU acceleration.
/// Receives frames directly from ComposerService and renders them to screen via WorkerW.
/// </summary>
public class Direct2DRenderer : IDisposable
{
    private readonly ILogger<Direct2DRenderer> _logger;
    private readonly DesktopWindowManager _desktopWindowManager;
    private bool _disposed;

    // Frame buffer cache (one per client/screen)
    private readonly Dictionary<string, IntPtr> _screenBuffers = new();
    private readonly Dictionary<string, Bitmap> _previousFrames = new();

    public Direct2DRenderer(
        ILogger<Direct2DRenderer> logger,
        DesktopWindowManager desktopWindowManager)
    {
        _logger = logger;
        _desktopWindowManager = desktopWindowManager;
    }

    /// <summary>
    /// Display a pre-composed frame on screen via GDI+.
    /// Called by the composer after frame composition is complete.
    /// </summary>
    public void DisplayFrame(string clientId, Bitmap frame, ScreenMapping screen)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Direct2DRenderer));

        if (frame == null)
            throw new ArgumentNullException(nameof(frame));

        try
        {
            _logger.LogDebug("Displaying frame for screen {Order} (client {ClientId})", screen.Order, clientId);

            // Get the WorkerW window
            var workerW = _desktopWindowManager.WorkerWHandle;
            if (workerW == IntPtr.Zero)
            {
                _logger.LogWarning("WorkerW window not found");
                return;
            }

            // Render the frame to the screen
            RenderFrameToScreen(workerW, frame, screen);

            // Cache the frame for potential reuse
            var previousFrame = _previousFrames.TryGetValue(clientId, out var cached) ? cached : null;
            _previousFrames[clientId] = frame;

            // Dispose previous frame to avoid memory leak
            previousFrame?.Dispose();

            _logger.LogTrace("Frame displayed successfully for screen {Order}", screen.Order);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error displaying frame for screen {Order}", screen.Order);
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
            _logger.LogWarning("No frames to display");
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
                _logger.LogWarning("Screen mapping not found for client {ClientId}", clientId);
                frame.Dispose();
            }
        }
    }

    /// <summary>
    /// Render frame to screen using GDI+ (will be upgraded to true Direct2D in Phase 2).
    /// For now, we use GDI+ which is sufficient for single-screen testing.
    /// </summary>
    private void RenderFrameToScreen(IntPtr workerW, Bitmap frame, ScreenMapping screen)
    {
        try
        {
            _logger.LogTrace("[GDI+] Getting device context for WorkerW={WorkerW}", workerW);
            var deviceContext = Direct2DInterop.GetDC(workerW);
            if (deviceContext == IntPtr.Zero)
            {
                _logger.LogError("[GDI+] Failed to get device context for WorkerW window (handle={WorkerW})", workerW);
                return;
            }

            _logger.LogTrace("[GDI+] Got device context={DeviceContext}, drawing frame {Width}x{Height} to screen bounds {Bounds}",
                deviceContext, frame.Width, frame.Height, screen.ScreenBounds);

            try
            {
                using (var graphics = Graphics.FromHdc(deviceContext))
                {
                    _logger.LogTrace("[GDI+] Created graphics object, clearing to black");
                    graphics.Clear(Color.Black);

                    // Draw the frame to fill the screen
                    _logger.LogTrace("[GDI+] Drawing frame to screen position (0,0) with size {Width}x{Height}",
                        screen.ScreenBounds.Width, screen.ScreenBounds.Height);
                    graphics.DrawImage(frame, 0, 0, screen.ScreenBounds.Width, screen.ScreenBounds.Height);

                    _logger.LogTrace("[GDI+] Frame drawn successfully");
                }
            }
            finally
            {
                Direct2DInterop.ReleaseDC(workerW, deviceContext);
                _logger.LogTrace("[GDI+] Released device context");
            }

            _logger.LogInformation("[GDI+] Frame rendered to screen {Order} via GDI+ ({Width}x{Height})",
                screen.Order, frame.Width, frame.Height);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GDI+] Error rendering frame to screen via GDI+");
            throw;
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

        foreach (var buffer in _screenBuffers.Values)
        {
            if (buffer != IntPtr.Zero)
            {
                // Buffer cleanup would go here for true Direct2D implementation
            }
        }
        _screenBuffers.Clear();

        _logger.LogDebug("Frame buffer cache cleared");
    }

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("Disposing Direct2D renderer");

        ClearCache();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

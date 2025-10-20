using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.WallpaperEngine.Composition;

/// <summary>
/// Renders the background layer for cross-screen wallpaper composition.
/// Supports solid colors, stretched images, and tiled images.
/// </summary>
public class BackgroundLayerRenderer : IDisposable
{
    private readonly ILogger<BackgroundLayerRenderer> _logger;
    private BackgroundLayerConfig? _config;
    private Image? _backgroundImage;
    private Color _solidColor;
    private bool _disposed;

    public BackgroundLayerRenderer(ILogger<BackgroundLayerRenderer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize the background renderer with configuration
    /// </summary>
    public async Task InitializeAsync(BackgroundLayerConfig config)
    {
        _logger.LogInformation("Initializing background layer renderer: Mode={Mode}", config.Mode);
        _config = config;

        // Parse solid color
        _solidColor = ParseColorFromHex(config.ColorHex);

        // Load background image if needed
        if ((config.Mode == BackgroundMode.StretchedImage || config.Mode == BackgroundMode.TiledImage)
            && !string.IsNullOrEmpty(config.ImagePath))
        {
            await LoadBackgroundImageAsync(config.ImagePath);
        }

        _logger.LogInformation("Background layer renderer initialized successfully");
    }

    /// <summary>
    /// Render the background layer for a specific screen
    /// </summary>
    public Bitmap RenderForScreen(ScreenMapping screen)
    {
        if (_config == null)
            throw new InvalidOperationException("Renderer not initialized. Call InitializeAsync first.");

        _logger.LogDebug("Rendering background for screen {Order}: {Width}x{Height}",
            screen.Order, screen.ScreenBounds.Width, screen.ScreenBounds.Height);

        // Create bitmap matching screen resolution
        var bitmap = new Bitmap(screen.ScreenBounds.Width, screen.ScreenBounds.Height);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            // Set high-quality rendering
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            switch (_config.Mode)
            {
                case BackgroundMode.SolidColor:
                    RenderSolidColor(graphics, screen);
                    break;

                case BackgroundMode.StretchedImage:
                    RenderStretchedImage(graphics, screen);
                    break;

                case BackgroundMode.TiledImage:
                    RenderTiledImage(graphics, screen);
                    break;

                default:
                    _logger.LogWarning("Unknown background mode: {Mode}, falling back to solid color", _config.Mode);
                    RenderSolidColor(graphics, screen);
                    break;
            }
        }

        return bitmap;
    }

    private void RenderSolidColor(Graphics graphics, ScreenMapping screen)
    {
        using var brush = new SolidBrush(_solidColor);
        graphics.FillRectangle(brush, 0, 0, screen.ScreenBounds.Width, screen.ScreenBounds.Height);
    }

    private void RenderStretchedImage(Graphics graphics, ScreenMapping screen)
    {
        if (_backgroundImage == null)
        {
            _logger.LogWarning("Background image not loaded, falling back to solid color");
            RenderSolidColor(graphics, screen);
            return;
        }

        // Calculate which portion of the stretched image should be visible on this screen
        // The image is stretched across the entire virtual canvas

        var virtualCanvas = screen.VirtualBounds;

        // Calculate the source rectangle from the background image
        // Map the screen's virtual position to the background image coordinates
        float scaleX = (float)_backgroundImage.Width / virtualCanvas.Width;
        float scaleY = (float)_backgroundImage.Height / virtualCanvas.Height;

        var sourceX = (int)(screen.VirtualBounds.X * scaleX);
        var sourceY = (int)(screen.VirtualBounds.Y * scaleY);
        var sourceWidth = (int)(screen.ScreenBounds.Width * scaleX);
        var sourceHeight = (int)(screen.ScreenBounds.Height * scaleY);

        // Clamp to image bounds
        sourceX = Math.Max(0, Math.Min(sourceX, _backgroundImage.Width - 1));
        sourceY = Math.Max(0, Math.Min(sourceY, _backgroundImage.Height - 1));
        sourceWidth = Math.Min(sourceWidth, _backgroundImage.Width - sourceX);
        sourceHeight = Math.Min(sourceHeight, _backgroundImage.Height - sourceY);

        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            _logger.LogWarning("Invalid source dimensions for stretched image, filling with solid color");
            RenderSolidColor(graphics, screen);
            return;
        }

        var sourceRect = new Rectangle(sourceX, sourceY, sourceWidth, sourceHeight);
        var destRect = new Rectangle(0, 0, screen.ScreenBounds.Width, screen.ScreenBounds.Height);

        graphics.DrawImage(_backgroundImage, destRect, sourceRect, GraphicsUnit.Pixel);
    }

    private void RenderTiledImage(Graphics graphics, ScreenMapping screen)
    {
        if (_backgroundImage == null)
        {
            _logger.LogWarning("Background image not loaded, falling back to solid color");
            RenderSolidColor(graphics, screen);
            return;
        }

        // Create a texture brush from the background image
        using var textureBrush = new TextureBrush(_backgroundImage);

        // Offset the brush based on the screen's virtual position
        // This ensures the tiling pattern is continuous across screens
        textureBrush.TranslateTransform(-screen.VirtualBounds.X, -screen.VirtualBounds.Y);

        graphics.FillRectangle(textureBrush, 0, 0, screen.ScreenBounds.Width, screen.ScreenBounds.Height);
    }

    private async Task LoadBackgroundImageAsync(string imagePath)
    {
        try
        {
            _logger.LogInformation("Loading background image: {ImagePath}", imagePath);

            if (!File.Exists(imagePath))
            {
                _logger.LogError("Background image file not found: {ImagePath}", imagePath);
                return;
            }

            // Load image asynchronously
            await Task.Run(() =>
            {
                _backgroundImage?.Dispose();
                _backgroundImage = Image.FromFile(imagePath);
            });

            if (_backgroundImage != null)
            {
                _logger.LogInformation("Background image loaded: {Width}x{Height}",
                    _backgroundImage.Width, _backgroundImage.Height);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load background image: {ImagePath}", imagePath);
            _backgroundImage = null;
        }
    }

    private Color ParseColorFromHex(string hexColor)
    {
        try
        {
            // Remove '#' if present
            hexColor = hexColor.TrimStart('#');

            // Parse RGB or RGBA
            if (hexColor.Length == 6)
            {
                var r = Convert.ToByte(hexColor.Substring(0, 2), 16);
                var g = Convert.ToByte(hexColor.Substring(2, 2), 16);
                var b = Convert.ToByte(hexColor.Substring(4, 2), 16);
                return Color.FromArgb(r, g, b);
            }
            else if (hexColor.Length == 8)
            {
                var a = Convert.ToByte(hexColor.Substring(0, 2), 16);
                var r = Convert.ToByte(hexColor.Substring(2, 2), 16);
                var g = Convert.ToByte(hexColor.Substring(4, 2), 16);
                var b = Convert.ToByte(hexColor.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
            else
            {
                _logger.LogWarning("Invalid hex color format: {Hex}, defaulting to black", hexColor);
                return Color.Black;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse hex color: {Hex}, defaulting to black", hexColor);
            return Color.Black;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("Disposing background layer renderer");

        _backgroundImage?.Dispose();
        _backgroundImage = null;

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

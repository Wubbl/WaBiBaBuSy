using System.Drawing;
using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.WallpaperEngine.Composition;

/// <summary>
/// Manages the virtual canvas coordinate space that spans multiple physical screens.
/// Calculates screen positions and handles coordinate transformations.
/// </summary>
public class VirtualCanvasManager
{
    private readonly ILogger<VirtualCanvasManager> _logger;
    private Rectangle _virtualBounds;
    private List<ScreenMapping> _screenMappings = new();

    /// <summary>
    /// Total bounds of the virtual canvas spanning all screens
    /// </summary>
    public Rectangle VirtualBounds => _virtualBounds;

    /// <summary>
    /// List of screen mappings ordered by their position in the chain
    /// </summary>
    public IReadOnlyList<ScreenMapping> ScreenMappings => _screenMappings.AsReadOnly();

    /// <summary>
    /// Number of screens in the virtual canvas
    /// </summary>
    public int ScreenCount => _screenMappings.Count;

    public VirtualCanvasManager(ILogger<VirtualCanvasManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculate the virtual canvas layout from a list of screen configurations.
    /// Screens are arranged horizontally (left to right) based on their Order property.
    /// </summary>
    /// <param name="screens">Screen configurations with resolution and order information</param>
    /// Each screen's <see cref="ScreenConfiguration.PixelsPerCm"/> and
    /// <see cref="ScreenConfiguration.PhysicalDistanceCm"/> are used to insert gap pixels
    /// between screens when both are non-zero, so the animation visually travels through
    /// real-world space between monitors.
    public void CalculateLayout(IEnumerable<ScreenConfiguration> screens)
    {
        _logger.LogInformation("Calculating virtual canvas layout");

        var screenList = screens.OrderBy(s => s.Order).ToList();

        if (screenList.Count == 0)
        {
            _logger.LogWarning("No screens provided for layout calculation");
            _virtualBounds = Rectangle.Empty;
            _screenMappings.Clear();
            return;
        }

        var mappings = new List<ScreenMapping>();
        int currentX = 0;
        int maxHeight = 0;
        bool isFirst = true;

        foreach (var screen in screenList)
        {
            // Validate screen bounds
            if (screen.Width <= 0 || screen.Height <= 0)
            {
                _logger.LogWarning("Invalid screen dimensions for {ClientId}: {Width}x{Height}, skipping",
                    screen.ClientId, screen.Width, screen.Height);
                continue;
            }

            // Insert gap pixels before this screen (all screens except the first)
            if (!isFirst && screen.PixelsPerCm > 0 && screen.PhysicalDistanceCm > 0)
            {
                int gapPx = (int)(screen.PhysicalDistanceCm * screen.PixelsPerCm);
                currentX += gapPx;
                _logger.LogInformation("  Gap before {ClientId}: {DistanceCm}cm × {PixelsPerCm:F1}px/cm = {GapPx}px",
                    screen.ClientId, screen.PhysicalDistanceCm, screen.PixelsPerCm, gapPx);
            }

            // Physical screen bounds (local coordinates on the client)
            var screenBounds = new Rectangle(0, 0, screen.Width, screen.Height);

            // Virtual bounds (position in the global canvas)
            var virtualBounds = new Rectangle(currentX, 0, screen.Width, screen.Height);

            var mapping = new ScreenMapping
            {
                ClientId = screen.ClientId,
                ScreenBounds = screenBounds,
                VirtualBounds = virtualBounds,
                Order = screen.Order,
                PhysicalDistanceCm = screen.PhysicalDistanceCm,
                Hostname = screen.Hostname,
                MonitorIndex = screen.MonitorIndex
            };

            mappings.Add(mapping);

            _logger.LogInformation("Screen {Order} ({ClientId}): Resolution={Width}x{Height}, VirtualX={VirtualX}, Distance={Distance}cm",
                mapping.Order,
                mapping.ClientId,
                screen.Width,
                screen.Height,
                currentX,
                screen.PhysicalDistanceCm);

            // Advance X position for next screen
            currentX += screen.Width;
            isFirst = false;

            // Track maximum height
            if (screen.Height > maxHeight)
                maxHeight = screen.Height;
        }

        // Second pass: vertically center each screen inside the canvas height so a 1080-px node
        // and a 1440-px node agree on where "canvas center" is. Players subtract VirtualBounds.Y
        // exactly like they subtract VirtualBounds.X. (Roadmap Tier 0.1 - mixed monitor heights.)
        for (int i = 0; i < mappings.Count; i++)
        {
            var m = mappings[i];
            int offsetY = (maxHeight - m.ScreenBounds.Height) / 2;
            if (offsetY == 0) continue;
            mappings[i] = new ScreenMapping
            {
                ClientId = m.ClientId,
                ScreenBounds = m.ScreenBounds,
                VirtualBounds = new Rectangle(m.VirtualBounds.X, offsetY, m.VirtualBounds.Width, m.VirtualBounds.Height),
                Order = m.Order,
                PhysicalDistanceCm = m.PhysicalDistanceCm,
                Hostname = m.Hostname,
                MonitorIndex = m.MonitorIndex
            };
        }

        _screenMappings = mappings;

        // Calculate total virtual canvas bounds
        _virtualBounds = new Rectangle(0, 0, currentX, maxHeight);

        _logger.LogInformation("Virtual canvas calculated: {Width}x{Height} px, spanning {ScreenCount} screens",
            _virtualBounds.Width,
            _virtualBounds.Height,
            _screenMappings.Count);
    }

    /// <summary>
    /// Get the screen mapping that contains a specific point in virtual coordinates
    /// </summary>
    public ScreenMapping? GetScreenAtVirtualPosition(int x, int y)
    {
        return _screenMappings.FirstOrDefault(m => m.ContainsVirtualPoint(x, y));
    }

    /// <summary>
    /// Get the screen mapping by client ID
    /// </summary>
    public ScreenMapping? GetScreenByClientId(string clientId)
    {
        return _screenMappings.FirstOrDefault(m => m.ClientId == clientId);
    }

    /// <summary>
    /// Get the screen mapping by order index
    /// </summary>
    public ScreenMapping? GetScreenByOrder(int order)
    {
        return _screenMappings.FirstOrDefault(m => m.Order == order);
    }

    /// <summary>
    /// Calculate which screens are visible for a given rectangular region in virtual coordinates
    /// </summary>
    public IEnumerable<ScreenMapping> GetVisibleScreens(Rectangle virtualRegion)
    {
        foreach (var screen in _screenMappings)
        {
            if (screen.VirtualBounds.IntersectsWith(virtualRegion))
                yield return screen;
        }
    }

    /// <summary>
    /// Calculate the visible portion of a virtual region on a specific screen.
    /// Returns the intersection rectangle in screen-local coordinates.
    /// </summary>
    public Rectangle? GetVisibleRegionOnScreen(ScreenMapping screen, Rectangle virtualRegion)
    {
        // Check if regions intersect
        if (!screen.VirtualBounds.IntersectsWith(virtualRegion))
            return null;

        // Calculate intersection in virtual coordinates
        var intersection = Rectangle.Intersect(screen.VirtualBounds, virtualRegion);

        if (intersection.IsEmpty)
            return null;

        // Convert to screen-local coordinates
        var localX = intersection.X - screen.VirtualBounds.X;
        var localY = intersection.Y - screen.VirtualBounds.Y;

        return new Rectangle(localX, localY, intersection.Width, intersection.Height);
    }

    /// <summary>
    /// Clear all screen mappings (useful for reset/reconfiguration)
    /// </summary>
    public void Clear()
    {
        _logger.LogInformation("Clearing virtual canvas layout");
        _screenMappings.Clear();
        _virtualBounds = Rectangle.Empty;
    }
}

/// <summary>
/// Configuration data for a single screen
/// </summary>
public class ScreenConfiguration
{
    public required string ClientId { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Order { get; init; }
    public int PhysicalDistanceCm { get; init; }
    public string? Hostname { get; init; }
    public int MonitorIndex { get; init; }
    /// <summary>
    /// Physical pixels per centimeter for this screen, derived from monitor DPI.
    /// When > 0 and PhysicalDistanceCm > 0, gap pixels are inserted before this screen
    /// in the virtual canvas so animation travels through real-world space between monitors.
    /// Set to 0 to treat screens as edge-to-edge.
    /// </summary>
    public float PixelsPerCm { get; init; }
}

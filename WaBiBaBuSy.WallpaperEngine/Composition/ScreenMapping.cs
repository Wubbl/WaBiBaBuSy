using System.Drawing;

namespace WaBiBaBuSy.WallpaperEngine.Composition;

/// <summary>
/// Maps a physical screen to its position in the virtual canvas coordinate space.
/// </summary>
public class ScreenMapping
{
    /// <summary>
    /// Unique identifier for the client/screen
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Physical screen resolution (actual pixel dimensions)
    /// </summary>
    public required Rectangle ScreenBounds { get; init; }

    /// <summary>
    /// Position and size in the virtual canvas coordinate space
    /// </summary>
    public required Rectangle VirtualBounds { get; init; }

    /// <summary>
    /// Display order in the screen chain (0-based, left to right)
    /// </summary>
    public required int Order { get; init; }

    /// <summary>
    /// Physical distance in centimeters from the previous screen in the chain.
    /// Used to calculate animation delays for natural movement.
    /// </summary>
    public int PhysicalDistanceCm { get; init; }

    /// <summary>
    /// Hostname of the client machine
    /// </summary>
    public string? Hostname { get; init; }

    /// <summary>
    /// Monitor index on the client machine (for multi-monitor clients)
    /// </summary>
    public int MonitorIndex { get; init; } = 0;

    /// <summary>
    /// Check if a point in virtual canvas coordinates is within this screen's virtual bounds
    /// </summary>
    public bool ContainsVirtualPoint(int x, int y)
    {
        return VirtualBounds.Contains(x, y);
    }

    /// <summary>
    /// Convert a point from virtual canvas coordinates to local screen coordinates
    /// </summary>
    public Point VirtualToLocal(Point virtualPoint)
    {
        var localX = virtualPoint.X - VirtualBounds.X;
        var localY = virtualPoint.Y - VirtualBounds.Y;
        return new Point(localX, localY);
    }

    /// <summary>
    /// Convert a point from local screen coordinates to virtual canvas coordinates
    /// </summary>
    public Point LocalToVirtual(Point localPoint)
    {
        var virtualX = localPoint.X + VirtualBounds.X;
        var virtualY = localPoint.Y + VirtualBounds.Y;
        return new Point(virtualX, virtualY);
    }

    public override string ToString()
    {
        return $"Screen {Order}: {ClientId} | Physical: {ScreenBounds.Width}x{ScreenBounds.Height} | Virtual: ({VirtualBounds.X}, {VirtualBounds.Y}) {VirtualBounds.Width}x{VirtualBounds.Height}";
    }
}

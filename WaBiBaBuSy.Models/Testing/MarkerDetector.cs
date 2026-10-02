namespace WaBiBaBuSy.Models.Testing;

/// <summary>Which way the marker's off-center dot points.</summary>
public enum MarkerOrientation
{
    /// <summary>Too few dot pixels to tell (dot cut off, or not drawn): never a failure on its own.</summary>
    Unknown,
    /// <summary>Dot right of center: the asset as drawn.</summary>
    Normal,
    /// <summary>Dot left of center: the sprite was drawn mirrored.</summary>
    Mirrored,
}

/// <summary>Where the marker's magenta area was found (device px) and which way its dot points.</summary>
public sealed class MarkerDetection
{
    /// <summary>At least <see cref="MarkerDetector.MinPixels"/> magenta pixels were found.</summary>
    public bool Found { get; set; }
    /// <summary>Left edge of the magenta bounding box, device px.</summary>
    public float X { get; set; }
    /// <summary>Top edge of the magenta bounding box, device px.</summary>
    public float Y { get; set; }
    /// <summary>Bounding box width, device px.</summary>
    public float Width { get; set; }
    /// <summary>Bounding box height, device px.</summary>
    public float Height { get; set; }
    /// <summary>Horizontal center of the box (= the sprite's center: the black border is symmetric).</summary>
    public float CenterX => X + Width / 2f;
    /// <summary>Vertical center of the box.</summary>
    public float CenterY => Y + Height / 2f;
    /// <summary>Which way the dot points; Unknown when it could not be seen.</summary>
    public MarkerOrientation Orientation { get; set; }
    /// <summary>Magenta pixels counted on the whole surface (also set when not found).</summary>
    public int PixelCount { get; set; }
    /// <summary>The magenta box touches the surface edge (marker partly off-screen).</summary>
    public bool TouchesEdge { get; set; }
}

/// <summary>Finds the test marker (magenta square with an off-center white dot) in a BGRA buffer.</summary>
public static class MarkerDetector
{
    /// <summary>Fewer magenta pixels than this = not found (noise, anti-aliasing).</summary>
    public const int MinPixels = 64;

    /// <summary>The marker's fill color, with headroom for scaling / color-management drift.</summary>
    public static bool IsMagenta(byte r, byte g, byte b) => r >= 200 && b >= 200 && g <= 80;
    private static bool IsWhite(byte r, byte g, byte b) => r >= 220 && g >= 220 && b >= 220;

    /// <summary>
    /// Bounding box of every magenta pixel and the dot's side within it. <paramref name="bgra"/> is 32-bit
    /// BGRA, top-down, <paramref name="stride"/> bytes per row; throws <see cref="ArgumentException"/> when
    /// it is too small for <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    public static MarkerDetection Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        BgraBuffer.Require(bgra, width, height, stride, nameof(bgra));
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                if (!IsMagenta(bgra[i + 2], bgra[i + 1], bgra[i])) continue;
                count++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (count < MinPixels) return new MarkerDetection { Found = false, PixelCount = count };

        long sumX = 0;
        int dots = 0;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                int i = y * stride + x * 4;
                if (IsWhite(bgra[i + 2], bgra[i + 1], bgra[i])) { sumX += x; dots++; }
            }

        var orientation = MarkerOrientation.Unknown;
        if (dots >= 4)
            orientation = (double)sumX / dots > (minX + maxX) / 2.0 ? MarkerOrientation.Normal : MarkerOrientation.Mirrored;

        return new MarkerDetection
        {
            Found = true,
            X = minX,
            Y = minY,
            Width = maxX - minX + 1,
            Height = maxY - minY + 1,
            Orientation = orientation,
            PixelCount = count,
            TouchesEdge = minX == 0 || minY == 0 || maxX == width - 1 || maxY == height - 1,
        };
    }
}

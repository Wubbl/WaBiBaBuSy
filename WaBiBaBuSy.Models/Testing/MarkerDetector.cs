namespace WaBiBaBuSy.Models.Testing;

public enum MarkerOrientation { Unknown, Normal, Mirrored }

/// <summary>Where the marker's magenta area was found (device px) and which way its dot points.</summary>
public sealed class MarkerDetection
{
    public bool Found { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float CenterX => X + Width / 2f;
    public float CenterY => Y + Height / 2f;
    public MarkerOrientation Orientation { get; set; }
    public int PixelCount { get; set; }
    /// <summary>The magenta box touches the surface edge (marker partly off-screen).</summary>
    public bool TouchesEdge { get; set; }
}

/// <summary>Finds the test marker (magenta square with an off-center white dot) in a BGRA buffer.</summary>
public static class MarkerDetector
{
    /// <summary>Fewer magenta pixels than this = not found (noise, anti-aliasing).</summary>
    public const int MinPixels = 64;

    public static bool IsMagenta(byte r, byte g, byte b) => r >= 200 && b >= 200 && g <= 80;
    private static bool IsWhite(byte r, byte g, byte b) => r >= 220 && g >= 220 && b >= 220;

    public static MarkerDetection Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
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

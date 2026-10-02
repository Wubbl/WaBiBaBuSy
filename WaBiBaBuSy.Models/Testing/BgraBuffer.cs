namespace WaBiBaBuSy.Models.Testing;

/// <summary>Geometry guard shared by the image checks (32-bit BGRA, top-down rows of <c>stride</c> bytes).</summary>
internal static class BgraBuffer
{
    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="bgra"/> holds <paramref name="height"/> rows of <paramref name="width"/> pixels.</summary>
    public static void Require(ReadOnlySpan<byte> bgra, int width, int height, int stride, string paramName)
    {
        if (width < 0 || height < 0)
            throw new ArgumentException($"negative image size {width}×{height}", paramName);
        if (width == 0 || height == 0) return;
        if (stride < width * 4)
            throw new ArgumentException($"stride {stride} is shorter than a row of {width} BGRA pixels", paramName);
        long needed = (long)(height - 1) * stride + (long)width * 4;   // the last row needs no padding
        if (bgra.Length < needed)
            throw new ArgumentException($"buffer has {bgra.Length} bytes, {width}×{height} at stride {stride} needs {needed}", paramName);
    }
}

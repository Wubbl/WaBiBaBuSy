namespace WaBiBaBuSy.Models.Testing;

public sealed class PixelDiffResult
{
    public bool SizeMismatch { get; set; }
    public int DiffPixels { get; set; }
    public double DiffPct { get; set; }
    /// <summary>BGRA, same size as the inputs: differing pixels red, others a dimmed grey of the first image.</summary>
    public byte[]? DiffImage { get; set; }
}

/// <summary>Exact-frame parity: compares B, G, R per pixel (alpha ignored — the swap chain alpha mode is Ignore).</summary>
public static class PixelDiff
{
    public static PixelDiffResult Compare(
        ReadOnlySpan<byte> a, int aw, int ah, int aStride,
        ReadOnlySpan<byte> b, int bw, int bh, int bStride,
        int channelTolerance = 2)
    {
        if (aw != bw || ah != bh) return new PixelDiffResult { SizeMismatch = true, DiffPct = 100 };

        var diff = new byte[aw * ah * 4];
        int count = 0;
        for (int y = 0; y < ah; y++)
            for (int x = 0; x < aw; x++)
            {
                int ia = y * aStride + x * 4, ib = y * bStride + x * 4, id = (y * aw + x) * 4;
                bool differs = Math.Abs(a[ia] - b[ib]) > channelTolerance
                    || Math.Abs(a[ia + 1] - b[ib + 1]) > channelTolerance
                    || Math.Abs(a[ia + 2] - b[ib + 2]) > channelTolerance;
                if (differs)
                {
                    count++;
                    diff[id + 2] = 255;   // red
                }
                else
                {
                    byte grey = (byte)((a[ia] + a[ia + 1] + a[ia + 2]) / 9);
                    diff[id] = grey; diff[id + 1] = grey; diff[id + 2] = grey;
                }
                diff[id + 3] = 255;
            }
        return new PixelDiffResult { DiffPixels = count, DiffPct = count * 100.0 / (aw * ah), DiffImage = diff };
    }
}

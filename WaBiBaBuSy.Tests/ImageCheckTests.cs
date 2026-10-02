using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ImageCheckTests
{
    private const int W = 400, H = 300, Stride = W * 4;

    private static byte[] Surface(byte r = 30, byte g = 30, byte b = 30)
    {
        var px = new byte[Stride * H];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        return px;
    }

    private static void Fill(byte[] px, int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(H, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(W, x0 + w); x++)
            {
                int i = y * Stride + x * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
            }
    }

    /// <summary>The 64×64 marker asset, top-left at (x, y); mirrored draws the dot on the left.</summary>
    private static void Marker(byte[] px, int x, int y, bool mirrored = false)
    {
        Fill(px, x, y, 64, 64, 0, 0, 0);
        Fill(px, x + 4, y + 4, 56, 56, 255, 0, 255);
        Fill(px, mirrored ? x + 12 : x + 40, y + 26, 12, 12, 255, 255, 255);
    }

    [Fact]
    public void Detect_FindsCenterAndOrientation()
    {
        var px = Surface();
        Marker(px, 100, 50);
        var d = MarkerDetector.Detect(px, W, H, Stride);
        Assert.True(d.Found);
        Assert.Equal(132f, d.CenterX, 2);   // magenta box x 104..159 (56 px) → center 132 = center of the 100..163 sprite
        Assert.Equal(82f, d.CenterY, 2);    // y 54..109
        Assert.Equal(MarkerOrientation.Normal, d.Orientation);
        Assert.False(d.TouchesEdge);
    }

    [Fact]
    public void Detect_Mirrored() =>
        Assert.Equal(MarkerOrientation.Mirrored, MarkerDetector.Detect(Marker2(mirrored: true), W, H, Stride).Orientation);

    private static byte[] Marker2(bool mirrored) { var px = Surface(); Marker(px, 100, 50, mirrored); return px; }

    [Fact]
    public void Detect_NoMarker_NotFound() => Assert.False(MarkerDetector.Detect(Surface(), W, H, Stride).Found);

    [Fact]
    public void Detect_CutAtTheEdge_TouchesEdge()
    {
        var px = Surface();
        Marker(px, W - 30, 50);
        var d = MarkerDetector.Detect(px, W, H, Stride);
        Assert.True(d.Found);
        Assert.True(d.TouchesEdge);
    }

    [Fact]
    public void PixelDiff_Identical_IsZero()
    {
        var r = PixelDiff.Compare(Surface(), W, H, Stride, Surface(), W, H, Stride);
        Assert.Equal(0, r.DiffPixels);
        Assert.Equal(0, r.DiffPct);
        Assert.Equal(Stride * H, r.DiffImage!.Length);
    }

    [Fact]
    public void PixelDiff_CountsBeyondToleranceOnly_IgnoresAlpha()
    {
        var a = Surface();
        var b = Surface();
        b[0] = (byte)(b[0] + 3);          // pixel 0: blue +3 → differs
        b[4 + 1] = (byte)(b[4 + 1] + 2);  // pixel 1: green +2 → within tolerance
        b[8 + 3] = 0;                     // pixel 2: alpha only → ignored (swap chain alpha is "Ignore")
        var r = PixelDiff.Compare(a, W, H, Stride, b, W, H, Stride);
        Assert.Equal(1, r.DiffPixels);
        Assert.Equal(100.0 / (W * H), r.DiffPct, 6);
    }

    [Fact]
    public void PixelDiff_SizeMismatch()
    {
        var r = PixelDiff.Compare(Surface(), W, H, Stride, new byte[8], 2, 1, 8);
        Assert.True(r.SizeMismatch);
        Assert.Equal(100, r.DiffPct);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(123456u)]
    [InlineData(uint.MaxValue)]
    public void Timecode_RoundTrips(uint value)
    {
        var px = Surface();
        var cells = TimecodeStrip.Encode(value);
        var (ox, oy) = TimecodeStrip.Origin(H);
        for (int i = 0; i < cells.Length; i++)
        {
            byte c = cells[i] ? (byte)255 : (byte)0;
            Fill(px, ox + i * TimecodeStrip.CellPx, oy, TimecodeStrip.CellPx, TimecodeStrip.CellPx, c, c, c);
        }
        Assert.Equal(value, TimecodeStrip.Decode(px, W, H, Stride));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0xA5C3_0F01u)]
    [InlineData(uint.MaxValue)]
    public void Timecode_SpanEncode_MatchesArrayEncode(uint value)
    {
        Span<bool> cells = stackalloc bool[TimecodeStrip.Cells];
        TimecodeStrip.Encode(value, cells);
        Assert.Equal(TimecodeStrip.Encode(value), cells.ToArray());
    }

    [Fact]
    public void Timecode_SpanEncode_TooShort_Throws() =>
        Assert.Throws<ArgumentException>(() => TimecodeStrip.Encode(1u, new bool[TimecodeStrip.Cells - 1]));

    [Fact]
    public void Timecode_NoStrip_DecodesToNull() => Assert.Null(TimecodeStrip.Decode(Surface(), W, H, Stride));

    [Fact]
    public void Timecode_NegativeElapsed_WrapsInsteadOfThrowing() => Assert.Equal(uint.MaxValue, TimecodeStrip.ToCode(-1));

    // ── PositionCheck ──────────────────────────────────────────────────────

    private static NodeProbeSample Sample(float animX = 928, float animY = 508, bool flipped = false) => new()
    {
        NodeId = "a", NodeName = "pc-01", PlayerAnimX = animX, PlayerAnimY = animY,
        PlayerAnimWidth = 64, PlayerAnimHeight = 64, PlayerFlipped = flipped,
    };

    private static MarkerExpectation Expect(MarkerVisibility v = MarkerVisibility.Visible) =>
        new() { Visibility = v, X = 928, Y = 508, Width = 64, Height = 64 };

    private static MarkerDetection Detected(float cx, float cy, MarkerOrientation o = MarkerOrientation.Normal) =>
        new() { Found = true, X = cx - 28, Y = cy - 28, Width = 56, Height = 56, Orientation = o };

    [Fact]
    public void Position_OnTarget_Pass()
    {
        var r = PositionCheck.Evaluate(Sample(), Expect(), Detected(960, 540), 1f, 3);
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.Equal(0, r.ErrorPx, 3);
    }

    [Fact]
    public void Position_TenPixelsOff_Fail()
    {
        var r = PositionCheck.Evaluate(Sample(), Expect(), Detected(970, 540), 1f, 3);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Equal(10, r.ErrorPx, 3);
        Assert.Equal(0, r.PlayerErrorPx, 3);   // player math agreed → a drawing problem, not a math problem
    }

    [Fact]
    public void Position_NotFound_Fail() =>
        Assert.Equal(Verdict.Fail, PositionCheck.Evaluate(Sample(), Expect(), new MarkerDetection { Found = false }, 1f, 3).Verdict);

    [Fact]
    public void PartialMarker_Skipped() =>
        Assert.Equal(Verdict.Skipped, PositionCheck.Evaluate(Sample(), Expect(MarkerVisibility.Partial), Detected(10, 10), 1f, 3).Verdict);

    [Fact]
    public void OffScreenButFound_Fail() =>
        Assert.Equal(Verdict.Fail, PositionCheck.Evaluate(Sample(), Expect(MarkerVisibility.OffScreen), Detected(960, 540), 1f, 3).Verdict);

    [Fact]
    public void NotStarted_NothingVisible_Skipped() =>
        Assert.Equal(Verdict.Skipped, PositionCheck.Evaluate(Sample(), Expect(MarkerVisibility.NotStarted), new MarkerDetection(), 1f, 3).Verdict);

    [Fact]
    public void Detect_NoDot_OrientationUnknown()
    {
        var px = Surface();
        Fill(px, 100, 50, 56, 56, 255, 0, 255);   // magenta square without the white dot
        var d = MarkerDetector.Detect(px, W, H, Stride);
        Assert.True(d.Found);
        Assert.Equal(MarkerOrientation.Unknown, d.Orientation);
    }

    [Fact]
    public void Detect_BelowMinPixels_NotFound_ButCounted()
    {
        var px = Surface();
        Fill(px, 100, 50, 7, 9, 255, 0, 255);     // 63 magenta pixels
        var below = MarkerDetector.Detect(px, W, H, Stride);
        Assert.False(below.Found);
        Assert.Equal(MarkerDetector.MinPixels - 1, below.PixelCount);

        Fill(px, 107, 50, 1, 1, 255, 0, 255);     // the 64th
        Assert.True(MarkerDetector.Detect(px, W, H, Stride).Found);
    }

    [Fact]
    public void Detect_BufferTooSmall_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => MarkerDetector.Detect(new byte[Stride * H - 1], W, H, Stride));
        Assert.Throws<ArgumentException>(() => MarkerDetector.Detect(new byte[Stride * H], W, H, W * 4 - 1));   // stride shorter than a row
    }

    [Fact]
    public void Detect_LastRowWithoutPadding_IsEnough()
    {
        // A padded stride only needs width × 4 bytes in the last row.
        const int padded = Stride + 16;
        var d = MarkerDetector.Detect(new byte[padded * (H - 1) + W * 4], W, H, padded);
        Assert.False(d.Found);
    }

    [Fact]
    public void PixelDiff_BufferTooSmall_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => PixelDiff.Compare(Surface(), W, H, Stride, new byte[16], W, H, Stride));
        Assert.Throws<ArgumentException>(() => PixelDiff.Compare(new byte[16], W, H, Stride, Surface(), W, H, Stride));
    }

    [Fact]
    public void Timecode_BufferTooSmall_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() => TimecodeStrip.Decode(new byte[16], W, H, Stride));

    [Fact]
    public void OffScreen_NoCapture_Skipped_LikeVisibleWithoutCapture()
    {
        var off = PositionCheck.Evaluate(Sample(), Expect(MarkerVisibility.OffScreen), null, 1f, 3);
        var visible = PositionCheck.Evaluate(Sample(), Expect(), null, 1f, 3);
        Assert.Equal(Verdict.Skipped, off.Verdict);
        Assert.Equal(Verdict.Skipped, visible.Verdict);
    }

    [Fact]
    public void OffScreen_CaptureWithoutMarker_Pass() =>
        Assert.Equal(Verdict.Pass, PositionCheck.Evaluate(Sample(), Expect(MarkerVisibility.OffScreen), new MarkerDetection { Found = false }, 1f, 3).Verdict);

    [Fact]
    public void UnknownOrientation_IsNotAFailure()
    {
        var r = PositionCheck.Evaluate(Sample(flipped: true), Expect(), Detected(960, 540, MarkerOrientation.Unknown), 1f, 3);
        Assert.Equal(Verdict.Pass, r.Verdict);
    }

    [Fact]
    public void WrongOrientation_Fail()
    {
        var r = PositionCheck.Evaluate(Sample(flipped: false), Expect(), Detected(960, 540, MarkerOrientation.Mirrored), 1f, 3);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains("mirrored", r.Message);
    }
}

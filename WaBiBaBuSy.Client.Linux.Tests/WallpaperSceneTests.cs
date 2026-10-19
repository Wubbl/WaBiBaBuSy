using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using WaBiBaBuSy.Client.Linux.Rendering;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;
using Xunit;

namespace WaBiBaBuSy.Client.Linux.Tests;

public class WallpaperSceneTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wbbs-scene-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteSolidPng(string name, int w, int h, SKColor color)
    {
        var path = Path.Combine(_dir, name);
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(color);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static PlayerCommandLoadAnimation LoadCmd(string path, int canvasW, int offsetX, int h,
        MovementConfig? movement = null, string bg = "#000000")
        => new()
        {
            AnimationConfig = new AnimationLayerConfig { AnimationPath = path, FitMode = ContentFitMode.Center },
            BackgroundConfig = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = bg },
            VirtualCanvasWidth = canvasW,
            VirtualCanvasHeight = h,
            MonitorOffsetX = offsetX,
            MovementConfig = movement
        };

    private static SKBitmap RenderAt(WallpaperScene scene, double nowUnixMs)
    {
        var bmp = new SKBitmap(scene.Width, scene.Height);
        using var canvas = new SKCanvas(bmp);
        scene.Render(canvas, nowUnixMs);
        return bmp;
    }

    [Fact]
    public void ColorMatrix_TranslationIsNormalized_AndRowVectorConvertsToSkia()
    {
        // D2D convention: out.R = r*M11 + g*M21 + b*M31 + a*M41 + M51.
        // Matrix: R' = G (M21 = 1), G' = 0, B' = 0.5 offset, A' = A.
        var m = new ColorMatrix5x4(
            0, 0, 0, 0,
            1, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 1,
            0, 0, 0.5f, 0);

        using var filter = WallpaperScene.ToColorFilter(m);
        using var bmp = new SKBitmap(1, 1);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { ColorFilter = filter, Color = new SKColor(10, 200, 30) };
            canvas.DrawRect(0, 0, 1, 1, paint);
        }
        var px = bmp.GetPixel(0, 0);
        Assert.InRange(px.Red, 198, 202);    // took green
        Assert.Equal(0, px.Green);
        Assert.InRange(px.Blue, 126, 129);   // 0.5 offset → ~128, so offsets are 0..1 like D2D
        Assert.Equal(255, px.Alpha);
    }

    [Fact]
    public void LinearMovement_MatchesMovementCalculatorMinusMonitorOffset()
    {
        var path = WriteSolidPng("a.png", 100, 50, SKColors.Red);
        var movement = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 500 };
        using var scene = new WallpaperScene(1920, 1080, NullLogger.Instance);
        scene.Load(LoadCmd(path, canvasW: 3840, offsetX: 1920, h: 1080, movement));
        scene.Start(new PlayerCommandStartAnimation { StartTimestampMs = 1_000_000, PixelsPerSecond = 500 });

        foreach (long elapsed in new long[] { 0, 1234, 5000, 9999 })
        {
            using var _ = RenderAt(scene, 1_000_000 + elapsed);
            var (vx, vy) = MovementCalculator.Calculate(movement, elapsed, 100, 50, 3840, 1080);
            var (x, y, w, h) = scene.CurrentLayout;
            Assert.Equal(vx - 1920, x, 3);
            Assert.Equal(vy, y, 3);
            Assert.Equal((100, 50), (w, h));
        }
    }

    [Fact]
    public void SequentialMode_AnimationCrossesTheSeamContinuously()
    {
        // Two 400px monitors side by side on an 800px canvas; a 100px red square straddling the seam
        var path = WriteSolidPng("sq.png", 100, 100, SKColors.Red);
        var movement = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 100 };
        using var left = new WallpaperScene(400, 200, NullLogger.Instance);
        using var right = new WallpaperScene(400, 200, NullLogger.Instance);
        left.Load(LoadCmd(path, 800, 0, 200, movement));
        right.Load(LoadCmd(path, 800, 400, 200, movement));
        var start = new PlayerCommandStartAnimation { StartTimestampMs = 5000, PixelsPerSecond = 100 };
        left.Start(start);
        right.Start(start);

        // Linear starts at x = -100; after 4.5 s it is at x = 350 → spans 350..450 on the canvas
        using var l = RenderAt(left, 5000 + 4500);
        using var r = RenderAt(right, 5000 + 4500);

        Assert.Equal(SKColors.Black, l.GetPixel(340, 100));
        Assert.Equal(SKColors.Red, l.GetPixel(360, 100));
        Assert.Equal(SKColors.Red, l.GetPixel(399, 100));
        Assert.Equal(SKColors.Red, r.GetPixel(0, 100));
        Assert.Equal(SKColors.Red, r.GetPixel(45, 100));
        Assert.Equal(SKColors.Black, r.GetPixel(60, 100));
    }

    [Fact]
    public void Gif_FramesAndDelaysFollowTheWindowsPlayerRules()
    {
        var path = Path.Combine(_dir, "anim.gif");
        using (var collection = new MagickImageCollection())
        {
            foreach (var (color, delay) in new[] { (MagickColors.Red, 10u), (MagickColors.Lime, 0u), (MagickColors.Blue, 20u) })
            {
                var frame = new MagickImage(color, 20, 20) { AnimationDelay = delay };
                collection.Add(frame);
            }
            collection.Write(path, MagickFormat.Gif);
        }

        using var src = AnimationSource.Load(path);
        Assert.Equal(3, src.Frames.Length);
        Assert.Equal(new[] { 100, 10, 200 }, src.Delays); // 0-delay → 10 ms
        Assert.Equal(310, src.TotalDurationMs);
        Assert.Equal(0, src.FrameIndexAt(99));
        Assert.Equal(1, src.FrameIndexAt(100));
        Assert.Equal(2, src.FrameIndexAt(110));
        Assert.Equal(0, src.FrameIndexAt(310)); // loops

        // Rendered frame follows elapsed * SpeedMultiplier (default 2.0, like the D2D player)
        using var scene = new WallpaperScene(20, 20, NullLogger.Instance);
        var cmd = LoadCmd(path, 20, 0, 20);
        cmd.AnimationConfig.FitMode = ContentFitMode.Stretch;
        scene.Load(cmd);
        scene.Start(new PlayerCommandStartAnimation { StartTimestampMs = 1000 });
        using var at0 = RenderAt(scene, 1000 + 0);
        using var at60 = RenderAt(scene, 1000 + 60);   // effective 120 ms → frame 2 (blue)
        Assert.Equal(SKColors.Red, at0.GetPixel(10, 10));
        Assert.Equal(SKColors.Blue, at60.GetPixel(10, 10));
    }

    [Fact]
    public void Opaque_Jpeg_LoadsOpaque()
    {
        var path = Path.Combine(_dir, "photo.jpg");
        using (var img = new MagickImage(MagickColors.Orange, 8, 8)) img.Write(path, MagickFormat.Jpeg);
        using var src = AnimationSource.Load(path);
        using var bmp = SKBitmap.FromImage(src.Frames[0]);
        var px = bmp.GetPixel(4, 4);
        Assert.Equal(255, px.Alpha);
        Assert.InRange(px.Red, 245, 255);
    }

    [Fact]
    public void PatternWithTravelingColors_RendersWithoutErrors()
    {
        var path = WriteSolidPng("cell.png", 40, 40, SKColors.White);
        using var scene = new WallpaperScene(400, 200, NullLogger.Instance);
        var cmd = LoadCmd(path, 800, 400, 200,
            new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 60, Endless = true });
        cmd.AnimationConfig.Pattern = new PatternConfig { SpacingX = 10, SpacingY = 10 };
        cmd.AnimationConfig.ColorGrading = new ColorGradingConfig { Mode = ColorGradingMode.TravelingRainbow };
        scene.Load(cmd);
        scene.Start(new PlayerCommandStartAnimation { StartTimestampMs = 1 });

        using var bmp = RenderAt(scene, 1 + 3000);
        // Pattern fills the monitor: plenty of non-background, non-white (graded) pixels
        int colored = 0;
        for (int y = 0; y < 200; y += 5)
            for (int x = 0; x < 400; x += 5)
            {
                var p = bmp.GetPixel(x, y);
                if (p != SKColors.Black && p != SKColors.White) colored++;
            }
        Assert.True(colored > 200, $"expected a graded pattern, got {colored} coloured samples");
    }

    [Fact]
    public void Video_IsSkipped_BackgroundStillRenders()
    {
        var path = Path.Combine(_dir, "clip.mp4");
        File.WriteAllBytes(path, new byte[16]);
        using var scene = new WallpaperScene(10, 10, NullLogger.Instance);
        scene.Load(LoadCmd(path, 10, 0, 10, bg: "#336699"));
        scene.Start(new PlayerCommandStartAnimation());
        using var bmp = RenderAt(scene, WallpaperScene.NowUnixMs());
        Assert.Equal(new SKColor(0x33, 0x66, 0x99), bmp.GetPixel(5, 5));
    }
}

using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using WaBiBaBuSy.Client.Linux.Rendering;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;
using Xunit;

namespace WaBiBaBuSy.Client.Linux.Tests;

public class WallpaperSceneTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wbbs-scene-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    internal string WriteSolidPng(string name, int w, int h, SKColor color)
    {
        var path = Path.Combine(_dir, name);
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(color);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>A seat on a one-row canvas, the way the server's seat map lays it out.</summary>
    internal static NodeLayout Seat(int canvasW, int offsetX, int w, int h, int order = 0, string? id = null) => new()
    {
        Id = id ?? $"seat{order}", Order = order, OffsetX = offsetX, OffsetY = 0, Width = w, Height = h,
        CanvasWidth = canvasW, CanvasHeight = h, Scale = 1f,
    };

    internal static SceneLayer Layer(string path, MovementConfig? movement = null,
        AnimationDistributionMode distribution = AnimationDistributionMode.Sequential, ContentFitMode fit = ContentFitMode.Center) => new()
    {
        Animation = new AnimationLayerConfig { AnimationPath = path, FitMode = fit },
        Movement = movement ?? new MovementConfig { Type = MovementType.Static },
        Distribution = distribution,
    };

    internal static PlayerCommandLoadAnimation LoadCmd(NodeLayout? layout, string bg = "#000000", params SceneLayer[] layers) => new()
    {
        Layers = layers.ToList(),
        BackgroundConfig = new BackgroundLayerConfig { Mode = BackgroundMode.SolidColor, ColorHex = bg },
        Layout = layout,
    };

    internal static SKBitmap RenderAt(WallpaperScene scene, double nowUnixMs)
    {
        var bmp = new SKBitmap(scene.Width, scene.Height);
        using var canvas = new SKCanvas(bmp);
        scene.Render(canvas, nowUnixMs);
        return bmp;
    }

    private static WallpaperScene Started(int w, int h, PlayerCommandLoadAnimation cmd, long startMs, string transition = "")
    {
        var scene = new WallpaperScene(w, h, NullLogger.Instance, "test-machine");
        scene.Load(cmd);
        scene.Start(new PlayerCommandStartAnimation { StartTimestampMs = startMs }, transition);
        return scene;
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
    public void LinearMovement_MatchesMovementCalculatorMinusSeatOffset()
    {
        var path = WriteSolidPng("a.png", 100, 50, SKColors.Red);
        var movement = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 500 };
        using var scene = Started(1920, 1080, LoadCmd(Seat(3840, 1920, 1920, 1080), "#000000", Layer(path, movement)), 1_000_000);

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
    public void SequentialLayer_CrossesTheSeamContinuously()
    {
        // Two 400px seats side by side on an 800px canvas; a 100px red square straddling the seam
        var path = WriteSolidPng("sq.png", 100, 100, SKColors.Red);
        var movement = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 100 };
        using var left = Started(400, 200, LoadCmd(Seat(800, 0, 400, 200, 0), "#000000", Layer(path, movement)), 5000);
        using var right = Started(400, 200, LoadCmd(Seat(800, 400, 400, 200, 1), "#000000", Layer(path, movement)), 5000);

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
    public void SimultaneousLayer_IgnoresTheSeatOffset()
    {
        // Static, centred: every monitor is its own canvas, so the square is in the middle of the right seat too
        var path = WriteSolidPng("sq.png", 40, 40, SKColors.Lime);
        using var right = Started(400, 200, LoadCmd(Seat(800, 400, 400, 200, 1), "#000000",
            Layer(path, distribution: AnimationDistributionMode.Simultaneous)), 1000);
        using var bmp = RenderAt(right, 2000);
        Assert.Equal(SKColors.Lime, bmp.GetPixel(200, 100));
        Assert.Equal(SKColors.Black, bmp.GetPixel(20, 100));
    }

    [Fact]
    public void Layers_DrawBottomFirst_WithOpacity()
    {
        var red = WriteSolidPng("red.png", 100, 100, SKColors.Red);
        var blue = WriteSolidPng("blue.png", 50, 50, SKColors.Blue);
        var top = Layer(blue, distribution: AnimationDistributionMode.Simultaneous);
        top.Opacity = 0.5f;
        using var scene = Started(100, 100, LoadCmd(null, "#000000",
            Layer(red, distribution: AnimationDistributionMode.Simultaneous, fit: ContentFitMode.Stretch), top), 1000);
        using var bmp = RenderAt(scene, 1500);
        Assert.Equal(SKColors.Red, bmp.GetPixel(5, 5));          // only the bottom layer
        var mid = bmp.GetPixel(50, 50);                           // half blue over red
        Assert.InRange(mid.Red, 120, 135);
        Assert.InRange(mid.Blue, 120, 135);
    }

    [Fact]
    public void SpawnWindow_HidesTheLayerOutsideItsWindow()
    {
        var red = WriteSolidPng("red.png", 20, 20, SKColors.Red);
        var layer = Layer(red, distribution: AnimationDistributionMode.Simultaneous, fit: ContentFitMode.Stretch);
        layer.ShowEveryMs = 4000;
        layer.ShowForMs = 1000;
        using var scene = Started(20, 20, LoadCmd(null, "#000000", layer), 1000);
        using var shown = RenderAt(scene, 1000 + 500);
        using var hidden = RenderAt(scene, 1000 + 2500);
        Assert.Equal(SKColors.Red, shown.GetPixel(10, 10));
        Assert.Equal(SKColors.Black, hidden.GetPixel(10, 10));
    }

    [Fact]
    public void SourcePerSeat_EachSeatDrawsItsOwnSource()
    {
        var red = WriteSolidPng("red.png", 20, 20, SKColors.Red);
        var blue = WriteSolidPng("blue.png", 20, 20, SKColors.Blue);
        SceneLayer PerSeat()
        {
            var l = Layer(red, distribution: AnimationDistributionMode.Simultaneous, fit: ContentFitMode.Stretch);
            l.Animation.AdditionalAnimationPaths = new List<string> { blue };
            l.SourcePerSeat = true;
            return l;
        }
        using var seat0 = Started(20, 20, LoadCmd(Seat(40, 0, 20, 20, 0), "#000000", PerSeat()), 1000);
        using var seat1 = Started(20, 20, LoadCmd(Seat(40, 20, 20, 20, 1), "#000000", PerSeat()), 1000);
        using var a = RenderAt(seat0, 1500);
        using var b = RenderAt(seat1, 1500);
        Assert.Equal(SKColors.Red, a.GetPixel(10, 10));
        Assert.Equal(SKColors.Blue, b.GetPixel(10, 10));
    }

    [Fact]
    public void Panorama_EachSeatShowsItsSlice()
    {
        // 200×50 image, left half red, right half blue, over a 2-seat 200×50 canvas (Stretch: exact halves)
        var path = Path.Combine(_dir, "pano.png");
        using (var bmp = new SKBitmap(200, 50))
        using (var c = new SKCanvas(bmp))
        {
            using var p = new SKPaint { Color = SKColors.Red };
            c.DrawRect(0, 0, 100, 50, p);
            p.Color = SKColors.Blue;
            c.DrawRect(100, 0, 100, 50, p);
            c.Flush();
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        PlayerCommandLoadAnimation Cmd(NodeLayout seat)
        {
            var cmd = LoadCmd(seat);
            cmd.BackgroundConfig = new BackgroundLayerConfig { Mode = BackgroundMode.Panorama, ImagePath = path, PanoramaFit = PanoramaFit.Stretch };
            return cmd;
        }
        using var left = Started(100, 50, Cmd(Seat(200, 0, 100, 50, 0)), 1000);
        using var right = Started(100, 50, Cmd(Seat(200, 100, 100, 50, 1)), 1000);
        using var l = RenderAt(left, 1100);
        using var r = RenderAt(right, 1100);
        Assert.Equal(SKColors.Red, l.GetPixel(50, 25));
        Assert.Equal(SKColors.Blue, r.GetPixel(50, 25));
    }

    [Fact]
    public void StandbySwitch_OldSceneStaysUntilTheNewStart()
    {
        var red = WriteSolidPng("red.png", 10, 10, SKColors.Red);
        var blue = WriteSolidPng("blue.png", 10, 10, SKColors.Blue);
        using var scene = Started(10, 10, LoadCmd(null, "#000000", Layer(red, fit: ContentFitMode.Stretch)), 1000);
        scene.Load(LoadCmd(null, "#000000", Layer(blue, fit: ContentFitMode.Stretch)));
        scene.Start(new PlayerCommandStartAnimation { StartTimestampMs = 5000 });
        using var before = RenderAt(scene, 4900);
        using var after = RenderAt(scene, 5100);
        Assert.Equal(SKColors.Red, before.GetPixel(5, 5));
        Assert.Equal(SKColors.Blue, after.GetPixel(5, 5));
    }

    [Fact]
    public void FadeTransition_DipsThroughBlack()
    {
        var red = WriteSolidPng("red.png", 10, 10, SKColors.Red);
        var blue = WriteSolidPng("blue.png", 10, 10, SKColors.Blue);
        using var scene = Started(10, 10, LoadCmd(null, "#000000", Layer(red, fit: ContentFitMode.Stretch)), 1000);
        scene.Load(LoadCmd(null, "#000000", Layer(blue, fit: ContentFitMode.Stretch)));
        scene.Start(new PlayerCommandStartAnimation { StartTimestampMs = 10_000 }, "Fade:2000");
        using var early = RenderAt(scene, 8000);    // fade-out starts at T0 − 1 s
        using var halfOut = RenderAt(scene, 9500);
        using var atStart = RenderAt(scene, 10_000);
        using var halfIn = RenderAt(scene, 10_500);
        using var done = RenderAt(scene, 11_500);
        Assert.Equal(SKColors.Red, early.GetPixel(5, 5));
        Assert.InRange(halfOut.GetPixel(5, 5).Red, 110, 145);
        Assert.Equal(SKColors.Black, atStart.GetPixel(5, 5));
        Assert.InRange(halfIn.GetPixel(5, 5).Blue, 110, 145);
        Assert.Equal(SKColors.Blue, done.GetPixel(5, 5));
    }

    [Fact]
    public void WipeTransition_RevealsTheNewSceneAlongTheSeatMap()
    {
        var red = WriteSolidPng("red.png", 10, 10, SKColors.Red);
        var blue = WriteSolidPng("blue.png", 10, 10, SKColors.Blue);
        var seat = Seat(100, 0, 100, 10);
        var layer = (string p) => Layer(p, distribution: AnimationDistributionMode.Simultaneous, fit: ContentFitMode.Stretch);
        using var scene = Started(100, 10, LoadCmd(seat, "#000000", layer(red)), 1000);
        scene.Load(LoadCmd(seat, "#000000", layer(blue)));
        scene.Start(new PlayerCommandStartAnimation { StartTimestampMs = 10_000 }, "Wipe:1000");
        using var mid = RenderAt(scene, 10_500);    // boundary at x = 50
        using var done = RenderAt(scene, 11_100);
        Assert.Equal(SKColors.Blue, mid.GetPixel(20, 5));
        Assert.Equal(SKColors.Red, mid.GetPixel(80, 5));
        Assert.Equal(SKColors.Blue, done.GetPixel(80, 5));
    }

    [Fact]
    public void Gif_FramesAndDelaysFollowTheWindowsPlayerRules()
    {
        var path = Path.Combine(_dir, "anim.gif");
        using (var collection = new MagickImageCollection())
        {
            foreach (var (color, delay) in new[] { (MagickColors.Red, 10u), (MagickColors.Lime, 0u), (MagickColors.Blue, 20u), (MagickColors.White, 300u) })
            {
                var frame = new MagickImage(color, 20, 20) { AnimationDelay = delay };
                collection.Add(frame);
            }
            collection.Write(path, MagickFormat.Gif);
        }

        using var src = AnimationSource.Load(path);
        Assert.Equal(4, src.Frames.Length);
        Assert.Equal(new[] { 100, 10, 200, 3000 }, src.Delays); // 0-delay → 10 ms, long holds are kept (no 1 s cap)
        Assert.Equal(3310, src.TotalDurationMs);
        Assert.Equal(0, src.FrameIndexAt(99));
        Assert.Equal(1, src.FrameIndexAt(100));
        Assert.Equal(2, src.FrameIndexAt(110));
        Assert.Equal(3, src.FrameIndexAt(2000));
        Assert.Equal(0, src.FrameIndexAt(3310)); // loops

        // Rendered frame follows elapsed * SpeedMultiplier (default 2.0, like the D2D player)
        using var scene = Started(20, 20, LoadCmd(null, "#000000", Layer(path, fit: ContentFitMode.Stretch)), 1000);
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
        var layer = Layer(path, new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 60, Endless = true });
        layer.Animation.Pattern = new PatternConfig { SpacingX = 10, SpacingY = 10 };
        layer.Animation.ColorGrading = new ColorGradingConfig { Mode = ColorGradingMode.TravelingRainbow };
        using var scene = Started(400, 200, LoadCmd(Seat(800, 400, 400, 200, 1), "#000000", layer), 1);

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
    public void SpriteEffects_TrailAndBezel_RenderWithoutErrors()
    {
        var path = WriteSolidPng("sq.png", 40, 40, SKColors.Red);
        var layer = Layer(path, new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 300 });
        layer.Animation.Effects = new SpriteEffectsConfig
        {
            Trail = new TrailConfig { Count = 4 },
            Pulse = new PulseConfig { Amplitude = 0.2f },
            SpinDegPerSecond = 90,
            Entry = BezelEffect.Portal,
            Exit = BezelEffect.Glow,
        };
        using var scene = Started(200, 100, LoadCmd(Seat(400, 200, 200, 100, 1), "#000000", layer), 1000);
        for (long t = 0; t < 3000; t += 100)
            using (RenderAt(scene, 1000 + t)) { }
    }

    [Fact]
    public void Video_IsSkipped_BackgroundStillRenders()
    {
        var path = Path.Combine(_dir, "clip.mp4");
        File.WriteAllBytes(path, new byte[16]);
        using var scene = Started(10, 10, LoadCmd(null, "#336699", Layer(path)), 1);
        using var bmp = RenderAt(scene, WallpaperScene.NowUnixMs());
        Assert.Equal(new SKColor(0x33, 0x66, 0x99), bmp.GetPixel(5, 5));
    }

    [Fact]
    public void BackgroundOnlyScene_Renders()
    {
        using var scene = Started(10, 10, LoadCmd(null, "#112233"), 1);
        using var bmp = RenderAt(scene, 100);
        Assert.Equal(new SKColor(0x11, 0x22, 0x33), bmp.GetPixel(5, 5));
    }
}

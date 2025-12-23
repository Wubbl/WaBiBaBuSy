using Microsoft.Extensions.Logging;
using System.Drawing;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Direct2D;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.D2DTest;

/// <summary>
/// Simple test program to verify D2DVorticeRenderer renders correctly.
/// This creates a red window on the desktop behind icons for 10 seconds.
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("WaBiBaBuSy Direct2D Vortice Renderer Test");
        Console.WriteLine("==========================================");
        Console.WriteLine();
        Console.WriteLine("This test will:");
        Console.WriteLine("1. Create a Direct2D DXGI renderer on your primary monitor");
        Console.WriteLine("2. Parent it to the desktop (behind icons)");
        Console.WriteLine("3. Fill it with RED color");
        Console.WriteLine("4. Keep it visible for 10 seconds");
        Console.WriteLine();
        Console.WriteLine("You should see a RED wallpaper behind your desktop icons.");
        Console.WriteLine();
        Console.WriteLine("Press ENTER to start test...");
        Console.ReadLine();

        // Create logger
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        var rendererLogger = loggerFactory.CreateLogger<D2DVorticeRenderer>();
        var desktopLogger = loggerFactory.CreateLogger<DesktopWindowManager>();

        try
        {
            Console.WriteLine();
            Console.WriteLine("Creating desktop manager...");
            var desktopManager = new DesktopWindowManager(desktopLogger);

            Console.WriteLine("Getting primary screen bounds...");
            var primaryScreen = System.Windows.Forms.Screen.PrimaryScreen;
            var bounds = primaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

            Console.WriteLine($"Primary screen: {bounds.Width}x{bounds.Height} at ({bounds.X}, {bounds.Y})");
            Console.WriteLine();

            // Create ScreenMapping for D2DVorticeRenderer
            var screenMapping = new ScreenMapping
            {
                ClientId = "test-client",
                Order = 0,
                ScreenBounds = bounds,
                VirtualBounds = bounds  // Same as screen bounds for single-screen test
            };

            Console.WriteLine("Creating D2DVorticeRenderer...");
            using var renderer = new D2DVorticeRenderer(
                screenMapping,
                rendererLogger,
                desktopManager);

            Console.WriteLine("Initializing renderer...");
            renderer.Initialize();

            Console.WriteLine("SUCCESS: Renderer initialized!");
            Console.WriteLine();

            // Create a red bitmap
            var testBitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(testBitmap))
            {
                g.Clear(Color.Red);
            }

            Console.WriteLine();
            Console.WriteLine("===================================");
            Console.WriteLine("RED WINDOW SHOULD NOW BE VISIBLE!");
            Console.WriteLine("===================================");
            Console.WriteLine();
            Console.WriteLine("Rendering red color continuously for 10 seconds...");
            Console.WriteLine("Check your desktop - you should see a red wallpaper behind your icons.");
            Console.WriteLine();

            // Render continuously for 10 seconds at ~60 FPS
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int frameCount = 0;

            while (stopwatch.Elapsed.TotalSeconds < 10)
            {
                // Display frame
                renderer.DisplayFrame(testBitmap);
                frameCount++;

                // Update console every second
                int secondsLeft = 10 - (int)stopwatch.Elapsed.TotalSeconds;
                if (frameCount % 60 == 0)
                {
                    Console.Write($"\rRendering... {secondsLeft} seconds remaining (Frames: {frameCount})  ");
                }

                // Target ~60 FPS with VSync (renderer uses Present(1))
                Thread.Sleep(16);
            }

            Console.WriteLine();
            Console.WriteLine($"\nRendered {frameCount} frames in {stopwatch.Elapsed.TotalSeconds:F2} seconds ({frameCount / stopwatch.Elapsed.TotalSeconds:F1} FPS)");

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Disposing renderer...");
            testBitmap.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("ERROR: Test failed with exception:");
            Console.WriteLine(ex.ToString());
            Console.WriteLine();
            Console.WriteLine("Press ENTER to exit...");
            Console.ReadLine();
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Test completed successfully!");
        Console.WriteLine("Press ENTER to exit...");
        Console.ReadLine();
    }
}

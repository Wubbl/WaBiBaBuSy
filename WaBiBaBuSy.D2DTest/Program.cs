using Microsoft.Extensions.Logging;
using System.Drawing;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Direct2D;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.D2DTest;

/// <summary>
/// Simple test program to verify D2DVorticeRenderer renders correctly.
/// This creates a red window on the desktop behind icons for 10 seconds.
/// NO WINDOWS FORMS - uses native Win32 windows only.
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
        Console.WriteLine("4. Keep it visibl 60 seconds");
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

            // Get primary screen bounds using Win32 API (no Windows Forms)
            var primaryScreen = GetPrimaryScreenBounds();
            Console.WriteLine($"Primary screen: {primaryScreen.Width}x{primaryScreen.Height} at ({primaryScreen.X}, {primaryScreen.Y})");
            Console.WriteLine();

            // Create ScreenMapping for D2DVorticeRenderer
            var screenMapping = new ScreenMapping
            {
                ClientId = "test-client",
                Order = 0,
                ScreenBounds = primaryScreen,
                VirtualBounds = primaryScreen  // Same as screen bounds for single-screen test
            };

            Console.WriteLine("Creating D2DVorticeRenderer (native Win32 window)...");
            using var renderer = new D2DVorticeRenderer(
                screenMapping,
                rendererLogger,
                desktopManager);

            Console.WriteLine("Initializing renderer...");
            renderer.Initialize();

            Console.WriteLine("SUCCESS: Renderer initialized!");
            Console.WriteLine();

            // Create a red bitmap
            var testBitmap = new Bitmap(primaryScreen.Width, primaryScreen.Height);
            using (var g = Graphics.FromImage(testBitmap))
            {
                g.Clear(Color.Red);
            }

            Console.WriteLine();
            Console.WriteLine("===================================");
            Console.WriteLine("RED WINDOW SHOULD NOW BE VISIBLE!");
            Console.WriteLine("===================================");
            Console.WriteLine();
            Console.WriteLine("Rendering red color continuously for 60 seconds...");
            Console.WriteLine("Check your desktop - you should see a red wallpaper behind your icons.");
            Console.WriteLine();

            // Render continuously for 60 seconds at ~60 FPS
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int frameCount = 0;

            while (stopwatch.Elapsed.TotalSeconds < 60)
            {
                // Display frame
                renderer.DisplayFrame(testBitmap);
                frameCount++;

                // Update console every second
                int secondsLeft = 60 - (int)stopwatch.Elapsed.TotalSeconds;
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

    /// <summary>
    /// Gets the primary screen bounds using native Win32 API (no Windows Forms).
    /// </summary>
    private static Rectangle GetPrimaryScreenBounds()
    {
        // SM_CXSCREEN = 0 (width), SM_CYSCREEN = 1 (height)
        int width = GetSystemMetrics(0);
        int height = GetSystemMetrics(1);

        return new Rectangle(0, 0, width, height);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}

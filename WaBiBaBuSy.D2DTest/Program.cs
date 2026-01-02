using Microsoft.Extensions.Logging;
using System.Drawing;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Direct2D;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.D2DTest;

/// <summary>
/// Test program for D2DPlayerHost - the separate process approach for DXGI rendering.
/// This tests whether parenting an external process's window to the desktop works
/// without crashing explorer.exe on Windows 11 24H2+.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("WaBiBaBuSy D2D Player Host Test");
        Console.WriteLine("================================");
        Console.WriteLine();
        Console.WriteLine("This test will:");
        Console.WriteLine("1. Spawn a separate D2D player process");
        Console.WriteLine("2. Parent its window to the desktop (behind icons)");
        Console.WriteLine("3. Render RED color for 30 seconds");
        Console.WriteLine("4. Change to GREEN, then BLUE");
        Console.WriteLine();
        Console.WriteLine("You should be able to click on desktop icons WITHOUT crashing explorer!");
        Console.WriteLine();
        Console.WriteLine("Press ENTER to start test...");
        Console.ReadLine();

        // Create logger
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        var hostLogger = loggerFactory.CreateLogger<D2DPlayerHost>();
        var desktopLogger = loggerFactory.CreateLogger<DesktopWindowManager>();

        try
        {
            Console.WriteLine();
            Console.WriteLine("Creating desktop manager...");
            var desktopManager = new DesktopWindowManager(desktopLogger);

            Console.WriteLine("Getting primary screen bounds...");
            var primaryScreen = GetPrimaryScreenBounds();
            Console.WriteLine($"Primary screen: {primaryScreen.Width}x{primaryScreen.Height} at ({primaryScreen.X}, {primaryScreen.Y})");
            Console.WriteLine();

            // Create ScreenMapping
            var screenMapping = new ScreenMapping
            {
                ClientId = "test-client",
                Order = 0,
                ScreenBounds = primaryScreen,
                VirtualBounds = primaryScreen
            };

            Console.WriteLine("Creating D2DPlayerHost (separate process approach)...");
            using var host = new D2DPlayerHost(
                screenMapping,
                hostLogger,
                desktopManager,
                primaryScreen);

            Console.WriteLine("Initializing player host (spawning player process)...");
            await host.InitializeAsync();

            Console.WriteLine();
            Console.WriteLine("============================================");
            Console.WriteLine("PLAYER WINDOW SHOULD NOW BE ON YOUR DESKTOP!");
            Console.WriteLine("============================================");
            Console.WriteLine();
            Console.WriteLine("TRY CLICKING ON DESKTOP ICONS - they should work!");
            Console.WriteLine();

            // Test color changes
            Console.WriteLine("Setting color to RED...");
            await host.SetColorAsync(Color.Red);
            Console.WriteLine("Waiting 10 seconds... (try clicking on icons!)");
            await Task.Delay(10000);

            Console.WriteLine("Setting color to GREEN...");
            await host.SetColorAsync(Color.Green);
            Console.WriteLine("Waiting 10 seconds...");
            await Task.Delay(10000);

            Console.WriteLine("Setting color to BLUE...");
            await host.SetColorAsync(Color.Blue);
            Console.WriteLine("Waiting 10 seconds...");
            await Task.Delay(10000);

            Console.WriteLine();
            Console.WriteLine("Test completed! Cleaning up...");
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

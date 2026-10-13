using Avalonia;
using Avalonia.Win32;
using System;

namespace WaBiBaBuSy.UI;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        CliConsole.Initialize();
        // One instance per user session: a second one would kill the first one's players at startup
        // (orphan cleanup), fight over the server port and the hotkeys, and repaint the desktop.
        using var instance = new System.Threading.Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            CliConsole.WriteLine("WaBiBaBuSy is already running (tray icon). Close it first, or run tests from its Developer tools.", error: true);
            return 2;   // = "could not run" for --test-run --exit
        }
        var options = StartupOptions.Parse(args);
        foreach (var warning in options.Warnings)
            CliConsole.WriteLine($"WaBiBaBuSy: {warning} ({StartupOptions.Usage})", error: true);
        // The lifetime returns the code passed to Shutdown(): --test-run --exit relies on it (0 / 1 / 2).
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Per-session (Local\) name; also usable as the installer's AppMutex.</summary>
    public const string SingleInstanceMutexName = @"Local\WaBiBaBuSy.UI";

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            // GPU Optimization: Use software rendering backend to reduce GPU load
            // This trades GPU usage for slightly higher CPU usage, but results in more predictable performance
            // Can be toggled by setting environment variable AVALONIA_RENDER_MODE=software or removing this line
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Software, Win32RenderingMode.AngleEgl }
            });
}

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
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

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

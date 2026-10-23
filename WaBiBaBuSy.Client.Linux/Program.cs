using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Models.Configuration;

namespace WaBiBaBuSy.Client.Linux;

public static class Program
{
    internal static ClientOptions Options { get; private set; } = null!;

    public static int Main(string[] args)
    {
        try
        {
            Options = ClientOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(ClientOptions.Usage);
            return 2;
        }
        if (Options.ShowHelp)
        {
            Console.WriteLine(ClientOptions.Usage);
            return 0;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Console.Error.WriteLine("DISPLAY is not set. The client needs X11 (on Wayland sessions it runs through XWayland).");
            return 1;
        }

        var logging = new LoggingConfiguration
        {
            Level = Options.Verbose ? "Debug" : "Information",
            LogFrameByFrame = Options.Verbose,
            LogToFile = Options.LogDirectory != null
        };
        if (Options.LogDirectory != null) logging.LogDirectory = Options.LogDirectory;
        AppLogger.Initialize(logging);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseX11()   // always X11: EWMH desktop hints are an X11 concept (XWayland on Wayland sessions)
            .UseSkia()
            .UseHarfBuzz()
            .With(new X11PlatformOptions
            {
                // GPU (GLX/EGL) first, CPU fallback; --software skips the GPU for broken drivers
                RenderingMode = Options?.SoftwareRendering == true
                    ? new[] { X11RenderingMode.Software }
                    : new[] { X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software }
            })
            .LogToTrace();
}

public sealed class App : Application
{
    private LinuxClientHost? _host;

    // A theme supplies the Window template that presents Content (our scene surface)
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = new LinuxClientHost(Program.Options);
            _ = _host.StartAsync();

            bool shuttingDown = false;
            void Shutdown()
            {
                if (shuttingDown) return;
                shuttingDown = true;
                Dispatcher.UIThread.Post(async () =>
                {
                    if (_host != null) await _host.DisposeAsync();
                    desktop.Shutdown();
                });
            }
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

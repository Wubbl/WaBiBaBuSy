using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.UI.ViewModels;
using WaBiBaBuSy.UI.Views;

namespace WaBiBaBuSy.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Initialize logging from persisted config before anything else starts
        var loggingConfig = ConfigurationManager.LoadLoggingConfiguration();
        AppLogger.Initialize(loggingConfig);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Don't create MainWindow on startup - start minimized to tray
            // Application will run until explicitly shut down via Exit command
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            // Set DataContext for TrayIcon bindings
            DataContext = new TrayViewModel(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void TrayIcon_OnClicked(object? sender, System.EventArgs e)
    {
        if (DataContext is TrayViewModel trayVm)
        {
            trayVm.ShowWindowCommand.Execute(null);
        }
    }


}
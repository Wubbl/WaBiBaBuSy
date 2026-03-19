using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
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
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

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

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
using Avalonia.Controls;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Views;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    public SettingsWindow()
    {
        InitializeComponent();

        var viewModel = new SettingsViewModel();
        DataContext = viewModel;
        viewModel.SetStorageProvider(StorageProvider);

        viewModel.SettingsSaved += (s, e) => Close();
        viewModel.SettingsCancelled += (s, e) => Close();
    }

    /// <summary>
    /// Show the Settings window, or bring the already open one to the front. Tray and the
    /// main window's ⋯ menu both open it, so a second click must not load a second copy.
    /// </summary>
    public static void ShowSingle()
    {
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }

        _open = new SettingsWindow();
        _open.Closed += (s, e) => _open = null;
        _open.Show();
    }
}

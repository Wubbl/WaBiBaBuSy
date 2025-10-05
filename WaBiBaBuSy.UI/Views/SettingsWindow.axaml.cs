using Avalonia.Controls;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        var viewModel = new SettingsViewModel();
        DataContext = viewModel;

        // Subscribe to events
        viewModel.SettingsSaved += (s, e) => Close();
        viewModel.SettingsCancelled += (s, e) => Close();
    }
}

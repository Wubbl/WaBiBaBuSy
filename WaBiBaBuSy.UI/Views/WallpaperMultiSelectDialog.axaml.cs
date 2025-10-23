using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Views;

public partial class WallpaperMultiSelectDialog : Window
{
    public WallpaperMultiSelectDialog()
    {
        InitializeComponent();

        if (DataContext is WallpaperMultiSelectDialogViewModel viewModel)
        {
            viewModel.SetCloseAction(() => Close());
        }
    }

    /// <summary>
    /// Get the selected wallpapers from the dialog
    /// </summary>
    public static async System.Threading.Tasks.Task<System.Collections.Generic.List<WallpaperMultiSelectItem>?> ShowDialogAsync(
        Window ownerWindow,
        System.Collections.Generic.IEnumerable<WallpaperItemViewModel> wallpapers)
    {
        var viewModel = new WallpaperMultiSelectDialogViewModel();
        viewModel.LoadWallpapers(wallpapers);

        var dialog = new WallpaperMultiSelectDialog
        {
            DataContext = viewModel
        };

        await dialog.ShowDialog(ownerWindow);

        if (viewModel.DialogResult)
        {
            return viewModel.GetSelectedWallpapers();
        }

        return null;
    }
}

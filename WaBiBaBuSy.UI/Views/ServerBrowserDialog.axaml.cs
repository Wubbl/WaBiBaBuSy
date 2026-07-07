using Avalonia.Controls;
using Avalonia.Input;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Views;

public partial class ServerBrowserDialog : Window
{
    public ServerBrowserDialog()
    {
        InitializeComponent();
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Double-click a server = select and connect.
        if (DataContext is ServerBrowserViewModel vm && vm.ConnectCommand.CanExecute(null))
            vm.ConnectCommand.Execute(null);
    }
}

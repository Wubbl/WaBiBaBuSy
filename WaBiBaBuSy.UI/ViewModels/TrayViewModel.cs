using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.UI.Views;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// ViewModel for the system tray icon
/// </summary>
public partial class TrayViewModel : ObservableObject
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private MainWindow? _mainWindow;

    public TrayViewModel(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
    }

    [RelayCommand]
    private void ShowWindow()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };
        }

        _mainWindow.Show();
        _mainWindow.Activate();
    }

    [RelayCommand]
    private void StartServer()
    {
        // TODO: Implement server startup logic
        // This will be implemented when the server service is ready
    }

    [RelayCommand]
    private void StopServer()
    {
        // TODO: Implement server stop logic
    }

    [RelayCommand]
    private void Connect()
    {
        // TODO: Show client connection dialog
        // This will open the ClientConnectWindow
    }

    [RelayCommand]
    private void Disconnect()
    {
        // TODO: Implement client disconnect logic
    }

    [RelayCommand]
    private void Settings()
    {
        // TODO: Show settings window
    }

    [RelayCommand]
    private void Exit()
    {
        // Cleanup: Close main window if open
        _mainWindow?.Close();

        // Shutdown the application
        _desktop.Shutdown();
    }
}

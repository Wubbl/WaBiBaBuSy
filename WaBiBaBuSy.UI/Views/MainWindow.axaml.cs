// Same using set as before the rewrite (file-drop helpers such as TryGetFiles are extension methods).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.UI.ViewModels;
using WaBiBaBuSy.WallpaperEngine.Services;

namespace WaBiBaBuSy.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Pre-initialize LibVLC in background to eliminate ~9s delay on first wallpaper
        _ = LibVLCPreloader.PreloadAsync(AppLogger.CreateLogger<MainWindow>());

        // Drag-and-drop of files onto the window adds them to the gallery
        AddHandler(DragDrop.DropEvent, OnFileDrop);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);

        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        // Click on empty gallery area deselects the wallpaper
        var galleryScrollViewer = this.FindControl<ScrollViewer>("GalleryScrollViewer");
        if (galleryScrollViewer != null)
        {
            galleryScrollViewer.PointerPressed -= OnGalleryPointerPressed;
            galleryScrollViewer.PointerPressed += OnGalleryPointerPressed;
        }
    }

    /// <summary>Handle click on gallery background to deselect wallpaper.</summary>
    private void OnGalleryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is ScrollViewer or ScrollContentPresenter or ItemsControl or WrapPanel or Border { Name: "GalleryScrollViewer" })
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                foreach (var w in viewModel.Wallpapers)
                    w.IsSelected = false;
                viewModel.SelectedWallpaper = null;
            }
        }
    }

    private void OnWindowOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetStorageProvider(StorageProvider);
            viewModel.SetMainWindow(this);
            viewModel.UpdateServerStatus();
            viewModel.StartRefreshTimer();
        }
    }

    private void OnWindowClosed(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.StopRefreshTimer();
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files == null) return;
        foreach (var item in files)
        {
            var path = item.Path?.LocalPath;
            if (!string.IsNullOrEmpty(path))
                vm.AddWallpaperFromPath(path);
        }
    }
}

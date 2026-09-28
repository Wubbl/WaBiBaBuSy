// Same using set as before the rewrite (file-drop helpers such as TryGetFiles are extension methods).
using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.UI.ViewModels;
using WaBiBaBuSy.WallpaperEngine.Services;

namespace WaBiBaBuSy.UI.Views;

public partial class MainWindow : Window
{
    /// <summary>Right panel width restored when it is expanded again (px); starts at the width set in MainWindow.axaml.</summary>
    private double _panelWidth;
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        _panelWidth = MainSplit.ColumnDefinitions[2].Width.Value;

        // Pre-initialize LibVLC in background to eliminate ~9s delay on first wallpaper
        if (!Design.IsDesignMode)
            _ = LibVLCPreloader.PreloadAsync(AppLogger.CreateLogger<MainWindow>());

        // Drag-and-drop of files onto the window adds them to the gallery
        AddHandler(DragDrop.DropEvent, OnFileDrop);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);

        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm == null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyPanelState(_vm.IsRightPanelOpen);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsRightPanelOpen) && _vm != null)
            ApplyPanelState(_vm.IsRightPanelOpen);
    }

    /// <summary>Open: restore the last panel width and the splitter. Collapsed: a 32 px rail, no splitter.</summary>
    private void ApplyPanelState(bool open)
    {
        var column = MainSplit.ColumnDefinitions[2];
        if (!open && column.ActualWidth > 100) _panelWidth = column.ActualWidth;
        column.MinWidth = open ? 360 : 0;
        column.Width = new GridLength(open ? _panelWidth : 32);
        PanelSplitter.IsVisible = open;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetStorageProvider(StorageProvider);
            viewModel.SetMainWindow(this);
            viewModel.UpdateServerStatus();
            viewModel.StartRefreshTimer();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
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

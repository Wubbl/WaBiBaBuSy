using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WaBiBaBuSy.UI.Controls;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Views;

public partial class CrossScreenConfigDialog : Window
{
    private DispatcherTimer? _previewDebounce;
    private CrossScreenConfigViewModel? _vm;

    public CrossScreenConfigDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChangedForPreview;
    }

    // ── Live preview wiring ──────────────────────────────────────────────────
    // Every property change on the view model (or a monitor selection change) rebuilds the
    // preview's scene + layout after a short debounce, then restarts the design clock.
    private void OnDataContextChangedForPreview(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.AvailableMonitors.CollectionChanged -= OnMonitorsChanged;
            foreach (var m in _vm.AvailableMonitors) m.PropertyChanged -= OnMonitorItemChanged;
        }
        _vm = DataContext as CrossScreenConfigViewModel;
        if (_vm == null) return;

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.AvailableMonitors.CollectionChanged += OnMonitorsChanged;
        foreach (var m in _vm.AvailableMonitors) m.PropertyChanged += OnMonitorItemChanged;

        _previewDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _previewDebounce!.Stop();
            UpdatePreview(restartClock: true);
        });
        UpdatePreview(restartClock: true);
    }

    private void OnMonitorsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (MonitorSelectionItem m in e.NewItems) m.PropertyChanged += OnMonitorItemChanged;
        if (e.OldItems != null) foreach (MonitorSelectionItem m in e.OldItems) m.PropertyChanged -= OnMonitorItemChanged;
        SchedulePreviewUpdate();
    }

    private void OnMonitorItemChanged(object? sender, PropertyChangedEventArgs e) => SchedulePreviewUpdate();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) => SchedulePreviewUpdate();

    private void SchedulePreviewUpdate()
    {
        if (_previewDebounce == null) return;
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void UpdatePreview(bool restartClock)
    {
        var preview = this.FindControl<ScenePreviewControl>("PreviewControl");
        if (preview == null || _vm == null) return;
        try
        {
            preview.Scene = _vm.BuildConfig();
            preview.Layout = _vm.BuildPreviewLayout();
            preview.Labels = _vm.PreviewLabels;
            preview.SpriteImagePath = _vm.ResolvePreviewImagePath();
            if (restartClock) preview.RestartClock();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Preview] Update failed: {ex.Message}");
        }
    }

    private void OnPreviewPauseClick(object? sender, RoutedEventArgs e)
    {
        var preview = this.FindControl<ScenePreviewControl>("PreviewControl");
        if (preview == null) return;
        preview.IsPaused = !preview.IsPaused;
        if (sender is Button b) b.Content = preview.IsPaused ? "Play" : "Pause";
    }

    private void OnPreviewRestartClick(object? sender, RoutedEventArgs e)
        => this.FindControl<ScenePreviewControl>("PreviewControl")?.RestartClock();

    private void OnPreviewSpeedChanged(object? sender, SelectionChangedEventArgs e)
    {
        var preview = this.FindControl<ScenePreviewControl>("PreviewControl");
        if (preview == null || sender is not ComboBox cb) return;
        preview.ClockSpeed = cb.SelectedIndex switch { 1 => 4.0, 2 => 16.0, _ => 1.0 };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsWindows())
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                const int GWL_STYLE = -16;
                const int WS_MINIMIZEBOX = 0x20000;
                const int WS_MAXIMIZEBOX = 0x10000;
                var style = GetWindowLong(handle, GWL_STYLE);
                SetWindowLong(handle, GWL_STYLE, style & ~(WS_MINIMIZEBOX | WS_MAXIMIZEBOX));
            }
        }
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// Guard against PlatformImpl being null when Avalonia dispatches input
    /// before the native window is fully created or after disposal.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (PlatformImpl == null) return;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (PlatformImpl == null) return;
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (PlatformImpl == null) return;
        base.OnPointerReleased(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (PlatformImpl == null) return;
        base.OnKeyDown(e);
    }
}

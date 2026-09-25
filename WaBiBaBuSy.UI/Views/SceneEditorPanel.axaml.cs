using Avalonia.Controls;
using Avalonia.Input;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Views;

/// <summary>Right-panel "Scene" tab: gallery strip, validation chips, Content/Motion/Look/Background tabs and the play footer.</summary>
public partial class SceneEditorPanel : UserControl
{
    public SceneEditorPanel() => InitializeComponent();

    /// <summary>Strip click: primary file; Ctrl+click adds an extra image; while picking a background, sets it.</summary>
    private void OnStripItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { DataContext: WallpaperItemViewModel item } || DataContext is not MainWindowViewModel vm) return;
        vm.PickGalleryItem(item, e.KeyModifiers.HasFlag(KeyModifiers.Control));
        e.Handled = true;
    }
}

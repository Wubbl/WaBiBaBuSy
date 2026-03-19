using Avalonia.Controls;
using Avalonia.Input;

namespace WaBiBaBuSy.UI.Views;

public partial class CrossScreenConfigDialog : Window
{
    public CrossScreenConfigDialog()
    {
        InitializeComponent();
    }

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

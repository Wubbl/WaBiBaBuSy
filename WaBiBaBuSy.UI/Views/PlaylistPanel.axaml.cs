using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Views;

/// <summary>
/// Right-panel "Playlist" tab. Rows are reordered by pressing the ≡ handle and dragging: the pointer is
/// captured by the list, a marker shows the target gap, the drop calls <see cref="PlaylistViewModel.MoveItem"/>.
/// </summary>
public partial class PlaylistPanel : UserControl
{
    private int _dragFrom = -1;
    private IPointer? _dragPointer;

    public PlaylistPanel()
    {
        InitializeComponent();
        ItemsList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        ItemsList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        ItemsList.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { DataContext: PlaylistItemRow row } || DataContext is not PlaylistViewModel vm) return;
        _dragFrom = vm.Items.IndexOf(row);
        if (_dragFrom < 0) return;
        _dragPointer = e.Pointer;
        e.Pointer.Capture(ItemsList);
        e.Handled = true;
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom < 0) return;
        var (_, y) = InsertionPoint(e.GetPosition(ItemsList));
        DropMarker.Width = ItemsList.Bounds.Width;
        Canvas.SetTop(DropMarker, y - 1.5);
        DropMarker.IsVisible = true;
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragFrom < 0) return;
        var (insertBefore, _) = InsertionPoint(e.GetPosition(ItemsList));
        int from = _dragFrom;
        EndDrag();
        if (DataContext is PlaylistViewModel vm) vm.MoveItem(from, insertBefore);
        e.Handled = true;
    }

    /// <summary>End a drag without moving anything. Safe to call repeatedly (capture loss re-enters it).</summary>
    private void EndDrag()
    {
        if (_dragFrom < 0) return;
        _dragFrom = -1;
        var pointer = _dragPointer;
        _dragPointer = null;
        pointer?.Capture(null);
        DropMarker.IsVisible = false;
    }

    /// <summary>Gap under the pointer: the index of the row it sits before (count = after the last row) and that gap's Y in list coordinates.</summary>
    private (int InsertBefore, double Y) InsertionPoint(Point p)
    {
        int count = ItemsList.ItemCount;
        double lastBottom = 0;
        for (int i = 0; i < count; i++)
        {
            if (ItemsList.ContainerFromIndex(i) is not Control container) continue;   // virtualized away
            var topLeft = container.TranslatePoint(new Point(0, 0), ItemsList);
            if (topLeft == null) continue;
            double top = topLeft.Value.Y, height = container.Bounds.Height;
            if (p.Y < top + height / 2) return (i, top);
            lastBottom = top + height;
        }
        return (count, lastBottom);
    }
}

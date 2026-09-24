// WaBiBaBuSy.UI/Controls/IRoomHost.cs
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>
/// Everything <see cref="RoomView"/> reads and changes. Implemented by MainWindowViewModel so the
/// control stays free of networking and persistence. Row indices are seat-map row indices; node ids
/// are client ids.
/// </summary>
public interface IRoomHost : INotifyPropertyChanged
{
    /// <summary>All nodes of the room (local monitors, clients, expanded multi-monitor nodes).</summary>
    ObservableCollection<ClientNodeViewModel> Clients { get; }

    /// <summary>The room's current seat map (rows, traversal, gaps).</summary>
    SeatMap SeatMap { get; }

    /// <summary>Bumped on every seat-map change; the view re-lays out when it changes.</summary>
    int SeatMapVersion { get; }

    /// <summary>Lay the given nodes out over the current seat map (tile grid geometry).</summary>
    SeatMapLayoutResult BuildSeatLayout(IEnumerable<ClientNodeViewModel> clients);

    /// <summary>Short warning shown as ⚠ on the tile (e.g. mixed refresh rates), or null.</summary>
    string? NodeWarning(string nodeId);

    /// <summary>True when remote logs can be requested for this node (server mode, remote node).</summary>
    bool CanFetchLogs(string nodeId);

    /// <summary>Select exactly this node (plain click).</summary>
    void SelectOnly(string nodeId);

    /// <summary>Flip this node's selection (Ctrl+click).</summary>
    void ToggleSelection(string nodeId);

    /// <summary>Add this node to the selection (Shift+click).</summary>
    void AddToSelection(string nodeId);

    /// <summary>Select the nodes under the rubber band; <paramref name="additive"/> keeps the rest of the selection.</summary>
    void SetRubberBandSelection(IReadOnlyCollection<string> nodeIds, bool additive);

    /// <summary>Deselect every node.</summary>
    void ClearSelection();

    /// <summary>Pause topology refreshes while a tile is dragged. Idempotent.</summary>
    void BeginNodeDrag();

    /// <summary>Resume topology refreshes after a drag. Idempotent; no-op when no drag paused them.</summary>
    void EndNodeDrag();

    /// <summary>Move a node to <paramref name="row"/> at chain index <paramref name="indexInRow"/> (row past the end = new row).</summary>
    Task MoveNodeAsync(string nodeId, int row, int indexInRow);

    /// <summary>Start a new row at this node (it and the rest of its row move down).</summary>
    Task SplitRowAtAsync(string nodeId);

    /// <summary>Append a row's seats to the previous row.</summary>
    Task MergeRowIntoPreviousAsync(int row);

    /// <summary>Delete a row; its seats join the neighbouring row.</summary>
    Task DeleteRowAsync(int row);

    /// <summary>Swap a row with its neighbour (<paramref name="delta"/> = -1 up, +1 down).</summary>
    Task MoveRowAsync(int row, int delta);

    /// <summary>Toggle a row between Facing and SameSide.</summary>
    Task ToggleRowFacingAsync(int row);

    /// <summary>Rename a row; blank names are ignored.</summary>
    Task RenameRowAsync(int row, string name);

    /// <summary>Set the bezel gap (cm) between this node and its previous node in the chain.</summary>
    Task SetGapCmAsync(string nodeId, int cm);

    /// <summary>Stop what the given nodes show.</summary>
    Task ClearNodesAsync(IReadOnlyCollection<string> nodeIds);

    /// <summary>Re-send the running animation to the given remote nodes.</summary>
    Task ResyncNodesAsync(IReadOnlyCollection<string> nodeIds);

    /// <summary>Fetch and show a remote node's logs.</summary>
    Task ShowNodeLogsAsync(string nodeId);
}

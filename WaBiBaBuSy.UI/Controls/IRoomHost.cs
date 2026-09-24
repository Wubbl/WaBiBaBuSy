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
    ObservableCollection<ClientNodeViewModel> Clients { get; }
    SeatMap SeatMap { get; }

    /// <summary>Bumped on every seat-map change; the view re-lays out when it changes.</summary>
    int SeatMapVersion { get; }

    SeatMapLayoutResult BuildSeatLayout(IEnumerable<ClientNodeViewModel> clients);

    /// <summary>Short warning shown as ⚠ on the tile (e.g. mixed refresh rates), or null.</summary>
    string? NodeWarning(string nodeId);

    /// <summary>True when remote logs can be requested for this node (server mode, remote node).</summary>
    bool CanFetchLogs(string nodeId);

    void SelectOnly(string nodeId);
    void ToggleSelection(string nodeId);
    void AddToSelection(string nodeId);
    void SetRubberBandSelection(IReadOnlyCollection<string> nodeIds, bool additive);
    void ClearSelection();

    /// <summary>Pause topology refreshes while a tile is dragged.</summary>
    void BeginNodeDrag();
    void EndNodeDrag();

    Task MoveNodeAsync(string nodeId, int row, int indexInRow);
    Task SplitRowAtAsync(string nodeId);
    Task MergeRowIntoPreviousAsync(int row);
    Task DeleteRowAsync(int row);
    Task MoveRowAsync(int row, int delta);
    Task ToggleRowFacingAsync(int row);
    Task RenameRowAsync(int row, string name);
    Task SetGapCmAsync(string nodeId, int cm);

    Task ClearNodesAsync(IReadOnlyCollection<string> nodeIds);
    Task ResyncNodesAsync(IReadOnlyCollection<string> nodeIds);
    Task ShowNodeLogsAsync(string nodeId);
}

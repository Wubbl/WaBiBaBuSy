// WaBiBaBuSy.UI/Controls/RoomView.Menus.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>Menus and inline editors of the room view: node context menu, lane chips, gap editor.</summary>
public partial class RoomView
{
    partial void HandleSecondaryPress(TileBox tile)
    {
        var host = _host;
        if (host == null) return;
        var ids = host.Clients.Where(c => c.IsSelected).Select(c => c.ClientId).ToList();
        if (ids.Count == 0) ids.Add(tile.Id);
        string suffix = ids.Count > 1 ? $" ({ids.Count})" : "";

        var items = new List<Control>
        {
            Item("Start new row here", () => host.SplitRowAtAsync(tile.Id), tile.ChainIndex > 0),
            Item("Merge into previous row", () => host.MergeRowIntoPreviousAsync(tile.Row), tile.Row > 0),
            new Separator(),
            Item("Clear wallpaper" + suffix, () => host.ClearNodesAsync(ids)),
            Item("Resync" + suffix, () => host.ResyncNodesAsync(ids)),
            Item("View logs", () => host.ShowNodeLogsAsync(tile.Id), host.CanFetchLogs(tile.Id)),
        };
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    partial void HandleChromePress(Point p, ref bool handled)
    {
        var host = _host;
        if (host == null) return;

        var gap = _grid.HitGap(p.X, p.Y);
        if (gap != null)
        {
            OpenGapEditor(gap);
            handled = true;
            return;
        }

        foreach (var lane in _grid.Lanes)
        {
            if (lane.Row > 0 && lane.FacingChip.Contains(p.X, p.Y))
            {
                _ = RunSafe(host.ToggleRowFacingAsync(lane.Row));
                handled = true;
                return;
            }
            if (lane.MenuChip.Contains(p.X, p.Y))
            {
                OpenLaneMenu(lane);
                handled = true;
                return;
            }
        }
    }

    partial void HandleLaneSecondaryPress(LaneBox lane) => OpenLaneMenu(lane);

    private void OpenLaneMenu(LaneBox lane)
    {
        var host = _host!;
        var items = new List<Control>
        {
            Item("Rename…", () => { OpenRenameEditor(lane); return Task.CompletedTask; }),
            Item("Move row up", () => host.MoveRowAsync(lane.Row, -1), lane.Row > 0),
            Item("Move row down", () => host.MoveRowAsync(lane.Row, +1), lane.Row < _grid.Lanes.Count - 1),
            new Separator(),
            Item("Delete row (seats join the neighbouring row)", () => host.DeleteRowAsync(lane.Row), _grid.Lanes.Count > 1),
        };
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    private void OpenGapEditor(GapBox gap)
    {
        var host = _host!;
        var client = host.Clients.FirstOrDefault(c => c.ClientId == gap.HolderId);
        if (client == null) return;

        var input = new NumericUpDown
        {
            Minimum = 0, Maximum = SeatMapEditor.MaxGapCm, Increment = 1,
            Value = client.PhysicalDistanceCm, FormatString = "0", Width = 130
        };
        var ok = new Button { Content = "Set" };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = $"Gap before {client.DisplayName} (cm)", FontSize = 12 },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { input, ok } }
                }
            }
        };

        void Commit()
        {
            flyout.Hide();
            _ = RunSafe(host.SetGapCmAsync(gap.HolderId, (int)(input.Value ?? 0)));   // blank → 0, clamped in the host
        }
        ok.Click += (_, _) => Commit();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        flyout.Placement = PlacementMode.Pointer;   // Avalonia 12.1.2 has no ShowAt(Control, bool); Placement is the equivalent.
        flyout.ShowAt(this);
    }

    private void OpenRenameEditor(LaneBox lane)
    {
        var host = _host!;
        string current = lane.Row < host.SeatMap.Rows.Count ? host.SeatMap.Rows[lane.Row].Name : $"Row {lane.Row + 1}";
        var input = new TextBox { Text = current, Width = 180 };
        var ok = new Button { Content = "Rename" };
        var flyout = new Flyout
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { input, ok } }
        };

        void Commit()
        {
            flyout.Hide();
            _ = RunSafe(host.RenameRowAsync(lane.Row, input.Text ?? string.Empty));
        }
        ok.Click += (_, _) => Commit();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        flyout.Placement = PlacementMode.Pointer;   // Avalonia 12.1.2 has no ShowAt(Control, bool); Placement is the equivalent.
        flyout.ShowAt(this);
    }

    private MenuItem Item(string header, Func<Task> action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => _ = RunSafe(action());
        return item;
    }
}

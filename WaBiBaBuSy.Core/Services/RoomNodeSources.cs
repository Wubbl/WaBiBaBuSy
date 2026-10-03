using System;
using System.Collections.Generic;
using System.Linq;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.Core.Services;

/// <summary>
/// The room's node list from its sources — this machine's monitors and, in server mode, the connected clients
/// (multi-monitor clients expanded into one node per monitor). Shared by the control panel and the playback
/// host, which builds the room itself when no window is open (tray Quick start).
/// </summary>
public static class RoomNodeSources
{
    /// <summary>Server mode: one node per server monitor, then the clients (multi-monitor clients expanded).</summary>
    public static List<ConnectedClient> ServerNodes(LocalScreens local, IEnumerable<ConnectedClient> clients,
        Func<string, int?> persistedOrder, Func<string, int?> persistedGapCm, IReadOnlyDictionary<string, int> sessionGapCm)
    {
        var screen = ScreenConfig(local);
        var nodes = new List<ConnectedClient>();
        for (int i = 0; i < local.Monitors.Count; i++)
        {
            var nodeId = $"{NodeIds.ServerLocalPrefix}{i}";
            nodes.Add(new ConnectedClient
            {
                ClientId = nodeId,
                Hostname = $"{local.MachineName} - Monitor {i + 1} (Server)",
                IpAddress = local.Monitors[i].IsPrimary ? "Primary Monitor (Server)" : $"Monitor {i + 1} (Server)",
                Status = ClientStatusEnum.ClientConnected,
                // Use persisted order if available, otherwise default to index
                OrderPosition = persistedOrder(nodeId) ?? i,
                PhysicalDistanceCm = persistedGapCm(nodeId) ?? sessionGapCm.GetValueOrDefault(nodeId),
                ScreenConfig = screen
            });
        }

        int nextOrder = local.Monitors.Count;
        foreach (var client in clients)
        {
            int clientMonitorCount = client.ScreenConfig?.MonitorCount ?? 1;
            if (clientMonitorCount > 1 && client.ScreenConfig?.Monitors.Count > 0)
            {
                // Expand into one node per monitor
                for (int m = 0; m < client.ScreenConfig.Monitors.Count; m++)
                {
                    var expandedId = $"{client.ClientId}_MONITOR_{m}";
                    var expandedOrder = persistedOrder(expandedId) ?? nextOrder;
                    nextOrder++;
                    nodes.Add(new ConnectedClient
                    {
                        ClientId = expandedId,
                        Hostname = $"{client.Hostname} - Monitor {m + 1}",
                        IpAddress = client.IpAddress,
                        Status = client.Status,
                        OrderPosition = expandedOrder,
                        // Expanded nodes are not connected clients: their gap lives only in the topology store.
                        PhysicalDistanceCm = persistedGapCm(expandedId) ?? client.PhysicalDistanceCm,
                        ScreenConfig = client.ScreenConfig,
                        ClockOffsetMs = client.ClockOffsetMs,
                        RttMs = client.RttMs,
                        LastDriftReportUtc = client.LastDriftReportUtc
                    });
                }
            }
            else
            {
                // Single monitor client - use server's OrderPosition directly
                nodes.Add(new ConnectedClient
                {
                    ClientId = client.ClientId,
                    Hostname = client.Hostname,
                    IpAddress = client.IpAddress,
                    Status = client.Status,
                    OrderPosition = client.OrderPosition,
                    PhysicalDistanceCm = client.PhysicalDistanceCm,
                    ScreenConfig = client.ScreenConfig,
                    ClockOffsetMs = client.ClockOffsetMs,
                    RttMs = client.RttMs,
                    LastDriftReportUtc = client.LastDriftReportUtc
                });
            }
        }
        return nodes;
    }

    /// <summary>No server: one node per local monitor. No store, so gaps set in the room live for the session.</summary>
    public static List<ConnectedClient> StandaloneNodes(LocalScreens local, IReadOnlyDictionary<string, int> sessionGapCm)
    {
        var screen = ScreenConfig(local);
        return local.Monitors.Select((monitor, i) =>
        {
            var nodeId = $"{NodeIds.LocalMachinePrefix}{i}";
            return new ConnectedClient
            {
                ClientId = nodeId,
                Hostname = $"{local.MachineName} - Monitor {i + 1}",
                IpAddress = monitor.IsPrimary ? "Primary Monitor" : $"Monitor {i + 1}",
                Status = ClientStatusEnum.ClientConnected,
                OrderPosition = i,
                PhysicalDistanceCm = sessionGapCm.GetValueOrDefault(nodeId),
                ScreenConfig = screen   // shared by every local node
            };
        }).ToList();
    }

    /// <summary>
    /// The monitor a node shows: the index from a <c>…MONITOR_n</c> id (info when the client reported it), else the
    /// only monitor of a single-monitor client, else (-1, null).
    /// </summary>
    public static (int Index, MonitorInfo? Monitor) MonitorFor(ConnectedClient client)
    {
        var id = client.ClientId;
        string? suffix = null;
        if (id.StartsWith(NodeIds.LocalMachinePrefix, StringComparison.Ordinal)) suffix = id[NodeIds.LocalMachinePrefix.Length..];
        else if (id.StartsWith(NodeIds.ServerLocalPrefix, StringComparison.Ordinal)) suffix = id[NodeIds.ServerLocalPrefix.Length..];
        else if (id.Contains("_MONITOR_", StringComparison.Ordinal)) suffix = id[(id.LastIndexOf("_MONITOR_", StringComparison.Ordinal) + "_MONITOR_".Length)..];

        var monitors = client.ScreenConfig?.Monitors;
        if (suffix != null && int.TryParse(suffix, out int index))
            return (index, monitors != null && monitors.Count > index ? monitors[index] : null);
        if (monitors?.Count == 1) return (0, monitors[0]);
        return (-1, null);
    }

    /// <summary>A node as the playback host lays scenes out over it.</summary>
    public static RoomNode ToRoomNode(ConnectedClient client)
    {
        var (index, monitor) = MonitorFor(client);
        return new RoomNode
        {
            Id = client.ClientId,
            Hostname = client.Hostname,
            Order = client.OrderPosition,
            MonitorIndex = index,
            WidthPx = monitor?.Width ?? 0,
            HeightPx = monitor?.Height ?? 0,
            RefreshHz = monitor?.RefreshRate ?? 0,
            GapBeforeCm = client.PhysicalDistanceCm,
            PixelsPerCm = monitor?.PixelsPerCm ?? 0f,
            IsConnected = client.Status is ClientStatusEnum.ClientConnected or ClientStatusEnum.ClientPlaying,
            RttMs = client.RttMs,
            ClockOffsetMs = client.ClockOffsetMs
        };
    }

    private static ScreenConfiguration ScreenConfig(LocalScreens local)
    {
        var screen = new ScreenConfiguration
        {
            MonitorCount = local.Monitors.Count,
            TotalWidth = local.VirtualWidth,
            TotalHeight = local.VirtualHeight
        };
        foreach (var m in local.Monitors)
        {
            screen.Monitors.Add(new MonitorInfo
            {
                Index = m.Index,
                Width = m.Width,
                Height = m.Height,
                X = m.X,
                Y = m.Y,
                IsPrimary = m.IsPrimary,
                DeviceName = m.DeviceName,
                RefreshRate = m.RefreshHz
            });
        }
        return screen;
    }
}

namespace WaBiBaBuSy.Models.Topology;

/// <summary>
/// One node of the room as the server's playback host sees it: a local monitor, a remote client or an
/// expanded monitor of a multi-monitor client. A snapshot of the control panel's topology, so scenes and
/// shows keep applying (and test runs keep listing nodes) while the main window is closed.
/// </summary>
public sealed record RoomNode
{
    /// <summary>Client id (<c>SERVER_LOCALHOST_MONITOR_n</c>, <c>LOCAL_MACHINE_MONITOR_n</c>, a client id, or <c>{client}_MONITOR_n</c>).</summary>
    public required string Id { get; init; }
    public string Hostname { get; init; } = string.Empty;
    /// <summary>Position in the chain (topology order).</summary>
    public int Order { get; init; }
    /// <summary>Monitor index on its machine; -1 = whole-machine node.</summary>
    public int MonitorIndex { get; init; } = -1;
    /// <summary>0 = unknown.</summary>
    public int WidthPx { get; init; }
    /// <summary>0 = unknown.</summary>
    public int HeightPx { get; init; }
    public int RefreshHz { get; init; }
    /// <summary>Bezel / physical gap to the previous node in the chain (cm).</summary>
    public int GapBeforeCm { get; init; }
    /// <summary>Pixels per cm the node reported; 0 = unknown.</summary>
    public float PixelsPerCm { get; init; }
    public bool IsConnected { get; init; }
    /// <summary>
    /// An offline machine's seat: it keeps its place and size in the layout, but nothing is sent to it (the
    /// command is only parked for its session resume).
    /// </summary>
    public bool IsGhost { get; init; }
    public double RttMs { get; init; }
    public double ClockOffsetMs { get; init; }

    /// <summary>True for the server's (or the standalone machine's) own monitors.</summary>
    public bool IsLocal => NodeIds.IsLocalMonitor(Id);
}

/// <summary>Node id conventions for the machine's own monitors.</summary>
public static class NodeIds
{
    /// <summary>Local monitors without a server running.</summary>
    public const string LocalMachinePrefix = "LOCAL_MACHINE_MONITOR_";
    /// <summary>The server's own monitors.</summary>
    public const string ServerLocalPrefix = "SERVER_LOCALHOST_MONITOR_";

    /// <summary>True when the id is one of this machine's monitors (standalone or server mode).</summary>
    public static bool IsLocalMonitor(string id) =>
        id.StartsWith(LocalMachinePrefix, StringComparison.Ordinal) || id.StartsWith(ServerLocalPrefix, StringComparison.Ordinal);

    /// <summary>The monitor index of a local monitor id; -1 for any other id.</summary>
    public static int MonitorIndex(string id)
    {
        var suffix = id.StartsWith(LocalMachinePrefix, StringComparison.Ordinal) ? id[LocalMachinePrefix.Length..]
                   : id.StartsWith(ServerLocalPrefix, StringComparison.Ordinal) ? id[ServerLocalPrefix.Length..]
                   : null;
        return suffix != null && int.TryParse(suffix, out var index) ? index : -1;
    }
}

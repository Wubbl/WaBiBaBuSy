namespace WaBiBaBuSy.Models.Topology;

/// <summary>One of this machine's monitors as the room sees it (screen bounds + refresh rate).</summary>
public sealed record LocalMonitor(int Index, int Width, int Height, int X, int Y, bool IsPrimary, string DeviceName, int RefreshHz);

/// <summary>This machine's monitors, the virtual screen size and the machine name (node labels).</summary>
public sealed record LocalScreens(IReadOnlyList<LocalMonitor> Monitors, int VirtualWidth, int VirtualHeight, string MachineName);

using System;
using System.Collections.Generic;
using System.Linq;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.UI.Services;

/// <summary>This machine's monitors (WinForms bounds + native refresh rate) and the physical canvas's reference DPI.</summary>
public static class LocalMonitors
{
    private static float? _referencePixelsPerCm;

    /// <summary>
    /// The monitors now, left to right (<see cref="NativeMonitorInfo.GetAllMonitors"/> order — the order local node
    /// ids, the players, the busy probe and remote clients all use). Not WinForms <c>Screen.AllScreens</c>: that is
    /// in enumeration order (the primary is not always first) and cached, so its index 1 could be the primary.
    /// </summary>
    public static LocalScreens Snapshot()
    {
        var monitors = NativeMonitorInfo.GetAllMonitors().Select((s, i) => new LocalMonitor(i, s.Bounds.Width, s.Bounds.Height,
            s.Bounds.X, s.Bounds.Y, s.IsPrimary, s.DeviceName, s.RefreshRateHz)).ToList();
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        return new LocalScreens(monitors, virtualScreen.Width, virtualScreen.Height, Environment.MachineName);
    }

    /// <summary>
    /// Pixels per cm of the primary monitor — the reference DPI of a physical canvas. Cached (monitor DPI does not
    /// change at runtime). 0 when it cannot be queried.
    /// </summary>
    public static float ReferencePixelsPerCm
    {
        get
        {
            if (_referencePixelsPerCm.HasValue) return _referencePixelsPerCm.Value;
            try
            {
                var monitors = NativeMonitorInfo.GetAllMonitors();
                var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
                _referencePixelsPerCm = primary?.PixelsPerCm ?? 0f;
            }
            catch { _referencePixelsPerCm = 0f; }
            return _referencePixelsPerCm.Value;
        }
    }
}

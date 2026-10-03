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

    /// <summary>The monitors now, in WinForms order (the order local node ids use).</summary>
    public static LocalScreens Snapshot()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var refreshByDevice = NativeMonitorInfo.GetAllMonitors()
            .GroupBy(m => m.DeviceName)
            .ToDictionary(g => g.Key, g => g.First().RefreshRateHz);
        var monitors = screens.Select((s, i) => new LocalMonitor(i, s.Bounds.Width, s.Bounds.Height, s.Bounds.X, s.Bounds.Y,
            s.Primary, s.DeviceName, refreshByDevice.GetValueOrDefault(s.DeviceName))).ToList();
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

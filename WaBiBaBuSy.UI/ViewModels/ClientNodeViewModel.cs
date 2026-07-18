using CommunityToolkit.Mvvm.ComponentModel;
using WaBiBaBuSy.Models.Networking;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// Represents a client node in the network topology graph
/// </summary>
public partial class ClientNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _thumbnailImage;

    [ObservableProperty]
    private string _clientId = string.Empty;

    [ObservableProperty]
    private string _hostname = string.Empty;

    [ObservableProperty]
    private string _ipAddress = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _status = "Disconnected";

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private int _order;

    [ObservableProperty]
    private string? _currentWallpaper;

    [ObservableProperty]
    private int _physicalDistanceCm;

    [ObservableProperty]
    private int _monitorIndex = -1; // -1 means all monitors (legacy mode)

    [ObservableProperty]
    private string? _monitorName;

    [ObservableProperty]
    private int _monitorWidth;

    [ObservableProperty]
    private int _monitorHeight;

    [ObservableProperty]
    private bool _isPrimaryMonitor;

    /// <summary>
    /// Refresh rate of this monitor in Hz. 0 if unknown / not yet reported by client.
    /// </summary>
    [ObservableProperty]
    private int _monitorRefreshHz;

    /// <summary>
    /// Physical pixels per cm of this monitor (from the client's GetDpiForMonitor).
    /// 0 if unknown — the server then falls back to its own first monitor's DPI
    /// for bezel/distance gap math.
    /// </summary>
    [ObservableProperty]
    private float _pixelsPerCm;

    [ObservableProperty]
    private bool _isAnimating;

    [ObservableProperty]
    private bool _isCurrentAnimationTarget;

    /// <summary>
    /// Accent color assigned when this node shares a machine with other nodes (multi-monitor).
    /// Null for single-monitor machines (no color coding needed).
    /// </summary>
    [ObservableProperty]
    private Avalonia.Media.Color? _groupColor;

    [ObservableProperty]
    private string? _activeAnimationName;

    /// <summary>Last reported clock offset (server − client), ms. Only meaningful when DriftState is not None.</summary>
    [ObservableProperty]
    private double _driftMs;

    /// <summary>Heartbeat round-trip time of the client's best clock sample, ms.</summary>
    [ObservableProperty]
    private double _rttMs;

    /// <summary>Sync-quality classification of the last drift report.</summary>
    [ObservableProperty]
    private DriftState _driftState = DriftState.None;

    /// <summary>
    /// Display name for the client
    /// </summary>
    public string DisplayName
    {
        get
        {
            var baseName = string.IsNullOrEmpty(Hostname) ? IpAddress : Hostname;
            if (MonitorIndex >= 0 && !string.IsNullOrEmpty(MonitorName))
            {
                return $"{baseName} - {MonitorName}";
            }
            return baseName;
        }
    }

    /// <summary>
    /// Display name for the monitor
    /// </summary>
    public string MonitorDisplayName
    {
        get
        {
            if (MonitorIndex < 0)
                return "All Monitors";

            var primary = IsPrimaryMonitor ? " (Primary)" : "";
            var hz = MonitorRefreshHz > 0 ? $" @ {MonitorRefreshHz}Hz" : "";
            return $"Monitor {MonitorIndex + 1}: {MonitorWidth}x{MonitorHeight}{hz}{primary}";
        }
    }

    /// <summary>
    /// Calculate delay in milliseconds based on physical distance and animation speed
    /// </summary>
    public long CalculateDelayMs(int animationSpeedCmPerSec)
    {
        if (animationSpeedCmPerSec <= 0 || PhysicalDistanceCm <= 0)
            return 0;

        // delay (ms) = (distance (cm) / speed (cm/s)) * 1000
        return (long)((PhysicalDistanceCm / (double)animationSpeedCmPerSec) * 1000);
    }
}

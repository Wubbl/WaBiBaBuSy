using CommunityToolkit.Mvvm.ComponentModel;

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

    [ObservableProperty]
    private bool _isAnimating;

    [ObservableProperty]
    private bool _isCurrentAnimationTarget;

    [ObservableProperty]
    private string? _activeAnimationName;

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
            return $"Monitor {MonitorIndex + 1}: {MonitorWidth}x{MonitorHeight}{primary}";
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

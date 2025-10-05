using CommunityToolkit.Mvvm.ComponentModel;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// Represents a client node in the network topology graph
/// </summary>
public partial class ClientNodeViewModel : ObservableObject
{
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

    /// <summary>
    /// Display name for the client
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(Hostname) ? IpAddress : Hostname;

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

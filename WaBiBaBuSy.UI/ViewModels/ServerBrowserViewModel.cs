using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Core.Services.Networking;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// ViewModel for the mDNS server-browser dialog. Starts discovery on creation,
/// live-updates the list as servers appear/disappear, and exposes the selection
/// the user confirmed (double-click or Connect button).
/// </summary>
public partial class ServerBrowserViewModel : ObservableObject, IDisposable
{
    private readonly MdnsClientDiscoveryService _discovery;
    private Action? _closeAction;

    public ObservableCollection<DiscoveredServerItem> Servers { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private DiscoveredServerItem? _selectedServer;

    [ObservableProperty]
    private string _statusText = "Searching for servers on the local network...";

    /// <summary>True when the user confirmed a server (vs. cancelled).</summary>
    public bool DialogResult { get; private set; }

    public ServerBrowserViewModel()
    {
        _discovery = new MdnsClientDiscoveryService(
            AppLogger.CreateLogger<MdnsClientDiscoveryService>());
        _discovery.ServerDiscovered += OnServerDiscovered;
        _discovery.ServerLost += OnServerLost;

        try
        {
            _discovery.StartDiscovery();
        }
        catch (Exception ex)
        {
            StatusText = $"Discovery failed to start: {ex.Message}";
            Debug.WriteLine($"[ServerBrowser] StartDiscovery failed: {ex}");
        }
    }

    public void SetCloseAction(Action closeAction) => _closeAction = closeAction;

    private void OnServerDiscovered(object? sender, ServerDiscoveredEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Servers.Any(s => s.InstanceName == e.Server.InstanceName))
                return;
            Servers.Add(DiscoveredServerItem.From(e.Server));
            StatusText = $"{Servers.Count} server(s) found";
        });
    }

    private void OnServerLost(object? sender, ServerLostEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = Servers.FirstOrDefault(s => s.InstanceName == e.Server.InstanceName);
            if (item != null)
                Servers.Remove(item);
            StatusText = Servers.Count > 0
                ? $"{Servers.Count} server(s) found"
                : "Searching for servers on the local network...";
        });
    }

    [RelayCommand]
    private void Refresh()
    {
        Servers.Clear();
        StatusText = "Searching for servers on the local network...";
        try
        {
            _discovery.StopDiscovery();
            _discovery.StartDiscovery();
        }
        catch (Exception ex)
        {
            StatusText = $"Discovery failed: {ex.Message}";
            Debug.WriteLine($"[ServerBrowser] Refresh failed: {ex}");
        }
    }

    private bool CanConnect() => SelectedServer != null;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        if (SelectedServer == null) return;
        DialogResult = true;
        _closeAction?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        _closeAction?.Invoke();
    }

    public void Dispose()
    {
        _discovery.ServerDiscovered -= OnServerDiscovered;
        _discovery.ServerLost -= OnServerLost;
        try { _discovery.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"[ServerBrowser] Dispose failed: {ex.Message}"); }
    }
}

/// <summary>One row in the server browser list.</summary>
public class DiscoveredServerItem
{
    public string InstanceName { get; init; } = string.Empty;
    public string Hostname { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }

    /// <summary>Address to connect to: prefer the resolved IP, fall back to the mDNS hostname.</summary>
    public string ConnectAddress =>
        !string.IsNullOrEmpty(IpAddress) ? IpAddress : Hostname.TrimEnd('.');

    public string DisplayName
    {
        get
        {
            // Instance names look like "hostname._wabibabusy._tcp.local" — show the leading label.
            var name = InstanceName.Split('.')[0];
            return string.IsNullOrEmpty(name) ? Hostname.TrimEnd('.') : name;
        }
    }

    public string DisplayEndpoint => $"{ConnectAddress}:{Port}";

    public static DiscoveredServerItem From(DiscoveredServer server) => new()
    {
        InstanceName = server.InstanceName,
        Hostname = server.Hostname,
        IpAddress = server.IpAddress,
        Port = server.Port
    };
}

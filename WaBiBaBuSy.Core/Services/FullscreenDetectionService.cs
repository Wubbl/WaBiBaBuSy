using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.Core.Services;

/// <summary>
/// Monitors the foreground window and detects when a fullscreen application is active.
/// Raises events so the wallpaper rendering can be paused/resumed.
/// </summary>
public class FullscreenDetectionService : IDisposable
{
    private const int CHECK_INTERVAL_MS = 2000; // Check every 2 seconds

    private readonly ILogger<FullscreenDetectionService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private bool _isFullscreenActive;

    /// <summary>
    /// Raised when a fullscreen application is detected or closes.
    /// </summary>
    public event EventHandler<FullscreenStateChangedEventArgs>? FullscreenStateChanged;

    /// <summary>
    /// Whether a fullscreen app is currently detected.
    /// </summary>
    public bool IsFullscreenActive => _isFullscreenActive;

    /// <summary>
    /// Whether the detection loop is currently running.
    /// </summary>
    public bool IsRunning => _monitorTask != null;

    public FullscreenDetectionService(ILogger<FullscreenDetectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start monitoring for fullscreen applications.
    /// </summary>
    public void Start()
    {
        if (_monitorTask != null)
            return;

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
        _logger.LogInformation("Fullscreen detection started (checking every {Interval}ms)", CHECK_INTERVAL_MS);
    }

    /// <summary>
    /// Stop monitoring.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _monitorTask = null;

        // If we were in fullscreen state, reset it
        if (_isFullscreenActive)
        {
            _isFullscreenActive = false;
            FullscreenStateChanged?.Invoke(this, new FullscreenStateChangedEventArgs(false, null));
        }

        _logger.LogInformation("Fullscreen detection stopped");
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CHECK_INTERVAL_MS, ct);
                CheckFullscreen();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in fullscreen detection loop");
            }
        }
    }

    private void CheckFullscreen()
    {
        var isFullscreen = IsFullscreenAppRunning(out var processName);

        if (isFullscreen != _isFullscreenActive)
        {
            _isFullscreenActive = isFullscreen;

            if (isFullscreen)
                _logger.LogInformation("Fullscreen app detected: {ProcessName}", processName);
            else
                _logger.LogInformation("Fullscreen app closed, resuming wallpaper");

            FullscreenStateChanged?.Invoke(this, new FullscreenStateChangedEventArgs(isFullscreen, processName));
        }
    }

    private static bool IsFullscreenAppRunning(out string? processName)
    {
        processName = null;

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
            return false;

        // Ignore desktop and shell windows
        var shellWindow = GetShellWindow();
        var desktopWindow = GetDesktopWindow();
        if (foregroundWindow == shellWindow || foregroundWindow == desktopWindow)
            return false;

        // Get the window class name to filter out explorer, taskbar, etc.
        var className = GetWindowClassName(foregroundWindow);
        if (IsSystemWindow(className))
            return false;

        // Get the window rect and the monitor it's on
        if (!GetWindowRect(foregroundWindow, out var windowRect))
            return false;

        var monitor = MonitorFromWindow(foregroundWindow, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        var screenRect = monitorInfo.rcMonitor;

        // Check if the window covers the entire monitor
        bool coversScreen = windowRect.Left <= screenRect.Left
            && windowRect.Top <= screenRect.Top
            && windowRect.Right >= screenRect.Right
            && windowRect.Bottom >= screenRect.Bottom;

        if (coversScreen)
        {
            // Get the process name for logging
            GetWindowThreadProcessId(foregroundWindow, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch
            {
                processName = $"PID:{pid}";
            }
        }

        return coversScreen;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        int length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static bool IsSystemWindow(string className)
    {
        // Ignore Windows shell, taskbar, and system UI
        return className is
            "Progman" or          // Desktop
            "WorkerW" or          // Desktop worker
            "Shell_TrayWnd" or    // Taskbar
            "Shell_SecondaryTrayWnd" or // Secondary taskbar
            "NotifyIconOverflowWindow" or
            "Windows.UI.Core.CoreWindow" or // Start menu, action center
            "XamlExplorerHostIslandWindow" or
            "ForegroundStaging";
    }

    public void Dispose()
    {
        Stop();
    }

    #region P/Invoke

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    #endregion
}

public class FullscreenStateChangedEventArgs : EventArgs
{
    public bool IsFullscreen { get; }
    public string? ProcessName { get; }

    public FullscreenStateChangedEventArgs(bool isFullscreen, string? processName)
    {
        IsFullscreen = isFullscreen;
        ProcessName = processName;
    }
}

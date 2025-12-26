using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Direct2D;

/// <summary>
/// Hosts the separate D2D player process and manages its lifecycle.
/// The player process creates a DXGI swap chain window which can be safely
/// parented to the desktop without crashing explorer.exe on Windows 11 24H2+.
/// </summary>
public class D2DPlayerHost : IDisposable
{
    private readonly ILogger<D2DPlayerHost> _logger;
    private readonly DesktopWindowManager _desktopWindowManager;
    private readonly ScreenMapping _screen;

    private Process? _playerProcess;
    private IntPtr _playerHwnd = IntPtr.Zero;
    private StreamWriter? _playerStdin;
    private bool _disposed;
    private bool _isInitialized;

    public D2DPlayerHost(
        ScreenMapping screen,
        ILogger<D2DPlayerHost> logger,
        DesktopWindowManager desktopWindowManager)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _desktopWindowManager = desktopWindowManager ?? throw new ArgumentNullException(nameof(desktopWindowManager));
    }

    /// <summary>
    /// Gets whether the player process is running and initialized.
    /// </summary>
    public bool IsRunning => _playerProcess != null && !_playerProcess.HasExited && _isInitialized;

    /// <summary>
    /// Gets the HWND of the player window.
    /// </summary>
    public IntPtr PlayerHwnd => _playerHwnd;

    /// <summary>
    /// Initializes the player process and parents its window to the desktop.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            _logger.LogWarning("D2DPlayerHost already initialized");
            return;
        }

        try
        {
            _logger.LogInformation("Initializing D2DPlayerHost for screen {Order} ({Width}x{Height})",
                _screen.Order, _screen.ScreenBounds.Width, _screen.ScreenBounds.Height);

            // Find the player executable
            var playerPath = FindPlayerExecutable();
            _logger.LogInformation("Player executable: {Path}", playerPath);

            // Prepare command line arguments
            var bounds = _screen.ScreenBounds;
            var args = $"--bounds {bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}";

            // Start the player process
            _playerProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = playerPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            _playerProcess.Exited += OnPlayerExited;
            _playerProcess.ErrorDataReceived += OnPlayerError;

            _logger.LogInformation("Starting player process...");
            _playerProcess.Start();
            _playerProcess.BeginErrorReadLine();

            _playerStdin = _playerProcess.StandardInput;

            // Wait for HWND from player
            _logger.LogInformation("Waiting for player HWND...");
            var hwnd = await WaitForHwndAsync(_playerProcess.StandardOutput, cancellationToken);
            _playerHwnd = new IntPtr(hwnd);
            _logger.LogInformation("Received player HWND: {Hwnd}", _playerHwnd);

            // Parent the player window to the desktop
            await ParentToDesktopAsync();

            _isInitialized = true;
            _logger.LogInformation("D2DPlayerHost initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize D2DPlayerHost");
            Dispose();
            throw;
        }
    }

    private string FindPlayerExecutable()
    {
        // Look for the player in common locations
        var possiblePaths = new[]
        {
            // Same directory as the current executable
            Path.Combine(AppContext.BaseDirectory, "WaBiBaBuSy.Player.D2D.exe"),
            // Development: relative to WallpaperEngine output
            Path.Combine(AppContext.BaseDirectory, "..", "WaBiBaBuSy.Player.D2D", "bin", "Debug", "net9.0-windows", "WaBiBaBuSy.Player.D2D.exe"),
            // Development: from solution root
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WaBiBaBuSy.Player.D2D", "bin", "Debug", "net9.0-windows", "WaBiBaBuSy.Player.D2D.exe"),
        };

        foreach (var path in possiblePaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
            _logger.LogDebug("Player not found at: {Path}", fullPath);
        }

        throw new FileNotFoundException(
            "Could not find WaBiBaBuSy.Player.D2D.exe. Make sure the player project is built.",
            "WaBiBaBuSy.Player.D2D.exe");
    }

    private async Task<long> WaitForHwndAsync(StreamReader stdout, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(cts.Token);
                if (line == null)
                {
                    throw new InvalidOperationException("Player process closed stdout unexpectedly");
                }

                _logger.LogDebug("Player output: {Line}", line);

                if (line.StartsWith("HWND:"))
                {
                    var hwndStr = line.Substring(5);
                    if (long.TryParse(hwndStr, out var hwnd))
                    {
                        return hwnd;
                    }
                    throw new InvalidOperationException($"Invalid HWND format: {hwndStr}");
                }
                else if (line.StartsWith("ERROR:"))
                {
                    throw new InvalidOperationException($"Player error: {line.Substring(6)}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Timed out waiting for player HWND after {timeout.TotalSeconds} seconds");
        }

        throw new InvalidOperationException("Failed to receive HWND from player");
    }

    private async Task ParentToDesktopAsync()
    {
        if (_playerHwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Player HWND not available");
        }

        _logger.LogInformation("Sending PARENT command to player...");

        // Find the desktop windows
        var workerW = _desktopWindowManager.FindDesktopWorkerWindow();
        if (workerW == IntPtr.Zero)
        {
            _logger.LogWarning("Failed to find WorkerW window");
            return;
        }

        // Get the parent window (Progman in layered mode) and z-order window (DefView)
        var progman = _desktopWindowManager.ProgmanHandle;
        var defView = _desktopWindowManager.ShellDllDefViewHandle;

        _logger.LogInformation("Desktop handles - Progman: {Progman}, DefView: {DefView}, WorkerW: {WorkerW}",
            progman, defView, workerW);

        // Send PARENT command to player - player will do the parenting itself
        // This avoids cross-process SetParent issues
        var parentHwnd = _desktopWindowManager.IsLayeredDesktopMode ? progman : workerW;
        var zOrderHwnd = _desktopWindowManager.IsLayeredDesktopMode ? defView : IntPtr.Zero;

        _logger.LogInformation("Sending PARENT:{Parent},{ZOrder} to player (layered mode: {Layered})",
            parentHwnd.ToInt64(), zOrderHwnd.ToInt64(), _desktopWindowManager.IsLayeredDesktopMode);

        await SendCommandAsync($"PARENT:{parentHwnd.ToInt64()},{zOrderHwnd.ToInt64()}");

        // Wait for response
        if (_playerProcess != null)
        {
            var response = await _playerProcess.StandardOutput.ReadLineAsync();
            _logger.LogInformation("Player PARENT response: {Response}", response);

            if (response?.StartsWith("ERROR:") == true)
            {
                _logger.LogError("Player failed to parent: {Error}", response);
            }
            else
            {
                _logger.LogInformation("Player successfully parented to desktop");
            }
        }
    }

    /// <summary>
    /// Sends a color command to the player to fill the screen with a solid color.
    /// </summary>
    /// <param name="color">The color to fill with</param>
    public async Task SetColorAsync(Color color)
    {
        if (!IsRunning)
        {
            _logger.LogWarning("Cannot send command: player not running");
            return;
        }

        var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        await SendCommandAsync($"COLOR:{hex}");
    }

    /// <summary>
    /// Sends a command to the player process.
    /// </summary>
    private async Task SendCommandAsync(string command)
    {
        if (_playerStdin == null)
        {
            throw new InvalidOperationException("Player stdin not available");
        }

        _logger.LogDebug("Sending command to player: {Command}", command);
        await _playerStdin.WriteLineAsync(command);
        await _playerStdin.FlushAsync();
    }

    private void OnPlayerExited(object? sender, EventArgs e)
    {
        _logger.LogWarning("Player process exited with code {ExitCode}",
            _playerProcess?.ExitCode ?? -1);
        _isInitialized = false;
    }

    private void OnPlayerError(object? sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            _logger.LogError("Player error: {Error}", e.Data);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _logger.LogInformation("Disposing D2DPlayerHost");

        try
        {
            // Send exit command
            if (_playerStdin != null && _playerProcess != null && !_playerProcess.HasExited)
            {
                try
                {
                    _playerStdin.WriteLine("EXIT");
                    _playerStdin.Flush();

                    // Wait a bit for graceful shutdown
                    _playerProcess.WaitForExit(1000);
                }
                catch
                {
                    // Ignore errors during shutdown
                }
            }

            // Force kill if still running
            if (_playerProcess != null && !_playerProcess.HasExited)
            {
                _logger.LogWarning("Force killing player process");
                _playerProcess.Kill();
            }

            _playerStdin?.Dispose();
            _playerProcess?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing D2DPlayerHost");
        }

        _playerHwnd = IntPtr.Zero;
        _playerProcess = null;
        _playerStdin = null;
        _disposed = true;
        _isInitialized = false;

        _logger.LogDebug("D2DPlayerHost disposed");
    }
}

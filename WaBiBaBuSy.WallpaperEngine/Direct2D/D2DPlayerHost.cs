using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;
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
    private readonly Rectangle _actualMonitorBounds;

    private Process? _playerProcess;
    private IntPtr _playerHwnd = IntPtr.Zero;
    private StreamWriter? _playerStdin;
    private bool _disposed;
    private bool _isInitialized;

    // Player processes started by any host in this app; the last one to go repaints the user's wallpaper.
    private static int s_livePlayers;
    private bool _counted;

    public D2DPlayerHost(
        ScreenMapping screen,
        ILogger<D2DPlayerHost> logger,
        DesktopWindowManager desktopWindowManager,
        Rectangle actualMonitorBounds)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _desktopWindowManager = desktopWindowManager ?? throw new ArgumentNullException(nameof(desktopWindowManager));
        _actualMonitorBounds = actualMonitorBounds;
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
    /// Raised when the player signals that a path lap has completed.
    /// The event argument is the 1-based lap number reported by the player.
    /// </summary>
    public event EventHandler<int>? LapCompleted;

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

            // Prepare command line arguments using actual monitor bounds
            var bounds = _actualMonitorBounds;
            var args = $"--bounds {bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}";

            _logger.LogInformation("Player window bounds: X={X}, Y={Y}, Width={W}, Height={H}",
                bounds.X, bounds.Y, bounds.Width, bounds.Height);

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
            Interlocked.Increment(ref s_livePlayers);
            _counted = true;
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
        // Explorer may be restarting: retry briefly, then fail. Unparented, the player's monitor-sized popup
        // would show as an ordinary window over the desktop instead of behind the icons (review D5).
        for (int attempt = 1; workerW == IntPtr.Zero && attempt <= 5; attempt++)
        {
            _logger.LogWarning("Failed to find WorkerW window (attempt {Attempt}/5) — retrying", attempt);
            await Task.Delay(1000);
            workerW = _desktopWindowManager.FindDesktopWorkerWindow();
        }
        if (workerW == IntPtr.Zero)
            throw new InvalidOperationException("desktop WorkerW window not found: the player cannot be placed behind the icons");

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

        {
            var response = await RequestAsync($"PARENT:{parentHwnd.ToInt64()},{zOrderHwnd.ToInt64()}", "PARENT", TimeSpan.FromSeconds(10));
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
    /// Re-runs desktop discovery and re-issues the PARENT command to the player.
    /// Triggered by an unsolicited NEEDS_REPARENT signal from the player (desktop
    /// tree was torn down by Snipping Tool / display change / explorer restart).
    /// Fire-and-forget: we do NOT await the player's READY response, because this
    /// runs from a background event and could race with in-flight command/response
    /// pairs on stdout. Its one reply line is counted as stale, so the next
    /// <see cref="RequestAsync"/> skips it instead of taking it as its own answer.
    /// </summary>
    private async Task ReissuePlayerParentingAsync()
    {
        if (_playerHwnd == IntPtr.Zero || _playerStdin == null) return;

        var workerW = _desktopWindowManager.FindDesktopWorkerWindow();
        if (workerW == IntPtr.Zero)
        {
            _logger.LogWarning("Reparent retry: WorkerW not found — skipping");
            return;
        }

        var progman = _desktopWindowManager.ProgmanHandle;
        var defView = _desktopWindowManager.ShellDllDefViewHandle;
        var parentHwnd = _desktopWindowManager.IsLayeredDesktopMode ? progman : workerW;
        var zOrderHwnd = _desktopWindowManager.IsLayeredDesktopMode ? defView : IntPtr.Zero;

        _logger.LogInformation("Reparent retry: re-issuing PARENT:{Parent},{ZOrder}",
            parentHwnd.ToInt64(), zOrderHwnd.ToInt64());
        Interlocked.Increment(ref _staleReplies);
        await SendCommandAsync($"PARENT:{parentHwnd.ToInt64()},{zOrderHwnd.ToInt64()}");
    }

    // Review 2026-10-13 D4: replies are paired by position on stdout. Reply lines nobody waits for (a background
    // re-parent, a request that timed out) are counted here and skipped, so they never shift later pairs.
    private int _staleReplies;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private Task<string?>? _pendingRead;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>
    /// Send a command that answers with one "READY" / "ERROR:…" line and return that line. One request at a time;
    /// a player that does not answer within <paramref name="timeout"/> throws <see cref="TimeoutException"/>
    /// (its late answer is then skipped by the next request).
    /// </summary>
    private async Task<string> RequestAsync(string command, string what, TimeSpan timeout)
    {
        var process = _playerProcess ?? throw new InvalidOperationException("player not running");
        await _requestGate.WaitAsync();
        try
        {
            await SendCommandAsync(command);
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                // A pipe read cannot be cancelled and a second read while one is pending throws: a read that timed
                // out stays pending in _pendingRead, and the next request continues it instead of starting another.
                var read = _pendingRead ??= process.StandardOutput.ReadLineAsync();
                string? line;
                try { line = await read.WaitAsync(Max(TimeSpan.Zero, deadline - DateTime.UtcNow)); }
                catch (TimeoutException)
                {
                    Interlocked.Increment(ref _staleReplies);
                    throw new TimeoutException($"player did not answer {what} within {timeout.TotalSeconds:F0} s");
                }
                _pendingRead = null;
                if (line == null) throw new InvalidOperationException($"player closed stdout during {what}");
                bool isReply = line == "READY" || line.StartsWith("ERROR:");
                if (!isReply)
                {
                    _logger.LogWarning("Unexpected player output during {What}: {Line}", what, line);
                    continue;
                }
                if (Interlocked.Decrement(ref _staleReplies) >= 0)
                {
                    _logger.LogDebug("Skipped a stale player reply during {What}: {Line}", what, line);
                    continue;
                }
                Interlocked.Increment(ref _staleReplies);   // undo: nothing was stale
                return line;
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    /// <summary>
    /// Sends a LOAD_ANIMATION command with animation metadata to the player.
    /// This replaces the per-frame approach with metadata-based rendering.
    /// </summary>
    public async Task SendLoadAnimationAsync(PlayerCommandLoadAnimation cmd)
    {
        if (!IsRunning)
        {
            _logger.LogWarning("Cannot send load animation: player not running");
            return;
        }

        try
        {
            // Serialize command to JSON
            var json = JsonConvert.SerializeObject(cmd);
            _logger.LogInformation("Sending LOAD_ANIMATION command: {Layers} layer(s), first {Path}", cmd.Layers.Count, cmd.Layers.FirstOrDefault()?.Animation.AnimationPath);

            // Send JSON command; LOAD extracts GIF frames / opens the video, so it gets a long timeout.
            {
                var response = await RequestAsync(json, "LOAD_ANIMATION", TimeSpan.FromSeconds(60));
                if (response?.StartsWith("ERROR:") == true)
                {
                    _logger.LogError("Player failed to load animation: {Error}", response);
                    throw new InvalidOperationException($"Player failed to load animation: {response}");
                }
                else if (response == "READY")
                {
                    _logger.LogInformation("Animation loaded successfully");
                }
                else
                {
                    _logger.LogWarning("Unexpected response from player: {Response}", response);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send load animation command");
            throw;
        }
    }

    /// <summary>
    /// Sends a START_ANIMATION command to the player to begin playback.
    /// </summary>
    public async Task SendStartAnimationAsync(PlayerCommandStartAnimation cmd)
    {
        if (!IsRunning)
        {
            _logger.LogWarning("Cannot send start animation: player not running");
            return;
        }

        try
        {
            // Serialize command to JSON
            var json = JsonConvert.SerializeObject(cmd);
            _logger.LogInformation("Sending START_ANIMATION command: timestamp={Timestamp}ms, speed={Speed}px/s",
                cmd.StartTimestampMs, cmd.PixelsPerSecond);

            {
                var response = await RequestAsync(json, "START_ANIMATION", TimeSpan.FromSeconds(10));
                if (response?.StartsWith("ERROR:") == true)
                {
                    _logger.LogError("Player failed to start animation: {Error}", response);
                    throw new InvalidOperationException($"Player failed to start animation: {response}");
                }
                else if (response == "READY")
                {
                    _logger.LogInformation("Animation started successfully");
                }
                else
                {
                    _logger.LogWarning("Unexpected response from player: {Response}", response);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send start animation command");
            throw;
        }
    }

    /// <summary>
    /// Sends a STOP_ANIMATION command to the player to stop playback.
    /// </summary>
    public async Task SendStopAnimationAsync()
    {
        if (!IsRunning)
        {
            _logger.LogWarning("Cannot send stop animation: player not running");
            return;
        }

        try
        {
            var cmd = new PlayerCommandStopAnimation();
            var json = JsonConvert.SerializeObject(cmd);
            _logger.LogInformation("Sending STOP_ANIMATION command");

            // Wait for response with timeout to avoid hanging if player is unresponsive
            {
                string? response;
                try
                {
                    response = await RequestAsync(json, "STOP_ANIMATION", TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Timed out waiting for STOP_ANIMATION response from player");
                    return;
                }

                if (response?.StartsWith("ERROR:") == true)
                {
                    _logger.LogError("Player failed to stop animation: {Error}", response);
                }
                else if (response == "READY")
                {
                    _logger.LogInformation("Animation stopped successfully");
                }
                else
                {
                    _logger.LogWarning("Unexpected response from player: {Response}", response);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send stop animation command");
        }
    }

    /// <summary>
    /// Sends a debug-overlay toggle to the player. When <paramref name="toggle"/> is true,
    /// the player flips whatever state it is in, ignoring <paramref name="enabled"/>.
    /// Fire-and-forget: we do not block waiting for a response so UI stays snappy even if
    /// a player is unresponsive.
    /// </summary>
    public async Task SendToggleDebugOverlayAsync(bool enabled, bool toggle)
    {
        if (!IsRunning) return;
        try
        {
            var cmd = new PlayerCommandToggleDebugOverlay { Enabled = enabled, Toggle = toggle };
            var json = JsonConvert.SerializeObject(cmd);
            await SendCommandAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send debug overlay toggle");
        }
    }

    /// <summary>
    /// Sends an updated waypoint path to the player. Fire-and-forget: no READY
    /// response is awaited so the caller is not blocked during normal animation.
    /// </summary>
    public async Task SendUpdatePathAsync(List<WaypointF> path)
    {
        if (!IsRunning) return;
        var cmd = new PlayerCommandUpdatePath { Path = path };
        await SendCommandAsync(JsonConvert.SerializeObject(cmd));
    }

    /// <summary>
    /// Sends all five debug overlay flags explicitly to the player process.
    /// Fire-and-forget: we do not block waiting for a response so UI stays snappy even if
    /// a player is unresponsive.
    /// </summary>
    public async Task SendSetDebugOverlayFlagsAsync(bool enabled, bool showPath, bool showIconRects, bool showZoneBands, bool showInfoPanel)
    {
        if (!IsRunning) return;
        try
        {
            var cmd = new PlayerCommandSetDebugOverlayFlags
            {
                Enabled = enabled,
                ShowPath = showPath,
                ShowIconRects = showIconRects,
                ShowZoneBandOutlines = showZoneBands,
                ShowInfoPanel = showInfoPanel
            };
            var json = JsonConvert.SerializeObject(cmd);
            await SendCommandAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send debug overlay flags");
        }
    }

    // Automated test mode: probes waiting for their SIGNAL:PROBE line on stderr, by probe id.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PlayerProbeReply>> _pendingProbes = new();

    /// <summary>Test mode on/off (timecode strip, simulated clock skew). Fire-and-forget: the player sends no stdout reply.</summary>
    public async Task SendTestModeAsync(bool timecode, int clockSkewMs)
    {
        if (!IsRunning) return;
        await SendCommandAsync(JsonConvert.SerializeObject(new PlayerCommandTestMode { Timecode = timecode, ClockSkewMs = clockSkewMs }));
    }

    /// <summary>One-shot show event. Fire-and-forget: the player sends no stdout reply.</summary>
    public async Task SendShowEventAsync(ShowEvent evt)
    {
        if (!IsRunning) return;
        await SendCommandAsync(JsonConvert.SerializeObject(new PlayerCommandShowEvent { Event = evt }));
    }

    /// <summary>Item transition (in = this player starts the new item, before its start). Fire-and-forget.</summary>
    public async Task SendTransitionAsync(bool incoming, string transition, long startUtcMs)
    {
        if (!IsRunning) return;
        await SendCommandAsync(JsonConvert.SerializeObject(new PlayerCommandTransition { Incoming = incoming, Transition = transition, StartUtcMs = startUtcMs }));
    }

    /// <summary>
    /// Probe the player; completes when its SIGNAL:PROBE arrives on stderr, null on timeout.
    /// A cancelled <paramref name="ct"/> throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<PlayerProbeReply?> ProbeAsync(PlayerProbeRequest request, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsRunning) return null;
        var waiter = new TaskCompletionSource<PlayerProbeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingProbes[request.ProbeId] = waiter;
        try
        {
            await SendCommandAsync(JsonConvert.SerializeObject(new PlayerCommandProbe
            {
                ProbeId = request.ProbeId,
                AtLocalUtcMs = request.AtLocalUtcMs,
                Capture = request.Capture,
                ExactElapsedMs = request.ExactElapsedMs,
                CaptureDirectory = request.CaptureDirectory,
            }));
            return await ProbeFanOut.WaitForReplyAsync(waiter.Task, timeout, ct);
        }
        finally
        {
            _pendingProbes.TryRemove(request.ProbeId, out _);
        }
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

        _logger.LogDebug("Sending command to player: {Command}", command.Substring(0, Math.Min(100, command.Length)));
        // One writer at a time: test mode adds concurrent senders (fire-and-forget test mode, probes
        // from Task.Run) next to the host's own commands; interleaved lines would corrupt the JSON protocol.
        await _stdinLock.WaitAsync();
        try
        {
            await _playerStdin.WriteLineAsync(command);
            await _playerStdin.FlushAsync();
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    // Serializes writes to the player's stdin (see SendCommandAsync). Never disposed: late senders must not throw ObjectDisposedException.
    private readonly SemaphoreSlim _stdinLock = new(1, 1);

    private void OnPlayerExited(object? sender, EventArgs e)
    {
        int code = -1;
        try { code = _playerProcess?.ExitCode ?? -1; } catch { /* disposed meanwhile */ }
        _logger.LogWarning("Player process exited with code {ExitCode}", code);
        _isInitialized = false;
        if (!_disposing) PlayerExitedUnexpectedly?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The player process ended without <see cref="Dispose"/> (crash, explorer restart took its window).</summary>
    public event EventHandler? PlayerExitedUnexpectedly;

    private volatile bool _disposing;

    private void OnPlayerError(object? sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            var line = e.Data;

            // Control signals from the player (raw, no log prefix). The player emits
            // these directly via Console.Error.WriteLine so they arrive ahead of any
            // log-formatted line that describes the same event.
            if (line == "SIGNAL:NEEDS_REPARENT")
            {
                _logger.LogWarning("[Player] NEEDS_REPARENT signal — re-issuing PARENT command");
                _ = Task.Run(async () =>
                {
                    try { await ReissuePlayerParentingAsync(); }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to re-issue PARENT after player signal"); }
                });
                return;
            }

            if (line.StartsWith(ProbeFanOut.ProbeSignalPrefix, StringComparison.Ordinal))
            {
                // ParseProbeSignal never throws: an exception here would surface on the stderr event thread and crash the app.
                var reply = ProbeFanOut.ParseProbeSignal(line, JsonConvert.DeserializeObject<PlayerProbeReply>, out var problem);
                if (reply == null)
                    _logger.LogWarning("[Player] PROBE signal ignored: {Problem}", problem);
                else if (_pendingProbes.TryRemove(reply.ProbeId, out var waiter))
                    waiter.TrySetResult(reply);
                return;
            }

            if (line != null && line.StartsWith("SIGNAL:LAP_COMPLETE:") &&
                int.TryParse(line[20..], out int lapNum))
            {
                _logger.LogInformation("[Player] LAP_COMPLETE signal: lap {Lap}", lapNum);
                LapCompleted?.Invoke(this, lapNum);
                return;
            }

            // Extract log level and strip .NET logging prefix
            // SimpleConsole SingleLine format: "HH:mm:ss info: Category[0] Actual message"
            // We want just: "[Player] Actual message" with correct host log level
            LogLevel level = LogLevel.Debug;
            string message = line;

            // Find and strip the level prefix
            int levelEnd = line.IndexOf(": ");
            if (levelEnd > 0 && levelEnd < 20) // Level prefix is short
            {
                var prefix = line.Substring(0, levelEnd).TrimStart(); // Trim timestamp
                // Check for level keywords anywhere in the prefix (timestamp may precede)
                if (prefix.Contains("fail") || prefix.Contains("crit"))
                    level = LogLevel.Error;
                else if (prefix.Contains("warn"))
                    level = LogLevel.Warning;
                else if (prefix.Contains("info"))
                    level = LogLevel.Information;
                else if (prefix.Contains("dbug") || prefix.Contains("trce"))
                    level = LogLevel.Debug;

                // Strip everything up to the message content
                // Format after level: "Category[N] The actual message"
                message = line.Substring(levelEnd + 2);

                // Strip "Category[N] " prefix if present
                int bracketEnd = message.IndexOf("] ");
                if (bracketEnd > 0 && bracketEnd < 120)
                {
                    message = message.Substring(bracketEnd + 2);
                }
            }

            _logger.Log(level, "[Player] {Message}", message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _logger.LogInformation("Disposing D2DPlayerHost");
        _disposing = true;   // the EXIT / Kill below is expected: no PlayerExitedUnexpectedly

        try
        {
            // Send exit command
            if (_playerStdin != null && _playerProcess != null && !_playerProcess.HasExited)
            {
                // Take the stdin lock briefly so EXIT does not interleave with an in-flight command.
                bool locked = _stdinLock.Wait(500);
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
                finally
                {
                    if (locked) _stdinLock.Release();
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

            // Last player gone (Stop, Clear all, exit, test restore): the desktop would keep its last frame.
            // A scene switch replaces players monitor by monitor, so with 2+ monitors the count stays above 0;
            // a single-monitor machine shows its own wallpaper for the start gap instead of a frozen frame.
            if (_counted && Interlocked.Decrement(ref s_livePlayers) == 0)
            {
                DesktopWindowManager.RefreshUserWallpaper();
                _logger.LogInformation("Last player exited, user wallpaper repainted");
            }
            _counted = false;
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

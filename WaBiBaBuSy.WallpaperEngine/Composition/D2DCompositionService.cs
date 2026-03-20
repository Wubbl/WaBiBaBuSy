using System.Drawing;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;
using WaBiBaBuSy.WallpaperEngine.Direct2D;
using WaBiBaBuSy.WallpaperEngine.Native;

namespace WaBiBaBuSy.WallpaperEngine.Composition;

/// <summary>
/// Orchestrates the Direct2D composition and display pipeline using metadata-based IPC.
/// Instead of composing frames in the main process and sending via JPEG,
/// this service sends animation metadata to D2DPlayer processes which handle composition locally.
/// Result: Main process CPU from 10% → ~0%, no JPEG encoding overhead, animations play at correct speed.
/// </summary>
public class D2DCompositionService : IDisposable
{
    private readonly ILogger<D2DCompositionService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DesktopWindowManager _desktopWindowManager;

    private VirtualCanvasManager? _canvasManager;
    private BackgroundLayerConfig? _backgroundConfig;
    private AnimationLayerConfig? _animationConfig;
    private MovementConfig? _movementConfig;
    private readonly Dictionary<int, D2DPlayerHost> _playerHosts = new();
    private bool _disposed;
    private bool _isRunning;
    private int _monitorIndex;

    public D2DCompositionService(
        ILogger<D2DCompositionService> logger,
        ILoggerFactory loggerFactory,
        DesktopWindowManager desktopWindowManager)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _desktopWindowManager = desktopWindowManager ?? throw new ArgumentNullException(nameof(desktopWindowManager));
    }

    /// <summary>
    /// Gets whether the service is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the player window handle for the first (or only) player host.
    /// Used for thumbnail capture via PrintWindow.
    /// </summary>
    public IntPtr PlayerHwnd
    {
        get
        {
            foreach (var playerHost in _playerHosts.Values)
            {
                if (playerHost.PlayerHwnd != IntPtr.Zero)
                    return playerHost.PlayerHwnd;
            }
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Gets or sets whether to run in static mode (render first frame only, then stop).
    /// Must be set before calling InitializeAsync.
    /// TESTING MODE: Use this to verify if rendering pipeline works at all.
    /// </summary>
    public bool StaticMode { get; set; } = false;

    /// <summary>
    /// Initialize the composition service with canvas layout and layer configurations.
    /// Creates D2DPlayer processes and sends animation metadata to each.
    /// </summary>
    /// <param name="perMonitorMode">
    /// When true (Simultaneous mode), each player treats its own monitor as the entire canvas.
    /// When false (Sequential/spanning mode), animation spans across the full virtual canvas.
    /// </param>
    public async Task InitializeAsync(
        VirtualCanvasManager canvasManager,
        BackgroundLayerConfig backgroundConfig,
        AnimationLayerConfig animationConfig,
        Rectangle actualMonitorBounds,
        int monitorIndex = 0,
        MovementConfig? movementConfig = null,
        bool perMonitorMode = false,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(D2DCompositionService));

        _logger.LogInformation("Initializing D2D composition service for {ScreenCount} screens (metadata-based), movement={MovementType}, perMonitor={PerMonitor}",
            canvasManager.ScreenMappings.Count, movementConfig?.Type.ToString() ?? "None", perMonitorMode);

        _canvasManager = canvasManager ?? throw new ArgumentNullException(nameof(canvasManager));
        _backgroundConfig = backgroundConfig ?? throw new ArgumentNullException(nameof(backgroundConfig));
        _animationConfig = animationConfig ?? throw new ArgumentNullException(nameof(animationConfig));
        _movementConfig = movementConfig;
        _monitorIndex = monitorIndex;

        // Create D2D player hosts for each screen
        foreach (var screen in canvasManager.ScreenMappings)
        {
            _logger.LogInformation("Creating D2D player host for screen {Order}", screen.Order);

            var playerHost = new D2DPlayerHost(
                screen,
                _loggerFactory.CreateLogger<D2DPlayerHost>(),
                _desktopWindowManager,
                actualMonitorBounds);

            // Propagate StaticMode setting to player host
            playerHost.StaticMode = StaticMode;

            await playerHost.InitializeAsync(cancellationToken);

            _playerHosts[screen.Order] = playerHost;

            _logger.LogInformation("Sending animation metadata to player {Order}", screen.Order);

            // Send LOAD_ANIMATION command with metadata
            // In per-monitor (simultaneous) mode: each player is its own independent canvas
            // In spanning (sequential) mode: players share a virtual canvas with offset
            var loadCmd = new PlayerCommandLoadAnimation
            {
                AnimationConfig = animationConfig,
                BackgroundConfig = backgroundConfig,
                MonitorIndex = monitorIndex,
                VirtualCanvasHeight = actualMonitorBounds.Height,
                VirtualCanvasWidth = perMonitorMode
                    ? actualMonitorBounds.Width
                    : (canvasManager.VirtualBounds.Width > 0 ? canvasManager.VirtualBounds.Width : actualMonitorBounds.Width),
                MonitorOffsetX = perMonitorMode ? 0 : screen.VirtualBounds.X,
                MovementConfig = _movementConfig
            };

            await playerHost.SendLoadAnimationAsync(loadCmd);
        }

        _logger.LogInformation("D2D composition service initialized successfully (metadata sent to all players)");
    }

    /// <summary>
    /// Start animation playback on all D2DPlayer processes.
    /// Sends START_ANIMATION commands with timing information.
    /// No render loop in main process - players handle rendering locally!
    /// </summary>
    public async Task StartAsync(long startTimestampMs, int pixelsPerSecond)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(D2DCompositionService));

        if (_isRunning)
        {
            _logger.LogWarning("D2D composition service already running");
            return;
        }

        if (_canvasManager == null || _playerHosts.Count == 0)
        {
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");
        }

        _logger.LogInformation("Starting animation playback on all players: timestamp={Timestamp}ms, speed={Speed}px/s",
            startTimestampMs, pixelsPerSecond);

        // Send START_ANIMATION command to all players
        foreach (var (order, playerHost) in _playerHosts)
        {
            if (!playerHost.IsRunning)
            {
                _logger.LogWarning("Player host for screen {Order} not running, skipping", order);
                continue;
            }

            var startCmd = new PlayerCommandStartAnimation
            {
                StartTimestampMs = startTimestampMs,
                PixelsPerSecond = pixelsPerSecond
            };

            try
            {
                await playerHost.SendStartAnimationAsync(startCmd);
                _logger.LogInformation("Animation started on player {Order}", order);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start animation on player {Order}", order);
            }
        }

        _isRunning = true;
        _logger.LogInformation("D2D composition service started (all players now rendering locally)");
    }

    /// <summary>
    /// Stop animation playback on all D2DPlayer processes.
    /// Sends STOP_ANIMATION commands to all players.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
        {
            _logger.LogWarning("D2D composition service not running");
            return;
        }

        _logger.LogInformation("Stopping animation playback on all players");

        // Send STOP_ANIMATION command to all players
        foreach (var (order, playerHost) in _playerHosts)
        {
            if (!playerHost.IsRunning)
            {
                _logger.LogWarning("Player host for screen {Order} not running, skipping", order);
                continue;
            }

            try
            {
                await playerHost.SendStopAnimationAsync();
                _logger.LogInformation("Animation stopped on player {Order}", order);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop animation on player {Order}", order);
            }
        }

        _isRunning = false;
        _logger.LogInformation("D2D composition service stopped");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _logger.LogInformation("Disposing D2D composition service");

        // Mark as not running (StopAsync should have been awaited by the caller)
        _isRunning = false;

        // Dispose all player hosts (sends EXIT command and kills process)
        foreach (var playerHost in _playerHosts.Values)
        {
            playerHost.Dispose();
        }
        _playerHosts.Clear();

        _disposed = true;

        _logger.LogInformation("D2D composition service disposed");
    }
}

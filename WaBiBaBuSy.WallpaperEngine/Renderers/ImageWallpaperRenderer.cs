using System.Drawing;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Models;
using WaBiBaBuSy.WallpaperEngine.Native;
using WaBiBaBuSy.Player.Common;
using WaBiBaBuSy.Player.Common.Messages;

namespace WaBiBaBuSy.WallpaperEngine.Renderers;

/// <summary>
/// Static image wallpaper renderer for JPG, PNG, BMP formats.
/// Uses a separate WPF process (WaBiBaBuSy.Player.Image.exe) to avoid Windows Forms parenting issues.
/// </summary>
public class ImageWallpaperRenderer : IWallpaperRenderer
{
    private readonly ILogger<ImageWallpaperRenderer> _logger;
    private readonly DesktopWindowManager _desktopManager;

    private ProcessCommunicator? _processCommunicator;
    private WallpaperConfig? _config;
    private WallpaperState _state = WallpaperState.Uninitialized;
    private bool _disposed;

    public event EventHandler<FrameRenderedEventArgs>? FrameRendered;
    public event EventHandler<WallpaperState>? StateChanged;

    public ImageWallpaperRenderer(
        ILogger<ImageWallpaperRenderer> logger,
        DesktopWindowManager desktopManager)
    {
        _logger = logger;
        _desktopManager = desktopManager;
    }

    public WallpaperState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, _state);
            }
        }
    }

    /// <summary>
    /// For static images, position is always 0
    /// </summary>
    public long PositionMs => 0;

    public async Task InitializeAsync(WallpaperConfig config)
    {
        try
        {
            _logger.LogInformation("Initializing Image wallpaper renderer for {FilePath}", config.FilePath);
            _config = config;

            // Verify image file exists
            if (!File.Exists(config.FilePath))
            {
                throw new FileNotFoundException($"Image file not found: {config.FilePath}");
            }

            // Get path to the WPF Image player executable
            var playerExePath = GetPlayerExecutablePath();
            if (!File.Exists(playerExePath))
            {
                throw new FileNotFoundException($"WaBiBaBuSy.Player.Image.exe not found at: {playerExePath}");
            }

            _logger.LogInformation("Starting Image player process: {PlayerExePath}", playerExePath);

            // Launch player process and setup IPC
            _processCommunicator = new ProcessCommunicator(playerExePath);
            _logger.LogInformation("ProcessCommunicator created, process running: {IsRunning}", _processCommunicator.IsRunning);

            _processCommunicator.MessageReceived += OnPlayerMessageReceived;
            _processCommunicator.ErrorReceived += OnPlayerErrorReceived;
            _logger.LogInformation("Event handlers attached");

            // Wait for player to send HWND
            _logger.LogInformation("Waiting for HWND from player (10s timeout)...");
            var gotHwnd = await _processCommunicator.WaitForWindowHandleAsync(TimeSpan.FromSeconds(10));
            if (!gotHwnd)
            {
                _logger.LogError("Timeout waiting for HWND. Process running: {IsRunning}, HWND: {Hwnd}",
                    _processCommunicator.IsRunning,
                    _processCommunicator.WindowHandle);
                throw new TimeoutException("Player process did not send window handle within timeout");
            }

            _logger.LogInformation("Received HWND from player: 0x{Hwnd:X}", _processCommunicator.WindowHandle);

            // CRITICAL: Load the image BEFORE parenting to desktop
            // This ensures content is ready before the window becomes a child of Progman
            _logger.LogInformation("Sending LOAD command to player before parenting");
            await _processCommunicator.SendCommandAsync(new PlayerCommandLoad
            {
                FilePath = config.FilePath
            });

            // Wait for the "loaded" message to confirm image is ready
            _logger.LogInformation("Waiting for image to load and RENDER before parenting...");
            await Task.Delay(2000); // Give WPF time to decode, layout, and RENDER the image

            // NOW parent the window to desktop after content is loaded AND RENDERED
            _logger.LogInformation("Image should be visible now, calling SetParent...");
            await SetPlayerAsWallpaperAsync(config);

            State = WallpaperState.Stopped;
            _logger.LogInformation("Image wallpaper renderer initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Image wallpaper renderer");
            State = WallpaperState.Error;
            throw;
        }
    }

    public async Task StartAsync()
    {
        try
        {
            if (_processCommunicator == null)
                throw new InvalidOperationException("Renderer not initialized");

            _logger.LogInformation("Starting Image display");

            // Send PLAY command to player (for static images, this is mostly a no-op in the player)
            await _processCommunicator.SendCommandAsync(new PlayerCommandPlay());

            State = WallpaperState.Playing;
            _logger.LogInformation("Image display started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Image display");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task PauseAsync()
    {
        try
        {
            // Static images don't have playback, but we'll track the state
            State = WallpaperState.Paused;
            _logger.LogInformation("Image display paused");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pause Image display");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task ResumeAsync()
    {
        try
        {
            State = WallpaperState.Playing;
            _logger.LogInformation("Image display resumed");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume Image display");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task StopAsync()
    {
        try
        {
            State = WallpaperState.Stopped;
            _logger.LogInformation("Image display stopped");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop Image display");
            State = WallpaperState.Error;
            throw;
        }
    }

    public Task SeekAsync(TimeSpan position)
    {
        // Static images have no timeline, seeking is a no-op
        _logger.LogDebug("Seek called on static image (no-op)");
        return Task.CompletedTask;
    }

    private string GetPlayerExecutablePath()
    {
        // Look for the player executable in the same directory as the current assembly
        var assemblyDir = Path.GetDirectoryName(typeof(ImageWallpaperRenderer).Assembly.Location);
        if (string.IsNullOrEmpty(assemblyDir))
        {
            throw new InvalidOperationException("Could not determine assembly directory");
        }

        var playerExePath = Path.Combine(assemblyDir, "WaBiBaBuSy.Player.Image.exe");
        return playerExePath;
    }

    private Task SetPlayerAsWallpaperAsync(WallpaperConfig config)
    {
        if (_processCommunicator == null)
            throw new InvalidOperationException("Process communicator not initialized");

        var playerHwnd = _processCommunicator.WindowHandle;

        // Find desktop window
        var desktopWindow = _desktopManager.FindDesktopWorkerWindow();
        if (desktopWindow == IntPtr.Zero)
        {
            _logger.LogWarning("Could not find WorkerW window, wallpaper may not render behind icons");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Found desktop window: 0x{DesktopWindow:X}", desktopWindow);

        // Get monitor bounds
        var screen = System.Windows.Forms.Screen.AllScreens[config.MonitorIndex];
        var screenBounds = new Rectangle(
            screen.Bounds.X,
            screen.Bounds.Y,
            screen.Bounds.Width,
            screen.Bounds.Height);

        _logger.LogInformation("Setting player window as wallpaper on monitor {Index}: {Bounds}",
            config.MonitorIndex,
            screenBounds);

        // Call SetAsWallpaperWindow to parent the player window to the desktop
        _desktopManager.SetAsWallpaperWindow(playerHwnd, screenBounds);

        _logger.LogInformation("Player window set as wallpaper behind desktop icons");

        return Task.CompletedTask;
    }

    private void OnPlayerMessageReceived(object? sender, PlayerMessageBase message)
    {
        _logger.LogDebug("Received message from player: {MessageType}", message.MessageType);

        if (message is PlayerMessageLoaded loadedMsg)
        {
            if (loadedMsg.Success)
            {
                _logger.LogInformation("Player successfully loaded wallpaper");
            }
            else
            {
                _logger.LogError("Player failed to load wallpaper: {Error}", loadedMsg.ErrorMessage);
                State = WallpaperState.Error;
            }
        }
    }

    private void OnPlayerErrorReceived(object? sender, string error)
    {
        _logger.LogError("Player error: {Error}", error);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _logger.LogInformation("Disposing Image wallpaper renderer");

        if (_processCommunicator != null)
        {
            _processCommunicator.MessageReceived -= OnPlayerMessageReceived;
            _processCommunicator.ErrorReceived -= OnPlayerErrorReceived;
            _processCommunicator.Dispose();
            _processCommunicator = null;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

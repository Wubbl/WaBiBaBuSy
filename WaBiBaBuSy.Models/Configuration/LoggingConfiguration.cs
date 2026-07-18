namespace WaBiBaBuSy.Models.Configuration;

/// <summary>
/// Logging configuration with component-level filtering and file output options.
/// Persisted to %APPDATA%\WaBiBaBuSy\logging-config.json
/// </summary>
public class LoggingConfiguration
{
    /// <summary>
    /// Global minimum log level: Trace, Debug, Information, Warning, Error
    /// </summary>
    public string Level { get; set; } = "Information";

    // ── Component Filters ────────────────────────────────────────────────────

    /// <summary>UI layer (ViewModels, TrayViewModel, etc.)</summary>
    public bool LogUI { get; set; } = true;

    /// <summary>D2D Player process (WaBiBaBuSy.Player.D2D)</summary>
    public bool LogD2DPlayer { get; set; } = true;

    /// <summary>Composition system (CompositionRenderer, AnimationLayerRenderer, etc.)</summary>
    public bool LogComposition { get; set; } = true;

    /// <summary>Renderers (VideoWallpaperRenderer, GifWallpaperRenderer, ImageRenderer)</summary>
    public bool LogRenderers { get; set; } = true;

    /// <summary>Networking layer (gRPC, WallpaperSyncService, mDNS discovery)</summary>
    public bool LogNetworking { get; set; } = true;

    /// <summary>Animation system (AnimationDistributor, Orchestrator)</summary>
    public bool LogAnimation { get; set; } = false;

    /// <summary>File transfer (UpdateDownloader, UpdateVerifier, TransferContent)</summary>
    public bool LogFileTransfer { get; set; } = false;

    // ── Advanced ─────────────────────────────────────────────────────────────

    /// <summary>Write logs to a rolling daily file in addition to the console</summary>
    public bool LogToFile { get; set; } = false;

    /// <summary>Log CPU/RAM/FPS performance metrics (logged at Debug level)</summary>
    public bool LogPerformanceMetrics { get; set; } = false;

    /// <summary>Log every rendered frame (very verbose – Debug level, high volume)</summary>
    public bool LogFrameByFrame { get; set; } = false;

    // ── File Logging Settings ─────────────────────────────────────────────────

    /// <summary>Directory where log files are written when LogToFile is true</summary>
    public string LogDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WaBiBaBuSy", "Logs");

    /// <summary>Maximum size of a single log file in MB before rolling</summary>
    public int MaxLogFileSizeMB { get; set; } = 10;

    /// <summary>Number of rolled log files to keep</summary>
    public int MaxLogFiles { get; set; } = 5;
}

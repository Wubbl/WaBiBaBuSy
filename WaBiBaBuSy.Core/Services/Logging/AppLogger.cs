using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Configuration;

namespace WaBiBaBuSy.Core.Services.Logging;

/// <summary>
/// Central application logger with runtime-configurable component filters and optional file output.
///
/// Usage:
///   // In App startup:
///   AppLogger.Initialize(ConfigurationManager.LoadLoggingConfiguration());
///
///   // In any service:
///   private readonly ILogger _logger = AppLogger.CreateLogger<MyService>();
///
///   // When settings change:
///   AppLogger.ApplyConfig(newConfig);  // No factory rebuild needed
/// </summary>
public static class AppLogger
{
    // Volatile so the filter closure reads the latest config on every log call.
    private static volatile LoggingConfiguration _config = new();
    private static readonly FileLoggerProvider _fileProvider;
    private static readonly ILoggerFactory _factory;

    static AppLogger()
    {
        _fileProvider = new FileLoggerProvider(
            isEnabled: () => _config.LogToFile,
            getLogDirectory: () => _config.LogDirectory);

        _factory = LoggerFactory.Create(builder =>
        {
            // Our filter handles global level, component toggles, and frame-by-frame suppression.
            // SetMinimumLevel to Trace so nothing gets pre-filtered before our logic runs.
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddFilter((category, level) => PassesFilter(category, level));

            builder.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace; // all levels to stderr
            });

            builder.AddProvider(_fileProvider);
        });
    }

    /// <summary>
    /// Apply a new logging configuration. Takes effect immediately on all existing loggers
    /// (the filter function reads _config at call time, not at factory creation time).
    /// </summary>
    public static void ApplyConfig(LoggingConfiguration config)
    {
        _config = config; // atomic volatile write
    }

    /// <summary>
    /// Convenience: load config from disk and apply it immediately.
    /// Call this once in App.OnFrameworkInitializationCompleted().
    /// </summary>
    public static void Initialize(LoggingConfiguration config)
    {
        ApplyConfig(config);
    }

    public static ILogger<T> CreateLogger<T>() => _factory.CreateLogger<T>();
    public static ILogger CreateLogger(string categoryName) => _factory.CreateLogger(categoryName);

    // ── Filter Logic ───────────────────────────────────────────────────────

    private static bool PassesFilter(string? category, LogLevel level)
    {
        var cfg = _config; // read volatile once for consistency

        // Global minimum level
        var minLevel = cfg.Level switch
        {
            "Trace"       => LogLevel.Trace,
            "Debug"       => LogLevel.Debug,
            "Warning"     => LogLevel.Warning,
            "Error"       => LogLevel.Error,
            _             => LogLevel.Information
        };
        if (level < minLevel) return false;

        // Frame-by-frame suppression: silence Debug unless explicitly enabled
        if (!cfg.LogFrameByFrame && level == LogLevel.Debug) return false;

        if (category == null) return true;

        // Component-level filters (checked in order of specificity)
        if (!cfg.LogUI && IsUICategory(category))               return false;
        if (!cfg.LogD2DPlayer && IsD2DCategory(category))       return false;
        if (!cfg.LogComposition && IsCompositionCategory(category)) return false;
        if (!cfg.LogRenderers && IsRendererCategory(category))  return false;
        if (!cfg.LogNetworking && IsNetworkingCategory(category)) return false;
        if (!cfg.LogAnimation && IsAnimationCategory(category)) return false;
        if (!cfg.LogFileTransfer && IsFileTransferCategory(category)) return false;

        return true;
    }

    private static bool IsUICategory(string c) =>
        c.StartsWith("WaBiBaBuSy.UI", StringComparison.Ordinal) ||
        c.Contains("ViewModel") || c.Contains("TrayView");

    private static bool IsD2DCategory(string c) =>
        c.Contains("Player.D2D") || c.Contains("D2DPlayer") ||
        c.Contains("D2DVortice") || c.Contains("D2DPlayerHost");

    private static bool IsCompositionCategory(string c) =>
        c.Contains("Composition") || c.Contains("AnimationLayer") ||
        c.Contains("BackgroundLayer") || c.Contains("VirtualCanvas") ||
        c.Contains("D2DComposition");

    private static bool IsRendererCategory(string c) =>
        c.Contains("Renderer") && !IsCompositionCategory(c) && !IsD2DCategory(c);

    private static bool IsNetworkingCategory(string c) =>
        c.Contains("Sync") || c.Contains("Discovery") ||
        c.Contains("Grpc") || c.Contains("gRPC") ||
        c.Contains("WallpaperSync");

    private static bool IsAnimationCategory(string c) =>
        c.Contains("Animat") || c.Contains("Timing") ||
        c.Contains("Orchestrat") || c.Contains("Distribut") ||
        c.Contains("CrossScreen");

    private static bool IsFileTransferCategory(string c) =>
        c.Contains("Transfer") || c.Contains("Downl") ||
        c.Contains("UpdateManager") || c.Contains("UpdateVerif") ||
        c.Contains("UpdateAppl") || c.Contains("UpdateDownl");
}

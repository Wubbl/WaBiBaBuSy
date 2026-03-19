using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.Core.Services.Logging;

/// <summary>
/// Simple rolling-daily-file ILoggerProvider. Only writes when LogToFile is enabled in the
/// current config (checked on every log call via the config getter delegate).
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly Func<bool> _isEnabled;
    private readonly Func<string> _getLogDirectory;
    private readonly object _writeLock = new();

    private StreamWriter? _writer;
    private string _currentPath = string.Empty;

    public FileLoggerProvider(Func<bool> isEnabled, Func<string> getLogDirectory)
    {
        _isEnabled = isEnabled;
        _getLogDirectory = getLogDirectory;
    }

    internal StreamWriter? GetWriter()
    {
        if (!_isEnabled()) return null;

        var dir = _getLogDirectory();
        if (string.IsNullOrWhiteSpace(dir)) return null;

        var path = Path.Combine(dir, $"wabibabusy-{DateTime.Today:yyyy-MM-dd}.log");

        if (_writer == null || _currentPath != path)
        {
            lock (_writeLock)
            {
                if (_writer == null || _currentPath != path)
                {
                    _writer?.Dispose();
                    _writer = null;
                    try
                    {
                        Directory.CreateDirectory(dir);
                        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
                        _currentPath = path;
                        _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} INF] [FileLogger] Log file opened: {path}");
                    }
                    catch (Exception ex)
                    {
                        _writer = null;
                        _lastError = $"Failed to create log file at '{path}': {ex.Message}";
                        Console.Error.WriteLine($"[FileLogger] {_lastError}");
                    }
                }
            }
        }

        return _writer;
    }

    /// <summary>Last error encountered when trying to create the log file (for diagnostics).</summary>
    internal string? LastError => _lastError;
    private volatile string? _lastError;

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(categoryName, GetWriter, _writeLock);

    public void Dispose()
    {
        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly Func<StreamWriter?> _getWriter;
    private readonly object _lock;

    public FileLogger(string category, Func<StreamWriter?> getWriter, object @lock)
    {
        _category = category;
        _getWriter = getWriter;
        _lock = @lock;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => _getWriter() != null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var writer = _getWriter();
        if (writer == null) return;

        var level = logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };

        lock (_lock)
        {
            try
            {
                writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} {level}] [{_category}] {formatter(state, exception)}");
                if (exception != null)
                    writer.WriteLine(exception.ToString());
            }
            catch { /* never crash on logging */ }
        }
    }
}

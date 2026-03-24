using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.Core.Services;

/// <summary>
/// Manages the content cache directory with LRU eviction when size exceeds the configured limit.
/// </summary>
public class ContentCacheManager
{
    private readonly ILogger<ContentCacheManager> _logger;
    private readonly string _cacheDirectory;
    private readonly long _maxCacheSizeBytes;

    public ContentCacheManager(ILogger<ContentCacheManager> logger, string cacheDirectory, int maxCacheSizeMB)
    {
        _logger = logger;
        _cacheDirectory = cacheDirectory;
        _maxCacheSizeBytes = (long)maxCacheSizeMB * 1024 * 1024;
    }

    /// <summary>
    /// Gets the current cache size in bytes.
    /// </summary>
    public long GetCacheSizeBytes()
    {
        if (!Directory.Exists(_cacheDirectory))
            return 0;

        return new DirectoryInfo(_cacheDirectory)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
    }

    /// <summary>
    /// Marks a file as recently used by updating its last access time.
    /// </summary>
    public void TouchFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.SetLastAccessTimeUtc(filePath, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not update access time for {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Checks if adding a file of the given size would exceed the cache limit.
    /// If so, evicts LRU files until there is enough space.
    /// Returns true if the file can be added (enough space after eviction).
    /// </summary>
    public bool EnsureSpace(long requiredBytes)
    {
        if (_maxCacheSizeBytes <= 0)
            return true; // No limit configured

        if (!Directory.Exists(_cacheDirectory))
            return true;

        var currentSize = GetCacheSizeBytes();
        if (currentSize + requiredBytes <= _maxCacheSizeBytes)
            return true;

        _logger.LogInformation(
            "Cache size {CurrentMB:F1} MB + {RequiredMB:F1} MB exceeds limit {MaxMB:F1} MB. Running LRU eviction...",
            currentSize / (1024.0 * 1024), requiredBytes / (1024.0 * 1024), _maxCacheSizeBytes / (1024.0 * 1024));

        return EvictUntilFits(currentSize, requiredBytes);
    }

    /// <summary>
    /// Runs LRU eviction to bring the cache under the size limit.
    /// </summary>
    public int Evict()
    {
        if (_maxCacheSizeBytes <= 0 || !Directory.Exists(_cacheDirectory))
            return 0;

        var currentSize = GetCacheSizeBytes();
        if (currentSize <= _maxCacheSizeBytes)
            return 0;

        var targetSize = (long)(_maxCacheSizeBytes * 0.9); // Evict to 90% to avoid thrashing
        return EvictToSize(currentSize, targetSize);
    }

    private bool EvictUntilFits(long currentSize, long requiredBytes)
    {
        var targetSize = _maxCacheSizeBytes - requiredBytes;
        if (targetSize < 0)
        {
            _logger.LogWarning("Required file size {RequiredMB:F1} MB exceeds entire cache limit {MaxMB:F1} MB",
                requiredBytes / (1024.0 * 1024), _maxCacheSizeBytes / (1024.0 * 1024));
            // Still allow it — evict as much as possible
            targetSize = 0;
        }

        var evicted = EvictToSize(currentSize, targetSize);
        return evicted >= 0; // Always allow the download to proceed
    }

    private int EvictToSize(long currentSize, long targetSize)
    {
        var files = new DirectoryInfo(_cacheDirectory)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .OrderBy(f => f.LastAccessTimeUtc) // Least recently accessed first
            .ToList();

        int evictedCount = 0;
        long runningSize = currentSize;

        foreach (var file in files)
        {
            if (runningSize <= targetSize)
                break;

            try
            {
                var fileSize = file.Length;
                file.Delete();
                runningSize -= fileSize;
                evictedCount++;

                _logger.LogInformation("Evicted cached file: {FileName} ({SizeMB:F1} MB)",
                    file.Name, fileSize / (1024.0 * 1024));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete cached file: {FileName}", file.Name);
            }
        }

        if (evictedCount > 0)
        {
            _logger.LogInformation("LRU eviction complete: removed {Count} file(s), cache now {SizeMB:F1} MB",
                evictedCount, runningSize / (1024.0 * 1024));
        }

        return evictedCount;
    }
}

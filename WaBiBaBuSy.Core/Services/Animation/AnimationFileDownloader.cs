using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using WaBiBaBuSy.Models.Configuration;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>
/// Client-side service for downloading and caching animation files from server.
/// Uses SHA256-based caching to avoid re-downloading identical files.
/// </summary>
public class AnimationFileDownloader
{
    private readonly ILogger<AnimationFileDownloader> _logger;
    private readonly string _cacheDirectory;

    /// <summary>
    /// Callback to request file chunks from server
    /// </summary>
    public delegate Task<(byte[] data, bool moreChunks)> DownloadChunkDelegate(string filePath, int chunkIndex);
    public event DownloadChunkDelegate? OnRequestChunk;

    /// <summary>
    /// Callback when download progress updates
    /// </summary>
    public delegate Task DownloadProgressDelegate(string filePath, int currentChunk, int totalChunks, double percentComplete);
    public event DownloadProgressDelegate? OnProgressUpdate;

    /// <summary>
    /// Cache metadata for downloaded files
    /// </summary>
    private class CacheMetadata
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileHash { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime CachedAt { get; set; }
    }

    /// <summary>
    /// In-memory cache of metadata
    /// </summary>
    private readonly Dictionary<string, CacheMetadata> _cacheMetadata = new();

    public AnimationFileDownloader(ILogger<AnimationFileDownloader> logger, string? customCacheDirectory = null)
    {
        _logger = logger;
        _cacheDirectory = customCacheDirectory ?? GetDefaultCacheDirectory();

        // Ensure cache directory exists
        if (!Directory.Exists(_cacheDirectory))
        {
            Directory.CreateDirectory(_cacheDirectory);
            _logger.LogInformation("Created animation cache directory: {CacheDirectory}", _cacheDirectory);
        }

        // Load existing cache metadata
        LoadCacheMetadata();
    }

    /// <summary>
    /// Get the default cache directory for animation files
    /// </summary>
    public static string GetDefaultCacheDirectory()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appDataPath, "WaBiBaBuSy", "AnimationCache");
    }

    /// <summary>
    /// Download animation file from server with caching
    /// </summary>
    public async Task<string> DownloadAnimationFileAsync(string serverFilePath, string fileHash, long fileSize)
    {
        _logger.LogInformation(
            "Downloading animation file: Path={FilePath}, Hash={Hash}, Size={Size} bytes",
            serverFilePath, fileHash, fileSize);

        // Check if file is already cached
        var cachedFile = GetCachedFilePath(serverFilePath, fileHash);
        if (File.Exists(cachedFile) && VerifyCacheFile(cachedFile, fileHash, fileSize))
        {
            _logger.LogInformation("Using cached animation file: {CachedFile}", cachedFile);
            AddToGallery(cachedFile);
            return cachedFile;
        }

        // Download from server
        var downloadedFile = await DownloadFromServerAsync(serverFilePath, fileHash, fileSize);

        // Verify downloaded file
        if (!VerifyDownloadedFile(downloadedFile, fileHash))
        {
            File.Delete(downloadedFile);
            throw new InvalidOperationException($"Downloaded file hash mismatch: {downloadedFile}");
        }

        // Update cache metadata
        UpdateCacheMetadata(serverFilePath, fileHash, fileSize);

        // Add to wallpaper gallery so it shows up in the client's UI
        AddToGallery(cachedFile);

        _logger.LogInformation("Successfully downloaded and cached animation file: {File}", downloadedFile);
        return downloadedFile;
    }

    /// <summary>
    /// Download file from server in chunks
    /// </summary>
    private async Task<string> DownloadFromServerAsync(string serverFilePath, string fileHash, long fileSize)
    {
        var tempFile = Path.Combine(_cacheDirectory, $".download_{Guid.NewGuid()}.tmp");

        try
        {
            using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);

            int chunkIndex = 0;
            long bytesDownloaded = 0;
            const int chunkSize = 1024 * 1024; // 1MB chunks

            _logger.LogInformation("Starting download: {FilePath}", serverFilePath);

            while (true)
            {
                // Request chunk from server
                if (OnRequestChunk == null)
                {
                    throw new InvalidOperationException("No OnRequestChunk handler registered");
                }

                var (chunkData, moreChunks) = await OnRequestChunk(serverFilePath, chunkIndex);

                if (chunkData.Length == 0 && !moreChunks)
                {
                    break;
                }

                // Write chunk to temp file
                await fileStream.WriteAsync(chunkData, 0, chunkData.Length);
                bytesDownloaded += chunkData.Length;

                // Report progress
                var percentComplete = (double)bytesDownloaded / fileSize * 100;
                var estimatedTotalChunks = (int)Math.Ceiling((double)fileSize / chunkSize);

                _logger.LogDebug(
                    "Download progress: File={FilePath}, Chunk={ChunkIndex}, Progress={Progress:F1}%",
                    serverFilePath, chunkIndex, percentComplete);

                OnProgressUpdate?.Invoke(serverFilePath, chunkIndex, estimatedTotalChunks, percentComplete);

                if (!moreChunks)
                {
                    break;
                }

                chunkIndex++;
            }

            fileStream.Close();

            _logger.LogInformation(
                "Download complete: {FilePath}, Total={TotalBytes} bytes",
                serverFilePath, bytesDownloaded);

            return tempFile;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading file: {FilePath}", serverFilePath);
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
            throw;
        }
    }

    /// <summary>
    /// Get cache file path for a file
    /// </summary>
    private string GetCachedFilePath(string serverFilePath, string fileHash)
    {
        var fileName = Path.GetFileName(serverFilePath);
        var cacheFileName = $"{fileHash}_{fileName}";
        return Path.Combine(_cacheDirectory, cacheFileName);
    }

    /// <summary>
    /// Verify cached file integrity
    /// </summary>
    private bool VerifyCacheFile(string cachedFilePath, string expectedHash, long expectedSize)
    {
        try
        {
            // Check file exists and size matches
            if (!File.Exists(cachedFilePath))
            {
                return false;
            }

            var fileInfo = new FileInfo(cachedFilePath);
            if (fileInfo.Length != expectedSize)
            {
                _logger.LogWarning(
                    "Cache file size mismatch: {File}, Expected={Expected}, Actual={Actual}",
                    cachedFilePath, expectedSize, fileInfo.Length);
                return false;
            }

            // Verify hash
            using var sha256 = SHA256.Create();
            using var fileStream = File.OpenRead(cachedFilePath);
            var hash = Convert.ToHexString(sha256.ComputeHash(fileStream)).ToLowerInvariant();

            if (hash != expectedHash.ToLowerInvariant())
            {
                _logger.LogWarning(
                    "Cache file hash mismatch: {File}, Expected={Expected}, Actual={Actual}",
                    cachedFilePath, expectedHash, hash);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying cache file: {File}", cachedFilePath);
            return false;
        }
    }

    /// <summary>
    /// Verify downloaded file integrity
    /// </summary>
    private bool VerifyDownloadedFile(string filePath, string expectedHash)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogError("Downloaded file not found: {File}", filePath);
                return false;
            }

            using var sha256 = SHA256.Create();
            using var fileStream = File.OpenRead(filePath);
            var hash = Convert.ToHexString(sha256.ComputeHash(fileStream)).ToLowerInvariant();

            if (hash != expectedHash.ToLowerInvariant())
            {
                _logger.LogError(
                    "Downloaded file hash mismatch: {File}, Expected={Expected}, Actual={Actual}",
                    filePath, expectedHash, hash);
                return false;
            }

            // Move temp file to cache location
            var cachedPath = GetCachedFilePath(filePath, expectedHash);
            if (File.Exists(cachedPath))
            {
                File.Delete(filePath);
            }
            else
            {
                File.Move(filePath, cachedPath, overwrite: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying downloaded file: {File}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Update cache metadata
    /// </summary>
    private void UpdateCacheMetadata(string serverFilePath, string fileHash, long fileSize)
    {
        var metadata = new CacheMetadata
        {
            FilePath = serverFilePath,
            FileHash = fileHash,
            FileSize = fileSize,
            CachedAt = DateTime.UtcNow,
        };

        var cacheKey = GetCacheKey(serverFilePath, fileHash);
        _cacheMetadata[cacheKey] = metadata;
        SaveCacheMetadata();
    }

    /// <summary>
    /// Get cache key for file
    /// </summary>
    private static string GetCacheKey(string serverFilePath, string fileHash)
    {
        return $"{fileHash}:{serverFilePath}";
    }

    /// <summary>
    /// Load cache metadata from disk
    /// </summary>
    private void LoadCacheMetadata()
    {
        var metadataFile = Path.Combine(_cacheDirectory, ".cache_metadata");
        if (!File.Exists(metadataFile))
        {
            return;
        }

        try
        {
            var lines = File.ReadAllLines(metadataFile);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                {
                    continue;
                }

                var parts = line.Split('|');
                if (parts.Length == 4 && long.TryParse(parts[2], out var size) && DateTime.TryParse(parts[3], out var cachedAt))
                {
                    var metadata = new CacheMetadata
                    {
                        FilePath = parts[0],
                        FileHash = parts[1],
                        FileSize = size,
                        CachedAt = cachedAt,
                    };

                    var cacheKey = GetCacheKey(metadata.FilePath, metadata.FileHash);
                    _cacheMetadata[cacheKey] = metadata;
                }
            }

            _logger.LogInformation("Loaded {Count} cache metadata entries", _cacheMetadata.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading cache metadata");
        }
    }

    /// <summary>
    /// Save cache metadata to disk
    /// </summary>
    private void SaveCacheMetadata()
    {
        var metadataFile = Path.Combine(_cacheDirectory, ".cache_metadata");

        try
        {
            var lines = new List<string> { "# Animation Cache Metadata - Auto-generated" };
            foreach (var kvp in _cacheMetadata)
            {
                var metadata = kvp.Value;
                lines.Add($"{metadata.FilePath}|{metadata.FileHash}|{metadata.FileSize}|{metadata.CachedAt:O}");
            }

            File.WriteAllLines(metadataFile, lines);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving cache metadata");
        }
    }

    /// <summary>
    /// Add a downloaded file to the wallpaper gallery (non-critical, won't throw)
    /// </summary>
    private void AddToGallery(string filePath)
    {
        try
        {
            if (ConfigurationManager.AddToGalleryIfMissing(filePath))
            {
                _logger.LogInformation("Added animation file to wallpaper gallery: {FilePath}", filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add animation file to gallery (non-critical)");
        }
    }

    /// <summary>
    /// Get cache statistics
    /// </summary>
    public (int fileCount, long totalSize) GetCacheStats()
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                return (0, 0);
            }

            var files = Directory.GetFiles(_cacheDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("."))
                .ToList();

            var totalSize = files.Sum(f => new FileInfo(f).Length);
            return (files.Count, totalSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache statistics");
            return (0, 0);
        }
    }

    /// <summary>
    /// Clear animation cache
    /// </summary>
    public void ClearCache()
    {
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                var files = Directory.GetFiles(_cacheDirectory, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => !Path.GetFileName(f).StartsWith("."))
                    .ToList();

                foreach (var file in files)
                {
                    File.Delete(file);
                }

                _cacheMetadata.Clear();
                SaveCacheMetadata();
                _logger.LogInformation("Cleared animation cache: {FileCount} files deleted", files.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cache");
        }
    }

    /// <summary>
    /// Get cache directory path
    /// </summary>
    public string GetCachePath()
    {
        return _cacheDirectory;
    }

    /// <summary>
    /// Check if file is cached
    /// </summary>
    public bool IsCached(string serverFilePath, string fileHash)
    {
        var cachedFile = GetCachedFilePath(serverFilePath, fileHash);
        return File.Exists(cachedFile);
    }
}

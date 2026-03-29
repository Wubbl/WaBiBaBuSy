using Grpc.Core;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Update;

namespace WaBiBaBuSy.Core.Services.Update;

/// <summary>
/// Downloads update packages from the server via gRPC streaming
/// </summary>
public class UpdateDownloader
{
    private readonly ILogger<UpdateDownloader> _logger;
    private readonly UpdateVerifier _verifier;

    public UpdateDownloader(ILogger<UpdateDownloader> logger, UpdateVerifier verifier)
    {
        _logger = logger;
        _verifier = verifier;
    }

    public event EventHandler<UpdateProgressEventArgs>? ProgressChanged;

    /// <summary>
    /// Download an update package from the server
    /// </summary>
    /// <param name="grpcClient">Connected gRPC client</param>
    /// <param name="updateInfo">Information about the update to download</param>
    /// <param name="downloadDirectory">Directory to save the package</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the downloaded package file</returns>
    public async Task<string> DownloadUpdateAsync(
        WallpaperSync.WallpaperSyncClient grpcClient,
        UpdateInfo updateInfo,
        string downloadDirectory,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting download of update package version {Version}", updateInfo.Version);

        // Ensure download directory exists
        Directory.CreateDirectory(downloadDirectory);

        // Check disk space
        if (!_verifier.CheckDiskSpace(downloadDirectory, updateInfo.PackageSize))
        {
            throw new InvalidOperationException("Insufficient disk space for update");
        }

        var packageFileName = $"UpdatePackage_{updateInfo.Version}.zip";
        var packagePath = Path.Combine(downloadDirectory, packageFileName);

        try
        {
            // Clean up any leftover file from a previous download attempt
            if (File.Exists(packagePath))
            {
                try
                {
                    File.Delete(packagePath);
                    _logger.LogInformation("Deleted existing package file: {Path}", packagePath);
                }
                catch (IOException ex)
                {
                    // File is locked — use a unique filename instead
                    _logger.LogWarning(ex, "Could not delete existing package file (in use), using alternate filename");
                    packagePath = Path.Combine(downloadDirectory, $"UpdatePackage_{updateInfo.Version}_{Guid.NewGuid():N}.zip");
                }
            }

            // Request update download from server
            var request = new UpdateDownloadRequest
            {
                ClientId = "current-client", // TODO: Get from client service
                RequestedVersion = updateInfo.Version
            };

            long totalBytesReceived = 0;
            int chunksReceived = 0;
            string? packageHashFromChunks = null;

            // File stream is scoped here so it is fully closed/flushed before verification opens the same file
            using (var call = grpcClient.DownloadUpdate(request, cancellationToken: cancellationToken))
            await using (var fileStream = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 8192, useAsync: true))
            {
                await foreach (var chunk in call.ResponseStream.ReadAllAsync(cancellationToken))
                {
                    // Write chunk data to file
                    await fileStream.WriteAsync(chunk.Data.ToByteArray(), cancellationToken);

                    // Capture package hash from the server (sent in every chunk)
                    if (!string.IsNullOrEmpty(chunk.PackageHash))
                        packageHashFromChunks = chunk.PackageHash;

                    totalBytesReceived += chunk.Data.Length;
                    chunksReceived++;

                    // Use chunk.TotalSize as fallback when PackageSize is unknown (0)
                    var totalSize = updateInfo.PackageSize > 0 ? updateInfo.PackageSize : chunk.TotalSize;
                    var progressPercent = totalSize > 0
                        ? (int)((totalBytesReceived * 100) / totalSize)
                        : 0;
                    ProgressChanged?.Invoke(this, new UpdateProgressEventArgs
                    {
                        ProgressPercent = progressPercent,
                        BytesReceived = totalBytesReceived,
                        TotalBytes = totalSize,
                        ChunksReceived = chunksReceived
                    });

                    _logger.LogDebug("Downloaded chunk {ChunkIndex}/{TotalChunks} ({Bytes} bytes)",
                        chunk.ChunkIndex + 1, chunk.TotalChunks, chunk.Data.Length);
                }

                await fileStream.FlushAsync(cancellationToken);
            }
            // fileStream and gRPC call are fully disposed here — file handle is released before verification

            _logger.LogInformation("Download complete. Total size: {Size:N0} bytes in {Chunks} chunks",
                totalBytesReceived, chunksReceived);

            // Verify package hash — prefer hash from chunks; fall back to updateInfo (may be empty if server deferred it)
            var expectedHash = !string.IsNullOrEmpty(updateInfo.PackageHash)
                ? updateInfo.PackageHash
                : packageHashFromChunks;

            if (string.IsNullOrEmpty(expectedHash))
            {
                _logger.LogWarning("No package hash available from server — skipping integrity check");
                return packagePath;
            }

            _logger.LogInformation("Verifying package integrity...");
            var isValid = await _verifier.VerifyFileHashAsync(packagePath, expectedHash, cancellationToken);

            if (!isValid)
            {
                // Delete corrupted package
                File.Delete(packagePath);
                throw new InvalidOperationException("Downloaded package failed integrity check (SHA-256 mismatch)");
            }

            _logger.LogInformation("Package integrity verified successfully");
            return packagePath;
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "gRPC error during update download: {Status}", ex.StatusCode);

            // Clean up partial download
            if (File.Exists(packagePath))
            {
                try { File.Delete(packagePath); } catch { /* Best effort */ }
            }

            throw new InvalidOperationException($"Failed to download update: {ex.Status.Detail}", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading update package");

            // Clean up partial download
            if (File.Exists(packagePath))
            {
                try { File.Delete(packagePath); } catch { /* Best effort */ }
            }

            throw;
        }
    }
}

/// <summary>
/// Event args for update download progress
/// </summary>
public class UpdateProgressEventArgs : EventArgs
{
    public int ProgressPercent { get; set; }
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public int ChunksReceived { get; set; }
}

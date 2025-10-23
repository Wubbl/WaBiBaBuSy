using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Update;

namespace WaBiBaBuSy.Core.Services.Update;

/// <summary>
/// Verifies integrity of update packages using SHA-256 hashing
/// </summary>
public class UpdateVerifier
{
    private readonly ILogger<UpdateVerifier> _logger;

    public UpdateVerifier(ILogger<UpdateVerifier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Compute SHA-256 hash of a file
    /// </summary>
    public async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 8192, useAsync: true);
            using var sha256 = SHA256.Create();
            var hashBytes = await sha256.ComputeHashAsync(fileStream, cancellationToken);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing hash for file: {FilePath}", filePath);
            throw;
        }
    }

    /// <summary>
    /// Verify a file matches expected hash
    /// </summary>
    public async Task<bool> VerifyFileHashAsync(string filePath, string expectedHash, CancellationToken cancellationToken = default)
    {
        try
        {
            var actualHash = await ComputeFileHashAsync(filePath, cancellationToken);
            var matches = string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);

            if (!matches)
            {
                _logger.LogWarning("Hash mismatch for {FilePath}. Expected: {Expected}, Actual: {Actual}",
                    filePath, expectedHash, actualHash);
            }

            return matches;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying hash for file: {FilePath}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Verify all files in manifest match their expected hashes
    /// </summary>
    public async Task<bool> VerifyManifestFilesAsync(UpdateManifest manifest, string baseDirectory,
                                                       CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Verifying {Count} files in update manifest", manifest.Files.Count);

        var verificationTasks = manifest.Files.Select(async fileInfo =>
        {
            var fullPath = Path.Combine(baseDirectory, fileInfo.Path);
            if (!File.Exists(fullPath))
            {
                _logger.LogError("File not found: {FilePath}", fullPath);
                return false;
            }

            return await VerifyFileHashAsync(fullPath, fileInfo.Sha256, cancellationToken);
        });

        var results = await Task.WhenAll(verificationTasks);
        var allValid = results.All(r => r);

        if (allValid)
        {
            _logger.LogInformation("All files verified successfully");
        }
        else
        {
            _logger.LogError("One or more files failed verification");
        }

        return allValid;
    }

    /// <summary>
    /// Check if sufficient disk space is available
    /// </summary>
    public bool CheckDiskSpace(string path, long requiredBytes, long minimumFreeBytes = 500_000_000)
    {
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? "C:\\");
            var availableSpace = driveInfo.AvailableFreeSpace;
            var totalRequired = requiredBytes + minimumFreeBytes;

            if (availableSpace < totalRequired)
            {
                _logger.LogWarning(
                    "Insufficient disk space. Available: {Available:N0} bytes, Required: {Required:N0} bytes",
                    availableSpace, totalRequired);
                return false;
            }

            _logger.LogInformation("Disk space check passed. Available: {Available:N0} bytes", availableSpace);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking disk space for path: {Path}", path);
            return false;
        }
    }
}

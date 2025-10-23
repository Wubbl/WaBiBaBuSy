using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Common.Version;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Update;

namespace WaBiBaBuSy.Core.Services.Update;

/// <summary>
/// Manages the complete update lifecycle: check, download, extract, and apply
/// </summary>
public class UpdateManager
{
    private readonly ILogger<UpdateManager> _logger;
    private readonly UpdateDownloader _downloader;
    private readonly UpdateVerifier _verifier;
    private readonly string _downloadDirectory;
    private readonly string _backupDirectory;

    public UpdateManager(
        ILogger<UpdateManager> logger,
        UpdateDownloader downloader,
        UpdateVerifier verifier,
        string downloadDirectory,
        string backupDirectory)
    {
        _logger = logger;
        _downloader = downloader;
        _verifier = verifier;
        _downloadDirectory = downloadDirectory;
        _backupDirectory = backupDirectory;

        // Ensure directories exist
        Directory.CreateDirectory(_downloadDirectory);
        Directory.CreateDirectory(_backupDirectory);
    }

    public event EventHandler<UpdateStatusInfo>? StatusChanged;

    /// <summary>
    /// Check if an update is available from the server
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(
        WallpaperSync.WallpaperSyncClient grpcClient,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Checking for updates. Current version: {Version} build {Build}",
            VersionInfo.AppVersion, VersionInfo.BuildNumber);

        RaiseStatusChanged(new UpdateStatusInfo
        {
            Status = UpdateStatusType.Checking,
            UpdateId = VersionInfo.AppVersion
        });

        try
        {
            var request = new UpdateCheckRequest
            {
                ClientId = clientId,
                CurrentVersion = VersionInfo.AppVersion,
                CurrentBuild = VersionInfo.BuildNumber
            };

            var response = await grpcClient.CheckForUpdatesAsync(request, cancellationToken: cancellationToken);

            if (!response.UpdateAvailable)
            {
                _logger.LogInformation("No updates available");
                return null;
            }

            _logger.LogInformation("Update available: Version {Version} build {Build} ({Size:N0} bytes)",
                response.NewVersion, response.NewBuild, response.PackageSize);

            return new UpdateInfo
            {
                Version = response.NewVersion,
                BuildNumber = response.NewBuild,
                PackageSize = response.PackageSize,
                PackageHash = response.PackageHash,
                ReleaseNotes = response.ReleaseNotes,
                IsMandatory = response.IsMandatory,
                PackageFiles = response.PackageFiles.ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for updates");
            RaiseStatusChanged(new UpdateStatusInfo
            {
                Status = UpdateStatusType.Failed,
                ErrorMessage = $"Failed to check for updates: {ex.Message}"
            });
            throw;
        }
    }

    /// <summary>
    /// Download and prepare an update package
    /// </summary>
    public async Task<string> DownloadAndPrepareUpdateAsync(
        WallpaperSync.WallpaperSyncClient grpcClient,
        UpdateInfo updateInfo,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Downloading update version {Version}", updateInfo.Version);

        RaiseStatusChanged(new UpdateStatusInfo
        {
            Status = UpdateStatusType.Downloading,
            UpdateId = updateInfo.Version
        });

        // Subscribe to download progress
        _downloader.ProgressChanged += (sender, args) =>
        {
            RaiseStatusChanged(new UpdateStatusInfo
            {
                Status = UpdateStatusType.Downloading,
                ProgressPercent = args.ProgressPercent,
                UpdateId = updateInfo.Version
            });
        };

        try
        {
            // Download the package
            var packagePath = await _downloader.DownloadUpdateAsync(
                grpcClient, updateInfo, _downloadDirectory, cancellationToken);

            RaiseStatusChanged(new UpdateStatusInfo
            {
                Status = UpdateStatusType.Downloaded,
                ProgressPercent = 100,
                UpdateId = updateInfo.Version
            });

            // Extract the package
            _logger.LogInformation("Extracting update package...");
            var extractPath = Path.Combine(_downloadDirectory, $"Extract_{updateInfo.Version}");
            Directory.CreateDirectory(extractPath);

            ZipFile.ExtractToDirectory(packagePath, extractPath, overwriteFiles: true);
            _logger.LogInformation("Package extracted to: {Path}", extractPath);

            // Load and verify manifest
            RaiseStatusChanged(new UpdateStatusInfo
            {
                Status = UpdateStatusType.Verifying,
                UpdateId = updateInfo.Version
            });

            var manifestPath = Path.Combine(extractPath, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new InvalidOperationException("Update package does not contain a manifest.json file");
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestJson);

            if (manifest == null)
            {
                throw new InvalidOperationException("Failed to parse update manifest");
            }

            _logger.LogInformation("Manifest loaded: Version {Version}, {FileCount} files",
                manifest.Version, manifest.Files.Count);

            // Verify all files in the package
            var isValid = await _verifier.VerifyManifestFilesAsync(manifest, extractPath, cancellationToken);

            if (!isValid)
            {
                throw new InvalidOperationException("Update package verification failed");
            }

            RaiseStatusChanged(new UpdateStatusInfo
            {
                Status = UpdateStatusType.Downloaded,
                ProgressPercent = 100,
                UpdateId = updateInfo.Version
            });

            return extractPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error preparing update");
            RaiseStatusChanged(new UpdateStatusInfo
            {
                Status = UpdateStatusType.Failed,
                ErrorMessage = $"Failed to prepare update: {ex.Message}",
                UpdateId = updateInfo.Version
            });
            throw;
        }
    }

    /// <summary>
    /// Create a backup of the current installation
    /// </summary>
    public async Task<string> CreateBackupAsync(string installationPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating backup of current version {Version}", VersionInfo.AppVersion);

        var backupPath = Path.Combine(_backupDirectory, $"Backup_{VersionInfo.AppVersion}_{DateTime.UtcNow:yyyyMMddHHmmss}");
        Directory.CreateDirectory(backupPath);

        try
        {
            // Copy main executable and DLLs
            var filesToBackup = Directory.GetFiles(installationPath, "WaBiBaBuSy.*", SearchOption.TopDirectoryOnly);

            foreach (var file in filesToBackup)
            {
                var fileName = Path.GetFileName(file);
                var destPath = Path.Combine(backupPath, fileName);
                await Task.Run(() => File.Copy(file, destPath, overwrite: true), cancellationToken);
                _logger.LogDebug("Backed up: {FileName}", fileName);
            }

            _logger.LogInformation("Backup created successfully at: {BackupPath}", backupPath);

            // Clean up old backups (keep only last 2)
            await CleanupOldBackupsAsync(2);

            return backupPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating backup");
            throw;
        }
    }

    /// <summary>
    /// Remove old backup directories, keeping only the most recent N backups
    /// </summary>
    private async Task CleanupOldBackupsAsync(int keepCount)
    {
        await Task.Run(() =>
        {
            try
            {
                var backupDirs = Directory.GetDirectories(_backupDirectory, "Backup_*")
                    .OrderByDescending(d => Directory.GetCreationTimeUtc(d))
                    .Skip(keepCount)
                    .ToList();

                foreach (var dir in backupDirs)
                {
                    _logger.LogInformation("Removing old backup: {BackupPath}", dir);
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up old backups");
            }
        });
    }

    private void RaiseStatusChanged(UpdateStatusInfo statusInfo)
    {
        statusInfo.LastUpdated = DateTime.UtcNow;
        StatusChanged?.Invoke(this, statusInfo);
    }
}

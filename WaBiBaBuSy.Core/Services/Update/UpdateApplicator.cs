using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.Core.Services.Update;

/// <summary>
/// Applies updates by launching the standalone updater executable
/// </summary>
public class UpdateApplicator
{
    private readonly ILogger<UpdateApplicator> _logger;

    public UpdateApplicator(ILogger<UpdateApplicator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Launch the updater and exit the current application
    /// </summary>
    /// <param name="updateDirectory">Directory containing extracted update files</param>
    /// <param name="installDirectory">Application installation directory</param>
    /// <param name="backupDirectory">Directory to store backup files</param>
    /// <param name="updaterExePath">Path to WaBiBaBuSy.Updater.exe (optional, auto-detected if not provided)</param>
    /// <returns>True if updater launched successfully</returns>
    public bool LaunchUpdaterAndExit(
        string updateDirectory,
        string installDirectory,
        string backupDirectory,
        string? updaterExePath = null)
    {
        try
        {
            // Find updater executable
            if (string.IsNullOrEmpty(updaterExePath))
            {
                // Look in the update package first
                updaterExePath = Path.Combine(updateDirectory, "updater", "WaBiBaBuSy.Updater.exe");

                if (!File.Exists(updaterExePath))
                {
                    // Try current directory
                    updaterExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WaBiBaBuSy.Updater.exe");
                }

                if (!File.Exists(updaterExePath))
                {
                    _logger.LogError("Updater executable not found. Searched: {Path1}, {Path2}",
                        Path.Combine(updateDirectory, "updater", "WaBiBaBuSy.Updater.exe"),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WaBiBaBuSy.Updater.exe"));
                    return false;
                }
            }

            _logger.LogInformation("Found updater: {UpdaterPath}", updaterExePath);

            // Get current process ID
            var currentProcessId = Process.GetCurrentProcess().Id;

            // Build command-line arguments for updater
            var arguments = $"--update-dir \"{updateDirectory}\" " +
                          $"--install-dir \"{installDirectory}\" " +
                          $"--backup-dir \"{backupDirectory}\" " +
                          $"--process-id {currentProcessId}";

            _logger.LogInformation("Launching updater with arguments: {Arguments}", arguments);

            // Launch updater process — Verb="runas" ensures UAC elevation so the updater
            // can write to C:\Program Files\ (manifest also requests requireAdministrator)
            var startInfo = new ProcessStartInfo
            {
                FileName = updaterExePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(updaterExePath) ?? AppDomain.CurrentDomain.BaseDirectory
            };

            var updaterProcess = Process.Start(startInfo);

            if (updaterProcess == null)
            {
                _logger.LogError("Failed to start updater process");
                return false;
            }

            _logger.LogInformation("Updater launched successfully (PID: {UpdaterPid}). Current process will exit.", updaterProcess.Id);

            // Give the updater a moment to initialize
            Thread.Sleep(500);

            // Exit current application - updater will wait for this process to terminate
            Environment.Exit(0);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error launching updater");
            return false;
        }
    }

    /// <summary>
    /// Copy updater executable from update package to a temporary location
    /// This ensures the updater can run even if it's replacing its own files
    /// </summary>
    /// <param name="updateDirectory">Directory containing update package</param>
    /// <returns>Path to the copied updater executable, or null if failed</returns>
    public string? ExtractUpdaterToTemp(string updateDirectory)
    {
        try
        {
            var sourceUpdater = Path.Combine(updateDirectory, "updater", "WaBiBaBuSy.Updater.exe");

            if (!File.Exists(sourceUpdater))
            {
                _logger.LogError("Updater not found in update package: {Path}", sourceUpdater);
                return null;
            }

            // Copy to temp directory
            var tempDir = Path.Combine(Path.GetTempPath(), "WaBiBaBuSy_Update", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            var tempUpdaterPath = Path.Combine(tempDir, "WaBiBaBuSy.Updater.exe");
            File.Copy(sourceUpdater, tempUpdaterPath, overwrite: true);

            _logger.LogInformation("Extracted updater to temp location: {TempPath}", tempUpdaterPath);
            return tempUpdaterPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting updater to temp");
            return null;
        }
    }

    /// <summary>
    /// Validate that all necessary update components are present
    /// </summary>
    public bool ValidateUpdatePackage(string updateDirectory)
    {
        try
        {
            // Check for manifest
            var manifestPath = Path.Combine(updateDirectory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                _logger.LogError("Update package missing manifest.json");
                return false;
            }

            // Check for binaries directory
            var binariesDir = Path.Combine(updateDirectory, "binaries");
            if (!Directory.Exists(binariesDir))
            {
                _logger.LogError("Update package missing binaries directory");
                return false;
            }

            // Check for updater — package location preferred, fallback to current install dir
            var updaterPath = Path.Combine(updateDirectory, "updater", "WaBiBaBuSy.Updater.exe");
            if (!File.Exists(updaterPath))
            {
                var installUpdater = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WaBiBaBuSy.Updater.exe");
                if (!File.Exists(installUpdater))
                {
                    _logger.LogError("Update package missing updater executable (searched: {PackagePath}, {InstallPath})",
                        updaterPath, installUpdater);
                    return false;
                }
                _logger.LogWarning("Updater not in update package, will use installed updater: {InstallPath}", installUpdater);
            }

            _logger.LogInformation("Update package validation successful");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating update package");
            return false;
        }
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Common.Update;

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
    /// <summary>
    /// One quoted command-line argument. Backslashes right before the closing quote are doubled: the install dir
    /// is AppContext.BaseDirectory, which ends in '\', and <c>"C:\…\WaBiBaBuSy\"</c> made Windows read <c>\"</c> as a
    /// literal quote and swallow the rest of the line — the updater then exited on missing options (review E1).
    /// </summary>
    internal static string QuoteArg(string value)
    {
        int trailing = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', trailing) + "\"";
    }

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
                updaterExePath = Path.Combine(updateDirectory, "updater", UpdaterFiles.ExeName);

                if (File.Exists(updaterExePath) && !EnsurePackageUpdaterDependencies(updateDirectory))
                {
                    // Package updater would crash on startup (missing assembly) — use the installed one
                    updaterExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, UpdaterFiles.ExeName);
                }

                if (!File.Exists(updaterExePath))
                {
                    // Try current directory
                    updaterExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, UpdaterFiles.ExeName);
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

            // Run a temp copy (exe + its dependency DLLs): the installed updater cannot overwrite its own exe or
            // System.CommandLine.dll while it runs (the update then failed and rolled back), and its self-delete
            // after a successful run removed it from the install folder (review 2026-10-13, part 8).
            var tempUpdaterDir = Path.Combine(Path.GetTempPath(), "WaBiBaBuSy_Update", Guid.NewGuid().ToString("N"));
            var missing = UpdaterFiles.CopyUpdater(Path.GetDirectoryName(updaterExePath)!, tempUpdaterDir);
            if (missing.Count > 0)
                _logger.LogWarning("Updater copy is missing {Missing}; it may not start", string.Join(", ", missing));
            updaterExePath = Path.Combine(tempUpdaterDir, UpdaterFiles.ExeName);

            // Get current process ID
            var currentProcessId = Process.GetCurrentProcess().Id;

            // Build command-line arguments for updater
            var arguments = $"--update-dir {QuoteArg(updateDirectory)} " +
                          $"--install-dir {QuoteArg(installDirectory)} " +
                          $"--backup-dir {QuoteArg(backupDirectory)} " +
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
    /// Make sure the package's updater folder contains the updater's dependency assemblies.
    /// Packages built by older servers shipped only <c>WaBiBaBuSy.Updater.*</c>; the missing
    /// System.CommandLine.dll is taken from the package's <c>binaries/</c> folder.
    /// </summary>
    /// <returns>True if the package updater has everything it needs to start.</returns>
    private bool EnsurePackageUpdaterDependencies(string updateDirectory)
    {
        var updaterDir = Path.Combine(updateDirectory, "updater");
        try
        {
            var missing = UpdaterFiles.RepairDependencies(updaterDir, Path.Combine(updateDirectory, "binaries"));
            if (missing.Count == 0)
                return true;

            _logger.LogWarning("Package updater is missing dependencies {Missing}; falling back to the installed updater",
                string.Join(", ", missing));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify package updater dependencies; falling back to the installed updater");
            return false;
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

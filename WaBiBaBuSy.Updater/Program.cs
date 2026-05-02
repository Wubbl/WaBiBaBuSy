using System.CommandLine;
using System.Diagnostics;
using System.Reflection;

namespace WaBiBaBuSy.Updater;

/// <summary>
/// WaBiBaBuSy Update Installer
///
/// This standalone executable replaces the main application files while it's not running.
/// It is launched by the main application before it exits.
///
/// Usage:
///   WaBiBaBuSy.Updater.exe --update-dir "path" --install-dir "path" --backup-dir "path" --process-id 1234
/// </summary>
class Program
{
    static StreamWriter? _logFile;

    static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine(line);
        try { _logFile?.WriteLine(line); _logFile?.Flush(); } catch { /* best effort */ }
    }

    static async Task<int> Main(string[] args)
    {
        // Initialize file log before anything else
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WaBiBaBuSy", "Logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"updater-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _logFile = new StreamWriter(logPath, append: false) { AutoFlush = true };
            Console.WriteLine($"Log file: {logPath}");
        }
        catch
        {
            // Logging failure must not prevent the update
        }

        Log("=================================================");
        Log("  WaBiBaBuSy Updater");
        Log($"  Version: {GetVersion()}");
        Log("=================================================");
        Log("");

        // Define command-line options
        var updateDirOption = new Option<string>("--update-dir")
        {
            Description = "Directory containing extracted update files",
            Required = true
        };

        var installDirOption = new Option<string>("--install-dir")
        {
            Description = "Application installation directory",
            Required = true
        };

        var backupDirOption = new Option<string>("--backup-dir")
        {
            Description = "Directory to store backup files",
            Required = true
        };

        var processIdOption = new Option<int>("--process-id")
        {
            Description = "Process ID of the main application to wait for",
            Required = true
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Force kill the process if it doesn't exit gracefully"
        };

        var noLaunchOption = new Option<bool>("--no-launch")
        {
            Description = "Don't launch the application after update"
        };

        // Create root command
        var rootCommand = new RootCommand("WaBiBaBuSy Update Installer - Replaces application files safely");
        rootCommand.Options.Add(updateDirOption);
        rootCommand.Options.Add(installDirOption);
        rootCommand.Options.Add(backupDirOption);
        rootCommand.Options.Add(processIdOption);
        rootCommand.Options.Add(forceOption);
        rootCommand.Options.Add(noLaunchOption);

        rootCommand.SetAction(async (parseResult, ct) =>
        {
            var updateDir = parseResult.GetValue(updateDirOption)!;
            var installDir = parseResult.GetValue(installDirOption)!;
            var backupDir = parseResult.GetValue(backupDirOption)!;
            var processId = parseResult.GetValue(processIdOption);
            var force = parseResult.GetValue(forceOption);
            var noLaunch = parseResult.GetValue(noLaunchOption);

            return await PerformUpdateAsync(updateDir, installDir, backupDir, processId, force, noLaunch);
        });

        return await rootCommand.Parse(args).InvokeAsync();
    }

    /// <summary>
    /// Perform the update process
    /// </summary>
    static async Task<int> PerformUpdateAsync(
        string updateDir,
        string installDir,
        string backupDir,
        int processId,
        bool force,
        bool noLaunch)
    {
        Log($"Update Directory: {updateDir}");
        Log($"Install Directory: {installDir}");
        Log($"Backup Directory: {backupDir}");
        Log($"Target Process ID: {processId}");
        Log("");

        try
        {
            // Step 1: Validate directories
            if (!Directory.Exists(updateDir))
            {
                Log($"ERROR: Update directory not found: {updateDir}");
                return 1;
            }

            if (!Directory.Exists(installDir))
            {
                Log($"ERROR: Installation directory not found: {installDir}");
                return 1;
            }

            // Step 2: Wait for main application to exit
            Log("Step 1/4: Waiting for main application to exit...");
            var processMonitor = new ProcessMonitor(processId, "WaBiBaBuSy.UI");
            var exited = await processMonitor.WaitForProcessExitAsync(timeoutSeconds: 30);

            if (!exited)
            {
                if (force)
                {
                    Log("Process did not exit gracefully. Force-killing...");
                    if (!processMonitor.ForceKillProcess())
                    {
                        Log("ERROR: Failed to terminate process");
                        return 1;
                    }
                }
                else
                {
                    Log("ERROR: Process did not exit in time. Use --force to kill it.");
                    return 1;
                }
            }

            // Extra safety: wait a bit to ensure all file handles are released
            Log("Waiting for file handles to be released...");
            await Task.Delay(2000);

            // Step 3: Replace files
            Log("Step 2/4: Replacing application files...");
            var fileReplacer = new FileReplacer(installDir, backupDir);
            var replaceSuccess = await fileReplacer.ReplaceFilesAsync(updateDir, createBackup: true);

            if (!replaceSuccess)
            {
                Log("ERROR: File replacement failed. Update aborted.");
                return 1;
            }

            // Step 4: Verify installation
            Log("Step 3/4: Verifying installation...");
            var mainExe = Path.Combine(installDir, "WaBiBaBuSy.UI.exe");
            if (!File.Exists(mainExe))
            {
                Log($"ERROR: Main executable not found after update: {mainExe}");
                Log("Attempting rollback...");
                await fileReplacer.RollbackAsync();
                return 1;
            }

            Log("Installation verified successfully");

            // Step 5: Launch updated application
            if (!noLaunch)
            {
                Log("Step 4/4: Launching updated application...");
                await Task.Delay(1000); // Brief pause before launch

                if (processMonitor.LaunchApplication(mainExe, installDir))
                {
                    Log("Updated application launched successfully");
                }
                else
                {
                    Log("WARNING: Failed to launch updated application");
                    Log($"Please manually launch: {mainExe}");
                }
            }
            else
            {
                Log("Step 4/4: Skipping application launch (--no-launch specified)");
            }

            // Step 6: Self-cleanup (schedule this updater for deletion)
            Log("");
            Log("Update completed successfully!");
            Log("Scheduling updater cleanup...");
            ScheduleSelfDelete();

            return 0;
        }
        catch (Exception ex)
        {
            Log("");
            Log($"FATAL ERROR: {ex.Message}");
            Log(ex.StackTrace ?? "");
            return 1;
        }
        finally
        {
            _logFile?.Dispose();
        }
    }

    /// <summary>
    /// Schedule this updater executable for deletion after it exits
    /// </summary>
    static void ScheduleSelfDelete()
    {
        try
        {
            var updaterPath = Process.GetCurrentProcess().MainModule?.FileName;
            if (updaterPath == null || !File.Exists(updaterPath))
            {
                Log("WARNING: Cannot determine updater path for self-delete");
                return;
            }

            // Create a batch file that waits and then deletes the updater
            var batchFile = Path.Combine(Path.GetTempPath(), "cleanup_updater.bat");
            var batchContent = $@"@echo off
timeout /t 3 /nobreak >nul
del /f /q ""{updaterPath}""
del /f /q ""%~f0""
";

            File.WriteAllText(batchFile, batchContent);

            // Launch the cleanup batch file
            var startInfo = new ProcessStartInfo
            {
                FileName = batchFile,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(startInfo);
            Log("Cleanup scheduled");
        }
        catch (Exception ex)
        {
            Log($"WARNING: Failed to schedule cleanup: {ex.Message}");
        }
    }

    /// <summary>
    /// Get the updater version
    /// </summary>
    static string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "Unknown";
    }
}

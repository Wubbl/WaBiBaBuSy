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
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("  WaBiBaBuSy Updater");
        Console.WriteLine($"  Version: {GetVersion()}");
        Console.WriteLine("=================================================");
        Console.WriteLine();

        // Define command-line options
        var updateDirOption = new Option<string>(
            name: "--update-dir",
            description: "Directory containing extracted update files")
        { IsRequired = true };

        var installDirOption = new Option<string>(
            name: "--install-dir",
            description: "Application installation directory")
        { IsRequired = true };

        var backupDirOption = new Option<string>(
            name: "--backup-dir",
            description: "Directory to store backup files")
        { IsRequired = true };

        var processIdOption = new Option<int>(
            name: "--process-id",
            description: "Process ID of the main application to wait for")
        { IsRequired = true };

        var forceOption = new Option<bool>(
            name: "--force",
            description: "Force kill the process if it doesn't exit gracefully",
            getDefaultValue: () => false);

        var noLaunchOption = new Option<bool>(
            name: "--no-launch",
            description: "Don't launch the application after update",
            getDefaultValue: () => false);

        // Create root command
        var rootCommand = new RootCommand("WaBiBaBuSy Update Installer - Replaces application files safely")
        {
            updateDirOption,
            installDirOption,
            backupDirOption,
            processIdOption,
            forceOption,
            noLaunchOption
        };

        rootCommand.SetHandler(async (updateDir, installDir, backupDir, processId, force, noLaunch) =>
        {
            var exitCode = await PerformUpdateAsync(updateDir, installDir, backupDir, processId, force, noLaunch);
            Environment.Exit(exitCode);
        },
        updateDirOption, installDirOption, backupDirOption, processIdOption, forceOption, noLaunchOption);

        return await rootCommand.InvokeAsync(args);
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
        Console.WriteLine($"Update Directory: {updateDir}");
        Console.WriteLine($"Install Directory: {installDir}");
        Console.WriteLine($"Backup Directory: {backupDir}");
        Console.WriteLine($"Target Process ID: {processId}");
        Console.WriteLine();

        try
        {
            // Step 1: Validate directories
            if (!Directory.Exists(updateDir))
            {
                Console.WriteLine($"ERROR: Update directory not found: {updateDir}");
                return 1;
            }

            if (!Directory.Exists(installDir))
            {
                Console.WriteLine($"ERROR: Installation directory not found: {installDir}");
                return 1;
            }

            // Step 2: Wait for main application to exit
            Console.WriteLine("Step 1/4: Waiting for main application to exit...");
            var processMonitor = new ProcessMonitor(processId, "WaBiBaBuSy.UI");
            var exited = await processMonitor.WaitForProcessExitAsync(timeoutSeconds: 30);

            if (!exited)
            {
                if (force)
                {
                    Console.WriteLine("Process did not exit gracefully. Force-killing...");
                    if (!processMonitor.ForceKillProcess())
                    {
                        Console.WriteLine("ERROR: Failed to terminate process");
                        return 1;
                    }
                }
                else
                {
                    Console.WriteLine("ERROR: Process did not exit in time. Use --force to kill it.");
                    return 1;
                }
            }

            // Extra safety: wait a bit to ensure all file handles are released
            Console.WriteLine("Waiting for file handles to be released...");
            await Task.Delay(2000);

            // Step 3: Replace files
            Console.WriteLine("Step 2/4: Replacing application files...");
            var fileReplacer = new FileReplacer(installDir, backupDir);
            var replaceSuccess = await fileReplacer.ReplaceFilesAsync(updateDir, createBackup: true);

            if (!replaceSuccess)
            {
                Console.WriteLine("ERROR: File replacement failed. Update aborted.");
                return 1;
            }

            // Step 4: Verify installation
            Console.WriteLine("Step 3/4: Verifying installation...");
            var mainExe = Path.Combine(installDir, "WaBiBaBuSy.UI.exe");
            if (!File.Exists(mainExe))
            {
                Console.WriteLine($"ERROR: Main executable not found after update: {mainExe}");
                Console.WriteLine("Attempting rollback...");
                await fileReplacer.RollbackAsync();
                return 1;
            }

            Console.WriteLine("Installation verified successfully");

            // Step 5: Launch updated application
            if (!noLaunch)
            {
                Console.WriteLine("Step 4/4: Launching updated application...");
                await Task.Delay(1000); // Brief pause before launch

                if (processMonitor.LaunchApplication(mainExe, installDir))
                {
                    Console.WriteLine("Updated application launched successfully");
                }
                else
                {
                    Console.WriteLine("WARNING: Failed to launch updated application");
                    Console.WriteLine($"Please manually launch: {mainExe}");
                }
            }
            else
            {
                Console.WriteLine("Step 4/4: Skipping application launch (--no-launch specified)");
            }

            // Step 6: Self-cleanup (schedule this updater for deletion)
            Console.WriteLine();
            Console.WriteLine("Update completed successfully!");
            Console.WriteLine("Scheduling updater cleanup...");
            ScheduleSelfDelete();

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"FATAL ERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
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
                Console.WriteLine("WARNING: Cannot determine updater path for self-delete");
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
            Console.WriteLine("Cleanup scheduled");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Failed to schedule cleanup: {ex.Message}");
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

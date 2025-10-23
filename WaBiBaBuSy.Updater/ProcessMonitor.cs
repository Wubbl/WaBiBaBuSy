using System.Diagnostics;

namespace WaBiBaBuSy.Updater;

/// <summary>
/// Monitors and manages process lifecycle during updates
/// </summary>
public class ProcessMonitor
{
    private readonly int _processId;
    private readonly string _processName;

    public ProcessMonitor(int processId, string processName)
    {
        _processId = processId;
        _processName = processName;
    }

    /// <summary>
    /// Wait for the main application process to exit
    /// </summary>
    /// <param name="timeoutSeconds">Maximum time to wait (default: 30 seconds)</param>
    /// <returns>True if process exited, false if timeout</returns>
    public async Task<bool> WaitForProcessExitAsync(int timeoutSeconds = 30)
    {
        Console.WriteLine($"[ProcessMonitor] Waiting for process {_processId} ({_processName}) to exit...");

        try
        {
            var process = Process.GetProcessById(_processId);

            var exitedInTime = await Task.Run(() => process.WaitForExit(timeoutSeconds * 1000));

            if (exitedInTime)
            {
                Console.WriteLine($"[ProcessMonitor] Process {_processId} exited successfully");
                return true;
            }
            else
            {
                Console.WriteLine($"[ProcessMonitor] WARNING: Process {_processId} did not exit within {timeoutSeconds} seconds");
                return false;
            }
        }
        catch (ArgumentException)
        {
            // Process doesn't exist - already exited
            Console.WriteLine($"[ProcessMonitor] Process {_processId} already exited");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProcessMonitor] ERROR checking process: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Forcefully terminate the process if it hasn't exited
    /// </summary>
    public bool ForceKillProcess()
    {
        Console.WriteLine($"[ProcessMonitor] Attempting to force-kill process {_processId}...");

        try
        {
            var process = Process.GetProcessById(_processId);
            process.Kill();
            process.WaitForExit(5000);
            Console.WriteLine($"[ProcessMonitor] Process {_processId} killed successfully");
            return true;
        }
        catch (ArgumentException)
        {
            Console.WriteLine($"[ProcessMonitor] Process {_processId} already terminated");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProcessMonitor] ERROR killing process: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Launch the updated application
    /// </summary>
    /// <param name="executablePath">Path to the executable to launch</param>
    /// <param name="workingDirectory">Working directory for the process</param>
    /// <returns>True if launched successfully</returns>
    public bool LaunchApplication(string executablePath, string workingDirectory)
    {
        Console.WriteLine($"[ProcessMonitor] Launching updated application: {executablePath}");

        try
        {
            if (!File.Exists(executablePath))
            {
                Console.WriteLine($"[ProcessMonitor] ERROR: Executable not found: {executablePath}");
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };

            var process = Process.Start(startInfo);

            if (process != null)
            {
                Console.WriteLine($"[ProcessMonitor] Application launched successfully (PID: {process.Id})");
                return true;
            }
            else
            {
                Console.WriteLine($"[ProcessMonitor] ERROR: Failed to start process");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProcessMonitor] ERROR launching application: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if any process with the given name is running
    /// </summary>
    public static bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        return processes.Length > 0;
    }

    /// <summary>
    /// Get all processes with the given name
    /// </summary>
    public static Process[] GetProcessesByName(string processName)
    {
        return Process.GetProcessesByName(processName);
    }
}

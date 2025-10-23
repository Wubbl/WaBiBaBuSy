using System.Security.Cryptography;

namespace WaBiBaBuSy.Updater;

/// <summary>
/// Safely replaces files during update process with backup and rollback support
/// </summary>
public class FileReplacer
{
    private readonly string _installDirectory;
    private readonly string _backupDirectory;
    private readonly List<FileReplacement> _replacements = new();

    public FileReplacer(string installDirectory, string backupDirectory)
    {
        _installDirectory = installDirectory;
        _backupDirectory = backupDirectory;
    }

    /// <summary>
    /// Replace files in the installation directory with new versions
    /// </summary>
    /// <param name="updateDirectory">Directory containing new files</param>
    /// <param name="createBackup">Whether to create backups before replacing</param>
    /// <returns>True if all replacements succeeded</returns>
    public async Task<bool> ReplaceFilesAsync(string updateDirectory, bool createBackup = true)
    {
        Console.WriteLine($"[FileReplacer] Starting file replacement from: {updateDirectory}");
        Console.WriteLine($"[FileReplacer] Install directory: {_installDirectory}");
        Console.WriteLine($"[FileReplacer] Backup directory: {_backupDirectory}");

        try
        {
            // Find all files to replace (binaries directory in update package)
            var binariesDir = Path.Combine(updateDirectory, "binaries");
            if (!Directory.Exists(binariesDir))
            {
                Console.WriteLine($"[FileReplacer] ERROR: Binaries directory not found: {binariesDir}");
                return false;
            }

            var filesToReplace = Directory.GetFiles(binariesDir, "*.*", SearchOption.AllDirectories);
            Console.WriteLine($"[FileReplacer] Found {filesToReplace.Length} files to replace");

            // Create backup if requested
            if (createBackup)
            {
                Console.WriteLine($"[FileReplacer] Creating backup...");
                if (!await CreateBackupAsync())
                {
                    Console.WriteLine($"[FileReplacer] ERROR: Failed to create backup");
                    return false;
                }
            }

            // Replace each file
            foreach (var sourceFile in filesToReplace)
            {
                var relativePath = Path.GetRelativePath(binariesDir, sourceFile);
                var targetFile = Path.Combine(_installDirectory, relativePath);

                if (!await ReplaceFileAsync(sourceFile, targetFile))
                {
                    Console.WriteLine($"[FileReplacer] ERROR: Failed to replace file: {relativePath}");

                    // Attempt rollback
                    Console.WriteLine($"[FileReplacer] Attempting rollback...");
                    await RollbackAsync();
                    return false;
                }
            }

            Console.WriteLine($"[FileReplacer] All files replaced successfully");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileReplacer] ERROR during file replacement: {ex.Message}");
            await RollbackAsync();
            return false;
        }
    }

    /// <summary>
    /// Replace a single file
    /// </summary>
    private async Task<bool> ReplaceFileAsync(string sourceFile, string targetFile)
    {
        try
        {
            // Ensure target directory exists
            var targetDir = Path.GetDirectoryName(targetFile);
            if (targetDir != null)
            {
                Directory.CreateDirectory(targetDir);
            }

            // Check if target exists and is locked
            if (File.Exists(targetFile))
            {
                if (IsFileLocked(targetFile))
                {
                    Console.WriteLine($"[FileReplacer] WARNING: File is locked: {Path.GetFileName(targetFile)}");
                    // Try waiting a bit
                    await Task.Delay(1000);

                    if (IsFileLocked(targetFile))
                    {
                        Console.WriteLine($"[FileReplacer] ERROR: File remains locked: {Path.GetFileName(targetFile)}");
                        return false;
                    }
                }
            }

            // Compute hashes for verification
            var sourceHash = await ComputeFileHashAsync(sourceFile);

            // Perform the replacement
            File.Copy(sourceFile, targetFile, overwrite: true);

            // Verify the copy
            var targetHash = await ComputeFileHashAsync(targetFile);
            if (sourceHash != targetHash)
            {
                Console.WriteLine($"[FileReplacer] ERROR: Hash mismatch after copy: {Path.GetFileName(targetFile)}");
                return false;
            }

            _replacements.Add(new FileReplacement
            {
                TargetPath = targetFile,
                Succeeded = true
            });

            Console.WriteLine($"[FileReplacer] Replaced: {Path.GetFileName(targetFile)}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileReplacer] ERROR replacing file {Path.GetFileName(targetFile)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Create backup of files about to be replaced
    /// </summary>
    private async Task<bool> CreateBackupAsync()
    {
        try
        {
            Directory.CreateDirectory(_backupDirectory);

            var filesToBackup = Directory.GetFiles(_installDirectory, "WaBiBaBuSy.*", SearchOption.TopDirectoryOnly);

            foreach (var file in filesToBackup)
            {
                var fileName = Path.GetFileName(file);
                var backupPath = Path.Combine(_backupDirectory, fileName);

                await Task.Run(() => File.Copy(file, backupPath, overwrite: true));
                Console.WriteLine($"[FileReplacer] Backed up: {fileName}");
            }

            Console.WriteLine($"[FileReplacer] Backup created successfully ({filesToBackup.Length} files)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileReplacer] ERROR creating backup: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Rollback changes by restoring from backup
    /// </summary>
    public async Task<bool> RollbackAsync()
    {
        Console.WriteLine($"[FileReplacer] Rolling back changes...");

        if (!Directory.Exists(_backupDirectory))
        {
            Console.WriteLine($"[FileReplacer] ERROR: Backup directory not found: {_backupDirectory}");
            return false;
        }

        try
        {
            var backupFiles = Directory.GetFiles(_backupDirectory, "*.*", SearchOption.AllDirectories);
            Console.WriteLine($"[FileReplacer] Restoring {backupFiles.Length} files from backup...");

            foreach (var backupFile in backupFiles)
            {
                var relativePath = Path.GetRelativePath(_backupDirectory, backupFile);
                var targetFile = Path.Combine(_installDirectory, relativePath);

                await Task.Run(() => File.Copy(backupFile, targetFile, overwrite: true));
                Console.WriteLine($"[FileReplacer] Restored: {Path.GetFileName(targetFile)}");
            }

            Console.WriteLine($"[FileReplacer] Rollback completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileReplacer] ERROR during rollback: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if a file is locked by another process
    /// </summary>
    private bool IsFileLocked(string filePath)
    {
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Compute SHA-256 hash of a file
    /// </summary>
    private async Task<string> ComputeFileHashAsync(string filePath)
    {
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 8192, useAsync: true);
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(fileStream);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Clean up backup directory
    /// </summary>
    public void CleanupBackup()
    {
        try
        {
            if (Directory.Exists(_backupDirectory))
            {
                Directory.Delete(_backupDirectory, recursive: true);
                Console.WriteLine($"[FileReplacer] Backup directory cleaned up");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileReplacer] WARNING: Failed to cleanup backup: {ex.Message}");
        }
    }

    private class FileReplacement
    {
        public string TargetPath { get; set; } = string.Empty;
        public bool Succeeded { get; set; }
    }
}

using System.Security.Cryptography;

namespace WaBiBaBuSy.Updater;

/// <summary>
/// Safely replaces files during update process with backup and rollback support
/// </summary>
public class FileReplacer
{
    private readonly string _installDirectory;
    private readonly string _backupDirectory;
    // Review 2026-10-13 E4: this run's backup holds exactly the files the package overwrites (relative paths kept),
    // and _created the ones it adds — rollback restores the first and deletes the second. The old backup covered only
    // top-level WaBiBaBuSy.* files, so a failure left new dependencies next to old app code.
    private readonly string _runBackupDirectory;
    private readonly List<string> _created = new();

    public FileReplacer(string installDirectory, string backupDirectory)
    {
        _installDirectory = installDirectory;
        _backupDirectory = backupDirectory;
        _runBackupDirectory = Path.Combine(backupDirectory, $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
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
                if (!await CreateBackupAsync(binariesDir, filesToReplace))
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
    /// Back up every install file the package overwrites (keeping its relative path) and note the ones it adds.
    /// </summary>
    private async Task<bool> CreateBackupAsync(string binariesDir, IEnumerable<string> packageFiles)
    {
        try
        {
            Directory.CreateDirectory(_runBackupDirectory);
            int count = 0;
            foreach (var sourceFile in packageFiles)
            {
                var relativePath = Path.GetRelativePath(binariesDir, sourceFile);
                var target = Path.Combine(_installDirectory, relativePath);
                if (!File.Exists(target))
                {
                    _created.Add(target);
                    continue;
                }
                var backupPath = Path.Combine(_runBackupDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                await Task.Run(() => File.Copy(target, backupPath, overwrite: true));
                count++;
            }

            Console.WriteLine($"[FileReplacer] Backup created in {_runBackupDirectory} ({count} files, {_created.Count} new)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileReplacer] ERROR creating backup: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Rollback: restore this run's backup and delete the files the update added. Every file is tried even when one fails.
    /// </summary>
    public async Task<bool> RollbackAsync()
    {
        Console.WriteLine($"[FileReplacer] Rolling back changes...");
        bool ok = true;

        if (Directory.Exists(_runBackupDirectory))
        {
            foreach (var backupFile in Directory.GetFiles(_runBackupDirectory, "*", SearchOption.AllDirectories))
            {
                var targetFile = Path.Combine(_installDirectory, Path.GetRelativePath(_runBackupDirectory, backupFile));
                try
                {
                    await Task.Run(() => File.Copy(backupFile, targetFile, overwrite: true));
                    Console.WriteLine($"[FileReplacer] Restored: {Path.GetFileName(targetFile)}");
                }
                catch (Exception ex)
                {
                    ok = false;
                    Console.WriteLine($"[FileReplacer] ERROR restoring {targetFile}: {ex.Message}");
                }
            }
        }

        foreach (var added in _created)
        {
            try { if (File.Exists(added)) File.Delete(added); }
            catch (Exception ex)
            {
                ok = false;
                Console.WriteLine($"[FileReplacer] ERROR removing added file {added}: {ex.Message}");
            }
        }

        Console.WriteLine(ok ? "[FileReplacer] Rollback completed successfully" : "[FileReplacer] Rollback finished with errors");
        return ok;
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
}

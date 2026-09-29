using System.Text.Json;

namespace WaBiBaBuSy.Common.Update;

/// <summary>
/// Resolves the set of files <c>WaBiBaBuSy.Updater.exe</c> needs to start.
/// The updater is a framework-dependent app, so its NuGet dependencies (System.CommandLine)
/// ship as separate DLLs next to the exe. Copying only <c>WaBiBaBuSy.Updater.*</c> leaves the
/// updater unable to JIT <c>Main</c> — it dies with FileNotFoundException before writing a log.
/// </summary>
public static class UpdaterFiles
{
    /// <summary>File name of the updater executable.</summary>
    public const string ExeName = "WaBiBaBuSy.Updater.exe";

    /// <summary>File name of the updater's dependency manifest.</summary>
    public const string DepsJsonName = "WaBiBaBuSy.Updater.deps.json";

    private const string OwnFilePattern = "WaBiBaBuSy.Updater.*";

    /// <summary>
    /// Assemblies the updater cannot start without, relative to the updater folder.
    /// Read from <c>WaBiBaBuSy.Updater.deps.json</c> (runtime assets of every library
    /// except the updater project itself); falls back to a known list if the file is missing or unreadable.
    /// </summary>
    public static IReadOnlyList<string> GetRequiredDependencies(string updaterDir)
        => ReadDepsJson(updaterDir).Runtime;

    /// <summary>
    /// Copy the updater and everything it needs from <paramref name="sourceDir"/> into <paramref name="destDir"/>:
    /// its own <c>WaBiBaBuSy.Updater.*</c> files, dependency assemblies and (optional) satellite resource assemblies.
    /// </summary>
    /// <param name="sourceDir">Folder containing a working updater (e.g. the server's install dir).</param>
    /// <param name="destDir">Target updater folder.</param>
    /// <param name="overwrite">Replace files already present in <paramref name="destDir"/>.</param>
    /// <returns>Required dependency assemblies that could not be found in <paramref name="sourceDir"/>.</returns>
    public static IReadOnlyList<string> CopyUpdater(string sourceDir, string destDir, bool overwrite = true)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir, OwnFilePattern))
            CopyFile(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite);

        return CopyDependencies(sourceDir, destDir, sourceDir, overwrite);
    }

    /// <summary>
    /// Copy the updater's dependency assemblies (as listed in the deps.json in <paramref name="updaterDir"/>)
    /// from <paramref name="sourceDir"/> into <paramref name="updaterDir"/> where they are missing.
    /// Used on the client to repair update packages built before the server shipped the dependencies.
    /// </summary>
    /// <returns>Required dependency assemblies still missing afterwards.</returns>
    public static IReadOnlyList<string> RepairDependencies(string updaterDir, string sourceDir)
        => CopyDependencies(sourceDir, updaterDir, updaterDir, overwrite: false);

    private static IReadOnlyList<string> CopyDependencies(string sourceDir, string destDir, string depsDir, bool overwrite)
    {
        var deps = ReadDepsJson(depsDir);
        var missing = new List<string>();

        foreach (var relative in deps.Runtime)
        {
            var source = Path.Combine(sourceDir, relative);
            var dest = Path.Combine(destDir, relative);
            if (File.Exists(source))
                CopyFile(source, dest, overwrite);
            else if (!File.Exists(dest))
                missing.Add(relative);
        }

        // Satellite resources only localize help/error text — copy when available, never required.
        foreach (var relative in deps.Resources)
        {
            var source = Path.Combine(sourceDir, relative);
            if (File.Exists(source))
                CopyFile(source, Path.Combine(destDir, relative), overwrite);
        }

        return missing;
    }

    private static void CopyFile(string source, string dest, bool overwrite)
    {
        if (!overwrite && File.Exists(dest))
            return;
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: true);
    }

    private sealed record DepsFiles(IReadOnlyList<string> Runtime, IReadOnlyList<string> Resources);

    /// <summary>Known dependencies, used when deps.json is unavailable.</summary>
    private static readonly DepsFiles Fallback = new(["System.CommandLine.dll"], []);

    private static DepsFiles ReadDepsJson(string dir)
    {
        var path = Path.Combine(dir, DepsJsonName);
        if (!File.Exists(path))
            return Fallback;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var runtime = new List<string>();
            var resources = new List<string>();

            foreach (var target in doc.RootElement.GetProperty("targets").EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    // The updater project's own assembly is covered by the WaBiBaBuSy.Updater.* pattern.
                    if (library.Name.StartsWith("WaBiBaBuSy.Updater/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Assets are NuGet-relative ("lib/net8.0/X.dll"); the build output flattens them.
                    if (library.Value.TryGetProperty("runtime", out var runtimeAssets))
                    {
                        foreach (var asset in runtimeAssets.EnumerateObject())
                            runtime.Add(Path.GetFileName(asset.Name));
                    }

                    if (library.Value.TryGetProperty("resources", out var resourceAssets))
                    {
                        foreach (var asset in resourceAssets.EnumerateObject())
                        {
                            if (asset.Value.TryGetProperty("locale", out var locale) && locale.GetString() is { Length: > 0 } culture)
                                resources.Add(Path.Combine(culture, Path.GetFileName(asset.Name)));
                        }
                    }
                }
            }

            return new DepsFiles(runtime.Distinct().ToList(), resources.Distinct().ToList());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Fallback;
        }
    }
}

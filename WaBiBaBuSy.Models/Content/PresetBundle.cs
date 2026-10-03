using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Content;

/// <summary>
/// Makes a scene self-contained in a folder: every asset is copied into <c>&lt;folder&gt;\assets\</c> and the
/// scene references it as <c>assets/&lt;file&gt;</c>, which <see cref="Testing.SceneFile.Load"/> resolves against
/// the scene file's folder. Used for preset import, starter seeding and export.
/// </summary>
public static class PresetBundle
{
    /// <summary>Asset sub-folder next to the scene files.</summary>
    public const string AssetsFolder = "assets";

    /// <summary>
    /// Copy every asset of <paramref name="config"/> (absolute paths) into <c>targetDir\assets\</c> and rewrite the
    /// paths to <c>assets/&lt;file&gt;</c>. A file with the same name and content is reused; a different file with
    /// the same name is stored as <c>&lt;stem&gt;-&lt;hash8&gt;&lt;ext&gt;</c>. Throws <see cref="FileNotFoundException"/>
    /// for a missing or relative asset path (nothing is rewritten then).
    /// </summary>
    public static async Task BundleAssetsAsync(CrossScreenConfig config, string targetDir)
    {
        var assets = SceneAssets.CollectPaths(config);
        foreach (var (path, _) in assets)
            if (!Path.IsPathRooted(path) || !File.Exists(path))
                throw new FileNotFoundException($"asset not found: {path}", path);

        var assetsDir = Path.Combine(targetDir, AssetsFolder);
        Directory.CreateDirectory(assetsDir);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _) in assets)
            map[path] = $"{AssetsFolder}/{await PlaceAsync(path, assetsDir)}";

        Rewrite(config, p => map.TryGetValue(p, out var relative) ? relative : p);
    }

    /// <summary>Rewrite absolute paths inside <paramref name="baseDir"/> as forward-slash paths relative to it.</summary>
    public static void RelativizeUnder(CrossScreenConfig config, string baseDir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir)) + Path.DirectorySeparatorChar;
        Rewrite(config, p =>
            Path.IsPathRooted(p) && Path.GetFullPath(p).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(root, p).Replace('\\', '/')
                : p);
    }

    /// <summary>File name the asset gets in <paramref name="assetsDir"/>; copies it unless an identical file is there.</summary>
    private static async Task<string> PlaceAsync(string source, string assetsDir)
    {
        var name = Path.GetFileName(source);
        var target = Path.Combine(assetsDir, name);
        if (!File.Exists(target))
        {
            await CopyAsync(source, target);
            return name;
        }

        var hash = Hash(source);
        if (Hash(target) == hash) return name;

        var hashed = $"{Path.GetFileNameWithoutExtension(name)}-{hash[..8]}{Path.GetExtension(name)}";
        var hashedTarget = Path.Combine(assetsDir, hashed);
        if (!File.Exists(hashedTarget)) await CopyAsync(source, hashedTarget);
        return hashed;
    }

    private static string Hash(string path)
    {
        var id = ContentIdentity.ComputeId(path);
        if (!ContentIdentity.HasHash(id)) throw new IOException($"cannot read asset: {path}");
        return id[^ContentIdentity.HashPrefixLength..];
    }

    private static async Task CopyAsync(string source, string target)
    {
        try
        {
            await using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await from.CopyToAsync(to);
        }
        catch
        {
            // Never leave a half-written asset that a later bundle would take for a complete one.
            try { if (File.Exists(target)) File.Delete(target); } catch { /* best effort */ }
            throw;
        }
    }

    private static void Rewrite(CrossScreenConfig config, Func<string, string> map)
    {
        string One(string p) => string.IsNullOrWhiteSpace(p) ? p : map(p);
        config.Animation.AnimationPath = One(config.Animation.AnimationPath);
        config.Animation.AdditionalAnimationPaths = config.Animation.AdditionalAnimationPaths.Select(One).ToList();
        if (!string.IsNullOrEmpty(config.Background.ImagePath))
            config.Background.ImagePath = One(config.Background.ImagePath);
    }
}

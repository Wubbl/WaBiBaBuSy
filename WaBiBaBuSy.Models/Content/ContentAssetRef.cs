using System.Collections.Generic;
using System.IO;
using System.Linq;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Content;

/// <summary>Role of a content file inside a scene.</summary>
public static class AssetRole
{
    public const string Primary = "primary";
    public const string Additional = "additional";
    public const string Background = "background";
}

/// <summary>
/// One transferable file of a scene: its content id (name + hash), its role and the path the
/// authoring machine used (so the receiving side can rewrite configs to its local cache path).
/// </summary>
public class ContentAssetRef
{
    public string ContentId { get; set; } = string.Empty;
    public string Role { get; set; } = AssetRole.Primary;
    public string OriginalPath { get; set; } = string.Empty;
}

/// <summary>
/// Pure helpers over a scene's file references. The server registers every asset for download and
/// ships the list with the LOAD/PREFETCH commands; the client downloads them all before applying.
/// </summary>
public static class SceneAssets
{
    /// <summary>Every file path a scene references, with its role. Duplicates removed, order stable.</summary>
    public static IReadOnlyList<(string Path, string Role)> CollectPaths(CrossScreenConfig config)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        void Add(string? path, string role)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!seen.Add(path)) return;
            list.Add((path, role));
        }

        Add(config.Animation.AnimationPath, AssetRole.Primary);
        foreach (var p in config.Animation.AdditionalAnimationPaths) Add(p, AssetRole.Additional);
        if (config.Background.Mode is BackgroundMode.StretchedImage or BackgroundMode.TiledImage)
            Add(config.Background.ImagePath, AssetRole.Background);
        return list;
    }

    /// <summary>
    /// Rewrite a scene's file references to local paths. Unknown paths are kept when the file exists
    /// locally (same machine) and dropped otherwise; a missing background image degrades to solid color.
    /// Returns clones — the input is not modified.
    /// </summary>
    public static (AnimationLayerConfig Animation, BackgroundLayerConfig Background) RewriteToLocal(
        AnimationLayerConfig animation, BackgroundLayerConfig background,
        IReadOnlyDictionary<string, string> originalToLocal, string? primaryLocalPath)
    {
        string? Map(string? original)
        {
            if (string.IsNullOrWhiteSpace(original)) return null;
            if (originalToLocal.TryGetValue(original, out var local) && File.Exists(local)) return local;
            return File.Exists(original) ? original : null;
        }

        var anim = System.Text.Json.JsonSerializer.Deserialize<AnimationLayerConfig>(System.Text.Json.JsonSerializer.Serialize(animation))!;
        if (!string.IsNullOrEmpty(primaryLocalPath)) anim.AnimationPath = primaryLocalPath;
        else anim.AnimationPath = Map(anim.AnimationPath) ?? anim.AnimationPath;
        anim.AdditionalAnimationPaths = anim.AdditionalAnimationPaths
            .Select(Map).Where(p => p != null).Select(p => p!).ToList();

        var bg = System.Text.Json.JsonSerializer.Deserialize<BackgroundLayerConfig>(System.Text.Json.JsonSerializer.Serialize(background))!;
        if (bg.Mode is BackgroundMode.StretchedImage or BackgroundMode.TiledImage)
        {
            var local = Map(bg.ImagePath);
            if (local == null)
            {
                bg.Mode = BackgroundMode.SolidColor;
                bg.ImagePath = null;
            }
            else bg.ImagePath = local;
        }
        return (anim, bg);
    }
}

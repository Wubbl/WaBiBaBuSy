using System.Collections.Generic;
using System.IO;
using System.Linq;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Content;

/// <summary>Role of a content file inside a scene.</summary>
public static class AssetRole
{
    public const string Primary = "primary";
    public const string Additional = "additional";
    public const string Background = "background";
    /// <summary>Image of a one-shot show event (Traversal).</summary>
    public const string Event = "event";
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

        // Role "primary" = the bottom layer's main file; every other sprite file is "additional".
        // Receivers map files by OriginalPath, so the role carries no layer index.
        for (int i = 0; i < config.Layers.Count; i++)
        {
            var anim = config.Layers[i].Animation;
            Add(anim.AnimationPath, i == 0 ? AssetRole.Primary : AssetRole.Additional);
            foreach (var p in anim.AdditionalAnimationPaths) Add(p, AssetRole.Additional);
        }
        if (config.Background.UsesImage)
            Add(config.Background.ImagePath, AssetRole.Background);
        return list;
    }

    /// <summary>
    /// Rewrite every file reference of a scene to local paths. Unknown paths are kept when the file exists
    /// locally (same machine). Per layer: an additional image that is missing here is replaced by that layer's
    /// primary — never dropped, because the per-cell image pick is <c>hash % imageCount</c> and one image less
    /// would shuffle every cell on this machine only (review 2026-10-13 E8); a layer whose primary file is
    /// missing is disabled on this machine (the other layers still play). A missing background image degrades
    /// to solid color. <paramref name="primaryLocalPath"/>, when given, is the bottom layer's downloaded file.
    /// Returns a clone — the input is not modified.
    /// </summary>
    public static CrossScreenConfig RewriteToLocal(CrossScreenConfig scene,
        IReadOnlyDictionary<string, string> originalToLocal, string? primaryLocalPath = null)
    {
        string? Map(string? original)
        {
            if (string.IsNullOrWhiteSpace(original)) return null;
            if (originalToLocal.TryGetValue(original, out var local) && File.Exists(local)) return local;
            return File.Exists(original) ? original : null;
        }

        var clone = SceneFile.Clone(scene);
        for (int i = 0; i < clone.Layers.Count; i++)
        {
            var anim = clone.Layers[i].Animation;
            // A background-only scene sends the background image as primary: never hand it to an empty layer 0.
            var primary = i == 0 && !string.IsNullOrWhiteSpace(anim.AnimationPath) && !string.IsNullOrEmpty(primaryLocalPath) && File.Exists(primaryLocalPath)
                ? primaryLocalPath
                : Map(anim.AnimationPath);
            if (primary == null)
            {
                if (!string.IsNullOrWhiteSpace(anim.AnimationPath)) clone.Layers[i].Enabled = false;
                continue;
            }
            anim.AnimationPath = primary;
            anim.AdditionalAnimationPaths = anim.AdditionalAnimationPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Map(p) ?? primary)
                .ToList();
        }

        var bg = clone.Background;
        if (bg.UsesImage)
        {
            var local = Map(bg.ImagePath);
            if (local == null)
            {
                bg.Mode = BackgroundMode.SolidColor;
                bg.ImagePath = null;
            }
            else bg.ImagePath = local;
        }
        return clone;
    }
}
